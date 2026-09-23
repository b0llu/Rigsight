; Rigsight installer (Inno Setup 6). Build it with tools\build-installer.ps1, which publishes
; the programs first and passes the version in.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{5AAE562A-D0E3-4CCA-82CA-6CA35DD0FF7E}
AppName=Rigsight
AppVersion={#AppVersion}
AppVerName=Rigsight {#AppVersion}
AppPublisher=Rigsight
AppPublisherURL=https://github.com/b0llu/Rigsight
DefaultDirName={autopf}\Rigsight
DefaultGroupName=Rigsight
DisableProgramGroupPage=yes
; The agent reads CPU and motherboard sensors, which needs admin rights anyway.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Rigsight-Setup-{#AppVersion}
SetupIconFile=..\assets\Rigsight.ico
UninstallDisplayIcon={app}\Rigsight.exe
UninstallDisplayName=Rigsight
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "pawnio"; Description: "Install the PawnIO driver (needed for CPU and motherboard temperatures)"; GroupDescription: "Sensors:"; Check: not PawnIOInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Rigsight"; Filename: "{app}\Rigsight.exe"
Name: "{group}\Uninstall Rigsight"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Rigsight"; Filename: "{app}\Rigsight.exe"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements"; StatusMsg: "Installing the PawnIO sensor driver..."; Flags: runhidden waituntilterminated; Tasks: pawnio
; Setup already has admin rights: use them to register "start with Windows" and start the agent,
; so the user never sees a second UAC prompt.
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--register-startup"; StatusMsg: "Starting the Rigsight background agent..."; Flags: runhidden waituntilterminated
Filename: "{app}\Rigsight.exe"; Description: "Open Rigsight"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Rigsight Agent"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[Code]
function PawnIOInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'))
         or RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure StopRigsight;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Rigsight.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Rigsight.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

// Updating over a running copy: stop it so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRigsight;
  Result := '';
end;
