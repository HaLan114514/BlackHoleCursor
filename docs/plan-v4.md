# 开发计划 v4：预设管理 / 界面重整 / 程序图标（plan-v4.md）

> 依据 **`prd-v4.md`**（改什么、为什么、按什么顺序）。本文只写**怎么落地**：
> 每个子步改哪些文件、加什么测试、怎么验收。
>
> **规则**（沿用 v3，别省）：每个子步结束都要 —— 改代码 → 加/改测试 → **全量跑绿** → 更新本文 → 停下来汇报。
> **回滚点**：`9819b0d`（v4 开工前的干净点，测试 606 全绿）。
> 任何一步搞砸都可以 `git checkout 9819b0d -- <路径>` 精确退回。
>
> **本轮不动**：渲染、抓屏、性能、架构（`fixes-v3` 问题 4 仍是暂缓状态）。

---

## 0. 决策快照（详见 `prd-v4.md` §3）

| # | 决策 |
|---|---|
| 1 | 预设**独立成页**（不是同页分区、不是下拉框） |
| 2 | 预设页能力：分组列出 + 重命名 + 覆盖保存 + 删除 + 导出 / 导入 |
| 3 | 导出 / 导入**现在就做**（重名给「覆盖 / 另存为」） |
| 4 | 页面**6 页**：外观 / 预设 / 渲染与性能 / 引力透镜 / 跟随与动效 / 系统与程序 |
| 5 | 图标用**真 `.ico` 资源**（16 / 32 / 48 / 256）+ `ApplicationIcon` |
| 6 | 底图 = 现有 `IconFactory` 那套图形**高清 256 导出**（风格不变） |
| 7 | 托盘**两张**（彩色 / 灰暗）表示启用 / 禁用 |
| 8 | 版本号统一 **v4.0 / 4.0.0** |

---

## P1 · 预设页（V4-1 / V4-2）

> 目标：内置与「我的」视觉上明确分开；自定义预设可重命名、覆盖保存、删除。
> **先做 Core 层（可单测），再做 UI** —— 逻辑全在 `PresetService`，UI 只是入口。

### ✅ P1.1 已完成（2026-09-27）：`PresetService` 增加两个能力

**改** `src/BlackHoleCursor.Core/Presets.cs`

```csharp
/// 重命名「我的」里的预设。内置名 / 空名 / 与现有重名（大小写不敏感）一律拒绝。
public bool TryRename(string oldName, string newName)

/// 用当前设置覆盖「我的」里**已存在**的预设。内置名 / 不存在的名字一律拒绝
/// （新建走原来的 TrySave，别让 SaveOver 变成「静默新建」）。
public bool TrySaveOver(string name, SettingsModel settings)
```

要点：
- 两个方法都要 `Persist()`（原子落盘，已有）。
- 返回值用 `bool` + 在 UI 层给具体提示文案（沿用现在 `PresetHint` 的套路）。

**结果**（新增 6 项断言，`PresetServiceTests` 25 → **31 通过**；全量 **612 通过 / 0 失败**）

- `Rename_WorksAndPersists`（改名后重新从磁盘加载仍正确）
- `Rename_RejectsBuiltInNames`（改成内置名、改内置名，都拒绝）
- `Rename_RejectsEmptyDuplicateAndUnknownNames`（空名 / 与别人重名 / 改不存在的，都拒绝且不误伤别人）
- `Rename_AllowsCaseOnlyChange`（见下）
- `SaveOver_UpdatesTheExistingPresetAndPersists`
- `SaveOver_DoesNotCreateANewPreset`（覆盖不能变成「静默新建」）

> ⚠️ **这里把计划里的一条写反了，已按实现改掉**：原文写「`_custom` 是 `OrdinalIgnoreCase`
> ⇒ 改名 `A → a` 属于自冲突，要判掉」。实现时发现**判掉才是 bug** ——
> 字典不区分大小写，`A` 与 `a` 命中的是**同一个条目**，用户只是想改个大小写，
> 拒绝它没有道理。所以判重用**引用比较**放行「命中自己」的情况：
> ```csharp
> if (_custom.TryGetValue(target, out var clash) && !ReferenceEquals(clash, preset)) return false;
> ```
> **验证过这条不是空过**：把条件改成「名字存在就拒绝」，`Rename_AllowsCaseOnlyChange` **立刻红**。

### P1.2 新增「预设」页

**新增** `src/BlackHoleCursor/UI/Views/PresetView.xaml(.cs)`

布局（沿用现有 `Card` / `GhostButton` 样式）：
- Card 1「内置」：`WrapPanel`，8 个按钮，点击 = 应用（不可改名 / 删除）。
- Card 2「我的预设」：每行一个预设 = 名称 + `应用` / `覆盖保存` / `重命名` / `删除`（4 个小按钮）。
- 底部：「另存为」输入框 + 按钮（沿用现在 `PresetNameBox` 的交互）。

**应用预设的顺序不能反**（M5.2 的老坑，搬移时最容易弄反）：
```csharp
App.Current.BeginPresetFade();   // 先抓当前外观做淡入起点
preset.ApplyTo(App.Current.Settings);   // 再增量补丁
```
设置对象取 `App.Current.Settings`；预设列表取 `App.Current.Presets`。
重命名 / 覆盖 / 删除的确认沿用现有的 `MessageBox.Show` 二次确认。

**改** `src/BlackHoleCursor/UI/Views/AppearanceView.xaml`：删掉「预设」整个 Card
（`PresetPanel` / `PresetNameBox` / `PresetHint` 一并移除；`.xaml.cs` 里的 `BuildPresetButtons` /
`SavePreset_Click` / `DeletePreset` 随之删除，逻辑搬到 `PresetView`）。

### P1.3 接入导航

**改** `src/BlackHoleCursor/MainWindow.xaml`：导航加一项，放在「外观」下面
```xml
<ListBoxItem Content="外观" Tag="Appearance" IsSelected="True" />
<ListBoxItem Content="预设" Tag="Presets" />
```
**改** `MainWindow.xaml.cs` 的 `Navigate` switch（:238）加 `"Presets" => new PresetView(),`。

### P1.4 测试与验收

**新增 / 扩充** `tests/BlackHoleCursor.Tests/PresetServiceTests.cs`（Core 层，不依赖 WPF）
- `Rename_Works` / `Rename_RejectsBuiltIn` / `Rename_RejectsDuplicate`
- `Rename_RejectsCaseOnlyChange`（`A → a` 自冲突）
- `Rename_RejectsEmptyName`
- `SaveOver_UpdatesTheExistingPreset` / `SaveOver_RejectsBuiltIn` / `SaveOver_RejectsUnknownName`
- 现有 15 项断言保持绿（内置名不可占用 / 删除 / 损坏容错等）

**UI 文本断言**（沿用 `AppearanceBindingTests` 的 XAML 文本校验风格）
- 导航里存在 `Tag="Presets"`；`Navigate` 里存在 `new PresetView()`
- 外观页**不再**包含 `PresetPanel`（防止搬漏）

**验收**：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` 全绿；
实机点一遍：应用内置 / 另存 / 覆盖 / 重命名 / 删除都正常，应用预设时**仍有 1 秒淡入**。

---

## P2 · 预设导出 / 导入（V4-3）

### ✅ P2.1 已完成（2026-09-27）：Core API + 7 项断言

**改** `src/BlackHoleCursor.Core/Presets.cs`

```csharp
public const int PresetFileVersion = 1;

public bool TryExport(IEnumerable<string> names, string path)
public bool TryParsePresetFile(string path, out IReadOnlyList<SettingsPreset> presets, out string error)
public ImportResult ApplyImport(IReadOnlyList<SettingsPreset> presets, ConflictPolicy policy)
```

新增类型：`PresetExportFile`（带 `Version`）、`ConflictPolicy`（Overwrite / KeepBoth）、
`ImportResult`（Added / Updated / Skipped / Renamed / Total）。
顺手把 `Persist()` 与新加的 `WriteAtomically()` 统一（导出文件也是原子写）。

**语义**：只导「我的」；内置名在导入时**跳过不报错**；入库前 `Clamp()`；
`KeepBoth` 生成「名字 (2)」且**原预设不动**；`Total` 不重复累加 `Renamed`（它已计入 `Added`）。

> 🔥 **踩坑：大小写敏感让「版本防线」形同虚设**
> 第一版 `ParsePresetFile` 对 `{"version": 99, ...}` 居然**放行**了 ——
> `JsonSerializer` 默认 `PropertyNameCaseInsensitive = false`，小写的 `version` 根本匹配不上
> `PresetExportFile.Version`，于是它退回默认值 **1**，等于自己骗过了自己。
> 已给 `SerializerOptions` 加上 `PropertyNameCaseInsensitive = true`
> （导入的是别人给的文件，本就该宽容；导出仍是 PascalCase，与现有 `presets.json` 一致）。
> 这条**验证过不是空过**：去掉该设置，`ParsePresetFile_RejectsWrongVersion` 立刻红。

**测试**（`PresetServiceTests` 25 → **38 通过**，新增 7 项）
`ExportImport_RoundTripKeepsNameAndValues` / `Import_Overwrite_ReplacesTheExisting` /
`Import_KeepBoth_RenamesAndKeepsTheOriginal` / `Import_SkipsBuiltInNames` /
`ParsePresetFile_RejectsCorruptFile_AndLeavesStoreUntouched` /
`ParsePresetFile_RejectsWrongVersion` / `Export_RejectsUnknownNames`

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **624 通过 / 0 失败**（617 + 7）。

### P2.1 文件格式与 Core API（原始设计，已完成同上）

**格式**（带版本号，防以后字段变了读不回来 —— 风险 R1）：
```json
{
  "version": 1,
  "presets": [ { "name": "我的预设", "appearance": { ... } } ]
}
```

**改** `src/BlackHoleCursor.Core/Presets.cs`
```csharp
/// 导出「我的」里的若干个（或全部）预设到文件。
public bool TryExport(IEnumerable<string> names, string path)

/// 只解析、不落库：给 UI 先看有没有重名，再决定策略。
public bool TryParsePresetFile(string path, out IReadOnlyList<SettingsPreset> presets, out string error)

/// 按策略入库。ConflictPolicy = Overwrite | KeepBoth（另存为 "名字 (2)"）。
public ImportResult ApplyImport(IReadOnlyList<SettingsPreset> presets, ConflictPolicy policy)
```
规则：
- **内置名**出现在导入文件里 → 跳过（与 `Load()` 的现有规则一致）。
- 未知字段 → 忽略；`version` 不匹配 → 返回 error，不改现有数据。
- 入库前 `Clamp()`（与 `Load()` 一致）。
- `ImportResult { Added, Updated, Skipped, Renamed }` 供 UI 报告结果。

### P2.2 UI

`PresetView` 底部加两个按钮：
- 「导出」→ `SaveFileDialog`（默认文件名 = 预设名，`.json`）
- 「导入」→ `OpenFileDialog` → 解析 → **有重名才弹选择**（覆盖 / 另存为），无重名直接入库 → 用 `PresetHint` 报告结果

### P2.3 测试与验收

- **往返一致**：导出 → 导入回来，值与原名一致
- **重名**：`Overwrite` 覆盖、`KeepBoth` 生成新名字且不丢原预设
- **损坏文件**：JSON 坏了 → 返回 error，**现有 `presets.json` 一个字节都不变**
- **内置名**：导入文件里带内置名 → 跳过且不报错
- **缺字段 / 多字段**：少了走默认，多了忽略
- **版本号不符**：返回 error 且不动现有数据

---

### ✅ P2.2 / P2.3 已完成（2026-09-27）：UI + 容错测试

**UI**（`PresetView.xaml(.cs)` 底部新增两个按钮）

| 按钮 | 行为 |
|---|---|
| 导出 | `SaveFileDialog`（默认名 = 单个预设名，多个则「我的预设」）→ `TryExport` 全部「我的」预设 |
| 导入 | `OpenFileDialog` → `TryParsePresetFile` → **只在有重名时**才问策略（是=覆盖 / 否=另存为 / 取消）→ `ApplyImport` → 用 `PresetHint` 报告「新增 X，覆盖 Y，跳过 Z」 |

「只在有重名时才弹窗」是有意的：没重名还弹一次，用户会习惯性点掉，弹窗就失去意义了。

**补测**（`PresetServiceTests` 38 → **39 通过**）
`Import_ToleratesMissingAndUnknownFields` —— 缺字段走 `SettingsModel` 默认值（不是 0 / NaN），
多出的未知字段忽略。这是风险 R1 的另一半（前半是版本号）。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **625 通过 / 0 失败**。

⇒ **P2 整体完成**（Core API + UI + 8 项断言）。

---

## P3 · 页面重分为 6 页（V4-4 / V4-5）

> ⚠️ **顺序很重要：先写 P3.1 的两条断言，再开始搬控件。** 搬完再补断言等于没补。

### P3.1 先写两条「防搬丢 / 防搬重」的断言

**新增** `tests/BlackHoleCursor.Tests/UiBindingTests.cs`（可复用 `AppearanceBindingTests.Locate()` 那套
从测试目录往上找 `src/.../Views` 的工具；或者直接扫整个 Views 目录）

1. **`NoSettingIsBoundTwice`**：扫描 `UI/Views/*.xaml` 里所有 `{Binding X, ...}` 的属性名，
   同一个属性出现两次即失败 —— **这条现在就应该红**（`LensEnabled` 在 `AppearanceView` 与 `LensView` 各一个开关，
   正是 V4-5 要修的）。修完转绿。
2. **`EveryAdjustableSettingHasAControl`**：显式列出「用户可调属性清单」（沿用
   `SlidersCoverAllAdjustableV2Parameters` 那个数组的风格），断言每个都在某个 View 里出现至少一次。
   新增设置时必须同步往清单里加 —— 这就是它的价值。

### ✅ P3.1 已完成（2026-09-27）：两条断言到位，且 **V4-5 顺手修掉了**

`UiBindingTests.cs` 在 P1 那批里已经建好（含 `NoSettingIsBoundTwice` 的 Skip 版本），
这里补齐第二条、并打开第一条。

**验证过第一条「不是空过」**：去掉 Skip 后**立刻红**，且只报一处 ——
```
Collection: [["LensEnabled"] = ["AppearanceView.xaml", "LensView.xaml"]]
```
与 V4-5 的诊断完全一致。随即删掉 `AppearanceView.xaml` 里那个开关（唯一入口保留在 `LensView`），
断言转绿。**这正是「先写断言再搬控件」的价值：它直接告诉我要删的是哪一个。**

**第二条清单的取舍**（跑出来的结果，不是我拍的）

列了 40 项，跑出来**只有两项没有 XAML 控件**：
- `Enabled` —— 总开关，入口在**托盘菜单**（`App.UpdateTrayIcon()`），页面里没有、也不该有 → 移出清单并注明
- `TargetFrameRate` —— ⚠️ **至今没有任何界面入口**，v2 遗留缺口 → 移出清单 + 记在下面的待办里

> 📌 因此新增一条遗留：**`TargetFrameRate` 无 UI 入口**。
> 不在 v4 范围（v4 只重整分类、不新增控件），但等给它加了控件要把这一项补回清单。

**顺带**：修掉一个我自己造成的 xUnit 警告
（`Assert.False(x.Any(...))` → `Assert.DoesNotContain`，`PresetServiceTests:436`）。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **627 通过 / 0 失败**（625 + 新增 1 + Skip 转绿 1）。

### P3.2 新增「渲染与性能」页

**新增** `UI/Views/RenderView.xaml(.cs)`，把 `AppearanceView.xaml` 的整个「渲染」Card 搬过去：
曝光 / 星空强度 / 星云强度 / 星云色调（按钮 + 色块）/ 氛围流速 / 积分步数（`QualityBox`）。

⚠️ `EnsureQualityItems()`（填充 `QualityBox` 的下拉项）在 `AppearanceView.xaml.cs`，**要一起搬**，
否则下拉框变空的（不报错，只是没选项 —— 正是这类 silent failure 要靠 P3.1 的第 2 条 + 实机发现）。

### ✅ P3.2 已完成（2026-09-27）：新增「渲染与性能」页

**新增** `UI/Views/RenderView.xaml(.cs)`，把外观页的「渲染」整块搬过去：
曝光 / 星空强度 / 星云强度 / 星云色调（按钮 + 色块）/ 氛围流速 / 积分步数。

✅ **`EnsureQualityItems()` 跟着搬了**（计划里点名的静默失败点）——
它填「积分步数」下拉框的选项，漏掉的话下拉框**不报错、只是没选项**。
已用断言钉住：`RenderView_HostsTheRenderControls` 里检查 `EnsureQualityItems` 与「自动（按帧率）」都在。

**导航**：`MainWindow.xaml` 加 `Tag="Render"`、`MainWindow.xaml.cs` 加 `"Render" => new RenderView()`。

**既有断言同步（搬控件必须跟着改，否则必红）**
- `AppearanceBindingTests.SlidersCoverAllAdjustableV2Parameters`：清单去掉 5 个渲染类参数
  （它们改由跨页的 `EveryAdjustableSettingHasAControl` 守住）
- `AppearanceView_ExposesTheNebulaColorPicker` → **`AppearanceView_NoLongerHostsTheRenderControls`**
  （语义反过来：外观页不该再出现星云色调 / 积分步数，防「两处都有」）
- 新增 `RenderView_HostsTheRenderControls`、`MainWindow_NavigatesToTheRenderPage`

> ⚠️ **踩坑：文本断言会连注释一起扫**
> 我在外观页写了句注释「`EnsureQualityItems` 已搬到 RenderView」，
> 结果 `NoLongerHostsTheRenderControls` 立刻红 —— 注释里提到那些名字也算命中。
> 已把注释改成不含代码名的写法，并在原地留了提醒。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **629 通过 / 0 失败**。

### ✅ P3.3 已完成（2026-09-27）：引力透镜页补上抓屏状态

**你已定（prd-v4 §10）**：补上 —— 那页不再只有一个开关，抓屏是否正常一眼可见。

新增一块「抓屏状态」（照 `SystemView` 的每秒轮询写法，数据本来就有，只是搬到该在的地方）：

| 行 | 内容 |
|---|---|
| 抓屏 | 桌面复制（DXGI）/ **GDI 兼容模式（已降级）**；全黑时追加「扭曲已暂停」 |
| 帧率 | `XX FPS`，软件渲染时追加标记 |
| 积分步数 | 自动档显示实测步数（`AutoQuality.StepCount`），固定档显示档位步数 |

断言 `LensView_ShowsTheCaptureStatus`：三个标签在，且**三个数据源真的取到了**
（`IsDegraded` / `FramesPerSecond` / `AutoQuality.StepCount`）—— 否则只是三个空标签。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **630 通过 / 0 失败**。

### 原始计划：P3.3 引力透镜页

- `AppearanceView.xaml` 里那个「引力透镜」`ToggleRow` **删除**（唯一入口保留在 `LensView`）。
- `LensView` 保留「启用桌面扭曲」，并**补上抓屏状态**（是否降级 / 当前帧率 / 自动档步数）——
  数据取法照搬 `MainWindow.UpdateStatus()`（它已经在读 `RuntimeHost.Overlay` 的这些值）。
  > 这条**待你确认**（`prd-v4.md` §10）：不加也行，那页就只留一个开关。

### ✅ P3.4（前半，2026-09-27）：「出现 / 消失动画」组搬到跟随与动效页

从「程序设置」页搬走整个动画组（启用过渡动画 / 动画时长 / 过冲峰值 / 重播一次按钮），
`ReplayTransition_Click` 处理器跟着一起搬 —— 漏搬的话按钮点了没反应，而且不报错。

断言：新增 `FollowView_HostsTheTransitionGroup`（防搬丢）
+ `SettingsView_NoLongerHostsTheTransitionGroup`（防搬重）。

**顺带把 P3.5 的一半提前做了**：`XamlFilesUnderTest` 原来是**手列文件名**（只有外观页与设置页），
改成**扫描整个 Views 目录** —— 否则每加一个新页都要记得补进来，漏了就是「绑定名写错却没人发现」。

> ⚠️ 改完立刻暴露一条红的：`EverySettingsRangesReference_ExistsAndIsDouble` 对每个文件
> `Assert.NotEmpty(names)`。设置页搬走动画组后**再没有任何范围常量**（它只剩开关与按钮），
> 于是被自己那条「必须扫到东西」误伤。
> 修法：两条断言的 `NotEmpty` 都从「每个文件必须有」改成「**整体**至少扫到一个」——
> 这样仍能挡住正则失效导致的假绿，但不再强求每个页面都有滑块。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **632 通过 / 0 失败**。

### P3.4（后半，未做）跟随与动效 / 系统与程序

- **跟随与动效** = 现有 `FollowView`（惯性跟随 / 静止游荡 / 阻尼手感预设）
  ✅ **+ 从 `SettingsView` 搬来的「出现 / 消失动画」组**（已完成，见上）。
- **系统与程序** = 现有 `SystemView`（隐藏系统光标 / 智能兜底 / 运行状态）
  **+ `SettingsView` 剩下的组**（开机自启 / 截图补偿 / 配置文件）⇒ 两个页**合并**。
  合并后删掉 `SettingsView.xaml(.cs)`，`Navigate` 里去掉 `"Settings"` 分支。
- ⚠️ `AppearanceBindingTests.SettingsXamlPath` 直接引用 `SettingsView.xaml`，**删文件时要同步改**，
  否则测试崩（不是断言失败，是找不到文件）。

最终导航 6 项：外观 / 预设 / 渲染与性能 / 引力透镜 / 跟随与动效 / 系统与程序。

> **2026-09-27 微调**：你要求把「外观」与「预设」**换个位**，现为
> **预设 / 外观** / 渲染与性能 / 引力透镜 / 跟随与动效 / 系统与程序。
> `IsSelected="True"` 仍留在「外观」上 —— 换的是**顺序**，不是默认打开的页面，
> 所以打开主界面看到的还是外观页（想改成默认打开预设页的话说一声，挪个特性即可）。
> 断言 `MainWindow_HasExactlySixNavItems` 的期望顺序已同步改。

### ✅ P3 全部完成（2026-09-27）：6 页结构齐了

**P3.4 后半**：原「程序设置」页剩下的两组（运行开关 + 配置文件）并入「系统与程序」页，
`SettingsView.xaml(.cs)` **已删除**，导航 **7 项 → 6 项**：

```
外观 / 预设 / 渲染与性能 / 引力透镜 / 跟随与动效 / 系统与程序
```

搬的不只是 XAML：`ConfigPath` 的填充与三个按钮的处理器（打开目录 / 恢复默认 / 退出程序）
都跟着进了 `SystemView` —— 漏搬的话按钮点了没反应，而且不报错。

✅ **计划里预警的那个坑踩准了**：`AppearanceBindingTests.SettingsXamlPath` 直接
`Locate("SettingsView.xaml")`，删文件后是**「找不到文件」而不是断言失败**（测试直接崩）。
已同步改成 `SystemView.xaml`，那条断言也相应改名为 `SystemView_ExposesTheScreenshotCompensationToggle`。

**新增断言**
- `SettingsViewIsGone` —— 确认旧页真的删了（留着就是两个入口）
- `SystemView_HostsTheMergedProgramGroups` —— 合并过来的两组都在，且三个按钮的处理器真的搬了
- `MainWindow_HasExactlySixNavItems` —— 钉住「6 项、且顺序与名字都对」，防止以后又加回 7 项

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **634 通过 / 0 失败**。

✅ **实机验收已通过**（2026-09-27，你的原话「实机还不错」）：
6 页逐页点过，控件即时生效、`LensEnabled` 只有一处开关。**P3 至此全部闭环。**

---

## P4 · 图标与版本号（V4-6 / V4-7 / V4-8）

### ✅ P4 已完成（2026-09-27）

**P4.1** 新增 `tools/IconGen/`（控制台，**链接** `src/BlackHoleCursor/IconFactory.cs` 同一份绘制代码 ——
图标只有一份画法，否则托盘图标和 exe 图标迟早长得不一样）。
产物：`src/BlackHoleCursor/Resources/app.ico`（26 KB，启用）与 `app-off.ico`（23 KB，禁用），
各含 **256 / 48 / 32 / 16** 四张；16 与 32 走简化分支（去掉光晕与内环，只留黑核 + 一圈亮环，风险 R4）。

**P4.2 / P4.3** csproj 加 `<ApplicationIcon>` + 两张 `<Resource>`；托盘改走
`IconFactory.LoadAppIcon(enabled)`（按 `Settings.Enabled` 在两张之间切换），不再每次启动现画。

**P4.4** `.iss`：`AppVersion` → `4.0.0`、`SetupIconFile` 指向 app.ico、注释里的产物名同步；
`MainWindow.xaml`：`CURSOR v1.0` → **`CURSOR v4.0`**。

**P4.5** 新增 `IconTests`（4 项）：两张能被构造成 `Icon`、ICO 头正确且尺寸表含 16/32/48/256、
启用/禁用**看起来**不同（比渲染后的平均色差，实测 **25.33**，阈值 10）、版本号三处一致且无 v1.0 残留。

> ⚠️ **三个坑（都踩了）**
> 1. `dotnet run --project ... --nologo` 会把 `--nologo` 当成**本程序**的参数 →
>    输出目录被建成一个叫「--nologo」的文件夹。改成只认不以 `-` 开头的参数。
> 2. 用 `..\..\..\..` 数层级找仓库根**数错了**（`bin/Release/<tfm>` 深度会变），
>    产物跑到 `tools/src/...`。改成**往上找有 sln 的那一层**（与测试里 Locate 同一套做法）。
> 3. 控制台项目没有 WPF 隐式带进来的 `System.Drawing`，必须显式
>    `<PackageReference Include="System.Drawing.Common" Version="8.0.0" />`。

**验证**：`dotnet build -c Release` **0 警告 0 错误**；全量 **638 通过 / 0 失败**（634 + 4）；
发布后 `Icon.ExtractAssociatedIcon(dist\BlackHoleCursor.exe)` **成功取到 32×32 图标** ——
exe 图标确实嵌进去了。

⏳ **还剩两处需要你看（风险 R3，两处都要看）**：
任务栏 / Alt+Tab / 标题栏显示的是不是这个图标；托盘在启用 / 禁用下是不是**两张不同**的图标；
以及 16px 下还认不认得出「一个环 + 黑核」。

### 原始计划：P4.1 生成 `.ico`

- **新增** `tools/IconGen/`（控制台项目，照 `tools/ApiProbe/` 的样子）：
  - 把 `IconFactory` 的绘制部分抽成可复用的 `DrawBlackHoleIcon(Graphics, size, enabled)`；
  - 渲染 **256 / 48 / 32 / 16** 四个尺寸，写成一个多图 ICO；
  - **16 与 32 单独简化**：去掉光晕与内环，只留「黑核 + 一圈亮环」（风险 R4：不然小尺寸糊成一团）；
  - 出两张：`app.ico`（彩色，启用）/ `app-off.ico`（灰暗，禁用）。
- 产物提交到 `src/BlackHoleCursor/Resources/`（**编译不再依赖工具**，工具只是生成器）。
- 多图 ICO 沿用现有 `PngToIcon` 的做法（PNG 压缩，Vista+ 支持；我们的最低系统是 Win10 1809，安全）。

### P4.2 接到程序上

**改** `src/BlackHoleCursor/BlackHoleCursor.csproj`
```xml
<ApplicationIcon>Resources\app.ico</ApplicationIcon>
<ItemGroup>
  <Resource Include="Resources\app.ico" />
  <Resource Include="Resources\app-off.ico" />
</ItemGroup>
```

### P4.3 托盘两张

**改** `src/BlackHoleCursor/IconFactory.cs`：从资源加载（不再每次运行时现画），
`App.UpdateTrayIcon()`（:281）按 `Settings.Enabled` 在两张之间切换 —— 保留现在「启用 / 禁用看得出来」的行为。

### P4.4 安装包与版本号

**改** `installer/BlackHoleCursor.iss`
- `SetupIconFile=..\src\BlackHoleCursor\Resources\app.ico`
- `#define AppVersion "4.0.0"`；注释里的产物名同步（`BlackHoleCursor-Setup-4.0.0.exe`）
- `UninstallDisplayIcon` 指向 exe，设了 `ApplicationIcon` 后自动生效，不用改

**改** `MainWindow.xaml`：`CURSOR v1.0` → `CURSOR v4.0`。

### P4.5 测试与验收

**新增** `tests/BlackHoleCursor.Tests/IconTests.cs`
- 两张资源存在，且能被构造成 `System.Drawing.Icon`
- ICO 头正确、图像数 ≥ 3、包含 16/32/48/256
- 启用 / 禁用取到**不同**的图标（纯函数，不依赖 WPF）

**文本断言**：`MainWindow.xaml` 含 `v4.0`；`.iss` 含 `4.0.0`；csproj 含 `ApplicationIcon`。

**验收（两处都要看，风险 R3）**
- exe 文件属性里有图标；任务栏 / Alt+Tab / 窗口标题栏显示它
- 托盘在启用 / 禁用下是**两张不同**图标
- 小尺寸（16px）下仍然认得出是「一个环 + 黑核」

---

## P5 · 全量测试 + 发布 + 实机验收

### ✅ P5 收尾（2026-09-27）

1. `dotnet build -c Release` → **0 警告 0 错误** ✅
2. `dotnet test` → **638 通过 / 0 失败** ✅（基线 606，v4 净增 **32** 条，只加没减）
3. 发布：`Remove-Item dist -Recurse` **被安全机制拒绝**（`genie-trash rejected`，
   这个坑在 v2 就记过）→ 实际是**增量发布**，产物 `12:40:40`。
   ⚠️ 既然删不动，**核对就不要数文件，改成实跑** —— 但这次冒烟**跳过**了：
   已有实例在跑（PID 16420，`12:45:42` 启动，**晚于产物** ⇒ 跑的正是最新版），
   强杀会让你失去鼠标，不值得为了一次冒烟冒这个险。
4. 实机清单状态：

   | 项 | 状态 |
   |---|---|
   | 6 页逐页（控件即时生效 / `LensEnabled` 只有一处） | ✅ 已确认（「实机还不错」） |
   | 图标：exe / 任务栏 / 托盘两张 / 16px 可辨认 | ✅ **已实机确认（2026-09-27）** |
   | 预设页：应用内置 / 另存 / 覆盖 / 重命名 / 删除 / 导出导入 + 1 秒淡入 | ✅ **已实机确认（2026-09-27）** |

   ⇒ **实机清单全部打勾**，v4 无悬挂项。

---

## v4 收尾后的遗留项（都不在 v4 范围内 / 或因故跳过）

1. ~~**图标实机三处**（风险 **R3**）~~ → ✅ **已确认并关闭（2026-09-27）**：
   任务栏 / Alt+Tab / 标题栏是新图标；托盘启用与禁用是**两张**；16px 可辨认。
   **R3 解除。**
2. **安装包从未编译**：`.iss` 已按 V4-8 改好（4.0.0 + `SetupIconFile`），
   但本机没有 Inno Setup，至今没编译过 —— `prd-v2.md` **R16**。
3. **v2 遗留（你此前决定留到以后）**：后台线程抓屏（52 → 60 FPS 的唯一出路）、
   预览改用真实抓屏底图（现为程序化棋盘）。

### 原始 P5 清单

1. `dotnet build -c Release` → 0 警告 0 错误
2. `dotnet test` → 全绿（**基线 606**，v4 只会加不会减）
3. `Remove-Item dist -Recurse` 后重新 `publish`（用 App 的 csproj，别在仓库根 publish）
4. 实机清单：
   - 预设页：应用内置 / 另存 / 覆盖 / 重命名 / 删除 / 导出 / 导入（含重名与损坏文件）
   - 6 页逐页：每个控件都即时生效；`LensEnabled` 只有一处
   - 图标：exe / 任务栏 / 托盘（启用与禁用）、16px 下仍可辨认

---

## 风险登记

| # | 风险 | 应对 |
|---|---|---|
| **R1** | 导出的 `.json` 以后字段变了读不回来 | 带 `version` 字段；未知字段忽略；版本不符返回 error 且不动现有数据 |
| **R2** | 拆页时把控件搬丢 / 搬重 | P3.1 两条断言**先写**；搬完逐页实机点一遍 |
| **R3** | `.ico` 没生效（`ApplicationIcon` 与托盘取图标是两条路径） | 两处分别验收；托盘单独从资源构造，不依赖 exe 图标 |
| **R4** | 16 / 32px 图标糊成一团 | 小尺寸单独简化（去掉光晕与内环） |
| **R5** | 预设改名 / 删除后 UI 与持久化不同步 | 所有写操作走 `PresetService` 并**立即重建列表** |
| **R6** | 删 `SettingsView.xaml` 会打断 `AppearanceBindingTests` | ✅ **已处理**（2026-09-27）：`SettingsXamlPath` → `SystemXamlPath`（`SystemView.xaml`），断言同步改名。删文件带来的不是断言失败而是**测试直接崩**，务必先改再删 |
| **R7** | 全量测试 OOM（本机 8GB，plan-v3 踩过） | 先看可用内存；成片 `X8000` 报错先怀疑内存，再 `dotnet test -- xunit.parallelizeTestCollections=false` |
| **R8** | 预设名大小写（`A → a`） | 字典是 `OrdinalIgnoreCase`，改名要判自冲突（P1.4 有断言） |

---

## 验收标准（P1–P4 全部完成）

| 项 | 标准 |
|---|---|
| 预设页 | 内置与「我的」视觉上明确分开；内置不可改名 / 删除 |
| 预设操作 | 另存 / 覆盖 / 重命名 / 删除 全部可用，非法名给出明确提示 |
| 导出导入 | 往返一致；重名给「覆盖 / 另存为」；损坏文件不崩且不破坏现有预设 |
| 6 页 | 导航 6 项；每个设置项在界面里**恰好出现一次**（自动断言） |
| 无遗漏 | 可调属性清单里的每一项都能在界面上找到（自动断言） |
| 图标 | exe / 任务栏 / 窗口 / 托盘全覆盖；托盘启用与禁用是两张 |
| 版本 | 主界面 v4.0；安装包 4.0.0 |
| 回归 | 0 警告 0 错误 + `dotnet test` 全绿 + 实机无异常 |
