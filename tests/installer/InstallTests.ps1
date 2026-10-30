<#
.SYNOPSIS
Installs Nax Event Viewer silently for the current user into a temporary folder, checks what the installer put in
place, uninstalls it and checks that nothing is left behind.

.DESCRIPTION
Run build-installer.ps1 first. The script refuses to run when Nax Event Viewer is already installed for the current
user, so it never touches a real installation. A crash log folder that already exists is moved aside during the
test and restored afterwards. Prints one line per test and exits with the number of failed tests.
#>
param([string]$Setup)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
if (-not $Setup) {
    $Setup = Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter 'Nax-EventViewer-*-setup.exe' |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'No installer found. Run build-installer.ps1 first.' }

$appId = '{6B0E7C1A-4F2D-4C8B-9E31-5D7A2B9C4E60}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$progIdKey = 'HKCU:\Software\Classes\NaxEventViewer.evtx'
$openWithKey = 'HKCU:\Software\Classes\.evtx\OpenWithProgids'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nax Event Viewer.lnk'
$crashFolder = Join-Path $env:LOCALAPPDATA 'Nax-EventViewer'

if (Test-Path $uninstallKey) { throw 'Nax Event Viewer is installed for this user. Uninstall it before running this test.' }
$evtxKey = 'HKCU:\Software\Classes\.evtx'
$evtxKeyExistedBefore = Test-Path $evtxKey

$installDir = Join-Path ([IO.Path]::GetTempPath()) ('nax-eventviewer-install-test-' + [Guid]::NewGuid().ToString('N'))
$savedCrashFolder = $null
if (Test-Path $crashFolder) {
    $savedCrashFolder = "$crashFolder.saved-by-install-test"
    Move-Item $crashFolder $savedCrashFolder
}

$failures = 0
function Report([string]$name, [string]$scenario, [string[]]$problems) {
    if ($problems.Count -eq 0) { Write-Host "InstallTests.ps1::$name | $scenario | PASS" }
    else {
        Write-Host "InstallTests.ps1::$name | $scenario | FAIL"
        foreach ($p in $problems) { Write-Host "    $p" }
        $script:failures++
    }
}

function OpenWithValuePresent {
    (Test-Path $openWithKey) -and ((Get-Item $openWithKey).GetValueNames() -contains 'NaxEventViewer.evtx')
}

try {
    $install = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$installDir`"", '/TASKS=openwith' -Wait -PassThru
    $problems = @()
    if ($install.ExitCode -ne 0) { $problems += "setup exited with code $($install.ExitCode)" }
    if (-not (Test-Path (Join-Path $installDir 'Nax-EventViewer.exe'))) { $problems += 'the program file is missing' }
    if (-not (Test-Path (Join-Path $installDir 'Assets\Nax-EventViewer.ico'))) { $problems += 'the icon file is missing' }
    if (-not (Test-Path $uninstallKey)) { $problems += 'there is no entry in Installed apps' }
    if (-not (Test-Path $shortcut)) { $problems += 'the Start menu shortcut is missing' }
    if (-not (OpenWithValuePresent)) { $problems += 'the Open with entry for .evtx files is missing' }
    $command = if (Test-Path "$progIdKey\shell\open\command") { (Get-Item "$progIdKey\shell\open\command").GetValue('') } else { '' }
    if ($command -notlike "*$installDir\Nax-EventViewer.exe*") { $problems += "the Open with command is wrong: $command" }
    Report 'install_for_current_user_puts_program_shortcut_and_open_with_entry_in_place' `
        'A silent installation for the current user installs the program, the Start menu shortcut, the Installed apps entry and the Open with entry for .evtx files.' $problems

    # A crash log written while the app was installed must go with the app.
    New-Item -ItemType Directory -Force $crashFolder | Out-Null
    Set-Content (Join-Path $crashFolder 'crash.log') 'test entry'

    $uninstaller = Join-Path $installDir 'unins000.exe'
    $problems = @()
    if (Test-Path $uninstaller) {
        Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        # The uninstaller hands over to a copy of itself, so wait until the folder is gone.
        $deadline = (Get-Date).AddSeconds(60)
        while ((Test-Path $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    else { $problems += 'the uninstaller is missing' }
    if (Test-Path $installDir) { $problems += "the program folder is still there: $installDir" }
    if (Test-Path $uninstallKey) { $problems += 'the Installed apps entry is still there' }
    if (Test-Path $shortcut) { $problems += 'the Start menu shortcut is still there' }
    if (Test-Path $progIdKey) { $problems += 'the Open with program entry is still there' }
    if (OpenWithValuePresent) { $problems += 'the .evtx Open with value is still there' }
    if (-not $evtxKeyExistedBefore -and (Test-Path $evtxKey)) { $problems += 'the .evtx key that the installer created is still there' }
    if (Test-Path $crashFolder) { $problems += 'the crash log folder is still there' }
    Report 'silent_uninstall_leaves_no_files_shortcuts_registry_entries_or_crash_log' `
        'A silent uninstallation removes the program folder, the shortcut, the Installed apps entry, the Open with entries and the crash log folder.' $problems
}
finally {
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($savedCrashFolder) {
        if (Test-Path $crashFolder) { Remove-Item $crashFolder -Recurse -Force }
        Move-Item $savedCrashFolder $crashFolder
    }
}
exit $failures
