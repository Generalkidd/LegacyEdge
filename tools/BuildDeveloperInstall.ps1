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

$requiredTemplates = @("Install.cmd", "Install.ps1", "Uninstall.ps1", "README.txt")
$requiredDependencies = @(
    "Microsoft.NET.Native.Framework.2.2.appx",
    "Microsoft.NET.Native.Runtime.2.2.appx",
    "Microsoft.VCLibs.ARM64.14.00.appx"
)
foreach ($name in $requiredTemplates)
{
    if (-not (Test-Path -LiteralPath (Join-Path $installerTemplates $name) -PathType Leaf))
    {
        throw "Missing installer template '$name'."
    }
}
foreach ($name in $requiredDependencies)
{
    if (-not (Test-Path -LiteralPath (Join-Path $dependencies $name) -PathType Leaf))
    {
        throw "Missing ARM64 dependency '$name'."
    }
}

if ((Test-Path -LiteralPath $output) -and -not $ReplaceOutput)
{
    throw "OutputDirectory already exists. Pass -ReplaceOutput to replace it recoverably."
}

$kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
    Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
    ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
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
    if ($identity.Name -ne "Codex.LegacyEdge" -or $identity.Publisher -ne "CN=CodexDeveloper" -or
        $identity.Version -ne "1.0.0.0" -or $identity.ProcessorArchitecture -ne "arm64")
    {
        throw "Package identity is not the expected Legacy Edge 1.0.0.0 ARM64 development identity."
    }

    foreach ($name in $requiredTemplates)
    {
        Copy-Item -LiteralPath (Join-Path $installerTemplates $name) -Destination (Join-Path $staging $name)
    }
    $dependencyOutput = Join-Path $staging "Dependencies\arm64"
    New-Item -ItemType Directory -Path $dependencyOutput -Force | Out-Null
    foreach ($name in $requiredDependencies)
    {
        Copy-Item -LiteralPath (Join-Path $dependencies $name) -Destination (Join-Path $dependencyOutput $name)
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
    Get-FileHash -Algorithm SHA256 -LiteralPath $package | Format-List Algorithm,Hash,Path
}
finally
{
    if ($staging -and (Test-Path -LiteralPath $staging))
    {
        Write-Warning "Incomplete staging directory retained for diagnosis: $staging"
    }
}
