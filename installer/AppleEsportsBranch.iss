; ============================================================================
;  Apple Esports ERP - branch installer
;
;  One file, two kinds of machine:
;
;    Operator counter PC  - database, API and dashboard, all local. The branch
;                           trades with no internet at all.
;    Customer gaming PC   - the agent that locks and unlocks the screen.
;
;  Build:  pwsh installer\build-branch-installer.ps1
;  (stages everything first, then compiles this)
; ============================================================================

#define AppName        "Apple Esports"
#define AppVersion     "3.1.98"
#define AppPublisher   "Apple Esports"
#define Staging        "branch\staging"

[Setup]
; Same AppId as the client-only build on purpose: a branch that already has the
; thin client installed upgrades in place rather than ending up with two entries
; in Programs and Features and two icons that do different things.
AppId={{7C4F1E62-9A3B-4D58-8E11-2F6A0B93C4D7}

AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Registers Windows services and writes to Program Files.
PrivilegesRequired=admin

OutputDir=..\dist
OutputBaseFilename=AppleEsports-Branch-Setup-{#AppVersion}
SetupIconFile=..\desktop-client\appicon.ico
UninstallDisplayIcon={app}\AppleEsports.exe
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "operator"; Description: "Operator counter PC  -  runs the branch (database + dashboard)"
Name: "gaming";   Description: "Customer gaming PC  -  locked screen only"

[Components]
Name: "core";   Description: "Apple Esports dashboard";     Types: operator gaming; Flags: fixed
Name: "server"; Description: "Branch database and services"; Types: operator
Name: "agent";  Description: "Gaming PC screen lock";        Types: gaming

[Files]
; -- Always --
Source: "..\desktop-client\publish\AppleEsports.exe"; DestDir: "{app}"; Components: core; Flags: ignoreversion
Source: "..\SHORTCUT_KEYS.md";                        DestDir: "{app}"; Components: core; Flags: ignoreversion

; Run by MainForm on every "session-ended" message from the overlay page - closes whatever
; a customer opened during their session. Shipped on both roles (harmless, just never
; triggered on an operator PC) rather than gated to the gaming component alone, since a
; machine can change role after install via Ctrl+Shift+P without a reinstall.
Source: "branch\end-session-cleanup.ps1"; DestDir: "{app}"; Components: core; Flags: ignoreversion
; The SYSTEM updater. Ships to every role: this is what installs updates with nothing to click,
; on counter PCs and gaming PCs alike. See KioskGuard.EnsureAutoUpdateTask.
Source: "branch\apply-update.ps1"; DestDir: "{app}"; Components: core; Flags: ignoreversion
; Restarts this PC if AppleEsports.exe ends without going through its own exit - a Task Manager
; "End Task" in particular. See restart-on-kill.ps1 for the full reasoning.
Source: "branch\restart-on-kill.ps1"; DestDir: "{app}"; Components: core; Flags: ignoreversion
Source: "branch\setup-restart-on-kill.ps1"; DestDir: "{app}"; Components: core; Flags: ignoreversion
; The restart above already resumes the kiosk on its own - the [Registry] Run key below (and
; KioskGuard.EnsureStartsOnBoot's own repair of it) already relaunches AppleEsports.exe on
; every boot/logon, so there is nothing new to register here for that half of this feature.
; NOTE: AppleEsports.config.json is deliberately NOT shipped. It is written by
; WriteClientConfig below, per machine, because what belongs in it depends on which kind
; of PC this is. The version in the repo is a developer's, pointed at a public server and
; carrying the dashboard gate password in plain text - it must never reach a branch.

; -- Operator counter PC: the whole branch --
Source: "{#Staging}\api\*";   DestDir: "{app}\api";   Components: server; Check: InstallServerParts; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Staging}\pgsql\*"; DestDir: "{app}\pgsql"; Components: server; Check: InstallServerParts; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "branch\setup-database.ps1"; DestDir: "{app}"; Components: server; Check: InstallServerParts; Flags: ignoreversion
Source: "branch\stop-services.ps1"; Flags: dontcopy
Source: "branch\setup-api.ps1";      DestDir: "{app}"; Components: server; Check: InstallServerParts; Flags: ignoreversion

; Staff-facing workaround for Ctrl+Alt+Q not registering on some keyboard layouts - simulates
; the real shortcut rather than force-killing the app. See SHORTCUT_KEYS.md.
Source: "branch\quit-app.ps1"; DestDir: "{app}"; Components: server; Check: InstallServerParts; Flags: ignoreversion

; -- Customer gaming PC --
; In its own subfolder, not directly under {app}: {app}\agent is the ONLY place on a gaming PC
; that gets Permissions: users-modify below, so the customer account that is logged in when the
; agent self-updates can replace this one exe and nothing else under Program Files - AppleEsports.exe
; and the setup scripts alongside it stay admin-only, same as an operator PC. See
; AgentSelfUpdater.cs and ReleasesController.AgentLatest/UploadAgent for the other half of this.
Source: "..\AppleEsportsErp\src\AppleEsportsErp.ClientAgent\publish\AppleEsportsAgent.exe"; DestDir: "{app}\agent"; Components: agent; Check: InstallAgentParts; Flags: ignoreversion; Permissions: users-modify
; Never shipped before this - the agent had no admin PIN, no health-check/failover timing, no
; cloud URL, nothing, on any machine actually installed through this installer, and would only
; run at all on one where somebody had copied the file in by hand. Deliberately NOT given
; Permissions: users-modify like the exe above: it holds AdminPinHash, and a customer able to
; rewrite that could clear the PIN gate protecting the lock screen's own escape hatch.
Source: "..\AppleEsportsErp\src\AppleEsportsErp.ClientAgent\publish\appsettings.agent.json"; DestDir: "{app}\agent"; Components: agent; Check: InstallAgentParts; Flags: onlyifdoesntexist

[Dirs]
; Created up front so the setup scripts are never the first thing to touch them.
Name: "{app}\backups"; Components: server

; Modify (not just write) because the agent's self-update swaps this exe by renaming the running
; file aside and moving a freshly-downloaded, hash-verified one into its place - both of which
; need write+delete on the folder, not only on the file that already exists in it. Scoped to this
; one subfolder rather than the whole {app} tree: a gaming PC's logged-in account is a customer
; seat, and nothing else installed here should be writable by whoever is sitting at it.
Name: "{app}\agent"; Components: agent; Check: InstallAgentParts; Permissions: users-modify

[Registry]
; Starts the app when Windows starts, which nothing did before this. A gaming PC that lost
; power came back to the Windows desktop with no lock screen, no member login and no billing
; - free to use. The old installer's opt-in start-with-Windows checkbox, which people
; remember, lives in AppleEsports.iss and was never carried into the installer we ship.
;
; HKLM rather than HKCU, and that distinction is the whole fix working or not. This installer
; runs elevated, so HKCU during setup is the ADMINISTRATOR's hive - not the account the
; operator or the customer actually logs in as. The entry would have been written to a user
; who never signs in at that machine, and the PC would have carried on booting to a bare
; desktop with the registry looking correct. Inno warns about exactly this
; (UsedUserAreasWarning) and it is right to. HKLM starts the app for whoever logs in, which
; is what a shared kiosk machine wants anyway.
; See KioskGuard.EnsureStartsOnBoot for the per-user fallback. uninsdeletevalue so uninstalling really
; does stop it coming back.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AppleEsports"; ValueData: """{app}\AppleEsports.exe"""; Flags: uninsdeletevalue

; KioskGuard.EnsureStartsOnBoot falls back to writing this same value under HKCU (the logged-in
; user's own hive) whenever HKLM was unreadable or wrong at some point during the machine's life -
; see the comment there. This installer never writes that fallback itself (ValueType: none means
; install does nothing here), but uninstall must still remove it if KioskGuard ever did, or the
; machine would keep relaunching a deleted AppleEsports.exe every time that user logs in, forever,
; with "Uninstall a program" having reported a clean removal.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AppleEsports"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#AppName}";              Filename: "{app}\AppleEsports.exe"
Name: "{group}\Keyboard shortcuts";      Filename: "{app}\SHORTCUT_KEYS.md"
Name: "{group}\Uninstall {#AppName}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";        Filename: "{app}\AppleEsports.exe"
; Nothing has ever shipped a way to launch the agent - it has only ever been started by hand, from
; whatever shortcut a technician made pointing at wherever the exe happened to be. Now that it
; lives under {app}\agent (see [Files]/[Dirs] above), that hand-made shortcut is exactly what
; would silently stop working; these replace it with one the installer keeps correct.
Name: "{group}\Apple Esports Agent";     Filename: "{app}\agent\AppleEsportsAgent.exe"; Components: agent
Name: "{autodesktop}\Apple Esports Agent"; Filename: "{app}\agent\AppleEsportsAgent.exe"; Components: agent

[Run]
; The branch setup is NOT run from here. A [Run] step that fails is ignored, and the
; wizard still reports success - which is exactly how an install ends up looking fine
; while the branch has no database and can never start. It runs from CurStepChanged
; instead, where the exit code can be checked and a failure actually reported.
; Registers the auto-update task from HERE, while Setup still has administrator rights.
;
; It used to be done by the app on launch, and that could never work on a gaming PC: creating a
; task that runs as SYSTEM needs admin, and the app runs as whoever is logged in. It failed
; silently on every launch, so a gaming PC stayed on an old version with the script sitting right
; there on disk and no error anywhere. The counter PC masked it completely, because its app happens
; to run elevated and so the same broken code appeared to work.
;
; runascurrentuser is wrong here and deliberately not used - Setup is elevated and that elevation
; is exactly what this needs.
Filename: "powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\apply-update.ps1"" -Register"; StatusMsg: "Setting up automatic updates..."; Flags: runhidden waituntilterminated; Components: core

; Same elevation reasoning as the auto-update registration just above - both of these need
; admin rights that only Setup has right now, and both must be re-run on every upgrade in case
; anything about them (the auditing policy, the exe's own path) needs restoring.
Filename: "powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\setup-restart-on-kill.ps1"" -AppExePath ""{app}\AppleEsports.exe"""; StatusMsg: "Setting up unexpected-close protection..."; Flags: runhidden waituntilterminated; Components: core

Filename: "{app}\AppleEsports.exe"; Description: "Set up this PC now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Services must go before the files they point at, or Windows is left with entries
; referencing an executable that no longer exists and the names cannot be reused.
Filename: "sc.exe"; Parameters: "stop AppleEsportsApi";   Flags: runhidden; RunOnceId: "StopApi"
Filename: "sc.exe"; Parameters: "delete AppleEsportsApi"; Flags: runhidden; RunOnceId: "DelApi"
Filename: "sc.exe"; Parameters: "stop AppleEsportsDb";    Flags: runhidden; RunOnceId: "StopDb"
Filename: "sc.exe"; Parameters: "delete AppleEsportsDb";  Flags: runhidden; RunOnceId: "DelDb"
; The kiosk watchdog, for the same reason as the services above: left behind, it would keep
; trying to launch an executable that has just been deleted, every two minutes, for ever.
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""AppleEsports Kiosk Guard"""; Flags: runhidden; RunOnceId: "DelKioskGuard"
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""AppleEsports Auto Update"""; Flags: runhidden; RunOnceId: "DelAutoUpdate"
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""AppleEsports Restart On Kill"""; Flags: runhidden; RunOnceId: "DelRestartOnKill"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AppleEsports"
Type: filesandordirs; Name: "{userappdata}\AppleEsports"
; NOTE: {commonappdata}\Apple Esports is deliberately NOT listed. That folder now holds
; the branch's database - its takings, sessions and members. Uninstalling the software
; must never destroy the business records. Retiring a machine for good is a decision
; someone makes deliberately, not a side effect of clicking Uninstall.
; UNINSTALL-EVERYTHING.ps1 removes it when a genuinely clean slate is wanted.

[Code]

{ Whether this machine is already an operator counter PC running the branch.

  Decided from what is actually on disk and registered with Windows, never from what the
  installer was told. This is what makes a silent update correct.

  The fault it fixes: an update is launched with /VERYSILENT and no /TYPE or /COMPONENTS,
  because the updater has no way of knowing what the machine chose when it was first set up.
  Inno then falls back to the default type, "server" is not selected, and every file tagged
  Components: server is skipped - the entire API and PostgreSQL. The dashboard window has no
  component restriction, so THAT gets replaced.

  The result is the worst kind of half-update: a new front end talking to an old engine, with
  the version on screen reporting the new number. Seen on a real branch - the window said 2.2.2
  while the API was still the previous build, and nothing anywhere said the update had only
  partly happened. Worse, the script that restarts the services is guarded by the same check, so
  an update could stop the branch and never start it again.

  A machine that is already running the branch is always updated as one. }
function IsExistingServerInstall(): Boolean;
begin
  Result :=
    DirExists(ExpandConstant('{app}\api')) or
    DirExists(ExpandConstant('{app}\pgsql')) or
    RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\AppleEsportsApi') or
    RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\AppleEsportsDb');
end;

{ Likewise for a gaming PC: the screen-lock agent being present says what this machine is. }
function IsExistingAgentInstall(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\agent\AppleEsportsAgent.exe'));
end;

{ Used by Check: on every server file, so they are copied when the component is selected OR
  when this machine plainly already is a branch. Belt and braces: the component selection is
  still honoured on a fresh install, where there is nothing on disk to detect. }
function InstallServerParts(): Boolean;
begin
  Result := IsComponentSelected('server') or IsExistingServerInstall();
end;

function InstallAgentParts(): Boolean;
begin
  Result := IsComponentSelected('agent') or IsExistingAgentInstall();
end;

function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

{ Runs before a single file is copied.

  On an upgrade the API is running as a service with its own DLLs open, so the copy fails
  with "DeleteFile failed; code 5" partway through and leaves the install half replaced.
  Stopping first is the difference between an upgrade over a working branch succeeding and
  needing a full uninstall. setup-api.ps1 starts everything again at the end. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  { Also true when this machine already runs the branch, so a silent update given no
    component flags still restarts the services it has just stopped. }
  if not InstallServerParts() then
    Exit;

  ExtractTemporaryFile('stop-services.ps1');

  { Deliberately not treated as fatal. If something cannot be stopped, the copy hits the
    same lock and Inno offers Retry/Skip/Cancel - a better place to decide than here, with
    the file that is actually stuck named on screen. }
  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\stop-services.ps1') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

  if not WebView2Installed() then
  begin
    if MsgBox(
      'Apple Esports needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.' + #13#10#13#10 +
      'It is a free Microsoft component and takes about a minute to install.' + #13#10#13#10 +
      'Open the download page now? (Choose No to carry on installing anyway.)',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/#download', '', '', SW_SHOW, ewNoWait, ErrorCode);
      Result := False;
    end;
  end;
end;

{ Runs a setup script and returns its exit code, so a failure can be reported rather
  than swallowed. }
function RunSetupScript(const ScriptName, Description: String): Boolean;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := Description;

  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\') + ScriptName + '"' +
    ' -InstallDir "' + ExpandConstant('{app}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if Result and (ResultCode <> 0) then
    Result := False;
end;

{ Writes the client's settings so the app knows, before anyone opens it, what kind of PC
  this is and where its branch server is.

  This is what makes the shop work with the internet off. The address below is the BRANCH -
  this machine on the counter PC, the counter PC on a gaming PC - never Head Office. Head
  Office is reached only by the branch's own background sync. }
procedure WriteClientConfig();
var
  Path, Server, Role: String;
  Lines: TArrayOfString;
begin
  Path := ExpandConstant('{app}\AppleEsports.config.json');

  { Never overwritten on an upgrade or repair: it carries the admin PIN and the seat this
    machine was claimed as, and losing those would un-configure a working PC. }
  if FileExists(Path) then
    Exit;

  if IsComponentSelected('server') then
  begin
    { The branch database and API are installed on this machine, so there is nothing to
      ask - the answer cannot be anything else. }
    { 127.0.0.1, not localhost: localhost resolves to IPv6 first and anything else
      holding that port answers instead of the branch. }
    Server := 'http://127.0.0.1:5016';
    Role := 'operator';
  end
  else
  begin
    { Only someone standing in the shop knows the counter PC's address, so it is left empty
      and the first-run wizard asks. A guess here would be wrong at three branches in four. }
    Server := '';
    Role := 'user';
  end;

  SetArrayLength(Lines, 5);
  Lines[0] := '{';
  Lines[1] := '  "ServerUrl": "' + Server + '",';
  Lines[2] := '  "Role": "' + Role + '",';
  Lines[3] := '  "StartMaximized": true';
  Lines[4] := '}';

  SaveStringsToFile(Path, Lines, False);
end;

{ Starts the branch's two services, whatever else has happened.

  PrepareToInstall stops them both before a single file is copied, so every path out of the
  post-install step has to leave them running again. A setup script that fails is a problem;
  a setup script that fails and also leaves the shop switched off is a much larger one, and
  the operator has no way of telling the two apart.

  Safe to call when they are already running - sc.exe simply reports so and changes nothing. }
procedure EnsureServicesRunning();
var
  ResultCode, Port, Waited: Integer;
  PortFile, PgIsReady: String;
  PortStr: AnsiString;
begin
  Exec('sc.exe', 'start AppleEsportsDb', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { "sc start" returns the moment Windows reports the service running - not once Postgres has
    actually finished its own recovery and is accepting connections. Starting the API right
    behind it left a real gap: a request landing in that window got a database refusing the
    connection, surfaced to whoever was standing at the counter as a bare "Internal server
    error" with no way to know it would resolve itself in a few seconds. setup-database.ps1
    already solved this correctly for a fresh install with a pg_isready poll; this is the same
    fix for every update and repair after that, which never had it. }
  Port := 5433;
  PortFile := ExpandConstant('{commonappdata}\Apple Esports\db.port');
  if FileExists(PortFile) and LoadStringFromFile(PortFile, PortStr) then
    Port := StrToIntDef(Trim(PortStr), Port);

  PgIsReady := ExpandConstant('{app}\pgsql\bin\pg_isready.exe');
  if FileExists(PgIsReady) then
  begin
    Waited := 0;
    while (Waited < 30) do
    begin
      Exec(PgIsReady, '-h localhost -p ' + IntToStr(Port) + ' -q', '', SW_HIDE,
           ewWaitUntilTerminated, ResultCode);
      if ResultCode = 0 then
        Break;
      Sleep(1000);
      Waited := Waited + 1;
    end;
  end;

  Exec('sc.exe', 'start AppleEsportsApi', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  { Both kinds of PC need this, so it runs before the operator-only work below. }
  WriteClientConfig();

  (* Cleans up the pre-3.1.23 location. The agent exe used to sit directly in the install folder;
     it now lives one level down, in its own "agent" subfolder, so the Dirs section's
     users-modify permission can be scoped to just that one subfolder instead of loosening the
     whole install directory a customer's own account can reach. Best-effort: if the old copy is
     still running as a machine's only way of showing a lock screen, deleting it fails harmlessly
     and it is cleaned up on the next update instead. *)
  if FileExists(ExpandConstant('{app}\AppleEsportsAgent.exe')) then
    DeleteFile(ExpandConstant('{app}\AppleEsportsAgent.exe'));

  { The guard that took a real branch offline.

    PrepareToInstall stops both services using InstallServerParts(), which is true whenever
    this machine already runs the branch. This line used IsComponentSelected('server') on its
    own - and that is FALSE during a silent update, because the updater passes no /COMPONENTS
    and Inno falls back to the default type.

    So a silent update stopped the branch, replaced its files, and then returned here and gave
    up before running the scripts that start it again. Both services left stopped, and a
    dashboard showing "Cannot reach the branch system" with a list of things to check, none of
    which were wrong. A shop taking an update overnight would have opened to a dead counter.

    Whatever decides to stop the branch and whatever decides to start it again must be the
    same decision. That is why this is InstallServerParts() and not a second, similar test. }
  if not InstallServerParts() then
    Exit;

  if not RunSetupScript('setup-database.ps1', 'Setting up the branch database (this takes a minute)...') then
  begin
    { Started again before reporting anything. Under /SUPPRESSMSGBOXES - which every silent
      update uses - this message is answered for us and nobody ever sees it, so leaving
      without this call is how a failure here becomes a shop that simply never comes back. }
    EnsureServicesRunning();

    { Guarded for the same reason as the success message below, and it matters more here. A box
      drawn in session 0 waits for a click that cannot come, so a FAILED silent update would hang
      for ever and take every future update down with it - Inno will not start a second install
      while this one is still sitting there. The log is where a silent failure gets reported; a
      message box is only for somebody who is actually looking at the screen. }
    if not WizardSilent then
      MsgBox(
        'The branch database could not be set up.' + #13#10#13#10 +
        'The rest of the software installed, but this PC cannot run the branch until the '
        + 'database is working.' + #13#10#13#10 +
        'What went wrong was written to:' + #13#10 +
        ExpandConstant('{commonappdata}\Apple Esports\logs\setup-database.log'),
        mbCriticalError, MB_OK);
    Exit;
  end;

  if not RunSetupScript('setup-api.ps1', 'Starting the branch system...') then
  begin
    EnsureServicesRunning();

    if not WizardSilent then
      MsgBox(
        'The database is ready, but the branch system did not start.' + #13#10#13#10 +
        'What went wrong was written to:' + #13#10 +
        ExpandConstant('{commonappdata}\Apple Esports\logs\setup-api.log'),
        mbCriticalError, MB_OK);
    Exit;
  end;

  { Belt and braces on the ordinary path too. setup-api.ps1 reporting success and the service
    actually being up are not quite the same claim, and this costs nothing to be sure of. }
  EnsureServicesRunning();

  { Only when a person is actually there to read it.

    This message stopped a branch updating for an hour. An update launched by the branch's own
    Windows service runs in session 0, which has no desktop - so this box was drawn where nobody
    could ever see it, let alone click OK, and Setup sat waiting on it. The install itself had
    already succeeded; the process simply never exited. Inno refuses to start a second install
    while one is running, so every later update downloaded, launched, and was turned away, and the
    command that launched it cheerfully reported "verified and launched".

    The comment further up this file assumed /SUPPRESSMSGBOXES answers these for us. It does not
    do so reliably for MsgBox called from [Code], and the whole failure rests on that assumption
    being believed rather than checked. WizardSilent is the actual question - is anybody looking -
    so ask that instead of trusting a flag to have handled it. }
  if not WizardSilent then
    MsgBox(
      'This PC now runs the branch itself.' + #13#10#13#10 +
      'The database and dashboard start automatically with Windows, so the shop works ' +
      'even with no internet at all. The internet is only used to report to Head Office ' +
      'and to receive updates.' + #13#10#13#10 +
      'Point the gaming PCs at this machine when you set them up.',
      mbInformation, MB_OK);
end;

{ The last thing that runs, whatever happened.

  This is the one that would have saved a real branch. An update stopped both services in
  PrepareToInstall, began copying, failed partway, and Inno rolled the files back - so setup
  ended without ever reaching ssPostInstall. Every restart-the-services path lives in
  CurStepChanged, none of it ran, and the counter PC was left with the old version installed
  and both services stopped. Sixteen minutes later somebody had to start them by hand.

  Rolling the files back is right. Leaving the shop switched off is not, and a failed upgrade
  must always end with the branch running whatever version it had before - that version worked
  this morning and is worth infinitely more than a tidy exit.

  DeinitializeSetup runs on success, on failure and on cancel alike, which is exactly the
  guarantee needed here. On a gaming PC or a fresh install the services do not exist and
  sc.exe simply reports so. }
procedure DeinitializeSetup();
begin
  if IsExistingServerInstall() then
    EnsureServicesRunning();
end;
