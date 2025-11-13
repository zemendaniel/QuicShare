; -------------------------------------------------------------
; QuicShare Installer Script with Uninstall
; Self-contained Avalonia Windows App
; -------------------------------------------------------------

[Setup]
AppName=QuicShare
AppVersion=1.0
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

; -------------------------------
; Use a relative path to your published folder
; This assumes the script is in the 'installer/' folder
; -------------------------------
#define PublishDir "..\QuicFileSharing.GUI\bin\Release\net9.0\win-x64\publish"

[Files]
Source: "{#PublishDir}\QuicShare.exe"; DestDir: "{app}"; Flags: ignoreversion
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
