using System.Drawing;
using System.Text.Json;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M2.1：v2 参数体系落在 <see cref="SettingsModel"/> 上的行为
/// （默认值、钳制、序列化往返、以及到 <see cref="BlackHoleLook"/> 的映射）。
/// </summary>
public class SettingsV2Tests
{
    [Fact]
    public void Defaults_MatchGargantuaPreset()
    {
        var settings = new SettingsModel();

        Assert.Equal(0.06, settings.HoleSize);
        Assert.Equal(4500.0, settings.DiskTemp);
        Assert.Equal(1.52, settings.DiskIncl);
        Assert.Equal(0.10, settings.DiskRoll);
        Assert.Equal(2.2, settings.DiskInner);
        Assert.Equal(7.0, settings.DiskOuter);
        Assert.Equal(0.85, settings.DiskOpacity);
        Assert.Equal(0.35, settings.DopplerMix);
        Assert.Equal(2.0, settings.BeamExponent);
        Assert.Equal(1.4, settings.DiskGain);
        Assert.Equal(0.5, settings.DiskContrast);
        Assert.Equal(7.0, settings.DiskWinding);
        Assert.Equal(5.0, settings.DiskSpeed);
        Assert.Equal(1.20, settings.Exposure);
        Assert.Equal(0.0, settings.StarGain);
        Assert.Equal(0.0, settings.Spin);
        Assert.Equal(QualitySteps.Auto, settings.QualitySteps);
    }

    [Fact]
    public void Defaults_ProduceTheGargantuaLook()
    {
        var look = LookPresets.FromSettings(new SettingsModel());
        var gargantua = LookPresets.Create(LookPresets.Gargantua);

        Assert.Equal(gargantua.TempKelvin, look.TempKelvin, 6);
        Assert.Equal(gargantua.Inclination, look.Inclination, 6);
        Assert.Equal(gargantua.Roll, look.Roll, 6);
        Assert.Equal(gargantua.InnerRadius, look.InnerRadius, 6);
        Assert.Equal(gargantua.OuterRadius, look.OuterRadius, 6);
        Assert.Equal(gargantua.Opacity, look.Opacity, 6);
        Assert.Equal(gargantua.DopplerMix, look.DopplerMix, 6);
        Assert.Equal(gargantua.BeamExponent, look.BeamExponent, 6);
        Assert.Equal(gargantua.Gain, look.Gain, 6);
        Assert.Equal(gargantua.Contrast, look.Contrast, 6);
        Assert.Equal(gargantua.Winding, look.Winding, 6);
        Assert.Equal(gargantua.Speed, look.Speed, 6);
        Assert.Equal(gargantua.Exposure, look.Exposure, 6);
    }

    [Theory]
    [InlineData(0.001, 0.02)]
    [InlineData(0.9, 0.15)]
    public void HoleSize_IsClampedToValidRange(double input, double expected)
    {
        var settings = new SettingsModel { HoleSize = input };

        settings.Clamp();

        Assert.Equal(expected, settings.HoleSize, 6);
    }

    [Fact]
    public void DiskOuter_IsForcedLargerThanInner()
    {
        var settings = new SettingsModel { DiskInner = 5.0, DiskOuter = 4.0 };

        settings.Clamp();

        Assert.True(settings.DiskOuter > settings.DiskInner,
            $"外径 {settings.DiskOuter} 必须大于内径 {settings.DiskInner}");
    }

    [Fact]
    public void Spin_IsClampedBelowExtremal()
    {
        var settings = new SettingsModel { Spin = 5.0 };

        settings.Clamp();

        Assert.Equal(SettingsRanges.SpinMax, settings.Spin, 6);
    }

    [Fact]
    public void UndefinedQualitySteps_FallsBackToAuto()
    {
        var settings = new SettingsModel { QualitySteps = (QualitySteps)999 };

        settings.Clamp();

        Assert.Equal(QualitySteps.Auto, settings.QualitySteps);
    }

    [Theory]
    [InlineData(QualitySteps.Auto, 48)]
    [InlineData(QualitySteps.Low, 24)]
    [InlineData(QualitySteps.Medium, 48)]
    [InlineData(QualitySteps.High, 88)]
    public void EffectiveStepCount_ResolvesQualitySteps(QualitySteps steps, int expected)
    {
        var settings = new SettingsModel { QualitySteps = steps };

        Assert.Equal(expected, settings.EffectiveStepCount);
    }

    [Fact]
    public void FromSettings_AppliesStepCountAndClamps()
    {
        var settings = new SettingsModel { QualitySteps = QualitySteps.High, DiskTemp = 99999, Spin = 3 };

        var look = LookPresets.FromSettings(settings);

        Assert.Equal(88, look.StepCount);
        Assert.Equal(BlackHoleLook.TempMax, look.TempKelvin, 6);
        Assert.Equal(BlackHoleLook.SpinMax, look.Spin, 6);
    }

    [Fact]
    public void FromSettings_CarriesTint()
    {
        var settings = new SettingsModel
        {
            TintColor = Color.FromArgb(0xFF, 0x40, 0x80, 0xC0),
            TintStrength = 0.75,
        };

        var look = LookPresets.FromSettings(settings);

        Assert.Equal(settings.TintColor, look.Tint);
        Assert.Equal(0.75, look.TintStrength, 6);
    }

    [Fact]
    public void CopyFrom_CarriesAllV2Values()
    {
        var source = new SettingsModel
        {
            HoleSize = 0.11,
            DiskTemp = 12345,
            DiskIncl = 0.4,
            DiskRoll = -1.2,
            DiskInner = 3.5,
            DiskOuter = 12.0,
            DiskOpacity = 0.2,
            DopplerMix = 0.9,
            BeamExponent = 5.0,
            DiskGain = 3.0,
            DiskContrast = 1.5,
            DiskWinding = 10.0,
            DiskSpeed = -4.0,
            Exposure = 2.0,
            StarGain = 0.5,
            Spin = 0.77,
            QualitySteps = QualitySteps.High,
        };

        var target = new SettingsModel();
        target.CopyFrom(source);

        Assert.Equal(0.11, target.HoleSize, 6);
        Assert.Equal(12345, target.DiskTemp, 6);
        Assert.Equal(0.4, target.DiskIncl, 6);
        Assert.Equal(-1.2, target.DiskRoll, 6);
        Assert.Equal(3.5, target.DiskInner, 6);
        Assert.Equal(12.0, target.DiskOuter, 6);
        Assert.Equal(0.2, target.DiskOpacity, 6);
        Assert.Equal(0.9, target.DopplerMix, 6);
        Assert.Equal(5.0, target.BeamExponent, 6);
        Assert.Equal(3.0, target.DiskGain, 6);
        Assert.Equal(1.5, target.DiskContrast, 6);
        Assert.Equal(10.0, target.DiskWinding, 6);
        Assert.Equal(-4.0, target.DiskSpeed, 6);
        Assert.Equal(2.0, target.Exposure, 6);
        Assert.Equal(0.5, target.StarGain, 6);
        Assert.Equal(0.77, target.Spin, 6);
        Assert.Equal(QualitySteps.High, target.QualitySteps);
    }

    /// <summary>走真实的 SettingsService 落盘再读回，同时覆盖 Color 的自定义序列化。</summary>
    [Fact]
    public void SaveAndLoad_RoundTripsAllV2Values()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bhc-v2-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var saving = new SettingsService(dir))
            {
                saving.Settings.HoleSize = 0.09;
                saving.Settings.DiskTemp = 15000;
                saving.Settings.TintColor = Color.FromArgb(0xFF, 0x11, 0x22, 0x33);
                saving.Settings.TintStrength = 0.4;
                saving.Settings.Spin = 0.65;
                saving.Settings.QualitySteps = QualitySteps.Low;
                saving.SaveNow();
            }

            using var loading = new SettingsService(dir);
            loading.Load();

            Assert.Equal(0.09, loading.Settings.HoleSize, 6);
            Assert.Equal(15000, loading.Settings.DiskTemp, 6);
            Assert.Equal(Color.FromArgb(0xFF, 0x11, 0x22, 0x33), loading.Settings.TintColor);
            Assert.Equal(0.4, loading.Settings.TintStrength, 6);
            Assert.Equal(0.65, loading.Settings.Spin, 6);
            Assert.Equal(QualitySteps.Low, loading.Settings.QualitySteps);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结果
            }
        }
    }

    /// <summary>
    /// 派生量（算出来的只读值）不能写进配置文件：手改它们没有任何效果，
    /// 留在文件里只会让人误以为「改了能生效」。
    /// </summary>
    [Fact]
    public void DerivedValues_AreNotSerialized()
    {
        string json = JsonSerializer.Serialize(new SettingsModel());

        Assert.DoesNotContain(nameof(SettingsModel.EffectiveStepCount), json, StringComparison.Ordinal);

        // M1.8.4：v1 参数已整体移除，新写出的配置里不该再残留任何 v1 字段
        Assert.DoesNotContain("HorizonRadius", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RingRadius", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LensStrength", json, StringComparison.Ordinal);

        // 真正可写的参数必须还在
        Assert.Contains(nameof(SettingsModel.HoleSize), json, StringComparison.Ordinal);
        Assert.Contains(nameof(SettingsModel.QualitySteps), json, StringComparison.Ordinal);
    }

    /// <summary>旧版配置文件没有 v2 字段，加载后必须回落到默认值而不是崩。</summary>
    [Fact]
    public void LegacyJson_WithoutV2Fields_UsesDefaults()
    {
        const string legacy = """
        {
          "Enabled": true,
          "HideSystemCursor": true,
          "HorizonRadius": 14.0,
          "RingRadius": 30.0
        }
        """;

        var restored = JsonSerializer.Deserialize<SettingsModel>(legacy);

        Assert.NotNull(restored);
        Assert.Equal(0.06, restored!.HoleSize, 6);
        Assert.Equal(4500.0, restored.DiskTemp, 6);
        Assert.Equal(QualitySteps.Auto, restored.QualitySteps);
    }
}
