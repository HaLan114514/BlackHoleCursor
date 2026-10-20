using System.Reflection;
using System.Text.RegularExpressions;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

/// <summary>
/// 外观页是纯数据绑定：属性名写错不会编译报错，运行时只会静默失效（滑块变成死控件）。
/// 这个测试把 XAML 里的绑定路径与 <c>x:Static</c> 常量逐一对照模型，挡住这类低级错误。
/// </summary>
public class AppearanceBindingTests
{
    private static readonly string XamlPath = Locate("AppearanceView.xaml");

    /// <summary>
    /// 系统页同样是纯数据绑定（M4.3 的「截图补偿」开关就在那里），
    /// 写错一样不报错、只静默失效，所以一并纳入校验。
    ///
    /// v4：原「程序设置」页已并入该系统页，所以这里从 `SettingsView.xaml` 改成了 `SystemView.xaml`
    /// —— 删文件时**必须**同步改这里，否则测试是「找不到文件」而不是断言失败。
    /// </summary>
    private static readonly string SystemXamlPath = Locate("SystemView.xaml");

    private static readonly string ViewsDirectory = Path.GetDirectoryName(XamlPath)!;

    private static string Locate(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(
                dir.FullName, "src", "BlackHoleCursor", "UI", "Views", fileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 {fileName}，测试无法校验绑定");
    }

    /// <summary>
    /// v4：**扫描整个 Views 目录**，而不是手列文件名。
    ///
    /// 原来只列了外观页与设置页 —— 每加一个新页就得记得往这里补，漏了就是「绑定名写错
    /// 却没人发现 → 控件变成死的，且不报错」。改成扫目录后新页自动纳入。
    /// </summary>
    private static IEnumerable<string> XamlFilesUnderTest
        => Directory.GetFiles(ViewsDirectory, "*.xaml").OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void EveryBindingPath_ExistsOnSettingsModel()
    {
        // ⚠️ 不要求「每个页面都有绑定」—— 像预设页这种控件靠代码生成的页面本来就没有。
        // 这里只保证整体扫到了东西（否则正则失效会假绿）。
        int total = 0;

        foreach (string xamlPath in XamlFilesUnderTest)
        {
            string name = Path.GetFileName(xamlPath);
            string xaml = File.ReadAllText(xamlPath);
            var paths = Regex
                .Matches(xaml, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)\s*,")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            total += paths.Count;

            foreach (string path in paths)
            {
                var property = typeof(SettingsModel).GetProperty(
                    path, BindingFlags.Public | BindingFlags.Instance);

                Assert.True(property is not null, $"{name} 绑定了 SettingsModel 上不存在的属性「{path}」");
                Assert.True(property!.CanWrite, $"绑定「{path}」是只读属性，双向绑定不会生效");
            }
        }

        Assert.True(total > 0, "一个绑定都没扫到 —— 正则失效了？");
    }

    [Fact]
    public void EverySettingsRangesReference_ExistsAndIsDouble()
    {
        // 同上：不要求每个页面都有滑块（纯开关的页面就没有范围常量），只保证整体扫到了东西
        int total = 0;

        foreach (string xamlPath in XamlFilesUnderTest)
        {
            string name = Path.GetFileName(xamlPath);
            string xaml = File.ReadAllText(xamlPath);
            var names = Regex
                .Matches(xaml, @"\{x:Static\s+core:SettingsRanges\.([A-Za-z0-9_]+)\}")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            total += names.Count;

            foreach (string rangeName in names)
            {
                var field = typeof(SettingsRanges).GetField(
                    rangeName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                Assert.True(field is not null, $"{name} 引用了不存在的 SettingsRanges.{rangeName}");
                Assert.True(
                    field!.FieldType == typeof(double),
                    $"SettingsRanges.{rangeName} 必须是 double 才能绑到 Slider 的 Minimum/Maximum");
            }
        }

        Assert.True(total > 0, "一个范围常量都没扫到 —— 正则失效了？");
    }

    /// <summary>
    /// M4.3：截图补偿的开关必须真的出现在界面上，否则用户没法关。
    /// v4：它随「程序设置」页合并到了系统页，因此这条现在查的是系统页。
    /// </summary>
    [Fact]
    public void SystemView_ExposesTheScreenshotCompensationToggle()
    {
        string xaml = File.ReadAllText(SystemXamlPath);

        Assert.Contains(
            "{" + $"Binding {nameof(SettingsModel.ScreenshotCompensation)}, Mode=TwoWay" + "}",
            xaml,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// v4（V4-4）：渲染类控件已整体搬到「渲染与性能」页。
    /// 这条是**防搬漏**的另一半 —— 外观页不该再留着它们（留着就会变成两处重复入口）。
    /// </summary>
    [Fact]
    public void AppearanceView_NoLongerHostsTheRenderControls()
    {
        string xaml = File.ReadAllText(XamlPath);
        string codeBehind = File.ReadAllText(Path.ChangeExtension(XamlPath, ".xaml.cs"));

        foreach (string leftover in new[]
                 {
                     "NebulaColor_Click", "NebulaSwatch",       // 星云色调（fixes-v2 问题 6）
                     "QualityBox", "EnsureQualityItems",         // 积分步数（含下拉项的填充）
                 })
        {
            Assert.DoesNotContain(leftover, xaml, StringComparison.Ordinal);
            Assert.DoesNotContain(leftover, codeBehind, StringComparison.Ordinal);
        }
    }

    /// <summary>页面上的每个滑块都必须能真正驱动 v2 渲染，否则等于白摆一个控件。</summary>
    [Fact]
    public void SlidersCoverAllAdjustableV2Parameters()
    {
        string xaml = File.ReadAllText(XamlPath);
        var bound = Regex
            .Matches(xaml, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)\s*,")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        string[] adjustable =
        [
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

            // ⚠️ v4（V4-4）：下面 5 个**不在此清单** —— 它们随「渲染」那块搬到了
            // 「渲染与性能」页（RenderView）：Exposure / StarGain / NebulaGain /
            // AmbientFlowSpeed / QualitySteps。
            // 它们由 `UiBindingTests.EveryAdjustableSettingHasAControl`（跨所有 View）负责守住。
        ];

        foreach (string name in adjustable)
        {
            Assert.True(bound.Contains(name), $"外观页缺少对「{name}」的控件，用户无法调整它");
        }
    }
}
