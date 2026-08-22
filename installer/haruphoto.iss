; haruphoto Windows installer
#define AppName "haruphoto"
#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#define AppPublisher "scj040921"
#define AppExeName "PhotoAlbum.exe"
#define BuildDir "..\bin\Release\net8.0-windows10.0.26100.0\win-x64"

[Setup]
AppId={{A1C4B7A9-2BB9-4F2E-9A7A-6F8BBE1D7B7A}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppVerName={#AppName} {#MyAppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=haruphoto-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
Uninstallable=yes
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\Assets\AppIcon.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
