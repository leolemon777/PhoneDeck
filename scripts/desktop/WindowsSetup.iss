#ifndef SourceDir
  #error SourceDir must point to a clean build-desktop package.
#endif
[Setup]
AppId={{9B92BEB7-5591-41D5-8612-730DF6190C02}
AppName=PhoneDeck Desktop Preview
AppVersion=2.0.0-alpha.1
DefaultDirName={localappdata}\Programs\PhoneDeck Desktop
DefaultGroupName=PhoneDeck
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=PhoneDeck-2.0.0-alpha.1-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PhoneDeck.Desktop.exe
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Tasks]
Name: desktopicon; Description: "Create desktop shortcut"; Flags: unchecked
[Icons]
Name: "{group}\PhoneDeck"; Filename: "{app}\Start-PhoneDeck.vbs"
Name: "{autodesktop}\PhoneDeck"; Filename: "{app}\Start-PhoneDeck.vbs"; Tasks: desktopicon
[Run]
Filename: "{app}\Start-PhoneDeck.vbs"; Description: "Open PhoneDeck"; Flags: postinstall shellexec skipifsilent
