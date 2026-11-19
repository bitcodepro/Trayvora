[Setup]
AppId={{97185E8B-5C64-4FBA-86A5-02D8F27B6B5D}
AppName=Trayvora
AppVersion={#TrayvoraVersion}
DefaultDirName={localappdata}\Programs\Trayvora
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\Trayvora.exe
SetupIconFile=..\..\src\VoiceAgent.Desktop\Assets\trayvora.ico
OutputBaseFilename=trayvora-win-x64-setup
Compression=lzma2/fast
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "START.txt"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\Trayvora"; Filename: "{app}\Trayvora.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Trayvora"; Filename: "{app}\Trayvora.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Trayvora.exe"; Description: "Start Trayvora"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  StartupCommand: String;
begin
  // Remove a tray-created startup entry only when it points at this installation.
  if (Step = usPostUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Trayvora.VoiceAgent', StartupCommand) and
     (CompareText(StartupCommand, '"' + ExpandConstant('{app}\Trayvora.exe') + '"') = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Trayvora.VoiceAgent');
end;
