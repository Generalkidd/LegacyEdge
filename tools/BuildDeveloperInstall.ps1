param(
    [Parameter(Mandatory = $true)] [string]$PackagePath,
    [Parameter(Mandatory = $true)] [string]$DependencyDirectory,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [switch]$ReplaceOutput
)

$ErrorActionPreference = "Stop"
$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$installerTemplates = Join-Path $sourceRoot "Installer"
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$dependencies = (Resolve-Path -LiteralPath $DependencyDirectory).Path
$outputParent = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputDirectory))
$outputLeaf = Split-Path -Leaf ([System.IO.Path]::GetFullPath($OutputDirectory))
if ([string]::IsNullOrWhiteSpace($outputLeaf)) { throw "OutputDirectory must name a child directory." }
if (-not (Test-Path -LiteralPath $outputParent -PathType Container))
{
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}
$output = Join-Path (Resolve-Path -LiteralPath $outputParent).Path $outputLeaf

$architectures = @{
    "x86" = [pscustomobject]@{ DisplayName = "x86"; VCLibsFile = "Microsoft.VCLibs.x86.14.00.appx" }
    "x64" = [pscustomobject]@{ DisplayName = "x64"; VCLibsFile = "Microsoft.VCLibs.x64.14.00.appx" }
    "arm" = [pscustomobject]@{ DisplayName = "ARM"; VCLibsFile = "Microsoft.VCLibs.ARM.14.00.appx" }
    "arm64" = [pscustomobject]@{ DisplayName = "ARM64"; VCLibsFile = "Microsoft.VCLibs.ARM64.14.00.appx" }
}
$dependencyPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-AppxIdentityInfo([string]$Path)
{
    $archive = $null
    $stream = $null
    $reader = $null
    try
    {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $entry = $archive.GetEntry("AppxManifest.xml")
        if (-not $entry) { throw "AppxManifest.xml is missing." }
        $stream = $entry.Open()
        $reader = New-Object System.IO.StreamReader $stream
        [xml]$dependencyManifest = $reader.ReadToEnd()
        $identity = $dependencyManifest.Package.Identity
        if (-not $identity) { throw "Package identity is missing." }
        return [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [version]$identity.Version
            Architecture = ([string]$identity.ProcessorArchitecture).ToLowerInvariant()
        }
    }
    catch
    {
        throw "Dependency package '$Path' could not be inspected: $($_.Exception.Message)"
    }
    finally
    {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
        if ($archive) { $archive.Dispose() }
    }
}

function Test-DependencySet([string]$Directory, [string[]]$Names)
{
    foreach ($name in $Names)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { return $false }
    }
    return $true
}

$requiredTemplates = @("Install.cmd", "Install.ps1", "Uninstall.ps1", "README.txt")
foreach ($name in $requiredTemplates)
{
    if (-not (Test-Path -LiteralPath (Join-Path $installerTemplates $name) -PathType Leaf))
    {
        throw "Missing installer template '$name'."
    }
}

if ((Test-Path -LiteralPath $output) -and -not $ReplaceOutput)
{
    throw "OutputDirectory already exists. Pass -ReplaceOutput to replace it recoverably."
}

$kitsRoot = (Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" -ErrorAction SilentlyContinue).KitsRoot10
if (-not $kitsRoot) { $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10" }
$kitsBin = Join-Path $kitsRoot "bin"
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$hostToolArchitectures = switch ($hostArchitecture)
{
    "arm64" { @("arm64", "x86", "x64") }
    "x64" { @("x64", "x86") }
    default { @("x86") }
}
$makeAppx = $null
$sdkVersions = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
    Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending
foreach ($sdkVersion in $sdkVersions)
{
    foreach ($toolArchitecture in $hostToolArchitectures)
    {
        $candidate = Join-Path $sdkVersion.FullName ($toolArchitecture + "\makeappx.exe")
        if (Test-Path -LiteralPath $candidate -PathType Leaf)
        {
            $makeAppx = $candidate
            break
        }
    }
    if ($makeAppx) { break }
}
if (-not $makeAppx) { throw "MakeAppx.exe was not found in the installed Windows 10 SDK." }

$staging = Join-Path $outputParent ("." + $outputLeaf + ".staging-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null
try
{
    & $makeAppx unpack /p $package /d $staging
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx unpack failed with exit code $LASTEXITCODE." }

    $manifestPath = Join-Path $staging "AppxManifest.xml"
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $identity = $manifest.Package.Identity
    $application = @($manifest.Package.Applications.Application) | Select-Object -First 1
    if (-not $identity -or $identity.Name -ne "Codex.LegacyEdge" -or $identity.Publisher -ne "CN=CodexDeveloper" -or
        -not $application -or $application.Id -ne "App" -or $application.Executable -ne "LegacyEdge.exe" -or
        $application.EntryPoint -ne "LegacyEdge.App")
    {
        throw "Package identity is not the expected Legacy Edge development identity."
    }
    try { $version = [version]$identity.Version }
    catch { throw "Package version '$($identity.Version)' is invalid." }

    $architectureKey = ([string]$identity.ProcessorArchitecture).ToLowerInvariant()
    $architecture = $architectures[$architectureKey]
    if (-not $architecture)
    {
        throw "Package architecture '$($identity.ProcessorArchitecture)' is unsupported. Expected x86, x64, ARM, or ARM64."
    }

    $requiredReleasePayload = @(
        "AppxManifest.xml",
        "AppxBlockMap.xml",
        "LegacyEdge.exe",
        "LegacyEdge.dll",
        "clrcompression.dll",
        "resources.pri"
    )
    foreach ($relativePath in $requiredReleasePayload)
    {
        $payloadPath = Join-Path $staging $relativePath
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf) -or (Get-Item -LiteralPath $payloadPath).Length -eq 0)
        {
            throw "Only Release .NET Native packages are supported. Required payload '$relativePath' is missing or empty."
        }
    }

    $dependencyFiles = @{
        "Microsoft.NET.Native.Framework.2.2" = "Microsoft.NET.Native.Framework.2.2.appx"
        "Microsoft.NET.Native.Runtime.2.2" = "Microsoft.NET.Native.Runtime.2.2.appx"
        "Microsoft.VCLibs.140.00" = $architecture.VCLibsFile
    }
    $declaredDependencies = @($manifest.Package.Dependencies.PackageDependency)
    if ($declaredDependencies.Count -ne $dependencyFiles.Count)
    {
        throw "Only Release .NET Native packages are supported. The package declares an unexpected dependency set."
    }
    $dependencyContracts = foreach ($dependencyName in $dependencyFiles.Keys)
    {
        $matches = @($declaredDependencies | Where-Object { $_.Name -eq $dependencyName })
        if ($matches.Count -ne 1 -or $matches[0].Publisher -ne $dependencyPublisher)
        {
            throw "Only Release .NET Native packages are supported. Expected dependency '$dependencyName' from the Microsoft publisher."
        }
        try { $minimumVersion = [version]$matches[0].MinVersion }
        catch { throw "Dependency '$dependencyName' has an invalid minimum version '$($matches[0].MinVersion)'." }
        [pscustomobject]@{ File = $dependencyFiles[$dependencyName]; Name = $dependencyName; Minimum = $minimumVersion }
    }
    $requiredDependencies = @($dependencyContracts | Select-Object -ExpandProperty File)
    $dependencySource = $dependencies
    if (-not (Test-DependencySet $dependencySource $requiredDependencies))
    {
        $architectureDirectory = Join-Path $dependencies $architectureKey
        if (Test-DependencySet $architectureDirectory $requiredDependencies)
        {
            $dependencySource = $architectureDirectory
        }
        else
        {
            throw "Missing $($architecture.DisplayName) release dependencies. Supply either the matching architecture directory or its parent Dependencies directory. Required files: $($requiredDependencies -join ', ')."
        }
    }

    foreach ($contract in $dependencyContracts)
    {
        $dependencyPath = Join-Path $dependencySource $contract.File
        $dependencyIdentity = Get-AppxIdentityInfo $dependencyPath
        if ($dependencyIdentity.Name -ne $contract.Name -or $dependencyIdentity.Publisher -ne $dependencyPublisher -or
            $dependencyIdentity.Version -lt $contract.Minimum -or $dependencyIdentity.Architecture -ne $architectureKey)
        {
            throw "Dependency '$($contract.File)' has an unexpected identity. Expected '$($contract.Name)' >= $($contract.Minimum), $($architecture.DisplayName), Microsoft publisher; found '$($dependencyIdentity.Name)' $($dependencyIdentity.Version), '$($dependencyIdentity.Architecture)', '$($dependencyIdentity.Publisher)'."
        }
    }

    foreach ($name in $requiredTemplates)
    {
        Copy-Item -LiteralPath (Join-Path $installerTemplates $name) -Destination (Join-Path $staging $name)
    }
    $dependencyOutput = Join-Path $staging ("Dependencies\" + $architectureKey)
    New-Item -ItemType Directory -Path $dependencyOutput -Force | Out-Null
    foreach ($name in $requiredDependencies)
    {
        Copy-Item -LiteralPath (Join-Path $dependencySource $name) -Destination (Join-Path $dependencyOutput $name)
    }

    if (Test-Path -LiteralPath $output)
    {
        $backup = $output + ".previous-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        Move-Item -LiteralPath $output -Destination $backup
        Write-Host "Previous output retained at: $backup"
    }
    Move-Item -LiteralPath $staging -Destination $output
    $staging = $null

    Write-Host "Developer installer staged at: $output"
    Write-Host "Payload: Legacy Edge $version $($architecture.DisplayName)"
    Get-FileHash -Algorithm SHA256 -LiteralPath $package | Format-List Algorithm,Hash,Path
}
finally
{
    if ($staging -and (Test-Path -LiteralPath $staging))
    {
        Write-Warning "Incomplete staging directory retained for diagnosis: $staging"
    }
}
