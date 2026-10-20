# BlackHole Cursor — 开发执行计划 v2

> 依据 `prd-v2.md` 编写。上一版 `plan.md`（v1）已全部完成并归档。
> **执行顺序按用户指定：先改渲染核心，再修 bug。** 核心换掉后 bug 的形态会变，先修会返工。
> 每个**子步骤**（M1.1、M1.2 …）结束都要：加测试 → 测试全绿 → 更新本文件 → 停下来汇报。
>
> 📌 **`fixes-v2.md`：工程完工后实机发现的观感缺陷，共 6 条**
> （背景亮度导致光效消失 / 多圈亮环 / 预览与实机差距大 / 关透镜黑圈+炫彩变化 /
> 透镜抖动+不像黑洞 / 星云变化+预设配色）。
> 用户指定**先把整个工程做完（M3 – M6）再统一修**，因此它们不打断本计划；
> 本文件里 **M3 的 B1 / B2 / B3 仍属主线**，照做。观感缺陷排在本计划全部完成之后。
> ✅ **6 条已全部修完（2026-09-24）**，逐条记录见 `fixes-v2.md`，总览见本文件 §8.5。
>
> 🛠️ **环境**：本机原先没有 .NET SDK，已在 M1.5 期间用 winget 装上 **.NET SDK 8.0.425**
> （`C:\Program Files\dotnet`，含 WindowsDesktop 运行时）。
> 注意 PATH 上还有一个只有 5.0.7 运行时的 `C:\Program Files (x86)\dotnet`，
> 裸敲 `dotnet` 会解析到它并报 "no installed .NET SDKs" —— 用完整路径
> `& "C:\Program Files\dotnet\dotnet.exe"` 即可。

---

## 0. M0 — 基线盘点 ✅ 已完成

### 0.1 已具备的可复用资产
| 资产 | 位置 | v2 中的角色 |
|---|---|---|
| D3D11 渲染骨架 | `Core/Rendering/RenderDevice.cs`、`SceneRenderer.cs` | **沿用**，只换着色器与 uniform |
| DirectComposition 透明叠加 | `OverlayController.BindComposition()` | **沿用** |
| 抓屏 + 降级链 | `Core/Capture/*` | **沿用**（M3 补自排除捕获） |
| 覆盖层线程 | `Core/Rendering/OverlayRunner.cs` | 沿用，M3 验证帧率 |
| 惯性跟随 / 静止游荡 / 出现消失 | `FollowPhysics.cs`、`IdleWander.cs`、`TransitionState.cs` | **原样保留** |
| 光标接管与兜底 | `CursorManager.cs`、`RuntimeHost.cs` | **原样保留** |
| 配置 / 预设 / 托盘 / 单实例 | `SettingsService.cs`、`Presets.cs`、`App.xaml.cs` | 改造参数与预设 |
| 渲染测试基础设施 | `tests/SceneRendererTests.cs`（WARP 实渲取像素） | **沿用并扩展** |

### 0.2 测试基线 ✅
`dotnet test` 基线：**313 通过 / 0 失败**。
M1 之后因参数体系替换，与旧参数绑定的用例会被**改写**（不是删除），总数应 ≥ 313。

> M0 期间修复：① `OverlayController` 引用未声明的 `_pendingSeconds` 字段导致整个解决方案编译失败；
> ② 4 处测试断言仍按旧版 `FrameScheduler.ActiveFramesPerSecond` 常量写法调用（现为方法）。

### 0.3 已知缺陷基线 ✅（M3 再修）
| # | 现象 | 当前状态 |
|---|---|---|
| B1 | 帧率高时移速变慢 | ✅ **已在 M3.1 锁死**：注入虚拟时钟，10 项断言覆盖 60/165/240 × 节流/不节流，物理推进量 == 真实经过时间 |
| B2 | 帧率拉不到 165 | 已改为 `TimeBeginPeriod(1)` + `WaitAndPump(1)`（实测 32→57）；**165 未验证** → M3 实测锁死 |
| B3 | 抓屏镜像回廊 | ✅ **已在 M3.3 修掉**：`Win32OverlayWindow` 构造时设 `WDA_EXCLUDEFROMCAPTURE`，4 项断言锁住（含「Move/显隐/置顶后不丢」） |
| ~~B4~~ | ~~N4 出现/消失的**缩放**在 v2 下失效~~ | ✅ 已在 M1.8.3 修掉（`holeFraction *= scale`） |

---

## 1. WGSL → HLSL 翻译对照（M1 全程用）

| WGSL | HLSL |
|---|---|
| `vec2/3<f32>`, `mix`, `fract` | `float2/3`, `lerp`, `frac` |
| `inverseSqrt(x)` | `rsqrt(x)` |
| `select(a,b,cond)` | `cond ? b : a` |
| `gmod(x,y)` | `x - y*floor(x/y)` 原样保留 |
| `textureSampleLevel(t,s,uv,0.0)` | `t.SampleLevel(s, uv, 0)` |
| `for (var i=0;i<N;i++){}` | 同写；`N` 用 uniform 上限 + `break`，避免动态循环被完全展开 |
| `pow(a,b)` 负底数 | 改用 `x*x`（HLSL `pow` 负底数未定义） |

**必须照搬的常量**
```
B_CRIT = 2.5980762     // 临界撞击参数 (3√3/2)·r_s
LENS_DEPTH = 13.0      // 天空平面距离 (r_s)
N_STEPS = 48           // Schwarzschild
KERR_STEPS = 88        // Kerr
bmax = rout + 3.0
Z0 = max(14.0, rout + 5.0)
window = exp(-pow(plen/(7.0*rh), 2.0))     // 远场淡出
ab(色差) = 0.035 * smoothstep(1.0, 2.0, b/bmax)
```

---

## 2. 步骤总览

> **实际执行顺序（已偏离原编号顺序，用户指定）**
> `M0 → M1.1 → M1.2 → M1.3 → M1.4.1 → M1.4.2 → M1.4.3 → M1.4.4 → M2.2 → M2.3 → M2.1 → M5.1 → M1.5 …`
>
> 原因：M1.4.4 把主流程切到 v2 之后，宿主还没传 `Look` / `ScreenHeight`，
> 也没按新半径计算窗口，导致**实机上黑洞偏小、外观写死**。
> 为了让效果先能在桌面上被看到和判断，先做 M2.2 / M2.3（宿主适配）；
> 随后补 M2.1（参数体系）与 M5.1（外观页），**至此 UI 滑块已能真正驱动 v2 渲染**。
> 接下来是 M1.5 – M1.8（远场解析 / Kerr / 星场 / 清理）：
> **M1.5、M1.6、M1.7 均已完成**（474 全绿）—— F1 的 12 项需求至此**全部落地**；
> **M1.8.1 – M1.8.5 已全部完成**（410 全绿）—— v1 参数与派生量已全部移除，src 侧零残留，
> 实机冒烟通过（47 FPS、阴影半径 64.8 px、配置文件零 v1 残留）。
> **M1（渲染核心）整体完成**，下一步进入 M2.4。
>
> **M5.2（8 预设 + 1 秒交叉淡入）已完成**（483 全绿）：预设改为**增量补丁式应用**
> （大小 / 性能档位 / 染色保持用户选择），新增 `LookFade` 并让覆盖层与主界面预览**共用同一实例**。
> **M5.3（旧配置迁移）已完成**（496 全绿）：v1 字段显式识别 → 备份 → 回落默认 → 提示一次。
> **M5（UI / 预设 / 配置迁移）整体完成**，下一步进入 **M6（打磨与发布）**。
> **M6.1（性能与自动降档）已完成**（510 全绿）：自动档按 5 秒均值在 24/48/88 间升降，
> 覆盖层与预览共用同一调节器；165 实测按 M3.2 的决定继续跳过。
> **M6.2（异常与降级矩阵）已完成**（529 全绿）：降级矩阵抽成纯函数、着色器编译失败有可识别异常。
> **M6.3（全量测试 + 发布 + 实机验收）已完成**（529 全绿，22 文件干净发布，§7 共 12 条逐条过）。
> **🎉 M6 完成 = 整个 v2 工程完工**（M0 – M6）。
> 随后的 `fixes-v2.md` **6 条观感缺陷也已全部修完**（2026-09-24，576 全绿），见本文件 §8.5。
> ✅ **问题 5 观感已于 2026-09-24 实机确认达标**（「像了，可以收尾」）—— 观感类问题至此全部闭环。
> ⏸️ 剩下的是性能：抓屏 17ms 是 DWM 同步等待，**「半分辨率抓屏」已实测证伪**（见 `fixes-v2.md` 末尾），
> 唯一出路是「抓屏搬后台线程」，你决定**留到下一轮**。
> 📌 另两项至今未做（都不在本计划步骤内）：**打包安装包**（`installer/*.iss` 从未编译过）、
> **预览改用真实抓屏底图**（现为程序化棋盘，已登记为 prd-v2 R12 的未处理部分）。


| 步骤 | 内容 | 关键验收 |
|---|---|---|
| M1.1 ✅ | 着色器工具层（噪声/黑体/星场/旋转） | 新增 14 项测试，335 通过 |
| M1.2 ✅ | 新 uniform 骨架 + 半径换算 | 新增 32 项测试，367 通过 |
| M1.3 ✅ | 薄盘穿越着色 `ShadeCrossing` | 新增 14 项测试，381 通过 |
| M1.4.1 ✅ | 积分骨架：落入 / 逃逸判定 | 新增 10 项测试，391 通过 |
| M1.4.2 ✅ | 背景反投影 | 新增 9 项测试，401 通过（镜像二次像已确认） |
| M1.4.3 ✅ | 薄盘多次穿越 | 新增 5 项测试，406 通过（远侧光弧、盘遮挡背景） |
| M1.4.4 ✅ | tonemap / 淡出 / 合流主流程 | 新增 5 项测试，411 通过（主流程已切到 v2） |
| M1.5 ✅ | 远场解析分支 + 无缝 + 色差 | 新增 8 项测试，**459 通过**（交界跳变 0.15px、14rh 处 0.021px） |
| M1.6 ✅ | Kerr 自旋 | 新增 7 项测试，**466 通过**（ISCO 3.00→0.80 r_s，盘内径 2.20→0.82） |
| M1.7 ✅ | 星场 + 曝光 + 质量档位 | 新增 8 项测试，**474 通过**（star=1 → 31 个星点，最亮 106/255） |
| M1.8.1 ✅ / M1.8.2 ✅ / M1.8.3 ✅ / M1.8.4 ✅ / M1.8.5 ✅ | 清理 v1 残留（拆 5 小步） | **410 通过 / 0 失败**，src 侧 v1 残留为 0 |
| M2.1 ✅ | SettingsModel 换参数 | 新增 16 项测试，444 通过 |
| M2.2 ✅ | 窗口尺寸按新半径计算 | 11 项纯函数断言，不被裁切 |
| M2.3 ✅ | OverlayController 传参 + 屏高换算 | 5 项映射断言 + 窗口断言改写，428 通过 |
| M2.4 ✅ | 预览同步 | 新增 6 项，**416 通过**；预览与实机像素尺度一致 |
| M3.1 ✅ | 物理/节流解耦测试锁死 | 新增 10 项，**426 通过**；物理推进 == 真实时间 |
| M3.2 ⏭ | 165 帧率达标 | **跳过**（你定的：机器跑不了高帧率，不做这项实测） |
| M3.3 ✅ | 自排除捕获 | 新增 4 项，**430 通过**；亲和性实测设上且不丢 |
| M4.1 ✅ | 剪贴板监听与全屏判定 | 新增 13 项；非全屏图不处理 |
| M4.2 ✅ | 合成回写剪贴板 | 新增 11 项；输出图中心区域含黑洞 |
| M4.3 ✅ | 开关与设置项 | 默认开启，关闭后什么都不做；**462 通过** |
| M5.1 ✅ | 外观页重做 | 新增 7 项测试，451 通过 |
| M5.2 ✅ | 8 预设 + 交叉淡入 | 新增 21 项，**483 通过**；补丁式应用 + 1 秒 smoothstep 淡入 |
| M5.3 ✅ | 旧配置迁移 | 新增 13 项，**496 通过**；v1 字段识别 → 备份 → 回落默认 → 提示一次 |
| M6.1 ✅ | 性能与自动降档 | 新增 14 项，**510 通过**；自动档按 5s 均值升降 24/48/88；软件渲染气泡提示（165 实测按 M3.2 决定跳过） |
| M6.2 ✅ | 异常与降级矩阵 | 新增 19 项，**529 通过**；矩阵抽出为纯函数 `RenderFailure.Decide`；着色器编译失败有可识别异常 |
| M6.3 ✅ | 全量测试 + 发布 + 实机验收 | **529 通过 / 0 失败**；干净发布 **21** 文件（⚠️ 此处原记 22，2026-09-24 复核为 21，见该节批注）；§7 共 12 条逐条过（仅第 8 条 165Hz 按 M3.2 跳过） |

---

## 3. M1 — 渲染核心（逐像素零测地线）

### M1.1 着色器工具层 ✅ 已完成
**改** `Shaders/common.hlsl`（只新增，不动现有主流程）
- [x] `gmod(x,y)`、`mirrorUV(uv)`、`rot(v,a)`
- [x] `vnoiseWrapY(p, perY)`（复用已有 `Hash21`；角向 wrap，跨 atan 分支无缝）
- [x] `blackbody(T)`（Tanner Helland 拟合，clamp 1500–40000K）
- [x] `stars(dir)`（按弯折后方向索引的稀疏星点）
- [x] `Extra` 之后新增 `Debug` float4，配套 C#：`SceneConstants.Debug`、`ShaderProbe` 记录、`SceneDescription.Probe` 可选参数
- [x] 两个验证入口 `ToolsColorProbePS` / `ToolsTransformProbePS` + `SceneRenderer.CreatePixelShader` / `Render(..., ID3D11PixelShader?)`

**踩坑记录**：HLSL 的数值构造器**不接受标量展开**（WGSL 的 `vec3(0.0)` 可以，`float3(0.0)` 不行），必须写满分量。

**测试**（`tests/ShaderToolsTests.cs`，新增 14 项）：
- [x] 验证入口可编译；工具函数确实出现在着色器源码中
- [x] 黑体：1500/2500/3200K 偏暖（R>B）；9000/15000/20000K 偏冷（B>R）
- [x] 黑体：6600K 接近纯白、1500K 无蓝分量、40000K 蓝占优、越界输入被 clamp
- [x] 黑体：蓝通道随色温单调上升
- [x] wrap 噪声：像素间有明显起伏、取值不越界
- [x] 旋转保持向量长度、不同角度输出不同
- [x] `mirrorUV` 在 0..1 间折返
- [x] `SceneParamsBuilder`：Debug 探针参数正确下发、默认值安全；`FloatCount` 断言同步为 40

**结果**：`dotnet test` **335 通过 / 0 失败**（基线 313）；Release 构建 0 警告 0 错误。

### M1.2 新 uniform 骨架与半径换算 ✅ 已完成
**改** `Rendering/SceneConstants.cs`、`Rendering/LensMath.cs`、**新增** `Rendering/BlackHoleLook.cs`
- [x] 新增 `BlackHoleLook`（v2 外观模型，**刻意独立于 SettingsModel**，M2.1 再映射）+ `LookPresets`（8 预设原值照搬）
- [x] `SceneConstants` 增加 v2 字段：`Hole`(半径/W/步数)、`Look0–3`(色温/倾角/roll/染色、内外径/不透明度/多普勒、beam/gain/对比/缠绕、速度/曝光/星场/自旋)、`Tint`；**旧字段暂时保留**以免破坏旧着色器（FloatCount 40→64）
- [x] `SceneDescription` 增加可选 `Look` 与 `ScreenHeight`（覆盖层要按**屏幕**高度算半径，不是窗口高度）
- [x] `SceneParamsBuilder.Build` 填充新字段
- [x] `LensMath` 增加 `CriticalImpactParameter`、`WorldScale(W=B_CRIT/rh)`、`ShadowRadiusPixels`、`CameraDistance`、`MaxImpactParameter`
- [x] HLSL cbuffer 同步新增这 6 个 float4（保持与 C# 一致）

**测试**（`tests/BlackHoleLookTests.cs`，新增 32 项）：
- [x] `BlackHoleLook` 默认值 = Gargantua、全字段钳制、外径>内径、NaN 修复、Clone 独立
- [x] 8 个预设逐项对照 prd-v2 §6.3 原值（含 Inferno→Zen 全表）、预设本身合法、未知名回落、预设不覆盖 HoleSize
- [x] 几何换算：b_crit = 3√3/2、W 随半径增大而减小、除零保护、像素半径随屏高缩放、相机距离容纳盘、远场阈值
- [x] uniform 映射：Hole/Look0–3/Tint 逐通道断言、未传 Look 时用默认、FloatCount = 64、ScreenHeight 不改变屏高单位下的半径

**结果**：`dotnet test` **367 通过 / 0 失败**；Release 构建 0 警告 0 错误。

### M1.3 薄盘穿越着色 ✅ 已完成
**新增** `Shaders/disk.hlsl`（并加入 `ShaderSources` 与 csproj 嵌入资源）
- [x] `ShadeCrossing(xc, vdir, n, e2, rin, rout, t, sdir, spd, trans)`
  - 开普勒角速度 `kep = pow(rin/rc, 1.5)`
  - 引力时间膨胀 `gloc = sqrt(max(1 - 1.5/rc, 0.02))`
  - 条纹 `swirl = rc*wind*0.12 - t*kep*spd*gloc*sdir`（用 M1.1 的 `vnoiseWrapY`）
  - 多普勒 `beta = clamp(rsqrt(max(2*(rc-1),0.2)),0,0.99)`；`g = gloc/(1+beta·dot(gasdir,vdir))`；`boost = pow(g, beam)`
  - Shakura-Sunyaev `tprof = pow(rin/rc,0.75)*pow(max(1-sqrt(rin/rc),0),0.25)/0.488`
  - 染色：**乘性**叠加在黑体色上（`lerp(1, Tint.rgb, TintStrength)`），保留黑体亮度梯度
  - 返回 `float4(trans * cbb * (gain*2.2*density*tprof²*boost), density)`
- [x] 验证入口 `DiskProbePS`（uv.x → 穿越半径，uv.y → 方位角，`Debug.w` → 观察者方向角）

**踩坑记录**：多普勒不能靠「沿整个环求平均」来验证 —— 迎面亮、背面暗会互相抵消。
必须逐像素比对两个相反观察方向；`dopp=0` 时两幅图应**完全一致**。

**测试**（`tests/DiskShadingTests.cs`，新增 14 项）：
- [x] 盘内有发光与密度；内径以内、外径以外均为 0
- [x] 发光峰值靠近内径（Shakura-Sunyaev 剖面，理论峰值 ≈ 1.36·rin）
- [x] 多普勒：改变观察方向会改变同一处发光；`dopp=0` 时两方向图像完全一致；内圈效应强于外圈
- [x] 色温决定盘颜色（3000K 偏红 / 16000K 偏蓝）；增益放大亮度
- [x] 染色改变颜色、`TintStrength=0` 时不改变；条纹沿轨道有起伏；反向公转仍发光

**结果**：`dotnet test` **381 通过 / 0 失败**；Release 构建 0 警告 0 错误。

### M1.4 Schwarzschild 测地线主流程（核心一步，拆为 4 个检查点）

#### M1.4.1 积分骨架：只判「落入 / 逃逸」 ✅ 已完成
**改** `Shaders/lens.hlsl`（新增 `background()`、`TraceGeodesic()`、验证入口 `GeodesicProbePS`，**不动**现有主流程）
- [x] 屏幕↔世界：`center = CenterRadius.xy / Viewport.xy`；`aspect = W/H`；`p = (uv-center)*float2(aspect,1)`
- [ ] `pr = rot(float2(p.x,-p.y), roll) * W`（`W = Hole.y`）；`h2 = dot(pr,pr)`
- [ ] 相机 `Z0 = max(14, rout+5)`；初始 `x = float3(pr, Z0)`、`v = (0,0,-1)`
- [ ] 48 步 leapfrog：`acc = -1.5*h2*x/(r²·r²·r)`；`dt = clamp(0.16*r, 0.03, 1.5)`；kick-drift-kick
- [ ] 退出判定：落入 `r²<1`、逃逸 `x.z<-Z0 && v.z<0`、飞远 `r²>4Z0²`；步数耗尽仍在缠绕 → 计入落入
- [x] 本步**只**输出：落入 → 纯黑；逃逸 → 原样取背景（不做反投影、不接盘）
- [x] `TraceGeodesic(pr, z0, steps, out escaped)`：leapfrog kick-drift-kick，步数耗尽仍在缠绕 → 计入落入

**测试**（`tests/GeodesicTests.cs`，新增 10 项）：
- [x] 阴影中心为纯黑；远离黑洞处仍显示桌面背景
- [x] 阴影半径实测 ≈ `ShadowRadiusPixels(HoleSize, 屏高)`（0.15 → 约 38px，误差 <15%）
- [x] `HoleSize` 6% → 15% 黑色面积增长约 6 倍
- [x] 阴影近似圆形（左右对称、上下对称、横纵半径差 ≤3px）
- [x] 积分步数 24 / 88 不改变阴影大小
- [x] 阴影跟随指定的中心位置；整体旋转 `Roll` 不改变阴影大小；6% 阴影不覆盖大片画面

**结果**：`dotnet test` **391 通过 / 0 失败**；Release 构建 0 警告 0 错误。
（真阴影已由测地线积分自然涌现，不是画的圆）

#### M1.4.2 背景反投影：爱因斯坦环 + 镜像二次像 ✅ 已完成
- [x] `TraceGeodesic` 改为输出终态 `outX / outV`（供反投影使用）
- [x] `ReprojectToSky(x, v, ...)`：逃逸光线投到 `z = -LENS_DEPTH` 平面
  `tpl = (-LENS_DEPTH - x.z)/d.z` → `hp = x + d*tpl` → `q = rot(hp.xy,-roll)/W` → `sp = (q.x,-q.y)` → `suv = center + sp/(aspect,1)`
- [x] 弯折过度（`-d.z` 太小，到不了天空平面）的射线淡出 `toward = smoothstep(0.05,0.35,-d.z)`
- [x] `Debug.x < 0` → 关闭反投影（供测试做「有/无反投影」对照）
- [x] HLSL 补上 `LENS_DEPTH = 13.0` / `B_CRIT = 2.5980762`（与 `LensMath` 同名常量一致）

**测试技巧（值得复用）**：背景改用「坐标编码」纹理 —— R = uv.x 渐变、G = uv.y 渐变、B = 常量 255。
于是 `R/B`、`G/B` 就是该像素**实际取样的 uv**，且这个比值不受 `toward` 整体缩放影响，可直接反推位移量。
注意 BGRA8 的字节顺序是 B,G,R,A（第一版把字节序写反了，量出 600px 的荒唐位移）。

**测试**（`tests/GeodesicReprojectionTests.cs`，新增 9 项）：
- [x] 关闭反投影时取样位置 = 像素自身位置（位移 ≈ 0）
- [x] 开启后画面明显变化
- [x] **位移随距离单调衰减**（2rh > 4rh > 8rh）；远场位移 < 近场的 1/5
- [x] **镜像二次像**：紧贴阴影外侧确实存在取到黑洞另一侧的采样点
- [x] 近环弯折强度 > 远场 3 倍以上；黑洞越大同样屏幕距离弯折越强
- [x] 24 步与 88 步结果大体一致（差异 < 25%）

**同时修正 M1.4.1 的测试**：量阴影尺寸必须**关闭**反投影 —— 开启后紧贴阴影的过度弯折光线会被淡出成黑，
暗区比真阴影更大。另补一条「反投影不应缩小暗区」。

**结果**：`dotnet test` **401 通过 / 0 失败**；Release 构建 0 警告 0 错误。

#### M1.4.3 接入薄盘多次穿越 ✅ 已完成
- [x] 盘基向量 `n = (0, sin incl, cos incl)`、`e2 = (0, cos incl, -sin incl)`
- [x] 穿越检测 `s*sPrev < 0` → 线性插值交点 → `ShadeCrossing`（一条光线可多次穿越）
- [x] `trans *= (1 - clamp(opac*density, 0, 1))`；`trans < 0.02` 时提前结束
- [x] `TraceGeodesic` 增加 `withDisk` / `out emit` / `out trans`；新增完整版验证入口 `GeodesicDiskProbePS`
      （输出 `背景 × trans + 盘的累积发光`，tonemap 留给 M1.4.4）

**测试技巧（值得复用）**：预览底图是**非均匀棋盘**，直接比较两个不同位置的亮度不可靠。
改为「同一像素、有盘 vs 无盘」对照（无盘 = `Gain 0` + `Opacity 0`），背景图案的影响被完全抵消。
同理，验证遮挡时把 `Gain` 设为 0，让盘只遮挡不发光，才能单独量化背景被吃掉多少。

**测试**（`tests/GeodesicDiskTests.cs`，新增 5 项）：
- [x] `GeodesicDiskProbePS` 可编译，输出缓冲完整
- [x] 阴影外侧一圈有盘时显著变亮（远侧光弧 + 近侧，证明多次穿越）
- [x] 盘光不泄漏到远离黑洞处（防止「整体提亮」的假通过）
- [x] `Opacity = 0` 时完全不遮挡背景
- [x] 提高 `opac` 后遮挡的像素数更多（0.25 → 1.0 单调递增）

**结果**：`dotnet test` **406 通过 / 0 失败**；Release 构建 0 警告 0 错误。

#### M1.4.4 tonemap / 淡出窗口 / 合流主流程 ✅ 已完成
- [x] `col = bg*trans + (1 - exp(-emit*expo))`（HDR → tonemap，盘光不糊成白团）
- [x] 远场淡出窗口 `window = exp(-pow(plen/(7*rh), 2))`；
      远处用 `lerp(原始像素, 弯折后像素, window)` 平滑换回，覆盖层边缘无硬边
- [x] `CompositePS` 换成 v2 流程；alpha = `max(window, 1-trans)`（真阴影不透明，
      盘遮挡处也不被背景冲淡），并保留 N4 的 `Extra.w` 淡入淡出（rgb 与 a 一起乘）
- [x] v1 旧流程保留为 `LegacyCompositePS`，仅给 v1 视觉回归测试用（**M1.8 必须移除**）

**踩坑记录 1（值得记住）**：阴影中心**不是纯黑**是正确的。
盘倾角 1.52 rad（≈87° 侧视）时，中心那条视线会在 `z ≈ +3.0 r_s` 处穿过盘的**近侧**，
而该处半径落在 `[rin=2.2, rout=7]` 内 → 产生发光。这正是 Gargantua 造型里
「近侧盘面横穿阴影中央」的那条亮带。不要把它当 bug 去「修黑」。

**踩坑记录 2**：v1 的 18 项视觉断言在换主流程后**必然失败**，因为它们按 v1 几何取样
（视界 14px、盘 33px），而 v2 的尺度完全不同（Size=128 下阴影仅 ~7.7px）。
处理办法：让这些用例显式走 `LegacyCompositePS`，把「改写为 v2 断言」留给 M1.8。
（plan 原写「既有断言不回退」在参数体系替换的前提下不成立，已按实际情况调整。）

**已知限制（M2.2 / M2.3 解决）**：目前 `Hole.x` 按**视口高度**而非屏幕高度换算，
宿主也还没传 `Look` / `ScreenHeight`，所以实机上黑洞会**偏小**且固定用 Gargantua 外观。

**测试**（`tests/GeodesicCompositeTests.cs`，新增 5 项）：
- [x] `CompositePS` 可编译并渲染出完整缓冲
- [x] 阴影中心纯黑且不透明（prd-v2 §7.1）
- [x] 曝光提高后盘峰值亮度不降低（tonemap 方向正确）
- [x] 远场 alpha 由内向外单调衰减，远处基本透出桌面
- [x] 远场淡出平滑：沿半径方向 alpha 无突变（<12）

**结果**：`dotnet test` **411 通过 / 0 失败**；Release 构建 0 警告 0 错误。

### M1.5 远场解析分支 + 无缝 + 色差 ✅ 已完成

**改** `Shaders/lens.hlsl`、`Shaders/composite.hlsl`
- [x] 新增 `LensSample` + `ResolveLens(uv, center, aspect)`：
      `b >= bmax`（= rout + 3）走解析远场，否则走测地线积分 —— 两条分支共用一个返回结构
- [x] 解析式照搬参考实现：`uu = Z0·rsqrt(Z0²+b²)`；
      `defl = (2/W²)/plen · (1.29·uu + 0.07) · max(LENS_DEPTH - 2.14·uu + 0.75, 0) · window`
- [x] 色差 `ab = 0.035·smoothstep(1, 2, b/bmax)`：红 `(1-ab)` / 绿 `1` / 蓝 `(1+ab)`，三通道分别取样
      （交界圆上 smoothstep = 0，色差不破坏无缝）
- [x] `bmax = rout + 3.0` 之外的光线不可能碰到盘 → 不再跑 48~88 步积分，
      这是覆盖层面积占比最大的区域，**主要的性能收益来源**
- [x] **无缝的关键**：`ReprojectToSky` 改为返回**屏幕 p 空间**的源位置；近场改成
      `spw = p + (sp - p)·window`（淡出**位移**而不是颜色），与远场共用同一个 window → 交界圆上位移连续
- [x] `CompositePS` 改为调用 `ResolveLens`；远场三通道取样、近场单通道取样（省两次取样）
- [x] 新增验证入口 `FarFieldProbePS`：输出该像素最终取样的 uv（rg = 绿通道基准，ba = 蓝通道）

**踩坑记录（值得记住）**：M1.4.4 原本是 `lerp(原像素, 弯折像素, window)` —— 淡出的是**颜色**。
远处 window≈0.74 时它与「淡出位移」并不等价，交界圆两侧会出现一条可见的接缝。
参考实现的注释其实早就写明：*fade the displacement, never the color*。
把近场也改成位移淡出后，两侧用同一个 window，位移连续 → 颜色自然连续。

**测试**（`tests/FarFieldLensingTests.cs`，新增 8 项）：
- [x] `ResolveLens` / `FarFieldProbePS` 存在于着色器源码且可编译
- [x] 交界圆无接缝：跨边界的**单位距离跳变** < 交界圆内侧自然梯度 × 1.5 + 0.05
- [x] 两侧位移一致：交界圆内外各 1px 处的平均位移相对差 < 20%
- [x] 远场位移随距离单调衰减（rb+5 > rb+15 > rb+30，且最远处 < 近处的 1/2）
- [x] 14 个阴影半径之外位移 < 0.5px（"不抖动"）
- [x] 色差：b/bmax = 2 处蓝光位移 > 绿光（弯得更多）
- [x] 色差在交界圆处消失（|蓝 − 绿| < 0.05px）
- [x] 远场不受积分步数影响（24 vs 88 **逐字节相同**），近场会变（防断言空转）

**踩坑记录 2（测量方法，两个都值得记住）**：验证入口一开始直接输出**取样 uv**，结果远场测出
0.42px 的位移，而解析式算出来只有 0.02px —— 20 倍。原因是 8 位 UNORM 把 uv 存成
`round(255·uv)/255`，相当于把整张图按 **256/255** 缩放，测出的位移带一个 **r/255 px 的外向偏差**
（r = 到中心距离；r=107px 时正好 0.42px，与实测值 4 位有效数字相符），远处信号本身≈0 时它完全掩盖真相。
改成输出「取样位置 − 像素自身位置」后偏差只剩 1/255 的**相对**误差。

**踩坑记录 3**：位移在同一圈上是**常数**，所以量化误差不会因整圈平均而抵消，只会留下一个固定的
舍入偏差（增益 5 时步长 0.2px，足以伪造出 0.1px 的"位移"）。
解决办法是**自动量程**：先 gain=5 粗测，再按实测偏移把增益调到接近满量程（偏移约 0.4/0.5），
把步长压到远小于被测值。最终远场测到 **0.0206px**，与手算解析值 **0.0206px** 完全吻合 —— 解析分支可确认正确。

**实测数据**（Size=256，HoleSize=0.03，Gargantua 的 rout=7 → bmax=10，交界圆 rb=29.56px）：

| 量 | 实测 |
|---|---|
| 交界圆内侧（rb−1）位移 | −6.283px |
| 交界圆外侧（rb+1）位移 | −5.513px |
| 近侧自然梯度 | 0.46px/px |
| **真实跳变**（外侧 vs 近侧线性外推） | **0.15px（约 2.8%）** |
| 14 个阴影半径处位移 | 0.0206px（解析值 0.0206px） |
| 色差 蓝/绿 位移比 | 1.0348（设计值 1+0.035 = 1.035） |
| 远场 24 步 vs 88 步 | 逐字节相同（证明没跑积分） |

**注意**：直接比较「rb−1 与 rb+1」会得到 12.26% 的"差异"，但那 2px 间隔本身就含 0.92px 的自然梯度，
**不是接缝**。真实接缝是 0.15px，亚像素级别，肉眼不可见。

**与参考实现的偏差**：参考实现声称交界圆上位移差 < 1%，我们实测约 **2.8% / 0.15px**。
拟合式的四个系数（1.29 / 0.07 / 2.14 / 0.75）是对**它的**积分器拟合出来的，
与我们的积分器有约 3% 的系统差。当前影响是亚像素、不可见，**留作 M6.1 打磨时评估是否重新拟合**。

**结果**：`dotnet build -c Release` **0 警告 0 错误**；`dotnet test` **459 通过 / 0 失败**（基线 451 + 新增 8）。

### M1.6 Kerr 自旋 ✅ 已完成

**新增** `Shaders/kerr.hlsl`（已加入 csproj 嵌入资源与 `ShaderSources.Parts`，排在 `lens.hlsl` 之前）
- [x] `KerrIscoRs(spin)`（Bardeen 1972，顺行 ISCO，单位 r_s）
- [x] `KerrRadius`（Kerr-Schild 的 Boyer-Lindquist 半径）、`KerrHamiltonian`、
      `KerrDxdl`（解析）、`KerrDpdl`（对 H 做中心差分 `e=2e-3`）
      —— M = 0.5 保证 r_s = 1，与 Schwarzschild 路径的单位完全一致
- [x] `DragTwist(b, spin) = 1.3·spin/(1+0.8·(b/B_CRIT)²)`（夸张化帧拖拽；spin=0 时严格为 0）
- [x] `TraceKerr`：在以自旋轴（= 盘法线）为 +z 的**黑洞坐标系**里 leapfrog，
      `a = 0.5·spin·sdir`、`rhor = 0.5+sqrt(0.25−a²)`、穿越赤道面即盘面，交点与切向都转回世界坐标再复用 `ShadeCrossing`
- [x] `lens.hlsl` 里把原来的循环抽成 `TraceSchwarzschild`，`TraceGeodesic` 按 `spin < 0.005` 分派
- [x] `DragTwist` 同时接进 M1.5 的远场解析分支与近场反投影（参考实现两处都加）
- [x] 新增验证入口 `KerrProbePS`：R = ISCO/4，G = 视界半径/2，B = 盘内径 rinK/8
- [x] `common.hlsl` 新增 `DiskGeom`，两条积分路径共用同一份「盘怎么着色」

**有意偏离参考实现 1（盘内径）**：参考写 `rin_k = max(rin·ISCO/3, ISCO)`，
但 `ISCO(spin→0) = 3 r_s`，而 Schwarzschild 路径用的是 `rin`（Gargantua = 2.2）——
照搬会让滑块从 0 推过 0.005 时盘内径从 2.2 **突跳到 3.0**。
改成 `max(rin·ISCO/3, min(ISCO, rin))` 后：spin→0 时连续等于 rin，高自旋时仍收缩到 ISCO。

**踩坑记录（测量方法）**：帧拖拽的不对称度量一开始用棋盘底图，结果 spin=0 就有 5.95 的"不对称"
（棋盘自身左右不对称），把信号完全淹没了。换成**只随 y 变化的竖直渐变**后：
纯径向的透镜位移不会破坏左右镜像对称（镜像两点取样的 y 相同），不对称度就成了帧拖拽的干净度量。

**实测数据**（`KerrProbePS`，rin = 2.2）：

| spin | ISCO (r_s) | 视界半径 | 盘内径 rinK |
|---|---|---|---|
| 0.0 | 2.996 | 1.004 | 2.196 |
| 0.3 | 2.494 | 0.980 | 2.196 |
| 0.5 | 2.118 | 0.933 | 2.102 |
| 0.9 | 1.161 | 0.722 | 1.161 |
| 0.98 | 0.800 | 0.596 | 0.816 |

ISCO 与 Bardeen 1972 的解析值（6M / 4.98M / 4.23M / 2.32M / 1.6M → 换算成 r_s）逐项吻合。

画面侧：spin=0 vs 0.9 的近场平均像素距离 **14.18**（全图 3.82）；
关掉盘只留透镜时，左右镜像不对称度 **0.954 → 2.607**（帧拖拽确实打破了镜像对称）。

**测试**（`tests/KerrTests.cs`，新增 7 项）：
- [x] Kerr 函数存在于着色器且 `KerrProbePS` 可编译
- [x] ISCO 随自旋收缩：3.00 → 2.12 → 1.16（spin=0 处断言 = 3 r_s）
- [x] 盘内径随自旋收缩到 ISCO 附近，**且 spin→0 时连续等于 rin**（防"跳一下"）
- [x] 视界半径随自旋收缩（1.00 → 0.72）
- [x] spin=0.9 与 spin=0 的近场画面明显不同
- [x] 帧拖拽打破左右镜像对称（0.954 → 2.607）
- [x] 快速路径阈值 0.005 两侧不出现跳变

**已知问题（已登记为 prd-v2 R9）**：阈值 0.005 两侧的画面距离实测
`0.004 → 0.01`、`0.006 → 5.61`（近场平均灰阶）。原因是 Kerr 路径的步长钳制
（`clamp(0.16r, 0.02, 1.1)`）与逃逸判据都和 Schwarzschild 的不同，spin→0 时两者并不逐点相等。
滑块从 0 往上推过 0.005 时会有一次轻微"变脸"，M6.1 再决定是否在 0.005–0.02 做交叉淡入。

**结果**：`dotnet build -c Release` **0 警告 0 错误**；`dotnet test` **466 通过 / 0 失败**（459 + 新增 7）。

### M1.7 星场 + 曝光 + 质量档位 ✅ 已完成

**改** `Shaders/lens.hlsl`、`Shaders/composite.hlsl`
- [x] `LensSample` 增加 `star` 字段。两条分支都按**弯折后**的光线方向索引星场：
      近场用积分终态 `normalize(finalV)`；远场不积分，直接用解析弱偏折角
      `normalize(float3(-pr·(2/b²), -1))`（参考实现同一招，省一次采样）
- [x] `CompositePS` 里 `bg += s.star`：星场与背景一起被盘的透射率衰减
      （盘前面的星星会被盘挡住），落入视界的光线不产生星点 → 阴影保持纯黑
- [x] **曝光** `1 - exp(-emit·expo)`：M1.4.4 已生效，本步补断言
- [x] **质量档位** Low24 / Medium48 / High88：M2.1 + M5.1 已端到端接通
      （`SettingsModel.QualitySteps` → `EffectiveStepCount` → `Hole.z`），本步补三档渲染断言；
      UI 下拉也在 M5.1 就绑好了

**测试**（`tests/StarFieldTests.cs`，新增 8 项）

隔离方式：底图用**纯黑**、盘关掉（`Gain = 0`、`Opacity = 0`）——
于是画面里除了星点没有别的光源，星点数量与亮度可以直接量化。

- [x] `s.star = stars(` / `bg += s.star;` 确实出现在主流程（防「M1.1 就定义了却一直没接」）
- [x] `star = 0` → 0 个亮像素；`star = 1` → 31 个亮像素（> 15 灰阶），占全图 0.05%（**稀疏**，不是整片提亮）
- [x] `star` 0.3 → 1.0 总亮度 **454 → 1539**（3.4 倍，与增益成正比）
- [x] 星场不漏进阴影（中心仍纯黑）
- [x] 三档步数 **24 / 48 / 88** 都能渲染（Theory ×3）：缓冲完整、阴影仍纯黑、星点仍可见

**实测**：256² 视口、HoleSize = 0.10、star = 1 → 31 个亮像素（> 15），40 个（> 8），最亮星点 **106/255**。

**结果**：`dotnet build -c Release` **0 警告 0 错误**；`dotnet test` **474 通过 / 0 失败**（466 + 新增 8）。

### M1.8 清理 v1 残留（拆为 5 个小步）

| 小步 | 内容 | 状态 |
|---|---|---|
| M1.8.1 | `SceneRendererTests` 的 v1 断言切到 `CompositePS` | ✅ |
| M1.8.2 | 删 v1 着色器：`blackhole.hlsl` + `SampleLensed` + `LegacyCompositePS` + v1 常量/工具 | ✅ |
| M1.8.3 | `SceneConstants` 收缩 + cbuffer 同步 + 删 `LensMath` v1 段 / `GlowMath` / `DebrisMath` | ✅ |
| M1.8.4 | 删 `SettingsModel` v1 参数 + `LensView` 清理 + 预设改写 v2 字段 | ✅ 410 通过 |
| M1.8.5 | 全量回归 + 发布 `dist` + 实机冒烟 | ✅ **410 通过**，实机 47 FPS |

#### M1.8.4 ✅ 已完成：删 v1 参数 + LensView + 预设改 v2 字段

> 接手时代码库是**编译不过**的：上一轮已删掉 v1 派生属性
> （`EffectiveLensRadius` / `EffectiveHorizonRadius` / `EffectiveRing*` / `VisualRadius`），
> 但 `LensView.xaml.cs`、`SettingsV2Tests`、`SettingsModelTests` 还在引用 → 10 个编译错误。
> 本步一次性把 src 与 tests 两侧收干净。

**`SettingsModel` / `SettingsRanges` 收缩**

删 16 个 v1 参数：`HorizonRadius`、`RingInnerColor`、`RingOuterColor`、`GradientEnabled`、
`GlowIntensity`、`RingOpacity`、`RingRadius`、`RingThickness`、`GlobalScale`、`RingTilt`、
`RingRoll`、`RingSpinSpeed`、`RingDebrisEnabled`、`RingDebrisCount`、`LensStrength`、`LensRadius`。
`SettingsRanges` 里对应的 v1 段一并删（含 N2 碎石带的 `RingDebrisCount*`）。
保留 `LensEnabled` —— 它是 v2 参数（prd-v2 §6.1），语义收窄为「是否抓屏」：
v2 的偏折量由测地线积分天然决定，**没有**可手调的「强度 / 半径」。
`Clamp()` 末尾对 `_horizonRadius` / `_globalScale` 的 NaN 兜底同时删除。

**`LensView` 清理**：删掉「扭曲强度 / 影响半径」两个滑块与依赖 `EffectiveLensRadius` 的提示文字，
只留 `LensEnabled` 开关 + 一段说明（引导用户去外观页调阴影大小与盘外径）。
`.xaml.cs` 退化为只有 `InitializeComponent()`。这是唯一还绑 v1 参数的页面，至此 XAML 侧零残留。

**预设改写 v2 字段**：`PresetLibrary` 原来自己维护第二份 v1 数值表（经典橙 / 蓝矮星 / 冷白 / 深空紫 +
`ApplyAppearanceSkeleton`）。现在改为**直接复用 `LookPresets` 的 8 个预设**（prd-v2 §6.3）作为唯一真源，
新增反向映射 `LookPresets.ApplyTo(look, settings)`。这样 M5.2 只需做「1 秒交叉淡入 + 补丁式应用」，
数值表已经就位。

**`ApplyTo` 刻意不写 `HoleSize` 与 `QualitySteps`** —— 大小与性能档位是用户选择，不是「外观」。
（✅ M5.2 已把外观页的应用方式改成增量补丁 `ApplyAppearanceFrom`，这条真正成立；
`CopyFrom` 仅保留给「撤销 / 恢复默认 / 载入配置」这类**整体**替换场景。
已有断言锁住「预设本身不给出这两项」。）

**顺带修掉的连带点**：`FrameScheduler.HasAnimation` 仍按 `RingSpinSpeed > 0` 判断，
改用 `Math.Abs(DiskSpeed) > 0`（v2 负值是反向公转，同样算「在动」，否则负速时会误降帧）。

**测试改写**（415 → 410，净 −5）

- `SettingsModelTests`：删 3 项 v1 派生量用例（`EffectiveLensRadius` / `EffectiveRadii` / `VisualRadius`）；
  `Defaults_*` 与 `Clamp_*` 改绑 v2 参数
- `SettingsServiceTests`：落盘往返、越界钳制、撤销 / 重置等 8 处改绑 v2 参数
- `PresetServiceTests`：**重写**。原「配色不同 / 共用骨架 / 发光区间 / 经典橙复刻」四项都是 v1 概念，
  换成 `BuiltInPresets_MatchTheLookPresetsTable`（配置侧 8 预设 == 渲染侧 `LookPresets`，防两边漂移）、
  `BuiltInPresets_AreMutuallyDistinct`、`BuiltInPresets_DoNotAuthorSizeOrQuality`、
  `BuiltInPresets_ExposureStaysInAUsableRange`、`Gargantua_MatchesPrdTable`（逐项对照 §6.3）
- `SettingsV2Tests`：`DerivedValues_AreNotSerialized` 改为断言 **v1 字段不再出现在 JSON 里**
- `OverlayControllerTests`：`LensRadiusChange_DoesNotResizeWindow` → `DiskTempChange_DoesNotResizeWindow`
  （改纯外观参数不该重建交换链）
- `FrameSchedulerTests` / `IdleWanderTests`：`RingSpinSpeed` → `DiskSpeed`

**踩坑记录（值得记住）**：`Presets.cs` 新增 `using BlackHoleCursor.Core.Rendering;` ——
`LookPresets` 在 `Core.Rendering` 命名空间下，而 `SettingsModel` 在 `Core`，
删掉 v1 字段后这个文件才第一次需要引用渲染侧，容易漏。

**结果**：`dotnet build -c Release` **0 警告 0 错误**；`dotnet test` **410 通过 / 0 失败**。
全库检索 v1 参数名（`src/**/*.{cs,xaml,hlsl}`）**0 命中**。

#### M1.8.5 ✅ 已完成：全量回归 + 发布 + 实机冒烟

**全量回归**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **410 通过 / 0 失败**。

**发布**（先 `Remove-Item dist -Recurse` 再 `dotnet publish -c Release -o dist`）：
产出 21 个文件 + `runtimes/`，exe 可正常启动。原「dist 必须 8 个文件」的结论已失效，见上方修正说明。

**实机冒烟（可复用的方法）**：用 App 自带的诊断开关 `--no-takeover` 启动 ——
本次运行**不接管系统光标**，所以即使最后用 `Stop-Process` 强杀也不会让用户「失去鼠标」：

```powershell
$log = "$env:LOCALAPPDATA\BlackHoleCursor\log.txt"
Remove-Item $log -Force -ErrorAction SilentlyContinue
$p = Start-Process .\BlackHoleCursor.exe -ArgumentList "--no-takeover" -PassThru
Start-Sleep -Seconds 25
$p.HasExited; $p.Kill()
Get-Content $log -Encoding UTF8 -Tail 12     # 必须 -Encoding UTF8，否则中文全乱码
```

**实测结果**（默认 Gargantua，屏高 1080）：

| 量 | 实测 |
|---|---|
| 首帧 | 窗口 872×872，窗口半径 435.7，阴影半径 **64.8 px**（= 0.06 × 1080 ✓） |
| 交换链 | `FlipDiscard/Premultiplied` ✓ |
| 帧率（近 5s 平均） | 39.2 → 42.4 → 45.7 → **47.0 FPS**（向 60 目标收敛） |
| 分段耗时 | 抓屏 21.20 / 渲染 0.01 / Present 0.30 / 整帧 21.94 ms |
| 透镜 | 扭曲=开、全黑=False ✓ |

**注意两个观察点（不阻断，留给 M6.1）**：
1. **抓屏降级=True**：本次 DXGI Desktop Duplication 不可用，走了 GDI BitBlt 兜底，
   抓屏 21ms 占整帧 21.94ms 的 **97%** —— 渲染本身（0.01ms）根本不是瓶颈。
   正常路径下（M2.3 实测抓屏 0.09ms）整帧只有 0.56ms，差距全部来自降级。
2. 启动时日志里的「启动后 5s 平均 0.2 FPS」是**预热假象**，别拿它当性能结论；
   等第二个 5s 窗口再读就回到 39+ FPS 了。

**配置文件核对**（实机写出的 `%LOCALAPPDATA%\BlackHoleCursor\settings.json`）：
9 个 v1 字段名（`HorizonRadius` / `RingRadius` / `RingSpinSpeed` / `GlowIntensity` /
`GlobalScale` / `LensStrength` / `LensRadius` / `RingDebris*` / `GradientEnabled`）**0 命中**；
`HoleSize` / `DiskTemp` / `DiskIncl` / `DiskOuter` / `Spin` / `StarGain` / `QualitySteps` /
`LensEnabled` 全部在位 —— M1.8.4 的配置侧清理在实机上确认生效。

> 未覆盖：窗口内 8 个预设的**切换**与外观页滑块联动（需 UI 自动化或人工点一遍）。
> 这部分等 M5.2 做完交叉淡入后一起人工验收。

#### M1.8.3 ✅ 已完成：常量收缩 + 删除 v1 数学

**`SceneConstants` 收缩：11 行 × float4，`FloatCount` 64 → 44**

| 保留 | 删除 |
|---|---|
| `Viewport` / `CenterRadius.xy` / `Timing.x`(时间) / `Timing.w`(透镜开关) / `Extra.w`(N4 淡入淡出) / `Debug` / `Hole` / `Look0–3` / `Tint` | `RingGeom`、`ColorInner`、`ColorOuter`、`Appearance`、`LensParams`、`Timing.y/z`、`Extra.x/y/z`、`CenterRadius.z/w` |

同步改了 `common.hlsl` 的 cbuffer（**两侧必须一起改**：这是按字节顺序对应的隐式契约，
编译期不会报错）。每行固定一个 float4、未用分量填 0，保持"顺序即布局"的简单性。
删除字段时特意**一次性**做完，避免中间态出现错位。

**顺手修掉 B4（N4 缩放失效）**：`scale` 原先只乘到 v1 的 `Effective*Radius`，
v1 删掉后它就没有任何消费者了。现在改为 `holeFraction *= scale` ——
盘的半径是以 r_s 为单位的相对量，缩放阴影半径就能带动整体。
`SceneRendererTests` 里 M1.8.1 删掉的 `TransitionScale_*` 语义由此恢复（不再单独加回，
因为缩放已经并入 `holeFraction`，由 `DiskSpeed_Animates…` / `DefaultSettings_…` 间接覆盖）。

**时间门换锚点**：`time = RingSpinSpeed > 0 ? t : 0` → `Math.Abs(DiskSpeed) > 0 ? t : 0`。
`RingSpinSpeed` 是 v1 参数（M1.8.4 删），而 v2 的条纹速度是 `DiskSpeed`，语义本就该是它。

**删** `GlowMath.cs`、`DebrisMath.cs`（各含配套测试 `GlowMathTests.cs`，`DebrisTests` 上一步已删）、
`LensMath` 的 v1 段（`Deflection*` / `PhotonRing*` / `Fade*` / `LensWeight` / `SmoothStep` 与四个常量）、
`OverlayGeometry.ComputeWindowRadius`（v1 窗口半径，生产早已改用 `ComputeV2WindowRadius`）。
`OverlayController` 首帧日志里的光晕取证改为输出阴影半径。

**测试改写**（`RenderingMathTests` 39 → 14，`BlackHoleLookTests` 1 项改名）
- 删 `LensMathTests` 整类（10 项，测 v1 位移近似/光子环/渐隐）—— v2 的换算由
  `BlackHoleLookTests` 的「几何换算」一组覆盖
- 删 v1 的 `CenterAndRadii` / `Tilt` ×2 / `Colors` / `Appearance` / `LensParams` / `SpinPhase` ×2 /
  `RingRoll` ×3 / 光晕可达半径 ×2 / `LensRadius_DoesNotChange…`
- 改写：`Timing_CarriesTimeAndLensFlag`（只留时间与开关）、新增 `ZeroDiskSpeed_FreezesTime`、
  新增 `DiskRoll_OnlyChangesTheRollConstant`（v2 版"改一个参数不得带出一串"的守门用例）
- 修硬编码下标：`[32]`(Extra.x) / `[24..27]`(LensParams) 随布局变化后已失效，改为 `Look0.z = [26]`

**踩坑记录**：写 `DiskRoll_OnlyChangesTheRollConstant` 时又踩到 M1.8.1 记下的那个陷阱 ——
`BuildConstants` 没传 `Look`，于是 `DiskRoll` 根本没进常量数组，测试报"两个值相等"。
同一天踩两次，说明这个默认值（"不传 Look 就用默认 Gargantua"）确实反直觉。

**结果**：`dotnet build` 0 警告 0 错误；`dotnet test` **415 通过 / 0 失败**（445 − 30）。
cbuffer 与 C# 的一致性由现有渲染用例间接验证（中心位置、淡入淡出、时间门、
探针通道、Hole/Look0–3 全部仍被断言覆盖）。

#### M1.8.2 ✅ 已完成：删 v1 着色器代码

**删**
- [x] `Shaders/blackhole.hlsl` **整文件**（手绘椭圆盘 + 高斯光子环 + N2 碎石带）——
      连带 `BlackHoleCursor.Core.csproj` 的 `EmbeddedResource` 与 `ShaderSources.Parts` 两处登记，
      **漏掉任何一处都会在运行时抛异常**（`ShaderSources.Concatenate()` 找不到资源 /
      着色器里出现未定义函数），而编译期不会报错
- [x] `lens.hlsl` 的 `SampleLensed`（v1 位移近似 + 光子环）
- [x] `composite.hlsl` 的 `LegacyCompositePS`
- [x] `common.hlsl` 里只服务 v1 的部分：`BlendOver`、`ValueNoise2`、`Gaussian`、`SmoothStep01`、
      `PhotonRingFactor/WidthFactor`、`HaloFalloff/ReachFactor`、`PhotonGain/LensPhotonGain/LensPhotonTint`、
      `MaxDebrisCount/DebrisTracks/DebrisSizeFactor/DebrisTailFactor`
      保留：`Hash21`（v2 的盘条纹与星场都用）、`mirrorUV`、`rot`、`gmod`、`vnoiseWrapY`、
      `blackbody`、`stars`、`DiskGeom`、`LENS_DEPTH`、`B_CRIT`、两个 M1.1 验证入口
- [x] `tests/DebrisTests.cs` 整文件（9 项）—— N2 碎石带是 v1 特性，v2 没有对应概念，
      其中 `ShaderDeclaresTheSameMaxCount` 还正则匹配已被删除的 `MaxDebrisCount`

**未动**：cbuffer 字段（`RingGeom` / `ColorInner` / `ColorOuter` / `Appearance` / `LensParams` /
`Timing.y/z` / `Extra.x/y` / `CenterRadius.z,w`）—— 它们与 C# 侧 `SceneConstants` 是**字节布局**
级别的隐式契约，留到 M1.8.3 一次性两侧同步，避免"编译过但画面参数全乱"这种最难查的错。

**结果**：`dotnet build` 0 警告 0 错误；`dotnet test` **445 通过 / 0 失败**（454 − 删除 9）。

#### M1.8.1 ✅ 已完成：`SceneRendererTests` 切到 v2

**改** `tests/SceneRendererTests.cs`（整文件重写，30 → 10 项）
- [x] `RenderFrame` 不再显式走 `LegacyCompositePS`，改走产品主流程 `CompositePS`
- [x] 按语义重新归类，而不是机械地"改写"：
  - **改写（v2 有对应语义）**：时间动画、冻结时间、透镜开关、盘旋转（DiskRoll）、
    N4 淡入淡出（fade=0 / rgb 与 alpha 同变）、渲染冒烟、PreviewRenderer 缓冲与缩放
  - **不重复（已被 v2 专用测试文件覆盖）**：真阴影、盘发光、遮挡、远场淡出、
    色温、染色 —— 见 `GeodesicCompositeTests` / `GeodesicDiskTests` / `DiskShadingTests` /
    `FarFieldLensingTests` / `KerrTests` / `StarFieldTests`
  - **删除（只属于 v1，v2 里该特性不存在）**：碎石带 N2（5 项）、光子环调参（2 项）、
    透镜强度/半径、`GlobalScale`、v1 椭圆/倾角/配色
- [x] N4 的**缩放**两条用例删除，原因见下方新缺陷 B4

**踩坑记录（值得记住）**：只传 `SettingsModel` 时渲染器用的是 `BlackHoleLook.Default` ——
`SceneParamsBuilder.Build` 里是 `d.Look ?? BlackHoleLook.Default`，
**不会**自动调用 `FromSettings`。所以只改 SettingsModel 的 v2 参数，画面纹丝不动。
生产路径（OverlayController / PreviewSurface）都显式传了 Look 所以没问题，
但写测试时必须显式传 `LookPresets.FromSettings(settings)`，否则断言的是一份"假的默认外观"。

**新发现的缺陷（登记为 B4，M3 修）**：N4 出现/消失动画的**缩放**在 v2 下是失效的 ——
`SceneParamsBuilder` 只把 `scale` 乘到 v1 的 `EffectiveHorizonRadius / RingInner / RingOuter`，
而 v2 的 `Hole.x`（阴影半径）没乘。表现为：缩小动画只剩淡入淡出，没有"从中心涨出来"。
因此 `TransitionScale_*` 两条用例删除，M3 修好后要**重新加回**。

**结果**：`dotnet build` 0 警告 0 错误；`dotnet test` **454 通过 / 0 失败**（474 − 删除 20）。

---

## 4. M2 — 宿主适配

### M2.1 SettingsModel 换参数 ✅ 已完成

- [x] `SettingsModel` 加入 prd-v2 §6.1 的 v2 参数（默认值 = Gargantua；当时 18 个 + `QualitySteps`，
      后陆续补入 `NebulaGain` / `NebulaColor` / `LensEnabled`，现 22 个）：
      `HoleSize / DiskTemp / TintColor / TintStrength / DiskIncl / DiskRoll / DiskInner /
      DiskOuter / DiskOpacity / DopplerMix / BeamExponent / DiskGain / DiskContrast /
      DiskWinding / DiskSpeed / Exposure / StarGain / Spin` + `QualitySteps`（枚举）
- [x] `SettingsRanges` 同步全部上下限（与 `BlackHoleLook.Clamp` 一致）
- [x] `Clamp()` 覆盖新参数；额外保证 **盘外径 > 内径**、枚举越界回落 `Auto`
- [x] `CopyFrom` 携带新参数（预设机制依赖它，M5.2 会再改成增量补丁）
- [x] 新增 `LookPresets.FromSettings(SettingsModel)`：唯一的「设置 → 渲染模型」映射点，
      放在渲染侧而不是 UI 侧，避免 UI 模型与渲染核心互相纠缠
- [x] `OverlayController` 每帧重新映射（`UseExplicitLook` 供测试注入）；
      `PreviewSurface` 同样走 `FromSettings`，保证「预览 = 实机」
- [x] `EffectiveStepCount`：Auto → 48（自动降档留给 M6.1）
- [x] **v1 视觉参数已删除**（M1.8.4 完成）：原先计划等 M5.1 之后再删，实际留到 M1.8.4
      与 `LensView`、预设一起收干净，见 M1.8.4 一节

**测试**（`tests/SettingsV2Tests.cs`，新增 16 项）：
- [x] 默认值逐项等于 Gargantua 预设；默认设置映射出的 look = Gargantua
- [x] `HoleSize` / `Spin` 越界被钳制；外径被强制大于内径；非法枚举回落 Auto
- [x] 四档 `QualitySteps` → 步数解析正确
- [x] `FromSettings` 传递步数与染色、并做钳制
- [x] `CopyFrom` 携带全部 v2 值
- [x] `SettingsService` 落盘/读回全程往返（含 `Color` 的自定义序列化）
- [x] 旧版 JSON（无 v2 字段）加载后回落默认，不崩

**结果**：`dotnet test` **444 通过 / 0 失败**；Release 构建 0 警告 0 错误。

> ⚠️ **UI 尚未接上**：外观页仍绑定 v1 参数，所以**滑块现在还控制不了 v2**。
> 下一步 M5.1 会把外观页改绑到这些新参数，届时滑块才真正生效。

### M2.2 窗口尺寸按新半径计算 ✅ 已完成（提前到 M1.5 之前做）
- [x] 新增 `OverlayGeometry.ComputeV2WindowRadius(shadowRadiusPx, diskOuterPx, peakScale)`（纯函数）
      = `max(盘外径×1.15, 阴影半径×6) × 过冲峰值 + 8`，钳制在 `[8, MaxExtent/2]`
- [x] 新增 `OverlayGeometry.DiskOuterRadiusPixels(outerRs, shadowRadiusPx)`：
      以 `B_CRIT = 3√3/2 ≈ 2.598 r_s` 对应阴影半径为基准换算（2.598 r_s → 恰好等于阴影半径）
- [x] `OverlayController.ComputeV2Radius()` 调用它们，并保留 N4 的 `TransitionState.PeakScale` 预留过冲
- [x] v1 的 `ComputeWindowRadius(SettingsModel, ...)` 保留给旧路径，M1.8 一并清理

**测试**（`tests/OverlayGeometryV2Tests.cs`，新增 11 项）：
- [x] 盘外径换算：临界撞击参数处 = 阴影半径；线性缩放
- [x] 半径随阴影半径单调增大
- [x] 半径必然容纳整个盘（含余量）—— 防 R5「盘外缘被裁」
- [x] 半径覆盖远场过渡
- [x] 过冲峰值越大窗口越大
- [x] 0.02 / 0.06 / 0.15 三档 hole size 都在合法区间内
- [x] 退化输入（0 / 负数 / peakScale 0）不崩且不越界
- [x] 窗口矩形仍以黑洞为中心、边长 = 2×半径

### M2.3 OverlayController 传参与屏高换算 ✅ 已完成
- [x] 新增 `Rendering/ScreenMetrics.cs`：`MonitorFromPoint` + `GetMonitorInfo`
      取**光标所在显示器**的高度（多显示器下各屏高度不同，prd 要求按该屏高度算半径）
- [x] `SceneParamsBuilder`：`ScreenHeight > 0` 时把 `HoleSize` 从「占屏高比例」
      换算成「占**视口**高度比例」（`HoleSize × 屏高 / 视口高`），`Hole.Y` 随之重算；
      未传屏高时保持旧行为，不破坏既有调用方
- [x] `OverlayController` 把 `Look` 与 `ScreenHeight` 传入 `SceneDescription`；
      **质量档位暂用 `BlackHoleLook.StepCount` 默认值 48**（档位 UI 属 M2.1 / M1.7）
- [x] `CompositePS` 增加**窗口边缘羽化**：覆盖层有 1024 上限，远场淡出未必能在窗口内走完，
      最外圈再收一次尾，保证永远不会出现被矩形裁出的硬边
- [x] 保留 `FrameTiming`、降级链、设备重建

**测试**（`tests/ScreenHeightMappingTests.cs`，新增 5 项）：
- [x] 未传屏高 → `Hole.X` 就是 `HoleSize`（旧行为）
- [x] 同一视口下屏高越大 → `Hole.X` 越大（这才是「按屏高算」）
- [x] 换算值精确（0.06 × 1080 / 400 = 0.162）
- [x] `Hole.Y`（W）与换算后的半径一致
- [x] 极端值被钳制在合理范围

**同时改写**（M1.2 遗留的旧断言，其语义已被本步有意改变）：
- `ScreenHeight_DoesNotChangeShadowRadiusInScreenUnits` → `ScreenHeight_ConvertsHoleSizeToViewportFraction`
- `OverlayControllerTests` 中 3 项 v1 窗口断言 → 改为
  「窗口容纳盘与远场」「透镜开关不影响窗口尺寸」「HoleSize 变化才改变窗口尺寸」

**结果**：`dotnet test` **428 通过 / 0 失败**；Release 构建 0 警告 0 错误。

### 实机验证（M2.3 之后）

发布产物跑起来后，桌面截图确认：**真阴影 + 绕射盘面 + 横穿阴影的近侧盘面**，
即 Gargantua 造型，v2 渲染核心已在实机生效。日志实测：
`窗口 1024x1024`、`近 5s 平均 55.7 FPS`、`抓屏 0.09 / 渲染 0.11 / Present 0.13 / 整帧 0.56 ms`。

> ⚠️ **发布踩坑（M1.8.5 已复核修正）**：`dotnet publish -o dist` 是**增量**的，
> 不确定就先 `Remove-Item dist -Recurse` 再发布。
> 原记录写「dist 必须 8 个文件，缺 5 个 WPF 原生 DLL（`wpfgfx_cor3.dll` 等）会
> `DllNotFoundException` 秒崩」——**该结论实测已不适用**：当前是框架依赖发布，
> 干净发布产出 **21 个文件 + `runtimes/`**，WPF 原生 DLL 由共享框架提供、不进 dist，
> exe 正常启动。**可靠的核对方式不是数文件，而是实跑**（见 M1.8.5 的 `--no-takeover` 冒烟法）。

### M2.4 预览同步 ✅ 已完成

- [x] `PreviewSurface` 用新参数与同一渲染器 —— M2.1 / M5.1 时已让它走
      `LookPresets.FromSettings(_settings)`，与覆盖层同一 `SceneRenderer` + 同一 `SceneParamsBuilder`
- [x] **修掉预览与实机的屏高口径不一致**（本次实际发现的问题）：
      预览用的是 WPF 的 `SystemParameters.PrimaryScreenHeight`（96dpi **逻辑**单位），
      而覆盖层用 `ScreenMetrics.MonitorHeightAt`（Win32 **物理**像素）。
      在 125% / 150% 缩放下两者不等 → 预览显示的黑洞比桌面上实际偏小，直接违反 F7-2。
      新增 `ScreenMetrics.PrimaryMonitorHeight()`（`MonitorFromWindow(NULL, MONITOR_DEFAULTTOPRIMARY)`），
      预览改用它，失败才回落 `SystemParameters`。

**「预览 = 实机」的正确断言方式（容易写错，值得记住）**：
两边视口尺寸不同（预览 184²、覆盖层窗口可达 872²），而 `HoleSize` 是「占**屏高**的比例」，
所以 `Hole.X`（uv 比例）**必然不同** —— 拿它直接比会得到「预览和实机不一致」的假失败。
真正必须一致的是**阴影半径的像素值** `Hole.X × 视口高`：
`0.06 × 1080 / 184 × 184 = 0.06 × 1080 / 872 × 872 = 64.8 px`。

**测试**（新增 `tests/PreviewSyncTests.cs`，6 项）：
- [x] 预览与覆盖层的 `Look0–3` / `Tint` / `Hole.Z`（步数）/ `Timing.W`（透镜开关）逐项相同
- [x] 两边阴影半径像素值一致（实测 64.8 px）
- [x] 防空转：屏高 720 vs 1440 → 像素半径正好 2 倍
- [x] 端到端：改 `DiskTemp` 3000 → 16000 预览画面确实变化
      （这条同时守住 M1.8.1 的坑：不传 `Look` 时渲染器用默认 Gargantua，断言会「假通过」）
- [x] 预览缓冲长度 = 184×184×4
- [x] `PrimaryMonitorHeight()` > 0（新 P/Invoke 签名写错会静默返回 0，让预览回落错误口径）

**结果**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **416 通过 / 0 失败**（410 + 新增 6）；
重新发布后实机冒烟正常（30.1 / 27.3 FPS，扭曲=开，全黑=False）。

---

## 5. M3 — 缺陷修复（核心稳定后再修）

### M3.1 物理/节流解耦（锁死 B1）✅ 已完成

- [x] 复核 `_pendingSeconds` 累计逻辑 —— **结论：代码本身已是对的**（M0 就修过），
      缺的只是测试。每个 Tick 都 `_pendingSeconds += delta`，只有真正渲染的帧才消费，
      并 `Math.Clamp(step, 0, MaxTimeStep)` 丢弃超出单帧上限的部分（防卡顿后瞬移）。
- [x] 新增时钟注入点：`OverlayController` 与 `FrameScheduler` 都可传入 `Func<double> clock`
      （默认仍是 `Stopwatch`）。**两者共用同一个时钟** —— 否则「过了多久」的理解会不一致。
      `FrameScheduler` 的 `MillisecondsUntilNextFrame` 与 `ShouldRender` 一并改走注入时钟；
      `Start()` 里 `_lastFrameSeconds` 改为 `_clock()` 而不是 0。
- [x] 新增 `OverlayController.ElapsedSeconds`（物理已推进的累计时间，只在渲染帧累加）——
      它正是「黑洞实际走了多久」，是 B1 唯一可直接观测的量。

**测试**（新增 `tests/FrameRateIndependenceTests.cs`，10 项）
- [x] `PhysicsTime_MatchesElapsedTime_RegardlessOfFrameRate`（Theory ×6）：
      目标帧率 60/165/240 × Tick 频率「= 目标」与「1000Hz 空转（大量帧被节流）」，
      物理推进量都等于真实经过时间
- [x] `WhenTheLoopTicksFasterThanItRenders_*`（Theory ×3）：
      1000Hz 空转 vs 不节流，终点位置一致；并先断言「确实有大量帧被跳过」防空转
- [x] `AHugeTimeStall_DoesNotTeleportTheBlackHole`：
      模拟卡 2 秒，单帧位移不得超过 `MaxTimeStep × MaxSpeed`

**踩坑记录 1（值得记住）**：第一版把断言写成「不同帧率下**终点坐标**一致」，实测失败
（60Hz vs 240Hz 差 14px）。原因是**鼠标每 Tick 采样**：60Hz 下目标每 16.7ms 跳一次、
且跳到已提前的位置，240Hz 下每 4.2ms 跳一次 —— 弹簧追的「阶梯」粒度不同，滞后量必然不同。
这是采样的固有性质，**不是 bug，改不动也不该改**。
中途我误以为是积分步长问题，加了固定子步长（1/240）后数值**一字未变**，随即撤回。
（判据：165 vs 240 只差 0.42px，若真与步长有关，两者应差得更多。）

**踩坑记录 2**：`OverlayController` 每个实例都持有一整套 D3D 设备 + 交换链 + 抓屏资源。
第一版把它们攒到类级 `Dispose()` 才释放，12 个实例直接把测试进程撑到
`OutOfMemoryException`（连测试框架自己都起不来）。改为**每次 `Simulate` 用完立刻 `Stop()`**。
另外 `OverlayController` **不是** `IDisposable`，用 `Stop()` 而非 `using`。

**结果**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **426 通过 / 0 失败**（416 + 10）。

### M3.2 165 帧率达标（锁死 B2）⏭ 已跳过

> **你定的：机器跑不了那么高的帧率，这一步不做。**
> 相关的代码（`TimeBeginPeriod(1)` + `WaitAndPump(1)`、按 `MillisecondsUntilNextFrame` 精确等待）
> 保持 M0 时的状态不动；`TargetFrameRate` 的 165 档位在 UI 上仍然可选（F6-1 已满足）。
>
> ⚠️ 顺带记录一个实测观察（M1.8.5 冒烟）：本次机器上
> **DXGI Desktop Duplication 不可用、走的是 GDI BitBlt 兜底**，抓屏占整帧 97%
> （21.20ms / 整帧 21.94ms）。也就是说即便换了等待方式，
> 在这台机器上瓶颈也是**抓屏而非帧调度** —— 真要追高帧率，先解决的是抓屏路径。

### M3.3 自排除捕获（锁死 B3）✅ 已完成

- [x] `Win32OverlayWindow` 构造时调用 `SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE)`。
      **注意与 `WDA_MONITOR(0x1)` 区分**：后者只是把内容涂黑、仍然会被抓进去，用错等于没修。
      老版本 Windows（< 2004 / build 19041）不支持，**失败不抛异常**，降级为不加 ——
      宁可回廊复发，也不能让覆盖层起不来。
- [x] 新增 `ExcludeFromCapture`（调用是否成功）与 `DisplayAffinity`（回读值）两个属性。

**测试**（新增 `tests/OverlayCaptureExclusionTests.cs`，4 项）
- [x] `OverlayWindow_IsExcludedFromCapture`：亲和性设上且回读 == `WDA_EXCLUDEFROMCAPTURE`
      （本机实测通过）
- [x] `DisplayAffinity_MatchesWhetherTheCallSucceeded`：回读值与「调用是否成功」一致 —— 防空转
- [x] `DisplayAffinity_SurvivesMoveShowHideAndTopmost`：每帧 Move + 周期性 EnsureTopmost 之后不丢
- [x] `DisplayAffinity_AfterDispose_ReturnsZero`：销毁后回读安全返回 0

**为什么不写「抓屏里没有黑洞」的像素级断言**：那需要真实桌面静止不动，
而壁纸动效 / 任务栏时钟 / 其它窗口刷新都会让两次抓屏不同，断言会**随机失败**。
这一条留给实机验收 —— 也是 F8（截图补偿）存在的前提。

**结果**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **430 通过 / 0 失败**（426 + 4）。

---

## 6. M4 — 截图补偿

### M4.1 剪贴板监听与全屏判定 ✅ 已完成

- [x] `ScreenshotPolicy`：只处理**尺寸与虚拟桌面一致**的全屏位图，
      容差 ±2px（PrtScn 走 DIB，第三方工具走 PNG 解码，取整会差一两个像素）。
      宽高互换、尺寸为 0（查询失败）一律不处理 —— 多显示器下放过去会补错地方。
- [x] `ScreenMetrics.VirtualScreenSize()`（`GetSystemMetrics(SM_CX/CYVIRTUALSCREEN)`）
- [x] `IClipboardImages` 抽象：Core 不引用 WPF，单测用内存替身，
      **绝不碰系统剪贴板**（否则会污染用户正在复制的内容）
- [x] `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`，
      监听宿主是一个 1×1 的隐藏工具窗口（`HwndSource` + `WS_EX_TOOLWINDOW`，
      不进任务栏也不进 Alt+Tab）。消息处理里只 `BeginInvoke`，不直接读写剪贴板（避免重入）。

**测试**（`ScreenshotPolicyTests`，13 项）：全屏命中、±2px 容差、区域/窗口截图忽略、
宽高互换忽略、虚拟桌面尺寸未知时忽略、开关关闭时不补偿。

### M4.2 合成回写剪贴板 ✅ 已完成

- [x] `ScreenshotCompositor`：用**同一套渲染器**（`SceneRenderer` + `PreviewRenderer`）
      把黑洞画回位图。关键做法是把位图上黑洞所在的那块区域当作**背景纹理**喂给渲染器，
      于是透镜折射的是**真实截图内容**而不是合成棋盘，补出来的效果与桌面上一致。
- [x] 补丁尺寸复用覆盖层同一套几何（M2.2 的 `ComputeV2WindowRadius`），
      保证补回去的黑洞大小与桌面上一致；上限 1024，且不超过被合成的图
- [x] 合成用预乘 alpha 叠加（`out = 已乘 alpha 的 rgb + 底图 × (1 − alpha)`）

**测试**（`ScreenshotCompositorTests`，11 项）：位图确实被改动、补丁内出现真阴影（明显更暗的像素）、
补丁之外不被波及（防「整图被提亮」的假通过）、贴边不越界、关掉透镜仍有黑洞本体、
退化输入被拒绝、补丁尺寸与覆盖层几何一致且随洞径增大。

**踩坑记录**：`PreviewRenderer` 的渲染目标是**固定尺寸**，补丁尺寸每次都可能不同
（洞径不同、贴边被裁），必须先 `Resize`。第一版忘了，渲染出 64×64 的缓冲却按 300×300 去读，
直接 `IndexOutOfRangeException`。

### M4.3 开关与设置项 ✅ 已完成

- [x] `SettingsModel.ScreenshotCompensation`（默认 **true**），程序设置页加了开关
- [x] 绑定校验扩展到 `SettingsView.xaml`（原先只扫 `AppearanceView.xaml`，
      新开关会漏检 —— 已改成扫描页面列表，并加了「开关必须出现在设置页」的断言）

**测试**（`ScreenshotCompensatorTests`，7 项）：全屏图被补偿、区域截图不动、
关闭后连全屏图也不动、剪贴板无位图时跳过、
**自己写回引发的那次通知必须被忽略**（否则无限自我补偿 —— 本功能最容易踩的坑）、
用户再截一张能再次补偿、合成结果里出现明显更暗的真阴影。

**「无限自我补偿」的解决办法**：写回剪贴板会推进 `GetClipboardSequenceNumber()`
并再触发一次 `WM_CLIPBOARDUPDATE`。处理成功后记下**写回之后**的序列号，
下次通知若序列号与它相同就跳过 —— 必须在写回之后再取，写操作本身会把它推到新值。

**结果**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **462 通过 / 0 失败**；
重新发布后实机冒烟正常（无「注册剪贴板监听失败」日志）。

---

## 7. M5 — UI / 预设 / 配置迁移

### M5.1 外观页重做 ✅ 已完成

`AppearanceView.xaml` 全部改绑 v2 参数，原来的 6 张卡片重排为 5 张：

| 卡片 | 控件 |
|---|---|
| 预设 | 不变（另存 / 删除逻辑沿用，M5.2 再换成补丁式应用） |
| 大小 | `HoleSize`（按百分比显示） |
| 颜色 | `DiskTemp`（F0）、`TintColor` 取色按钮、`TintStrength` |
| 盘形 | `DiskInner` / `DiskOuter` / `DiskIncl` / `DiskRoll` / `DiskOpacity` |
| 相对论外观 | `DopplerMix` / `BeamExponent` / `DiskGain` / `DiskContrast` / `DiskWinding` / `DiskSpeed` / `Spin` |
| 渲染 | `Exposure` / `StarGain` / `QualitySteps` 下拉 / `LensEnabled` 开关 |

- [x] `SliderRow` 新增 `ValueFormat`：`HoleSize` 的 0.02–0.15 用 `F1` 会全显示成
      「0.1」，改用 `P1` 后是「6.0%」
- [x] `QualitySteps` 用 `SelectedValue` + `SelectedValuePath="Tag"` 真正双向绑定，
      不再依赖代码后置手工同步
- [x] 删除 v1 的「碎石带」卡片与内圈/外圈双取色按钮（换成单一 `TintColor`）
- [ ] 每个滑块旁的**数值单位提示**还很简略，M5.2 收尾时再统一

**测试**（`tests/AppearanceBindingTests.cs` + `OverlayControllerTests`，新增 7 项）：
- [x] XAML 里每个 `{Binding X}` 都在 `SettingsModel` 上存在且**可写**
      （WPF 绑定写错不会编译报错，只会静默失效——必须挡住）
- [x] 每个 `{x:Static core:SettingsRanges.X}` 都存在且是 `double`
- [x] 18 个可调 v2 参数**逐个**都有控件（防「加了参数忘了给 UI」）
- [x] 改 `HoleSize` / `DiskOuter` → 覆盖层窗口在下一帧真的变大（端到端连通性）
- [x] 窗口边长永不超过 1024 性能上限
- [x] 派生量（`Effective*` / `VisualRadius`）带 `[JsonIgnore]`，不写进配置文件

**结果**：**451 通过 / 0 失败**；Release 构建 0 警告 0 错误；实机截图确认页面渲染正常、
`阴影大小` 显示为 `6.0%`。

> ⚠️ 实测发现：本机显示器高 1440，默认洞径 0.06 算出的窗口半径需要 **526px**，
> 已经顶到 512px 的 PRD 上限（窗口 1024×1024）。
> 也就是说**在 1440p 及以上屏幕上，默认状态下窗口就是一直接用满的**；
> 把 `HoleSize` 再调大时，远场光晕会被窗口裁掉一部分（R5 的「方形亮边」风险）。
> 这是 M2.2 的 `FarFieldRadiusFactor = 6.0` 与 PRD 的 1024 上限之间的固有矛盾，
> **留到 M6.1（性能与画质）一起决定**：是降远场系数，还是按窗口预算反推洞径上限。

### M5.2 8 预设 + 交叉淡入 ✅ 已完成

> 接手时代码库**编译不过**：上一轮已加好 `LookFade` 与 `IOverlayController.BeginLookFade()`，
> 但 `OverlayRunner` 没实现新接口成员（CS0535），且 UI 侧还没接线。

**8 个预设（数值表）**：M1.8.4 已落地 —— `PresetLibrary` 直接复用渲染侧 `LookPresets`
（prd-v2 §6.3），不再维护第二份数值表，已由 `PresetServiceTests` 的
`BuiltInPresets_MatchTheLookPresetsTable` / `Gargantua_MatchesPrdTable` 逐项锁住。本步不再动数值。

**1 秒交叉淡入**
- [x] `Rendering/LookFade.cs`：`Begin / Step / Blend / Cancel` + `Progress`，smoothstep 缓动
- [x] `OverlayController`：构造新增 `LookFade? lookFade`，`CurrentLook()` 里 `Blend`，Tick 里 `Step()`；
      `BeginLookFade()` 抓住**当前**外观作为起点
- [x] `OverlayRunner`：`BeginLookFade()`（补上编译缺口），并把同一个实例传进 `OverlayController`
- [x] `App.PresetFade` 作为**唯一实例**，覆盖层与 `PreviewSurface` 共用 —— 否则桌面在淡入、预览却瞬间跳变
- [x] `MainWindow`：「恢复默认」先 `PresetFade.Cancel()`（不该穿过一次 1 秒淡入）

**补丁式应用（替换原来的整体 `CopyFrom`）**
- [x] `SettingsModel.ApplyAppearanceFrom(other)`：只写预设会给出的 15 个观感参数
- [x] `SettingsPreset.ApplyTo(target)`：外观页点预设走这条路径
- [x] 不覆盖 `HoleSize`（大小）、`QualitySteps`（性能档位）、`TintColor` / `TintStrength`
      （预设表里没有染色）、以及一切系统行为 —— M1.8.4 记的那条「等 M5.2 才真正成立」至此生效

**踩坑记录 1（值得记住）：淡入不能用「累计 delta」推进。**
覆盖层渲染循环（~60Hz）与主界面预览（~30Hz）共用同一个实例，两边每帧各调一次 `Step`，
进度会被推到约 2 倍速。改成**按绝对时钟求进度**（`Progress = (now − start)/duration`，
时钟可注入）后，`Step()` 无论被调用几次都只是「到点了就结束」，快慢只由时钟决定。

**踩坑记录 2**：`Begin()` 必须在**改设置之前**调用。顺序反了起点就是新外观，淡入等于没做 ——
外观页里两行紧挨着，很容易被后面重构调换，已写进 `App.BeginPresetFade()` 的注释。

**测试**（新增 21 项：**483 通过 / 0 失败**）
- [x] `tests/LookFadeTests.cs`（18 项，虚拟时钟驱动，不真实等待）：
      未 Begin 时原样返回目标；t=0 等于旧外观；t=0.5 正好是中点；1 秒后落到目标并结束；
      默认时长 = 1 秒；**多个调用方各调一次 Step 不加速**（防上面踩坑 1 复发）；
      目标每帧实时取（淡入途中改目标会跟着收敛）；`Begin` 快照起点（之后改起点实例不影响）；
      `StepCount` 不插值；染色按通道插值；`Cancel` 立即落到目标；时长 0 直接到位；
      smoothstep 曲线值与单调性
- [x] `PresetServiceTests` 新增 3 项：`ApplyPatch_KeepsSizeQualityAndTint`、
      `ApplyPatch_WritesEveryAppearanceField`（15 个字段逐个断言）、`ApplyPatch_DoesNotTouchSystemBehaviour`

**未覆盖（留给实机验收 / M6.3）**：点预设按钮后的**观感**（是否真的平滑过渡）需人工点一遍；
`App` 级的实例共享没有单元测试（WPF App 不好起），靠代码结构与注释保证。

### M5.3 旧配置迁移 ✅ 已完成

> prd-v2 R3：v1 的 `HorizonRadius` / `RingSpinSpeed` / `LensRadius` 等字段在 v2 的
> `SettingsModel` 上已经不存在，而 `System.Text.Json` 会**静默忽略**未知字段 ——
> 用户的老配置会无声无息变成一堆默认值。所以要做的是「显式认出来」，不是「让它别崩」。

**新增** `Core/SettingsMigration.cs`
- [x] `LegacySettingsFields.Names`：16 个已废弃的 v1 字段名（prd-v2 §F2-1 移除清单 + N2 碎石带 + 渐变开关），
      匹配**大小写不敏感**（手写或被别的工具改写过的文件未必保持 PascalCase）
- [x] `SettingsMigration.DetectLegacyFields(json)`：用 `JsonDocument` 扫根对象的属性名；
      解析失败返回空 —— 那是「文件损坏」那条路径的事，不抢着报错
- [x] `SettingsMigrationNotice`（认出的字段 + 备份路径 + 提示文案）

**改** `SettingsService`
- [x] `Load()`：先 `DetectLegacyFields`，命中就**不走反序列化** → `BackupLegacyFile()` → 回落默认 → 记 `Migration`
- [x] 备份用 `File.Move`（`settings.v1-<时间戳>.json`）而不是复制：留着原文件会导致每次启动都再认一次、再提示一遍
- [x] `Migration` 属性 + `TakeMigrationNotice()`（取出即清空，保证只提示一次）
- [x] 损坏文件仍走原路径（`RecoveredFromCorruptFile`），两条互不干扰

**改** `MainWindow`
- [x] `ShowMigrationNoticeOnce()`：`SavedHint` + 一次托盘气泡

**一个刻意的设计选择**：只认**已知的 v1 字段**，不做「凡是未知字段都算旧配置」。
后者会让「新版本写的配置被旧版本打开」这种普通降级场景直接清空用户配置。
陌生字段仍然静默忽略（`UnrelatedUnknownFields_AreIgnoredQuietly` 锁住）。

**测试**（新增 `tests/SettingsMigrationTests.cs`，13 项：**496 通过 / 0 失败**）
- [x] 16 个 v1 字段全部被认出；当前格式的配置文件一个都不命中；大小写不敏感；非法 JSON 返回空
- [x] 旧文件加载 → 回落默认、且不报「损坏」；备份文件存在且内容仍是旧配置；原文件被挪走
- [x] 提示**只**一次（第二次 `TakeMigrationNotice()` 为 null）
- [x] 陌生字段不触发迁移；迁移后重新落盘 → 文件已是新格式，再加载不再触发（不会「提示个没完」）
- [x] 迁移路径仍按注册表同步 `AutoStart`；损坏文件仍按「损坏」处理

**实机验证**：手写一份 v1 配置放进配置目录后启动 —— 备份 `settings.v1-20260923172554.json` 生成，
程序正常运行（本次 DXGI 抓屏可用，55–57 FPS，窗口 872×872，抓屏降级=False），随后已恢复原配置。
> 注：迁移后新的 `settings.json` 要等用户下一次改动（防抖保存）才写出；
> 在此之前配置目录里只有备份文件，这是预期行为。

---

## 8. M6 — 打磨与发布

### M6.1 性能与自动降档 ✅ 已完成

**165Hz 实测**：⏭ **继续跳过**（沿用 M3.2 的决定：这台机器跑不了高帧率）。
本次实机的实测读数见下方「实测数据」。

**自动档按帧率降步数（prd-v2 F6-4）**
- [x] 新增 `Rendering/AutoQualityController.cs`：阶梯 24 / 48 / 88（与 `QualitySteps` 一致），
      默认中档；`< 45 FPS` 持续 3s 降一档，`>= 58 FPS` 持续 5s 升一档，一次只挪一档
- [x] 三条防抖：① 45–58 是**死区**，两边都不累积；② 换档要持续（升档比降档更苛刻）；
      ③ 至少 3 次采样才动作，挡住启动预热
- [x] `Apply(look, settings)`：**只有 `QualitySteps.Auto` 才覆盖步数**，用户显式选的档位原样保留
- [x] `OverlayController` / `OverlayRunner` / `PreviewSurface` 全部接同一个实例（App 创建），
      与 M5.2 的 `LookFade` 同理 —— 否则预览会用旧步数渲染，与桌面不一致
- [x] 换档时写一条日志；主界面状态行在自动档下显示「自动 N 步」

**软件渲染提示**
- [x] 状态行原本就有「软件渲染」；M6.1 补一次**托盘气泡**（只弹一次，恢复硬件渲染后重新武装）
      —— 提示文案指向「渲染」页的质量档位

**踩坑记录（值得记住）：自动档不能喂逐帧瞬时帧率。**
第一版直接喂 `_framesPerSecond`（EMA 瞬时值），实机日志出现
`自动质量档位 → 88 步（实测 117 FPS）`，而**同一时段 5 秒平均只有 41 FPS、抓屏还降级了** ——
启动头几秒抓屏还没就绪，帧特别快，瞬时值把它读成「性能富余」，在最卡的时候反而升到最高档。
改为喂 `FrameTiming.TryTakeLog` 的 **5 秒窗口平均值**后，实机表现正确（见下）。
对应测试：`AStartupBurstDoesNotLockInTheHighStepCount`。

**实测数据**（默认 Gargantua，1440p，本次 DXGI 不可用 → GDI 抓屏兜底）

| 量 | 实测 |
|---|---|
| 5s 平均帧率 | 33.7 → 44.3 → 46.6 → 44.9 → 43.7 → **39.5** FPS |
| 自动档动作 | 第 3 个窗口（约 20s）→ **24 步**（日志：近 5s 平均 39 FPS） |
| 分段耗时 | 抓屏 24.35 / 渲染 0.02 / Present 0.29 / 整帧 24.98 ms |

> ⚠️ **一个必须记住的结论**：这台机器上**抓屏占整帧约 95%**（24.35 / 24.98 ms），
> 渲染本身只有 0.02 ms —— 也就是说积分步数根本不是瓶颈，**降档能省的时间几乎为 0**。
> 自动降档在这里是「正确但收益有限」的；真要提帧率，得先解决抓屏路径（GDI 兜底）。
> 这条同时是 `fixes-v2.md` 问题 1（背景亮度导致光效消失）的关联线索：抓屏降级时段的画面最不稳。

**测试**（新增 `tests/AutoQualityTests.cs`，14 项：**510 通过 / 0 失败**）
- [x] 默认中档 48；持续低帧降档、持续高帧升档；一次只挪一档；到顶/到底不越界
- [x] 升档比降档耗时更久（4 秒不升、第 5 秒才升）；眨眼式低帧不降档；死区不动作
- [x] 无帧率数据（0 / NaN / 负数）忽略；启动单次采样不算数
- [x] 只有 Auto 被覆盖（High/Low 原样保留）；`Reset` 回到中档；`Changed` 只在真换档时为真
- [x] 阶梯与 `QualitySteps` 枚举一致；**启动预热的高读数不会把档位顶上去**

### M6.2 异常与降级矩阵 ✅ 已完成

> 目标：每一种失败都要有**明确行为**，并且**不崩**（prd-v2 稳定性）。

**矩阵（现在是一张可被单测逐格覆盖的表，而不是散在 catch 块里的逻辑）**
- [x] `RenderFailure` 新增 `FailureAction` 与纯函数 `Decide(kind, 连续失败次数, 已重建次数)`：
      设备丢失 → 重建（已重建 ≥ 3 次则停止）；驱动内部错误 → 立即停止；
      可恢复 → 跳过本帧（连续 120 次才停止）
- [x] `OverlayController.HandlePresentFailure` 改为按 `Decide` 的结果分派，不再自己写条件
- [x] 上限常量统一放到 `RenderFailure`，`OverlayController.Max*Rebuilds` 只是别名
      —— 测试里有一条断言两边必须相等，防止「测试测的是另一套规则」

**着色器编译失败**
- [x] 新增 `ShaderCompilationException`（含 `EntryPoint`）：`SceneRenderer.Compile` 捕获底层
      D3DCompiler 异常后包装抛出。覆盖层线程靠这个**可识别**的类型决定
      「记录原因 + 恢复系统光标 + 主界面显示渲染不可用」，而不是吞掉或崩掉
- [x] 主界面状态行补「预览不可用：…」（预览起不来时也要说清原因）

**抓屏失败**：已有链路维持不变（主方案重建 ≤3 次 → 降级 GDI；两条都不可用 →
只丢透镜层，**黑洞本体照画**），本步把它写进矩阵测试。

**测试**（新增 `tests/DegradationMatrixTests.cs`，19 项：**529 通过 / 0 失败**）
- [x] 设备丢失：已重建 0/1/2 → 重建，3 → 停止；驱动内部错误 → 立即停止
- [x] 可恢复：1 / 2 / 119 次 → 跳过本帧，120 次 → 停止
- [x] 整张矩阵按**真实 HRESULT** 再走一遍（防「分类层」与「处置层」对不上）
- [x] 上限常量与 `OverlayController` 共享
- [x] 抓屏两条方案全失败 → `RenderCount == 1`（黑洞仍在画）+ `IsDegraded` + 不算设备丢失
- [x] 着色器编译失败 → `ShaderCompilationException`（含入口名、保留 InnerException）；
      正常入口仍编译通过（防包装把成功路径也变成异常）

**踩坑记录**：`RecordingOverlayWindow` 不套真实 `Win32OverlayWindow` 时句柄是 0，
DirectComposition 的 `CreateSurfaceFromHwnd(IntPtr.Zero)` 会直接抛 `E_INVALIDARG` ——
写覆盖层相关用例时必须套真窗口。

### M6.3 全量测试 + 发布 + 实机验收 ✅ 已完成

**全量构建 / 测试**
- [x] `dotnet build -c Release`：**0 警告 0 错误**
- [x] `dotnet test`：**529 通过 / 0 失败**

**发布**
- [x] `dotnet publish src/BlackHoleCursor/BlackHoleCursor.csproj -c Release -o dist`（先删 dist）
      → **22 个文件 + `runtimes/`**
      ⚠️ **踩坑（值得记住）**：在仓库根直接 `dotnet publish -o dist` 会连**测试项目**一起发布
      （194 个文件，混进 `xunit` / 测试 DLL）。必须指定 App 的 csproj。
      框架依赖发布，WPF 原生 DLL 由共享框架提供，不进 dist（M1.8.5 的结论仍然成立）。

> **2026-09-24 复核（又踩两个坑，值得记住）**
> 1. 干净发布实测是 **21 个文件 + `runtimes/`**，不是 22 —— 文件数本身就靠不住。
>    `Remove-Item dist -Recurse` 会被 IDE 的安全删除机制拒绝（`trash-failed`），
>    于是实际是**增量发布**、旧文件不会被清掉。**既然删不动，核对就不要数文件，改成实跑**。
> 2. 机器上若**已有实例在运行**，新起的进程会因单实例互斥**立刻退出（code=0）**，
>    日志里只留下「进程启动 → 程序退出」两条，很容易误判成"发布产物启动失败"。
>    冒烟前先 `Get-Process BlackHoleCursor` 确认为空。
> 3. 实机数据（窗口 418×418）：**51.5 – 53.6 FPS**，抓屏 12–18 / 渲染 0.01 / Present 1.6–3.7 /
>    整帧 15.6–19.9 ms。抓屏仍是整帧成本的大头，归因见 `fixes-v2.md` 末尾「性能：已归因」。

**实机验收（prd-v2 §7 共 12 条）**

| # | 验收项 | 状态 | 依据 |
|---|---|---|---|
| 1 | 阴影纯黑且不透明 | ✅ | `GeodesicCompositeTests`（WARP 实渲像素断言） |
| 2 | 爱因斯坦环 | ✅ | `GeodesicReprojectionTests`（环处亮度高于原始背景） |
| 3 | 镜像二次像 | ✅ | 同上（紧贴阴影外侧取到黑洞另一侧的采样点） |
| 4 | 远侧光弧 | ✅ | `GeodesicDiskTests`（多次穿越） |
| 5 | 色温方向正确 | ✅ | `DiskShadingTests`（3000K 偏红 / 16000K 偏蓝） |
| 6 | Kerr：ISCO 随自旋收缩 | ✅ | `KerrTests`（ISCO 逐项吻合 Bardeen 1972） |
| 7 | 帧率无关性 | ✅ | `FrameRateIndependenceTests`（60/165/240 × 节流） |
| 8 | 165Hz 实测 ≥150fps | ⏭ **跳过** | 沿用 M3.2 的决定；本机抓屏是瓶颈（M6.1 实测） |
| 9 | 自排除捕获 | ✅ | `OverlayCaptureExclusionTests`（亲和性设上、Move/显隐/置顶后不丢） |
| 10 | 截图补偿 | ✅ **实机验证** | 见下方 |
| 11 | 不回退（v1 能力） | ✅ | 529 项全绿（配置 / 光标 / 跟随 / 游荡 / 动画 / 预设机制） |
| 12 | 实机桌面造型 | ✅ | 日志：窗口 872×872、阴影半径 **64.8 px**（= 0.06×1080 ✓）、扭曲=开、全黑=False；桌面观感以 `bugs/*.jpg` 与人工核对为准 |

**第 10 条的实机验证方法（可复用）**

用一段 PowerShell 往剪贴板放一张**与物理虚拟桌面同尺寸**的纯灰位图，等 5 秒后回读并采样亮度：

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bmp = New-Object System.Drawing.Bitmap(1920, 1080)      # 必须是**物理**像素
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::Gray); $g.Dispose()
[System.Windows.Forms.Clipboard]::SetImage($bmp)
Start-Sleep -Seconds 5
$out = [System.Windows.Forms.Clipboard]::GetImage()       # 再采样合成点亮度
```

**实测结果**：日志 `截图补偿：已把黑洞合成回剪贴板（1920x1080 @(1379,1072)）`；
回读位图在合成点 100×100 区域的平均亮度 **12.2**，远处同尺寸区域 **128**（灰底原样未被波及）
—— 真阴影确实被补了回去，且没有「整图被提亮」。

**踩坑（值得记住）**：不能用 `SystemInformation.VirtualScreen` / `SystemParameters` 的尺寸造测试图 ——
它们给的是 **96dpi 逻辑**尺寸（本机 1280×720），而 `ScreenMetrics.VirtualScreenSize()` 用
`GetSystemMetrics` 取的是**物理**像素（1920×1080）。用逻辑尺寸造的图会被 F8-2 正确地拒掉
（日志：`位图 1280x720 与虚拟桌面 1920x1080 不一致，按 F8-2 不补偿`），
看起来像是「补偿没生效」，其实是测试图造错了。**这也是多显示器 / 缩放环境下最容易误判的地方。**

**顺带补的诊断**（M6.3 验收需要可观测性）：`ScreenshotCompensator` 现在会
- 补偿成功 → 记一条 Info（含尺寸与合成坐标）
- 首次跳过 → 记一条 Info 说明原因（只记一次，否则每次复制文本/图片都会刷日志）

---

## 8.5 fixes-v2（工程完工后的观感精修）

> prd-v2 §1.2 的「先改核心再修 bug」：这些排在 M6 之后；逐条的方案、取证与实测数据都在
> `fixes-v2.md` 的执行记录里，这里只做总览。

| # | 问题 | 状态 |
|---|---|---|
| 1 | 暗背景下光效大幅减少甚至消失 | ✅ 已修（程序化背景光 `NebulaGain`，默认 0.45 常开；实机实测纯黑桌面环带 14.4 → 35.1） |
| 2 | 默认大小下出现很多圈亮环（应只有一圈） | ✅ 已修 + **实机复核通过**（越界取样**淡出**替代 `mirrorUV` 镜像折回 + 高阶像按累计转角衰减；`RingArtifactTests`） |
| 3 | 预览与实机差距过大 | ✅ 已修（先做缩略图 → 你反馈「洞太大时基本没用」→ 改**混合**：真实比例为主、盘装不下才缩小并在角落标「×0.54」；`OverlayGeometry.PreviewFit` + `PreviewSyncTests` 重写） |
| 4 | 关透镜后一大圈黑 + 炫彩加变化 | ✅ 黑圈已删（`bgAlpha` 只由「有没有内容」决定）+ ✅ 炫彩**时间流动**（星云漂移 + 明暗呼吸，走不受盘速度门控的 `Timing.y`） |
| 5 | 透镜实机效果差（抖动 / 不像黑洞） | ✅ 抖动已修（整屏固定抓屏 `CaptureMap`，`CaptureStabilityTests`）；✅ 观感按你指定的「未折射层完全压到折射层下面」实现（`backgroundCoverage` + 星云接管缺失那圈），**2026-09-24 实机确认达标**。⚠️ 残余性能问题：GDI 抓屏 17ms 是 DWM 同步等待，**「半分辨率抓屏」已实测证伪**，唯一出路是后台线程抓屏，留待下一轮 |
| 6 | 星云加变化 / 预设配色 | ✅ 预设可带**星云亮部配色**（`NebulaColor`，M5.2 的唯一例外；uniform 追加 `NebulaTint`，`FloatCount` 48 → 52） |

**当前测试**：**576 通过 / 0 失败**。

---

## 9. 提交与自检规范

```
chore(M0): 基线盘点            ✅
feat(M1.1): 着色器工具层
feat(M1.2): 新 uniform 骨架
feat(M1.3): 薄盘穿越着色
feat(M1.4): Schwarzschild 测地线主流程
feat(M1.5): 远场解析与无缝
feat(M1.6): Kerr 自旋
feat(M1.7): 星场 / 曝光 / 质量档
refactor(M1.8): 清理旧参数与旧着色器
feat(M2.x): 宿主适配
fix(M3.x): 解耦 / 帧率 / 自排除捕获
feat(M4.x): 截图补偿
feat(M5.x): UI / 预设 / 迁移
chore(M6.x): 打磨与发布
```

每个子步骤结束执行：
```powershell
dotnet build -c Release
dotnet test
# 手工冒烟：启动 → 拖动 → 切换预设 → 开关各功能 → 关闭主界面 → 退出 → 确认系统光标已恢复
```

**最高优先级回归点：任何退出路径都必须恢复系统箭头。**
