using System.Drawing;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public class SettingsModelTests
{
    [Fact]
    public void Defaults_MatchPrdSpecification()
    {
        var s = new SettingsModel();

        Assert.True(s.Enabled);
        Assert.True(s.HideSystemCursor);
        Assert.True(s.AutoFallback);

        // v2 外观默认值 = Gargantua 预设（prd-v2 §6.1），详细逐项断言见 SettingsV2Tests
        Assert.Equal(0.06, s.HoleSize);
        Assert.Equal(4500.0, s.DiskTemp);
        Assert.Equal(7.0, s.DiskOuter);
        Assert.Equal(5.0, s.DiskSpeed);

        Assert.True(s.LensEnabled);

        // F5：默认关闭，保证鼠标精确跟手
        Assert.False(s.FollowEnabled);
        Assert.Equal(60.0, s.FollowStiffness);
        Assert.Equal(12.0, s.FollowDamping);
        Assert.Equal(2500.0, s.FollowMaxSpeed);
        Assert.Equal(60000.0, s.FollowMaxAccel);
        Assert.Equal(1.5, s.FollowSnapDistance);
        Assert.False(s.FollowAdaptiveSpeed);

        Assert.False(s.AutoStart);
    }

    [Theory]
    [InlineData(100.0, 1500.0)]
    [InlineData(99999.0, 20000.0)]
    [InlineData(-1.0, 1500.0)]
    public void Clamp_DiskTemp_IsBounded(double input, double expected)
    {
        var s = new SettingsModel { DiskTemp = input };
        Assert.True(s.Clamp());
        Assert.Equal(expected, s.DiskTemp);
    }

    [Fact]
    public void Clamp_AllNumericFields_AreBounded()
    {
        var s = new SettingsModel
        {
            DiskTemp = 99999,
            DiskIncl = -3,
            DiskRoll = 99,
            DiskInner = 0.1,
            DiskOuter = 500,
            DiskOpacity = -5,
            Exposure = 1e9,
            StarGain = 42,
            Spin = -7,
            FollowStiffness = 0,
            FollowDamping = 1e9,
            FollowMaxSpeed = -1,
            FollowMaxAccel = 0,
            FollowSnapDistance = 100,
        };

        Assert.True(s.Clamp());
        Assert.InRange(s.DiskTemp, SettingsRanges.DiskTempMin, SettingsRanges.DiskTempMax);
        Assert.InRange(s.DiskIncl, SettingsRanges.DiskInclMin, SettingsRanges.DiskInclMax);
        Assert.InRange(s.DiskRoll, SettingsRanges.DiskRollMin, SettingsRanges.DiskRollMax);
        Assert.InRange(s.DiskInner, SettingsRanges.DiskInnerMin, SettingsRanges.DiskInnerMax);
        Assert.InRange(s.DiskOuter, SettingsRanges.DiskOuterMin, SettingsRanges.DiskOuterMax);
        Assert.InRange(s.DiskOpacity, SettingsRanges.DiskOpacityMin, SettingsRanges.DiskOpacityMax);
        Assert.InRange(s.Exposure, SettingsRanges.ExposureMin, SettingsRanges.ExposureMax);
        Assert.InRange(s.StarGain, SettingsRanges.StarGainMin, SettingsRanges.StarGainMax);
        Assert.InRange(s.Spin, SettingsRanges.SpinMin, SettingsRanges.SpinMax);
        Assert.InRange(s.FollowStiffness, SettingsRanges.FollowStiffnessMin, SettingsRanges.FollowStiffnessMax);
        Assert.InRange(s.FollowDamping, SettingsRanges.FollowDampingMin, SettingsRanges.FollowDampingMax);
        Assert.InRange(s.FollowMaxSpeed, SettingsRanges.FollowMaxSpeedMin, SettingsRanges.FollowMaxSpeedMax);
        Assert.InRange(s.FollowMaxAccel, SettingsRanges.FollowMaxAccelMin, SettingsRanges.FollowMaxAccelMax);
        Assert.InRange(s.FollowSnapDistance, SettingsRanges.FollowSnapDistanceMin, SettingsRanges.FollowSnapDistanceMax);
    }

    [Fact]
    public void Clamp_NanAndInfinity_AreRepaired()
    {
        var s = new SettingsModel { DiskTemp = double.NaN, FollowDamping = double.PositiveInfinity };

        Assert.True(s.Clamp());
        Assert.False(double.IsNaN(s.DiskTemp));
        Assert.False(double.IsInfinity(s.FollowDamping));
        Assert.InRange(s.DiskTemp, SettingsRanges.DiskTempMin, SettingsRanges.DiskTempMax);
    }

    [Fact]
    public void Clamp_ValidValues_DoesNotReportChange()
    {
        var s = new SettingsModel();
        Assert.False(s.Clamp());
    }

    [Fact]
    public void Clamp_RaisesPropertyChanged()
    {
        var s = new SettingsModel { DiskTemp = 100000 };
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.Clamp();

        Assert.Contains(nameof(SettingsModel.DiskTemp), changed);
    }

    [Fact]
    public void CopyFrom_CopiesAllButAutoStart()
    {
        var source = new SettingsModel
        {
            HoleSize = 0.12,
            TintColor = Color.FromArgb(1, 2, 3, 4),
            FollowEnabled = true,
            AutoStart = true,
        };
        var target = new SettingsModel { AutoStart = false };

        target.CopyFrom(source);

        Assert.Equal(0.12, target.HoleSize);
        Assert.Equal(Color.FromArgb(1, 2, 3, 4), target.TintColor);
        Assert.True(target.FollowEnabled);
        Assert.False(target.AutoStart, "AutoStart 属于程序设置，不应被外观预设覆盖");
    }

    [Fact]
    public void Clone_IsIndependentSnapshot()
    {
        var s = new SettingsModel { DiskTemp = 3000 };
        var copy = s.Clone();

        copy.DiskTemp = 9000;

        Assert.Equal(3000.0, s.DiskTemp);
        Assert.Equal(9000.0, copy.DiskTemp);
    }

    [Fact]
    public void PropertyChanged_IsRaisedOnlyOnRealChange()
    {
        var s = new SettingsModel();
        int count = 0;
        s.PropertyChanged += (_, _) => count++;

        s.DiskTemp = 6000;
        s.DiskTemp = 6000;

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("#FFFF6A1A", 255, 255, 106, 26)]
    [InlineData("FF6A1A", 255, 255, 106, 26)]
    [InlineData(null, 0, 0, 0, 0)]
    [InlineData("garbage", 0, 0, 0, 0)]
    public void ColorUtilities_ParseHex(string? hex, int a, int r, int g, int b)
    {
        var c = ColorUtilities.FromHex(hex);

        Assert.Equal(a, c.A);
        Assert.Equal(r, c.R);
        Assert.Equal(g, c.G);
        Assert.Equal(b, c.B);
    }

    [Fact]
    public void ColorUtilities_RoundTrip()
    {
        var c = Color.FromArgb(200, 12, 34, 56);
        var parsed = ColorUtilities.FromHex(ColorUtilities.ToHex(c));

        Assert.Equal(c, parsed);
    }
}
