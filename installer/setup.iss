; ============================================================
; Smart Play Cue - Inno Setup installer
; Self-contained .NET EXE (sem dependencias) + FFmpeg nativo
; ============================================================
#define MyAppName "Smart Play Cue"
#define MyAppVersion "2.6.8"
#define MyAppPublisher "SmartChoice"
#define MyAppExeName "SmartPlayCue.exe"

[Setup]
AppId={{8F1D7B6A-9C2E-4E3A-B5D0-6E7A1F2C3D44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Smart Play Cue
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=SmartPlayCue-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\StagePlayout.App\Assets\app-icon.ico
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\SmartPlayCue.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\FFmpeg\*.dll"; DestDir: "{app}\FFmpeg"; Flags: ignoreversion
; VC++ Redistributable (runtime para as DLLs FFmpeg/MSVC)
Source: "..\installer\redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist; Check: NeedsVcRedist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsVcRedist: Boolean;
var
  vcMajor: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', vcMajor) then
    if vcMajor >= 14 then
      Result := False;
end;





