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
dotnet publish $project -c Release -r win-x64 --self-contained -o $publishDir -p:Version=$Version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
# Without the .NET runtime in the folder, the app would only start on PCs that happen to have the same .NET installed.
if (-not (Test-Path (Join-Path $publishDir 'coreclr.dll'))) { throw "The published app does not include the .NET runtime." }

# The bundled .NET runtime, Windows App SDK and WebView2 parts stay under their own licenses, which require their
# notices to travel with every copy. Their license and notice files are copied from the packages the build used.
Write-Host "Collecting third-party notices"
$deps = Get-Content (Join-Path $publishDir 'Nax-EventViewer.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content (Join-Path $root 'src\NaxEventViewer.App\obj\project.assets.json') -Raw | ConvertFrom-Json
# The folder the restore actually used, which a NuGet.Config can move away from the default.
$packages = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$components = @(
    $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -in 'package', 'runtimepack' } | ForEach-Object { $_.Name -replace '^runtimepack\.', '' }
    $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name }
) | Where-Object { $_ -notmatch '^Microsoft\.Windows\.SDK\.BuildTools' } | Sort-Object -Unique

$licenseDir = Join-Path $publishDir 'licenses'
New-Item -ItemType Directory -Force $licenseDir | Out-Null
$overview = [System.Collections.Generic.List[string]]::new()
$overview.Add('Third-party components in Nax Event Viewer')
$overview.Add('')
$overview.Add('Nax Event Viewer includes the following components from other authors. They are not covered by the Nax Source-Available License; each stays under its own license. The folder named after each component inside the "licenses" folder next to this file holds the license texts and notices its package contains. For a package that contains no license text, that folder holds its copyright notice and the address of its license instead.')
$overview.Add('')
foreach ($component in $components) {
    $id, $componentVersion = $component.Split('/')
    $folder = Join-Path $packages "$($id.ToLowerInvariant())\$($componentVersion.ToLowerInvariant())"
    if (-not (Test-Path $folder)) { throw "The package folder of $component was not found at $folder." }
    $files = Get-ChildItem $folder -File | Where-Object { $_.Name -match '^(license|notice|third-?party-?notices)' }
    $target = New-Item -ItemType Directory -Force (Join-Path $licenseDir $id)
    if ($files) {
        foreach ($file in $files) { Copy-Item $file.FullName (Join-Path $target $file.Name) }
        $overview.Add("- $id ${componentVersion}: licenses\$id ($(($files.Name) -join ', '))")
    }
    else {
        # The package's own description names its copyright and its license, by expression or by address.
        $nuspec = [xml](Get-Content (Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1).FullName)
        $metadata = $nuspec.package.metadata
        $licenseText = if ($metadata.license) { "License: $($metadata.license.'#text') (https://spdx.org/licenses/$($metadata.license.'#text').html)" }
            elseif ($metadata.licenseUrl) { "License: $($metadata.licenseUrl)" }
            else { throw "The package $component names no license." }
        Set-Content (Join-Path $target 'LICENSE-NOTICE.txt') @(
            "$id $componentVersion"
            ''
            $metadata.copyright
            ''
            'This package contains no license text of its own.'
            $licenseText
        ) -Encoding utf8
        $overview.Add("- $id ${componentVersion}: licenses\$id (LICENSE-NOTICE.txt)")
    }
}
Set-Content (Join-Path $publishDir 'THIRD-PARTY-NOTICES.txt') $overview -Encoding utf8

Write-Host "Building the installer"
& $IsccPath /Q "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $root 'installer\Nax-EventViewer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Join-Path $root "artifacts\installer\Nax-EventViewer-$Version-setup.exe"
Write-Host "Installer written to $setup"
