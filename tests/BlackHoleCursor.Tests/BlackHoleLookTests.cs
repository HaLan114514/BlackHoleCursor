using System.Drawing;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

public class BlackHoleLookTests
{
    [Fact]
    public void Defaults_MatchGargantuaPreset()
    {
        var look = new BlackHoleLook();

        Assert.Equal(0.06, look.HoleSize);
        Assert.Equal(4500.0, look.TempKelvin);
        Assert.Equal(1.52, look.Inclination);
        Assert.Equal(0.10, look.Roll);
        Assert.Equal(2.2, look.InnerRadius);
        Assert.Equal(7.0, look.OuterRadius);
        Assert.Equal(0.85, look.Opacity);
        Assert.Equal(0.35, look.DopplerMix);
        Assert.Equal(2.0, look.BeamExponent);
        Assert.Equal(1.4, look.Gain);
        Assert.Equal(0.5, look.Contrast);
        Assert.Equal(7.0, look.Winding);
        Assert.Equal(5.0, look.Speed);
        Assert.Equal(1.20, look.Exposure);
        Assert.Equal(0.0, look.StarGain);
        Assert.Equal(0.0, look.Spin);
        Assert.Equal(48, look.StepCount);
    }

    [Fact]
    public void Defaults_AreAlreadyLegal()
    {
        Assert.False(new BlackHoleLook().Clamp());
    }

    [Fact]
    public void Clamp_BoundsEveryField()
    {
        var look = new BlackHoleLook
        {
            HoleSize = 5.0,
            TempKelvin = -1000,
            TintStrength = 9,
            Inclination = 99,
            Roll = 99,
            InnerRadius = 0.1,
            OuterRadius = 999,
            Opacity = -3,
            DopplerMix = 7,
            BeamExponent = 99,
            Gain = -1,
            Contrast = 99,
            Winding = 999,
            Speed = 999,
            Exposure = 0,
            StarGain = 5,
            Spin = 5,
            StepCount = 100000,
        };

        Assert.True(look.Clamp());
        Assert.InRange(look.HoleSize, BlackHoleLook.HoleSizeMin, BlackHoleLook.HoleSizeMax);
        Assert.InRange(look.TempKelvin, BlackHoleLook.TempMin, BlackHoleLook.TempMax);
        Assert.InRange(look.TintStrength, 0.0, 1.0);
        Assert.InRange(look.Inclination, BlackHoleLook.InclinationMin, BlackHoleLook.InclinationMax);
        Assert.InRange(look.Roll, BlackHoleLook.RollMin, BlackHoleLook.RollMax);
        Assert.InRange(look.InnerRadius, BlackHoleLook.InnerMin, BlackHoleLook.InnerMax);
        Assert.InRange(look.OuterRadius, BlackHoleLook.OuterMin, BlackHoleLook.OuterMax);
        Assert.InRange(look.Opacity, 0.0, 1.0);
        Assert.InRange(look.DopplerMix, 0.0, 1.0);
        Assert.InRange(look.BeamExponent, BlackHoleLook.BeamMin, BlackHoleLook.BeamMax);
        Assert.InRange(look.Gain, BlackHoleLook.GainMin, BlackHoleLook.GainMax);
        Assert.InRange(look.Contrast, BlackHoleLook.ContrastMin, BlackHoleLook.ContrastMax);
        Assert.InRange(look.Winding, BlackHoleLook.WindingMin, BlackHoleLook.WindingMax);
        Assert.InRange(look.Speed, BlackHoleLook.SpeedMin, BlackHoleLook.SpeedMax);
        Assert.InRange(look.Exposure, BlackHoleLook.ExposureMin, BlackHoleLook.ExposureMax);
        Assert.InRange(look.StarGain, BlackHoleLook.StarMin, BlackHoleLook.StarMax);
        Assert.InRange(look.Spin, BlackHoleLook.SpinMin, BlackHoleLook.SpinMax);
        Assert.InRange(look.StepCount, BlackHoleLook.StepsMin, BlackHoleLook.StepsMax);
    }

    [Fact]
    public void Clamp_KeepsOuterRadiusLargerThanInner()
    {
        var look = new BlackHoleLook { InnerRadius = 5.0, OuterRadius = 5.1 };

        look.Clamp();

        Assert.True(look.OuterRadius > look.InnerRadius, "盘外径必须大于内径，否则盘不存在");
    }

    [Fact]
    public void Clamp_RepairsNaN()
    {
        var look = new BlackHoleLook { TempKelvin = double.NaN, Spin = double.NaN };

        look.Clamp();

        Assert.False(double.IsNaN(look.TempKelvin));
        Assert.False(double.IsNaN(look.Spin));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var look = new BlackHoleLook();
        var copy = look.Clone();
        copy.TempKelvin = 12000;

        Assert.Equal(4500.0, look.TempKelvin);
        Assert.Equal(12000.0, copy.TempKelvin);
    }

    [Fact]
    public void Presets_HaveEightEntries()
    {
        Assert.Equal(8, LookPresets.Names.Count);
        Assert.Equal(LookPresets.Gargantua, LookPresets.DefaultName);
    }

    [Theory]
    [InlineData(LookPresets.Inferno, 5500.0, 1.50, 0.35, 1.8, 8.0, 0.90, 0.60, 2.5, 2.2, 1.6, 7.0, 5.0, 1.40, 0.0)]
    [InlineData(LookPresets.Gargantua, 4500.0, 1.52, 0.10, 2.2, 7.0, 0.85, 0.35, 2.0, 1.4, 0.5, 7.0, 5.0, 1.20, 0.0)]
    [InlineData(LookPresets.Quasar, 15000.0, 1.30, 0.35, 3.0, 14.0, 0.35, 1.00, 4.0, 1.2, 1.3, 8.0, 5.0, 0.80, 0.0)]
    [InlineData(LookPresets.M87Donut, 3800.0, 0.55, -0.30, 2.2, 6.0, 0.45, 0.90, 3.5, 1.6, 0.4, 3.0, 2.5, 1.10, 0.0)]
    [InlineData(LookPresets.Blazar, 18000.0, 1.05, 0.55, 3.0, 16.0, 0.30, 1.00, 5.0, 1.0, 1.5, 9.0, 6.0, 0.75, 0.0)]
    [InlineData(LookPresets.FaceOnEmber, 6500.0, 0.30, 0.00, 3.0, 10.0, 0.50, 0.80, 2.5, 1.0, 1.1, 7.0, 5.0, 1.00, 0.0)]
    [InlineData(LookPresets.PureLens, 5500.0, 1.50, 0.35, 1.8, 8.0, 0.00, 1.00, 2.5, 0.0, 1.6, 7.0, 5.0, 1.00, 0.6)]
    [InlineData(LookPresets.Zen, 7000.0, 1.45, 0.15, 3.5, 7.0, 0.40, 0.50, 2.0, 0.5, 0.3, 3.0, 1.5, 0.70, 0.0)]
    public void Presets_MatchTheReferenceValues(
        string name,
        double temp, double incl, double roll,
        double inner, double outer, double opac, double dopp,
        double beam, double gain, double contr, double wind,
        double speed, double expo, double star)
    {
        var look = LookPresets.Create(name);

        Assert.Equal(temp, look.TempKelvin, 3);
        Assert.Equal(incl, look.Inclination, 3);
        Assert.Equal(roll, look.Roll, 3);
        Assert.Equal(inner, look.InnerRadius, 3);
        Assert.Equal(outer, look.OuterRadius, 3);
        Assert.Equal(opac, look.Opacity, 3);
        Assert.Equal(dopp, look.DopplerMix, 3);
        Assert.Equal(beam, look.BeamExponent, 3);
        Assert.Equal(gain, look.Gain, 3);
        Assert.Equal(contr, look.Contrast, 3);
        Assert.Equal(wind, look.Winding, 3);
        Assert.Equal(speed, look.Speed, 3);
        Assert.Equal(expo, look.Exposure, 3);
        Assert.Equal(star, look.StarGain, 3);
    }

    [Fact]
    public void Presets_AllPassClamp()
    {
        foreach (string name in LookPresets.Names)
        {
            var look = LookPresets.Create(name);
            Assert.False(look.Clamp(), $"预设 {name} 的取值本身应当合法");
        }
    }

    [Fact]
    public void Presets_UnknownNameFallsBackToGargantua()
    {
        var look = LookPresets.Create("不存在的预设");

        Assert.Equal(4500.0, look.TempKelvin, 3);
    }

    [Fact]
    public void Presets_KeepDefaultHoleSize()
    {
        // 大小是用户独立的决策（默认 6%），预设不应覆盖它
        foreach (string name in LookPresets.Names)
        {
            Assert.Equal(0.06, LookPresets.Create(name).HoleSize);
        }
    }
}

public class GeodesicMathTests
{
    [Fact]
    public void CriticalImpactParameter_IsThreeRootThreeOverTwo()
    {
        Assert.Equal(2.5980762, LensMath.CriticalImpactParameter, 6);
        Assert.Equal(LensMath.CriticalImpactParameter, 3.0 * Math.Sqrt(3.0) / 2.0, 4);
    }

    [Fact]
    public void WorldScale_IsCriticalOverShadowRadius()
    {
        Assert.Equal(
            LensMath.CriticalImpactParameter / 0.06,
            LensMath.WorldScale(0.06),
            6);
    }

    [Fact]
    public void WorldScale_DecreasesAsTheHoleGrows()
    {
        double small = LensMath.WorldScale(0.02);
        double large = LensMath.WorldScale(0.15);

        Assert.True(small > large, "黑洞越大，同样的屏幕距离对应的撞击参数越小");
    }

    [Fact]
    public void WorldScale_NeverDividesByZero()
    {
        Assert.True(double.IsFinite(LensMath.WorldScale(0)));
        Assert.True(LensMath.WorldScale(0) > 0);
    }

    [Fact]
    public void ShadowRadiusPixels_ScalesWithScreenHeight()
    {
        Assert.Equal(86.4, LensMath.ShadowRadiusPixels(0.06, 1440), 3);
        Assert.Equal(64.8, LensMath.ShadowRadiusPixels(0.06, 1080), 3);
    }

    [Fact]
    public void CameraDistance_ContainsTheWholeDisk()
    {
        Assert.True(LensMath.CameraDistance(7) >= 7);
        Assert.True(LensMath.CameraDistance(20) > 20, "大盘需要更远的相机，否则会被裁掉");
        Assert.True(LensMath.CameraDistance(1) >= 14, "即使盘很小也有最小相机距离");
    }

    [Fact]
    public void MaxImpactParameter_IsBeyondTheDisk()
    {
        Assert.True(LensMath.MaxImpactParameter(7) > 7, "超过该值的光线不可能碰到盘");
    }

    [Fact]
    public void LensDepth_MatchesReference()
    {
        Assert.Equal(13.0, LensMath.LensDepth);
    }
}

public class SceneUniformV2Tests
{
    private static SceneDescription Create(BlackHoleLook? look = null, double screenHeight = 0)
        => new(256, 256, 128, 128, new BlackHoleCursor.Core.SettingsModel(), 0.0, false,
            Look: look, ScreenHeight: screenHeight);

    [Fact]
    public void HoleUniform_CarriesRadiusAndWorldScale()
    {
        var look = new BlackHoleLook { HoleSize = 0.09, StepCount = 24 };
        var c = SceneParamsBuilder.Build(Create(look));

        Assert.Equal(0.09f, c.Hole.X, 6);
        Assert.Equal((float)LensMath.WorldScale(0.09), c.Hole.Y, 3);
        Assert.Equal(24f, c.Hole.Z, 3);
    }

    [Fact]
    public void LookUniforms_CarryTheDiskParameters()
    {
        var look = new BlackHoleLook
        {
            TempKelvin = 9000,
            Inclination = 1.1,
            Roll = -0.4,
            TintStrength = 0.5,
            InnerRadius = 3.0,
            OuterRadius = 12.0,
            Opacity = 0.4,
            DopplerMix = 0.9,
            BeamExponent = 4.5,
            Gain = 2.5,
            Contrast = 1.2,
            Winding = 9,
            Speed = -3,
            Exposure = 0.9,
            StarGain = 0.7,
            Spin = 0.6,
        };
        var c = SceneParamsBuilder.Build(Create(look));

        Assert.Equal(9000f, c.Look0.X, 3);
        Assert.Equal(1.1f, c.Look0.Y, 3);
        Assert.Equal(-0.4f, c.Look0.Z, 3);
        Assert.Equal(0.5f, c.Look0.W, 3);

        Assert.Equal(3.0f, c.Look1.X, 3);
        Assert.Equal(12.0f, c.Look1.Y, 3);
        Assert.Equal(0.4f, c.Look1.Z, 3);
        Assert.Equal(0.9f, c.Look1.W, 3);

        Assert.Equal(4.5f, c.Look2.X, 3);
        Assert.Equal(2.5f, c.Look2.Y, 3);
        Assert.Equal(1.2f, c.Look2.Z, 3);
        Assert.Equal(9f, c.Look2.W, 3);

        Assert.Equal(-3f, c.Look3.X, 3);
        Assert.Equal(0.9f, c.Look3.Y, 3);
        Assert.Equal(0.7f, c.Look3.Z, 3);
        Assert.Equal(0.6f, c.Look3.W, 3);
    }

    [Fact]
    public void TintUniform_IsNormalized()
    {
        var look = new BlackHoleLook { Tint = Color.FromArgb(255, 128, 0, 255) };
        var c = SceneParamsBuilder.Build(Create(look));

        Assert.Equal(128f / 255f, c.Tint.X, 3);
        Assert.Equal(0f, c.Tint.Y, 3);
        Assert.Equal(1f, c.Tint.Z, 3);
    }

    [Fact]
    public void WithoutLook_UsesGargantuaDefaults()
    {
        var c = SceneParamsBuilder.Build(Create());

        Assert.Equal(0.06f, c.Hole.X, 6);
        Assert.Equal(4500f, c.Look0.X, 3);
        Assert.Equal(48f, c.Hole.Z, 3);
    }

    [Fact]
    public void FloatArray_MatchesTheShaderCbuffer()
    {
        var c = SceneParamsBuilder.Build(Create());

        // 13 行 × float4 = 52（fixes-v2 问题 6 追加 NebulaTint）；
        // 必须与 common.hlsl 的 cbuffer 行数一致 —— 这是按字节顺序的隐式契约
        Assert.Equal(52, c.ToFloatArray().Length);
        Assert.Equal(SceneConstants.FloatCount, c.ToFloatArray().Length);
        Assert.Equal(0, SceneConstants.FloatCount % 4);
    }

    /// <summary>
    /// M2.3 之后：<c>HoleSize</c> 是「占**屏幕**高度的比例」，而着色器工作在**视口** uv 上，
    /// 所以传了 ScreenHeight 时 Hole.X 会被换算成「占视口高度的比例」（窗口越小占比越大）。
    /// 换算的专项测试见 <see cref="ScreenHeightMappingTests"/>。
    /// </summary>
    [Fact]
    public void ScreenHeight_ConvertsHoleSizeToViewportFraction()
    {
        var withScreen = SceneParamsBuilder.Build(Create(new BlackHoleLook(), screenHeight: 1440));
        var withoutScreen = SceneParamsBuilder.Build(Create(new BlackHoleLook()));

        // Create 的视口是 256，屏高 1440 → 0.06 * 1440 / 256
        Assert.Equal(0.06f * 1440f / 256f, withScreen.Hole.X, 5);

        // 没传屏高时退化为「视口高度即屏高」，保持旧行为，避免破坏既有调用方
        Assert.Equal(0.06f, withoutScreen.Hole.X, 6);
    }
}
