; ------------------------------------------------------------------
;  图片查看器 —— Inno Setup 安装脚本（自包含 + setup.exe）
;
;  步骤 1：先做自包含发布（脚本会打包这个目录的全部内容）
;    dotnet publish WinFormsApp1.csproj -c Release -r win-x64 --self-contained true -o bin\Publish\win-x64
;
;  步骤 2：用 Inno Setup 编译器 ISCC.exe 编译本脚本
;    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "installer\图片查看器.iss"
;    产物：bin\Publish\Installer\图片查看器-Setup-1.0.0.exe
; ------------------------------------------------------------------

#define MyAppName      "小马看图"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "PonyView"
#define MyAppExeName   "PonyView.exe"
; 相对本脚本所在目录 installer\，指向自包含发布目录
#define PublishDir     "..\bin\Publish\win-x64"

[Setup]
; AppId 唯一标识本应用（升级 / 卸载识别用），后续版本请保持不变
AppId={{7F3A9C2E-6B1D-4E5A-9C8F-2D4A6B8E0C11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 安装时可由用户选择“仅为我安装”(免 UAC) 或“为所有用户安装”
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\bin\Publish\Installer
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 仅面向 64 位（Magick.NET-Q8-x64 原生库要求）；x64os = 真正的 x64 系统，不含 ARM64 仿真
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; 安装程序自身图标（多尺寸 app.ico 在项目根，相对 installer\ 为 ..\app.ico）
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
; 有简体中文语言包就用中文向导，否则回退英文，保证一定能编译通过
#if FileExists(CompilerPath + "\Languages\ChineseSimplified.isl")
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 打包整个自包含发布目录：程序 + .NET 运行时 + WinForms + Magick 原生库
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可勾选立即运行
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// ------------------------------------------------------------------
//  文件格式关联：安装向导中提供一个勾选页，列出程序支持的全部格式。
//  用户勾选的扩展名会注册为“用本程序打开”，未勾选的保持系统现状不变。
//  关联写入当前用户 (HKCU)；卸载时根据安装时的记录反向清理。
// ------------------------------------------------------------------
const
  ExeName = '{#MyAppExeName}';
  ProgId = 'PonyView.Image';
  AppRegKey = 'Software\PonyView';
  // 与 ImageLoader.SupportedExtensions 保持一致（45 种）
  SupportedFormats = '.jpg .jpeg .png .bmp .gif .tif .tiff .ico .wmf .emf .psd .psb .tga .webp .heic .heif .hif .avif .svg .svgz .jxl .qoi .dng .cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .raf .orf .pef .srw .rw2 .x3f .rwl .mef .mos .dcr .kdc .erf .mrw .raw';
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST = $0000;

// 通知资源管理器文件关联已变更（免重登生效）
procedure SHChangeNotify(wEventID, uFlags, dwItem1, dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';

var
  FormatPage: TWizardPage;
  FormatList: TNewCheckListBox;
  FormatExts: TStringList; // 与 FormatList 行号对齐，index 0 为“全选”占位

// 全选 / 全不选：点击第 0 行（全选项）时联动其余所有格式行
procedure FormatListClick(Sender: TObject);
var
  i: Integer;
  wantAll: Boolean;
begin
  if FormatList.ItemIndex = 0 then
  begin
    wantAll := FormatList.Checked[0];
    for i := 1 to FormatExts.Count - 1 do
      FormatList.Checked[i] := wantAll;
  end;
end;

procedure InitializeWizard;
var
  i: Integer;
  sl: TStringList;
begin
  FormatPage := CreateCustomPage(wpSelectDir,
    '选择要关联的图片格式',
    '勾选希望默认用「图片查看器」打开的格式；未勾选的保持不变。关联对当前 Windows 用户生效。' + #13#10 +
    '注：对已被其他程序占用的常见格式（如 .jpg），Windows 10/11 可能仍保留原默认程序，可在「设置 > 默认应用」中确认。');

  FormatList := TNewCheckListBox.Create(FormatPage);
  FormatList.Parent := FormatPage.Surface;
  FormatList.Width := FormatPage.SurfaceWidth;
  FormatList.Height := FormatPage.SurfaceHeight;
  FormatList.BorderStyle := bsNone;
  FormatList.Flat := True;
  FormatList.OnClick := @FormatListClick;

  FormatExts := TStringList.Create;
  FormatExts.Add('');                                  // index 0：全选占位
  // AddCheckBox(标题, 副标题, 层级, 勾选, 启用, ×, ×, notify) —— 8 参数，后三个布尔取值与官方 CodeClasses.iss 一致
  FormatList.AddCheckBox('全选 / 全不选', '', 0, False, True, False, True, nil);

  sl := TStringList.Create;
  try
    sl.StrictDelimiter := True;
    sl.Delimiter := ' ';
    sl.DelimitedText := SupportedFormats;
    for i := 0 to sl.Count - 1 do
    begin
      FormatExts.Add(sl.Strings[i]);
      FormatList.AddCheckBox(sl.Strings[i], '', 0, False, True, False, True, nil);
    end;
  finally
    sl.Free;
  end;
end;

// 写入文件关联（仅针对勾选的扩展名）
procedure ApplyFormatAssociations;
var
  i: Integer;
  ext, exePath, cmd, icon: String;
  anyChecked: Boolean;
begin
  anyChecked := False;
  for i := 1 to FormatExts.Count - 1 do
    if FormatList.Checked[i] then
      anyChecked := True;

  if not anyChecked then
    Exit; // 一个都没勾，完全不改动系统关联

  exePath := ExpandConstant('{app}\' + ExeName);
  cmd := '"' + exePath + '" "%1"';
  icon := exePath + ',0';

  // ProgId 本体
  RegWriteStringValue(HKCU, 'Software\Classes\' + ProgId, '', '小马看图可打开的图片');
  RegWriteStringValue(HKCU, 'Software\Classes\' + ProgId + '\DefaultIcon', '', icon);
  RegWriteStringValue(HKCU, 'Software\Classes\' + ProgId + '\shell\open\command', '', cmd);

  // 让“打开方式”列表里能看到本程序
  RegWriteStringValue(HKCU, 'Software\Classes\Applications\' + ExeName, 'FriendlyAppName', '小马看图');
  RegWriteStringValue(HKCU, 'Software\Classes\Applications\' + ExeName + '\shell\open\command', '', cmd);

  // 让“设置 > 默认应用”能识别本程序
  RegWriteStringValue(HKCU, AppRegKey + '\Capabilities', 'ApplicationName', '小马看图');
  RegWriteStringValue(HKCU, AppRegKey + '\Capabilities', 'ApplicationDescription', '小马看图 - 多格式图片查看器');

  for i := 1 to FormatExts.Count - 1 do
  begin
    if FormatList.Checked[i] then
    begin
      ext := FormatExts.Strings[i];
      RegWriteStringValue(HKCU, 'Software\Classes\' + ext, '', ProgId);                     // 默认关联
      RegWriteStringValue(HKCU, 'Software\Classes\' + ext + '\OpenWithProgids', ProgId, ''); // 加入“打开方式”
      RegWriteStringValue(HKCU, 'Software\Classes\Applications\' + ExeName + '\SupportedTypes', ext, '');
      RegWriteStringValue(HKCU, AppRegKey + '\Capabilities\FileAssociations', ext, ProgId);  // 记录，供卸载清理
    end;
  end;

  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

// 卸载时清理本程序写入的关联（仅清理仍指向本程序的项）
procedure RemoveFormatAssociations;
var
  i: Integer;
  names: TArrayOfString;
  ext, cur: String;
begin
  if RegGetValueNames(HKCU, AppRegKey + '\Capabilities\FileAssociations', names) then
  begin
    for i := 0 to GetArrayLength(names) - 1 do
    begin
      ext := names[i];
      // 仅当默认值仍指向本程序时才删，避免误删用户后来改过的关联
      if RegQueryStringValue(HKCU, 'Software\Classes\' + ext, '', cur) and (cur = ProgId) then
        RegDeleteValue(HKCU, 'Software\Classes\' + ext, '');
      // 删除本程序在“打开方式”里的登记；若该键随之变空则一并删除
      RegDeleteValue(HKCU, 'Software\Classes\' + ext + '\OpenWithProgids', ProgId);
      RegDeleteKeyIfEmpty(HKCU, 'Software\Classes\' + ext + '\OpenWithProgids');
    end;
  end;

  // 本程序独占的键，连同子键整棵删除
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\' + ExeName);
  RegDeleteKeyIncludingSubkeys(HKCU, AppRegKey);
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\' + ProgId);

  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ApplyFormatAssociations;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFormatAssociations;
end;
