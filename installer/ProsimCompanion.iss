; ProsimCompanion installer (Inno Setup 6, roadmap Phase 7).
;
; Build with installer\build-installer.ps1 — it publishes the app, VERIFIES ProSimSDK.dll is
; not in the payload (the SDK is ProSim-AR's property and is never redistributed; the app
; compiles against it with Private=false and loads it at runtime from the user's ProSim
; installation), reads the version from Directory.Build.props and invokes ISCC with
; /DAppVersion=... /DPublishDir=...
;
; What this installer does beyond a plain file copy:
;   * NEVER overwrites an existing {app}\config\settings.json — the template ships
;     onlyifdoesntexist, and updates skip the path prompts entirely (both paths are already
;     configured there and editable on the web Settings page)
;   * fresh installs prompt for the ProSimSDK.dll location (auto-detected where possible) and
;     write it into {app}\config\settings.json (prosim.sdkPath)
;   * fresh installs prompt for the VoiceMeeter Remote DLL (optional) -> audio.voiceMeeterDllPath
;   * prompts for the Virtuali directory and installs the GSX aircraft profiles (gsx.cfg for
;     the three ProSim A322 SimObject folders) into <Virtuali>\Airplanes — existing profiles
;     are kept unless the overwrite box is ticked (Prosim2GSX semantics). gsx_handler.py (the
;     in-sim event bridge + VDGS display) is copied into every profile directory and ALWAYS
;     refreshed — it is an app-owned runtime file, and the app serves its /api/gsxmenu
;     endpoints and keeps the script's port line in sync at startup.
;   * every path is optional — the app degrades the subsystem with guidance when unset.
;   * optional task (shown only when the Elgato Stream Deck app is detected): installs the
;     ProsimCompanion Stream Deck plugin into %APPDATA%\Elgato\StreamDeck\Plugins, restarting
;     the Stream Deck app when it was running so the new/updated plugin is picked up. The
;     packed .streamDeckPlugin also ships to {app}\streamdeck for manual installs (e.g. when
;     Stream Deck is installed after ProsimCompanion). Compile with /DNoStreamDeck (build
;     script: -SkipStreamDeck) to omit the plugin from the payload entirely.
;
; Uninstall removes {app} plus the Stream Deck plugin (app-owned, dead without the app).
; Virtuali profiles (sim-side config the user may have edited) and
; %LOCALAPPDATA%\ProsimCompanion (logs, sessions, logbook, tech log) are left in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\ProsimCompanion.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

#define AppName "ProsimCompanion"
#define AppPublisher "Protovision"
#define AppUrl "https://github.com/psyraxaus/ProsimCompanion"

[Setup]
AppId={{7B2F0C31-9A4E-4D2B-B7E4-5A31C6E0F5D2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
; Per-user install, no elevation — matches the predecessors' "never run as admin" stance.
PrivilegesRequired=lowest
OutputBaseFilename=ProsimCompanion-Setup-{#AppVersion}
OutputDir=Output
SetupIconFile=..\src\ProsimCompanion.App\Assets\ProsimCompanion.ico
UninstallDisplayIcon={app}\ProsimCompanion.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifndef NoStreamDeck
; Only offered when the Elgato Stream Deck app is present on this machine. The plugin talks
; to the app's loopback web server, so the sim PC is where it belongs.
Name: "streamdeckplugin"; Description: "Install the ProsimCompanion Stream Deck plugin (restarts the Stream Deck app)"; \
    GroupDescription: "Stream Deck:"; Check: StreamDeckDetected
#endif

[Files]
; The published application. ProSimSDK.dll is excluded belt-and-braces — the build script has
; already failed the build if it ever appears in the publish folder. settings.json is excluded
; from the bulk copy because the publish payload carries the template: letting ignoreversion
; copy it would clobber the user's entire configuration (access token, GSX tuning, everything)
; on every update. It is installed separately below, fresh installs only.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "ProSimSDK.dll,\config\settings.json"; \
    Flags: recursesubdirs createallsubdirs ignoreversion
; The settings template, FRESH INSTALLS ONLY (onlyifdoesntexist): on update the user's
; settings.json is never touched by [Files]; WriteSettings() surgically edits just the two
; installer-owned path keys. SettingsDefaultsWriter fills every other default on first start.
Source: "{#PublishDir}\config\settings.json"; DestDir: "{app}\config"; Flags: onlyifdoesntexist
; GSX aircraft profiles are extracted to temp and copied by [Code] so the keep-vs-overwrite
; choice can be honoured per profile.
Source: "GSXProfiles\prosim-a322-cfm\gsx.cfg"; DestDir: "{tmp}\GSXProfiles\prosim-a322-cfm"; Flags: dontcopy
Source: "GSXProfiles\prosim-a322-iae\gsx.cfg"; DestDir: "{tmp}\GSXProfiles\prosim-a322-iae"; Flags: dontcopy
Source: "GSXProfiles\Prosim-a322-neo\gsx.cfg"; DestDir: "{tmp}\GSXProfiles\Prosim-a322-neo"; Flags: dontcopy
; The in-sim handler script (event bridge + VDGS). One shared source; copied into every
; profile directory and ALWAYS refreshed on update regardless of the overwrite choice —
; it is a ProsimCompanion-owned runtime file, not user-editable sim config (predecessor
; rule). The app rewrites its port line at startup when the web port differs.
Source: "GSXProfiles\gsx_handler.py"; DestDir: "{tmp}\GSXProfiles"; Flags: dontcopy
#ifndef NoStreamDeck
; Stream Deck plugin, pre-built by build-installer.ps1 (which fails if bin\plugin.js is
; missing). Installed straight into the Elgato plugins folder — always refreshed on update
; (app-owned runtime files, same rule as gsx_handler.py). StopStreamDeckApp runs before the
; copy so no plugin file is locked by a running Stream Deck app.
Source: "..\streamdeck\com.prosimcompanion.streamdeck.sdPlugin\*"; \
    DestDir: "{userappdata}\Elgato\StreamDeck\Plugins\com.prosimcompanion.streamdeck.sdPlugin"; \
    Tasks: streamdeckplugin; BeforeInstall: StopStreamDeckApp; \
    Flags: recursesubdirs createallsubdirs ignoreversion
; The packed plugin always ships beside the app so it can be installed manually later
; (double-click) — e.g. when the Stream Deck app was not present at install time.
Source: "..\streamdeck\dist\com.prosimcompanion.streamdeck.streamDeckPlugin"; \
    DestDir: "{app}\streamdeck"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ProsimCompanion.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ProsimCompanion.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ProsimCompanion.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The Stream Deck plugin is app-owned and useless without the app. Deleted even on builds
; compiled with /DNoStreamDeck — a previous installer version may have put it there.
Type: filesandordirs; Name: "{userappdata}\Elgato\StreamDeck\Plugins\com.prosimcompanion.streamdeck.sdPlugin"

[Code]
var
  SdkPage: TInputFileWizardPage;
  VoiceMeeterPage: TInputFileWizardPage;
  VirtualiPage: TInputDirWizardPage;
  ProfilesCheckPage: TInputOptionWizardPage;
  StreamDeckStopAttempted: Boolean;
  StreamDeckWasRunning: Boolean;

{ ---- auto-detection --------------------------------------------------------------------- }

function DetectProSimSdk(): String;
var
  Candidates: array[0..4] of String;
  I: Integer;
begin
  Result := '';
  Candidates[0] := 'C:\prosim\prosim-system\ProSimSDK.dll';
  Candidates[1] := 'C:\ProSim-AR\ProSimSDK.dll';
  Candidates[2] := 'C:\ProSim\ProSimSDK.dll';
  Candidates[3] := ExpandConstant('{commonpf64}\ProSim-AR\ProSimSDK.dll');
  Candidates[4] := ExpandConstant('{commonpf32}\ProSim-AR\ProSimSDK.dll');
  for I := 0 to 4 do
    if FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      exit;
    end;
end;

{ True when the Elgato Stream Deck app has ever run for this user (its profile dir exists) —
  gates the plugin task so it is only offered where it can do something. }
function StreamDeckDetected(): Boolean;
begin
  Result := DirExists(ExpandConstant('{userappdata}\Elgato\StreamDeck'));
end;

function FindStreamDeckExe(): String;
var
  Candidates: array[0..1] of String;
  I: Integer;
begin
  Result := '';
  Candidates[0] := ExpandConstant('{commonpf64}\Elgato\StreamDeck\StreamDeck.exe');
  Candidates[1] := ExpandConstant('{commonpf32}\Elgato\StreamDeck\StreamDeck.exe');
  for I := 0 to 1 do
    if FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      exit;
    end;
end;

{ BeforeInstall on the plugin [Files] entry — Inno calls it once per file, hence the
  once-guard. Stopping the Stream Deck app releases any lock on the previous plugin version;
  taskkill needs no elevation because both installer and Stream Deck run as the same user.
  Exit code 0 = a process was killed (so it was running and we should restart it afterwards);
  128 = no such process. }
procedure StopStreamDeckApp();
var
  ResultCode: Integer;
begin
  if StreamDeckStopAttempted then
    exit;
  StreamDeckStopAttempted := True;
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StreamDeck.exe', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    StreamDeckWasRunning := (ResultCode = 0);
  if StreamDeckWasRunning then
  begin
    Log('Stream Deck app stopped for plugin install; will restart after.');
    Sleep(1500); { let child plugin processes exit and release file handles }
  end;
end;

procedure RestartStreamDeckApp();
var
  Exe: String;
  ResultCode: Integer;
begin
  if not StreamDeckWasRunning then
    exit;
  Exe := FindStreamDeckExe();
  if Exe <> '' then
    Exec(Exe, '', '', SW_SHOWMINNOACTIVE, ewNoWait, ResultCode)
  else
    Log('Stream Deck app was stopped but StreamDeck.exe was not found to restart it.');
end;

function DetectVoiceMeeter(): String;
begin
  Result := '';
  if FileExists('C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll') then
    Result := 'C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll'
  else if FileExists('C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll') then
    Result := 'C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll';
end;

{ ---- wizard pages ----------------------------------------------------------------------- }

procedure InitializeWizard();
begin
  SdkPage := CreateInputFilePage(wpSelectDir,
    'ProSim SDK location',
    'Where is ProSimSDK.dll?',
    'ProsimCompanion talks to ProSim through its SDK, which ships with your ProSim ' +
    'installation and is never redistributed. Leave empty to configure later on the web ' +
    'Settings page — the ProSim connection stays disabled until then.');
  SdkPage.Add('ProSimSDK.dll:',
    'ProSim SDK (ProSimSDK.dll)|ProSimSDK.dll|DLL files (*.dll)|*.dll|All files (*.*)|*.*',
    '.dll');
  SdkPage.Values[0] := DetectProSimSdk();

  VoiceMeeterPage := CreateInputFilePage(SdkPage.ID,
    'VoiceMeeter (optional)',
    'Where is VoicemeeterRemote64.dll?',
    'Only needed when the audio pillar should drive VoiceMeeter strips/buses instead of ' +
    'Windows per-app volumes. Leave empty to skip (auto-detection also runs at app start).');
  VoiceMeeterPage.Add('VoicemeeterRemote64.dll:',
    'VoiceMeeter Remote (VoicemeeterRemote64.dll)|VoicemeeterRemote64.dll|DLL files (*.dll)|*.dll',
    '.dll');
  VoiceMeeterPage.Values[0] := DetectVoiceMeeter();

  VirtualiPage := CreateInputDirPage(VoiceMeeterPage.ID,
    'GSX (Virtuali) directory',
    'Where is your GSX Virtuali folder?',
    'The GSX aircraft profiles for the ProSim A322 (door positions, service points, the ' +
    'door LVAR bindings GSX needs) are installed into <Virtuali>\Airplanes.',
    False, '');
  VirtualiPage.Add('Virtuali directory:');
  VirtualiPage.Values[0] := ExpandConstant('{userappdata}\Virtuali');

  ProfilesCheckPage := CreateInputOptionPage(VirtualiPage.ID,
    'GSX aircraft profiles',
    'Install the ProSim A322 GSX profiles?',
    'Profiles for prosim-a322-cfm, prosim-a322-iae and Prosim-a322-neo. Existing profiles ' +
    'are kept unless you tick the overwrite option (your own gsx.cfg edits survive updates).',
    False, False);
  ProfilesCheckPage.Add('Install GSX aircraft profiles');
  ProfilesCheckPage.Add('Overwrite existing profiles');
  ProfilesCheckPage.Values[0] := True;
  ProfilesCheckPage.Values[1] := False;
end;

{ Update installs skip the path prompts entirely: an existing settings.json already carries
  (or deliberately omits) sdkPath / voiceMeeterDllPath, and both are editable any time on the
  web Settings page. The page values are cleared so WriteSettings leaves the existing keys
  untouched — otherwise the auto-detected defaults would silently overwrite the user's
  configuration. WizardDirValue is final here: both pages sit after wpSelectDir. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = SdkPage.ID) or (PageID = VoiceMeeterPage.ID) then
    if FileExists(AddBackslash(WizardDirValue) + 'config\settings.json') then
    begin
      Result := True;
      if PageID = SdkPage.ID then
        SdkPage.Values[0] := '';
      if PageID = VoiceMeeterPage.ID then
        VoiceMeeterPage.Values[0] := '';
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = SdkPage.ID) and (SdkPage.Values[0] <> '') then
  begin
    if not FileExists(SdkPage.Values[0]) then
    begin
      MsgBox('That file does not exist. Pick the real ProSimSDK.dll or leave the field empty.',
        mbError, MB_OK);
      Result := False;
    end
    else if CompareText(ExtractFileName(SdkPage.Values[0]), 'ProSimSDK.dll') <> 0 then
    begin
      MsgBox('Expected a file named ProSimSDK.dll.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ ---- settings.json ---------------------------------------------------------------------- }

function JsonEscape(const Value: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(Value) do
    if Value[I] = '\' then
      Result := Result + '\\'
    else if Value[I] = '"' then
      Result := Result + '\"'
    else
      Result := Result + Value[I];
end;

{ Replace the value of "key": "..." in the file text; returns False when the key is absent.
  Good enough for the two flat keys the installer owns — the app itself is the real editor. }
function TryReplaceJsonString(var Text: String; const Key, Value: String): Boolean;
var
  KeyToken, Rest: String;
  KeyPos, ValueStart, ValueEnd: Integer;
begin
  Result := False;
  KeyToken := '"' + Key + '"';
  KeyPos := Pos(KeyToken, Text);
  if KeyPos = 0 then
    exit;
  Rest := Copy(Text, KeyPos + Length(KeyToken), MaxInt);
  ValueStart := Pos('"', Rest);
  if ValueStart = 0 then
    exit;
  ValueEnd := ValueStart + 1;
  while (ValueEnd <= Length(Rest)) and
        not ((Rest[ValueEnd] = '"') and (Rest[ValueEnd - 1] <> '\')) do
    ValueEnd := ValueEnd + 1;
  if ValueEnd > Length(Rest) then
    exit;
  Text := Copy(Text, 1, KeyPos + Length(KeyToken) - 1)
    + Copy(Rest, 1, ValueStart) + JsonEscape(Value) + Copy(Rest, ValueEnd, MaxInt);
  Result := True;
end;

procedure WriteSettings();
var
  SettingsPath, Text: String;
  Raw: AnsiString;
  Lines: TStringList;
  Changed: Boolean;
begin
  SettingsPath := ExpandConstant('{app}\config\settings.json');
  ForceDirectories(ExtractFileDir(SettingsPath));

  { LoadStringFromFile is AnsiString-only; the file is the app's own camelCase JSON (paths,
    tokens — effectively ASCII), so the boundary conversion is safe here. }
  if LoadStringFromFile(SettingsPath, Raw) then
  begin
    { Existing file (update, or the shipped template): surgically update the two keys the
      installer owns; anything else stays exactly as the user configured it. }
    Text := String(Raw);
    Changed := False;
    if SdkPage.Values[0] <> '' then
      Changed := TryReplaceJsonString(Text, 'sdkPath', SdkPage.Values[0]) or Changed;
    if VoiceMeeterPage.Values[0] <> '' then
      Changed := TryReplaceJsonString(Text, 'voiceMeeterDllPath', VoiceMeeterPage.Values[0]) or Changed;
    if Changed then
      SaveStringToFile(SettingsPath, AnsiString(Text), False);
    { A key the replace missed (fresh sections) is fine: the app's Settings page can set it,
      and the SDK path is also importable from a predecessor config on first run. }
    exit;
  end;

  { No settings.json at all: write the minimal file; the app fills in every other default
    on first start (SettingsDefaultsWriter). }
  Lines := TStringList.Create;
  try
    Lines.Add('{');
    Lines.Add('  "prosim": {');
    Lines.Add('    "sdkPath": "' + JsonEscape(SdkPage.Values[0]) + '"');
    Lines.Add('  },');
    Lines.Add('  "audio": {');
    Lines.Add('    "voiceMeeterDllPath": "' + JsonEscape(VoiceMeeterPage.Values[0]) + '"');
    Lines.Add('  }');
    Lines.Add('}');
    Lines.SaveToFile(SettingsPath);
  finally
    Lines.Free;
  end;
end;

{ ---- GSX profiles ----------------------------------------------------------------------- }

procedure InstallGsxProfile(const ProfileName: String; var Copied, Skipped: Integer);
var
  TargetDir, TargetFile: String;
begin
  TargetDir := AddBackslash(VirtualiPage.Values[0]) + 'Airplanes\' + ProfileName;
  TargetFile := TargetDir + '\gsx.cfg';
  if FileExists(TargetFile) and not ProfilesCheckPage.Values[1] then
  begin
    Skipped := Skipped + 1;
    exit;
  end;
  ForceDirectories(TargetDir);
  (* The whole GSXProfiles tree was staged to the temp dir by InstallGsxProfiles. *)
  if CopyFile(ExpandConstant('{tmp}\GSXProfiles\' + ProfileName + '\gsx.cfg'), TargetFile, False) then
    Copied := Copied + 1;
end;

{ The handler is refreshed into EVERY profile dir that exists — even when the user chose to
  keep their gsx.cfg edits. It is a ProsimCompanion-owned runtime file and must track the
  app version (predecessor WorkerGSXHandler rule). }
procedure InstallGsxHandler(const ProfileName: String);
var
  TargetDir: String;
begin
  TargetDir := AddBackslash(VirtualiPage.Values[0]) + 'Airplanes\' + ProfileName;
  if DirExists(TargetDir) then
    CopyFile(ExpandConstant('{tmp}\GSXProfiles\gsx_handler.py'), TargetDir + '\gsx_handler.py', False);
end;

procedure InstallGsxProfiles();
var
  Copied, Skipped: Integer;
begin
  ExtractTemporaryFiles('{tmp}\GSXProfiles\*');
  if ProfilesCheckPage.Values[0] then
  begin
    Copied := 0;
    Skipped := 0;
    InstallGsxProfile('prosim-a322-cfm', Copied, Skipped);
    InstallGsxProfile('prosim-a322-iae', Copied, Skipped);
    InstallGsxProfile('Prosim-a322-neo', Copied, Skipped);
    Log(Format('GSX profiles: %d copied, %d kept (already present)', [Copied, Skipped]));
  end;
  InstallGsxHandler('prosim-a322-cfm');
  InstallGsxHandler('prosim-a322-iae');
  InstallGsxHandler('Prosim-a322-neo');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteSettings();
    InstallGsxProfiles();
    RestartStreamDeckApp();
  end;
end;
