$ErrorActionPreference = "Stop"
$package = Get-AppxPackage -Name "Codex.LegacyEdge" -ErrorAction SilentlyContinue

if (-not $package)
{
    Write-Host "Legacy Edge is not installed for the current user."
    return
}

if ($package.PackageFamilyName -ne "Codex.LegacyEdge_sw4cebtq3t5b6")
{
    throw "Refusing to remove unexpected package '$($package.PackageFullName)'."
}

Remove-AppxPackage -Package $package.PackageFullName -Confirm:$false
Write-Host "Removed $($package.PackageFullName), including its local app data."
