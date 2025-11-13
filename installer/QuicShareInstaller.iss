; -------------------------------------------------------------
; QuicShare Installer Script with Uninstall
; Self-contained Avalonia Windows App
; -------------------------------------------------------------

; This PublishDir placeholder will be replaced by the workflow per platform
#define PublishDir "C:\placeholder\path"

; Provide a default AppVersion if not defined by workflow
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={49c50815-b575-44bd-ba5c-0182831f}
AppName=QuicShare
AppVersion={#AppVersion}       ; <- preprocessor symbol
AppVerName=QuicShare {#AppVersion}  ; satisfies Inno requirement
DefaultDirName={pf}\QuicShare
DefaultGroupName=QuicShare
DisableProgramGroupPage=no
OutputDir=.
OutputBaseFilename=QuicShareSetup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
UninstallDisplayIcon={app}\QuicShare.exe
Uninstallable=yes
SetupIconFile=..\QuicFileSharing.GUI\Assets\icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; Flags: exclusive

[Files]
; Main executable
Source: "{#PublishDir}\QuicShare.exe"; DestDir: "{app}"; Flags: ignoreversion
; Other files
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: QuicShare.exe

[Icons]
Name: "{group}\QuicShare"; Filename: "{app}\QuicShare.exe"; IconFilename: "{app}\QuicShare.exe"; Tasks: startmenuicon
Name: "{userdesktop}\QuicShare"; Filename: "{app}\QuicShare.exe"; IconFilename: "{app}\QuicShare.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QuicShare.exe"; Description: "Launch QuicShare"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Delete Start Menu folder
Type: filesandordirs; Name: "{group}"
; Delete Desktop shortcut
Type: files; Name: "{userdesktop}\QuicShare.lnk"
