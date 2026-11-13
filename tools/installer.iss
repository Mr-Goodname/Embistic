#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef ProjectRoot
  #define ProjectRoot ".."
#endif

[Setup]
AppId={{B3A4A8B8-71FD-4EF2-8459-B9C9E93C31B0}
AppName=Embistic
AppVersion={#AppVersion}
AppVerName=Embistic {#AppVersion}
AppPublisher=Embistic
AppPublisherURL=https://github.com/Mr-Goodname/Embistic
AppSupportURL=https://github.com/Mr-Goodname/Embistic/issues
AppUpdatesURL=https://github.com/Mr-Goodname/Embistic/releases
DefaultDirName={localappdata}\Programs\Embistic
DefaultGroupName=Embistic
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile={#ProjectRoot}\src\Embistic\Assets\embistic.ico
UninstallDisplayIcon={app}\Embistic.exe
UninstallDisplayName=Embistic
OutputDir={#ProjectRoot}\dist
OutputBaseFilename=Embistic-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
ChangesAssociations=no
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#ProjectRoot}\dist\Embistic-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Embistic"; Filename: "{app}\Embistic.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Embistic"; Filename: "{app}\Embistic.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Embistic.exe"; Description: "Launch Embistic"; Flags: nowait postinstall skipifsilent

; User settings/lyrics in LocalAppData\Embistic are deliberately outside {app}
; and are not removed by uninstall.
