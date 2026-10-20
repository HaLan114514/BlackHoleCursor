; ---------------------------------------------------------------------------
; BlackHole Cursor — Inno Setup 安装脚本（plan.md 5.4）
;
; 打包步骤：
;   1) 在项目根目录发布单文件自包含版本：
;        dotnet publish src/BlackHoleCursor/BlackHoleCursor.csproj ^
;          -c Release -r win-x64 --self-contained true ^
;          /p:PublishSingleFile=true ^
;          -o dist
;      说明：WPF 的原生依赖（wpfgfx_cor3.dll / PresentationNative_cor3.dll 等）
;      会与主 exe 并列输出，属于正常现象，必须一并安装。
;   2) 用 Inno Setup 6 编译本脚本（图形界面或命令行 iscc.exe）：
;        iscc installer\BlackHoleCursor.iss
;   3) 产物：installer\Output\BlackHoleCursor-Setup-4.0.0.exe
;
; 卸载：控制面板 → 应用 → BlackHole Cursor。
;       用户配置保留在 %LOCALAPPDATA%\BlackHoleCursor\，卸载默认不删除。
; ---------------------------------------------------------------------------

#define AppName "BlackHole Cursor"
#define AppExeName "BlackHoleCursor.exe"
; v4（V4-8）：版本号 1.0.0 → 4.0.0，与主界面显示的 CURSOR v4.0 保持一致
#define AppVersion "4.0.0"
#define AppPublisher "BlackHole Cursor"
#define SourceDir "..\dist"

[Setup]
AppId={{8F3B7C51-2E4A-4C8E-9B7D-1A6F5C2D9E30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=BlackHoleCursor-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 10 1809 及以上（plan.md 0.1）
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 普通用户权限即可安装与运行（PRD 权限要求）
PrivilegesRequired=lowest
; v4（V4-8）：安装向导 / 卸载程序条目显示的图标（由 tools/IconGen 生成）
SetupIconFile=..\src\BlackHoleCursor\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
DisableReadyPage=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 与应用内「开机自启」开关使用同一个 Run 项，避免两处状态不一致
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "BlackHoleCursor"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先结束进程并恢复系统光标，避免用户「失去鼠标」
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "KillApp"
