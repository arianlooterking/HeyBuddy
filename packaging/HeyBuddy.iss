; Build through scripts/release.ps1 after validation. Models, credentials, and user data are never packaged.
#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "..\artifacts\release\HeyBuddy"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\artifacts\release"
#endif

[Setup]
AppId={{E442DC07-CBA2-4F7F-BD2A-0745F7F81BE9}
AppName=HeyBuddy
AppVersion={#AppVersion}
AppVerName=HeyBuddy {#AppVersion}
AppPublisher=Arian
DefaultDirName={localappdata}\Programs\HeyBuddy
DefaultGroupName=HeyBuddy
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
AppMutex=Local\ClickyLocal.Desktop
CloseApplications=yes
CloseApplicationsFilter=HeyBuddy.exe
RestartApplications=no
UninstallDisplayName=HeyBuddy
UninstallDisplayIcon={app}\HeyBuddy.exe
OutputDir={#ReleaseDir}
OutputBaseFilename=HeyBuddy-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\Clicky.Connectors.pdb"
Type: files; Name: "{app}\Clicky.Core.pdb"
Type: files; Name: "{app}\Clicky.Runtime.pdb"
Type: files; Name: "{app}\HeyBuddy.pdb"

[Icons]
Name: "{autoprograms}\HeyBuddy"; Filename: "{app}\HeyBuddy.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\HeyBuddy"; Filename: "{app}\HeyBuddy.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\HeyBuddy.exe"; Description: "Launch HeyBuddy"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove only an autostart command pointing to this installed executable. Never delete user data or models. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClickyLocal', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\HeyBuddy.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClickyLocal');
  end;
end;
