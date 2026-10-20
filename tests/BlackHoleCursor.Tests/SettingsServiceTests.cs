using System.Drawing;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bhc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private SettingsService CreateService(IAutoStartRegistry? registry = null, TimeSpan? debounce = null)
        => new(_dir, registry, debounce ?? TimeSpan.FromMilliseconds(50));

    [Fact]
    public void Load_WhenFileMissing_UsesDefaults()
    {
        using var svc = CreateService();

        svc.Load();

        Assert.False(File.Exists(svc.ConfigFilePath));
        Assert.Equal(0.06, svc.Settings.HoleSize);
        Assert.Equal(4500.0, svc.Settings.DiskTemp);
        Assert.False(svc.RecoveredFromCorruptFile);
    }

    [Fact]
    public void SaveNow_ThenLoad_RoundTripsAllValues()
    {
        var registry = new FakeAutoStartRegistry();
        using (var svc = CreateService(registry))
        {
            svc.Settings.HoleSize = 0.11;
            svc.Settings.DiskTemp = 12345;
            svc.Settings.TintColor = Color.FromArgb(0xFF, 0x11, 0x22, 0x33);
            svc.Settings.TintStrength = 0.42;
            svc.Settings.DiskIncl = 0.47;
            svc.Settings.DiskRoll = -1.25;
            svc.Settings.DiskInner = 3.5;
            svc.Settings.DiskOuter = 11.0;
            svc.Settings.DiskOpacity = 0.55;
            svc.Settings.DiskSpeed = -2.5;
            svc.Settings.LensEnabled = false;
            svc.Settings.FollowEnabled = true;
            svc.Settings.FollowStiffness = 120;
            svc.Settings.FollowDamping = 20;
            svc.Settings.FollowMaxSpeed = 4321;
            svc.Settings.FollowMaxAccel = 77777;
            svc.Settings.FollowSnapDistance = 2.5;
            svc.Settings.FollowAdaptiveSpeed = true;
            svc.Settings.HideSystemCursor = false;
            svc.Settings.AutoFallback = false;
            svc.Settings.Enabled = false;
            svc.SaveNow();
        }

        using var reloaded = CreateService(registry);
        reloaded.Load();
        var s = reloaded.Settings;

        Assert.Equal(0.11, s.HoleSize, 6);
        Assert.Equal(12345.0, s.DiskTemp, 6);
        Assert.Equal(Color.FromArgb(0xFF, 0x11, 0x22, 0x33), s.TintColor);
        Assert.Equal(0.42, s.TintStrength, 6);
        Assert.Equal(0.47, s.DiskIncl, 6);
        Assert.Equal(-1.25, s.DiskRoll, 6);
        Assert.Equal(3.5, s.DiskInner, 6);
        Assert.Equal(11.0, s.DiskOuter, 6);
        Assert.Equal(0.55, s.DiskOpacity, 6);
        Assert.Equal(-2.5, s.DiskSpeed, 6);
        Assert.False(s.LensEnabled);
        Assert.True(s.FollowEnabled);
        Assert.Equal(120.0, s.FollowStiffness);
        Assert.Equal(20.0, s.FollowDamping);
        Assert.Equal(4321.0, s.FollowMaxSpeed);
        Assert.Equal(77777.0, s.FollowMaxAccel);
        Assert.Equal(2.5, s.FollowSnapDistance);
        Assert.True(s.FollowAdaptiveSpeed);
        Assert.False(s.HideSystemCursor);
        Assert.False(s.AutoFallback);
        Assert.False(s.Enabled);
    }

    [Fact]
    public void Load_WithCorruptFile_BacksUpAndFallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, SettingsService.ConfigFileName), "{ this is not json ]");

        using var svc = CreateService();
        svc.Load();

        Assert.True(svc.RecoveredFromCorruptFile);
        Assert.Equal(4500.0, svc.Settings.DiskTemp);
        string[] backups = Directory.GetFiles(_dir, "settings.corrupt-*.json");
        Assert.Single(backups);
    }

    [Fact]
    public void Load_WithOutOfRangeValues_ClampsThem()
    {
        File.WriteAllText(
            Path.Combine(_dir, SettingsService.ConfigFileName),
            """
            {
              "DiskTemp": 99999,
              "DiskOpacity": -3,
              "HoleSize": 0.9,
              "StarGain": 100000
            }
            """);

        using var svc = CreateService();
        svc.Load();

        Assert.Equal(SettingsRanges.DiskTempMax, svc.Settings.DiskTemp);
        Assert.Equal(SettingsRanges.DiskOpacityMin, svc.Settings.DiskOpacity);
        Assert.Equal(SettingsRanges.HoleSizeMax, svc.Settings.HoleSize);
        Assert.Equal(SettingsRanges.StarGainMax, svc.Settings.StarGain);
    }

    [Fact]
    public void PropertyChange_TriggersDebouncedSave()
    {
        using var svc = CreateService(debounce: TimeSpan.FromMilliseconds(400));

        svc.Settings.DiskTemp = 7777;
        Assert.False(File.Exists(svc.ConfigFilePath), "防抖窗口内不应写盘");

        svc.Flush();

        Assert.True(File.Exists(svc.ConfigFilePath));
        using var reloaded = CreateService();
        reloaded.Load();
        Assert.Equal(7777.0, reloaded.Settings.DiskTemp);
    }

    [Fact]
    public void SaveNow_IsAtomicAndLeavesNoTempFile()
    {
        using var svc = CreateService();
        svc.Settings.DiskTemp = 8000;

        svc.SaveNow();

        Assert.True(File.Exists(svc.ConfigFilePath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(1, svc.SaveCount);
    }

    [Fact]
    public void AutoStart_WhenEnabled_WritesRegistry()
    {
        var registry = new FakeAutoStartRegistry();
        using var svc = CreateService(registry);

        svc.Settings.AutoStart = true;
        svc.Flush();

        Assert.True(registry.IsEnabled(SettingsService.AppName));
    }

    [Fact]
    public void AutoStart_WhenDisabled_RemovesRegistryValue()
    {
        var registry = new FakeAutoStartRegistry();
        using var svc = CreateService(registry);

        svc.Settings.AutoStart = true;
        svc.Settings.AutoStart = false;
        svc.Flush();

        Assert.False(registry.IsEnabled(SettingsService.AppName));
    }

    [Fact]
    public void Load_SyncsAutoStartFromRegistry()
    {
        var registry = new FakeAutoStartRegistry();
        registry.SetEnabled(SettingsService.AppName, true, "dummy.exe");

        using var svc = CreateService(registry);
        svc.Load();

        Assert.True(svc.Settings.AutoStart);
    }

    [Fact]
    public void RegistryFailure_DoesNotThrow()
    {
        using var svc = CreateService(new ThrowingAutoStartRegistry());

        svc.Load();
        svc.Settings.AutoStart = true;
        svc.SaveNow();

        Assert.True(svc.Settings.AutoStart);
    }

    [Fact]
    public void ResetToDefaults_RestoresDefaultsAndPersists()
    {
        using var svc = CreateService();
        svc.Settings.DiskTemp = 16000;
        svc.SaveNow();

        svc.ResetToDefaults();

        Assert.Equal(4500.0, svc.Settings.DiskTemp);
        using var reloaded = CreateService();
        reloaded.Load();
        Assert.Equal(4500.0, reloaded.Settings.DiskTemp);
    }

    [Fact]
    public void Dispose_StopsPersistingChanges()
    {
        var svc = CreateService();
        svc.Dispose();

        svc.Settings.DiskTemp = 9000;
        svc.Flush();

        Assert.False(File.Exists(svc.ConfigFilePath));
    }

    // ---- 撤销：调坏参数后的退路 ----

    [Fact]
    public void Undo_WithoutAnyChange_ReturnsFalse()
    {
        using var svc = CreateService();
        svc.Load();

        Assert.False(svc.CanUndo);
        Assert.False(svc.Undo());
    }

    [Fact]
    public void Undo_RestoresValuesBeforeLastChange()
    {
        using var svc = CreateService();
        svc.Load();

        double original = svc.Settings.DiskTemp;
        svc.Settings.DiskTemp = 12000;
        svc.Settings.DiskGain = 2.5;

        Assert.True(svc.Undo());

        Assert.Equal(original, svc.Settings.DiskTemp);
        Assert.Equal(1.4, svc.Settings.DiskGain);
    }

    [Fact]
    public void CanUndo_BecomesTrueOnceAChangeIsPending()
    {
        using var svc = CreateService();
        svc.Load();
        Assert.False(svc.CanUndo);

        svc.Settings.Exposure = 2.2;

        Assert.True(svc.CanUndo);
    }

    [Fact]
    public void Undo_CanBeAppliedRepeatedlyUntilTheStart()
    {
        using var svc = CreateService();
        svc.Load();
        double original = svc.Settings.DiskTemp;

        svc.Settings.DiskTemp = 9000;
        Assert.True(svc.Undo());
        Assert.Equal(original, svc.Settings.DiskTemp);

        // 回到起点后没有更多可撤销的内容
        Assert.False(svc.Undo());
    }

    [Fact]
    public void Undo_RevertsResetToDefaults()
    {
        using var svc = CreateService();
        svc.Load();

        svc.Settings.DiskTemp = 19000;
        Assert.True(svc.Undo(), "先撤销回默认状态");

        svc.Settings.DiskTemp = 12000;
        svc.ResetToDefaults();
        Assert.Equal(4500.0, svc.Settings.DiskTemp);

        Assert.True(svc.Undo());
        Assert.Equal(12000, svc.Settings.DiskTemp);
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
