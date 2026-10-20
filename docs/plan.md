# BlackHole Cursor — 开发执行计划（plan.md）

> 依据 `prd.md` v1.0 编写。按 M0 → M5 顺序执行，每个里程碑结束必须跑通「验收自检」才能进入下一阶段。
> 所有命令在项目根目录 `d:/HaLan/Desktop/鼠标/` 下执行。

---

## 进度状态（2026-09-20）

| 里程碑 | 状态 | 说明 |
|---|---|---|
| M0 环境 / 骨架 | 完成 | `dotnet build -c Release` 通过，0 警告 0 错误 |
| M1 配置 / 托盘 / 单实例 / 主界面外壳 | 完成 | |
| M2 光标替换 + 黑洞渲染 + 预览 | 完成 | 见下方「与计划的偏差」 |
| M3 抓屏 + 引力透镜 | 完成 | DXGI Desktop Duplication 为主，GDI BitBlt 兜底 |
| M4 惯性跟随 + 光标兜底 | 完成 | |
| M5 预设 / 异常降级 / 打包 | 完成 | 已发布为 win-x64 单文件自包含；含 Inno Setup 脚本 |

代码结构：`src/BlackHoleCursor.Core`（渲染 / 抓屏 / 配置，无 UI 依赖）+ `src/BlackHoleCursor`（WPF 外壳）+ `tests`（216 个单元测试全部通过）。
冒烟结果：启动 → 覆盖层线程就绪 → 首帧 ~230ms 出图（360×360，`FlipDiscard/Premultiplied`），日志无异常。

### 修复的两个致命运行时缺陷（实机暴露，非计划内的遗漏）

**① 覆盖层窗口所在的线程没有消息循环 → 点击后「未响应」**

`OverlayRunner` 的后台线程只做 `Tick(); Thread.Sleep(1);`，从不 `PeekMessage/DispatchMessage`。
覆盖层窗口跟着鼠标，鼠标几乎永远在它上面，系统持续向它 `SendMessage(WM_SETCURSOR)`；
该线程不抽消息 → 系统输入线程同步阻塞 → 5 秒后 Windows 判定窗口挂起并生成幽灵窗口。
修复：`Win32OverlayWindow.WaitAndPump()`（`MsgWaitForMultipleObjectsEx` + `PeekMessage` 循环）替换 `Thread.Sleep`。

**② 没有做点击穿透 → 点击被覆盖层吃掉**

只用了 `WS_EX_TRANSPARENT`，它管的是**绘制顺序**而非输入穿透。

修复过程（两次试错，已用 `WindowFromPoint` 逐次实测）：

| 尝试 | 结果 |
|---|---|
| `WM_NCHITTEST` → `HTTRANSPARENT` | 无效。该返回值只在**同一线程内**推迟给下层窗口，覆盖层在自线程里是唯一窗口，命中测试仍以它收尾 |
| `EnableWindow(hwnd, FALSE)` | 无效。`WindowFromPoint` 对顶层禁用窗口仍会返回它 |
| **`WS_EX_LAYERED \| WS_EX_TRANSPARENT`** | **有效**。分层窗口配合 `WS_EX_TRANSPARENT` 才会让命中测试彻底跳过 |

注意：分层窗口默认完全透明，必须补 `SetLayeredWindowAttributes(hwnd, 0, 0xFF, LWA_ALPHA)`，
否则黑洞会看不见；实测与 DirectComposition 合成兼容，渲染正常。

**③ 顺带发现：`ShowCursor(FALSE)` 完全无效**

实测显示计数已到 **-1**，但 `GetCursorInfo` 仍报 `CURSOR_SHOWING=1`，移动鼠标也一样。
原因是它只影响**本进程**的计数，而桌面其它窗口在 `WM_SETCURSOR` 里照样 `SetCursor(箭头)`。
修复：改为用 `SetSystemCursor` 把 13 个标准系统光标替换为**透明光标**（`InvisibleCursor`，
单色 AND=1/XOR=0），退出时用 `SystemParametersInfo(SPI_SETCURSORS)` 重载用户方案。
关键点：`SetSystemCursor` **会接管并销毁传入句柄**，每个 ID 必须传新建的句柄（否则只有第 1 个成功，
后续报 `ERROR_INVALID_CURSOR_HANDLE/1402`）。安全网：启动时自愈重载 + `ProcessExit` 兜底还原。

> 另修：`SystemParametersInfo` 声明缺 `EntryPoint="SystemParametersInfoW"`（user32 只导出 A/W 版本），
> 原本是死代码所以一直没暴露，一调用就 `EntryPointNotFoundException`。

### 与计划的偏差（有意为之，均为等价或更简实现）

- **2.3 输入获取**：未使用 `WH_MOUSE_LL` 全局钩子，改为渲染线程内 `GetCursorPos` 轮询。
  帧级延迟已足够，且不引入「钩子超时被系统静默摘除」的风险。
- **2.6 实时预览**：未使用 `D3DImage`，改为共用**同一个** `SceneRenderer` 与同一套 HLSL 离屏渲染后
  回读到 `WriteableBitmap`。「预览 = 实机」的约束仍然成立，同时避开 D3D9/D3D11 互操作。
- **2.1 锁屏兜底**：未单独注册 `WTSRegisterSessionNotification`。`SHQueryUserNotificationState`
  返回 `QUNS_NOT_PRESENT` 已覆盖「工作站已锁定」，且已有 1 秒轮询兜底。
- **3.3 合成顺序**：Pass A / Pass B 合并为单个像素着色器（`composite.hlsl`）一次绘制完成。

### 已补齐的降级与打磨项（对应 2.1 / 3.4 / 5.2 / 5.3 / 5.4）

- [x] `Present` 失败检测 —— Vortice 的 `Present` 返回 `Result` 而**不抛异常**，原 `try/catch` 是死代码，已改为显式检查返回值
- [x] `DXGI_ERROR_DEVICE_REMOVED / RESET / HUNG` → 自动重建 Device 与全部资源（上限 3 次，超限停止渲染并提示）
- [x] 抓到全黑帧（DRM / HDR）→ 跳过扭曲层，只画黑洞本体（低频采样式检测，避免频繁回读）
- [x] 主界面打开时暂停降帧策略，保证预览与桌面效果同步流畅
- [x] 帧率持续 < 45 FPS 满 3 秒 → 托盘提示建议降级（PRD 1.2：v1.0 不自动降级）
- [x] 渲染彻底不可用时自动恢复系统光标，避免「箭头被隐藏、黑洞又没画出来」的失明状态
- [x] 设备重建时释放旧的 Desktop Duplication，避免继续占用显示输出
- [x] Inno Setup 安装脚本：`installer/BlackHoleCursor.iss`

### 仍未实现（遗留，非 v1.0 阻塞项）

- `Present` 未使用 `DXGI_PRESENT_DO_NOT_WAIT` 主动检测窗口被遮挡
- 安装包需要先执行 `dotnet publish ... -o dist` 再编译 `.iss`（完整命令见脚本头部注释）

### 本机构建命令

本机 `dotnet` 不在 `PATH`，且 SDK 位于用户目录，需先修正环境变量：

```powershell
$env:PATH="C:\Users\HaLan\AppData\Local\Microsoft\dotnet;" + $env:PATH
cd "d:/HaLan/Desktop/鼠标"
dotnet build BlackHoleCursor.sln -c Release
dotnet test  tests/BlackHoleCursor.Tests/BlackHoleCursor.Tests.csproj -c Release
dotnet publish src/BlackHoleCursor/BlackHoleCursor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist
```

调试用：`dist\BlackHoleCursor.exe --no-takeover`（本次运行不隐藏系统光标，便于排查渲染问题）。

---

## 0. 环境与依赖（M0）

### 0.1 前置条件
- [ ] Windows 10 1809+ / Windows 11
- [ ] .NET 8 SDK（`dotnet --version` ≥ 8.0）
- [ ] Visual Studio 2022（含 .NET 桌面开发工作负载）**或** 仅用 CLI + VS Code
- [ ] Windows SDK（提供 `fxc.exe`，用于离线编译 HLSL；路径通常在
      `C:\Program Files (x86)\Windows Kits\10\bin\<version>\x64\fxc.exe`）
- [ ] 支持 Direct3D 11 的显卡

### 0.2 建项目
```powershell
cd "d:/HaLan/Desktop/鼠标"
dotnet new wpf -n BlackHoleCursor -o src/BlackHoleCursor --framework net8.0-windows
dotnet new sln -n BlackHoleCursor
dotnet sln add src/BlackHoleCursor/BlackHoleCursor.csproj
```

### 0.3 NuGet 依赖（一次性装齐）
```powershell
dotnet add src/BlackHoleCursor package Vortice.Direct3D11
dotnet add src/BlackHoleCursor package Vortice.DXGI
dotnet add src/BlackHoleCursor package Vortice.D3DCompiler
dotnet add src/BlackHoleCursor package Hardcodet.NotifyIcon.Wpf
dotnet add src/BlackHoleCursor package CommunityToolkit.Mvvm
```
> 说明：`Vortice.*` 是 SharpDX 的活跃继任者（SharpDX 已停更）。若 Vortice 编译 HLSL 不顺，改用 `fxc.exe` 预编译成 `.cso` 嵌入资源，运行时不依赖 `d3dcompiler_47.dll`。

### 0.4 项目文件关键配置（`BlackHoleCursor.csproj`）
- [ ] `<UseWPF>true</UseWPF>`、`<UseWindowsForms>true</UseWindowsForms>`（托盘/光标 API 需要）
- [ ] `<TargetFramework>net8.0-windows</TargetFramework>`、`<PlatformTarget>x64</PlatformTarget>`
- [ ] `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`（在 `app.manifest` 中声明）
- [ ] 所有坐标统一走**物理像素**，UI 显示时再转 DIP，避免黑洞与热点错位

### 0.5 目录骨架（先建空文件占位）
```
src/BlackHoleCursor/
├── App.xaml
├── Core/
│   ├── SettingsService.cs      配置读写 / 预设 / 开机自启
│   ├── SettingsModel.cs        配置项强类型模型（见 prd.md 第 4 节）
│   ├── CursorManager.cs        光标隐藏 / 兜底 / 全屏检测
│   ├── OverlayWindow.cs        Win32 覆盖层窗口 + D3D11 SwapChain
│   ├── ScreenCapture.cs        局部抓屏（DXGI Desktop Duplication）
│   ├── LensRenderer.cs         引力透镜渲染
│   ├── BlackHoleRenderer.cs    黑洞 + 吸积盘渲染
│   ├── FollowPhysics.cs        F5 惯性跟随
│   └── RenderLoop.cs           统一渲染循环 / 帧率控制
├── Shaders/
│   ├── lens.hlsl
│   ├── blackhole.hlsl
│   └── common.hlsl             共享常量与工具函数
├── UI/
│   ├── MainWindow.xaml / .cs
│   ├── PreviewSurface.cs       D3DImage 预览承载
│   └── Views/
│       ├── AppearanceView.xaml     F1/F2
│       ├── LensView.xaml           F3
│       ├── FollowView.xaml         F5
│       ├── SystemView.xaml         F4
│       └── SettingsView.xaml       F7
└── Resources/
    ├── presets.json
    └── tray.ico
```

**M0 验收**：`dotnet build` 通过，空窗口能启动。

---

## 1. M1 — 骨架 / 配置 / 托盘 / 单实例

### 1.1 `SettingsModel.cs` + `SettingsService.cs`
- [ ] 按 `prd.md` 第 4 节建强类型配置类，实现 `INotifyPropertyChanged`
- [ ] 持久化到 `%LOCALAPPDATA%\BlackHoleCursor\settings.json`（`System.Text.Json`，写入用临时文件 + 原子替换）
- [ ] 加载时做**范围钳制**（防止手改 JSON 导致崩溃）
- [ ] 属性变更后 **debounce 300ms** 自动保存
- [ ] 开机自启：读写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（无需管理员）

### 1.2 单实例
- [ ] `Mutex("Global\BlackHoleCursor_SingleInstance")`，已运行则通过 `WM_SHOWME` 唤醒已有窗口后退出

### 1.3 托盘常驻
- [ ] `Hardcodet.NotifyIcon.Wpf`：`开关黑洞 / 打开主界面 / 退出`
- [ ] 主界面 `Closing` 事件 `e.Cancel = true` + `Hide()`（真正退出只走托盘菜单）
- [ ] 托盘图标随 `Enabled` 状态切换（亮/暗）

### 1.4 主界面外壳
- [ ] 左侧导航（外观 / 扭曲 / 跟随 / 系统 / 设置）+ 右侧内容区 + 顶部总开关
- [ ] 深色玻璃主题（Mica/纯色 fallback）：背景 `#FF0B0B10`、卡片 `#18FFFFFF`、圆角 12、微光边框
- [ ] 底部：`保存 / 重置本页 / 恢复默认`

**M1 验收**：启动→托盘出现；关窗不退出；改任一参数→关程序→重开，值被保留；自启开关写注册表生效。

---

## 2. M2 — 光标替换 + 黑洞渲染 + 预览（F1 / F2 / F6）

### 2.1 `CursorManager.cs`
- [ ] 隐藏：`ShowCursor(FALSE)`，内部维护 `bool _hidden` 保证**幂等**（避免多次调用失衡）
- [ ] 恢复：`ShowCursor(TRUE)`，仅在 `_hidden` 时调用
- [ ] 兜底触发条件（任一命中即恢复系统箭头）：
  - `SHQueryUserNotificationState` 返回 `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_BUSY`
  - 会话锁定（`WTSRegisterSessionNotification` → `WTS_SESSION_LOCK`）
  - 覆盖层窗口连续 N 帧 Present 失败
- [ ] 退出/禁用时**必须**恢复系统箭头（在 `App.Exit` + `finally` 双保险）
- [ ] 备用方案（若 `ShowCursor` 在部分 UWP 上失效）：`SetSystemCursor` 载入 1×1 透明光标，退出时 `SystemParametersInfo(SPI_SETCURSORS)` 还原

### 2.2 `OverlayWindow.cs`（Win32 窗口，不用 WPF）
- [ ] `CreateWindowEx`：`WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED`（按需）
  - `WS_EX_TRANSPARENT` → 鼠标事件穿透，不影响任何点击
- [ ] 尺寸 = 每帧跟随黑洞位置的小窗口（推荐）**或** 全屏窗口只画局部（备选，见风险表）
  - **首选**：窗口大小 = `2 × LensRadius`（上限 1024），每帧 `MoveWindow` 到黑洞周围
    - 优点：像素填充少、性能好；缺点：频繁 MoveWindow 可能有抖动 → 用 `SetWindowPos(SWP_NOREDRAW)` 并量化到偶数像素
- [ ] 创建 D3D11 Device + SwapChain（`DXGI_FORMAT_B8G8R8A8_UNORM`，`AlphaMode = Premultiplied`）
- [ ] 每帧 `Present(1, 0)`；`DXGI_PRESENT_DO_NOT_WAIT` 检测遮挡

### 2.3 输入获取
- [ ] 用 `SetWindowsHookEx(WH_MOUSE_LL)` 取真实鼠标坐标（比轮询 `GetCursorPos` 更及时且能用于唤醒渲染）
- [ ] 坐标转物理像素（按所在显示器 DPI 缩放）
- [ ] 全屏应用下钩子仍工作（用于兜底判定）

### 2.4 `blackhole.hlsl` — 黑洞本体
着色器输入常量（`common.hlsl` 共享）：
```
cbuffer Params {
  float2 Center;          // 黑洞中心（窗口局部坐标）
  float  HorizonRadius;   // R_h
  float  RingRadius;      // R_r
  float  RingThickness;
  float  RingTiltCos, RingTiltSin;   // 倾斜旋转
  float  SpinPhase;                  // 由 SpinSpeed * time 累积
  float3 RingColorInner, RingColorOuter;
  float  GlowIntensity, RingOpacity, GradientEnabled;
  float  Time;
}
```
- [ ] **事件视界**：`r < HorizonRadius` → 纯黑；`r` 接近 `HorizonRadius` 时叠加 1–2px 冷色轮廓光（防止黑背景下"消失"）
- [ ] **吸积盘**：把局部坐标做 `y /= cos(tilt)` 逆透视 → 得盘面极坐标 `(ρ, θ)`；
      `ρ ∈ [RingRadius, RingRadius+Thickness]` 时着色，颜色 `lerp(Inner, Outer, t)`，`t = (ρ-R)/Thickness`
      - 加 `θ + SpinPhase` 的角向噪声（value noise 即可）产生流动质感
      - 前后遮挡：盘面被视界遮挡的部分（透视上位于黑洞后方且被遮挡）需裁剪
- [ ] **光子环**：`r ≈ HorizonRadius × 1.35` 处一条细亮环，`smoothstep` 软化
- [ ] **光晕**：`exp(-(r-R)/k)` 多段叠加 × `GlowIntensity`，输出 premultiplied alpha
- [ ] 输出 premultiplied alpha，与底层画面 `SrcAlpha/OneMinusSrcAlpha` 混合

### 2.5 参数联动
- [ ] `GlobalScale` 统一乘到 `HorizonRadius / RingRadius / RingThickness`
- [ ] `SpinSpeed = 0` → 跳过时间累积，渲染进入"静态模式"（可降帧）

### 2.6 `PreviewSurface.cs` 实时预览
- [ ] 用 `D3DImage` 承载**同一个** D3D11 设备与**同一个** `blackhole.hlsl`，保证预览 = 实机效果
- [ ] 预览区背景：深色网格 + 一张测试图（便于观察吸积盘发光）
- [ ] 预览里黑洞以缓慢 Lissajous 轨迹自动移动，方便观察渲染；参数变更 <100ms 内反映

**M2 验收**：桌面箭头消失，出现带旋转吸积盘的黑洞；中心可正常点击任意窗口；主界面改颜色/半径/粗细/倾斜/转速，桌面与预览**同时即时**变化；关掉「隐藏系统光标」箭头立刻回来。

---

## 3. M3 — 引力透镜扭曲（F3，最难，实测驱动）

### 3.1 `ScreenCapture.cs` — 局部抓屏
- [ ] 主方案：**DXGI Desktop Duplication**（`IDXGIOutput1::DuplicateOutput`）
  - 每帧 `AcquireNextFrame(0)` → `CopySubresourceRegion` 拷贝「黑洞周围 `2×LensRadius`」矩形到 staging/直接到 shader 资源
  - 超时（`DXGI_ERROR_WAIT_TIMEOUT`）→ 复用上一帧纹理，不阻塞
  - 失败（`DXGI_ERROR_ACCESS_LOST`，如分辨率/显卡切换）→ 重建 duplication 对象
- [ ] 降级方案：`BitBlt`（`GetDC(NULL)` + `CreateCompatibleBitmap`）→ 慢但通用，作为 fallback 开关
- [ ] 只抓**需要的局部矩形**，绝不整屏拷贝
- [ ] 多显示器：按黑洞所在显示器选择对应 `IDXGIOutput`

### 3.2 `lens.hlsl` — 引力透镜
```
float2 d = P - Center;
float  r = max(length(d), 1.0);
float  Rh = HorizonRadius;

// 光线偏折近似：位移量 ∝ Rh^2 / r（弱场近似，够用且稳定）
float  defl = LensStrength * Rh * Rh / r;

// 关键：向"中心外侧"采样 —— 使外侧画面被拉向黑洞，形成环状拉伸
float2 uv = P + normalize(d) * defl;
```
- [ ] `r < Rh`：输出**纯黑**（事件视界），边缘留 1px 过渡避免锯齿
- [ ] 爱因斯坦环：`r ∈ [Rh*1.2, Rh*1.6]` 提亮（模拟光子环聚光），强度随 `LensStrength`
- [ ] 影响半径外渐隐：`w = 1.0 - smoothstep(LensRadius*0.65, LensRadius, r)`，最终 `lerp(原始画面, 扭曲画面, w)`，保证边界无硬切
- [ ] 采样用 `SamplerState` 的 `Clamp` + 双线性，避免边缘拉花
- [ ] 用 `ddx/ddy` 或 mip 选择抑制高扭曲区的采样噪点

### 3.3 合成顺序（每帧）
1. 抓屏（局部）→ `ScreenTex`
2. Pass A：全屏四边形采样 `ScreenTex`，跑 `lens.hlsl` → 扭曲后的底图
3. Pass B：在扭曲结果上叠加 `blackhole.hlsl`（premultiplied alpha）
4. `Present`

### 3.4 性能控制
- [ ] 局部矩形上限 1024×1024；超过则钳制 `LensRadius`
- [ ] 鼠标静止 + `SpinSpeed=0` + `LensStrength` 未变 → 降到 30 FPS 或暂停渲染
- [ ] 帧间隔 `dt` 上限裁剪 50ms（防止卡顿后瞬移）
- [ ] 帧率采样（60 帧滑窗），低于 45 FPS 持续 3 秒 → 弹提示建议降级（**v1.0 不自动降级**，按 PRD 1.2）

**M3 验收**：鼠标移动时周围画面出现径向拉伸 + 环状亮圈，随鼠标平滑移动；`LensStrength=0` 完全无扭曲；拖满 `LensRadius` 时帧率仍 ≥ 55 FPS；`dotnet build` 无警告堆积。

---

## 4. M4 — 惯性跟随物理（F5）+ 光标兜底（F4）

### 4.1 `FollowPhysics.cs`
```csharp
// 每帧调用，dt 单位秒（已裁剪）
Vector2 delta = target - pos;
Vector2 a = delta * Stiffness - vel * Damping;

if (AdaptiveSpeed)  // 距离越远限速越高
    vMax = MaxSpeed * clamp(1 + delta.Length() / 400.0, 1, 3);
else
    vMax = MaxSpeed;

if (a.Length() > MaxAccel) a = a.Normalized() * MaxAccel;

vel += a * dt;
if (vel.Length() > vMax) vel = vel.Normalized() * vMax;

pos += vel * dt;

if (delta.Length() < SnapDistance && vel.Length() < SnapDistance * 60) {
    pos = target; vel = Vector2.Zero;      // 吸附，消除亚像素抖动
}
```
- [ ] 关闭（`FollowEnabled=false`）时直接 `pos = target`，零开销
- [ ] 开启时**渲染用 `pos`，点击热点仍用 `target`**（热点由系统光标决定，天然满足）
- [ ] 参数改动即时生效，无需重启
- [ ] 建议 UI 里给 `Stiffness/Damping` 配一个"欠阻尼 / 临界 / 过阻尼"三档快捷按钮，降低调参门槛

### 4.2 兜底完善（F4）
- [ ] 主界面「系统」页：隐藏系统光标开关、智能兜底开关、当前状态指示（正常 / 已兜底）
- [ ] 兜底触发时托盘气泡提示一次（可关闭）
- [ ] 全屏退出后自动恢复黑洞（轮询 `SHQueryUserNotificationState`，1s 间隔）

**M4 验收**：开启跟随 + 低限速后甩动鼠标，黑洞明显落后并加速追赶，停手后平滑收敛无抖动；任何状态下点击位置都正确；进全屏游戏箭头自动回来，退出后黑洞恢复。

---

## 5. M5 — 打磨、预设、异常与打包

### 5.1 预设
- [ ] `presets.json` 内置：经典橙 / 蓝矮星 / 冷白 / 深空紫
- [ ] 一键应用、另存为自定义预设、删除自定义预设

### 5.2 异常处理与降级矩阵
| 故障 | 行为 |
|---|---|
| D3D11 设备创建失败 | 提示"需要支持 Direct3D 11 的显卡"，程序以托盘运行但不渲染 |
| Desktop Duplication 初始化失败 | 自动切 BitBlt；再失败则关闭扭曲层，仅保留黑洞图标 |
| `DXGI_ERROR_ACCESS_LOST` | 重建 duplication，最多重试 3 次，之后降级 |
| 抓到全黑帧（DRM/HDR 窗口） | 该帧跳过扭曲，只画黑洞 |
| 设备丢失（`DXGI_ERROR_DEVICE_REMOVED`） | 重建 Device + 所有资源 |
| 配置 JSON 损坏 | 备份损坏文件 → 载入默认值 → 提示一次 |

### 5.3 细节打磨
- [ ] 渲染循环与 UI 线程完全分离（渲染在独立线程 + `CompositionTarget.Rendering` 二选一，建议独立线程）
- [ ] 启动 300ms 内首帧出图（避免开机后一段时间没光标）
- [ ] 主界面打开时自动暂停"降帧策略"，保证预览流畅
- [ ] 全局异常捕获（`App.DispatcherUnhandledException` + `AppDomain.UnhandledException`）→ 写日志 + **务必恢复系统光标**

### 5.4 发布
- [ ] `dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`
- [ ] 可选：Inno Setup 打包 + 卸载入口

**M5 验收**：跑完 `prd.md` 第 7 节全部 7 条验收标准。

---

## 6. 风险登记与应对（开发时逐条落实）

| # | 风险 | 触发表现 | 应对（已写入对应里程碑） |
|---|---|---|---|
| R1 | 频繁 `MoveWindow` 导致覆盖层抖动/闪烁 | 快速移动时窗口拖影 | 位置量化到偶数像素 + `SWP_NOREDRAW`；不行则改为全屏窗口只画局部（M2 备选） |
| R2 | Desktop Duplication 与渲染不在同一 GPU | 抓屏失败或黑屏 | 用渲染 Device 的 Adapter 枚举 Output 创建 duplication（M3） |
| R3 | `ShowCursor` 计数失衡 | 关闭程序后光标仍不可见 | 幂等封装 + 退出双保险 + 全局异常处理中恢复（M2/M5） |
| R4 | 多 DPI 显示器坐标错位 | 第二屏上黑洞偏移 | 全部走物理像素，`MonitorFromPoint` 取该屏 DPI（M2） |
| R5 | HDR / DRM 内容抓屏为黑 | 扭曲区发黑 | 检测全黑帧跳过扭曲（M5） |
| R6 | 透镜数学过强导致画面撕裂 | 高 `LensStrength` 时采样乱飞 | 对 `defl` 做 `min(defl, r*0.9)` 钳制，禁止越过中心（M3） |
| R7 | 惯性跟随手感迟钝 | 用户抱怨不跟手 | 默认关闭 + 三档快捷预设 + 可调参数（M4） |

---

## 7. 每里程碑提交规范

- 一个里程碑一个提交：`feat(M1): 骨架与配置`、`feat(M2): 黑洞渲染` …
- 每个里程碑结束执行：
```powershell
dotnet build -c Release
# 手工冒烟：启动 → 拖动 → 开关各功能 → 关闭主界面 → 退出 → 确认系统光标已恢复
```
- **任何一次退出后必须确认系统箭头回来了**，这是最高优先级回归点。

---

## 8. 执行顺序速查

```
M0 环境/骨架
 └─ M1 配置 + 托盘 + 主界面外壳
     └─ M2 光标替换 + 黑洞/吸积盘渲染 + D3DImage 预览
         ├─ M3 抓屏 + 引力透镜扭曲      ← 技术难点，实测驱动
         └─ M4 惯性跟随 + 光标兜底      ← 可与 M3 并行开发（无耦合）
             └─ M5 预设 + 异常降级 + 打包
```
