; Refraction Studio — Windows installer (Inno Setup 6.3+)
;
; Built by packaging/package_windows.py, which publishes the application (self-contained, win-x64)
; into ..\..\artifacts\publish\win-x64 and passes /DAppVersion=… /DSourceDir=… /DOutputDir=….
; Code signing: pass /DSignTool=1 and configure a sign tool named "rssign" in the Inno Setup compiler
; (see docs/DEPLOYMENT.md). Unsigned builds are for testing only.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#define AppName "Refraction Studio"
#define AppPublisher "QuakeLogic Inc."
#define AppExe "RefractionStudio.exe"
#define AppUrl "https://www.quakelogic.net/"

[Setup]
; The AppId identifies the product for upgrades and uninstallation. Never change it.
AppId={{FF95C27A-6EC2-48D1-970F-23E8A9B33E4E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL=https://www.quakelogic.net/support
AppUpdatesURL={#AppUrl}
AppCopyright=Copyright © 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\QuakeLogic\Refraction Studio
DefaultGroupName=Refraction Studio
DisableProgramGroupPage=yes
; Installs for all users by default (administrator), or for the current user only when chosen.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=EULA.txt
OutputDir={#OutputDir}
OutputBaseFilename=RefractionStudio-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\RefractionStudio.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes
#ifdef SignTool
SignTool=rssign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "Open Refraction Studio projects (.rfsx) with Refraction Studio"; GroupDescription: "File types:"

[Files]
; Refraction Studio's own executable and assemblies are code-signed when the installer is built with
; /DSignTool=1 (third-party files keep their publishers' signatures).
#ifdef SignTool
Source: "{#SourceDir}\RefractionStudio*.exe"; DestDir: "{app}"; Flags: ignoreversion signonce
Source: "{#SourceDir}\RefractionStudio*.dll"; DestDir: "{app}"; Flags: ignoreversion signonce
#else
Source: "{#SourceDir}\RefractionStudio*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\RefractionStudio*.dll"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "{#SourceDir}\*"; Excludes: "RefractionStudio*.exe,RefractionStudio*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\samples\Refraction-Studio-Sample.rfsx"; DestDir: "{app}\Samples"; Flags: ignoreversion
Source: "EULA.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Refraction Studio"; Filename: "{app}\{#AppExe}"; Comment: "Seismic refraction analysis"
Name: "{autodesktop}\Refraction Studio"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Project file association (per machine or per user, following the installation mode).
Root: HKA; Subkey: "Software\Classes\.rfsx"; ValueType: string; ValueName: ""; ValueData: "QuakeLogic.RefractionStudio.Project"; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.rfsx\OpenWithProgids"; ValueType: string; ValueName: "QuakeLogic.RefractionStudio.Project"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: associate
Root: HKA; Subkey: "Software\Classes\QuakeLogic.RefractionStudio.Project"; ValueType: string; ValueName: ""; ValueData: "Refraction Studio project"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\QuakeLogic.RefractionStudio.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\QuakeLogic.RefractionStudio.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Refraction Studio}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only files the application itself created next to the program. User projects (Documents\Refraction
; Studio), settings, licence and logs (%LocalAppData%\QuakeLogic\RefractionStudio) are kept.
Type: filesandordirs; Name: "{app}\Samples"

[Code]
// Projects and settings are never removed by the uninstaller; tell the user where they are.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Refraction Studio has been removed.' #13#10#13#10 +
      'Your projects (Documents\Refraction Studio) and your settings and licence ' +
      '(%LocalAppData%\QuakeLogic\RefractionStudio) were kept.', mbInformation, MB_OK);
end;
