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
Name: "rtss"; Description: "Install RivaTuner Statistics Server (shows the game overlay inside fullscreen games)"; GroupDescription: "Game overlay:"; Check: not RtssInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Rigsight"; Filename: "{app}\Rigsight.exe"
Name: "{group}\Uninstall Rigsight"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Rigsight"; Filename: "{app}\Rigsight.exe"; Tasks: desktopicon

[Run]
; PawnIO, RivaTuner and starting the agent run from CurStepChanged below, so the progress bar can show
; they're still working (a finished-looking bar during a 2-minute download looks stuck).
Filename: "{app}\Rigsight.exe"; Description: "Open Rigsight"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Ask the agent to save and exit first (a forced kill would lose the game session in progress).
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitAgent"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Rigsight Agent"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[Code]
// PawnIO's own files must be there: its uninstaller can leave the driver's service entry behind,
// which on its own would hide the option after an uninstall (reinstalling over a leftover is harmless).
function PawnIOInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\PawnIO\PawnIOLib.dll'));
end;

// RTSS.exe must actually be there: RivaTuner's uninstaller leaves its registry key behind, so the key
// alone would hide the option after an uninstall.
function RtssInstalled: Boolean;
var
  Dir: String;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\RivaTuner Statistics Server\RTSS.exe'));
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\Unwinder\RTSS', 'InstallDir', Dir) then
    Result := FileExists(AddBackslash(Dir) + 'RTSS.exe');
end;

procedure StopRigsight;
var
  Code: Integer;
begin
  // Ask a running agent to save and exit cleanly; the force-kill below only catches what's left.
  if FileExists(ExpandConstant('{app}\Rigsight.Agent.exe')) then
    Exec(ExpandConstant('{app}\Rigsight.Agent.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Rigsight.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Rigsight.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

var
  RtssFailed: Boolean;

// One post-install step: a clear heading and a moving bar while it runs (these can take minutes).
procedure RunStep(const Title, Detail, FileName, Params: String);
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := Title;
  WizardForm.FilenameLabel.Caption := Detail;
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    // Setup keeps its window responsive while it waits, so the bar keeps moving.
    if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log(Format('Could not run %s: %s', [FileName, SysErrorMessage(Code)]))
    else
      Log(Format('%s %s exited with %d', [FileName, Params, Code]));
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then exit;
  if WizardIsTaskSelected('pawnio') then
    RunStep('Installing the PawnIO sensor driver...', 'Downloading and installing with winget. This can take a minute or two; setup is still working.',
      ExpandConstant('{cmd}'), '/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements');
  if WizardIsTaskSelected('rtss') then
  begin
    RunStep('Installing RivaTuner Statistics Server...', 'Downloading and installing with winget. This can take a few minutes; setup is still working.',
      ExpandConstant('{cmd}'), '/c winget install --id Guru3D.RTSS -e --silent --accept-package-agreements --accept-source-agreements');
    RtssFailed := not RtssInstalled;
  end;
  // Setup already has admin rights: use them to register "start with Windows" and start the agent,
  // so the user never sees a second UAC prompt. After RivaTuner, so the agent can start it.
  RunStep('Starting the Rigsight background agent...', '',
    ExpandConstant('{app}\Rigsight.Agent.exe'), '--register-startup');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and RtssFailed then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'RivaTuner Statistics Server couldn''t be installed automatically, so the overlay won''t show inside exclusive-fullscreen games yet. ' +
      'You can install it later from the Overlay page in Rigsight.';
end;

// Updating over a running copy: stop it so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRigsight;
  Result := '';
end;
