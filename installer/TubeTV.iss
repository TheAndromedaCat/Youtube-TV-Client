#ifndef AppVersion
  #define AppVersion "2.0.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8E5F8A7B-4B94-4C9B-B17A-6D8C2E9A3F12}
AppName=TubeTV - YouTube TV Client
AppVersion={#AppVersion}
AppPublisher=TubeTV Contributors
AppSupportURL=https://github.com/TheAndromedaCat/Youtube-TV-Client
DefaultDirName={autopf}\TubeTV
DefaultGroupName=TubeTV
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=TubeTV-Setup-{#AppVersion}-x64
SetupIconFile=..\Assets\TubeTV.ico
UninstallDisplayIcon={app}\TubeTV.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TubeTV"; Filename: "{app}\TubeTV.exe"
Name: "{autodesktop}\TubeTV"; Filename: "{app}\TubeTV.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TubeTV.exe"; Description: "{cm:LaunchProgram,TubeTV}"; Flags: nowait postinstall skipifsilent
