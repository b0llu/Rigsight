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
; A log of every run in %TEMP% ("Setup Log <date> #<n>.txt"): what each step did, for when something goes wrong.
SetupLogging=yes

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

[UninstallDelete]
; An in-app update copies the installer here before running it (the agent removes it on its next start).
Type: filesandordirs; Name: "{app}\update"

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

// The folder of a file given the way the installed-programs list gives it (maybe quoted, maybe ",0" after it).
function FolderOf(Value: String): String;
var
  I: Integer;
begin
  Value := Trim(Value);
  I := Pos(',', Value);
  if I > 0 then Value := Copy(Value, 1, I - 1);
  StringChangeEx(Value, '"', '', True);
  Result := ExtractFileDir(Value);
end;

// RivaTuner in the installed-programs list (however it got there: on its own, with MSI Afterburner, another folder).
function RtssListed(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Key, Name, Value: String;
begin
  Result := False;
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';
  if not RegGetSubkeyNames(RootKey, Key, Names) then exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if RegQueryStringValue(RootKey, Key + '\' + Names[I], 'DisplayName', Name) and (Pos('RIVATUNER STATISTICS SERVER', Uppercase(Name)) > 0) then
    begin
      if RegQueryStringValue(RootKey, Key + '\' + Names[I], 'InstallLocation', Value) and FileExists(AddBackslash(Trim(Value)) + 'RTSS.exe') then Result := True
      else if RegQueryStringValue(RootKey, Key + '\' + Names[I], 'UninstallString', Value) and FileExists(AddBackslash(FolderOf(Value)) + 'RTSS.exe') then Result := True
      else if RegQueryStringValue(RootKey, Key + '\' + Names[I], 'DisplayIcon', Value) and FileExists(AddBackslash(FolderOf(Value)) + 'RTSS.exe') then Result := True;
      if Result then exit;
    end;
end;

// Whether RivaTuner is installed, so its option isn't offered. RTSS.exe must actually be there: RivaTuner's
// uninstaller leaves its registry key behind. The agent checks again (and more) before installing anything.
function RtssInstalled: Boolean;
var
  Value: String;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\RivaTuner Statistics Server\RTSS.exe')) or
    FileExists(ExpandConstant('{commonpf64}\RivaTuner Statistics Server\RTSS.exe'));
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\Unwinder\RTSS', 'InstallDir', Value) then
    Result := FileExists(AddBackslash(Value) + 'RTSS.exe');
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\Unwinder\RTSS', 'InstallPath', Value) then
    Result := FileExists(Value);
  if not Result and RegQueryStringValue(HKLM64, 'SOFTWARE\Unwinder\RTSS', 'InstallDir', Value) then
    Result := FileExists(AddBackslash(Value) + 'RTSS.exe');
  if not Result then Result := RtssListed(HKLM32) or RtssListed(HKLM64);
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
  // How installing RivaTuner went: 0 fine, 1 failed, 2 stopped responding (ended), 3 no winget (see the agent's --install-rtss).
  RtssCode: Integer;

// One post-install step: a clear heading and a moving bar while it runs (these can take minutes). Returns the exit code.
function RunStep(const Title, Detail, FileName, Params: String): Integer;
var
  Code: Integer;
begin
  Result := -1;
  WizardForm.StatusLabel.Caption := Title;
  WizardForm.FilenameLabel.Caption := Detail;
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    // Setup keeps its window responsive while it waits, so the bar keeps moving.
    if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log(Format('Could not run %s: %s', [FileName, SysErrorMessage(Code)]))
    else
    begin
      Log(Format('%s %s exited with %d', [FileName, Params, Code]));
      Result := Code;
    end;
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

// Passed by an in-app update ("Restart" in Rigsight): open the app again once the update is in.
function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  if WizardIsTaskSelected('pawnio') then
    Code := RunStep('Installing the PawnIO sensor driver...', 'Downloading and installing with winget. This can take a minute or two; setup is still working.',
      ExpandConstant('{cmd}'), '/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements');
  // Through the agent, which checks again that it isn't installed already, waits as long as the install makes
  // progress, brings a question from RivaTuner's installer to the front, and ends it only if it stops doing anything.
  if WizardIsTaskSelected('rtss') then
    RtssCode := RunStep('Installing RivaTuner Statistics Server...', 'Downloading and installing with winget. On a slow connection this can take a while; setup is still working.',
      ExpandConstant('{app}\Rigsight.Agent.exe'), '--install-rtss');
  // Setup already has admin rights: use them to register "start with Windows" and start the agent,
  // so the user never sees a second UAC prompt. After RivaTuner, so the agent can start it.
  Code := RunStep('Starting the Rigsight background agent...', '',
    ExpandConstant('{app}\Rigsight.Agent.exe'), '--register-startup');
  // Through Explorer, so the app opens with normal (non-admin) rights like any other launch.
  if RelaunchRequested then
    Exec(ExpandConstant('{win}\explorer.exe'), AddQuotes(ExpandConstant('{app}\Rigsight.exe')), '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Msg: String;
begin
  if (CurPageID <> wpFinished) or (RtssCode = 0) then exit;
  if RtssCode = 2 then
    Msg := 'RivaTuner''s installer stopped responding, so setup ended it.'
  else if RtssCode = 3 then
    Msg := 'RivaTuner Statistics Server needs winget (App Installer, from the Microsoft Store) to be installed automatically.'
  else
    Msg := 'RivaTuner Statistics Server couldn''t be installed automatically.';
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Msg +
    ' The overlay won''t show inside exclusive-fullscreen games until it is: you can install it later from the Overlay page in Rigsight.';
end;

// Updating over a running copy: stop it so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRigsight;
  Result := '';
end;
