#define AppVersion "0.1.0"
[Setup]
AppId={{FA68F8F1-36C4-4AA7-9080-2403C52FB620}
AppName=pome
AppVersion={#AppVersion}
AppPublisher=pome contributors
DefaultDirName={autopf}\pome
DefaultGroupName=pome
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=pome-{#AppVersion}-x64-preview
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Pome.UI.exe
LicenseFile=..\LICENSE

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"
Source: "..\LICENSES.md"; DestDir: "{app}"

[Icons]
Name: "{group}\pome"; Filename: "{app}\Pome.UI.exe"

; Do not remove VM data, downloads or diagnostics on uninstall.
; Release pipeline must sign app/helper and installer before public distribution.
