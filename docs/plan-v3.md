# 开发计划 v3：改为全屏覆盖层架构（plan-v3.md）

> 依据 **`prd-v3.md`**（架构决策与影响面）。本文只写**怎么落地**：步骤顺序、每个子步改什么、
> 加什么测试、怎么验收。
> 上一版 `plan-v2.md`（M0 – M6 主线）已全部完工并归档。
>
> **规则**：每个子步结束都要 —— 改代码 → 加/改测试 → 全量跑绿 → 更新本文 → 停下来汇报。
> **回滚**：git 已有备份点 `8455e44`（2026-09-24 全量备份）与 `c125dd2`（问题 9 记录）。
> 任何一步搞砸都可以 `git checkout 8455e44 -- <路径>` 精确退回。

---

## 0. 决策快照（详见 `prd-v3.md` §3）

| # | 决策 |
|---|---|
| 1 | 整屏光栅化 + 着色器 `b >= bmax` 内部分支（照搬参考实现，**不做** CPU 侧 scissor） |
| 2 | 全屏窗口 + **保持透明**，只在黑洞附近输出 alpha（保留问题 4/5 的修复） |
| 3 | 多显示器**本轮不管**，单显示器一个全屏窗 |
| 4 | **分步**：第一步只做「窗口全屏 + 位置 uniform」 |
| 5 | **不保留**小窗口路径，直接换全屏 |
| 6 | 性能**不动**，先验收抖和黑，性能留到抓屏那步 |
| 7 | R1（自反馈）**改之前先单独验一次** |

---

## P0 · R1 预验：全屏窗会不会被自己的抓屏抓到

**为什么要先做**：这是唯一可能造成「整屏级」事故的风险。小窗口时 `SetWindowDisplayAffinity`
失效只是小范围回廊；全屏时失效就是**整屏镜像回廊**（点击穿透还在，但看不见桌面）。

**做法（临时改动，验完必还原）**

1. 临时把 `OverlayController.Tick` 里的窗口矩形换成显示器矩形：
   ```csharp
   // 临时：OverlayController.cs:425 附近
   var m = ScreenMetrics.MonitorRectAt((int)_follow.X, (int)_follow.Y);
   var rect = new OverlayGeometry.Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
   ```
2. 发布 `dist`，实机跑一次，**把桌面设成纯色**（纯色最容易看出重复图案）。
3. 判读：
   - 出现**自己的黑洞被反复复制**的嵌套图案 ⇒ 自反馈，**R1 不成立**，必须先解决再继续；
   - 画面正常、桌面内容清晰 ⇒ R1 通过，可以正式改。
4. 同时看日志确认 `ExcludeFromCapture` 是否为 `True`（`Win32OverlayWindow.cs:92` 的回读值）。

**收尾**：`git checkout -- src/` 还原临时改动，**不提交**。

**产出**：把结果写进 `prd-v3.md` §9 的 R1。

---

### ✅ P0 已完成（2026-09-25）：**R1 通过**，全屏窗不会被自己的抓屏抓到

**顺带测出来的两件事（比 R1 更值钱）**

1. 全屏窗口本身跑得通：日志 `首帧渲染完成：窗口 1920x1080 @(0,0)`，
   交换链 `FlipDiscard/Premultiplied`，抓屏降级=True（GDI）、后台抓屏已启用。
2. **性能根本不是问题** —— 见下表。

| 量 | 实测（全屏 1920×1080） |
|---|---|
| 帧率（近 5s 平均） | 15.6 → **56.7 → 57.3 → 56.4 FPS**（首个窗口是预热假象） |
| 分段耗时 | 抓屏 **0.56–0.94** / 渲染 **0.01–0.02** / Present **0.96–3.75** / 整帧 **1.68–4.60 ms** |

> 全屏后比改动前的小窗口（52 FPS）**还快**：因为抓屏已在后台线程，主循环整帧只剩 1.68–4.60ms。
> 12 倍的像素涨幅落在 GPU 上，而渲染本身只占 **0.01–0.02 ms** —— 瓶颈从来不在渲染。

**R1 的判定方法（可复用，比肉眼可靠）**

原本想「临时全屏 + 两次截图做像素差」，但**桌面动效噪声太大**（IDE 动画会污染 13%~61% 的像素），
做不出干净信号。改成**确定性对照实验**：

1. 自己造一个**纯洋红**（`R>200, G<60, B>200`，桌面上不可能出现的颜色）的全屏窗；
2. **不设** `WDA_EXCLUDEFROMCAPTURE` → 截屏采样点 **32400**（整屏都是洋红）✅ 说明这个测法能检出；
3. **设上** `WDA_EXCLUDEFROMCAPTURE`（回读 = **17** = 0x11）→ 截屏采样点 **0**（完全看不到）。

⇒ `WDA_EXCLUDEFROMCAPTURE` **对 GDI BitBlt 同样生效**，
而 `CopyFromScreen` / `BitBlt` 正是我们抓屏走的同一条路径。
**结论：全屏窗在抓屏里完全不可见，不会形成镜像回廊。**

**清理**：临时改动已 `git checkout` 还原（`git diff` 里只剩文档）；
4 张临时截图 `bugs/p0-*.png` 已删除。**dist 里当前是 P0 临时全屏版，P1.6 会重新发布覆盖。**

---

## P1 · 第一步：窗口全屏 + 黑洞位置改 uniform

> 目标：窗口永不移动，黑洞位置由连续浮点的 uniform 决定。
> **本步不动**：抓屏、截图补偿、预览、多显示器、性能裁剪。

### P1.1 窗口矩形改为显示器矩形

**改** `OverlayController.Tick`

现在（`OverlayController.cs:423-427`）：
```csharp
double radius = ComputeV2Radius(TransitionState.PeakScale(_settings));
var rect = OverlayGeometry.ComputeWindowRect(_follow.X, _follow.Y, radius);
_window.Move(rect.Left, rect.Top, rect.Width, rect.Height);
_window.SetVisible(true);
```

改成：
- 按**光标所在显示器**取矩形 `ScreenMetrics.MonitorRectAt(...)`；
- **只在矩形变化时**才调 `window.Move`（不是每帧）——
  参考实现 `main.rs:757-763` 明确警告：layered 窗上无意义的 swapchain 重建会失败；
- `EnsureRenderTarget(rect.Width)`（`:436`）跟着改为按显示器尺寸。

**注意**：显示器切换（光标跨屏）时矩形会变 → 需要重建渲染目标。
本轮只有单显示器，但仍要处理「尺寸变化时重建」这条路径，避免崩溃。

**测试**
- 新增：光标大幅移动后，连续多帧窗口 `left/top/width/height` **完全不变**
- 新增：窗口矩形 == 显示器矩形
- 新增：尺寸变化时才调 `Move`（防「每帧 Move」回归）

---

### ✅ P1.1 已完成（2026-09-25）

**改动**（`OverlayController`）
- `Tick` 里窗口矩形改为 `ScreenMetrics.MonitorRectAt(_follow.X, _follow.Y)` 的整块显示器，
  不再走 `ComputeV2Radius` + `ComputeWindowRect`，也不再每帧 `Move`
- 新增 `_lastWindowRect` 字段：只在矩形**真的变了**（换显示器 / 改分辨率）时才 `Move`
- `ComputeV2Radius` 在 `Tick` 里的调用随之删除；首帧日志改用实际半边长
  （`ComputeV2Radius` 方法本体留到 P1.5 连同 N4 过冲一起处理）

**断言改写**（`OverlayControllerTests`，6 条全部按新语义重写）

| 旧 | 新 |
|---|---|
| `Tick_RendersAndShowsWindowNearMouse`（窗口中心对齐鼠标） | 窗口 == 鼠标所在显示器的矩形，且鼠标落在窗口内 |
| `WindowSize_CoversTheDiskAndFarField` | `WindowRect_DoesNotFollowTheCursor`（**主判据**）+ `WindowRect_EqualsTheWholeMonitor` |
| `HoleSizeChange_ResizesWindowOnNextFrame` | `HoleSizeChange_DoesNotResizeWindow`（断言**不变**） |
| `SettingsHoleSize_DrivesWindowSize` | `SettingsHoleSize_DoesNotAffectWindowSize` |
| `SettingsDiskOuter_DrivesWindowSize` | `SettingsDiskOuter_DoesNotAffectWindowSize` |
| `WindowExtent_NeverExceedsPerformanceLimit`（≤1024） | `WindowRect_IsStableAcrossFrames`（40 帧后 `MoveCount == 1`） |

**验证**：`dotnet build -c Release` **0 警告 0 错误**；`OverlayControllerTests` **29 通过**；
全量 **`589 通过 / 0 失败`**。

> ⚠️ **踩坑：全量测试会 OOM，不是代码问题。**
> 本机 8GB、可用内存一度只剩 0.8GB，xunit **并行**跑时会同时编译多份着色器，
> 于是 20+ 条着色器测试集体报
> `error X8000: D3D11 Internal Compiler Error: Invalid Bytecode ... 0x8007000e`（E_OUTOFMEMORY），
> 看起来像着色器坏了，其实不是。
> **判定方法**：单独跑一个类（如 `StarFieldTests`）立刻全过 ⇒ 是内存，不是代码。
> **解决办法**：`dotnet test -c Release -- xunit.parallelizeTestCollections=false`
> （关掉集合并行，代价是 3 分钟而不是 2 分钟）。以后遇到成片 `X8000` 先怀疑内存。

### P1.2 黑洞位置改 uniform（连续、不量化）

**改** `OverlayController.cs:471`
```csharp
var (centerX, centerY) = OverlayGeometry.LocalCenter(_follow.X, _follow.Y, rect);
```

`LocalCenter` 现在返回**窗口中心的恒定值**（那是问题 7 的补丁）。窗口不动之后，
这个补丁的前提消失了 —— 现在应该返回**跟随位置在窗口内的真实坐标**（连续浮点）：

- `OverlayGeometry.LocalCenter` 改为返回 `centerX - window.Left, centerY - window.Top`
  （或改名 `HoleCenter` 更贴切，避免与旧语义混淆）；
- `PositionQuantum` / `Quantize` 在**定位**上不再使用。

**⚠️ 两处断言语义会反转，必须重写**（否则测试假失败）：
- `RenderingMathTests.LocalCenterIsFixedAtWindowCenter`
- `RenderingMathTests.LocalCenterDoesNotMoveWithCursor` ← 这条原来是「光标变了局部坐标也不能变」，
  新架构下**正好相反**：坐标必须跟着光标连续变。

**测试**
- 改：上面两条 → 改为「局部坐标 = 跟随位置 − 窗口原点」
- 新增：慢速移动时局部坐标**逐帧平滑推进**（相邻帧差值小于位移量，且不出现 0→1px 的台阶）
- 新增：不经过任何量化（给一个 0.5px 的位移，坐标应真的变 0.5）

---

### ✅ P1.2 已完成（2026-09-25）

**改动**（`OverlayGeometry.LocalCenter`）
```csharp
// 旧：窗口不动不了，只能把坐标钉死在窗口中心换「折射图案稳定」
=> (window.Width / 2.0, window.Height / 2.0);
// 新：窗口已固定为整块显示器，坐标就是真实跟随位置，连续浮点、不量化
=> (centerX - window.Left, centerY - window.Top);
```

**断言改写**（`RenderingMathTests` / `OverlayGeometryTests`，3 条）

| 旧 | 新 | 说明 |
|---|---|---|
| `LocalCenterIsFixedAtWindowCenter` | `LocalCenterIsTheFollowPositionInWindowCoords` | 语义从「恒定在窗口中心」改为「跟随位置 − 窗口原点」 |
| `LocalCenterDoesNotMoveWithCursor` | **`LocalCenterFollowsTheCursorExactly`** | **断言方向反转**：原来要求「不跟着光标动」，现在要求「必须精确跟着动，且区分得出 0.7px」 |
| `WindowCenterErrorIsBoundedByHalfQuantum` | `HolePositionIsExact_NotQuantized` | 旧的是给「问题 7 修法的代价」定边界；窗口固定后误差本来就是 0，不存在要设边界的代价 |

> ⚠️ **这条最值得记住**：`LocalCenterDoesNotMoveWithCursor` 在问题 7 期间是**正确**的断言，
> 到了 v3 它变成了**错误**的断言。断言的「对错」取决于架构前提 ——
> 改架构时必须回头检查那些「为了兜住旧架构缺陷」而写的断言，它们会集体反转。

**取舍说明**：计划里写的「慢速移动逐帧平滑推进」是控制器级集成测试，
需要暴露 `center` 才能断言；这里改用 `LocalCenter` 的**纯函数**测试覆盖同一性质
（精确性 + 亚像素可分辨 + 无量化），确定性更好，也不必为测试开内部接口。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **`589 通过 / 0 失败`**。

> ⚠️ **踩坑补充（P1.1 那条 OOM 的后续）**：
> 关掉集合并行**并不总能救回来** —— 可用内存只剩 0.9GB 时，关了并行照样 9 条失败。
> 清理残留 `dotnet` 进程、等内存回到 **2.6GB** 之后才全绿。
> 所以判定顺序是：先看可用内存 → 再决定要不要关并行 → 还不行就分批。

### P1.3 全黑帧检测的采样原点

**改** `OverlayController.cs:726-727`
```csharp
_sampleOriginX = rect.Left + ((rect.Width - sampleSize) / 2) - monitor.Left;
```
窗口全屏后，这个采样点会固定在**屏幕中心**，而检测目标是「黑洞所在处」。
改为跟随 `_follow`（黑洞位置）− 显示器原点。

**测试**：采样原点随黑洞移动（现有断言若写死为窗口中心，需改）。

---

### ✅ P1.3 已完成（2026-09-25）

**改动**

1. `OverlayGeometry` 新增纯函数 `BlackFrameSampleOrigin(holeX, holeY, sampleSize, monitorLeft, monitorTop)`
   —— 抽成纯函数是为了**可测**（采样原点是私有字段，否则没有断言入口）；
2. `OverlayController` 改用它：
   ```csharp
   // 旧：按窗口中心算 ⇒ 窗口全屏后永远落在屏幕正中
   _sampleOriginX = rect.Left + ((rect.Width - sampleSize) / 2) - monitor.Left;
   // 新：跟着黑洞走
   (_sampleOriginX, _sampleOriginY) = OverlayGeometry.BlackFrameSampleOrigin(
       _follow.X, _follow.Y, sampleSize, monitor.Left, monitor.Top);
   ```

**不需要处理越界**：`UpdateBlackFrameState`（`OverlayController.cs:828-829`）内部已经把采样矩形
钳进纹理范围，黑洞贴屏幕边时采样框会被推回纹理内，这里不必重复操心。

**测试**（`RenderingMathTests` 新增 3 条）
- `BlackFrameSampleOrigin_IsCenteredOnTheHole`：采样框以黑洞为中心
- `BlackFrameSampleOrigin_SubtractsTheMonitorOrigin`：减去显示器原点（副屏在左上、坐标为负时最容易错）
- `BlackFrameSampleOrigin_FollowsTheHole`：黑洞移动时采样点必须跟着走（防止又钉死在屏幕中心）

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **`592 通过 / 0 失败`**（589 + 新增 3）。

### P1.4 复核着色器在全屏下的行为

两项必须复核（不复核会出现新的观感缺陷）：

1. **窗口边缘羽化失效** —— `composite.hlsl` 的 `smoothstep(fadeR*0.70, fadeR, plen)`，
   `fadeR` 原本由窗口尺寸决定；窗口全屏后它变成屏幕半高，羽化等于没有。
   确认是否需要显式引入一个「影响半径上限」来收尾，避免靠屏幕边时出现硬边。
2. **星云 alpha 失控** —— `ambientAlpha = max(..., nebulaEnabled * (Timing.w > 0.5 ? 1.0 : 0.0))`
   会把那一圈 alpha 拉到 **1.0**。小窗口下是「一圈」，全屏下若范围变大，
   会变成**大面积不透明**。必须实测确认范围没失控。

**测试**：补一条「远离黑洞处 alpha ≈ 0」的断言（例如 3 倍影响半径之外 alpha < 8）。

---

### ✅ P1.4 已完成（2026-09-25）：两项都**不需要改**，但查出一个要注意的后果

#### ① 窗口边缘羽化 —— 没有失效，反而变好了（R5 作废）

`composite.hlsl:98-99`：
```hlsl
float fadeR = 0.5 / max(Hole.x, 1e-4);      // 视口半高，单位是「阴影半径」
alpha *= 1.0 - smoothstep(fadeR * 0.70, fadeR, s.plen);
```

| | 小窗口（418²） | 全屏（1920×1080） |
|---|---|---|
| `Hole.x`（阴影半径占视口高） | 64.8/418 ≈ **0.155** | 64.8/1080 = **0.06** |
| `fadeR` | ≈ **3.2 rs** | **8.33 rs** |

羽化起始 5.83 rs、终止 8.33 rs —— **而屏幕半高正好是 8.33 rs**，
所以 alpha 恰好在屏幕上下边缘收完，左右与四角更早就收完了。**不会出现矩形硬边。**

> **意外收获**：旧的小窗口其实一直在**切掉透镜** ——
> 它在 3.2 rs 处就把 alpha 收没了，而那里的远场窗口 `window = exp(-(3.2/7)²) ≈ 0.81`，
> 位移还剩 81% 就被砍断。全屏之后这部分透镜才真正显示出来。

#### ② 星云 alpha / `bgCoverage` —— 全屏下会变成**整屏 alpha = 1**

两条叠在一起造成的：
1. `backgroundCoverage()`（`lens.hlsl:59`）判的是「取样点是否落在抓屏纹理的 [0,1] 内」。
   全屏后 `CaptureMap` 恒等、取样点总在屏幕内 ⇒ **coverage 恒为 1**（只有屏幕边缘才淡出）。
2. `ambientAlpha` 的那一项 `nebulaEnabled * (Timing.w > 0.5 ? 1.0 : 0.0)` 是**与位置无关的常量**。

⇒ **开着透镜时，整屏 alpha = 1**。这与决策 2「只在黑洞附近输出 alpha」有出入。

**但视觉上无害**：远处偏折 → 0，`bg` 就是桌面本身，看到的仍是桌面；
效果上等价于参考实现那种 alpha=1 的全屏不透明窗口。
关掉透镜时 `bg` 与 `bgCoverage` 都归零、星云那项也归零 ⇒ 只剩阴影与盘的 alpha，仍是透明的。

**真正的代价（要盯）**：抓屏出问题时，**影响面从小方块变成整屏** ——
未上传 / 黑区 / 抓屏失败都会整屏体现（这正是 R6 的另一面）。P1.6 实机重点看这个。

#### 新增断言（`GeodesicCompositeTests`）

`OverlayAlpha_VanishesAtTheViewportEdge`：视口的四角 + 四边中点 alpha 必须 < 8。
这条锁的是「全屏后不会在屏幕四周看到一圈矩形硬边」——
以前由「窗口只有 418²」天然保证，现在只能靠 `fadeR` 那一项，必须显式锁住。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **`593 通过 / 0 失败`**（592 + 新增 1）。

### P1.5 清理与连带

| 项 | 处理 |
|---|---|
| `OverlayGeometry.ComputeV2WindowRadius` | **保留**。除窗口尺寸外，`ScreenshotCompositor.PatchExtent`（`:111`）还在用它算截图补偿的补丁尺寸 |
| `OverlayGeometry.ComputeWindowRect` | 若 P1.1 后无调用点 → 删（先确认 `RenderingMathTests:127-128,144` 的用途） |
| `OverlayMaxExtent`（1024） | 全屏后窗口尺寸必然超出。删除该常量，并清理 5 处断言：`RenderingMathTests:127-128,144,223-224`、`OverlayGeometryV2Tests:83,92-93` |
| `OverlayControllerTests:699-700` | 断言 `LastWidth <= OverlayMaxExtent` → **必然失败**，需删除或改语义 |
| `CaptureMap` | 窗口 == 显示器 ⇒ 自然退化为恒等。保留计算即可，补一条「恒等」断言 |
| N4 出现/消失动画过冲 | 原作用于窗口半径（`ComputeV2Radius(peakScale)`），窗口固定后失效 → 改作用于 `HoleSize` |
| 截图补偿（M4） | 位置（`VisualPosition`）仍正确；**补丁尺寸/取景需复核**。本步只记录不修，列入 P2 |
| 预览 | 独立 184² 渲染，预期不受影响；跑一遍 `PreviewSyncTests` 确认 |

### ✅ P1.5 已完成（2026-09-25）

**各项落地结果**

| 项 | 结果 |
|---|---|
| `ComputeV2WindowRadius` | **保留** —— `ScreenshotCompositor.PatchExtent`（`:111`）还在用它算截图补偿的补丁尺寸 |
| `ComputeWindowRect` | **已删** —— P1.1 后 src 与测试均无调用点 |
| `PositionQuantum` / `Quantize` | **已删** —— 窗口不再移动，量化失去意义（连带删了 `PositionIsQuantized`、`Quantize_RoundsToQuantum` 等断言） |
| `OverlayMaxExtent`（1024） | **已删** —— 全屏必然超过它；清理了 5 处断言。`OverlayControllerTests` 那条 `LastWidth <= OverlayMaxExtent` 已按新语义去掉 |
| N4 过冲 | **已删 `TransitionState.PeakScale`**。窗口是整块显示器，不存在「光晕被裁」，不需要按峰值预留尺寸；过冲照旧生效，缩放的是 `HoleSize`（`SceneConstants` 里 `holeFraction *= d.Scale`） |
| `CaptureMap` | 保留计算；**新增 2 条恒等断言**（整屏、以及原点非零的副屏） |
| 预览 | `PreviewSyncTests` 全过，未受全屏改造影响 |
| 截图补偿 | **未修**，按计划列入 P2 |

**⚠️ 踩坑：新写的断言自己就是坏的**

`TransitionStateTests.Overshoot_ShowsUpInTheVisualScale` 第一版写成
`state.Step(0.4 * PeakAt)` 想一步推进到峰值 —— 但 `Step` 会把 deltaTime
钳到 `MaxTimeStep`（0.05s），单次最多前进 0.05/0.4 = **0.125** 的进度，
而峰值在 `PeakAt` = 0.6，于是 `Scale` 只算出 **0.27**，看着像「过冲失效」。

**教训**：断言失败时先怀疑断言本身，尤其是刚为新语义写的那条。
这里改成**循环多步推进**到 `Progress >= PeakAt` 再断言。

**另两条失败是偶发，不是代码问题**：`Tick_DoesNotReassertTopmostEveryFrame`
（窗口类注册 1410，测试间干扰）与 `PresetServiceTests.DeleteCustom_KeepsTheOtherPresets`
单独跑都通过 —— 以后遇到先单独复跑一次再动手。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；
`dotnet test`（关集合并行）**589 通过 / 0 失败**。

### P1.6 测试 + 发布 + 实机

1. `dotnet build -c Release` → 0 警告 0 错误
2. `dotnet test` → 全绿
3. `Remove-Item dist -Recurse` 后重新 publish（App 的 csproj，别在仓库根 publish）
4. 实机验证三项：

### ✅ P1.6 已完成（2026-09-25）：**抖动消失、帧率达标、变黑不再出现**

**发布**：`dotnet publish src/BlackHoleCursor/... -o dist`（20:18，已覆盖 P0 临时全屏版）。

**① 抖动 —— 消失 ✅**

`--jitter-log` + 脚本匀速拖动，321 帧逐帧数据：

```
left=0  top=0  extent=1920            ← 光标大幅移动，窗口矩形**一帧都没变**
center=1320.86 → 1287.97 → 1243.40    ← 带小数、**连续平滑推进**（不再按 1px 跳）
mapOffset=0.00000,0.00000             ← CaptureMap 恒等（窗口 == 显示器）
```

对比改动前：`center` 在 229.51~231.48 之间锯齿（峰峰 1.97px）、`left` 按 2px 步进。
现在 `center` 是 `1320.86` 这种带两位小数的连续值 —— 亚像素定位已生效。

**② 帧率 —— 56.9 FPS ✅**（全屏 1920×1080，与 P0 一致）

| 段 | 耗时 |
|---|---|
| 抓屏 | **0.78 ms** |
| 渲染 | **0.02 ms** |
| Present | 1.89 ms |
| 整帧 | **3.46 ms** |

（首个统计窗口 0.1 FPS / 整帧 154ms 是预热假象，第二窗口起就正常。）

**③ 变黑 —— 已消失 ✅**（你的实机确认：*「确实不会闪了，而且效果很不错」*）

⚠️ 这一项**程序化验证不了**：覆盖层设了 `WDA_EXCLUDEFROMCAPTURE`，
任何截图（含 `CopyFromScreen`）都**抓不到黑洞本身** ——
P0 的洋红实验能成功，是因为那个测试窗**故意不设**这个标志。
所以只能靠实机肉眼，这类验收项以后直接交给用户确认，别浪费时间造自动化。

⇒ **问题 9 由架构改造解决**（与 P1.1 的预判一致）：窗口变成显示器矩形后，
抓屏区域的 margin 被 clamp 掉，区域自动 = 整块显示器且恒定，「旧区域」竞态从根上消失。

---

### 🎉 P1（第一步）整体完成 —— 验收标准逐条达成

| 项 | 标准 | 实测 |
|---|---|---|
| 窗口 | 尺寸 = 显示器矩形，连续多帧 `left/top/extent` 完全不变 | ✅ `0/0/1920` 恒定 |
| 黑洞位置 | 随光标连续变化，不被量化 | ✅ `1320.86` 连续浮点 |
| 抖动 | 匀速拖动时 `center` 平滑单调 | ✅ 无锯齿 |
| 变黑 | 纯白桌面移动无黑块 / 闪烁 | ✅ 已确认 |
| 帧率 | 记录 | ✅ 56.9 FPS（抓屏 0.78 / 渲染 0.02 / 整帧 3.46 ms） |
| 回归 | 0 警告 0 错误 + 全量全绿 | ✅ **589 通过 / 0 失败** |

**⚠️ 踩坑：取证脚本把自己坑了一次**

我把流程改成「等 JITTER 出现才移动鼠标」，结果永远等不到 ——
**鼠标静止时程序会空闲降帧、根本不渲染**，于是 20 秒后跳过移动、一帧都没有。
（之前成功的脚本是「等 3 秒初始化 → 直接移动」，移动才触发渲染。）
⇒ **取证脚本必须自己先动鼠标**，不能等信号再动。

> 附带确认：日志被 `RuntimeLog` 的 512KB 上限自动清空过，
> 所以「用行号偏移隔离本次运行」这招才可靠，别用「匹配旧 JITTER」判断就绪（会假阳性）。
   - **抖动**：`--jitter-log` + 脚本匀速拖动，`left/top/extent` 应恒定，`center` 平滑单调
   - **变黑**：纯白桌面上移动，折射区不出现黑块/闪烁（预期已由 P1.1 顺带解决，见下）
   - **帧率**：记录即可（决策 6：不达标也不在本步处理）

> **预期：问题 9 会被 P1.1 顺带解决。**
> 窗口变显示器矩形后，抓屏区域的 margin 会被 clamp 掉
> （`x0 = max(rect.Left − margin, monitor.Left)`，而 `rect.Left == monitor.Left`），
> 抓屏区域**自动 = 整块显示器且恒定**，「旧区域」竞态从根上消失。
> 代价是每帧抓的面积变大，但 GDI 耗时与面积弱相关（526²=16.6ms / 1052²=23.6ms），
> 且由后台线程承担，**不影响帧率**。实机要确认这条。

---

### ✅ P3 代码已完成（2026-09-25），实机已于 2026-09-26 验证通过

> 下面「补偿根本没触发」那段是当时的误判，真相见风险登记的 R7（已关闭）：
> 补偿一直工作，只是**首次要 2.2~3.5 秒**，取证脚本在它完成之前就把进程杀了。

**改动**
1. `PatchExtent` 改为覆盖**全屏远场范围**：
   `fadeR = 0.5 / Hole.x` 个阴影半径 ⇒ 像素半径 `= 0.5 × 屏高`（1080p 屏就是 540 ⇒ 边长 1080）。
   旧公式只用 `ComputeV2WindowRadius`（约 6 rs ≈ 389px），那是小窗口时代的口径；
   全屏后远场在桌面上是看得见的（P1.4 意外收获），不补就会「截图里的黑洞比桌面小一圈」。
   保留它作**下限**，避免大盘装不下。
2. `MaxPatchExtent` 1024 → **4096**：1024 反而会把 1080 的远场切一截；
   放宽不拖慢（整屏渲染只占 0.01ms）。

**断言改写**（`ScreenshotCompositorTests`，3 条）

| 旧 | 新 |
|---|---|
| `PatchExtent_MatchesTheOverlayWindowGeometry` | `PatchExtent_CoversTheFullScreenFarField`（= 屏高） |
| `PatchExtent_GrowsWithHoleSize` | `PatchExtent_DoesNotDependOnHoleSize` + `PatchExtent_GrowsWhenTheDiskIsHuge` |

> ⚠️ **语义反转**：补丁尺寸现在**不随洞径变化**（远场范围只取决于视口，`HoleSize` 在公式里被约掉）。
> 但盘的像素尺寸 ∝ `HoleSize × OuterRadius`，洞大到盘超过远场范围时补丁仍会跟着变大 ——
> 所以断言拆成两条，别指望「洞越大补丁越大」恒成立。

**验证**：`dotnet build -c Release` 0 警告 0 错误；全量 **590 通过 / 0 失败**。

**❌ 但实机没验证成 —— 补偿根本没触发**

复用了 M6.3 那套方法（往剪贴板放 1920×1080 纯灰位图 → 等 → 看日志）：
结果日志里**一条补偿记录都没有**，连 `HandleClipboardUpdate` 里那两处
`LogSkipOnce`（「没有可取回的位图」/「尺寸不一致」）都没出现 ——
说明 `WM_CLIPBOARDUPDATE` 压根没走到处理逻辑，不是补丁尺寸的问题。

已确认不是这些原因：`ScreenshotCompensation=True`、位图确实放进了剪贴板、
`ClipboardCompensationHost` 在 `App.xaml.cs:114` 无条件启动、注册失败会记 Error（日志无 Error）。

⇒ 当时据此判定「这是一个独立问题，与本步改动无关」，列入风险登记 R7。
**2026-09-26 已查明是误判**（详见 R7）：补偿一直工作，只是首次要 2.2~3.5 秒。

**实机验证结果**（全屏 1920×1080，往剪贴板放全屏位图）

| 次序 | 从「收到剪贴板消息」到「补偿写回」 |
|---|---|
| 第 1 次 | **2.16 s**（现建 D3D 设备 + 编译着色器） |
| 第 2 次 | **0.29 s** |
| 第 3 次 | **0.31 s** |

写回剪贴板自己触发的后续 `WM_CLIPBOARDUPDATE` 被**序列号**挡住，没有自我补偿循环 ✅
（这正是 `ScreenshotCompensator._lastWrittenSequence` 的作用，实测确实生效）。

> 首次那 2~3 秒已另立为 `fixes-v3.md` 问题 5（要不要在启动时预热合成器，待你定）。
> 与**补丁尺寸**无关 —— P3 改的 `PatchExtent` / `MaxPatchExtent` 经此验证是对的。

---

## P2 及以后（待 P1 验证后再排）

| 步骤 | 内容 | 状态 |
|---|---|---|
| P2 | 抓屏简化：区域恒定后把 margin / 局部更新逻辑显式去掉；问题 9 若仍未解决，按 `fixes-v2` 的三个候选方案修 | ✅ 已完成 |

### ✅ P2 已完成（2026-09-25）

**改动**
1. 抓取区域直接取 `monitorWidth / monitorHeight`，**删掉 `margin` 与两处 clamp** ——
   窗口 == 显示器后它们恒等于整屏，算不出别的结果，留着只会误导；
   而且正是这套「窗口 ± 5%」制造了问题 9 的「旧区域」竞态。
2. 删除常量 `CaptureMarginFactor`（0.05）。
3. **把后台抓屏的启用时机提前**：原来放在「第一次同步抓屏成功之后」，
   于是主线程必须先自己抓一帧**整屏**（1920×1080 的 GDI 抓取不便宜）。
   现在改为「确认 `IsDegraded` 就立刻交给后台线程」——
   `IsDegraded` 在 DXGI 初始化失败时已为真，不必等一次 `TryCapture`。
   ⇒ **主线程从此不再做任何整屏同步抓屏**，与参考实现「渲染侧从不等待抓屏」一致。

**实测**（全屏 1920×1080）

| 量 | P1.6（改前） | P2（改后） |
|---|---|---|
| 帧率 | 56.9 FPS | **57.3 FPS** |
| 抓屏 | 0.78 ms | **0.45 ms** |
| 渲染 | 0.02 ms | 0.01 ms |
| 整帧 | 3.46 ms | **1.67 ms** |
| 窗口 | `0/0/1920` 恒定 | 同（199 帧逐帧确认） |

**验证**：`dotnet build -c Release` 0 警告 0 错误；全量 **589 通过 / 0 失败**
（`Tick_DoesNotReassertTopmostEveryFrame` 单独复跑 29/29 通过，那条是偶发）。

> 🔥 **踩坑（本次最贵的一条，必须记住）**
> 改完之后实机连跑 **3 次都一帧不出**（日志只有「覆盖层已启动」，之后无任何输出），
> 我先后怀疑：P2 代码回归 → 内存不足 → 调度器早退 → 后台抓屏卡死，全都排查了一遍。
> **真正的原因是配置里 `Enabled=False`** —— 总开关关着，
> `Tick` 直接走「收起窗口 + return」，根本不渲染，于是 FPS / JITTER / 抓屏日志全都没有。
>
> **判定方法（以后第一步就做）**：实机没输出时，先看
> `%LOCALAPPDATA%\BlackHoleCursor\settings.json` 里的 `Enabled`，
> 再看日志里 `IsVisible` 是 True 还是 False。
> 别急着怀疑代码 —— 尤其刚改完代码时，最容易把「配置没开」误判成「自己改坏了」。
>
> 顺带：本机可用内存一度只剩 1.2GB，还堆着 3 个残留 `dotnet` 进程
> （`P1.5` 那条 OOM 踩坑的同类问题），清理后回到 1.57GB。
| P3 | 截图补偿（M4）适配全屏：补丁取景与尺寸 | ⚠️ 代码已完成，实机未验证（见下） |
| P4 | 死代码清理：`PositionQuantum` / `Quantize` / 小窗口遗留 | ✅ **已完成**（2026-09-27 复核） |
| P5 | 性能：抓屏搬后台线程（与 P2 同源）+ 若帧率仍不达标再评估 scissor | ✅ 已达成（P0 实测 56~57 FPS，scissor 不需要） |
| P6 | 多显示器（每显示器一个全屏窗，照搬 `main.rs:971-975`） | ❌ **不做**（2026-09-27 你决定跳过：无多屏需求） |

---

### ✅ P4 已完成（2026-09-27 复核）：死代码清理

清单里的三项都没了，逐项确认过：

| 原清单 | 现状 |
|---|---|
| `PositionQuantum` / `Quantize` | P1.5 已删（随 `ComputeWindowRect` 一起，窗口定位不再量化） |
| `MaxExtent` / `SettingsRanges.OverlayMaxExtent`（1024） | P1.5 已删 |
| `OverlayController.ComputeV2Radius` | P1.5 已删（副作用拆成 `UpdateLookAndMetrics`） |
| `TransitionState.PeakScale` | P1.5 已删（过冲缩放的是 `HoleSize`，不需要给窗口预留） |
| 截图补偿的 1024 上限 | P3 已放宽到 4096（`ScreenshotCompositor`） |
| `CaptureMarginFactor`（抓屏区域 ±5%） | P2 已删（区域恒等于整屏） |

**复核手段**：`read_lints` 对 `src` + `tests` 报 **0 条**；
`OverlayGeometry` 现存成员（`ComputeV2WindowRadius` / `DiskOuterRadiusPixels` / `PreviewFit` /
`CaptureMap` / `LocalCenter` / `BlackFrameSampleOrigin` / `Rect`）**逐个确认都有生产调用方**，
没有「只被测试用」的半死成员。

> 保留项（**不是死代码**，别误删）：`CaptureRefreshMinSeconds/MaxSeconds/CostFactor` 与
> `CaptureRefreshInterval` —— 同步抓屏路径仍会走（后台线程启用之前的那几帧，以及 DXGI 可用时）。

---

### ❌ P6 不做（2026-09-27 你的决定：没有多屏需求）

**不做多显示器**。但要把**当前行为的已知限制**写清楚，免得以后踩：

- 现在的实现是「**一个窗跟着光标在显示器之间搬家**」：
  `ScreenMetrics.MonitorRectAt(_follow)` 取光标所在显示器，切屏时 `rect != _lastWindowRect`
  ⇒ `Move` + `EnsureRenderTarget` **重建交换链**。
- 风险（参考实现 `main.rs:757-763` 明确警告）：**layered 窗上重建 swapchain 会失败**。
  这条路径**从未在多屏环境实测过**（你只有单屏需求），所以表现未知 ——
  可能正常、也可能黑一下或崩。
- 若将来有人报「切到第二个屏出问题」，第一件事就是看这里；
  修法即 P6 原本的方案：**每个显示器常驻一个全屏窗**，切屏只换渲染目标、不重建交换链。

---

## 验收标准（P1 完成）

| 项 | 标准 |
|---|---|
| 窗口 | 尺寸 = 显示器矩形；光标大幅移动时连续多帧 `left/top/extent` **完全不变** |
| 黑洞位置 | 随光标连续变化，**不被量化** |
| 抖动 | 匀速拖动时 `center` 平滑单调，折射区不抖 |
| 变黑 | 纯白桌面上移动，折射区无黑块 / 无闪烁 |
| 帧率 | 记录数值（不达标不在本步处理） |
| 回归 | `dotnet build -c Release` 0 警告 0 错误；`dotnet test` 全绿 |

---

## 风险登记

| # | 风险 | 应对 |
|---|---|---|
| R1 | 全屏窗被自己抓屏抓到 | **P0 先验**；失效则必须先解决 |
| R2 | 性能（整屏 0.92M 像素，约 5.3 倍） | 决策 6：本步不处理，留到 P5 |
| R3 | layered 窗上重建 swapchain 会失败 | P1.1 只在尺寸变化时 `Move`；禁止无意义重建 |
| R4 | 星云 alpha 在全屏下失控 | P1.4 复核 + 补「远处 alpha ≈ 0」断言 |
| R5 | 窗口边缘羽化失效导致硬边 | P1.4 复核 |
| R6 | 显示器切换时重建渲染目标可能崩 | P1.1 显式处理「尺寸变化」路径 |
| ~~**R7**~~ | ~~截图补偿（M4）在全屏下可能不工作~~ | ✅ **已确认工作（2026-09-26，假警报）**。给 `ClipboardCompensationHost` 加了分阶段取证日志后重测，整条链全通：注册成功 → 收到 `WM_CLIPBOARDUPDATE` → 调度回调执行 → **已把黑洞合成回剪贴板（1920x1080）**。当初「一条日志都没有」的真正原因是**取证时杀进程太早**：首次补偿要现建 D3D 设备 + 编译着色器，耗时 **2.2~3.5 秒**，在那之前什么都不会写进日志（跳过的两种原因都不成立，自然也没有 skip 日志）⇒ 看起来像「消息压根没到」。**教训：涉及一次性初始化的链路，取证要留足时间，且先加「已进入处理」的日志再判断。** |
