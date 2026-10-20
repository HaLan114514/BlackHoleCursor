using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M5.3（prd-v2 R3）：旧版（v1）配置的识别与迁移。
///
/// 关键点：v1 的字段名在 <see cref="SettingsModel"/> 上已经不存在，
/// <see cref="System.Text.Json"/> 会**静默忽略**它们 —— 用户的老配置会无声无息变成默认值。
/// 这一组负责保证「认出来 → 备份 → 回落默认 → 提示一次」这条链路始终有效。
/// </summary>
public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public SettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bhc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>一份典型的 v1.0 配置（字段取自 prd-v2 §F2-1 的移除清单）。</summary>
    private const string LegacyJson =
        """
        {
          "Enabled": true,
          "HorizonRadius": 14,
          "RingInnerColor": "#FFFF8800",
          "RingOuterColor": "#FF3355FF",
          "RingThickness": 3.5,
          "RingRadius": 33,
          "RingTilt": 1.2,
          "RingRoll": 0.4,
          "RingOpacity": 0.9,
          "RingSpinSpeed": 3.5,
          "RingDebrisEnabled": true,
          "RingDebrisCount": 40,
          "GlowIntensity": 1.2,
          "GlobalScale": 1.1,
          "LensStrength": 0.8,
          "LensRadius": 120,
          "GradientEnabled": true
        }
        """;

    private SettingsService CreateService(IAutoStartRegistry? registry = null)
        => new(_dir, registry ?? new FakeAutoStartRegistry(), TimeSpan.FromMilliseconds(50));

    private void WriteConfig(string json)
        => File.WriteAllText(Path.Combine(_dir, SettingsService.ConfigFileName), json);

    [Fact]
    public void DetectLegacyFields_FindsTheRetiredV1Fields()
    {
        var found = SettingsMigration.DetectLegacyFields(LegacyJson);

        Assert.Contains("HorizonRadius", found);
        Assert.Contains("RingSpinSpeed", found);
        Assert.Contains("LensRadius", found);
        Assert.Contains("GradientEnabled", found);
        Assert.Equal(16, found.Count);
    }

    [Fact]
    public void DetectLegacyFields_IgnoresCurrentFields()
    {
        using var svc = CreateService();
        svc.Settings.DiskTemp = 9000;
        svc.SaveNow();

        Assert.Empty(SettingsMigration.DetectLegacyFields(File.ReadAllText(svc.ConfigFilePath)));
    }

    /// <summary>手写或被别的工具改写过的文件未必保持 PascalCase。</summary>
    [Fact]
    public void DetectLegacyFields_IsCaseInsensitive()
    {
        var found = SettingsMigration.DetectLegacyFields("""{"horizonradius": 14, "ringroll": 0.2}""");

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void DetectLegacyFields_InvalidJson_ReturnsEmpty()
    {
        // 解析失败是「文件损坏」那条路径的事，这里不该抢着报错
        Assert.Empty(SettingsMigration.DetectLegacyFields("not json at all"));
        Assert.Empty(SettingsMigration.DetectLegacyFields(string.Empty));
        Assert.Empty(SettingsMigration.DetectLegacyFields("[1,2,3]"));
    }

    [Fact]
    public void Load_LegacyFile_FallsBackToDefaults()
    {
        WriteConfig(LegacyJson);

        using var svc = CreateService();
        svc.Load();

        var defaults = new SettingsModel();
        Assert.Equal(defaults.HoleSize, svc.Settings.HoleSize, 6);
        Assert.Equal(defaults.DiskTemp, svc.Settings.DiskTemp, 6);
        Assert.Equal(defaults.DiskIncl, svc.Settings.DiskIncl, 6);
        Assert.Equal(defaults.QualitySteps, svc.Settings.QualitySteps);
        Assert.False(svc.RecoveredFromCorruptFile, "旧版配置不是「损坏」，不该报损坏");
    }

    [Fact]
    public void Load_LegacyFile_BacksUpTheOriginalFile()
    {
        WriteConfig(LegacyJson);

        using var svc = CreateService();
        svc.Load();

        var notice = svc.TakeMigrationNotice();
        Assert.NotNull(notice);
        Assert.NotNull(notice!.BackupPath);
        Assert.True(File.Exists(notice.BackupPath), "旧文件必须留一份备份");
        Assert.Contains("HorizonRadius", File.ReadAllText(notice.BackupPath));
        Assert.False(File.Exists(svc.ConfigFilePath), "旧文件要挪走，否则每次启动都会再提示一遍");
    }

    [Fact]
    public void TheNoticeIsReportedOnlyOnce()
    {
        WriteConfig(LegacyJson);

        using var svc = CreateService();
        svc.Load();

        Assert.NotNull(svc.Migration);
        Assert.NotNull(svc.TakeMigrationNotice());
        // 提示过一次之后不能再提示
        Assert.Null(svc.TakeMigrationNotice());
        Assert.Null(svc.Migration);
    }

    [Fact]
    public void Load_CurrentFile_ReportsNoMigration()
    {
        using (var writer = CreateService())
        {
            writer.Settings.DiskTemp = 12000;
            writer.SaveNow();
        }

        using var svc = CreateService();
        svc.Load();

        Assert.Null(svc.Migration);
        Assert.Equal(12000.0, svc.Settings.DiskTemp, 6);
    }

    /// <summary>
    /// 只认**已知的 v1 字段**：将来多出来的陌生字段（新版本写、旧版本读）不该被当成旧配置，
    /// 否则一次普通的降级启动就会把用户的配置清空。
    /// </summary>
    [Fact]
    public void UnrelatedUnknownFields_AreIgnoredQuietly()
    {
        WriteConfig("""{"DiskTemp": 8000, "SomethingFromTheFuture": 42}""");

        using var svc = CreateService();
        svc.Load();

        Assert.Null(svc.Migration);
        Assert.Equal(8000.0, svc.Settings.DiskTemp, 6);
    }

    [Fact]
    public void MigrationNotice_MessageMentionsTheBackup()
    {
        WriteConfig(LegacyJson);

        using var svc = CreateService();
        svc.Load();

        var notice = svc.TakeMigrationNotice()!;

        Assert.Contains("v1", notice.Message);
        Assert.Contains(Path.GetFileName(notice.BackupPath!), notice.Message);
    }

    [Fact]
    public void Load_LegacyFile_KeepsAutoStartFromTheRegistry()
    {
        WriteConfig(LegacyJson);
        var registry = new FakeAutoStartRegistry();
        registry.SetEnabled(SettingsService.AppName, true, "dummy.exe");

        using var svc = CreateService(registry);
        svc.Load();

        Assert.True(svc.Settings.AutoStart);
    }

    /// <summary>迁移后重新落盘 → 文件已是新格式，再次加载不会再触发迁移（不会「提示个没完」）。</summary>
    [Fact]
    public void AfterMigration_SavingWritesTheCurrentFormat()
    {
        WriteConfig(LegacyJson);

        using (var svc = CreateService())
        {
            svc.Load();
            svc.Settings.DiskTemp = 7000;
            svc.SaveNow();
        }

        using var reloaded = CreateService();
        reloaded.Load();

        Assert.Null(reloaded.Migration);
        Assert.Equal(7000.0, reloaded.Settings.DiskTemp, 6);

        string json = File.ReadAllText(reloaded.ConfigFilePath);
        foreach (string legacy in LegacySettingsFields.Names)
        {
            Assert.DoesNotContain(legacy, json);
        }
    }

    /// <summary>损坏的文件仍然走「备份 + 回落默认」，与旧版识别互不干扰。</summary>
    [Fact]
    public void CorruptFile_IsStillHandledAsCorrupt()
    {
        WriteConfig("{ this is not json");

        using var svc = CreateService();
        svc.Load();

        Assert.True(svc.RecoveredFromCorruptFile);
        Assert.Null(svc.Migration);
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
