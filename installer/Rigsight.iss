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
AppName=RigSight
AppVersion={#AppVersion}
AppVerName=RigSight {#AppVersion}
AppPublisher=RigSight
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
UninstallDisplayName=RigSight
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
; Only where it means something: a PC with more than one account (or setup started from someone's sign-in with an admin's
; password, which makes it two). A PC with one account sees setup exactly as before.
Name: "everyone"; Description: "Install RigSight for everyone on this PC (starts it for each person who signs in)"; GroupDescription: "Other accounts:"; Check: OtherAccounts
Name: "keepupdated"; Description: "Keep RigSight updated for everyone on this PC (leaves a small updater that Windows runs with admin rights; it only installs RigSight)"; GroupDescription: "Other accounts:"; Check: OtherAccounts

[InstallDelete]
; Riggy (0.16.0 to 0.17.1) was removed in 0.18.0: its model and the library that ran it, left by an update otherwise.
Type: filesandordirs; Name: "{app}\ask"
Type: files; Name: "{app}\onnxruntime.dll"
Type: files; Name: "{app}\onnxruntime.lib"
Type: files; Name: "{app}\onnxruntime_providers_shared.dll"
Type: files; Name: "{app}\onnxruntime_providers_shared.lib"
Type: files; Name: "{app}\Microsoft.ML.OnnxRuntime.dll"
; The shortcuts were "Rigsight" before the name became RigSight: removed first, so the new ones get the new spelling
; (Windows keeps a file's old spelling when it is only overwritten).
Type: files; Name: "{group}\Rigsight.lnk"
Type: files; Name: "{autodesktop}\Rigsight.lnk"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RigSight"; Filename: "{app}\Rigsight.exe"
Name: "{group}\Uninstall RigSight"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RigSight"; Filename: "{app}\Rigsight.exe"; Tasks: desktopicon

[Run]
; PawnIO, RivaTuner and starting the agent run from CurStepChanged below, so the progress bar can show
; they're still working (a finished-looking bar during a 2-minute download looks stuck).
Filename: "{app}\Rigsight.exe"; Description: "Open RigSight"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; An in-app update copies the installer here before running it (the agent removes it on its next start).
Type: filesandordirs; Name: "{app}\update"
; The note of whose agent an update closed, if an uninstall by the system came before it was read.
Type: files; Name: "{app}\closed-for-update.txt"

[UninstallRun]
; What was set up for the whole PC goes with it: the start for everyone, the updater task, the note of both.
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--everyone off"; Flags: runhidden waituntilterminated; RunOnceId: "EveryoneOff"
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--updater off"; Flags: runhidden waituntilterminated; RunOnceId: "UpdaterOff"
Filename: "{sys}\reg.exe"; Parameters: "delete HKLM\SOFTWARE\Rigsight /f"; Flags: runhidden; RunOnceId: "ForgetMachineSetup"
; Ask the agent to save and exit first (a forced kill would lose the game session in progress).
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitAgent"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rigsight.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{app}\Rigsight.Agent.exe"; Parameters: "--forget-tasks"; Flags: runhidden waituntilterminated; RunOnceId: "ForgetTasks"
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
  // The same for the PawnIO driver (-1 not asked for), and whether Rigsight was set up for everyone.
  PawnCode: Integer;
  ForEveryone: Boolean;
  // "For everyone" or "keep updated" was asked for and refused: Rigsight isn't installed under Program Files.
  NotProtected: Boolean;

// More than one person's account on this PC: their profiles, where the folder is really there (an account that was
// removed can leave its entry behind) and isn't the one Windows itself makes while setting a PC up ("defaultuser0").
// The system's own accounts aren't counted.
function OtherAccounts: Boolean;
var
  Names: TArrayOfString;
  I, People: Integer;
  Key, Folder: String;
begin
  People := 0;
  Key := 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList';
  if RegGetSubkeyNames(HKLM64, Key, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      // People's accounts: local and domain ones (S-1-5-21-), and work or school ones from Microsoft Entra (S-1-12-1-).
      if ((Pos('S-1-5-21-', Names[I]) = 1) or (Pos('S-1-12-1-', Names[I]) = 1)) and RegQueryStringValue(HKLM64, Key + '\' + Names[I], 'ProfileImagePath', Folder) then
      begin
        if DirExists(Folder) and (Pos('DEFAULTUSER', Uppercase(ExtractFileName(Folder))) <> 1) then People := People + 1;
      end;
  Result := People > 1;
end;

// What was chosen for the whole PC the last time (see the agent's MachineSetup).
function Marked(const Name: String): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Rigsight', Name, Value) and (Value <> 0);
end;

// "/EVERYONE=1" or "/KEEPUPDATED=0" on setup's command line, for installing on many PCs without the wizard: 1, 0, or -1 when not said.
function Switch(const Name: String): Integer;
var
  I: Integer;
  P: String;
begin
  Result := -1;
  for I := 1 to ParamCount do
  begin
    P := Uppercase(ParamStr(I));
    if P = '/' + Name + '=1' then Result := 1
    else if P = '/' + Name + '=0' then Result := 0;
  end;
end;

// Whether a choice for the whole PC is wanted: what the command line says; without the wizard, what it was before (an
// update never turns one on by itself); with it, the box as it was left.
function Wanted(const Task, Mark, Name: String): Boolean;
begin
  if Switch(Name) >= 0 then Result := Switch(Name) = 1
  else if WizardSilent then Result := Marked(Mark)
  else Result := OtherAccounts and WizardIsTaskSelected(Task);
end;

// One post-install step: a clear heading and a moving bar while it runs (these can take minutes). Returns the exit code.
// Detail is a single line in the wizard: keep it under about 70 characters or it gets cut off.
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

// What setup offers beside Rigsight itself, each only when it's ticked (and it's only offered where it's missing).
procedure InstallExtras;
begin
  PawnCode := -1;
  if WizardIsTaskSelected('pawnio') then
    PawnCode := RunStep('Installing the PawnIO sensor driver...', 'Downloading with winget. This can take a minute or two.',
      ExpandConstant('{app}\Rigsight.Agent.exe'), '--install-pawnio');
  // Through the agent, which checks again that it isn't installed already, waits as long as the install makes
  // progress, brings a question from RivaTuner's installer to the front, and ends it only if it stops doing anything.
  if WizardIsTaskSelected('rtss') then
    RtssCode := RunStep('Installing RivaTuner Statistics Server...', 'Downloading with winget. This can take a few minutes.',
      ExpandConstant('{app}\Rigsight.Agent.exe'), '--install-rtss');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Agent: String;
  ForSomeoneElse: Boolean;
begin
  if CurStep <> ssPostInstall then exit;
  Agent := ExpandConstant('{app}\Rigsight.Agent.exe');
  // Only when someone runs setup themselves. An update installed without a window ("Restart" in the app, or by itself
  // as Windows starts) only updates Rigsight: the extra minutes would look like something broke, and whatever was
  // removed since the first install stays removed. The app says what's missing instead.
  if not WizardSilent then InstallExtras;
  // For the whole PC, as chosen: start for everyone who signs in, and the updater that keeps it up to date for them.
  // Switched off only where one was on: a PC that never had either is left exactly as it was, nothing noted, nothing run.
  // (Exit code 2: not installed under Program Files, where only an admin can change the program. Neither is set up then.)
  ForEveryone := Wanted('everyone', 'Everyone', 'EVERYONE');
  if ForEveryone then
  begin
    Exec(Agent, '--everyone on', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then
    begin
      ForEveryone := False;
      NotProtected := Code = 2;
    end;
  end
  else if Marked('Everyone') then
    Exec(Agent, '--everyone off', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Wanted('keepupdated', 'KeepUpdated', 'KEEPUPDATED') then
  begin
    Exec(Agent, '--updater on', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code = 2 then NotProtected := True;
  end
  else if Marked('KeepUpdated') then
    Exec(Agent, '--updater off', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Setup already has admin rights: use them to register "start with Windows" and start the agent,
  // so the user never sees a second UAC prompt. After RivaTuner, so the agent can start it.
  Code := RunStep('Starting the RigSight background agent...', '', Agent, '--register-startup');
  // Exit code 4: setup runs with another account's admin rights than the person at the screen (a standard account,
  // an admin's password). Nothing was registered for that admin; the person's own agent is started as them.
  ForSomeoneElse := Code = 4;
  if ForSomeoneElse then
    ExecAsOriginalUser(Agent, '--resume', '', SW_HIDE, ewNoWait, Code);
  // An update the system installed (its updater, an IT tool) closed everyone's agent: start it again for each person
  // signed in. From any other account than the system this starts nothing.
  if WizardSilent then
    Exec(Agent, '--start-in-sessions', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Through Explorer, so the app opens with normal (non-admin) rights like any other launch; for the person at the
  // screen where setup runs as someone else (opened by setup itself, it would be that admin's Rigsight).
  if RelaunchRequested then
  begin
    if ForSomeoneElse then
      ExecAsOriginalUser(ExpandConstant('{app}\Rigsight.exe'), '', '', SW_SHOWNORMAL, ewNoWait, Code)
    else
      Exec(ExpandConstant('{win}\explorer.exe'), AddQuotes(ExpandConstant('{app}\Rigsight.exe')), '', SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

// What the last page adds: what was set up for the other people on this PC, and anything that couldn't be installed.
// The page is already laid out for its usual text, so the label grows and the "Open Rigsight" box moves down with it
// (left where it was, the box covered every added line).
procedure SayMore(const Text: String);
var
  Grew: Integer;
begin
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Text;
  Grew := WizardForm.FinishedLabel.AdjustHeight;
  WizardForm.RunList.Top := WizardForm.RunList.Top + Grew + ScaleY(4);
  WizardForm.RunList.Height := WizardForm.RunList.Height - Grew - ScaleY(4);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Msg: String;
begin
  if CurPageID <> wpFinished then exit;
  if ForEveryone then
    SayMore('RigSight now starts by itself for everyone who signs in on this PC. On accounts without admin rights, CPU temperature, drive health and each app''s internet use are off.');
  if NotProtected then
    SayMore('RigSight wasn''t set up for the other accounts on this PC: that is only done when it is installed in Program Files, where ordinary accounts can''t change it.');
  if PawnCode > 0 then
    SayMore('The PawnIO sensor driver couldn''t be installed. An admin who uses RigSight on this PC can install it later from Settings; accounts without admin rights don''t use it.');
  if RtssCode = 0 then exit;
  if RtssCode = 2 then
    Msg := 'RivaTuner''s installer stopped responding, so setup ended it.'
  else if RtssCode = 3 then
    Msg := 'RivaTuner Statistics Server needs winget (App Installer, from the Microsoft Store) to be installed automatically.'
  else
    Msg := 'RivaTuner Statistics Server couldn''t be installed automatically.';
  SayMore(Msg + ' The overlay won''t show inside exclusive-fullscreen games until it is: you can install it later from the Overlay page in RigSight.');
end;

// Updating over a running copy: stop it so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRigsight;
  Result := '';
end;
