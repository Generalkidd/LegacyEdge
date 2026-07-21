param(
    [switch]$NoLaunch,
    [switch]$ResetApplicationData,
    [switch]$ReplaceExisting
)

$ErrorActionPreference = "Stop"
$packageName = "Codex.LegacyEdge"
$expectedPackage = "Codex.LegacyEdge_1.0.0.0_arm64__sw4cebtq3t5b6"
$packageFamily = "Codex.LegacyEdge_sw4cebtq3t5b6"
$expectedPublisher = "CN=CodexDeveloper"
$expectedVersion = [version]"1.0.0.0"
$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSCommandPath)).Path
$manifest = Join-Path $packageRoot "AppxManifest.xml"
$dependencyRoot = Join-Path $packageRoot "Dependencies\arm64"

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

$policy = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
if ($policy.AllowDevelopmentWithoutDevLicense -ne 1)
{
    throw "Windows Developer Mode is not enabled. Open Settings > System > For developers and enable Developer Mode."
}

$osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($osArchitecture -ne "Arm64")
{
    throw "This payload is ARM64, but Windows reports '$osArchitecture'. Use a package built for this PC's architecture."
}

$requiredPayload = @(
    $manifest,
    (Join-Path $packageRoot "AppxBlockMap.xml"),
    (Join-Path $packageRoot "LegacyEdge.exe"),
    (Join-Path $packageRoot "LegacyEdge.dll"),
    (Join-Path $packageRoot "clrcompression.dll"),
    (Join-Path $packageRoot "resources.pri")
)

$dependencies = @(
    [pscustomobject]@{ Name = "Microsoft.NET.Native.Framework.2.2"; Minimum = [version]"2.2.29512.0"; File = "Microsoft.NET.Native.Framework.2.2.appx" },
    [pscustomobject]@{ Name = "Microsoft.NET.Native.Runtime.2.2"; Minimum = [version]"2.2.28604.0"; File = "Microsoft.NET.Native.Runtime.2.2.appx" },
    [pscustomobject]@{ Name = "Microsoft.VCLibs.140.00"; Minimum = [version]"14.0.22929.0"; File = "Microsoft.VCLibs.ARM64.14.00.appx" }
)

foreach ($path in $requiredPayload) { Require-File $path }
foreach ($dependency in $dependencies) { Require-File (Join-Path $dependencyRoot $dependency.File) }

try { [xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw }
catch { throw "AppxManifest.xml could not be parsed: $($_.Exception.Message)" }

$identity = $manifestXml.Package.Identity
$application = @($manifestXml.Package.Applications.Application) | Select-Object -First 1
if (-not $identity -or $identity.Name -ne $packageName -or $identity.Publisher -ne $expectedPublisher -or
    [version]$identity.Version -ne $expectedVersion -or $identity.ProcessorArchitecture -ne "arm64" -or
    -not $application -or $application.Id -ne "App")
{
    throw "The loose package identity is unexpected. Expected $packageName $expectedVersion ARM64, publisher '$expectedPublisher', application 'App'."
}

foreach ($dependency in $dependencies)
{
    $compatible = Get-AppxPackage -Name $dependency.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture.ToString() -eq "Arm64" -and [version]$_.Version -ge $dependency.Minimum } |
        Select-Object -First 1
    if ($compatible)
    {
        Write-Host "Dependency ready: $($compatible.PackageFullName)"
        continue
    }

    $dependencyPath = Join-Path $dependencyRoot $dependency.File
    Write-Host "Installing dependency $($dependency.Name)..."
    try { Add-AppxPackage -Path $dependencyPath -ForceApplicationShutdown }
    catch
    {
        Write-DeploymentDiagnostics $_
        throw
    }
}

if ($ReplaceExisting)
{
    Write-Warning "-ReplaceExisting is no longer required. Known Legacy Edge registrations are replaced while preserving app data by default."
}

$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing)
{
    if ($existing.PackageFamilyName -ne $packageFamily -or $existing.PackageFullName -ne $expectedPackage)
    {
        throw "Refusing to replace unexpected package '$($existing.PackageFullName)'."
    }

    Write-Host "Closing Legacy Edge and refreshing its development registration..."
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

try { Add-AppxPackage -Register $manifest -ForceApplicationShutdown }
catch
{
    Write-DeploymentDiagnostics $_
    throw
}

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if (-not $installed) { throw "Legacy Edge did not appear in the current user's package registry after deployment." }

$installedRoot = (Resolve-Path -LiteralPath $installed.InstallLocation).Path
$statusOk = $installed.Status.ToString() -eq "Ok"
$locationOk = [string]::Equals($installedRoot, $packageRoot, [System.StringComparison]::OrdinalIgnoreCase)
if ($installed.PackageFullName -ne $expectedPackage -or $installed.PackageFamilyName -ne $packageFamily -or
    $installed.Architecture.ToString() -ne "Arm64" -or [version]$installed.Version -ne $expectedVersion -or
    -not $installed.IsDevelopmentMode -or -not $statusOk -or -not $locationOk)
{
    throw "Registration verification failed. FullName='$($installed.PackageFullName)', Architecture='$($installed.Architecture)', Status='$($installed.Status)', DevelopmentMode='$($installed.IsDevelopmentMode)', Location='$installedRoot'."
}

Write-Host "Installed and verified $($installed.PackageFullName)"
Write-Host "Live package folder: $packageRoot"

if (-not $NoLaunch)
{
    Start-Process -FilePath "explorer.exe" -ArgumentList "shell:AppsFolder\$packageFamily!App"
}
