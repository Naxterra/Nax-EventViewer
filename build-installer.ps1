<#
.SYNOPSIS
Publishes Nax Event Viewer as a self-contained x64 app and builds the installer with Inno Setup 7.

.DESCRIPTION
The installer is written to artifacts\installer. The version comes from the app project unless -Version is given.
Inno Setup 7 is expected at its standard per-user location, or at the path given with -IsccPath.
#>
param(
    [string]$Version,
    [string]$IsccPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\NaxEventViewer.App\NaxEventViewer.App.csproj'

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup 7 was not found at $IsccPath. Install it with: winget install JRSoftware.InnoSetup.7"
}
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "The app project has no <Version>." }

$publishDir = Join-Path $root 'artifacts\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing Nax Event Viewer $Version"
dotnet publish $project -c Release -r win-x64 -o $publishDir -p:Version=$Version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "Building the installer"
& $IsccPath /Q "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $root 'installer\Nax-EventViewer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Join-Path $root "artifacts\installer\Nax-EventViewer-$Version-setup.exe"
Write-Host "Installer written to $setup"
