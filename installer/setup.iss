; ============================================================================
; «Просмотрщик LAS» — установщик Inno Setup для всех пользователей
; Собирается scripts\Build-Installer.ps1, который передаёт MyAppVersion.
; ============================================================================

#define MyAppId          "{{F1CEB470-E4CA-4AB8-B88E-0580C248534B}"
#define MyAppName        "Просмотрщик LAS"
#define MyAppExeName     "LasViewer.exe"
#define MyAppIconName    "LasViewer.ico"
#define MyAppPublisher   'ООО НПФ "АМК ГОРИЗОНТ"'
#define MyAppDescription "Просмотр, обработка и анализ LAS-файлов геофизических исследований скважин"
#define MyAppCopyright   'Copyright (C) 2026 ООО НПФ "АМК ГОРИЗОНТ"'
#define PublishDir       "..\artifacts\publish\win-x64"
#define AppIconPath      "..\LasViewer\Assets\LasViewer.ico"

#ifndef MyAppVersion
  #error MyAppVersion не задан: собирайте установщик через scripts\Build-Installer.ps1
#endif

[Setup]
; AppId совпадает с ключом удаления, который читает AutoUpdateService.
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
AppUpdatesURL=https://amk-horizon.ru/downloads/las-viewer/

PrivilegesRequired=admin
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows App SDK требует Windows 10 1809 (сборка 17763) — то же значение, что
; TargetPlatformMinVersion проекта.
MinVersion=10.0.17763

CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

OutputDir=Output
OutputBaseFilename=LasViewer-Setup-{#MyAppVersion}
SetupIconFile={#AppIconPath}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
ShowLanguageDialog=auto
SetupLogging=yes
Uninstallable=yes
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoDescription=Установщик приложения «{#MyAppName}»

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Создать ярлык в меню «Пуск»"; GroupDescription: "Ярлыки приложения:"; Flags: checkedonce
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки приложения:"; Flags: checkedonce

[InstallDelete]
; Пакет движка и встроенный Python заменяются целиком: модуль, удалённый из
; новой версии, не должен остаться от старой и подгрузиться вместо нового.
Type: filesandordirs; Name: "{app}\engine"
Type: filesandordirs; Name: "{app}\python"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\{#MyAppIconName}"; Comment: "{#MyAppDescription}"; Tasks: startmenuicon
Name: "{commonprograms}\{#MyAppName}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\{#MyAppIconName}"; Comment: "{#MyAppDescription}"; Tasks: desktopicon

[Run]
; Приложение запускается через explorer.exe, а не напрямую. Установщик работает
; с правами администратора, и запущенное им приложение унаследовало бы их; в
; таком процессе Windows не открывает диалоги выбора файлов. Флаг
; runasoriginaluser не помогает, когда setup запущен уже повышенным — а именно
; так его запускает автообновление (подробно — в установщике «Визуализации данных»).
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#MyAppExeName}"""; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent
; Автообновление запускает setup с /UPDATE=1: после тихой установки приложение
; стартует заново.
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#MyAppExeName}"""; Flags: nowait; Check: IsAutoUpdateMode

[Code]
function IsAutoUpdateMode: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:update|0}'), '1') = 0;
end;
