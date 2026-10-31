<#
.SYNOPSIS
Installs Nax Event Viewer silently for the current user into a temporary folder, checks what the installer put in
place, uninstalls it and checks that nothing is left behind.

.DESCRIPTION
Run build-installer.ps1 first. The script refuses to run when Nax Event Viewer is already installed for the current
user, so it never touches a real installation. A crash log folder that already exists is moved aside during the
test and restored afterwards. The test starts the installed app twice and closes it again. Prints one line per test
and exits with the number of failed tests.
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
$evtxKey = 'HKCU:\Software\Classes\.evtx'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nax Event Viewer.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Nax Event Viewer.lnk'
$crashFolder = Join-Path $env:LOCALAPPDATA 'Nax-EventViewer'

if (Test-Path $uninstallKey) { throw 'Nax Event Viewer is installed for this user. Uninstall it before running this test.' }
# Setup waits for every open window of the app to close, so with one open the silent installation below would refuse.
if (Get-Process Nax-EventViewer -ErrorAction SilentlyContinue) { throw 'Nax Event Viewer is open. Close every window of it before running this test.' }
if (Test-Path $desktopShortcut) { throw "A desktop shortcut named Nax Event Viewer already exists: $desktopShortcut" }
$evtxKeyExistedBefore = Test-Path $evtxKey

$installDir = Join-Path ([IO.Path]::GetTempPath()) ('nax-eventviewer-install-test-' + [Guid]::NewGuid().ToString('N'))
$exe = Join-Path $installDir 'Nax-EventViewer.exe'
$uninstaller = Join-Path $installDir 'unins000.exe'
# A full log copy as a crashed .evtx save leaves it in the temporary folder.
$leftoverCopy = Join-Path ([IO.Path]::GetTempPath()) ('nax-eventviewer-export-' + [Guid]::NewGuid().ToString('N') + '.evtx')
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

function Start-InstalledApp {
    $process = Start-Process $exe -ArgumentList '--log', 'Application' -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 500
    }
    return $process
}

function Stop-InstalledApp($process) {
    if ($process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
}

function Wait-Uninstall([string[]]$arguments) {
    Start-Process $uninstaller -ArgumentList $arguments -Wait
    # The uninstaller hands over to a copy of itself, so wait until that copy has finished too.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$env:TEMP*\_iu*.tmp" })) {
        Start-Sleep -Milliseconds 500
    }
    while ((Test-Path $installDir) -and (Test-Path $uninstallKey) -eq $false -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

$app = $null
try {
    $install = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$installDir`"", '/TASKS=openwith,desktopicon' -Wait -PassThru
    $problems = @()
    if ($install.ExitCode -ne 0) { $problems += "setup exited with code $($install.ExitCode)" }
    if (-not (Test-Path $exe)) { $problems += 'the program file is missing' }
    if (-not (Test-Path (Join-Path $installDir 'Assets\Nax-EventViewer.ico'))) { $problems += 'the icon file is missing' }
    $license = Join-Path $installDir 'LICENSE.txt'
    if (-not ((Test-Path $license) -and ((Get-Content $license -TotalCount 1) -match '^Nax Source-Available License'))) { $problems += 'the license text is missing' }
    $noticesFile = Join-Path $installDir 'THIRD-PARTY-NOTICES.txt'
    $noticesText = if (Test-Path $noticesFile) { Get-Content $noticesFile -Raw } else { '' }
    foreach ($component in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsAppSDK', 'Microsoft.Windows.SDK.NET.Ref') {
        if ($noticesText -notmatch "(?m)^$([regex]::Escape($component)) ") { $problems += "the third-party notices do not list $component" }
    }
    # The full license texts are in the file, not only the names.
    if ($noticesText -notmatch 'Permission is hereby granted, free of charge') { $problems += 'the third-party notices do not contain the license texts' }
    if (-not (Test-Path $uninstallKey)) { $problems += 'there is no entry in Installed apps' }
    if (-not (Test-Path $shortcut)) { $problems += 'the Start menu shortcut is missing' }
    if (-not (Test-Path $desktopShortcut)) { $problems += 'the desktop shortcut is missing' }
    if (-not (OpenWithValuePresent)) { $problems += 'the Open with entry for .evtx files is missing' }
    $command = if (Test-Path "$progIdKey\shell\open\command") { (Get-Item "$progIdKey\shell\open\command").GetValue('') } else { '' }
    if ($command -notlike "*$installDir\Nax-EventViewer.exe*") { $problems += "the Open with command is wrong: $command" }
    Report 'install_for_current_user_puts_program_licenses_shortcuts_and_open_with_entry_in_place' `
        'A silent installation for the current user installs the program with its license text and THIRD-PARTY-NOTICES.txt holding the full licenses of the bundled .NET runtime, Windows App SDK and Windows SDK projection, the Start menu and desktop shortcuts, the Installed apps entry and the Open with entry for .evtx files.' $problems

    $problems = @()
    if (-not (Test-Path (Join-Path $installDir 'coreclr.dll'))) { $problems += 'the .NET runtime (coreclr.dll) is not in the program folder' }
    $runtimeConfig = Join-Path $installDir 'Nax-EventViewer.runtimeconfig.json'
    if ((Test-Path $runtimeConfig) -and ((Get-Content $runtimeConfig -Raw) -match '"framework"')) { $problems += 'the app still asks for a .NET installed on the PC' }
    $app = Start-InstalledApp
    if ($app.HasExited) { $problems += "the app ended at start with exit code $($app.ExitCode)" }
    elseif ($app.MainWindowHandle -eq 0) { $problems += 'the app showed no window within 45 seconds' }
    else {
        # The installer finds the running app through this mutex, so a normal process must be able to open it.
        $mutex = $null
        if ([Threading.Mutex]::TryOpenExisting('NaxEventViewer.Running', [ref]$mutex)) { $mutex.Dispose() }
        else { $problems += 'the running app has no mutex that a normal process can open' }
    }
    Report 'installed_app_carries_its_own_dotnet_starts_and_creates_its_running_mutex' `
        'The installed program folder contains the .NET runtime, the app does not ask for a .NET installed on the PC, it starts and shows its window, and another normal process can open its running mutex. An app started as administrator is not covered.' $problems

    # Uninstalling while the app runs must either refuse and leave the installation whole, or remove all of it.
    $problems = @()
    if ($app -and -not $app.HasExited) {
        Wait-Uninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $whole = (Test-Path $exe) -and (Test-Path $uninstallKey) -and (Test-Path $uninstaller)
        $gone = -not (Test-Path $installDir) -and -not (Test-Path $uninstallKey)
        if (-not ($whole -or $gone)) { $problems += 'the uninstallation stopped halfway: some parts are gone and others are still there' }
        Stop-InstalledApp $app
        $app = $null
    }
    else { $problems += 'the app was not running, so this case could not be tried' }
    Report 'uninstall_while_the_app_runs_removes_all_or_nothing' `
        'Uninstalling while the app is open either refuses and leaves the installation complete, or removes all of it, but never leaves half of it.' $problems

    # A crash log and a log copy written while the app was installed must go with the app.
    New-Item -ItemType Directory -Force $crashFolder | Out-Null
    Set-Content (Join-Path $crashFolder 'crash.log') 'test entry'
    Set-Content (Join-Path $crashFolder 'settings.json') '{"Language":"de-DE"}'
    Set-Content $leftoverCopy 'test copy'

    $problems = @()
    if (Test-Path $uninstaller) {
        Wait-Uninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $deadline = (Get-Date).AddSeconds(60)
        while ((Test-Path $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    elseif (Test-Path $installDir) { $problems += 'the uninstaller is missing' }
    if (Test-Path $installDir) { $problems += "the program folder is still there: $installDir" }
    if (Test-Path $uninstallKey) { $problems += 'the Installed apps entry is still there' }
    if (Test-Path $shortcut) { $problems += 'the Start menu shortcut is still there' }
    if (Test-Path $desktopShortcut) { $problems += 'the desktop shortcut is still there' }
    if (Test-Path $progIdKey) { $problems += 'the Open with program entry is still there' }
    if (OpenWithValuePresent) { $problems += 'the .evtx Open with value is still there' }
    if (-not $evtxKeyExistedBefore -and (Test-Path $evtxKey)) { $problems += 'the .evtx key that the installer created is still there' }
    if (Test-Path $crashFolder) { $problems += 'the folder with the settings and the crash log is still there' }
    if (Test-Path $leftoverCopy) { $problems += 'the log copy left in the temporary folder is still there' }
    Report 'silent_uninstall_leaves_no_files_shortcuts_registry_entries_settings_crash_log_or_log_copies' `
        'A silent uninstallation removes the program folder, both shortcuts, the Installed apps entry, the Open with entries, the settings, the crash log and log copies left in the temporary folder.' $problems
}
finally {
    Stop-InstalledApp $app
    if (Test-Path $uninstallKey) { Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction SilentlyContinue }
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $leftoverCopy) { Remove-Item $leftoverCopy -Force }
    if (Test-Path $crashFolder) { Remove-Item $crashFolder -Recurse -Force }
    if ($savedCrashFolder) { Move-Item $savedCrashFolder $crashFolder }
}
exit $failures
