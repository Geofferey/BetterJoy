#define MyAppName "BetterJoy"
#define MyAppVersion "v7.2.1"
#define MyAppPublisher "BetterJoy Contributors"
#define MyAppURL "https://github.com/Geofferey/BetterJoy"
#define MyAppExeName "BetterJoyForCemu.exe"
#define MyBuildDir "..\BetterJoyForCemu\bin\x64\Release"
#define MyViGEmBusInstaller "ViGEmBus_1.22.0_x64_x86_arm64.exe"
#define MyHidHideInstaller "HidHide_1.5.230_x64.exe"

[Setup]
; Same GUID as the project's ProjectGuid, so upgrades are detected correctly across releases.
AppId={{1BF709E9-C133-41DF-933A-C9FF3F664C7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=BetterJoy-Setup-{#MyAppVersion}
SetupIconFile=..\BetterJoyForCemu\Icons\betterjoyforcemu_icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "vigembus"; Description: "Install the ViGEmBus driver (required for XInput/DS4 output)"; GroupDescription: "Drivers:"; Flags: checkedonce
Name: "hidhide"; Description: "Install the HidHide driver (hides controllers from other programs, e.g. Steam)"; GroupDescription: "Drivers:"; Flags: unchecked
Name: "service"; Description: "Run BetterJoy as a Windows Service (starts before login)"; GroupDescription: "Advanced:"; Flags: unchecked

[Files]
; Everything from the Release build, except runtime-generated state that shouldn't ship pre-populated
; with whatever the machine that built it happened to have connected.
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Excludes: "settings,3rdPartyControllers,! Install the drivers in the Drivers folder"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\Drivers\{#MyViGEmBusInstaller}"; Parameters: "/quiet /norestart"; StatusMsg: "Installing ViGEmBus driver..."; Tasks: vigembus; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort: if the service was never installed these just fail quietly, which is fine -
; Inno doesn't treat a non-zero exit code here as an uninstall failure.
Filename: "{sys}\sc.exe"; Parameters: "stop BetterJoy"; Flags: runhidden waituntilterminated; RunOnceId: "StopBetterJoyService"
Filename: "{sys}\sc.exe"; Parameters: "delete BetterJoy"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteBetterJoyService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// HidHide is installed via Exec here (rather than a declarative [Run] line) so its exit code can
// be inspected: a WiX Burn bootstrapper returns 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) when a
// reboot is needed, and NeedsRestart() below surfaces that as Inno's own native restart prompt
// instead of it being silently lost.
var
  HidHideExitCode: Integer;
  WasServiceRunningBeforeUpgrade: Boolean;

// Real service-status polling via the SCM API - sc.exe stop only requests the stop and returns
// as soon as the SCM acknowledges the request, not once the service has actually finished
// shutting down (BetterJoyService.OnStop does real cleanup: unhiding HidHide devices,
// disconnecting ViGEm targets, stopping timers - not instant). Waiting merely for the sc.exe
// process to exit isn't the same as waiting for the file locks it's holding to be released.
const
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;

type
  SERVICE_STATUS = record
    dwServiceType: LongWord;
    dwCurrentState: LongWord;
    dwControlsAccepted: LongWord;
    dwWin32ExitCode: LongWord;
    dwServiceSpecificExitCode: LongWord;
    dwCheckPoint: LongWord;
    dwWaitHint: LongWord;
  end;

function OpenSCManagerW(lpMachineName, lpDatabaseName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: LongWord; lpServiceName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: LongWord; var lpServiceStatus: SERVICE_STATUS): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: LongWord): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';

// Returns the service's current SERVICE_* state, or 0 if it isn't installed / can't be queried
// (never installed, access denied, etc.) - 0 isn't a real SERVICE_* value, so callers can treat
// it as "unknown/absent" without confusing it for a real state.
function GetServiceState(ServiceName: string): LongWord;
var
  SCManager, Service: LongWord;
  Status: SERVICE_STATUS;
begin
  Result := 0;
  SCManager := OpenSCManagerW('', '', SC_MANAGER_CONNECT);
  if SCManager = 0 then
    exit;
  try
    Service := OpenServiceW(SCManager, ServiceName, SERVICE_QUERY_STATUS);
    if Service = 0 then
      exit;
    try
      if QueryServiceStatus(Service, Status) then
        Result := Status.dwCurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(SCManager);
  end;
end;

procedure InstallHidHide;
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('hidhide') then begin
    if Exec(ExpandConstant('{app}\Drivers\{#MyHidHideInstaller}'), '/exenoui /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      HidHideExitCode := ResultCode
    else
      HidHideExitCode := -1;
  end;
end;

// sc.exe's binPath value has to be one single argument containing the (space-containing,
// quoted) exe path followed by " -service" - the outer quotes let the command-line parser
// treat the whole thing as one token for sc.exe, the escaped inner quotes are what sc.exe
// itself then records as the actual service binary path.
// Fresh install/re-selecting the task always (re)creates the service from scratch. An upgrade
// that *doesn't* have the task selected but had the service running before StopExistingService
// touched it just gets restarted as-is - it already exists, so there's nothing to (re)create,
// only to bring back to how it was found. A service that was already stopped (or never
// installed) before this run is left alone either way, respecting whatever the user had.
procedure InstallService;
var
  ResultCode: Integer;
  Params: String;
begin
  if WizardIsTaskSelected('service') then begin
    Params := 'create BetterJoy binPath= "\"' + ExpandConstant('{app}\{#MyAppExeName}') + '\" -service" start= auto DisplayName= "BetterJoy"';
    Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'description BetterJoy "Nintendo Switch controller service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'start BetterJoy', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end else if WasServiceRunningBeforeUpgrade then begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'start BetterJoy', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Before files are copied: an existing installed service still running keeps
// BetterJoyForCemu.exe/its DLLs locked, which fails the file-copy step outright rather than a
// clean upgrade. Records whether it was actually running first, so InstallService can restore
// that afterward regardless of whether the "service" task happens to be selected this run -
// otherwise an upgrade where that box isn't re-checked would silently leave a previously-
// running service stopped. Does nothing if the service was never installed or wasn't running.
procedure StopExistingService;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  WasServiceRunningBeforeUpgrade := (GetServiceState('BetterJoy') = SERVICE_RUNNING);
  if not WasServiceRunningBeforeUpgrade then
    exit;

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop BetterJoy', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Poll for the service to actually reach SERVICE_STOPPED, not just for the sc.exe command to
  // return - up to 10 seconds, then give up and let the file copy fail loudly if it must rather
  // than hang the installer indefinitely on a service that's stuck shutting down.
  Attempts := 0;
  while (GetServiceState('BetterJoy') <> SERVICE_STOPPED) and (Attempts < 50) do begin
    Sleep(200);
    Attempts := Attempts + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    StopExistingService;
  end;
  if CurStep = ssPostInstall then begin
    InstallHidHide;
    InstallService;
  end;
end;

function NeedsRestart(): Boolean;
begin
  Result := (HidHideExitCode = 3010);
end;
