using System.Drawing;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

public class PresetServiceTests : IDisposable
{
    private readonly string _dir;

    public PresetServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bhc-presets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private PresetService Create() => new(_dir);

    [Fact]
    public void BuiltInPresets_AreAvailable()
    {
        var service = Create();

        // v2（M1.8.4）：内置预设就是 prd-v2 §6.3 的 8 个，真源在 LookPresets
        Assert.Equal(LookPresets.Names, PresetLibrary.BuiltInNames);
        Assert.Equal(8, service.All.Count(p => p.IsBuiltIn));
    }

    /// <summary>
    /// 配置侧的 8 个预设必须与渲染侧 <see cref="LookPresets"/> 逐项一致。
    /// 两边共用同一份数值，这条断言负责挡住「改了一边忘了另一边」。
    /// </summary>
    [Fact]
    public void BuiltInPresets_MatchTheLookPresetsTable()
    {
        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var fromSettings = LookPresets.FromSettings(PresetLibrary.CreateBuiltIn(name));
            var expected = LookPresets.Create(name);

            Assert.Equal(expected.TempKelvin, fromSettings.TempKelvin, 6);
            Assert.Equal(expected.Inclination, fromSettings.Inclination, 6);
            Assert.Equal(expected.Roll, fromSettings.Roll, 6);
            Assert.Equal(expected.InnerRadius, fromSettings.InnerRadius, 6);
            Assert.Equal(expected.OuterRadius, fromSettings.OuterRadius, 6);
            Assert.Equal(expected.Opacity, fromSettings.Opacity, 6);
            Assert.Equal(expected.DopplerMix, fromSettings.DopplerMix, 6);
            Assert.Equal(expected.BeamExponent, fromSettings.BeamExponent, 6);
            Assert.Equal(expected.Gain, fromSettings.Gain, 6);
            Assert.Equal(expected.Contrast, fromSettings.Contrast, 6);
            Assert.Equal(expected.Winding, fromSettings.Winding, 6);
            Assert.Equal(expected.Speed, fromSettings.Speed, 6);
            Assert.Equal(expected.Exposure, fromSettings.Exposure, 6);
            Assert.Equal(expected.StarGain, fromSettings.StarGain, 6);

            // fixes-v2 问题 6：星云亮部配色也走同一个真源（预设带的色调）
            Assert.Equal(expected.NebulaColor, fromSettings.NebulaColor);
        }
    }

    /// <summary>
    /// fixes-v2 问题 6：预设**可以**带自己的星云色调 ——
    /// 这是 M5.2「预设不改增益类参数」的唯一例外，用户已确认。
    /// </summary>
    [Fact]
    public void BuiltInPresets_AuthorTheirOwnNebulaColor()
    {
        var colors = new HashSet<int>();

        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var settings = PresetLibrary.CreateBuiltIn(name);
            colors.Add(settings.NebulaColor.ToArgb());

            Assert.NotEqual(0, settings.NebulaColor.R | settings.NebulaColor.G | settings.NebulaColor.B);
        }

        Assert.True(colors.Count >= 6, $"预设的星云色调应当各有区别，实测只有 {colors.Count} 种");

        // 方向性：暖色预设偏红、冷色预设偏蓝
        Assert.True(PresetLibrary.CreateBuiltIn(LookPresets.Inferno).NebulaColor.R
                    > PresetLibrary.CreateBuiltIn(LookPresets.Inferno).NebulaColor.B);
        Assert.True(PresetLibrary.CreateBuiltIn(LookPresets.PureLens).NebulaColor.B
                    > PresetLibrary.CreateBuiltIn(LookPresets.PureLens).NebulaColor.R);
    }

    /// <summary>应用预设要写入星云色调（例外项），但**不能**动星云增益（仍是用户选择）。</summary>
    [Fact]
    public void ApplyPatch_WritesTheNebulaColorButKeepsTheGain()
    {
        var target = new SettingsModel { NebulaGain = 0.8 };
        var expected = LookPresets.NebulaColorFor(LookPresets.Quasar);

        PresetLibrary
            .Create(LookPresets.Quasar, PresetLibrary.CreateBuiltIn(LookPresets.Quasar), isBuiltIn: true)
            .ApplyTo(target);

        Assert.Equal(expected, target.NebulaColor);
        Assert.Equal(0.8, target.NebulaGain, 6);
    }

    [Fact]
    public void BuiltInPresets_AreMutuallyDistinct()
    {
        var looks = PresetLibrary.BuiltInNames
            .Select(n => LookPresets.FromSettings(PresetLibrary.CreateBuiltIn(n)))
            .ToList();

        for (int i = 0; i < looks.Count; i++)
        {
            for (int j = i + 1; j < looks.Count; j++)
            {
                bool identical =
                    Math.Abs(looks[i].TempKelvin - looks[j].TempKelvin) < 1e-6 &&
                    Math.Abs(looks[i].Inclination - looks[j].Inclination) < 1e-6 &&
                    Math.Abs(looks[i].Roll - looks[j].Roll) < 1e-6 &&
                    Math.Abs(looks[i].InnerRadius - looks[j].InnerRadius) < 1e-6 &&
                    Math.Abs(looks[i].OuterRadius - looks[j].OuterRadius) < 1e-6 &&
                    Math.Abs(looks[i].Opacity - looks[j].Opacity) < 1e-6 &&
                    Math.Abs(looks[i].Gain - looks[j].Gain) < 1e-6 &&
                    Math.Abs(looks[i].StarGain - looks[j].StarGain) < 1e-6;

                Assert.False(identical,
                    $"{PresetLibrary.BuiltInNames[i]} 与 {PresetLibrary.BuiltInNames[j]} 完全相同，等于少了一个预设");
            }
        }
    }

    [Fact]
    public void BuiltInPresets_DoNotAuthorSizeOrQuality()
    {
        // 预设只「写外观」：大小与性能档位是用户选择，预设本身不给出这两项
        // （M5.2 把应用方式改成增量补丁后，切换预设就不会把它们冲掉）。
        var defaults = new SettingsModel();

        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var settings = PresetLibrary.CreateBuiltIn(name);

            Assert.Equal(defaults.HoleSize, settings.HoleSize, 6);
            Assert.Equal(defaults.QualitySteps, settings.QualitySteps);
        }
    }

    [Fact]
    public void BuiltInPresets_ExposureStaysInAUsableRange()
    {
        // 曝光过低整个盘发灰、过高过曝成一团白。§6.3 的 8 个预设都落在 0.70–1.40。
        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var settings = PresetLibrary.CreateBuiltIn(name);

            Assert.InRange(settings.Exposure, 0.5, 1.5);
            Assert.InRange(settings.DiskTemp, SettingsRanges.DiskTempMin, SettingsRanges.DiskTempMax);
        }
    }

    [Fact]
    public void BuiltInPresets_DoNotTakeOverSystemBehaviour()
    {
        // 预设只管观感：隐藏光标 / 兜底 / 跟随 / 自启都必须保持默认，不能被预设改写
        var defaults = new SettingsModel();

        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var settings = PresetLibrary.CreateBuiltIn(name);

            Assert.Equal(defaults.HideSystemCursor, settings.HideSystemCursor);
            Assert.Equal(defaults.AutoFallback, settings.AutoFallback);
            Assert.Equal(defaults.FollowEnabled, settings.FollowEnabled);
            Assert.Equal(defaults.FollowStiffness, settings.FollowStiffness);
            Assert.Equal(defaults.FollowDamping, settings.FollowDamping);
            Assert.Equal(defaults.FollowAdaptiveSpeed, settings.FollowAdaptiveSpeed);
            Assert.Equal(defaults.AutoStart, settings.AutoStart);
        }
    }

    /// <summary>Gargantua 是默认预设，逐项对照 prd-v2 §6.3 的原值。</summary>
    [Fact]
    public void Gargantua_MatchesPrdTable()
    {
        var settings = PresetLibrary.CreateBuiltIn(LookPresets.Gargantua);

        Assert.Equal(4500.0, settings.DiskTemp, 6);
        Assert.Equal(1.52, settings.DiskIncl, 6);
        Assert.Equal(0.10, settings.DiskRoll, 6);
        Assert.Equal(2.2, settings.DiskInner, 6);
        Assert.Equal(7.0, settings.DiskOuter, 6);
        Assert.Equal(0.85, settings.DiskOpacity, 6);
        Assert.Equal(0.35, settings.DopplerMix, 6);
        Assert.Equal(2.0, settings.BeamExponent, 6);
        Assert.Equal(1.4, settings.DiskGain, 6);
        Assert.Equal(0.5, settings.DiskContrast, 6);
        Assert.Equal(7.0, settings.DiskWinding, 6);
        Assert.Equal(5.0, settings.DiskSpeed, 6);
        Assert.Equal(1.20, settings.Exposure, 6);
        Assert.Equal(0.0, settings.StarGain, 6);
    }

    [Fact]
    public void BuiltInPresets_AlwaysPassClamp()
    {
        foreach (string name in PresetLibrary.BuiltInNames)
        {
            var settings = PresetLibrary.CreateBuiltIn(name);
            Assert.False(settings.Clamp(), $"预设 {name} 的取值应本身合法");
        }
    }

    [Fact]
    public void TryGet_BuiltIn_ReturnsCopy()
    {
        var service = Create();

        Assert.True(service.TryGet(LookPresets.Gargantua, out var first));
        first.DiskTemp = 111;

        Assert.True(service.TryGet(LookPresets.Gargantua, out var second));
        Assert.NotEqual(111.0, second.DiskTemp);
    }

    [Fact]
    public void SaveCustom_ThenReload_Persists()
    {
        var service = Create();
        var settings = new SettingsModel
        {
            TintColor = Color.FromArgb(255, 1, 2, 3),
            DiskTemp = 8800,
            DiskIncl = 0.33,
        };

        Assert.True(service.TrySave("我的预设", settings));

        var reloaded = new PresetService(_dir);
        Assert.True(reloaded.TryGet("我的预设", out var restored));
        Assert.Equal(8800.0, restored.DiskTemp, 6);
        Assert.Equal(0.33, restored.DiskIncl, 6);
        Assert.Equal(Color.FromArgb(255, 1, 2, 3), restored.TintColor);
    }

    [Fact]
    public void SaveCustom_CannotOverrideBuiltIn()
    {
        var service = Create();

        Assert.False(service.TrySave(LookPresets.Gargantua, new SettingsModel()));
        Assert.False(service.TrySave("   ", new SettingsModel()));
    }

    // -------------------------------------------------------------------------
    // v4（V4-2）：重命名 / 覆盖保存
    // -------------------------------------------------------------------------

    [Fact]
    public void Rename_WorksAndPersists()
    {
        var service = Create();
        service.TrySave("旧名字", new SettingsModel { DiskTemp = 6100 });

        Assert.True(service.TryRename("旧名字", "新名字"));
        Assert.False(service.TryGet("旧名字", out _));
        Assert.True(service.TryGet("新名字", out var renamed));
        Assert.Equal(6100.0, renamed.DiskTemp, 6);

        var reloaded = new PresetService(_dir);
        Assert.True(reloaded.TryGet("新名字", out var persisted));
        Assert.Equal(6100.0, persisted.DiskTemp, 6);
    }

    [Fact]
    public void Rename_RejectsBuiltInNames()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());

        Assert.False(service.TryRename("甲", LookPresets.Gargantua), "不能把自定义预设改成内置名");
        Assert.False(service.TryRename(LookPresets.Quasar, "乙"), "内置预设不允许改名");
        Assert.True(service.TryGet("甲", out _));
    }

    [Fact]
    public void Rename_RejectsEmptyDuplicateAndUnknownNames()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());
        service.TrySave("乙", new SettingsModel());

        Assert.False(service.TryRename("甲", "   "));
        Assert.False(service.TryRename("甲", "乙"), "不能改成与另一个预设重名");
        Assert.False(service.TryRename("不存在", "丙"));

        Assert.True(service.TryGet("甲", out _));
        Assert.True(service.TryGet("乙", out _));
        Assert.False(service.TryGet("丙", out _));
    }

    /// <summary>
    /// 字典是 <c>OrdinalIgnoreCase</c> ⇒ 只改大小写命中的是**同一个**条目，应当放行。
    /// 判重若写成「名字已存在就拒绝」，这一类改名会被错杀。
    /// </summary>
    [Fact]
    public void Rename_AllowsCaseOnlyChange()
    {
        var service = Create();
        service.TrySave("MyPreset", new SettingsModel { DiskTemp = 6600 });

        Assert.True(service.TryRename("MyPreset", "mypreset"));
        Assert.True(service.TryGet("mypreset", out var renamed));
        Assert.Equal(6600.0, renamed.DiskTemp, 6);
        Assert.Single(service.Custom);
    }

    [Fact]
    public void SaveOver_UpdatesTheExistingPresetAndPersists()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });

        Assert.True(service.TrySaveOver("甲", new SettingsModel { DiskTemp = 7700 }));
        Assert.True(service.TryGet("甲", out var updated));
        Assert.Equal(7700.0, updated.DiskTemp, 6);

        var reloaded = new PresetService(_dir);
        Assert.True(reloaded.TryGet("甲", out var persisted));
        Assert.Equal(7700.0, persisted.DiskTemp, 6);
    }

    [Fact]
    public void SaveOver_DoesNotCreateANewPreset()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });

        Assert.False(service.TrySaveOver("不存在", new SettingsModel()), "只覆盖，不新建");
        Assert.False(service.TrySaveOver(LookPresets.Gargantua, new SettingsModel()), "内置不允许覆盖");

        Assert.Single(service.Custom);
        Assert.True(service.TryGet("甲", out var kept));
        Assert.Equal(5100.0, kept.DiskTemp, 6);
    }

    [Fact]
    public void DeleteCustom_RemovesOnlyCustom()
    {
        var service = Create();
        service.TrySave("临时", new SettingsModel());

        Assert.True(service.TryDelete("临时"));
        Assert.False(service.TryGet("临时", out _));
        Assert.False(service.TryDelete(LookPresets.Quasar));
        Assert.True(service.TryGet(LookPresets.Quasar, out _));
    }

    // ---------- v4（V4-3）：导出 / 导入 ----------

    private string TempFile(string suffix = ".json")
        => Path.Combine(_dir, Guid.NewGuid().ToString("N") + suffix);

    /// <summary>导出再导入回来，名字与数值都必须一致。</summary>
    [Fact]
    public void ExportImport_RoundTripKeepsNameAndValues()
    {
        var source = Create();
        source.TrySave("我的暗色", new SettingsModel { DiskTemp = 3300, HoleSize = 0.11, DiskOuter = 13.0 });

        string path = TempFile();
        Assert.True(source.TryExport(new[] { "我的暗色" }, path));

        var target = new PresetService(Path.Combine(Path.GetTempPath(), "bhc-presets", Guid.NewGuid().ToString("N")));
        Assert.True(target.TryParsePresetFile(path, out var parsed, out string parseError), parseError);
        var result = target.ApplyImport(parsed, ConflictPolicy.Overwrite);

        Assert.Equal(1, result.Added);
        Assert.True(target.TryGet("我的暗色", out var imported));
        Assert.Equal(3300.0, imported.DiskTemp, 6);
        Assert.Equal(0.11, imported.HoleSize, 6);
        Assert.Equal(13.0, imported.DiskOuter, 6);
    }

    [Fact]
    public void Import_Overwrite_ReplacesTheExisting()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });

        var incoming = new[] { PresetLibrary.Create("甲", new SettingsModel { DiskTemp = 9100 }) };
        var result = service.ApplyImport(incoming, ConflictPolicy.Overwrite);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        Assert.Single(service.Custom);
        Assert.True(service.TryGet("甲", out var updated));
        Assert.Equal(9100.0, updated.DiskTemp, 6);
    }

    /// <summary>另存为：原预设必须原样保留，来的那份换个名字存。</summary>
    [Fact]
    public void Import_KeepBoth_RenamesAndKeepsTheOriginal()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });

        var incoming = new[] { PresetLibrary.Create("甲", new SettingsModel { DiskTemp = 9100 }) };
        var result = service.ApplyImport(incoming, ConflictPolicy.KeepBoth);

        Assert.Equal(1, result.Renamed);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, service.Custom.Count);

        Assert.True(service.TryGet("甲", out var original));
        Assert.Equal(5100.0, original.DiskTemp, 6);                 // 原预设没被动过
        Assert.True(service.TryGet("甲 (2)", out var copy));
        Assert.Equal(9100.0, copy.DiskTemp, 6);
    }

    /// <summary>内置名出现在导入文件里：跳过，且不报错、不污染库。</summary>
    [Fact]
    public void Import_SkipsBuiltInNames()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());

        var incoming = new[]
        {
            PresetLibrary.Create(LookPresets.Gargantua, new SettingsModel { DiskTemp = 9999 }),
            PresetLibrary.Create("乙", new SettingsModel { DiskTemp = 4400 }),
        };

        var result = service.ApplyImport(incoming, ConflictPolicy.Overwrite);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Added);
        Assert.True(service.TryGet("乙", out var added));
        Assert.Equal(4400.0, added.DiskTemp, 6);
        Assert.DoesNotContain(service.Custom, p => p.Name == LookPresets.Gargantua);
    }

    /// <summary>文件损坏：返回 error，且现有 presets.json **一个字节都不能变**。</summary>
    [Fact]
    public void ParsePresetFile_RejectsCorruptFile_AndLeavesStoreUntouched()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });
        string storeBefore = File.ReadAllText(service.FilePath);

        string path = TempFile();
        File.WriteAllText(path, "{ 这不是 json ");

        Assert.False(service.TryParsePresetFile(path, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error), "失败时要给得出原因");

        Assert.Equal(storeBefore, File.ReadAllText(service.FilePath));
        Assert.Single(service.Custom);
    }

    /// <summary>版本不符：明确拒绝，不能拿旧格式硬解析出错值。</summary>
    [Fact]
    public void ParsePresetFile_RejectsWrongVersion()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());
        string storeBefore = File.ReadAllText(service.FilePath);

        string path = TempFile();
        File.WriteAllText(path, "{\"version\": 99, \"presets\": [{\"name\": \"甲\", \"appearance\": {}}]}");

        Assert.False(service.TryParsePresetFile(path, out _, out string error));
        Assert.Contains("版本", error);
        Assert.Equal(storeBefore, File.ReadAllText(service.FilePath));
    }

    /// <summary>缺字段走默认、多出的未知字段忽略（风险 R1 的另一半）。</summary>
    [Fact]
    public void Import_ToleratesMissingAndUnknownFields()
    {
        var service = Create();
        string path = TempFile();

        // appearance 只给一个字段，外加一个程序不认识的字段
        File.WriteAllText(
            path,
            "{\"Version\":1,\"Presets\":[{\"Name\":\"甲\",\"Appearance\":{\"DiskTemp\":6000}," +
            "\"IsBuiltIn\":false,\"UnknownThing\":123}]}");

        Assert.True(service.TryParsePresetFile(path, out var parsed, out string error), error);
        var result = service.ApplyImport(parsed, ConflictPolicy.Overwrite);

        Assert.Equal(1, result.Added);
        Assert.True(service.TryGet("甲", out var imported));
        Assert.Equal(6000.0, imported.DiskTemp, 6);

        // 没给的字段应当是 SettingsModel 的默认值（不是 0 / NaN）
        Assert.True(imported.HoleSize > 0);
    }

    /// <summary>导出空列表 / 不存在的名字：应当失败，而不是写出一个空文件。</summary>
    [Fact]
    public void Export_RejectsUnknownNames()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());

        string path = TempFile();
        Assert.False(service.TryExport(new[] { "不存在" }, path));
        Assert.False(service.TryExport(Array.Empty<string>(), path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteCustom_PersistsToDiskImmediately()
    {
        var service = Create();
        service.TrySave("待删除", new SettingsModel { DiskTemp = 9500 });

        Assert.True(service.TryDelete("待删除"));

        // 重新从磁盘加载，确认删除已落盘
        var reloaded = new PresetService(_dir);
        Assert.False(reloaded.TryGet("待删除", out _));
        Assert.Empty(reloaded.Custom);
    }

    [Fact]
    public void DeleteCustom_KeepsTheOtherPresets()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel { DiskTemp = 5100 });
        service.TrySave("乙", new SettingsModel { DiskTemp = 5200 });

        Assert.True(service.TryDelete("甲"));

        Assert.False(service.TryGet("甲", out _));
        Assert.True(service.TryGet("乙", out var kept));
        Assert.Equal(5200.0, kept.DiskTemp, 6);
    }

    [Fact]
    public void Persist_LeavesNoTempFileBehind()
    {
        var service = Create();
        service.TrySave("临时", new SettingsModel());

        string path = Path.Combine(_dir, "presets.json");

        Assert.True(File.Exists(path), "原子落盘后正式文件必须存在");
        Assert.False(File.Exists(path + ".tmp"), "不应残留临时文件");
    }

    [Fact]
    public void DeleteBuiltIn_IsRejected()
    {
        var service = Create();

        foreach (string name in PresetLibrary.BuiltInNames)
        {
            Assert.False(service.TryDelete(name), $"内置预设 {name} 不允许删除");
            Assert.True(service.TryGet(name, out _));
        }
    }

    [Fact]
    public void CustomPresets_AreListedAfterBuiltIns()
    {
        var service = Create();
        service.TrySave("甲", new SettingsModel());
        service.TrySave("乙", new SettingsModel());

        var all = service.All;
        Assert.Equal(10, all.Count);
        Assert.Equal(8, all.Count(p => p.IsBuiltIn));
    }

    [Fact]
    public void CorruptPresetFile_IsIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "presets.json"), "not json at all");

        var service = Create();

        Assert.True(service.IsCorrupt);
        Assert.Empty(service.Custom);
        Assert.True(service.TryGet(LookPresets.Zen, out _));
    }

    /// <summary>
    /// M5.2：应用预设走**增量补丁**（<see cref="SettingsPreset.ApplyTo"/>），
    /// 不再是整体 CopyFrom —— 大小、性能档位、染色都是用户当前的选择，不该被预设冲掉。
    /// </summary>
    [Fact]
    public void ApplyPatch_KeepsSizeQualityAndTint()
    {
        var target = new SettingsModel
        {
            HoleSize = 0.11,
            QualitySteps = QualitySteps.High,
            TintColor = Color.FromArgb(255, 1, 2, 3),
            TintStrength = 0.7,
        };

        var preset = PresetLibrary.Create(
            LookPresets.Inferno,
            PresetLibrary.CreateBuiltIn(LookPresets.Inferno),
            isBuiltIn: true);

        preset.ApplyTo(target);

        Assert.Equal(0.11, target.HoleSize, 6);
        Assert.Equal(QualitySteps.High, target.QualitySteps);
        Assert.Equal(Color.FromArgb(255, 1, 2, 3), target.TintColor);
        Assert.Equal(0.7, target.TintStrength, 6);
    }

    /// <summary>补丁必须覆盖预设给出的**全部** 15 个观感参数（防「加了参数忘了写进补丁」）。</summary>
    [Fact]
    public void ApplyPatch_WritesEveryAppearanceField()
    {
        var target = new SettingsModel();
        var expected = PresetLibrary.CreateBuiltIn(LookPresets.Quasar);

        PresetLibrary
            .Create(LookPresets.Quasar, expected, isBuiltIn: true)
            .ApplyTo(target);

        Assert.Equal(expected.DiskTemp, target.DiskTemp, 6);
        Assert.Equal(expected.DiskIncl, target.DiskIncl, 6);
        Assert.Equal(expected.DiskRoll, target.DiskRoll, 6);
        Assert.Equal(expected.DiskInner, target.DiskInner, 6);
        Assert.Equal(expected.DiskOuter, target.DiskOuter, 6);
        Assert.Equal(expected.DiskOpacity, target.DiskOpacity, 6);
        Assert.Equal(expected.DopplerMix, target.DopplerMix, 6);
        Assert.Equal(expected.BeamExponent, target.BeamExponent, 6);
        Assert.Equal(expected.DiskGain, target.DiskGain, 6);
        Assert.Equal(expected.DiskContrast, target.DiskContrast, 6);
        Assert.Equal(expected.DiskWinding, target.DiskWinding, 6);
        Assert.Equal(expected.DiskSpeed, target.DiskSpeed, 6);
        Assert.Equal(expected.Exposure, target.Exposure, 6);
        Assert.Equal(expected.StarGain, target.StarGain, 6);
        Assert.Equal(expected.Spin, target.Spin, 6);
    }

    [Fact]
    public void ApplyPatch_DoesNotTouchSystemBehaviour()
    {
        var defaults = new SettingsModel();
        var target = new SettingsModel
        {
            Enabled = false,
            FollowEnabled = true,
            IdleWanderEnabled = true,
            TransitionEnabled = false,
            TargetFrameRate = 165,
        };

        PresetLibrary
            .Create(LookPresets.Zen, PresetLibrary.CreateBuiltIn(LookPresets.Zen), isBuiltIn: true)
            .ApplyTo(target);

        Assert.False(target.Enabled);
        Assert.True(target.FollowEnabled);
        Assert.True(target.IdleWanderEnabled);
        Assert.False(target.TransitionEnabled);
        Assert.Equal(165.0, target.TargetFrameRate, 6);
        Assert.Equal(defaults.HideSystemCursor, target.HideSystemCursor);
        Assert.Equal(defaults.AutoFallback, target.AutoFallback);
    }

    [Fact]
    public void PresetApplication_DoesNotTouchAutoStart()
    {
        var target = new SettingsModel { AutoStart = true };
        var preset = PresetLibrary.CreateBuiltIn(LookPresets.Gargantua);
        preset.AutoStart = false;

        target.CopyFrom(preset);

        Assert.True(target.AutoStart);
    }

    [Fact]
    public void LoadedCustomPreset_IsClamped()
    {
        File.WriteAllText(
            Path.Combine(_dir, "presets.json"),
            """
            [{"Name":"越界","Appearance":{"DiskTemp":99999,"Exposure":-5}}]
            """);

        var service = Create();

        Assert.True(service.TryGet("越界", out var settings));
        Assert.Equal(SettingsRanges.DiskTempMax, settings.DiskTemp);
        Assert.Equal(SettingsRanges.ExposureMin, settings.Exposure);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结果
        }
    }
}
