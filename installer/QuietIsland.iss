#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the self-contained publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{A7F2A8AF-9A67-4759-9B84-B0B3ECA08737}
AppName=QuietIsland
AppVersion={#AppVersion}
AppPublisher=Shohruh04
AppPublisherURL=https://github.com/Shohruh04/QuietIsland
AppSupportURL=https://github.com/Shohruh04/QuietIsland/issues
AppUpdatesURL=https://github.com/Shohruh04/QuietIsland/releases
DefaultDirName={localappdata}\Programs\QuietIsland
DefaultGroupName=QuietIsland
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousTasks=yes
UninstallDisplayIcon={app}\QuietIsland.exe
SetupIconFile=..\Assets\QuietIsland.ico
OutputDir={#OutputDir}
OutputBaseFilename=QuietIsland-Setup-{#AppVersion}-win-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.StartupTask=Start QuietIsland when I sign in
russian.StartupTask=Запускать QuietIsland при входе в Windows
english.LaunchApp=Launch QuietIsland
russian.LaunchApp=Запустить QuietIsland
english.CloseError=Close QuietIsland and try again.
russian.CloseError=Закройте QuietIsland и повторите попытку.

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\QuietIsland"; Filename: "{app}\QuietIsland.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Quiet Island"; Filename: "{app}\QuietIsland.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuietIsland"; ValueData: """{app}\QuietIsland.exe"" --startup"; Tasks: startup

[Run]
Filename: "{app}\QuietIsland.exe"; Parameters: "--startup"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\QuietIsland.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopQuietIsland"; AfterInstall: WaitForShutdown

[Code]
procedure WaitForShutdown;
begin
  Sleep(500);
end;

procedure RemoveOwnedStartup;
var
  Value: String;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\QuietIsland.exe');
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'QuietIsland', Value) then
    if (CompareText(Value, '"' + Exe + '" --startup') = 0) or
       (CompareText(Value, '"' + Exe + '"') = 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'QuietIsland');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('startup')) then
    RemoveOwnedStartup;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\QuietIsland.exe');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--exit', ExpandConstant('{app}'), SW_HIDE,
      ewWaitUntilTerminated, ExitCode) then
      Result := CustomMessage('CloseError');
    { The pipe acknowledges before the main instance finishes disposing. }
    Sleep(500);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Also remove startup enabled later in the application's tray menu. }
    RemoveOwnedStartup;
  end;
end;
