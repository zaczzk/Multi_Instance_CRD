; Multi_Instance_CRD — Unified Inno Setup Installer
; Builds both .NET-Included (Self-Contained) and Lightweight (.NET-Excluded) Installers
; Supports: Windows 11 on ARM64 (Qualcomm Snapdragon X) and x64 (Intel/AMD)
; Features: Seamless in-place updates, downgrade warnings, .NET 9 desktop runtime verification

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef BuildType
  ; "full" = self-contained (.NET included), "light" = framework-dependent (.NET excluded)
  #define BuildType "full"
#endif

#define AppName "Multi_Instance_CRD"
#define AppPublisher "zaczzk"
#define AppURL "https://github.com/zaczzk/Multi_Instance_CRD"
#define AppExeName "CRDLightweightARM.exe"
#define AppId "{{E7D6502B-1912-4BD1-92BE-749C1E1B4A5B}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=..\dist-installers
#if BuildType == "light"
  OutputBaseFilename=Multi_Instance_CRD-Setup-v{#AppVersion}-Lightweight
#else
  OutputBaseFilename=Multi_Instance_CRD-Setup-v{#AppVersion}-DotNetIncluded
#endif
SetupIconFile=..\assets\app-icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
CloseApplications=yes
RestartApplications=no
DisableDirPage=auto
DisableProgramGroupPage=auto
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Start automatically with Windows (System Tray)"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; --- ARM64 Payload (Snapdragon X Series) ---
#if BuildType == "light"
  Source: "..\dist-light\win-arm64\*"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#else
  Source: "..\dist\win-arm64\*"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- x64 Payload (Intel / AMD) ---
#if BuildType == "light"
  Source: "..\dist-light\win-x64\*"; DestDir: "{app}"; Check: IsX64; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak
#else
  Source: "..\dist\win-x64\*"; DestDir: "{app}"; Check: IsX64; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak
#endif

; Common files
Source: "..\Run-CRD.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\app-icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app-icon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app-icon.ico"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app-icon.ico"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// System architecture checks
function IsArm64: Boolean;
begin
  Result := (ProcessorArchitecture = paARM64);
end;

function IsX64: Boolean;
begin
  Result := not IsArm64;
end;

// Semantic version parser and comparator:
// Returns: 1 if V1 > V2, -1 if V1 < V2, 0 if V1 = V2
function CompareVersion(const V1, V2: string): Integer;
var
  P1, P2, N1, N2: Integer;
begin
  Result := 0;
  P1 := 1;
  P2 := 1;
  while (P1 <= Length(V1)) or (P2 <= Length(V2)) do
  begin
    N1 := 0;
    while (P1 <= Length(V1)) and (V1[P1] <> '.') and (V1[P1] <> '-') and (V1[P1] <> 'v') do
    begin
      if (V1[P1] >= '0') and (V1[P1] <= '9') then
        N1 := N1 * 10 + (Ord(V1[P1]) - Ord('0'));
      Inc(P1);
    end;
    if (P1 <= Length(V1)) and ((V1[P1] = '.') or (V1[P1] = '-') or (V1[P1] = 'v')) then
      Inc(P1);

    N2 := 0;
    while (P2 <= Length(V2)) and (V2[P2] <> '.') and (V2[P2] <> '-') and (V2[P2] <> 'v') do
    begin
      if (V2[P2] >= '0') and (V2[P2] <= '9') then
        N2 := N2 * 10 + (Ord(V2[P2]) - Ord('0'));
      Inc(P2);
    end;
    if (P2 <= Length(V2)) and ((V2[P2] = '.') or (V2[P2] = '-') or (V2[P2] = 'v')) then
      Inc(P2);

    if N1 > N2 then
    begin
      Result := 1;
      Exit;
    end
    else if N1 < N2 then
    begin
      Result := -1;
      Exit;
    end;
  end;
end;

// Verify .NET 9 Desktop Runtime installation for Lightweight build
function IsNet9DesktopRuntimeInstalled: Boolean;
var
  RegPaths: array of string;
  Names: TArrayOfString;
  I, J: Integer;
begin
  Result := False;
  SetArrayLength(RegPaths, 4);
  RegPaths[0] := 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\arm64\sharedfx\Microsoft.WindowsDesktop.App';
  RegPaths[1] := 'SOFTWARE\dotnet\Setup\InstalledVersions\arm64\sharedfx\Microsoft.WindowsDesktop.App';
  RegPaths[2] := 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RegPaths[3] := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

  for I := 0 to GetArrayLength(RegPaths) - 1 do
  begin
    if RegGetValueNames(HKEY_LOCAL_MACHINE, RegPaths[I], Names) then
    begin
      for J := 0 to GetArrayLength(Names) - 1 do
      begin
        if Pos('9.', Names[J]) = 1 then
        begin
          Result := True;
          Exit;
        end;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: string;
  UninstallKey: string;
  Cmp: Integer;
  Msg: string;
begin
  Result := True;

  #if BuildType == "light"
    // For lightweight installer, check if .NET 9 Desktop Runtime is present
    if not IsNet9DesktopRuntimeInstalled then
    begin
      Msg := 'Multi_Instance_CRD Lightweight requires Microsoft .NET 9.0 Desktop Runtime.' + #13#10#13#10 +
             'Would you like to open the official Microsoft download page to install it now?' + #13#10 +
             '(Alternatively, you can download the full ".NET-Included" installer which bundles the runtime)';
      if SuppressibleMsgBox(Msg, mbConfirmation, MB_YESNO, IDYES) = IDYES then
      begin
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/9.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, Cmp);
      end;
      Result := False;
      Exit;
    end;
  #endif

  // Check existing installation version in registry
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVersion) or
     RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', InstalledVersion) then
  begin
    Cmp := CompareVersion(InstalledVersion, '{#AppVersion}');
    
    // InstalledVersion > AppVersion (Downgrade scenario)
    if Cmp > 0 then
    begin
      Msg := 'A newer version (' + InstalledVersion + ') of {#AppName} is currently installed.' + #13#10#13#10 +
             'Are you sure you would like to downgrade to version {#AppVersion}?';
      if SuppressibleMsgBox(Msg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;
