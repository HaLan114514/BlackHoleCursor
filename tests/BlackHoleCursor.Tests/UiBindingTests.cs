using System.Text.RegularExpressions;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// v4：界面结构层面的断言（预设页接进去了吗、预设搬走了吗、有没有重复的控件）。
///
/// 为什么用「读 XAML 文本」这种笨办法：WPF 的绑定与导航都是**字符串**，
/// 写错不报错、运行时静默失效（控件变死的）。实机点一遍能发现，但那太贵了 ——
/// 这里用文本断言把这类错误挡在 CI 里。
/// </summary>
public class UiBindingTests
{
    private static readonly string ViewsDirectory = LocateDirectory("UI", "Views");
    /// <summary>Views 的上两级：`.../BlackHoleCursor/UI/Views` → `.../BlackHoleCursor`。</summary>
    private static readonly string AppDirectory =
        Path.GetDirectoryName(Path.GetDirectoryName(ViewsDirectory)!)!;

    private static string LocateDirectory(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "BlackHoleCursor");
            foreach (string part in parts)
            {
                candidate = Path.Combine(candidate, part);
            }

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"找不到 {string.Join('/', parts)}，界面断言无法进行");
    }

    private static string Read(string directory, string fileName)
        => File.ReadAllText(Path.Combine(directory, fileName));

    // -------------------------------------------------------------------------
    // V4-1：预设独立成页
    // -------------------------------------------------------------------------

    [Fact]
    public void MainWindow_NavigatesToThePresetPage()
    {
        string xaml = Read(AppDirectory, "MainWindow.xaml");
        string code = Read(AppDirectory, "MainWindow.xaml.cs");

        Assert.Contains("Tag=\"Presets\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new PresetView()", code, StringComparison.Ordinal);
    }

    /// <summary>预设必须**真的**从外观页搬走（留在两处会让用户不知道该点哪个）。</summary>
    [Fact]
    public void AppearanceView_NoLongerHostsThePresets()
    {
        string xaml = Read(ViewsDirectory, "AppearanceView.xaml");
        string code = Read(ViewsDirectory, "AppearanceView.xaml.cs");

        Assert.DoesNotContain("PresetPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PresetNameBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPresetButtons", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePreset_Click", code, StringComparison.Ordinal);
    }

    /// <summary>预设页要同时给出「内置」与「我的预设」两块，以及 V4-2 的四个操作入口。</summary>
    [Fact]
    public void PresetView_ExposesBothGroupsAndTheCustomActions()
    {
        string xaml = Read(ViewsDirectory, "PresetView.xaml");
        string code = Read(ViewsDirectory, "PresetView.xaml.cs");

        Assert.Contains("Text=\"内置\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"我的预设\"", xaml, StringComparison.Ordinal);

        // 自定义预设那四个操作是代码动态生成的（每行一组按钮）
        foreach (string action in new[] { "\"应用\"", "\"覆盖保存\"", "\"重命名\"", "\"删除\"" })
        {
            Assert.Contains(action, code, StringComparison.Ordinal);
        }

        // 「另存为」是 XAML 里的静态按钮
        Assert.Contains("另存为预设", xaml, StringComparison.Ordinal);

        // 内置与自定义要分别取自不同的源，否则等于没分开
        Assert.Contains("p.IsBuiltIn", code, StringComparison.Ordinal);
        Assert.Contains("Presets.Custom", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 应用预设的**顺序**是 M5.2 的老坑：必须先抓当前外观做淡入起点，再改设置。
    /// 顺序反了淡入就没了，而且不报错 —— 这条把顺序钉住。
    /// </summary>
    [Fact]
    public void PresetView_FadesBeforeItApplies()
    {
        string code = Read(ViewsDirectory, "PresetView.xaml.cs");

        int fade = code.IndexOf("BeginPresetFade()", StringComparison.Ordinal);
        int apply = code.IndexOf("preset.ApplyTo(", StringComparison.Ordinal);

        Assert.True(fade >= 0, "应用预设前必须先 BeginPresetFade()");
        Assert.True(apply > fade, "BeginPresetFade() 必须在 ApplyTo 之前 —— 顺序反了淡入等于没做");
    }

    /// <summary>
    /// 内置预设一个都不能少（搬 UI 时最容易漏的就是这种「动态生成」的列表）。
    /// </summary>
    [Fact]
    public void PresetView_ListsEveryBuiltInPreset()
    {
        string code = Read(ViewsDirectory, "PresetView.xaml.cs");

        // 走的是 Presets.All 里 IsBuiltIn 的那部分 ⇒ 真源仍然是 LookPresets
        Assert.Contains("App.Current.Presets.All", code, StringComparison.Ordinal);
        Assert.Equal(8, LookPresets.Names.Count);
    }

    // -------------------------------------------------------------------------
    // 下面是 V4-4 / V4-5（P3）要补的两条：先把「现在已知的问题」记在这里
    // -------------------------------------------------------------------------

    /// <summary>
    /// V4-5：`LensEnabled` 现在在**两个**页面各有一个开关 —— 这条断言现在就该是红的，
    /// P3 修完转绿（所以它现在被标记为 Skip，等 P3 再打开）。
    /// </summary>
    [Fact]
    public void NoSettingIsBoundTwice()
    {
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string path in Directory.GetFiles(ViewsDirectory, "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)"))
            {
                string name = m.Groups[1].Value;
                if (!seen.TryGetValue(name, out var files))
                {
                    seen[name] = files = new List<string>();
                }

                files.Add(Path.GetFileName(path));
            }
        }

        var duplicated = seen.Where(p => p.Value.Count > 1).ToList();
        Assert.Empty(duplicated);
    }

    /// <summary>
    /// V4-4：可调属性清单 —— 每一项都必须在**某个** View 里出现至少一次。
    ///
    /// 与 <see cref="NoSettingIsBoundTwice"/> 是一对：
    /// 那条防「搬重」（同一个设置两个控件），这条防「搬丢」（拆页时控件没跟着走）。
    /// 新增可调设置时必须**同步往这个清单里加** —— 这正是它存在的价值。
    /// </summary>
    [Fact]
    public void EveryAdjustableSettingHasAControl()
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in Directory.GetFiles(ViewsDirectory, "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)"))
            {
                bound.Add(m.Groups[1].Value);
            }
        }

        string[] adjustable =
        [
            // ---- 外观：造型 / 颜色 / 盘形 / 相对论 ----
            nameof(SettingsModel.HoleSize),
            nameof(SettingsModel.DiskTemp),
            nameof(SettingsModel.TintStrength),
            nameof(SettingsModel.DiskIncl),
            nameof(SettingsModel.DiskRoll),
            nameof(SettingsModel.DiskInner),
            nameof(SettingsModel.DiskOuter),
            nameof(SettingsModel.DiskOpacity),
            nameof(SettingsModel.DopplerMix),
            nameof(SettingsModel.BeamExponent),
            nameof(SettingsModel.DiskGain),
            nameof(SettingsModel.DiskContrast),
            nameof(SettingsModel.DiskWinding),
            nameof(SettingsModel.DiskSpeed),
            nameof(SettingsModel.Spin),

            // ---- 渲染与性能 ----
            nameof(SettingsModel.Exposure),
            nameof(SettingsModel.StarGain),
            nameof(SettingsModel.NebulaGain),
            nameof(SettingsModel.AmbientFlowSpeed),
            nameof(SettingsModel.QualitySteps),

            // ---- 引力透镜 ----
            nameof(SettingsModel.LensEnabled),

            // ---- 跟随与动效 ----
            nameof(SettingsModel.FollowEnabled),
            nameof(SettingsModel.FollowStiffness),
            nameof(SettingsModel.FollowDamping),
            nameof(SettingsModel.FollowMaxSpeed),
            nameof(SettingsModel.FollowMaxAccel),
            nameof(SettingsModel.FollowSnapDistance),
            nameof(SettingsModel.FollowAdaptiveSpeed),
            nameof(SettingsModel.IdleWanderEnabled),
            nameof(SettingsModel.IdleWanderDelay),
            nameof(SettingsModel.IdleWanderRadius),
            nameof(SettingsModel.IdleWanderSpeed),
            nameof(SettingsModel.TransitionEnabled),
            nameof(SettingsModel.TransitionDuration),
            nameof(SettingsModel.TransitionOvershoot),

            // ---- 系统与程序 ----
            // ⚠️ `Enabled` 不在清单里：它是**总开关**，入口在托盘菜单（不是页面控件的 Binding），
            //    由 `App.UpdateTrayIcon()` 切换，页面里没有、也不该有它的开关。
            // ⚠️ `TargetFrameRate` 也不在：**它至今没有任何界面入口**（v2 遗留缺口，
            //    已登记在 plan-v4.md）。等给它加了控件，记得把这一项补回清单。
            nameof(SettingsModel.HideSystemCursor),
            nameof(SettingsModel.AutoFallback),
            nameof(SettingsModel.AutoStart),
            nameof(SettingsModel.ScreenshotCompensation),
        ];

        var missing = adjustable.Where(p => !bound.Contains(p)).ToList();

        Assert.True(missing.Count == 0,
            "这些可调设置没有任何控件（拆页时搬丢了？）：" + string.Join("、", missing));
    }

    // -------------------------------------------------------------------------
    // V4-4：「引力透镜」页补抓屏状态（prd-v4 §10 的待确认项，已按「补上」实现）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 透镜靠抓屏活着，所以抓屏状态要摆在透镜页上：
    /// 是否降级 / 当前帧率 / 自动档步数，三项都得有。
    /// </summary>
    [Fact]
    public void LensView_ShowsTheCaptureStatus()
    {
        string xaml = Read(ViewsDirectory, "LensView.xaml");
        string code = Read(ViewsDirectory, "LensView.xaml.cs");

        foreach (string block in new[] { "CaptureState", "PerfState", "StepState" })
        {
            Assert.Contains(block, xaml, StringComparison.Ordinal);
        }

        // 三项的数据来源必须真的取到了，否则只是三个空标签
        Assert.Contains("IsDegraded", code, StringComparison.Ordinal);      // 是否降级
        Assert.Contains("FramesPerSecond", code, StringComparison.Ordinal); // 帧率
        Assert.Contains("AutoQuality.StepCount", code, StringComparison.Ordinal); // 自动档步数
    }

    // -------------------------------------------------------------------------
    // V4-4：出现 / 消失动画组从「程序设置」搬到「跟随与动效」
    // -------------------------------------------------------------------------

    [Fact]
    public void FollowView_HostsTheTransitionGroup()
    {
        string xaml = Read(ViewsDirectory, "FollowView.xaml");
        string code = Read(ViewsDirectory, "FollowView.xaml.cs");

        Assert.Contains("出现 / 消失动画", xaml, StringComparison.Ordinal);
        Assert.Contains("TransitionEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("TransitionDuration", xaml, StringComparison.Ordinal);
        Assert.Contains("TransitionOvershoot", xaml, StringComparison.Ordinal);

        // 「重播一次」是按钮 + 处理器，不走 Binding —— 漏搬的话按钮点了没反应
        Assert.Contains("ReplayTransition_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("private void ReplayTransition_Click", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// v4：程序设置页已**整体并入**系统页，所以这个文件应该**不存在**。
    /// 留着它就会有两个入口（导航 6 项也会变回 7 项）。
    /// </summary>
    [Fact]
    public void SettingsViewIsGone()
    {
        Assert.False(File.Exists(Path.Combine(ViewsDirectory, "SettingsView.xaml")),
            "程序设置页已并入「系统与程序」页，SettingsView.xaml 应该已删除");
        Assert.False(File.Exists(Path.Combine(ViewsDirectory, "SettingsView.xaml.cs")));
    }

    /// <summary>合并后，系统页要同时接管原程序设置页的那两组：程序运行 + 配置文件。</summary>
    [Fact]
    public void SystemView_HostsTheMergedProgramGroups()
    {
        string xaml = Read(ViewsDirectory, "SystemView.xaml");
        string code = Read(ViewsDirectory, "SystemView.xaml.cs");

        // 程序运行：开机自启 + 截图补偿
        Assert.Contains(nameof(SettingsModel.AutoStart), xaml, StringComparison.Ordinal);
        Assert.Contains(nameof(SettingsModel.ScreenshotCompensation), xaml, StringComparison.Ordinal);

        // 配置文件：路径显示 + 三个按钮的处理器（漏搬的话按钮点了没反应，且不报错）
        Assert.Contains("ConfigPath", xaml, StringComparison.Ordinal);
        foreach (string handler in new[] { "OpenFolder_Click", "Reset_Click", "Exit_Click" })
        {
            Assert.Contains(handler, xaml, StringComparison.Ordinal);
            Assert.Contains($"private void {handler}", code, StringComparison.Ordinal);
        }
    }

    /// <summary>导航最终是 6 项 —— 原来 7 项（多出来的「程序设置」已合并）。</summary>
    [Fact]
    public void MainWindow_HasExactlySixNavItems()
    {
        string xaml = Read(AppDirectory, "MainWindow.xaml");

        var items = Regex.Matches(xaml, @"<ListBoxItem\s+Content=""([^""]+)""\s+Tag=""(\w+)""")
            .Select(m => (Label: m.Groups[1].Value, Tag: m.Groups[2].Value))
            .ToList();

        Assert.Equal(6, items.Count);
        Assert.Equal(
            ["预设", "外观", "渲染与性能", "引力透镜", "跟随与动效", "系统与程序"],
            items.Select(i => i.Label).ToArray());
    }

    // -------------------------------------------------------------------------
    // V4-4：「渲染与性能」页（从外观页搬来的那一块）
    // -------------------------------------------------------------------------

    [Fact]
    public void MainWindow_NavigatesToTheRenderPage()
    {
        string xaml = Read(AppDirectory, "MainWindow.xaml");
        string code = Read(AppDirectory, "MainWindow.xaml.cs");

        Assert.Contains("Tag=\"Render\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new RenderView()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 渲染类控件必须**真的**出现在新页上（防搬丢）。
    /// 积分步数走 <c>ComboBox</c> + <c>Tag</c>，不在 Binding 扫描里，所以单列一条。
    /// </summary>
    [Fact]
    public void RenderView_HostsTheRenderControls()
    {
        string xaml = Read(ViewsDirectory, "RenderView.xaml");
        string code = Read(ViewsDirectory, "RenderView.xaml.cs");

        foreach (string label in new[] { "曝光", "星空强度", "星云强度", "氛围流速", "积分步数" })
        {
            Assert.Contains(label, xaml, StringComparison.Ordinal);
        }

        // fixes-v2 问题 6：星云色调是「按钮 + 代码后置」，不走 Binding
        Assert.Contains("NebulaColor_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("NebulaSwatch", xaml, StringComparison.Ordinal);
        Assert.Contains("private void NebulaColor_Click", code, StringComparison.Ordinal);

        // ⚠️ 最容易漏的就是这个：漏掉它下拉框**不报错、只是没有选项**，
        // 只有实机点开才发现 —— 典型静默失败，必须钉住。
        Assert.Contains("EnsureQualityItems", code, StringComparison.Ordinal);
        Assert.Contains("自动（按帧率）", code, StringComparison.Ordinal);
    }
}
