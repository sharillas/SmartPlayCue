; ============================================================
; StagePlayout - Inno Setup script
;
; 1) Publicar primeiro (self-contained, single-file):
;    dotnet publish src/StagePlayout.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
; 2) Compilar este script com o Inno Setup (6.2+)
; ============================================================

#define AppName "StagePixPlay"
#define AppVersion "1.0.0"
#define AppExe "StagePixPlay.exe"

[Setup]
AppId={{B7E2A1C4-5D3F-4E8A-9C1B-2F6D8A0E5B31}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=StagePlayout-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Criar atalho no Ambiente de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir {#AppName}"; Flags: postinstall nowait skipifsilent
