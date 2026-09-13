; ==============================================================================
; DroidLens - Modern Inno Setup Single-Window Installer & Streamlined Uninstaller
; Fast 1-click installation & 1-dialog complete uninstallation
; ==============================================================================

#define MyAppName "DroidLens"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DroidLens"
#define MyAppURL "https://github.com/Sfix0/DroidLens"
#define MyAppExeName "DroidLens.exe"
#define SoftcamClsid "{AEF3B972-5FA5-4647-9571-358EB472BC9E}"
; Default assumes the sources are checked out as documented (script lives in
; client/installer/, publish output lands in client/bin/...). Override from
; the command line on other layouts with:
;   ISCC.exe /DPublishDir="C:\path\to\publish" DroidLens_Installer.iss
#ifndef PublishDir
  #define PublishDir SourcePath + "\..\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{D37F2891-B891-4C55-9B21-E8A63BD9F081}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
SetupIconFile=assets\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark
WizardSizePercent=100,100
ShowLanguageDialog=no
OutputDir=Output
OutputBaseFilename=DroidLens_Setup_v{#MyAppVersion}
UninstallRestartComputer=no
CloseApplications=yes
RestartApplications=no

; ── Languages: uk + en only, mirroring the app itself (en.lang/uk.lang).
; No language dialog — Inno auto-selects by OS locale (uk-UA → Ukrainian,
; everything else falls back to the first listed language, English),
; same rule as the app's DetectSystemLanguage on first run.
[Languages]
Name: "en"; MessagesFile: "assets\English.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
en.WizardCaption=DroidLens Setup
uk.WizardCaption=Встановлення DroidLens
en.AppSubtitle=Wireless and wired smartphone camera streaming to PC
uk.AppSubtitle=Бездротова та дротова трансляція камери смартфона на ПК
en.VersionBadge=v{#MyAppVersion}  •  Setup
uk.VersionBadge=v{#MyAppVersion}  •  Встановлення
en.CardTitle=Installation options
uk.CardTitle=Параметри встановлення
en.DestFolder=Destination folder:
uk.DestFolder=Папка призначення:
en.BrowseButton=Browse...
uk.BrowseButton=Огляд...
en.BrowsePrompt=Select the folder to install DroidLens:
uk.BrowsePrompt=Оберіть папку для встановлення DroidLens:
en.ExtraActions=Additional actions:
uk.ExtraActions=Додаткові дії:
en.DesktopShortcut=Create a desktop shortcut
uk.DesktopShortcut=Створити ярлик на робочому столі
en.StartMenuShortcut=Create a Start menu shortcut
uk.StartMenuShortcut=Створити ярлик у меню «Пуск»
en.LaunchAfter=Launch DroidLens after installation completes
uk.LaunchAfter=Запустити DroidLens після завершення встановлення
en.ProgressTitle=Extracting and registering components...
uk.ProgressTitle=Розпакування та реєстрація компонентів...
en.ProgressStatus=Copying program files and the DirectShow virtual camera...
uk.ProgressStatus=Копіювання файлів програми та DirectShow віртуальної камери...
en.QuickHint=• Fast 1-click installation
uk.QuickHint=• Швидке встановлення в 1 клік
en.InstallingHint=• Installing... Please wait
uk.InstallingHint=• Встановлення... Зачекайте будь ласка
en.InstallButton=Install
uk.InstallButton=Встановити
en.CancelButton=Cancel
uk.CancelButton=Скасувати
en.NeedSpaceFull=Required free space: ~250 MB   •   Available on drive (%1): %2
uk.NeedSpaceFull=Потрібно вільного місця: ~250 МБ   •   Доступно на диску (%1): %2
en.NeedSpaceShort=Required free space: ~250 MB
uk.NeedSpaceShort=Потрібно вільного місця: ~250 МБ
en.SoftcamLockedWarning=softcam.dll is still in use and could not be removed. Close every app using the DroidLens virtual camera (OBS, Discord, browsers), then delete the file manually or restart Windows to complete removal.
uk.SoftcamLockedWarning=softcam.dll все ще використовується і його не вдалося видалити. Закрий усі програми, що використовують віртуальну камеру DroidLens (OBS, Discord, браузери), потім видали файл вручну або перезавантаж Windows для завершення видалення.
en.SoftcamRebootNote=softcam.dll was in use by another app (OBS, Discord, browser) and has been scheduled for removal on the next Windows restart. Nothing else to do.
uk.SoftcamRebootNote=softcam.dll використовувався іншою програмою (OBS, Discord, браузер), його видалення заплановано на наступне перезавантаження Windows. Більше нічого робити не треба.
en.SpaceUnitGB=GB
uk.SpaceUnitGB=ГБ

[Messages]
; NOTE: deliberately bilingual static text — {cm:} constants are NOT expanded
; in [Messages] by the uninstaller (Inno Setup 7 beta), so a per-language
; ConfirmUninstall via CustomMessages shows up literally. All other strings
; stay in [CustomMessages] and are resolved from code, which works fine.
ConfirmUninstall=Ви дійсно бажаєте видалити DroidLens?%n%nПід час видалення буде:%n  • Завершено всі запущені процеси DroidLens%n  • Скасовано системну реєстрацію віртуальної камери (softcam.dll)%n  • Видалено всі файли програми та створені ярлики%n  • Повністю очищено персональні налаштування (%AppData%\DroidLens)%n%n---%n%nDo you really want to uninstall DroidLens?%n%nDuring uninstallation:%n  • All running DroidLens processes will be terminated%n  • The virtual camera (softcam.dll) system registration will be removed%n  • All program files and created shortcuts will be deleted%n  • Personal settings (%AppData%\DroidLens) will be fully cleaned

[Files]
Source: "assets\logo_48.bmp"; Flags: dontcopy
Source: "assets\app_icon.ico"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateDesktopIcon
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateStartMenuIcon

[Code]
function DwmSetWindowAttribute(hwnd: HWND; dwAttribute: DWORD; var pvAttribute: Integer; cbAttribute: DWORD): LongInt;
external 'DwmSetWindowAttribute@dwmapi.dll stdcall';

// Schedules a file for deletion on next reboot (used when a locked DLL like
// softcam.dll can't be removed because another app still holds it).
// Returns True when the schedule was accepted — Windows then deletes the
// file itself, no manual step needed.
const
  MOVEFILE_DELAY_UNTIL_REBOOT = $00000004;
function MoveFileEx(lpExistingFileName, lpNewFileName: string; dwFlags: DWORD): BOOL;
external 'MoveFileExW@kernel32.dll stdcall';

function ScheduleDeleteOnReboot(const Path: string): Boolean;
begin
  Result := MoveFileEx(Path, '', MOVEFILE_DELAY_UNTIL_REBOOT);
end;

var
  BgPanel: TPanel;
  HeaderPanel: TPanel;
  HeaderBottomLine: TPanel;
  LogoImage: TBitmapImage;
  TitleDroidLabel: TLabel;
  TitleLensLabel: TLabel;
  VersionBadgeLabel: TLabel;
  SubtitleLabel: TLabel;

  CardBorderPanel: TPanel;
  CardPanel: TPanel;
  CardTitleLabel: TLabel;
  PathDescLabel: TLabel;
  PathEdit: TNewEdit;
  BrowseBtn: TNewButton;
  SpaceLabel: TLabel;
  CardDivider: TPanel;

  OptionsTitleLabel: TLabel;
  DesktopShortcutCheck: TNewCheckBox;
  StartMenuShortcutCheck: TNewCheckBox;
  LaunchAfterCheck: TNewCheckBox;

  ProgressTitleLabel: TLabel;
  ProgressStatusLabel: TLabel;

  FooterPanel: TPanel;
  FooterTopLine: TPanel;
  HintLabel: TLabel;
  InstallBtn: TNewButton;
  CancelBtn: TNewButton;

function ShouldCreateDesktopIcon(): Boolean;
begin
  Result := DesktopShortcutCheck.Checked;
end;

function ShouldCreateStartMenuIcon(): Boolean;
begin
  Result := StartMenuShortcutCheck.Checked;
end;

procedure UpdateDiskSpaceInfo;
var
  Drive: string;
  FreeMB, TotalMB: Cardinal;
  FreeGBStr: string;
begin
  Drive := ExtractFileDrive(PathEdit.Text);
  if Drive = '' then
    Drive := ExtractFileDrive(ExpandConstant('{autopf}'));
    
  if GetSpaceOnDisk(Drive, True, FreeMB, TotalMB) then
  begin
    FreeGBStr := Format('%.1n ' + CustomMessage('SpaceUnitGB'), [Extended(FreeMB) / 1024.0]);
    SpaceLabel.Caption := FmtMessage(CustomMessage('NeedSpaceFull'), [Drive, FreeGBStr]);
  end
  else
  begin
    SpaceLabel.Caption := CustomMessage('NeedSpaceShort');
  end;
end;

// Ensure path always includes a dedicated \DroidLens folder
function EnsureAppSubfolder(const InPath: string): string;
var
  CleanPath: string;
begin
  CleanPath := Trim(InPath);
  // Remove any trailing backslash for clean name extraction
  while (Length(CleanPath) > 0) and (CleanPath[Length(CleanPath)] = '\') do
    Delete(CleanPath, Length(CleanPath), 1);

  if (CleanPath <> '') and (CompareText(ExtractFileName(CleanPath), '{#MyAppName}') <> 0) then
    Result := CleanPath + '\{#MyAppName}'
  else
    Result := CleanPath;
end;

procedure BrowseBtnClick(Sender: TObject);
var
  SelectedDir: string;
begin
  SelectedDir := PathEdit.Text;
  if BrowseForFolder(CustomMessage('BrowsePrompt'), SelectedDir, True) then
  begin
    PathEdit.Text := EnsureAppSubfolder(SelectedDir);
    UpdateDiskSpaceInfo;
  end;
end;

procedure PathEditChange(Sender: TObject);
begin
  UpdateDiskSpaceInfo;
end;

procedure CancelBtnClick(Sender: TObject);
begin
  WizardForm.Close;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Cancel := True;
  Confirm := False;
end;

procedure InstallBtnClick(Sender: TObject);
var
  FinalDir: string;
begin
  // Make 100% sure we install into a dedicated DroidLens folder even if typed manually
  FinalDir := EnsureAppSubfolder(PathEdit.Text);
  PathEdit.Text := FinalDir;
  WizardForm.DirEdit.Text := FinalDir;

  CardTitleLabel.Visible := False;
  PathDescLabel.Visible := False;
  PathEdit.Visible := False;
  BrowseBtn.Visible := False;
  SpaceLabel.Visible := False;
  CardDivider.Visible := False;
  OptionsTitleLabel.Visible := False;
  DesktopShortcutCheck.Visible := False;
  StartMenuShortcutCheck.Visible := False;
  LaunchAfterCheck.Visible := False;
  InstallBtn.Visible := False;
  CancelBtn.Visible := False;

  HintLabel.Caption := CustomMessage('InstallingHint');
  HintLabel.Font.Color := $F88C81;

  ProgressTitleLabel.Visible := True;
  ProgressStatusLabel.Visible := True;

  WizardForm.ProgressGauge.Parent := CardPanel;
  WizardForm.ProgressGauge.SetBounds(ScaleX(20), ScaleY(100), CardPanel.Width - ScaleX(40), ScaleY(24));
  WizardForm.ProgressGauge.Visible := True;

  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure ApplyModernWindowStyle(FormHandle: HWND);
var
  Val: Integer;
begin
  Val := 1;
  DwmSetWindowAttribute(FormHandle, 20, Val, SizeOf(Val));

  Val := 2;
  DwmSetWindowAttribute(FormHandle, 33, Val, SizeOf(Val));

  Val := $003A272A;
  DwmSetWindowAttribute(FormHandle, 34, Val, SizeOf(Val));
end;

procedure InitializeWizard;
var
  BaseW, BaseH: Integer;
begin
  BaseW := 620;
  BaseH := 470;
  WizardForm.ClientWidth := ScaleX(BaseW);
  WizardForm.ClientHeight := ScaleY(BaseH);
  WizardForm.Position := poScreenCenter;
  WizardForm.Caption := CustomMessage('WizardCaption');

  ApplyModernWindowStyle(WizardForm.Handle);

  WizardForm.OuterNotebook.Visible := False;
  WizardForm.Bevel.Visible := False;
  WizardForm.BackButton.Visible := False;
  WizardForm.NextButton.Visible := False;
  WizardForm.CancelButton.Visible := False;

  ExtractTemporaryFile('logo_48.bmp');

  BgPanel := TPanel.Create(WizardForm);
  BgPanel.Parent := WizardForm;
  BgPanel.SetBounds(0, 0, WizardForm.ClientWidth, WizardForm.ClientHeight);
  BgPanel.Color := $140D0E;
  BgPanel.ParentBackground := False;
  BgPanel.BevelOuter := bvNone;

  HeaderPanel := TPanel.Create(BgPanel);
  HeaderPanel.Parent := BgPanel;
  HeaderPanel.SetBounds(0, 0, WizardForm.ClientWidth, ScaleY(82));
  HeaderPanel.Color := $201416;
  HeaderPanel.ParentBackground := False;
  HeaderPanel.BevelOuter := bvNone;

  HeaderBottomLine := TPanel.Create(HeaderPanel);
  HeaderBottomLine.Parent := HeaderPanel;
  HeaderBottomLine.SetBounds(0, HeaderPanel.Height - ScaleY(1), HeaderPanel.Width, ScaleY(1));
  HeaderBottomLine.Color := $3A272A;
  HeaderBottomLine.ParentBackground := False;
  HeaderBottomLine.BevelOuter := bvNone;

  LogoImage := TBitmapImage.Create(HeaderPanel);
  LogoImage.Parent := HeaderPanel;
  LogoImage.SetBounds(ScaleX(24), ScaleY(17), ScaleX(48), ScaleY(48));
  LogoImage.Bitmap.LoadFromFile(ExpandConstant('{tmp}\logo_48.bmp'));

  TitleDroidLabel := TLabel.Create(HeaderPanel);
  TitleDroidLabel.Parent := HeaderPanel;
  TitleDroidLabel.Caption := 'Droid';
  TitleDroidLabel.Font.Name := 'Segoe UI';
  TitleDroidLabel.Font.Size := 17;
  TitleDroidLabel.Font.Style := [fsBold];
  TitleDroidLabel.Font.Color := $F9F5F1;
  TitleDroidLabel.Left := ScaleX(84);
  TitleDroidLabel.Top := ScaleY(16);

  TitleLensLabel := TLabel.Create(HeaderPanel);
  TitleLensLabel.Parent := HeaderPanel;
  TitleLensLabel.Caption := 'Lens';
  TitleLensLabel.Font.Name := 'Segoe UI';
  TitleLensLabel.Font.Size := 17;
  TitleLensLabel.Font.Style := [fsBold];
  TitleLensLabel.Font.Color := $F88C81;
  TitleLensLabel.Left := TitleDroidLabel.Left + TitleDroidLabel.Width + ScaleX(1);
  TitleLensLabel.Top := TitleDroidLabel.Top;

  VersionBadgeLabel := TLabel.Create(HeaderPanel);
  VersionBadgeLabel.Parent := HeaderPanel;
  VersionBadgeLabel.Caption := CustomMessage('VersionBadge');
  VersionBadgeLabel.Font.Name := 'Segoe UI';
  VersionBadgeLabel.Font.Size := 9;
  VersionBadgeLabel.Font.Color := $A08A8E;
  VersionBadgeLabel.Left := TitleLensLabel.Left + TitleLensLabel.Width + ScaleX(10);
  VersionBadgeLabel.Top := TitleLensLabel.Top + ScaleY(8);

  SubtitleLabel := TLabel.Create(HeaderPanel);
  SubtitleLabel.Parent := HeaderPanel;
  SubtitleLabel.Caption := CustomMessage('AppSubtitle');
  SubtitleLabel.Font.Name := 'Segoe UI';
  SubtitleLabel.Font.Size := 9;
  SubtitleLabel.Font.Color := $A08A8E;
  SubtitleLabel.Left := ScaleX(84);
  SubtitleLabel.Top := ScaleY(47);

  CardBorderPanel := TPanel.Create(BgPanel);
  CardBorderPanel.Parent := BgPanel;
  CardBorderPanel.SetBounds(ScaleX(24), ScaleY(98), WizardForm.ClientWidth - ScaleX(48), ScaleY(282));
  CardBorderPanel.Color := $3A272A;
  CardBorderPanel.ParentBackground := False;
  CardBorderPanel.BevelOuter := bvNone;

  CardPanel := TPanel.Create(CardBorderPanel);
  CardPanel.Parent := CardBorderPanel;
  CardPanel.SetBounds(ScaleX(1), ScaleY(1), CardBorderPanel.Width - ScaleX(2), CardBorderPanel.Height - ScaleY(2));
  CardPanel.Color := $201416;
  CardPanel.ParentBackground := False;
  CardPanel.BevelOuter := bvNone;

  CardTitleLabel := TLabel.Create(CardPanel);
  CardTitleLabel.Parent := CardPanel;
  CardTitleLabel.Caption := CustomMessage('CardTitle');
  CardTitleLabel.Font.Name := 'Segoe UI';
  CardTitleLabel.Font.Size := 11;
  CardTitleLabel.Font.Style := [fsBold];
  CardTitleLabel.Font.Color := $F9F5F1;
  CardTitleLabel.Left := ScaleX(20);
  CardTitleLabel.Top := ScaleY(16);

  PathDescLabel := TLabel.Create(CardPanel);
  PathDescLabel.Parent := CardPanel;
  PathDescLabel.Caption := CustomMessage('DestFolder');
  PathDescLabel.Font.Name := 'Segoe UI';
  PathDescLabel.Font.Size := 9;
  PathDescLabel.Font.Color := $A08A8E;
  PathDescLabel.Left := ScaleX(20);
  PathDescLabel.Top := ScaleY(42);

  PathEdit := TNewEdit.Create(CardPanel);
  PathEdit.Parent := CardPanel;
  PathEdit.SetBounds(ScaleX(20), ScaleY(64), CardPanel.Width - ScaleX(142), ScaleY(28));
  PathEdit.Font.Name := 'Segoe UI';
  PathEdit.Font.Size := 9;
  PathEdit.Text := ExpandConstant('{autopf}\{#MyAppName}');
  PathEdit.OnChange := @PathEditChange;

  BrowseBtn := TNewButton.Create(CardPanel);
  BrowseBtn.Parent := CardPanel;
  BrowseBtn.SetBounds(CardPanel.Width - ScaleX(112), ScaleY(63), ScaleX(92), ScaleY(30));
  BrowseBtn.Caption := CustomMessage('BrowseButton');
  BrowseBtn.Font.Name := 'Segoe UI';
  BrowseBtn.Font.Size := 9;
  BrowseBtn.OnClick := @BrowseBtnClick;

  SpaceLabel := TLabel.Create(CardPanel);
  SpaceLabel.Parent := CardPanel;
  SpaceLabel.Font.Name := 'Segoe UI';
  SpaceLabel.Font.Size := 8;
  SpaceLabel.Font.Color := $80DE4A;
  SpaceLabel.Left := ScaleX(20);
  SpaceLabel.Top := ScaleY(100);
  UpdateDiskSpaceInfo;

  CardDivider := TPanel.Create(CardPanel);
  CardDivider.Parent := CardPanel;
  CardDivider.SetBounds(ScaleX(20), ScaleY(126), CardPanel.Width - ScaleX(40), ScaleY(1));
  CardDivider.Color := $3A272A;
  CardDivider.ParentBackground := False;
  CardDivider.BevelOuter := bvNone;

  OptionsTitleLabel := TLabel.Create(CardPanel);
  OptionsTitleLabel.Parent := CardPanel;
  OptionsTitleLabel.Caption := CustomMessage('ExtraActions');
  OptionsTitleLabel.Font.Name := 'Segoe UI';
  OptionsTitleLabel.Font.Size := 10;
  OptionsTitleLabel.Font.Style := [fsBold];
  OptionsTitleLabel.Font.Color := $F9F5F1;
  OptionsTitleLabel.Left := ScaleX(20);
  OptionsTitleLabel.Top := ScaleY(140);

  DesktopShortcutCheck := TNewCheckBox.Create(CardPanel);
  DesktopShortcutCheck.Parent := CardPanel;
  DesktopShortcutCheck.SetBounds(ScaleX(20), ScaleY(168), CardPanel.Width - ScaleX(40), ScaleY(22));
  DesktopShortcutCheck.Caption := CustomMessage('DesktopShortcut');
  DesktopShortcutCheck.Font.Name := 'Segoe UI';
  DesktopShortcutCheck.Font.Size := 9;
  DesktopShortcutCheck.Checked := True;

  StartMenuShortcutCheck := TNewCheckBox.Create(CardPanel);
  StartMenuShortcutCheck.Parent := CardPanel;
  StartMenuShortcutCheck.SetBounds(ScaleX(20), ScaleY(196), CardPanel.Width - ScaleX(40), ScaleY(22));
  StartMenuShortcutCheck.Caption := CustomMessage('StartMenuShortcut');
  StartMenuShortcutCheck.Font.Name := 'Segoe UI';
  StartMenuShortcutCheck.Font.Size := 9;
  StartMenuShortcutCheck.Checked := True;

  LaunchAfterCheck := TNewCheckBox.Create(CardPanel);
  LaunchAfterCheck.Parent := CardPanel;
  LaunchAfterCheck.SetBounds(ScaleX(20), ScaleY(224), CardPanel.Width - ScaleX(40), ScaleY(22));
  LaunchAfterCheck.Caption := CustomMessage('LaunchAfter');
  LaunchAfterCheck.Font.Name := 'Segoe UI';
  LaunchAfterCheck.Font.Size := 9;
  LaunchAfterCheck.Checked := True;

  ProgressTitleLabel := TLabel.Create(CardPanel);
  ProgressTitleLabel.Parent := CardPanel;
  ProgressTitleLabel.Caption := CustomMessage('ProgressTitle');
  ProgressTitleLabel.Font.Name := 'Segoe UI';
  ProgressTitleLabel.Font.Size := 12;
  ProgressTitleLabel.Font.Style := [fsBold];
  ProgressTitleLabel.Font.Color := $F9F5F1;
  ProgressTitleLabel.Left := ScaleX(20);
  ProgressTitleLabel.Top := ScaleY(40);
  ProgressTitleLabel.Visible := False;

  ProgressStatusLabel := TLabel.Create(CardPanel);
  ProgressStatusLabel.Parent := CardPanel;
  ProgressStatusLabel.Caption := CustomMessage('ProgressStatus');
  ProgressStatusLabel.Font.Name := 'Segoe UI';
  ProgressStatusLabel.Font.Size := 9;
  ProgressStatusLabel.Font.Color := $A08A8E;
  ProgressStatusLabel.Left := ScaleX(20);
  ProgressStatusLabel.Top := ScaleY(68);
  ProgressStatusLabel.Visible := False;

  FooterPanel := TPanel.Create(BgPanel);
  FooterPanel.Parent := BgPanel;
  FooterPanel.SetBounds(0, WizardForm.ClientHeight - ScaleY(68), WizardForm.ClientWidth, ScaleY(68));
  FooterPanel.Color := $140D0E;
  FooterPanel.ParentBackground := False;
  FooterPanel.BevelOuter := bvNone;

  FooterTopLine := TPanel.Create(FooterPanel);
  FooterTopLine.Parent := FooterPanel;
  FooterTopLine.SetBounds(0, 0, FooterPanel.Width, ScaleY(1));
  FooterTopLine.Color := $3A272A;
  FooterTopLine.ParentBackground := False;
  FooterTopLine.BevelOuter := bvNone;

  HintLabel := TLabel.Create(FooterPanel);
  HintLabel.Parent := FooterPanel;
  HintLabel.Caption := CustomMessage('QuickHint');
  HintLabel.Font.Name := 'Segoe UI';
  HintLabel.Font.Size := 9;
  HintLabel.Font.Color := $80DE4A;
  HintLabel.Left := ScaleX(24);
  HintLabel.Top := ScaleY(24);

  CancelBtn := TNewButton.Create(FooterPanel);
  CancelBtn.Parent := FooterPanel;
  CancelBtn.SetBounds(FooterPanel.Width - ScaleX(256), ScaleY(16), ScaleX(106), ScaleY(36));
  CancelBtn.Caption := CustomMessage('CancelButton');
  CancelBtn.Font.Name := 'Segoe UI';
  CancelBtn.Font.Size := 9;
  CancelBtn.OnClick := @CancelBtnClick;

  InstallBtn := TNewButton.Create(FooterPanel);
  InstallBtn.Parent := FooterPanel;
  InstallBtn.SetBounds(FooterPanel.Width - ScaleX(142), ScaleY(16), ScaleX(118), ScaleY(36));
  InstallBtn.Caption := CustomMessage('InstallButton');
  InstallBtn.Font.Name := 'Segoe UI';
  InstallBtn.Font.Size := 9;
  InstallBtn.Font.Style := [fsBold];
  InstallBtn.OnClick := @InstallBtnClick;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Exec('regsvr32.exe', '/s "' + ExpandConstant('{app}\softcam.dll') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if LaunchAfterCheck.Checked then
    begin
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

procedure InitializeUninstallProgressForm;
begin
  ApplyModernWindowStyle(UninstallProgressForm.Handle);
end;

// Best-effort retry delete for a file that may still be locked at uninstall
// time. Returns True when the file is gone.
function DeleteLockedFileRetry(const Path: string): Boolean;
var
  i: Integer;
begin
  Result := True;
  for i := 1 to 10 do
  begin
    if not FileExists(Path) then Exit;
    DeleteFile(Path);
    if not FileExists(Path) then Exit;
    Sleep(500);
  end;
  Result := not FileExists(Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  SettingsDir: string;
  AppDllPath: string;
  RegisteredPath: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('taskkill.exe', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // The bundled adb.exe runs as a persistent daemon that survives the app
    // and locks its own files — without this, Tools\ is left behind.
    // Graceful first (other adb clients just reconnect on next use).
    AppDllPath := ExpandConstant('{app}\Tools\adb.exe');
    if FileExists(AppDllPath) then
      Exec(AppDllPath, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Give killed processes a moment to release their file locks (incl. softcam.dll)
    Sleep(1500);

    // Unregister the path the registry actually points at (it may differ from
    // {app} if the app was moved/reinstalled — see VirtualCameraInstaller docs),
    // then the installed copy itself. Exit codes are best-effort here; the
    // explicit DeleteFile fallback in usPostUninstall is the real guarantee.
    AppDllPath := ExpandConstant('{app}\softcam.dll');
    if RegQueryStringValue(HKLM, 'SOFTWARE\Classes\CLSID\{#SoftcamClsid}\InprocServer32', '', RegisteredPath) then
      if (RegisteredPath <> '') and FileExists(RegisteredPath) then
        Exec('regsvr32.exe', '/u /s "' + RegisteredPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if FileExists(AppDllPath) then
      Exec('regsvr32.exe', '/u /s "' + AppDllPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // Fallback: files locked at deletion time are otherwise left behind
    // (e.g. softcam.dll loaded in OBS/Discord/browser). Retry explicitly —
    // by now the filter is unregistered and our own processes are gone.
    // If still locked, schedule reboot deletion so the user has nothing to do.
    AppDllPath := ExpandConstant('{app}\softcam.dll');
    if not DeleteLockedFileRetry(AppDllPath) then
    begin
      if FileExists(AppDllPath) and ScheduleDeleteOnReboot(AppDllPath) then
        MsgBox(CustomMessage('SoftcamRebootNote'), mbInformation, MB_OK)
      else if FileExists(AppDllPath) then
        MsgBox(CustomMessage('SoftcamLockedWarning'), mbInformation, MB_OK);
    end;

    // Same story as softcam.dll above, but for the adb daemon files: if the
    // daemon didn't die on kill-server, its exe/dlls stay locked. Silent
    // reboot-scheduling here — no message, these are our own files.
    if not DeleteLockedFileRetry(ExpandConstant('{app}\Tools\adb.exe')) then
      ScheduleDeleteOnReboot(ExpandConstant('{app}\Tools\adb.exe'));
    if not DeleteLockedFileRetry(ExpandConstant('{app}\Tools\AdbWinApi.dll')) then
      ScheduleDeleteOnReboot(ExpandConstant('{app}\Tools\AdbWinApi.dll'));
    if not DeleteLockedFileRetry(ExpandConstant('{app}\Tools\AdbWinUsbApi.dll')) then
      ScheduleDeleteOnReboot(ExpandConstant('{app}\Tools\AdbWinUsbApi.dll'));

    SettingsDir := ExpandConstant('{userappdata}\{#MyAppName}');
    if DirExists(SettingsDir) then
    begin
      DelTree(SettingsDir, True, True, True);
    end;
  end;
end;
