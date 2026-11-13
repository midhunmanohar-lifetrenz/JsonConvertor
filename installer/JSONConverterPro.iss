; Inno Setup script for the Windows installer (per-user install, no admin rights needed).
; Built by publish.ps1 -Installer, or by the release workflow on GitHub Actions.
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif

[Setup]
AppId={{B6E0C1E4-6F0A-4C77-9C1B-3A0D9C1E2F11}
AppName=JSON Converter Pro
AppVersion={#AppVersion}
AppVerName=JSON Converter Pro {#AppVersion}
AppPublisher=Lifetrenz
DefaultDirName={autopf}\JSON Converter Pro
DefaultGroupName=JSON Converter Pro
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\release
OutputBaseFilename=JSONConverterPro-{#AppVersion}-win-x64-setup
SetupIconFile=..\JSONConverterPro.App\Resources\AppIcon.ico
UninstallDisplayIcon={app}\JSONConverterPro.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\JSONConverterPro.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\USER_GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\JSON Converter Pro"; Filename: "{app}\JSONConverterPro.exe"
Name: "{group}\User guide"; Filename: "{app}\USER_GUIDE.md"
Name: "{group}\Uninstall JSON Converter Pro"; Filename: "{uninstallexe}"
Name: "{autodesktop}\JSON Converter Pro"; Filename: "{app}\JSONConverterPro.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\JSONConverterPro.exe"; Description: "{cm:LaunchProgram,JSON Converter Pro}"; Flags: nowait postinstall skipifsilent
