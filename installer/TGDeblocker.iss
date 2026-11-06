#ifndef PayloadDirectory
  #error PayloadDirectory must point to a self-contained Windows x64 publish.
#endif
#ifndef ProductVersion
  #error ProductVersion must be a numeric major.minor.patch version.
#endif

#define AppExe "TGDeblocker.App.exe"
#define ProductName "TGDeblocker"
#define InstallLeaf "TGDeblocker"
#ifdef QaId
  #define ProductId "TGDeblocker.Windows.QA." + QaId
  #define DataLeaf "TGDeblocker-InstallerQA-" + QaId
#else
  #define ProductId "TGDeblocker.Windows"
  #define DataLeaf "TGDeblocker"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher=moz9
AppPublisherURL=https://github.com/moz9/TGDeblocker-GUI
#ifdef QaId
DefaultDirName={localappdata}\TGDeblocker-Tests\{#QaId}\{#InstallLeaf}
#else
DefaultDirName={localappdata}\Programs\{#InstallLeaf}
#endif
UsePreviousAppDir=yes
DisableDirPage=no
AppendDefaultDirName=no
AllowRootDirectory=no
AllowUNCPath=no
AllowNetworkDrive=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\{#AppExe}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
SetupIconFile=..\src\TGDeblocker.App\Assets\AppIcon.ico
WizardStyle=modern dynamic
CloseApplications=yes
CloseApplicationsFilter={#AppExe}
RestartApplications=no
AppMutex={code:StartupAppMutex}
SetupMutex=Local\{#ProductId}.Setup
Compression=lzma2/fast
SolidCompression=yes
OutputBaseFilename={#ProductName}-Setup-{#ProductVersion}-x64
VersionInfoVersion={#ProductVersion}
VersionInfoCompany=moz9
VersionInfoDescription={#ProductName} Setup
VersionInfoProductName={#ProductName}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.UnsafePath=Choose a dedicated folder on a local drive, outside Windows, Program Files, and the TGDeblocker data folder.
ru.UnsafePath=Выберите отдельную папку на локальном диске вне Windows, Program Files и папки данных TGDeblocker.
en.EmptyPath=For a new installation, choose a new or empty folder.
ru.EmptyPath=Для новой установки выберите новую или пустую папку.
en.KeepPath=Install into the same folder as the registered TGDeblocker installation. To move the app, uninstall it first and keep your data.
ru.KeepPath=Выберите папку существующей установки TGDeblocker. Чтобы перенести приложение, сначала удалите его с сохранением данных.
en.InvalidUpdate=The updater requires an existing installation in the specified folder and a newer setup version.
ru.InvalidUpdate=Для обновления нужна существующая установка в указанной папке и более новая версия установщика.
en.OlderVersion=A newer version is installed. Use a setup of the same or newer version.
ru.OlderVersion=Уже установлена более новая версия. Используйте установщик той же или более новой версии.
en.InvalidHandoff=The update could not verify the running application. Start the update again from TGDeblocker.
ru.InvalidHandoff=Не удалось проверить запущенное приложение. Запустите обновление ещё раз из TGDeblocker.
en.AppStillRunning=TGDeblocker is still running. Close all its windows and start the update again.
ru.AppStillRunning=TGDeblocker ещё работает. Закройте все его окна и запустите обновление ещё раз.
en.MarkerError=Setup could not save its installation information in the selected folder.
ru.MarkerError=Не удалось записать сведения об установке в выбранную папку.
en.DeleteData=Also delete your TGDeblocker preferences, connection settings and Telegram session from this computer?%n%nChoose No to keep the data for a future installation.
ru.DeleteData=Удалить также настройки TGDeblocker, параметры подключения и сеанс Telegram с этого компьютера?%n%nВыберите «Нет», чтобы сохранить данные для будущей установки.
en.DataLeft=Some TGDeblocker data could not be removed. The remaining items are in:%n%1
ru.DataLeft=Часть данных TGDeblocker удалить не удалось. Оставшиеся файлы находятся в папке:%n%1

#ifndef QaId
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
#endif

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,installed.json,installed.json.new,unins*.exe,unins*.dat"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifndef QaId
[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--setup-launch"; Description: "{cm:LaunchProgram,{#ProductName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked; Check: IsManualSetup
Filename: "{app}\{#AppExe}"; Parameters: "{code:UpdateLaunchParameters}"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: IsUpdate

[UninstallDelete]
Type: files; Name: "{app}\installed.json"

[Code]
const
  ProductId = '{#ProductId}';
  DataLeaf = '{#DataLeaf}';
  CurrentVersion = '{#ProductVersion}';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ProductId + '_is1';
  DirectoryAttribute = $10;
  ReparseAttribute = $400;
  InvalidHandle = $FFFFFFFF;
  ReadAttributes = $80;
  ShareRead = 1;
  OpenExisting = 3;
  OpenDirectoryWithoutFollowing = $02200000;
  AppMutexName = 'Local\' + ProductId;
  SetupMutexName = AppMutexName + '.Setup';
  UpdateEventPrefix = 'Local\TGDeblocker.UpdateReady.';

type
  TDirectoryHandles = array of Cardinal;
  TAttributeTag = record
    Attributes: Cardinal;
    Tag: Cardinal;
  end;

var
  RemoveData: Boolean;

function PathAttributes(const Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function LongPathName(const Path: String; Output: String; Size: Cardinal): Cardinal;
  external 'GetLongPathNameW@kernel32.dll stdcall';
function ReadVolumeName(const Drive: String; Output: String; Size: Cardinal): Boolean;
  external 'GetVolumeNameForVolumeMountPointW@kernel32.dll stdcall';
function ReadDosDevice(const Drive: String; Output: String; Size: Cardinal): Cardinal;
  external 'QueryDosDeviceW@kernel32.dll stdcall';
function OpenPath(const Path: String; Access, Share, Security, Creation, Flags, Template: Cardinal): Cardinal;
  external 'CreateFileW@kernel32.dll stdcall';
function ReadAttributeTag(Handle, InfoClass: Cardinal; var Data: TAttributeTag; Size: Cardinal): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
function ClosePath(Handle: Cardinal): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function ReplacePath(const OldPath, NewPath: String; Flags: Cardinal): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';
function OpenProcessHandle(Access, Inherit, ProcessId: Cardinal): Cardinal;
  external 'OpenProcess@kernel32.dll stdcall';
function ReadProcessImage(Process, Flags: Cardinal; Image: String; var Size: Cardinal): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function OpenReadyEvent(Access, Inherit: Cardinal; const Name: String): Cardinal;
  external 'OpenEventW@kernel32.dll stdcall';
function SignalReadyEvent(Event: Cardinal): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function WaitForHandle(Handle, Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function HasArgument(const Argument: String): Boolean;
var
  Index: Integer;
begin
  Result := True;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), Argument) = 0 then Exit;
  Result := False;
end;

function ArgumentValue(const Prefix: String): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 1 to ParamCount do
    if CompareText(Copy(ParamStr(Index), 1, Length(Prefix)), Prefix) = 0 then begin
      if Result <> '' then begin Result := ''; Exit; end;
      Result := Copy(ParamStr(Index), Length(Prefix) + 1, Length(ParamStr(Index)));
      if Result = '' then Exit;
    end;
end;

function IsUpdate: Boolean;
begin
  Result := HasArgument('/UPDATE');
end;

function IsManualSetup: Boolean;
begin
  Result := not IsUpdate;
end;

function UpdateLaunchParameters(Param: String): String;
var
  Mode: String;
begin
  Mode := ArgumentValue('/RESTARTMODE=');
  if Mode = 'normal' then Result := '--setup-launch'
  else if Mode = 'session' then Result := '--setup-launch --resume-session'
  else if Mode = 'demo' then Result := '--setup-launch --demo'
  else if Mode = 'visual' then Result := '--setup-launch --visual-check'
  else Result := '';
end;

function StartupAppMutex(Param: String): String;
begin
  { Update deliberately starts while its verified parent is still running. }
  Result := '';
  if IsUninstaller or IsManualSetup then Result := AppMutexName;
end;

function HasSafeLeaf(const Leaf: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if (Leaf = '') or (Length(Leaf) > 64) then Exit;
  for Index := 1 to Length(Leaf) do
    if Pos(Leaf[Index], 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-') = 0 then Exit;
  Result := True;
end;

function VolumeName(const DriveRoot: String): String;
var
  Buffer: String;
begin
  Result := '';
  SetLength(Buffer, 512);
  if ReadDosDevice(Copy(DriveRoot, 1, 2), Buffer, Length(Buffer)) = 0 then Exit;
  { A SUBST drive uses a DOS path instead of a volume device. }
  if Pos('\Device\', Buffer) <> 1 then Exit;
  if ReadVolumeName(DriveRoot, Buffer, Length(Buffer)) then Result := Copy(Buffer, 1, Pos(#0, Buffer) - 1);
end;

function AbsolutePath(const Path: String): String;
var
  Input, Parent, Leaf, Buffer: String;
  Size: Cardinal;
  Attributes: Cardinal;
  Index: Integer;
begin
  Result := '';
  Input := RemoveBackslashUnlessRoot(Path);
  if (Length(Input) < 3) or (Copy(Input, 2, 2) <> ':\') then Exit;
  if Pos(Uppercase(Copy(Input, 1, 1)), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') = 0 then Exit;
  for Index := 4 to Length(Input) do
    if (Ord(Input[Index]) < 32) or (Pos(Input[Index], '/:*?"<>|') > 0) then Exit;
  if Length(Input) = 3 then begin
    if VolumeName(Input) <> '' then Result := Input;
    Exit;
  end;

  Leaf := ExtractFileName(Input);
  if (Leaf = '') or (Leaf[Length(Leaf)] = '.') or (Leaf[Length(Leaf)] = ' ') then Exit;
  Parent := AbsolutePath(ExtractFileDir(Input));
  if Parent = '' then Exit;
  Input := AddBackslash(Parent) + Leaf;
  Attributes := PathAttributes(Input);
  if Attributes = InvalidHandle then begin Result := Input; Exit; end;
  if ((Attributes and ReparseAttribute) <> 0) or ((Attributes and DirectoryAttribute) = 0) then Exit;
  SetLength(Buffer, 261);
  Size := LongPathName(Input, Buffer, Length(Buffer));
  if (Size > 0) and (Size < 261) then Result := Copy(Buffer, 1, Size);
end;

function Within(const Directory, Root: String): Boolean;
var
  DirectoryVolume, RootVolume: String;
begin
  Result := False;
  if (Length(Directory) < 3) or (Length(Root) < 3) then Exit;
  DirectoryVolume := VolumeName(Copy(Directory, 1, 3));
  RootVolume := VolumeName(Copy(Root, 1, 3));
  if (DirectoryVolume = '') or (CompareText(DirectoryVolume, RootVolume) <> 0) then Exit;
  Result := Pos(Uppercase(AddBackslash(Copy(Root, 3, Length(Root)))),
    Uppercase(AddBackslash(Copy(Directory, 3, Length(Directory))))) = 1;
end;

function DataPath: String;
var
  LocalData: String;
begin
  Result := '';
  if not HasSafeLeaf(DataLeaf) then Exit;
  LocalData := AbsolutePath(ExpandConstant('{localappdata}'));
  if (LocalData <> '') and (Length(LocalData) > 3) then Result := AddBackslash(LocalData) + DataLeaf;
end;

function SafeInstallPath(const Path: String): String;
var
  Candidate, Data, Profile, SystemRoot: String;
begin
  Result := '';
  Candidate := AbsolutePath(Path);
  Data := DataPath;
  Profile := AbsolutePath(GetShellFolderByCSIDL($28, False));
  SystemRoot := AbsolutePath(GetWinDir);
  if (Length(Candidate) <= 3) or (Length(Candidate) > 200) or (Data = '') or (Profile = '') or (SystemRoot = '') then Exit;
  if Within(Candidate, Data) or Within(Data, Candidate) or Within(Profile, Candidate) then Exit;
  if Within(Candidate, SystemRoot) or Within(SystemRoot, Candidate) or
     Within(Candidate, AbsolutePath(ExpandConstant('{commonpf64}'))) or
     Within(Candidate, AbsolutePath(ExpandConstant('{commonpf32}'))) or
     Within(Candidate, AbsolutePath(ExpandConstant('{commonappdata}'))) then Exit;
#ifdef QaId
  { The QA app ID must never be installed into a production directory. }
  if Within(Candidate, AbsolutePath(ExpandConstant('{localappdata}\Programs\TGDeblocker'))) or
     Within(Candidate, AbsolutePath(ExpandConstant('{localappdata}\TGDeblocker'))) then Exit;
#endif
  Result := Candidate;
end;

function DirectoryEmpty(const Path: String): Boolean;
var
  Item: TFindRec;
begin
  Result := not DirExists(Path);
  if Result or not FindFirst(AddBackslash(Path) + '*', Item) then Exit;
  try
    repeat
      if (Item.Name <> '.') and (Item.Name <> '..') then Exit;
    until not FindNext(Item);
    Result := True;
  finally
    FindClose(Item);
  end;
end;

function SafeTree(const Directory: String): Boolean;
var
  Item: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(Directory) + '*', Item) then Exit;
  try
    repeat
      if (Item.Name <> '.') and (Item.Name <> '..') then begin
        if (Item.Attributes and ReparseAttribute) <> 0 then Exit;
        if ((Item.Attributes and DirectoryAttribute) <> 0) and
           not SafeTree(AddBackslash(Directory) + Item.Name) then Exit;
      end;
    until not FindNext(Item);
    Result := True;
  finally
    FindClose(Item);
  end;
end;

function HasInstallMarker(const Directory, Version: String): Boolean;
var
  Contents: AnsiString;
begin
  Result := LoadStringFromFile(AddBackslash(Directory) + 'installed.json', Contents) and
    (Pos('{"schema":1,"appId":"' + ProductId + '","version":"' + Version + '",', Utf8Decode(Contents)) = 1);
end;

function InstallProblem(const RequestedDirectory: String): String;
var
  Candidate, ExistingDirectory, ExistingVersion: String;
  OldVersion, NewVersion: Int64;
  Registered, Updating: Boolean;
begin
  Candidate := SafeInstallPath(RequestedDirectory);
  Result := CustomMessage('UnsafePath');
  if Candidate = '' then Exit;

  Registered := RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', ExistingDirectory);
  Updating := not IsManualSetup;
  if Updating and (not Registered or (ArgumentValue('/DIR=') = '')) then begin Result := CustomMessage('InvalidUpdate'); Exit; end;
  if Registered then begin
    if CompareText(Candidate, SafeInstallPath(ExistingDirectory)) <> 0 then begin Result := CustomMessage('KeepPath'); Exit; end;
    if DirExists(Candidate) and not SafeTree(Candidate) then Exit;
    if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', ExistingVersion) and
       StrToVersion(ExistingVersion + '.0', OldVersion) and StrToVersion(CurrentVersion + '.0', NewVersion) then begin
      if ComparePackedVersion(NewVersion, OldVersion) < 0 then begin Result := CustomMessage('OlderVersion'); Exit; end;
    end else if Updating then begin Result := CustomMessage('InvalidUpdate'); Exit; end;
    if Updating and ((ComparePackedVersion(NewVersion, OldVersion) <= 0) or
       not FileExists(AddBackslash(Candidate) + '{#AppExe}') or
       not HasInstallMarker(Candidate, ExistingVersion)) then begin Result := CustomMessage('InvalidUpdate'); Exit; end;
  end else if not DirectoryEmpty(Candidate) then begin Result := CustomMessage('EmptyPath'); Exit; end;
  Result := '';
end;

function ValidReadyEvent(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if (Length(Name) <> Length(UpdateEventPrefix) + 32) or
     (Copy(Name, 1, Length(UpdateEventPrefix)) <> UpdateEventPrefix) then Exit;
  for Index := Length(UpdateEventPrefix) + 1 to Length(Name) do
    if Pos(Name[Index], '0123456789abcdefABCDEF') = 0 then Exit;
  Result := True;
end;

function AcceptHandoff(const Directory: String; var ParentProcess: Cardinal): Boolean;
var
  Pid: Integer;
  Image, ReadyEvent, ExpectedDirectory: String;
  Size, EventHandle: Cardinal;
begin
  Result := False;
  Pid := StrToIntDef(ArgumentValue('/PARENTPID='), 0);
  ReadyEvent := ArgumentValue('/READYEVENT=');
  if (Pid <= 0) or not ValidReadyEvent(ReadyEvent) then Exit;
  ParentProcess := OpenProcessHandle($101000, 0, Pid);
  if ParentProcess = 0 then Exit;
  Size := 261;
  SetLength(Image, Size);
  if not ReadProcessImage(ParentProcess, 0, Image, Size) then Exit;
  Image := Copy(Image, 1, Size);
  if CompareText(ExtractFileName(Image), '{#AppExe}') <> 0 then Exit;
  ExpectedDirectory := SafeInstallPath(Directory);
  if (ExpectedDirectory = '') or (CompareText(SafeInstallPath(ExtractFileDir(Image)), ExpectedDirectory) <> 0) then Exit;
  EventHandle := OpenReadyEvent(2, 0, ReadyEvent);
  if EventHandle = 0 then Exit;
  try
    Result := SignalReadyEvent(EventHandle);
  finally
    ClosePath(EventHandle);
  end;
end;

function InitializeSetup: Boolean;
var
  Directory, Problem: String;
  ParentProcess: Cardinal;
begin
  Result := True;
  if not IsUpdate then Exit;
  Directory := ArgumentValue('/DIR=');
  if UpdateLaunchParameters('') = '' then Problem := CustomMessage('InvalidHandoff')
  else Problem := InstallProblem(Directory);
  if (Problem = '') and CheckForMutexes(SetupMutexName) then Problem := CustomMessage('InvalidHandoff');
  if Problem = '' then begin
    ParentProcess := 0;
    try
      if not AcceptHandoff(Directory, ParentProcess) then Problem := CustomMessage('InvalidHandoff')
      else if (WaitForHandle(ParentProcess, 30000) <> 0) or CheckForMutexes(AppMutexName) then
        Problem := CustomMessage('AppStillRunning');
    finally
      if ParentProcess <> 0 then ClosePath(ParentProcess);
    end;
  end;
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Problem := InstallProblem(WizardDirValue);
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallProblem(WizardDirValue);
  if (Result = '') and CheckForMutexes(AppMutexName) then Result := CustomMessage('AppStillRunning');
end;

function JsonQuote(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := '"' + Value + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Marker, Contents, Staged: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Marker := ExpandConstant('{app}\installed.json');
  Staged := Marker + '.new';
  Contents := '{"schema":1,"appId":' + JsonQuote(ProductId) + ',"version":' + JsonQuote(CurrentVersion) +
    ',"installDirectory":' + JsonQuote(ExpandConstant('{app}')) +
    ',"uninstallExecutable":' + JsonQuote(ExpandConstant('{uninstallexe}')) + '}' + #13#10;
  if not SaveStringToFile(Staged, Utf8Encode(Contents), False) or not ReplacePath(Staged, Marker, 9) then begin
    DeleteFile(Staged);
    RaiseException(CustomMessage('MarkerError'));
  end;
end;

{ Keep every ancestor open without write/delete sharing while cleaning user data. }
function PinDirectory(const Path: String): Cardinal;
var
  Attributes: TAttributeTag;
begin
  Result := OpenPath(Path, ReadAttributes, ShareRead, 0, OpenExisting, OpenDirectoryWithoutFollowing, 0);
  if Result = InvalidHandle then Exit;
  if not ReadAttributeTag(Result, 9, Attributes, 8) or
     ((Attributes.Attributes and ReparseAttribute) <> 0) or
     ((Attributes.Attributes and DirectoryAttribute) = 0) then begin
    ClosePath(Result);
    Result := InvalidHandle;
  end;
end;

function PinParents(const Path: String; var Handles: TDirectoryHandles): Boolean;
var
  Handle: Cardinal;
  Index: Integer;
begin
  Result := False;
  if Length(Path) > 3 then
    if not PinParents(ExtractFileDir(Path), Handles) then Exit;
  Handle := PinDirectory(Path);
  if Handle = InvalidHandle then Exit;
  Index := GetArrayLength(Handles);
  SetArrayLength(Handles, Index + 1);
  Handles[Index] := Handle;
  Result := True;
end;

function DeleteDataTree(const Directory: String): Boolean;
var
  Handle: Cardinal;
  Item: TFindRec;
  Child: String;
begin
  Result := False;
  Handle := PinDirectory(Directory);
  if Handle = InvalidHandle then Exit;
  try
    if not FindFirst(AddBackslash(Directory) + '*', Item) then Exit;
    Result := True;
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin
          Child := AddBackslash(Directory) + Item.Name;
          if (Item.Attributes and ReparseAttribute) <> 0 then Result := False
          else if (Item.Attributes and DirectoryAttribute) <> 0 then begin
            if not DeleteDataTree(Child) then Result := False;
          end else if not DeleteFile(Child) then Result := False;
        end;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
  finally
    ClosePath(Handle);
  end;
  if Result then Result := RemoveDir(Directory);
end;

function InitializeUninstall: Boolean;
var
  Directory: String;
begin
  Directory := SafeInstallPath(ExpandConstant('{app}'));
  Result := (Directory <> '') and SafeTree(Directory);
  if not Result then SuppressibleMsgBox(CustomMessage('UnsafePath'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
  Parents: TDirectoryHandles;
  Index: Integer;
  Deleted: Boolean;
begin
  if UninstallSilent then Exit;
  Data := DataPath;
  if Data = '' then Exit;
  if CurUninstallStep = usUninstall then begin
    if PathAttributes(Data) <> InvalidHandle then
      RemoveData := SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  end else if (CurUninstallStep = usPostUninstall) and RemoveData then begin
    if PathAttributes(Data) = InvalidHandle then Exit;
    Deleted := False;
    try
      if PinParents(ExtractFileDir(Data), Parents) then Deleted := DeleteDataTree(Data);
    finally
      for Index := GetArrayLength(Parents) - 1 downto 0 do ClosePath(Parents[Index]);
    end;
    if not Deleted then
      SuppressibleMsgBox(FmtMessage(CustomMessage('DataLeft'), [Data]), mbInformation, MB_OK, IDOK);
  end;
end;
