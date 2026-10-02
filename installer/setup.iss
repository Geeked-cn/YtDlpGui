; 视频下载器 · yt-dlp 安装程序脚本（Inno Setup 7）
; 编译：ISCC.exe installer\setup.iss  （需先 dotnet build -c Release）

#define MyAppName "视频下载器"
#define MyAppNameEn "YtDlpGui"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "YtDlpGui"
#define MyAppExeName "YtDlpGui.exe"
#define AppOut "..\app\YtDlpGui\bin\Release\net8.0-windows10.0.19041.0\win-x64"

[Setup]
AppId={{8A5B3C21-6F4E-4B9A-9C7D-2E1F0A5D6B8C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Geeked-cn/YtDlpGui
AppSupportURL=https://github.com/Geeked-cn/YtDlpGui
DefaultDirName={localappdata}\Programs\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=YtDlpGui-{#MyAppVersion}-setup
SetupIconFile=app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 按用户安装（无需管理员），不写注册表 Run 键之外的系统位置
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序
ChangesAssociations=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 应用全部输出（含自包含 WinAppSDK 运行时、resources.pri、ytdlp_host.dll、tools\ 捆绑组件）
Source: "{#AppOut}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清理运行期生成的配置
Type: filesandordirs; Name: "{localappdata}\YtDlpGui"
