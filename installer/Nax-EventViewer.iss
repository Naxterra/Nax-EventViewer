; Installer for Nax Event Viewer. Build it with build-installer.ps1, which publishes the app and passes
; AppVersion and PublishDir to this script.

#ifndef AppVersion
  #error AppVersion is not defined. Run build-installer.ps1 instead of compiling this script directly.
#endif
#ifndef PublishDir
  #error PublishDir is not defined. Run build-installer.ps1 instead of compiling this script directly.
#endif

#define AppName "Nax Event Viewer"
#define AppExe "Nax-EventViewer.exe"
#define ProgId "NaxEventViewer.evtx"

[Setup]
AppId={{6B0E7C1A-4F2D-4C8B-9E31-5D7A2B9C4E60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Naxterra
DefaultDirName={autopf}\Nax-EventViewer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The app itself never needs administrator rights, so the default is an installation for the current user only.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Nax-EventViewer-{#AppVersion}-setup
SetupIconFile=..\src\NaxEventViewer.App\Assets\Nax-EventViewer.ico
; The license is shown for information with a Next button. Installing and running the app is what section 1 allows;
; the restrictions in sections 2 to 7 apply through copyright, not through a click. Replace this line with
; LicenseFile=..\LICENSE to make users accept the license before installing.
InfoBeforeFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
; The app holds this mutex while it runs. Setup and the uninstaller refuse to go on until it is closed, so no locked
; program file is left behind.
AppMutex=NaxEventViewer.Running

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.OpenWithTask=Offer {#AppName} in the "Open with" menu of .evtx files (Windows Event Viewer stays the default)
german.OpenWithTask={#AppName} im Menü „Öffnen mit“ von .evtx-Dateien anbieten (die Windows-Ereignisanzeige bleibt Standard)
english.DesktopTask=Create a desktop shortcut
german.DesktopTask=Desktopverknüpfung erstellen
english.LaunchApp=Start {#AppName}
german.LaunchApp={#AppName} starten
english.EvtxFileType=Event log file
german.EvtxFileType=Ereignisprotokolldatei
english.CrashLogQuestion={#AppName} kept a crash log in %1.%n%nDo you want to delete it as well?
german.CrashLogQuestion={#AppName} hat ein Absturzprotokoll in %1 gespeichert.%n%nMöchten Sie es ebenfalls löschen?
english.RestoreNeedsAdmin={#AppName} replaces the Windows Event Viewer, and only an uninstall with administrator rights can restore it. Turn the replacement off in the app's settings first, or uninstall it from Installed apps, which asks for administrator rights.
german.RestoreNeedsAdmin={#AppName} ersetzt die Windows-Ereignisanzeige, und nur eine Deinstallation mit Administratorrechten kann sie wiederherstellen. Schalten Sie das Ersetzen zuerst in den Einstellungen der App aus oder deinstallieren Sie sie über „Installierte Apps“, wo nach Administratorrechten gefragt wird.
english.RestoreFailed=The Windows Event Viewer could not be restored, so Windows still tries to start {#AppName} in its place. Delete the value "Debugger" under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\eventvwr.exe to restore it.
german.RestoreFailed=Die Windows-Ereignisanzeige konnte nicht wiederhergestellt werden, daher versucht Windows weiterhin, {#AppName} an ihrer Stelle zu starten. Löschen Sie den Wert „Debugger“ unter HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\eventvwr.exe, um sie wiederherzustellen.

[Tasks]
Name: "openwith"; Description: "{cm:OpenWithTask}"
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Only an "Open with" entry. The default program for .evtx files is not changed. The two .evtx keys are removed on
; uninstallation only when nothing else is left in them.
Root: HKA; Subkey: "Software\Classes\.evtx"; Flags: uninsdeletekeyifempty; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.evtx\OpenWithProgids"; Flags: uninsdeletekeyifempty; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.evtx\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "{cm:EvtxFileType}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith

[InstallDelete]
; The notices folder of earlier versions; THIRD-PARTY-NOTICES.txt now holds all notices in one file.
Type: filesandordirs; Name: "{app}\licenses"

[UninstallDelete]
; Copies of a whole log that a save interrupted by a crash left in the temporary folder of the user who uninstalls.
Type: files; Name: "{%TEMP}\nax-eventviewer-export-*.evtx"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  ReplacementKey = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\eventvwr.exe';

// The app writes its settings and, after a crash, a crash log into this folder of the user's local application data.
function DataFolder: String;
begin
  Result := ExpandConstant('{localappdata}\Nax-EventViewer');
end;

// True when Windows starts this installation's program in place of the Windows Event Viewer.
function ReplacementIsOurs: Boolean;
var
  Value: String;
begin
  Result := RegQueryStringValue(HKLM64, ReplacementKey, 'Debugger', Value) and
    (CompareText(RemoveQuotes(Trim(Value)), ExpandConstant('{app}\{#AppExe}')) = 0);
end;

// Nothing is changed here, because the user can still cancel and the uninstaller still waits for the app to close.
// It only refuses to start when this installation replaces the Windows Event Viewer and the uninstaller lacks the
// administrator rights to restore it, so the uninstall never leaves eventvwr.exe pointing at a deleted program.
function InitializeUninstall: Boolean;
begin
  Result := True;
  if ReplacementIsOurs and not IsAdmin then
  begin
    if not UninstallSilent then MsgBox(CustomMessage('RestoreNeedsAdmin'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CrashLog: String;
begin
  // The uninstall is now certain to go ahead: restore the Windows Event Viewer before any file is removed. Only a
  // replacement that points to this installation is removed; one that another program set up is left alone.
  if (CurUninstallStep = usUninstall) and ReplacementIsOurs then
  begin
    RegDeleteValue(HKLM64, ReplacementKey, 'Debugger');
    if ReplacementIsOurs then
    begin
      Log('The Windows Event Viewer replacement could not be removed.');
      if not UninstallSilent then MsgBox(CustomMessage('RestoreFailed'), mbError, MB_OK);
    end;
  end;

  if (CurUninstallStep = usPostUninstall) and DirExists(DataFolder) then
  begin
    // The settings always go. A silent uninstall removes the crash log as well, so nothing is left behind.
    DeleteFile(DataFolder + '\settings.json');
    DeleteFile(DataFolder + '\settings.json.tmp');
    CrashLog := DataFolder + '\crash.log';
    if FileExists(CrashLog) and (UninstallSilent or
      (MsgBox(FmtMessage(CustomMessage('CrashLogQuestion'), [CrashLog]), mbConfirmation, MB_YESNO) = IDYES)) then
      DeleteFile(CrashLog);
    // Removed only when nothing else is left in it.
    RemoveDir(DataFolder);
  end;
end;
