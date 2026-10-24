; KUDUTREE installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.
; (The fast scan of a whole drive does: KUDUTREE asks Windows for them when it starts one.)

#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUTREE"
#endif

#define MyAppName "KUDUTREE"
#define MyAppExe "KUDUTREE.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudutree"

[Setup]
AppId={{5B7D3E92-A16C-4F08-9D2B-E43C71A85F06}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=KUDUTREE-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUTREE\Assets\KUDUTREE.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Show sizes with KUDUTREE"" to folders' and drives' right-click menu"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--register-shell"; Tasks: contextmenu; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUTREE now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "KUDUTREEUnregister"

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDUTREE"; Flags: uninsdeletekey dontcreatekey
; Saved scans (.kudutree) open in KUDUTREE.
Root: HKCU; Subkey: "Software\Classes\.kudutree"; ValueType: string; ValueName: ""; ValueData: "KUDUTREE.Scan"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\KUDUTREE.Scan"; ValueType: string; ValueName: ""; ValueData: "KUDUTREE scan"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\KUDUTREE.Scan\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\KUDUTREE.Scan\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExe}"" --open ""%1"""
