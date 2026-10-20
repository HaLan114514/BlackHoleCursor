# 开发计划：抓屏改用 Windows.Graphics.Capture（plan-wgc.md）

> **目标**：解决 `fixes-v3.md` **问题 4** —— 透镜折射过的区域**变化速度跟不上**画面其它部分
> （滚动滚轮时肉眼可见的割裂感）。
>
> **状态（2026-09-27）**：计划已定，**尚未写任何代码**。
> 本文是后续开发的依据。回滚点见文末。
>
> 依据：`fixes-v3.md` 问题 4、`plan-v3.md`（P0 实测数据）、`prd-v2.md`（最低系统要求）。

---

## 1. 为什么必须换抓屏

```
画面其它部分：  DWM 直接合成                          → 实时
折射区：        GDI BitBlt 抓屏 → 上传 → 渲染 → 合成
                 └ 每次要付一次 DWM 合成同步等待（实测 ~17ms）
```

- 这 17ms **与面积无关、与是否复用 DC 无关**（「半分辨率抓屏」已实测证伪），
  是**等合成器**，不是拷像素。
- 抓屏搬后台线程（fixes-v2 问题 7）只解决了「**阻塞**主循环」，**没有缩短延迟** ——
  画出来的仍是旧帧。`plan-v3` P0 实测整帧只有 1.68–4.60ms、57 FPS，帧率完全没问题，
  滞后的只是**内容年龄**。
- 折射区画的是旧桌面，旁边是我们透出去的实时桌面 ⇒ 并排看出慢半拍；
  滚轮时内容变化快，就成了割裂。

**已排除的其它方向**（`fixes-v3` 问题 4 有记录）：限制 alpha 范围无效 ——
折射区那一块照样滞后，割裂不会消失。

**预期收益（要实测，不是承诺）**：内容年龄从「约 2 帧」降到「约 1 帧」。
⚠️ 抓屏到显示永远至少一帧（16.7ms @60Hz），WGC 也突破不了合成器节奏 ——
**是明显改善，不是彻底消除**。

---

## 2. 决策快照（2026-09-27，你定的）

| # | 决策 |
|---|---|
| 1 | **上 WGC**，接受把最低系统要求从 Win10 1809 抬到 **2004（build 19041）** 一档 |
| 2 | **分三步走，先用最低成本的探针证伪**：探针跑完拿到数据，再决定是否写那 200–350 行 |
| 3 | 探针放在**工作区内 `tools/`**（不像上次丢在桌面外 `d:\桌面\wgc-probe`，那个已删） |
| 4 | 探针期间**不动主项目的 TFM** |

---

## 3. 现状：抓屏链路

```
OverlayController.Tick
  └ TryRefreshBackground(rect)
      ├ 建/复用抓屏纹理（整块显示器大小，锚在显示器坐标）
      ├ _background != null ? 后台线程路径（SetRegion + TryUpload）
      │                      : _capture.TryCapture(...)   ← 同步路径
      └ _capture.IsDegraded 时启动后台抓屏
```

| 类型 | 角色 |
|---|---|
| `IDesktopCapture` | 抓屏实现接口：`Name` / `IsAvailable` / `Recreate()` / `SelectOutput(x,y)` / `TryCapture(ctx, dest, left, top, w, h, destX, destY)` / `ReleaseFrame()` |
| `CaptureCoordinator` | 重试 + 重建 + 降级。**两级**：`primary` = `DxgiDesktopCapture`，`fallback` = `GdiDesktopCapture`；`IsDegraded` ⇒ 已切到 GDI |
| `DxgiDesktopCapture` | DXGI Desktop Duplication → 本机 `DuplicateOutput` 返回 `0x887A0004 UNSUPPORTED`（硬件层面不支持） |
| `GdiDesktopCapture` | `Graphics.CopyFromScreen`（BitBlt），复用 Bitmap，局部 Box 增量更新 |
| `BackgroundGdiCapture` | GDI 搬到独立线程：`SetRegion` / `TryUpload` / `LastCostMs`；`IsDegraded` 时启用 |

**P2 之后的重要简化**：窗口 == 整块显示器 ⇒ **抓屏区域恒等于整屏**
（`CaptureMarginFactor` 已删）。这对 WGC 是好事：帧池尺寸恒定，不用跟着窗口变。

---

## 4. 分步计划

### W0 · 探针（约半小时，零风险，先做这个）

**新增** `tools/WgcProbe/`（控制台，**版本化 TFM**，主项目一个字都不动）。

只回答三个问题，每个都要有明确判据：

| 问题 | 判据 |
|---|---|
| ① 改了 TFM 后 `GraphicsCaptureItem.CreateForMonitor(hmon)` 还报不报 `E_NOINTERFACE` | 打印 HRESULT；成功则继续，失败则记录具体错误码 |
| ② 能不能真的**建帧池拿到帧** | 连续拿到 N 帧（目标 ≥ 30），打印每帧尺寸/时间戳 |
| ③ **实测延迟**是多少（对比 GDI 的 ~17ms） | 帧到达时间戳 vs 合成时间戳；再跑一遍 GDI 做对照组 |
| ④ WGC 抓显示器时**会不会抓到我们自己的覆盖层** | 探针窗口设 `WDA_EXCLUDEFROMCAPTURE`，看抓到的帧里有没有它（沿用 P0 的洋红对照法） |

**产出**：把结果写进 `fixes-v3.md` 问题 4。
**判定**：② 不成立 ⇒ **后面全部不做**，问题 4 维持暂缓，本文标记为「已证伪」。

> 探针项目要点（上次踩过的坑）：
> - TFM 必须是 `net8.0-windows10.0.19041.0` 一带，WinRT 互操作才有
> - 控制台项目要显式引 `System.Drawing.Common`（若用到）
> - 找仓库根用「往上找有 sln 的那一层」，别数 `..\`

### ✅ W1 已完成（2026-09-27）：TFM 变更，一次通过

**改动**：三个 csproj 的 `net8.0-windows` → **`net8.0-windows10.0.19041.0`**
（`BlackHoleCursor.Core` / `BlackHoleCursor` / `BlackHoleCursor.Tests`）。
`tools/IconGen` 不动（它不需要 WinRT）。

**验收**

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 0 错误** ✅ |
| `dotnet test`（638 条） | **638 通过 / 0 失败** ✅ |
| CA1416 平台警告 | **一条都没新增** ✅（项目维持 0 警告标准） |

**配套项的结论（两条都不用改，但要记下来）**

1. `app.manifest` **早就声明了** Win10 的 `supportedOS`（`{8e0f7a12-...}`），无需改动。
2. ⚠️ **「最低系统要求抬高」的准确含义**：抬的是「**WGC 这条路径能不能用**」，
   **不是「老系统上拒绝启动」**。
   老系统（< build 19041）上 WGC 激活会失败 ⇒ 由 `CaptureCoordinator` **降级到 GDI**，程序照常跑。
   ⇒ **W2 必须把 WGC 的创建全部包在 try/catch 里**，让失败表现为「不可用」而不是「崩溃」。
   这是 W2 的硬性要求，不是可选优化。

**未测**：装了 WinRT 投影后包体积 / 启动时间的变化（W6 发布时一并量）。

### ✅ W2 已完成（2026-09-27）：`WgcDesktopCapture` **拿到帧了**

**改动**：新增 `src/BlackHoleCursor.Core/Capture/WgcDesktopCapture.cs`（约 570 行），实现 `IDesktopCapture`。
五步初始化 + 取纹理全部跑通，实机连续出帧（**抓屏失败 0 次**）。

#### 四个踩坑（每一个都卡住过，全部记下来）

**① `IID_ID3D11Texture2D` 的 GUID 写错了 —— 这是卡住的直接原因**

```
错：6F15AAF2-A208-4E52-9AC1-576A9FE2A29C   ← 只有第一段是对的
对：6F15AAF2-D208-4E89-9AB4-489535D34F9C   ← d3d11.h 的 IID_ID3D11Texture2D
```
症状是 QI 一律 `E_NOINTERFACE`，**错误信息完全指不到"GUID 写错"**。
判定方法：拿 `IInspectable` 当对照组（每个 WinRT 对象必然实现它）——
它若成功而目标接口失败，才是"接口/IID 有问题"。

**② 诊断块对 marshaler 借出的指针多放了一次 `Marshal.Release`**

`WinRT.MarshalInspectable<...>.CreateMarshaler(obj).ThisPtr` 是**借**的，不是 AddRef 给调用方的。
多放一次把引用计数弄坏 ⇒ 后面再建 marshaler 直接抛 `InvalidCastException`，
看起来像"接口不存在"，极具误导性（当时三条路**一条日志都没来得及打**）。

**③ Vortice 的 COM 类型是**类**不是接口，不能 `__ComObject` 强转**

```csharp
// ❌ 必抛：Unable to cast COM object of type 'System.__ComObject' to class type 'Vortice.Direct3D11.ID3D11Texture2D'
(ID3D11Texture2D)Marshal.GetObjectForIUnknown(ptr)

// ✅ 正确：new 出来即拥有该引用（dispose 时 Release）
new ID3D11Texture2D(ptr)
```
（DXGI 那条路用的是 `resource.QueryInterface<ID3D11Texture2D>()`，同理。）
⚠️ 一旦交给 Vortice 托管，**外面就不能再 `Marshal.Release(ptr)`**，否则多放一次。

**④ 诊断日志每帧两行 → 撞上 `RuntimeLog` 的 512KB 上限**

跑 20 秒就把日志清空了，`帧耗时` 这些真正要看的全被冲掉。
⇒ 诊断与"走的哪条路"都改成**只记一次**（`_diagnosticsLogged` / `_routeLogged`）。

#### 实机数据（1920×1080，`--no-takeover`，26 秒）

| 项 | GDI（改造前） | **WGC** |
|---|---|---|
| 抓屏降级 | True | **False**（走 WGC，不再降级） |
| 抓屏耗时 | 0.47 ~ 1.05 ms | **0.15 ~ 0.36 ms** |
| 整帧 | 1.01 ~ 2.11 ms | **0.85 ~ 1.35 ms** |
| 帧率 | 57.3 FPS | 55.7 ~ 57.2 FPS |

> ⚠️ 抓屏耗时降了 2–4 倍，但这只是"我们这一侧的开销"。
> **真正的主判据是内容年龄（U3）**，得你实机滚滚轮看折射区还慢不慢 —— 见 §5。

### ~~W2 · `WgcDesktopCapture`（核心，中～大）~~（已完成，见上）

新增 `src/BlackHoleCursor.Core/Capture/WgcDesktopCapture.cs`，实现 `IDesktopCapture`。

关键步骤：
1. `GraphicsCaptureSession.IsSupported()` 判可用性（P0 已知 = True）
2. `GraphicsCaptureItem.CreateFromDisplayId / CreateForMonitor` 拿到显示器 item
3. `Direct3D11CaptureFramePool.Create(device, B8G8R8A8UIntNormalized, bufferCount, monitorSize)`
4. `framePool.FrameArrived += ...` 回调里 `TryGetNextFrame()`
5. 从 `frame.Surface` 经 **`IDirect3DDxgiInterfaceAccess`** 取出 `ID3D11Texture2D`
6. `CopySubresourceRegion` 拷进我们的抓屏纹理（**同设备 ⇒ GPU 内拷，CPU 不参与**）
7. `session.IsCursorCaptureEnabled = false`（我们隐藏系统光标，画面里不该出现它）
8. `Recreate()` = 重建 session + frame pool（供 `CaptureCoordinator` 重试调用）

帧池尺寸：**整块显示器**（P2 之后区域恒定，不用跟着窗口变）。

### ✅ W3 已完成（2026-09-27）：接入 `CaptureCoordinator`

**改动**（`OverlayController.cs`，**未提交**）：默认抓屏链
`new CaptureCoordinator(new DxgiDesktopCapture(...), new GdiDesktopCapture())`
→ `new CaptureCoordinator(new WgcDesktopCapture(_device.Device), new GdiDesktopCapture())`。

**W3 那条顾虑已确认不用改**：后台 GDI 抓屏的触发条件是 `if (_background is null && _capture.IsDegraded)`，
WGC 成功时 `IsDegraded = false` ⇒ **不会**启动后台 GDI，两套抓屏不会并存。
实机日志里也没有出现「抓屏已切到后台线程」这条 ⇒ 与判断一致。

**降级仍然有效**：WGC 失败时 `IsDegraded` 为 true ⇒ 切 GDI，程序不崩（W1 定的硬性要求）。

改成 `primary = WgcDesktopCapture`、`fallback = GdiDesktopCapture`。
现有两级结构够用，不必扩成三级（DXGI 在本机已证伪，留着只是噪声）。

⚠️ 注意：WGC 成功时**不应再启动** `BackgroundGdiCapture`（WGC 本身回调驱动、不阻塞），
否则两套抓屏并存会互相打架。`StartBackgroundCapture` 的触发条件要跟着收窄。

### W4 · 锁屏 / 会话切换的恢复

⚠️ **这条最容易漏**：WGC 在**会话锁定**或**无桌面会话**时不可用。
不处理的话：锁屏再回来，抓屏挂掉且不会自愈。

做法：`CaptureStatus.AccessLost` / 会话变更时 `Recreate()`，
并把「探测到会话恢复」接进现有的重试链路（`CaptureCoordinator` 已有 `MaxRecreateAttempts = 3`，
可能需要放宽或改判据）。

### W5 · 测试

WGC 无法在单测里真跑（依赖真实桌面会话），所以断言只能守住**边界**：

- WGC 不可用时**安全降级**到 GDI，程序不崩（可单测：注入一个假的 WGC）
- 降级后 `IsDegraded` 语义不变，现有降级断言保持绿
- 帧池尺寸 == 显示器尺寸（纯函数部分可单测）
- `IsCursorCaptureEnabled` 确实设了 false（源码/行为断言）

**实机验收**才是主判据，见 §6。

### ✅ W7 已实现（2026-09-27）：**整屏不透明** —— 抄参考实现，根治相对滞后

> 你确认的现象：**只有折射区慢一拍** ⇒ 是**相对滞后**，不是整体滞后。

**根因（补上 W2/W3 之后仍存在的那一刀）**

```
折射区        = 我们画的抓屏内容（天生老一帧）
屏幕外围/四角 = 透传的真实桌面（DWM 实时合成）   ← 这就是「实时参照物」
```
两者并排 ⇒ 中间的滞后被衬出来。而让外围保持透传的，是 `composite.hlsl` 末尾那次**边缘羽化**
（alpha 在 `0.70·fadeR → fadeR` 之间渐降到 0）。

参考实现**没有这一刀**：`singularity.wgsl` 里 alpha 恒为 1，
`alpha / blend / transparent / premultiply` **零命中** ⇒ 整屏统一 ⇒ 没有参照物 ⇒ 看不出慢半拍。

**改动（4 处，都很小）**

| 文件 | 改动 |
|---|---|
| `SceneDescription` | 新增 `DesktopReady`（**默认 0** ⇒ 预览区与单测行为完全不变） |
| `SceneConstants` | `Extra.z` = `DesktopReady`（原本就是保留分量，没扩 cbuffer） |
| `composite.hlsl` | `alpha = max(alpha * feather, saturate(Extra.z))` —— 抓屏可用 ⇒ 整屏不透明 |
| `WgcDesktopCapture` | 取帧改为**丢弃排队旧帧，只留最新一张** |

关于最后一处：`TryGetNextFrame()` 给的是池里**最老**的那一帧（没有"取最新"的 API），
只要有一次抖动就会长期积压一张旧帧 ⇒ 白压一帧（+16.7ms）。
参考实现同样是这么处理的：`GPU_BUFFERS = 3` + 注释"the newest frame lives in shared GPU texture i"。

**兜底（比参考实现严格）**

抓屏不可用（受保护内容 / HDR / 锁屏 / 抓挂了）时必须**退回透明**，否则整屏死黑。
参考实现是拿"程序化图案"顶着（很丑）；我们由 `BlackFrameDetector` 判 `useBackground` ⇒ 退回透明。

**测试（新增 2 条，638 → 640）**
- `OverlayAlpha_FillsTheWholeViewportWhenTheDesktopIsReady`（`DesktopReady=1` ⇒ 四角 alpha > 247）
- `OverlayAlpha_FallsBackToTransparentWhenTheDesktopIsNotReady`（`=0` ⇒ 透明，**兜底不能丢**）
- 这两条**互补**，同时通过本身就证明开关真的接上了（否则两者会给出同一个值，必有一条红）

**实机**（两次：26s / 32s）

| 项 | 结果 |
|---|---|
| 抓屏失败 | 0 |
| 抓屏降级 / 全黑 | False / False（走 WGC，没整屏黑） |
| 帧率 | 56.9 ~ 57.5 FPS |
| Present | 0.23 ~ 0.51 ms（第一次出现 13 / 24ms 尖峰，第二次没有 ⇒ 偶发，非改动所致） |

**最终判据仍是你的观感**：滚滚轮看折射区还慢不慢。

### W6 · 文档

- `prd-v2.md`：最低系统 **Win10 1809 → 2004（build 19041）**，并写明原因
- `fixes-v3.md` 问题 4：从「暂缓」改为「已修 / 已证伪」，附实测数据
- `plan-v3.md`：抓屏链路一节更新
- 新增本文档的实施记录

---

## 5. 三个不确定性与判定方法

| # | 不确定性 | 怎么判定 | 不成立怎么办 |
|---|---|---|---|
| ~~**U1**~~ | ~~`CreateForMonitor` 的 QI 上次返回 `E_NOINTERFACE`，是否真是 TFM 导致~~ | ✅ **已解决（2026-09-27 W2 实机）**：改 TFM 后 `CreateForMonitor` 成功，日志 `WGC 步骤1：item OK，size=1920x1080`。确实就是 TFM 的锅 | — |
| **U2** | WGC 抓显示器时会不会抓到我们自己的覆盖层 | **W0 ④** 洋红对照法（P0 已验证此法有效） | 若会 → 仍需依赖 `WDA_EXCLUDEFROMCAPTURE`，须确认它对 **WGC** 也生效（P0 只证明了对 GDI 生效） |
| **U3** | 延迟真的降多少 | 抓屏耗时已从 0.47~1.05ms 降到 **0.15~0.36ms**；**最终判据是你的实机观感**：滚滚轮时折射区还慢不慢 | 若观感没改善 → 白做，问题 4 维持暂缓 |

---

## 6. 验收标准（主判据是实机）

| 项 | 标准 |
|---|---|
| 拿到帧 | 连续 ≥ 30 帧，尺寸 == 显示器尺寸 |
| **延迟**（主判据） | 内容年龄**明显低于** GDI 对照组（目标 2 帧 → 1 帧） |
| **割裂**（你要的那条） | 滚轮连续滚动长页面时，折射区与周围**不再有可察觉的慢半拍** |
| 自排除 | 抓到的帧里**没有**我们自己的覆盖层 |
| 帧率 | 不低于改造前（57 FPS），理想到 60 |
| 锁屏恢复 | 锁屏 → 解锁后抓屏自动恢复，不需重启 |
| 回归 | 0 警告 0 错误 + 现有 638 条全绿 |

---

## 7. 风险与回退

| # | 风险 | 应对 |
|---|---|---|
| **R1** | W0 探针就失败（U1/U3 不成立） | 全部不做，问题 4 维持暂缓；本文标「已证伪」，代码零改动 |
| **R2** | TFM 变更引发连锁编译问题 | W1 单独一步，验完再往下；失败则回退本步 |
| **R3** | WGC 抓到自己（U2） | 验 `WDA_EXCLUDEFROMCAPTURE` 对 WGC 是否生效；不生效则另想办法 |
| **R4** | 锁屏后抓屏挂掉 | W4 单独一步，明确验收 |
| **R5** | 抬高最低系统要求 | 你已接受；`prd-v2` 同步改 |
| **R6** | 全量测试 OOM（本机 8GB，plan-v3/v4 都踩过） | 先看可用内存；成片 `X8000` 先怀疑内存，再 `dotnet test -- xunit.parallelizeTestCollections=false` |

**回滚点**：见下。任何一步搞砸都可精确退回。

---

## 8. 回滚点

- 提交 **`74e063f`**（v3/v4 全部完成 + P6 决定，工作区干净）
- 标签 **`pre-wgc`** —— 指向本次 WGC 开工前的状态

```bash
git checkout pre-wgc -- <路径>    # 精确退回单个文件
git checkout pre-wgc -- src/      # 退回整个源码
git diff pre-wgc                  # 看改了什么
```
