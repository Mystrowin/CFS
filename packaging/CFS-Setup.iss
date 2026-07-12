#ifndef SourceDir
  #error SourceDir must point to the published win-x64 application folder.
#endif

#ifndef OutputDir
  #error OutputDir must point to the release output folder.
#endif

#define ProductName "CFS"
#define ProductVersion "0.1.0"
#define ProductLabel "Beta"
#define Publisher "Neeraj Pragnya Krishna Vasagiri"
#define ProductExe "Cfs.App.exe"

[Setup]
AppId={{8A9237D3-6476-4F69-AE72-58221802FA45}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#ProductVersion} {#ProductLabel}
AppPublisher={#Publisher}
VersionInfoCompany={#Publisher}
VersionInfoDescription={#ProductName} {#ProductVersion} {#ProductLabel} Setup
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#ProductVersion}
VersionInfoVersion={#ProductVersion}
DefaultDirName={autopf}\CFS
DefaultGroupName=CFS
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=CFS-0.1.0-Beta-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#ProductExe}
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "enableprojfs"; Description: "Enable the Windows Projected File System feature (recommended)"; GroupDescription: "Windows feature:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CFS"; Filename: "{app}\{#ProductExe}"
Name: "{autodesktop}\CFS"; Filename: "{app}\{#ProductExe}"; Tasks: desktopicon

; These keys deliberately survive the automatic uninstall pass. The [Code]
; section removes them only if they still point at this CFS installation.
[Registry]
Root: HKLM; Subkey: "Software\Classes\.cfs"; ValueType: string; ValueName: ""; ValueData: "CFS.Archive"
Root: HKLM; Subkey: "Software\Classes\CFS.Archive"; ValueType: string; ValueName: ""; ValueData: "CFS Archive"
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ProductExe},0"
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ProductExe}"" ""%1"""

[Code]
var
  ProjFsRestartRequired: Boolean;

function InitializeSetup(): Boolean;
var
  WindowsVersion: TWindowsVersion;
begin
  Result := False;
  if not IsWin64 then
  begin
    MsgBox('CFS 0.1.0 Beta requires 64-bit Windows.', mbError, MB_OK);
    Exit;
  end;

  GetWindowsVersionEx(WindowsVersion);
  if (WindowsVersion.Major < 10) or
     ((WindowsVersion.Major = 10) and (WindowsVersion.Build < 17763)) then
  begin
    MsgBox('CFS requires Windows 10 version 1809 (build 17763) or newer.', mbError, MB_OK);
    Exit;
  end;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('enableprojfs') then
  begin
    if not Exec(ExpandConstant('{sysnative}\dism.exe'),
      '/Online /Enable-Feature /FeatureName:Client-ProjFS /All /NoRestart',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('CFS was installed, but Windows could not start the Projected File System feature check. Enable Client-ProjFS manually before mounting archives.', mbInformation, MB_OK);
    end
    else if ResultCode = 3010 then
      ProjFsRestartRequired := True
    else if ResultCode <> 0 then
      MsgBox(Format('CFS was installed, but Client-ProjFS could not be enabled (DISM exit code %d). Enable it manually before mounting archives.', [ResultCode]), mbInformation, MB_OK);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := ProjFsRestartRequired;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExtensionOwner: String;
  OpenCommand: String;
  InstalledCommand: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  InstalledCommand := '"' + ExpandConstant('{app}\{#ProductExe}') + '" "%1"';
  if RegQueryStringValue(HKLM, 'Software\Classes\.cfs', '', ExtensionOwner) and
     (CompareText(ExtensionOwner, 'CFS.Archive') = 0) then
  begin
    RegDeleteValue(HKLM, 'Software\Classes\.cfs', '');
    RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\.cfs');
  end;

  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\shell\open\command', '', OpenCommand) and
     (CompareText(OpenCommand, InstalledCommand) = 0) then
    RegDeleteKeyIncludingSubkeys(HKLM, 'Software\Classes\CFS.Archive');
end;
