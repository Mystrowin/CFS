#ifndef SourceDir
  #error SourceDir must point to the published win-x64 application folder.
#endif

#ifndef OutputDir
  #error OutputDir must point to the release output folder.
#endif

#define ProductName "CFS"
#define ProductVersion "0.3.0"
#define ProductLabel "Beta"
#define Publisher "Neeraj Pragnya Krishna Vasagiri"
#define ProductExe "Cfs.CommandClient.exe"
#define BrokerExe "Cfs.Broker.exe"
#define CommandClientExe "Cfs.CommandClient.exe"

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
OutputBaseFilename=CFS-0.2.0-Beta-Setup
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

; These keys deliberately survive the automatic uninstall pass. The [Code]
; section removes them only if they still point at this CFS installation.
[Registry]
Root: HKLM; Subkey: "Software\Classes\.cfs"; ValueType: string; ValueName: ""; ValueData: "CFS.Archive"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive"; ValueType: string; ValueName: ""; ValueData: "CFS Compressed Folder"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ProductExe},0"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#CommandClientExe}"" open ""%1"""; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\shell\CFS.Close"; ValueType: string; ValueName: ""; ValueData: "Close CFS"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\shell\CFS.Close"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#BrokerExe},0"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\CFS.Archive\shell\CFS.Close\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#CommandClientExe}"" close ""%1"""; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\.cfs\ShellNew"; ValueType: string; ValueName: "FileName"; ValueData: "{app}\ShellNew\CFS-Empty.cfs"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\Directory\shell\CFS.Compress"; ValueType: string; ValueName: ""; ValueData: "Compress to CFS"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\Directory\shell\CFS.Compress"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#BrokerExe},0"; Flags: uninsneveruninstall
Root: HKLM; Subkey: "Software\Classes\Directory\shell\CFS.Compress\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#CommandClientExe}"" compress ""%1"""; Flags: uninsneveruninstall

[Code]
var
  ProjFsRestartRequired: Boolean;

function TryGetLegacyCfsAppPath(var LegacyAppPath: String): Boolean;
var
  ExtensionOwner: String;
  ProgIdDescription: String;
  OpenCommand: String;
  QuotedTail: String;
  RemainingArguments: String;
  ClosingQuote: Integer;
begin
  Result := False;
  LegacyAppPath := '';

  if not RegQueryStringValue(HKCU, 'Software\Classes\.cfs', '', ExtensionOwner) or
     (CompareText(ExtensionOwner, 'CFS.Archive') <> 0) then
    Exit;

  if not RegQueryStringValue(HKCU, 'Software\Classes\CFS.Archive', '', ProgIdDescription) or
     ((CompareText(ProgIdDescription, 'CFS Archive') <> 0) and
      (CompareText(ProgIdDescription, 'CFS Compressed Folder') <> 0)) then
    Exit;

  if not RegQueryStringValue(HKCU,
    'Software\Classes\CFS.Archive\shell\open\command', '', OpenCommand) then
    Exit;

  { Accept only the exact legacy quoted command: "Cfs.App.exe" "%1". }
  if (Length(OpenCommand) < 2) or (OpenCommand[1] <> '"') then
    Exit;
  QuotedTail := Copy(OpenCommand, 2, Length(OpenCommand));
  ClosingQuote := Pos('"', QuotedTail);
  if ClosingQuote = 0 then
    Exit;
  LegacyAppPath := Copy(OpenCommand, 2, ClosingQuote - 1);
  RemainingArguments := Trim(Copy(OpenCommand, ClosingQuote + 2, Length(OpenCommand)));
  if (CompareText(ExtractFileName(LegacyAppPath), 'Cfs.App.exe') <> 0) or
     (RemainingArguments <> '"%1"') then
    Exit;

  { A same-directory Cfs.Core.dll distinguishes an installed CFS app from an
    unrelated executable that merely happens to use the same leaf name. }
  if not FileExists(LegacyAppPath) or
     not FileExists(AddBackslash(ExtractFileDir(LegacyAppPath)) + 'Cfs.Core.dll') then
    Exit;

  Result := True;
end;

function IsLegacySiblingBrokerCommand(Command: String; LegacyDirectory: String;
  Verb: String): Boolean;
var
  QuotedTail: String;
  HandlerPath: String;
  RemainingArguments: String;
  ClosingQuote: Integer;
begin
  Result := False;
  if (Length(Command) < 2) or (Command[1] <> '"') then
    Exit;
  QuotedTail := Copy(Command, 2, Length(Command));
  ClosingQuote := Pos('"', QuotedTail);
  if ClosingQuote = 0 then
    Exit;
  HandlerPath := Copy(Command, 2, ClosingQuote - 1);
  RemainingArguments := Trim(Copy(Command, ClosingQuote + 2, Length(Command)));
  if (CompareText(ExtractFileName(HandlerPath), 'Cfs.Broker.exe') <> 0) or
     (CompareText(ExtractFileDir(HandlerPath), LegacyDirectory) <> 0) or
     (RemainingArguments <> Verb + ' "%1"') or
     not FileExists(HandlerPath) then
    Exit;
  Result := True;
end;

procedure MigrateLegacyPerUserAssociation();
var
  LegacyAppPath: String;
  LegacyDirectory: String;
  LegacyBrokerPath: String;
  LegacyTemplatePath: String;
  RegisteredIcon: String;
  RegisteredCommand: String;
  RegisteredLabel: String;
  RegisteredTemplate: String;
begin
  if not TryGetLegacyCfsAppPath(LegacyAppPath) then
    Exit;

  LegacyDirectory := ExtractFileDir(LegacyAppPath);
  LegacyBrokerPath := AddBackslash(LegacyDirectory) + 'Cfs.Broker.exe';
  LegacyTemplatePath := AddBackslash(LegacyDirectory) + 'ShellNew\CFS-Empty.cfs';

  { Delete only recognized CFS-owned default values. Foreign named values and
    subkeys are deliberately retained, and keys are pruned only when empty. }
  RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive\shell\open\command', '');
  RegDeleteValue(HKCU, 'Software\Classes\.cfs', '');
  RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive', '');
  if RegQueryStringValue(HKCU, 'Software\Classes\CFS.Archive\DefaultIcon', '', RegisteredIcon) and
     ((CompareText(RegisteredIcon, LegacyAppPath + ',0') = 0) or
      (CompareText(RegisteredIcon, '"' + LegacyAppPath + '",0') = 0)) then
    RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive\DefaultIcon', '');

  if RegQueryStringValue(HKCU,
    'Software\Classes\CFS.Archive\shell\CFS.Close\command', '', RegisteredCommand) and
     IsLegacySiblingBrokerCommand(RegisteredCommand, LegacyDirectory, 'close') then
  begin
    RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close\command', '');
    if RegQueryStringValue(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close', '', RegisteredLabel) and
       (CompareText(RegisteredLabel, 'Close CFS') = 0) then
      RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close', '');
    if RegQueryStringValue(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close', 'Icon', RegisteredIcon) and
       ((CompareText(RegisteredIcon, LegacyBrokerPath + ',0') = 0) or
        (CompareText(RegisteredIcon, '"' + LegacyBrokerPath + '",0') = 0)) then
      RegDeleteValue(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close', 'Icon');
  end;

  if RegQueryStringValue(HKCU,
    'Software\Classes\Directory\shell\CFS.Compress\command', '', RegisteredCommand) and
     IsLegacySiblingBrokerCommand(RegisteredCommand, LegacyDirectory, 'compress') then
  begin
    RegDeleteValue(HKCU, 'Software\Classes\Directory\shell\CFS.Compress\command', '');
    if RegQueryStringValue(HKCU, 'Software\Classes\Directory\shell\CFS.Compress', '', RegisteredLabel) and
       (CompareText(RegisteredLabel, 'Compress to CFS') = 0) then
      RegDeleteValue(HKCU, 'Software\Classes\Directory\shell\CFS.Compress', '');
    if RegQueryStringValue(HKCU, 'Software\Classes\Directory\shell\CFS.Compress', 'Icon', RegisteredIcon) and
       ((CompareText(RegisteredIcon, LegacyBrokerPath + ',0') = 0) or
        (CompareText(RegisteredIcon, '"' + LegacyBrokerPath + '",0') = 0)) then
      RegDeleteValue(HKCU, 'Software\Classes\Directory\shell\CFS.Compress', 'Icon');
  end;

  if RegQueryStringValue(HKCU, 'Software\Classes\.cfs\ShellNew', 'FileName', RegisteredTemplate) and
     (CompareText(RegisteredTemplate, LegacyTemplatePath) = 0) and
     FileExists(LegacyTemplatePath) then
    RegDeleteValue(HKCU, 'Software\Classes\.cfs\ShellNew', 'FileName');

  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\shell\open\command');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\shell\open');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close\command');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\shell\CFS.Close');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\shell');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive\DefaultIcon');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\CFS.Archive');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\Directory\shell\CFS.Compress\command');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\Directory\shell\CFS.Compress');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\.cfs\ShellNew');
  RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\.cfs');
  Log('Migrated a verified legacy per-user CFS Cfs.App association to the installed broker handler.');
end;

function InitializeSetup(): Boolean;
var
  WindowsVersion: TWindowsVersion;
begin
  Result := False;
  if not IsWin64 then
  begin
    MsgBox('CFS 0.2.0 Beta requires 64-bit Windows.', mbError, MB_OK);
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
  if CurStep <> ssPostInstall then
    Exit;

  MigrateLegacyPerUserAssociation();
  if WizardIsTaskSelected('enableprojfs') then
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
  InstalledCompressCommand: String;
  InstalledCloseCommand: String;
  RegisteredCompressCommand: String;
  RegisteredCloseCommand: String;
  InstalledTemplate: String;
  RegisteredTemplate: String;
  RegisteredDescription: String;
  RegisteredIcon: String;
  RegisteredVerbLabel: String;
  RegisteredVerbIcon: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  InstalledCommand := '"' + ExpandConstant('{app}\{#CommandClientExe}') + '" open "%1"';
  InstalledCompressCommand := '"' + ExpandConstant('{app}\{#CommandClientExe}') + '" compress "%1"';
  InstalledCloseCommand := '"' + ExpandConstant('{app}\{#CommandClientExe}') + '" close "%1"';
  InstalledTemplate := ExpandConstant('{app}\ShellNew\CFS-Empty.cfs');
  if RegQueryStringValue(HKLM, 'Software\Classes\.cfs', '', ExtensionOwner) and
     (CompareText(ExtensionOwner, 'CFS.Archive') = 0) then
  begin
    RegDeleteValue(HKLM, 'Software\Classes\.cfs', '');
    RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\.cfs');
  end;

  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\shell\open\command', '', OpenCommand) and
     (CompareText(OpenCommand, InstalledCommand) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive\shell\open\command', '');

  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close\command', '', RegisteredCloseCommand) and
     (CompareText(RegisteredCloseCommand, InstalledCloseCommand) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close\command', '');
  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close', '', RegisteredVerbLabel) and
     (CompareText(RegisteredVerbLabel, 'Close CFS') = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close', '');
  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close', 'Icon', RegisteredVerbIcon) and
     (CompareText(RegisteredVerbIcon, ExpandConstant('{app}\{#BrokerExe},0')) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close', 'Icon');

  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive', '', RegisteredDescription) and
     (CompareText(RegisteredDescription, 'CFS Compressed Folder') = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive', '');

  if RegQueryStringValue(HKLM, 'Software\Classes\CFS.Archive\DefaultIcon', '', RegisteredIcon) and
     (CompareText(RegisteredIcon, ExpandConstant('{app}\{#ProductExe},0')) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\CFS.Archive\DefaultIcon', '');

  if RegQueryStringValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress\command', '', RegisteredCompressCommand) and
     (CompareText(RegisteredCompressCommand, InstalledCompressCommand) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress\command', '');

  if RegQueryStringValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress', '', RegisteredVerbLabel) and
     (CompareText(RegisteredVerbLabel, 'Compress to CFS') = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress', '');
  if RegQueryStringValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress', 'Icon', RegisteredVerbIcon) and
     (CompareText(RegisteredVerbIcon, ExpandConstant('{app}\{#BrokerExe},0')) = 0) then
    RegDeleteValue(HKLM, 'Software\Classes\Directory\shell\CFS.Compress', 'Icon');

  if RegQueryStringValue(HKLM, 'Software\Classes\.cfs\ShellNew', 'FileName', RegisteredTemplate) and
     (CompareText(RegisteredTemplate, InstalledTemplate) = 0) then
  begin
    RegDeleteValue(HKLM, 'Software\Classes\.cfs\ShellNew', 'FileName');
    RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\.cfs\ShellNew');
  end;
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\shell\open\command');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\shell\open');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close\command');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\shell\CFS.Close');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\shell');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive\DefaultIcon');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\CFS.Archive');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\Directory\shell\CFS.Compress\command');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\Directory\shell\CFS.Compress');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\.cfs\ShellNew');
  RegDeleteKeyIfEmpty(HKLM, 'Software\Classes\.cfs');
end;
