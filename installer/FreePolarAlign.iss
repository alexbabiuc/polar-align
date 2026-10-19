; The Windows installer (D39). It bundles no star index. Its own page offers
; Watney's packs, downloaded during installation from Watney's release, or,
; with none chosen, another plate solver set in the user's settings.
;
; Compiled by the release workflow:
;   ISCC /DAppVersion=0.0.28 /DPublishDir=<scripts/publish-windows.sh output> /DOutputDir=<dir> installer\FreePolarAlign.iss

#ifndef AppVersion
  #error Pass /DAppVersion=<version>, the one in Directory.Build.props.
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<directory>, the output of scripts/publish-windows.sh.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{BE7483DB-207D-4B17-BE6C-19B131FEE016}
AppName=FreePolarAlign
AppVersion={#AppVersion}
AppPublisher=alexbabiuc
AppPublisherURL=https://github.com/alexbabiuc/polar-align
; Per user, so no administrator is needed. The star index lives in the user's
; profile anyway, and so do the settings.
PrivilegesRequired=lowest
DefaultDirName={autopf}\FreePolarAlign
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=FreePolarAlign-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
UninstallDisplayIcon={app}\FreePolarAlign.App.exe

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An upgrade must not leave an older version's plugin files beside the new
; ones: a plugin loads what sits next to it before the host's copy (D24).
Type: filesandordirs; Name: "{app}\plugins"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\FreePolarAlign"; Filename: "{app}\FreePolarAlign.App.exe"
Name: "{autodesktop}\FreePolarAlign"; Filename: "{app}\FreePolarAlign.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FreePolarAlign.App.exe"; Description: "{cm:LaunchProgram,FreePolarAlign}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WatneyRelease = 'https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/';

var
  PackPage: TInputOptionWizardPage;
  SolverPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;

  PackRanges: array[0..3] of String;
  PackNames: array[0..3] of String;
  PackInstalledBefore: array[0..3] of Boolean;
  PackDownloaded: array[0..3] of Boolean;
  PackOutcome: array[0..3] of String;

  SolverArguments: array[0..2] of String;
  SolverNames: array[0..2] of String;
  SolverOutcome: String;

{ Where every build of the application finds the index (EngineFactory). Not
  beside the executable: it must survive uninstalling and upgrading. }
function QuadDatabaseDirectory: String;
begin
  Result := ExpandConstant('{%USERPROFILE}') + '\.free-polar-align\quaddb';
end;

function PackZip(I: Integer): String;
begin
  Result := 'watneyqdb-' + PackRanges[I] + '-20-v3.zip';
end;

{ The .qdbindex is the part most easily lost, and Watney refuses a pack
  without it (D13). The script checks the cells as well when it installs. }
function PackIsInstalled(I: Integer): Boolean;
begin
  Result := FileExists(QuadDatabaseDirectory + '\gaia2-' + PackRanges[I] + '-20.qdbindex');
end;

{ The same default locations the solver adapters look in. }
function SolverFound(I: Integer): Boolean;
begin
  case I of
    0: Result := FileExists(ExpandConstant('{commonpf64}\astap\astap_cli.exe')) or
                 FileExists(ExpandConstant('{commonpf64}\astap\astap.exe'));
    1: Result := FileExists(ExpandConstant('{commonpf32}\PlateSolver\PlateSolver.exe')) or
                 FileExists(ExpandConstant('{commonpf64}\PlateSolver\PlateSolver.exe'));
    2: Result := FileExists(ExpandConstant('{userdocs}\PlateSolve3\PlateSolve3.80\PlateSolve3.80.exe')) or
                 FileExists(ExpandConstant('{userdocs}\PlateSolve3\PlateSolve3.80.exe'));
  else
    Result := False;
  end;
end;

function AnyPackChosen: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to 3 do
    if PackPage.Values[I] then
      Result := True;
end;

function AnyPackInstalledBefore: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to 3 do
    if PackInstalledBefore[I] then
      Result := True;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

function AnySolverFound: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to 2 do
    if SolverFound(I) then
      Result := True;
end;

{ Why a first install starts with nothing ticked, said where it is seen. }
function FoundSolverNote: String;
var
  I: Integer;
begin
  Result := '';
  if AnyPackInstalledBefore then
    Exit;
  for I := 2 downto 0 do
    if SolverFound(I) then
      Result := SolverNames[I];
  if Result <> '' then
    Result := ' ' + Result + ' is already installed, so none is ticked.';
end;

procedure InitializeWizard;
var
  I, Found: Integer;
  Caption: String;
begin
  PackRanges[0] := '00-07';
  PackRanges[1] := '08-09';
  PackRanges[2] := '10-11';
  PackRanges[3] := '12-13';
  PackNames[0] := 'Wide field: frames 1.6 degrees across and wider (369 MB)';
  PackNames[1] := 'Medium field: 1.2 to 1.6 degrees (390 MB)';
  PackNames[2] := 'Narrow field: 0.8 to 1.2 degrees (780 MB)';
  PackNames[3] := 'Very narrow field: 0.6 to 0.8 degrees (1.56 GB)';

  SolverArguments[0] := 'Astap';
  SolverArguments[1] := 'Asps';
  SolverArguments[2] := 'Ps3';
  SolverNames[0] := 'ASTAP';
  SolverNames[1] := 'ASPS (All Sky Plate Solver)';
  SolverNames[2] := 'PlateSolve 3';

  { Before the page, whose text depends on it. }
  for I := 0 to 3 do
    PackInstalledBefore[I] := PackIsInstalled(I);

  PackPage := CreateInputOptionPage(wpSelectDir,
    'Star index',
    'Which frame widths will you plate-solve?',
    'The built-in solver needs a star index for the width of your camera''s frame. ' +
    'Tick each range your frames fall in. They are downloaded now from Watney''s own release.' + #13#10#13#10 +
    'Frame width is about the sensor diagonal divided by the focal length. A typical guide camera ' +
    '(6.4 mm diagonal) is wide field up to about 200 mm, medium to 300 mm, narrow to 450 mm and ' +
    'very narrow to 600 mm. Narrower frames need another solver.' + #13#10#13#10 +
    'Untick them all to use another plate solver instead.' + FoundSolverNote,
    False, False);

  for I := 0 to 3 do
  begin
    PackDownloaded[I] := False;
    PackOutcome[I] := '';
    Caption := PackNames[I];
    if PackInstalledBefore[I] then
      Caption := Caption + ' - already installed';
    PackPage.Add(Caption);
  end;

  { An upgrade keeps what is there and adds nothing unasked. A first install
    starts from the two wide packs, which cover most guide scopes, unless
    another solver is already installed: then nothing is ticked, the solver
    page offers the one found, and nothing is downloaded unasked (D39). }
  for I := 0 to 3 do
    if AnyPackInstalledBefore then
      PackPage.Values[I] := PackInstalledBefore[I]
    else
      PackPage.Values[I] := (I < 2) and not AnySolverFound;

  SolverPage := CreateInputOptionPage(PackPage.ID,
    'Plate solver',
    'Which plate solver should FreePolarAlign use?',
    'No star index was chosen, so FreePolarAlign needs another plate solver. It is installed ' +
    'separately, with its own star data. It will be used for every solve, and the Settings ' +
    'window can change it later.',
    True, False);

  Found := -1;
  for I := 0 to 2 do
    if SolverFound(I) then
    begin
      SolverPage.Add(SolverNames[I] + ' - found');
      if Found < 0 then
        Found := I;
    end
    else
      SolverPage.Add(SolverNames[I] + ' - not found where it is usually installed');

  if Found < 0 then
    Found := 0;
  SolverPage.SelectedValueIndex := Found;

  DownloadPage := CreateDownloadPage('Downloading the star index',
    'The packs come from Watney''s release on GitHub.', @OnDownloadProgress);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SolverPage.ID) and AnyPackChosen;
end;

{ Downloads into the temporary folder before anything is installed, one pack
  at a time, so a failure costs only that pack. Installing them waits for the
  files, because the script that does it is one of them. }
procedure DownloadPacks;
var
  I: Integer;
  Retry: Boolean;
begin
  for I := 0 to 3 do
  begin
    PackDownloaded[I] := False;
    if PackPage.Values[I] and not PackInstalledBefore[I] then
    begin
      Retry := True;
      while Retry do
      begin
        DownloadPage.Clear;
        DownloadPage.Add(WatneyRelease + PackZip(I), PackZip(I), '');
        DownloadPage.Show;
        try
          try
            DownloadPage.Download;
            PackDownloaded[I] := True;
            Retry := False;
          except
            if DownloadPage.AbortedByUser then
              Retry := False
            else
              Retry := SuppressibleMsgBox(AddPeriod(GetExceptionMessage) + #13#10#13#10 +
                'Retry the download, or Cancel to go on without this pack?',
                mbError, MB_RETRYCANCEL, IDCANCEL) = IDRETRY;
            if not Retry then
              PackOutcome[I] := 'not downloaded';
          end;
        finally
          DownloadPage.Hide;
        end;
      end;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = SolverPage.ID then
    if not SolverFound(SolverPage.SelectedValueIndex) then
      Result := SuppressibleMsgBox(SolverNames[SolverPage.SelectedValueIndex] +
        ' was not found where it is usually installed. Plate solving will fail until it is installed ' +
        'and, if it is somewhere else, its path is set in the Settings window.' + #13#10#13#10 +
        'Choose it anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;

  if CurPageID = wpReady then
    DownloadPacks;
end;

procedure InstallPacks;
var
  I, Code: Integer;
  Parameters: String;
begin
  for I := 0 to 3 do
    if PackDownloaded[I] then
    begin
      WizardForm.StatusLabel.Caption := 'Installing the star index: ' + PackNames[I];
      Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Download-QuadDatabase.ps1') +
        '" -UserProfile -Pack ' + PackRanges[I] + ' -ZipDirectory "' + ExpandConstant('{tmp}') + '"';
      if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '',
              SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
        PackOutcome[I] := 'installed'
      else
        PackOutcome[I] := 'could not be installed (code ' + IntToStr(Code) + ')';
      Log(PackZip(I) + ': ' + PackOutcome[I]);
    end;
end;

procedure SetSolver;
var
  Choice, Code: Integer;
begin
  Choice := SolverPage.SelectedValueIndex;
  WizardForm.StatusLabel.Caption := 'Setting the plate solver';

  { Code is the program's exit code only when Exec succeeds; otherwise it is a
    Windows error code, and 3 would be misread as SetSolverCommand's. }
  if not Exec(ExpandConstant('{app}\FreePolarAlign.App.exe'), '--set-solver ' + SolverArguments[Choice], '',
              SW_HIDE, ewWaitUntilTerminated, Code) then
    SolverOutcome := 'The plate solver was not set: the application could not be started (' +
      SysErrorMessage(Code) + '). Choose it in the Settings window.'
  else if Code = 0 then
    SolverOutcome := SolverNames[Choice] + ' is set as the plate solver.'
  else if Code = 3 then
    SolverOutcome := 'The plate solver was not set: the existing settings file could not be read, ' +
      'and has been left alone. Choose the solver in the Settings window.'
  else
    SolverOutcome := 'The plate solver was not set (code ' + IntToStr(Code) + '). ' +
      'Choose it in the Settings window.';
  Log(SolverOutcome);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallPacks;
    if not AnyPackChosen then
      SetSolver;
  end;
end;

{ The packs actually installed, never the ones ticked (D39). }
procedure CurPageChanged(CurPageID: Integer);
var
  I: Integer;
  Summary: String;
  AnyFailed: Boolean;
begin
  if CurPageID <> wpFinished then
    Exit;

  Summary := '';
  AnyFailed := False;
  for I := 0 to 3 do
    if PackPage.Values[I] then
    begin
      if PackInstalledBefore[I] then
        Summary := Summary + PackNames[I] + ': already installed' + #13#10
      else
      begin
        if PackOutcome[I] <> 'installed' then
          AnyFailed := True;
        if PackOutcome[I] = '' then
          PackOutcome[I] := 'not installed';
        Summary := Summary + PackNames[I] + ': ' + PackOutcome[I] + #13#10;
      end;
    end;

  if AnyFailed then
    Summary := Summary + #13#10 + 'Run this installer again to retry, or run Download-QuadDatabase.ps1 ' +
      'in the installation folder.' + #13#10;

  if SolverOutcome <> '' then
    Summary := Summary + SolverOutcome + #13#10;

  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Summary;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Directory: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  { Asked, and yes by default: uninstalling should take back the gigabytes it
    downloaded. Keeping it is for someone about to reinstall, who says so. }
  Directory := QuadDatabaseDirectory;
  if DirExists(Directory) then
    if SuppressibleMsgBox('Also delete the downloaded star index in ' + Directory + '?' + #13#10#13#10 +
         'It takes up to several gigabytes. Choose No to keep it if you will reinstall.',
         mbConfirmation, MB_YESNO, IDYES) = IDYES then
      DelTree(Directory, True, True, True);
end;
