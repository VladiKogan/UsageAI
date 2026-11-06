; Inno Setup script for UsageAI.
;
; Builds a Setup.exe around the framework-dependent single-file publish
; output. The app itself stays framework-dependent (small download); this
; script checks for the .NET 10 Desktop Runtime at install time and installs
; it silently if missing, so the end user never has to do that manually.
;
; Build:
;   dotnet publish ..\UsageAI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\publish
;   iscc UsageAI.iss /DMyAppVersion=<version>
;
; Requires Inno Setup 6.3+ (https://jrsoftware.org/isinfo.php) for the
; x64compatible architecture identifiers used below.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "UsageAI"
; The rest can be overridden only so a throwaway copy can be installed and uninstalled in a test
; without touching a real installation, its startup entry or its settings.
#ifndef MyAppId
  #define MyAppId "{{B1C6D9E4-6F5B-4E7B-9C2A-6E7B7F6B9B1D}"
#endif
#ifndef MyPrivileges
  #define MyPrivileges "admin"
#endif
#ifndef DataFolderName
  #define DataFolderName "UsageAI"
#endif
#ifndef StartupValueName
  #define StartupValueName "UsageAI"
#endif
#define MyAppPublisher "VladiKogan"
#define MyAppURL "https://github.com/VladiKogan/UsageAI"
#define MyAppExeName "UsageAI.exe"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=.\Output
OutputBaseFilename=UsageAI-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired={#MyPrivileges}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes
CloseApplications=force
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  StartupValueName = '{#StartupValueName}';

function IsDotNetDesktopRuntimeInstalled(): Boolean;
var
  ResultCode: Integer;
  TmpFileName: string;
  Output: TStringList;
  I: Integer;
begin
  Result := False;
  TmpFileName := ExpandConstant('{tmp}\dotnet-runtimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/C dotnet --list-runtimes > "' + TmpFileName + '" 2>nul',
     '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if FileExists(TmpFileName) then
    begin
      Output := TStringList.Create;
      try
        Output.LoadFromFile(TmpFileName);
        for I := 0 to Output.Count - 1 do
        begin
          if Pos('Microsoft.WindowsDesktop.App 10.', Output[I]) = 1 then
          begin
            Result := True;
            Break;
          end;
        end;
      finally
        Output.Free;
      end;
    end;
  end;
end;

function InstallDotNetDesktopRuntime(): Boolean;
var
  ResultCode: Integer;
  InstallerPath: string;
  DownloadCommand: string;
begin
  Result := False;
  InstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  DownloadCommand := '/C powershell -NoProfile -ExecutionPolicy Bypass -Command ' +
    '"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; ' +
    'try { Invoke-WebRequest -Uri ''' + DotNetRuntimeUrl + ''' -OutFile ''' + InstallerPath + ''' -UseBasicParsing } ' +
    'catch { exit 1 }"';

  if not Exec(ExpandConstant('{cmd}'), DownloadCommand, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if ResultCode <> 0 then
    Exit;
  if not FileExists(InstallerPath) then
    Exit;

  if Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0) or (ResultCode = 3010);

  DeleteFile(InstallerPath);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsDotNetDesktopRuntimeInstalled() then
  begin
    if not InstallDotNetDesktopRuntime() then
      Result := 'The .NET 10 Desktop Runtime is required and could not be installed ' +
        'automatically (this needs an internet connection). Install it manually from ' +
        'https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.';
  end;
end;

{ "Start with Windows" is a Run value under the user's own key. Left behind, Windows would list a
  startup app whose file is gone. Only the value that starts this copy goes: a portable copy's
  entry names another path and stays. }
procedure RemoveStartupEntry();
var
  Command: string;
begin
  if RegQueryStringValue(HKCU, RunKey, StartupValueName, Command) and
     (CompareText(RemoveQuotes(Trim(Command)), ExpandConstant('{app}\{#MyAppExeName}')) = 0) then
  begin
    RegDeleteValue(HKCU, RunKey, StartupValueName);
    RegDeleteValue(HKCU, StartupApprovedKey, StartupValueName);
  end;
end;

{ "Sign in to another account" makes login folders such as %USERPROFILE%\.claude-team, and the app
  lists them in created-logins.txt. Only those are offered, by path, defaulting to keep: they hold
  logins the user's own CLI may still use. No other .claude-* or .codex-* folder is ever touched. }
procedure RemoveCreatedLoginsIfAsked();
var
  ListFile, Listed: string;
  Lines: TArrayOfString;
  Folders: TStringList;
  I: Integer;
begin
  ListFile := ExpandConstant('{localappdata}\{#DataFolderName}\created-logins.txt');
  if not FileExists(ListFile) or not LoadStringsFromFile(ListFile, Lines) then
    Exit;

  Folders := TStringList.Create;
  try
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Listed := Trim(Lines[I]);
      { A full local path below a drive root, never a root, a share or a relative path. }
      if (Length(Listed) > 3) and (Copy(Listed, 2, 2) = ':\') and DirExists(Listed) then
        Folders.Add(Listed);
    end;

    if Folders.Count = 0 then
      Exit;

    if SuppressibleMsgBox(
         'Also delete the login folders UsageAI created for your other accounts?' + #13#10#13#10 +
         Folders.Text + #13#10 +
         'The Claude Code or Codex logins saved in them are removed. Keep them if you use these accounts from a terminal.',
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      for I := 0 to Folders.Count - 1 do
        DelTree(Folders[I], True, True, True);
  finally
    Folders.Free;
  end;
end;

{ Terminal commands such as claude-team live in the data folder's bin, which the app put on the
  user's PATH. They keep working without UsageAI, so the entry goes only with the data folder, and
  every other entry is written back exactly as it was. }
const
  HKEY_CURRENT_USER_HANDLE = $80000001;
  RRF_RT_ANY = $0000FFFF;
  RRF_NOEXPAND = $10000000;
  REG_SZ_TYPE = 1;
  WM_SETTINGCHANGE = $001A;
  SMTO_ABORTIFHUNG = $0002;

function RegGetValueType(Key: LongWord; SubKey, ValueName: string; Flags: LongWord;
  var ValueType: LongWord; Data: LongWord; DataSize: LongWord): Longint;
  external 'RegGetValueW@advapi32.dll stdcall';

function SendSettingChange(Window: LongWord; Msg: LongWord; WParam: LongWord; LParam: string;
  Flags: LongWord; Timeout: LongWord; var MessageResult: LongWord): LongWord;
  external 'SendMessageTimeoutW@user32.dll stdcall';

{ True when the user's Path is a plain REG_SZ rather than the usual REG_EXPAND_SZ. }
function UserPathIsPlainString(): Boolean;
var
  ValueType: LongWord;
begin
  ValueType := 0;
  Result := (RegGetValueType(HKEY_CURRENT_USER_HANDLE, 'Environment', 'Path',
    RRF_RT_ANY or RRF_NOEXPAND, ValueType, 0, 0) = 0) and (ValueType = REG_SZ_TYPE);
end;

procedure RemoveCommandFolderFromPath(const Folder: string);
var
  Remaining, Entry, Kept: string;
  Separator: Integer;
  Found: Boolean;
  BroadcastResult: LongWord;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Remaining) then
    Exit;

  Kept := '';
  Found := False;
  Remaining := Remaining + ';';
  while Length(Remaining) > 0 do
  begin
    Separator := Pos(';', Remaining);
    Entry := Copy(Remaining, 1, Separator - 1);
    Delete(Remaining, 1, Separator);
    if CompareText(RemoveBackslashUnlessRoot(Trim(Entry)), Folder) = 0 then
      Found := True
    else
    begin
      if Kept <> '' then
        Kept := Kept + ';';
      Kept := Kept + Entry;
    end;
  end;

  if not Found then
    Exit;

  { A Path that held only this entry goes, rather than staying as an empty value; otherwise the
    value keeps its type, so a plain REG_SZ Path is not turned into REG_EXPAND_SZ. }
  if Kept = '' then
    RegDeleteValue(HKCU, 'Environment', 'Path')
  else if UserPathIsPlainString() then
    RegWriteStringValue(HKCU, 'Environment', 'Path', Kept)
  else
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Kept);

  { Explorer re-reads the environment on this broadcast, so a terminal opened next no longer
    searches the deleted folder; without it the old PATH lasted until the next sign-in. }
  SendSettingChange(HWND_BROADCAST, WM_SETTINGCHANGE, 0, 'Environment', SMTO_ABORTIFHUNG, 5000, BroadcastResult);
end;

{ Settings and recorded history stay unless the user asks, so a reinstall picks up where it left
  off. A silent uninstall that suppresses message boxes keeps them. }
procedure RemoveUserDataIfAsked();
var
  DataDir: string;
begin
  DataDir := ExpandConstant('{localappdata}\{#DataFolderName}');
  if not DirExists(DataDir) then
    Exit;

  if SuppressibleMsgBox(
       'Also delete your UsageAI settings and recorded usage history?' + #13#10#13#10 +
       'They are in ' + DataDir + '. Keep them to pick up where you left off if you install UsageAI again.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    RemoveCommandFolderFromPath(DataDir + '\bin');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveStartupEntry()
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveCreatedLoginsIfAsked();
    RemoveUserDataIfAsked();
  end;
end;
