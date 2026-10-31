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

[Tasks]
Name: "openwith"; Description: "Offer {#AppName} in the ""Open with"" menu of .evtx files (Windows Event Viewer stays the default)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

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
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Event log file"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith

[InstallDelete]
; The notices of components an earlier version bundled and this one no longer does must not stay behind.
Type: filesandordirs; Name: "{app}\licenses"

[UninstallDelete]
; Copies of a whole log that a save interrupted by a crash left in the temporary folder of the user who uninstalls.
Type: files; Name: "{%TEMP}\nax-eventviewer-export-*.evtx"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// The app writes nothing but a crash log, into this folder of the user's local application data.
function CrashLogFolder: String;
begin
  Result := ExpandConstant('{localappdata}\Nax-EventViewer');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DirExists(CrashLogFolder) then
  begin
    // A silent uninstall removes the crash log as well, so nothing is left behind.
    if UninstallSilent or (MsgBox('Nax Event Viewer kept a crash log in ' + CrashLogFolder + '.' + #13#10 + #13#10 +
      'Do you want to delete it as well?', mbConfirmation, MB_YESNO) = IDYES) then
      DelTree(CrashLogFolder, True, True, True);
  end;
end;
