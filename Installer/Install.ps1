param(
    [switch]$NoLaunch,
    [switch]$ResetApplicationData,
    [switch]$ReplaceExisting
)

$ErrorActionPreference = "Stop"
$packageName = "Codex.LegacyEdge"
$packageFamily = "Codex.LegacyEdge_sw4cebtq3t5b6"
$expectedPublisher = "CN=CodexDeveloper"
$dependencyPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSCommandPath)).Path
$manifest = Join-Path $packageRoot "AppxManifest.xml"

$architectures = @{
    "x86" = [pscustomobject]@{ DisplayName = "x86"; RuntimeName = "X86"; VCLibsFile = "Microsoft.VCLibs.x86.14.00.appx" }
    "x64" = [pscustomobject]@{ DisplayName = "x64"; RuntimeName = "X64"; VCLibsFile = "Microsoft.VCLibs.x64.14.00.appx" }
    "arm" = [pscustomobject]@{ DisplayName = "ARM"; RuntimeName = "Arm"; VCLibsFile = "Microsoft.VCLibs.ARM.14.00.appx" }
    "arm64" = [pscustomobject]@{ DisplayName = "ARM64"; RuntimeName = "Arm64"; VCLibsFile = "Microsoft.VCLibs.ARM64.14.00.appx" }
}

function Require-File([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "The installer is incomplete. Missing '$Path'. Extract the entire ZIP to a local folder and run Install.cmd there."
    }
}

function Get-DeploymentCode([System.Exception]$Exception)
{
    return "0x{0:X8}" -f ($Exception.HResult -band 0xFFFFFFFFL)
}

function Write-DeploymentDiagnostics($ErrorRecord)
{
    $code = Get-DeploymentCode $ErrorRecord.Exception
    Write-Warning "AppX deployment failed with $code."
    $activityProperty = $ErrorRecord.Exception.PSObject.Properties["ActivityId"]
    if ($activityProperty -and $activityProperty.Value)
    {
        Write-Warning "Deployment ActivityId: $($activityProperty.Value)"
        Write-Warning "For details run: Get-AppPackageLog -ActivityID $($activityProperty.Value)"
    }
}

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

Require-File $manifest
try { [xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw }
catch { throw "AppxManifest.xml could not be parsed: $($_.Exception.Message)" }

$identity = $manifestXml.Package.Identity
$application = @($manifestXml.Package.Applications.Application) | Select-Object -First 1
if (-not $identity -or $identity.Name -ne $packageName -or $identity.Publisher -ne $expectedPublisher -or
    -not $application -or $application.Id -ne "App" -or $application.Executable -ne "LegacyEdge.exe" -or
    $application.EntryPoint -ne "LegacyEdge.App")
{
    throw "The loose package identity is unexpected. Expected '$packageName', publisher '$expectedPublisher', application 'App', executable 'LegacyEdge.exe', entry point 'LegacyEdge.App'."
}

try { $expectedVersion = [version]$identity.Version }
catch { throw "The loose package version '$($identity.Version)' is invalid." }

$architectureKey = ([string]$identity.ProcessorArchitecture).ToLowerInvariant()
$architecture = $architectures[$architectureKey]
if (-not $architecture)
{
    throw "The loose package architecture '$($identity.ProcessorArchitecture)' is unsupported. Expected x86, x64, ARM, or ARM64."
}

$dependencyRoot = Join-Path $packageRoot ("Dependencies\" + $architectureKey)
$osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if (-not [string]::Equals($osArchitecture, $architecture.RuntimeName, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "This payload targets $($architecture.DisplayName), but Windows reports '$osArchitecture'. Use the developer installer that matches this PC's architecture."
}

$requiredPayload = @(
    $manifest,
    (Join-Path $packageRoot "AppxBlockMap.xml"),
    (Join-Path $packageRoot "LegacyEdge.exe"),
    (Join-Path $packageRoot "LegacyEdge.dll"),
    (Join-Path $packageRoot "clrcompression.dll"),
    (Join-Path $packageRoot "resources.pri")
)

$dependencyFiles = @{
    "Microsoft.NET.Native.Framework.2.2" = "Microsoft.NET.Native.Framework.2.2.appx"
    "Microsoft.NET.Native.Runtime.2.2" = "Microsoft.NET.Native.Runtime.2.2.appx"
    "Microsoft.VCLibs.140.00" = $architecture.VCLibsFile
}
$declaredDependencies = @($manifestXml.Package.Dependencies.PackageDependency)
if ($declaredDependencies.Count -ne $dependencyFiles.Count)
{
    throw "Only Release .NET Native packages are supported. The package declares an unexpected dependency set."
}
$dependencies = foreach ($dependencyName in $dependencyFiles.Keys)
{
    $matches = @($declaredDependencies | Where-Object { $_.Name -eq $dependencyName })
    if ($matches.Count -ne 1 -or $matches[0].Publisher -ne $dependencyPublisher)
    {
        throw "Only Release .NET Native packages are supported. Expected dependency '$dependencyName' from the Microsoft publisher."
    }
    try { $minimumVersion = [version]$matches[0].MinVersion }
    catch { throw "Dependency '$dependencyName' has an invalid minimum version '$($matches[0].MinVersion)'." }
    [pscustomobject]@{ Name = $dependencyName; Minimum = $minimumVersion; File = $dependencyFiles[$dependencyName] }
}

foreach ($path in $requiredPayload) { Require-File $path }
foreach ($dependency in $dependencies)
{
    $dependencyPath = Join-Path $dependencyRoot $dependency.File
    Require-File $dependencyPath
    $dependencyIdentity = Get-AppxIdentityInfo $dependencyPath
    if ($dependencyIdentity.Name -ne $dependency.Name -or $dependencyIdentity.Publisher -ne $dependencyPublisher -or
        $dependencyIdentity.Version -lt $dependency.Minimum -or $dependencyIdentity.Architecture -ne $architectureKey)
    {
        throw "Dependency '$($dependency.File)' has an unexpected identity. Expected '$($dependency.Name)' >= $($dependency.Minimum), $($architecture.DisplayName), Microsoft publisher; found '$($dependencyIdentity.Name)' $($dependencyIdentity.Version), '$($dependencyIdentity.Architecture)', '$($dependencyIdentity.Publisher)'."
    }
}

$policy = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
if ($policy.AllowDevelopmentWithoutDevLicense -ne 1)
{
    throw "Windows Developer Mode is not enabled. Open Settings > System > For developers and enable Developer Mode."
}

foreach ($dependency in $dependencies)
{
    $compatible = Get-AppxPackage -Name $dependency.Name -ErrorAction SilentlyContinue |
        Where-Object {
            [string]::Equals($_.Architecture.ToString(), $architecture.RuntimeName, [System.StringComparison]::OrdinalIgnoreCase) -and
            [version]$_.Version -ge $dependency.Minimum -and $_.Publisher -eq $dependencyPublisher -and
            $_.Status.ToString() -eq "Ok"
        } |
        Select-Object -First 1
    if ($compatible)
    {
        Write-Host "Dependency ready: $($compatible.PackageFullName)"
        continue
    }

    $dependencyPath = Join-Path $dependencyRoot $dependency.File
    Write-Host "Installing $($architecture.DisplayName) dependency $($dependency.Name)..."
    try { Add-AppxPackage -Path $dependencyPath -ForceApplicationShutdown }
    catch
    {
        Write-DeploymentDiagnostics $_
        throw
    }
    $installedDependency = Get-AppxPackage -Name $dependency.Name -ErrorAction SilentlyContinue |
        Where-Object {
            [string]::Equals($_.Architecture.ToString(), $architecture.RuntimeName, [System.StringComparison]::OrdinalIgnoreCase) -and
            [version]$_.Version -ge $dependency.Minimum -and $_.Publisher -eq $dependencyPublisher -and
            $_.Status.ToString() -eq "Ok"
        } |
        Select-Object -First 1
    if (-not $installedDependency)
    {
        throw "Dependency '$($dependency.Name)' was not healthy after installation."
    }
}

if ($ReplaceExisting)
{
    Write-Warning "-ReplaceExisting is no longer required. Known Legacy Edge registrations are replaced while preserving app data by default."
}

$existingPackages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
foreach ($existing in $existingPackages)
{
    if ($existing.Name -ne $packageName -or $existing.Publisher -ne $expectedPublisher -or
        $existing.PackageFamilyName -ne $packageFamily)
    {
        throw "Refusing to replace unexpected package '$($existing.PackageFullName)'."
    }
}

if ($existingPackages.Count -gt 0)
{
    Write-Host "Closing Legacy Edge and refreshing its $($architecture.DisplayName) development registration..."
    foreach ($existing in $existingPackages)
    {
        if ($ResetApplicationData)
        {
            Write-Warning "Resetting favorites, history, settings, sessions, and other local app data."
            Remove-AppxPackage -Package $existing.PackageFullName -Confirm:$false
        }
        else
        {
            Remove-AppxPackage -Package $existing.PackageFullName -PreserveApplicationData -Confirm:$false
        }
    }
}

try { Add-AppxPackage -Register $manifest -ForceApplicationShutdown }
catch
{
    Write-DeploymentDiagnostics $_
    throw
}

if ($ResetApplicationData -and $existingPackages.Count -eq 0)
{
    $registeredForReset = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
        Where-Object { $_.PackageFamilyName -eq $packageFamily } |
        Select-Object -First 1
    if (-not $registeredForReset)
    {
        throw "Legacy Edge could not be registered temporarily to clear preserved application data."
    }
    Write-Warning "Clearing preserved favorites, history, settings, sessions, and other local app data."
    Remove-AppxPackage -Package $registeredForReset.PackageFullName -Confirm:$false
    try { Add-AppxPackage -Register $manifest -ForceApplicationShutdown }
    catch
    {
        Write-DeploymentDiagnostics $_
        throw
    }
}

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -eq $packageName -and $_.Publisher -eq $expectedPublisher -and
        $_.PackageFamilyName -eq $packageFamily -and [version]$_.Version -eq $expectedVersion -and
        [string]::Equals($_.Architecture.ToString(), $architecture.RuntimeName, [System.StringComparison]::OrdinalIgnoreCase)
    } |
    Select-Object -First 1
if (-not $installed)
{
    $observed = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PackageFullName) -join ", "
    throw "Legacy Edge $expectedVersion $($architecture.DisplayName) did not appear in the current user's package registry after deployment. Found: '$observed'."
}

$installedRoot = (Resolve-Path -LiteralPath $installed.InstallLocation).Path
$statusOk = $installed.Status.ToString() -eq "Ok"
$locationOk = [string]::Equals($installedRoot, $packageRoot, [System.StringComparison]::OrdinalIgnoreCase)
$architectureOk = [string]::Equals($installed.Architecture.ToString(), $architecture.RuntimeName, [System.StringComparison]::OrdinalIgnoreCase)
if ($installed.Name -ne $packageName -or $installed.Publisher -ne $expectedPublisher -or
    $installed.PackageFamilyName -ne $packageFamily -or -not $architectureOk -or
    [version]$installed.Version -ne $expectedVersion -or -not $installed.IsDevelopmentMode -or
    -not $statusOk -or -not $locationOk)
{
    throw "Registration verification failed. FullName='$($installed.PackageFullName)', Architecture='$($installed.Architecture)', Status='$($installed.Status)', DevelopmentMode='$($installed.IsDevelopmentMode)', Location='$installedRoot'."
}

Write-Host "Installed and verified $($installed.PackageFullName)"
Write-Host "Live $($architecture.DisplayName) package folder: $packageRoot"

if (-not $NoLaunch)
{
    Start-Process -FilePath "explorer.exe" -ArgumentList "shell:AppsFolder\$packageFamily!App"
}
