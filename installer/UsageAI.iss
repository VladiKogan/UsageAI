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
#define MyAppPublisher "VladiKogan"
#define MyAppURL "https://github.com/VladiKogan/UsageAI"
#define MyAppExeName "UsageAI.exe"

[Setup]
AppId={{B1C6D9E4-6F5B-4E7B-9C2A-6E7B7F6B9B1D}
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
PrivilegesRequired=admin
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
