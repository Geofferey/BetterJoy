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

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// HidHide is installed via Exec here (rather than a declarative [Run] line) so its exit code can
// be inspected: a WiX Burn bootstrapper returns 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) when a
// reboot is needed, and NeedsRestart() below surfaces that as Inno's own native restart prompt
// instead of it being silently lost.
var
  HidHideExitCode: Integer;

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallHidHide;
end;

function NeedsRestart(): Boolean;
begin
  Result := (HidHideExitCode = 3010);
end;
