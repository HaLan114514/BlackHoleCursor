using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

// M1.8.3：原 LensMathTests（10 项）测的是 v1 的位移近似 / 光子环 / 渐隐窗口，
// 已随旧着色器删除。v2 留下的换算（WorldScale / ShadowRadiusPixels / CameraDistance /
// MaxImpactParameter / CriticalImpactParameter）由 BlackHoleLookTests 的「几何换算」一组覆盖。

public class SceneParamsBuilderTests
{
    private static SceneDescription Create(SettingsModel? settings = null, double width = 256, double height = 256)
        => new(width, height, width / 2.0, height / 2.0, settings ?? new SettingsModel(), 0.0, true);

    [Fact]
    public void Viewport_ExposesSizeAndInverse()
    {
        var c = SceneParamsBuilder.Build(Create(width: 200, height: 100));

        Assert.Equal(200f, c.Viewport.X);
        Assert.Equal(100f, c.Viewport.Y);
        Assert.Equal(1f / 200f, c.Viewport.Z, 6);
        Assert.Equal(1f / 100f, c.Viewport.W, 6);
    }

    [Fact]
    public void Timing_CarriesTimeAndLensFlag()
    {
        var settings = new SettingsModel { DiskSpeed = 5.0 };

        var enabled = SceneParamsBuilder.Build(new SceneDescription(256, 256, 128, 128, settings, 2.0, true));
        Assert.Equal(2.0f, enabled.Timing.X, 3);
        Assert.Equal(1f, enabled.Timing.W);

        var disabled = SceneParamsBuilder.Build(new SceneDescription(256, 256, 128, 128, settings, 2.0, false));
        Assert.Equal(0f, disabled.Timing.W);
    }

    /// <summary>盘的条纹速度为 0 时冻结时间：画面完全静止，渲染层可安全降帧。</summary>
    [Fact]
    public void ZeroDiskSpeed_FreezesTime()
    {
        var settings = new SettingsModel { DiskSpeed = 0.0 };
        var c = SceneParamsBuilder.Build(new SceneDescription(256, 256, 128, 128, settings, 99.0, true));

        Assert.Equal(0f, c.Timing.X);
    }

    [Fact]
    public void LensFlag_IsOffWhenSettingDisabled()
    {
        var settings = new SettingsModel { LensEnabled = false };
        var c = SceneParamsBuilder.Build(Create(settings));

        Assert.Equal(0f, c.Timing.W);
    }

    [Fact]
    public void RemoteCenter_IsPassedThrough()
    {
        var c = SceneParamsBuilder.Build(new SceneDescription(300, 300, 42, 84, new SettingsModel(), 0, false));

        Assert.Equal(42f, c.CenterRadius.X);
        Assert.Equal(84f, c.CenterRadius.Y);
    }

    [Fact]
    public void DegenerateSizes_DoNotProduceNaN()
    {
        var c = SceneParamsBuilder.Build(new SceneDescription(0, 0, 0, 0, new SettingsModel(), 0, true));

        Assert.False(float.IsNaN(c.Viewport.Z));
        Assert.False(float.IsNaN(c.Timing.X));
        Assert.False(float.IsNaN(c.Hole.X));
        Assert.False(float.IsNaN(c.Hole.Y));
    }

    [Fact]
    public void ToFloatArray_MatchesFloatCount()
    {
        var c = SceneParamsBuilder.Build(Create());

        Assert.Equal(SceneConstants.FloatCount, c.ToFloatArray().Length);
        Assert.Equal(0, SceneConstants.FloatCount % 4);
    }

    [Fact]
    public void DebugProbe_IsPassedToShader()
    {
        var probe = new ShaderProbe(TemperatureKelvin: 3000, NoiseScale: 12, AngularPeriod: 7, AngleRadians: 1.25);
        var c = SceneParamsBuilder.Build(new SceneDescription(
            64, 64, 32, 32, new SettingsModel(), 0, false, Probe: probe));

        Assert.Equal(3000f, c.Debug.X, 3);
        Assert.Equal(12f, c.Debug.Y, 3);
        Assert.Equal(7f, c.Debug.Z, 3);
        Assert.Equal(1.25f, c.Debug.W, 3);
    }

    [Fact]
    public void DebugProbe_DefaultsAreSafe()
    {
        var c = SceneParamsBuilder.Build(new SceneDescription(64, 64, 32, 32, new SettingsModel(), 0, false));

        Assert.True(c.Debug.Y > 0, "噪声缩放必须为正，否则除零");
        Assert.True(c.Debug.Z >= 1, "角向周期至少为 1，否则回绕无意义");
    }
}

public class OverlayGeometryTests
{
    // ⚠️ v3（P1.5）已删除：WindowIsCenteredOnTheBlackHole / ExtentIsClampedToMaximum /
    // TinyRadiusStillProducesUsableWindow / PositionIsQuantized。
    // 它们断言的都是 `ComputeWindowRect` 与 `Quantize` —— 窗口 v3 起是整块显示器、
    // 不再由洞径推算、也不再量化左上角，这两个函数连同 1024 上限一起删除了。

    /// <summary>
    /// v3（P1.2）：窗口固定为整块显示器后，黑洞的局部坐标就是
    /// 「跟随位置 − 窗口原点」，而不是窗口中心。
    /// </summary>
    [Fact]
    public void LocalCenterIsTheFollowPositionInWindowCoords()
    {
        var window = new OverlayGeometry.Rect(100, 60, 1920, 1080);
        var (x, y) = OverlayGeometry.LocalCenter(1000, 500, window);

        Assert.Equal(900.0, x, 9);
        Assert.Equal(440.0, y, 9);
        Assert.InRange(x, 0, window.Width);
        Assert.InRange(y, 0, window.Height);
    }

    /// <summary>
    /// v3（P1.2）**关键回归**：局部坐标必须**精确跟随光标的亚像素变化**。
    ///
    /// 注意这条的断言方向在问题 7 期间是**相反**的：当时为了压住折射区的抖，
    /// 局部坐标被强制固定在窗口中心（不许跟着光标动）。
    /// 窗口改成全屏且永不移动之后，抖的根源（整数像素的窗口位置）没有了，
    /// 于是坐标必须重新连续起来 —— 现在「不跟着动」反而是 bug。
    /// </summary>
    [Fact]
    public void LocalCenterFollowsTheCursorExactly()
    {
        var window = new OverlayGeometry.Rect(0, 0, 1920, 1080);
        var a = OverlayGeometry.LocalCenter(1000.0, 500.0, window);
        var b = OverlayGeometry.LocalCenter(1000.7, 500.4, window);
        var c = OverlayGeometry.LocalCenter(1001.3, 500.9, window);

        Assert.Equal(1000.0, a.X, 9);
        Assert.Equal(1000.7, b.X, 9);
        Assert.Equal(1001.3, c.X, 9);

        // 必须真的分辨出亚像素：0.7px 的位移不能塌缩成同一个值
        Assert.NotEqual(a.X, b.X);
        Assert.NotEqual(b.X, c.X);
    }

    /// <summary>
    /// v3（P1.2）：黑洞位置**精确，没有量化误差**。
    ///
    /// 旧的 <c>WindowCenterErrorIsBoundedByHalfQuantum</c> 在这里作废 ——
    /// 那条是在给「问题 7 修法的代价」定边界（量化误差转移到黑洞整体位置，须 ≤ quantum/2 + 半像素）。
    /// 窗口固定之后位置由连续浮点 uniform 决定，误差本来就是 0，不存在要设边界的代价。
    /// </summary>
    [Fact]
    public void HolePositionIsExact_NotQuantized()
    {
        var window = new OverlayGeometry.Rect(0, 0, 1920, 1080);

        foreach (double cursor in new[] { 1000.0, 1000.4, 1000.7, 1001.3, 1377.9 })
        {
            var (x, y) = OverlayGeometry.LocalCenter(cursor, cursor * 0.5, window);

            Assert.Equal(cursor, x, 9);
            Assert.Equal(cursor * 0.5, y, 9);
        }
    }

    /// <summary>
    /// v3（P1.3）：全黑帧检测的采样框必须以**黑洞**为中心，并换算到抓屏纹理坐标系。
    /// </summary>
    [Fact]
    public void BlackFrameSampleOrigin_IsCenteredOnTheHole()
    {
        const int size = 64;

        var (x, y) = OverlayGeometry.BlackFrameSampleOrigin(1000, 500, size, 0, 0);

        Assert.Equal(1000 - size / 2, x);
        Assert.Equal(500 - size / 2, y);
    }

    /// <summary>
    /// v3（P1.3）：抓屏纹理是整块显示器，所以采样原点要**减去显示器原点**。
    /// 副屏在左上（负坐标）时这条最容易错。
    /// </summary>
    [Fact]
    public void BlackFrameSampleOrigin_SubtractsTheMonitorOrigin()
    {
        const int size = 64;

        var (x, y) = OverlayGeometry.BlackFrameSampleOrigin(1000, 500, size, -1920, 120);

        Assert.Equal(1000 - size / 2 + 1920, x);
        Assert.Equal(500 - size / 2 - 120, y);
    }

    /// <summary>
    /// v3（P1.3）：黑洞移动时采样原点必须跟着走 ——
    /// 否则窗口全屏后采样点会一直钉在屏幕正中，检测的是别处。
    /// </summary>
    [Fact]
    public void BlackFrameSampleOrigin_FollowsTheHole()
    {
        const int size = 64;

        var a = OverlayGeometry.BlackFrameSampleOrigin(300, 300, size, 0, 0);
        var b = OverlayGeometry.BlackFrameSampleOrigin(1200, 800, size, 0, 0);

        Assert.NotEqual(a.X, b.X);
        Assert.NotEqual(a.Y, b.Y);
    }

    // ⚠️ v3（P1.5）已删除 Quantize_RoundsToQuantum（Quantize 随窗口定位一起删除）
    // 与 WindowRadius_CappedByOverlayLimit（1024 上限已取消；窗口不再用它算尺寸）。
    // `ComputeV2WindowRadius` 本身保留 —— 截图补偿的补丁边长还在用它（见 ScreenshotCompositor）。

    /// <summary>
    /// v3（P1.5）：`ComputeV2WindowRadius` 现在的唯一用途是**截图补偿的补丁边长**，
    /// 所以只需保证它「同时容纳盘外径与远场过渡」且不小于下限，不再有上界。
    /// </summary>
    [Fact]
    public void InfluenceRadius_CoversTheDiskAndFarField()
    {
        double shadow = 64.8;
        double disk = 900.0;

        double radius = OverlayGeometry.ComputeV2WindowRadius(shadow, disk);

        Assert.True(radius >= disk * OverlayGeometry.DiskMarginFactor, $"应容纳盘外径，实际 {radius:F1}");
        Assert.True(radius >= shadow * OverlayGeometry.FarFieldRadiusFactor, $"应容纳远场过渡，实际 {radius:F1}");

        // 放大系数必须真的生效（截图补偿按同比例放大补丁）
        Assert.True(OverlayGeometry.ComputeV2WindowRadius(shadow, disk, 2.0) > radius * 1.5);
    }

    /// <summary>
    /// 改 DiskRoll 只应影响 Look0.z（roll），不得污染其它常量 ——
    /// 这条用来挡住「改一个参数顺手带出一串」的隐性耦合。
    /// 布局见 SceneConstants：Look0 从第 24 个 float 起（x = 色温，y = 倾角，z = roll，w = 染色强度）。
    /// </summary>
    [Fact]
    public void DiskRoll_OnlyChangesTheRollConstant()
    {
        const int rollIndex = 26;   // Look0.z

        float[] flat = BuildConstants(new SettingsModel { DiskRoll = 0.0 }, 200);
        float[] rolled = BuildConstants(new SettingsModel { DiskRoll = 1.0 }, 200);

        Assert.Equal(SceneConstants.FloatCount, flat.Length);
        Assert.NotEqual(flat[rollIndex], rolled[rollIndex]);

        for (int i = 0; i < flat.Length; i++)
        {
            if (i == rollIndex)
            {
                continue;
            }

            Assert.Equal(flat[i], rolled[i], 5);
        }
    }

    /// <summary>
    /// 构造一帧常量。中心固定为 0，只有窗口尺寸（Viewport）随用例变化，
    /// 这样比较两组常量时不会把「中心坐标」误判成差异。
    /// </summary>
    private static float[] BuildConstants(SettingsModel settings, int extent)
    {
        var description = new SceneDescription(
            extent,
            extent,
            0.0,
            0.0,
            settings,
            0.0,
            true,
            Look: LookPresets.FromSettings(settings));   // 不传 Look 会拿到默认外观，v2 参数进不去

        return SceneParamsBuilder.Build(description).ToFloatArray();
    }
}
