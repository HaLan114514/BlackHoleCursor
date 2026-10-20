using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.4.1：Schwarzschild 测地线积分骨架的测试。
/// 用 GeodesicProbePS 渲染，断言「阴影是否为真阴影、大小是否正确、是否为圆」。
/// </summary>
public class GeodesicTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _background;

    public GeodesicTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _background = PreviewBackground.CreateView(_device.Device, Size, Size);
        _preview.SetBackground(_background);
    }

    /// <param name="withReprojection">
    /// false 时关闭反投影（Debug.x &lt; 0），只测「纯阴影」。
    /// 开启反投影后，紧贴阴影外侧弯折过度的光线会被淡出成黑色，
    /// 暗区会比真阴影更大 —— 所以量阴影尺寸必须关掉反投影。
    /// </param>
    private byte[] RenderGeodesic(
        BlackHoleLook? look = null,
        int centerX = Size / 2,
        int centerY = Size / 2,
        bool withReprojection = false)
    {
        using var pixelShader = _renderer.CreatePixelShader("GeodesicProbePS");
        var description = new SceneDescription(
            Size, Size, centerX, centerY,
            new SettingsModel(), 0.0, true,
            Probe: new ShaderProbe(TemperatureKelvin: withReprojection ? 5500 : -1),
            Look: look ?? new BlackHoleLook(),
            ScreenHeight: Size);

        return _preview.Render(description, pixelShader);
    }

    private static (double R, double G, double B) Pixel(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buffer[i + 2], buffer[i + 1], buffer[i]);
    }

    private static double Brightness(byte[] buffer, int x, int y)
    {
        var p = Pixel(buffer, x, y);
        return (p.R + p.G + p.B) / 3.0;
    }

    /// <summary>从中心沿方向步进，找到第一个非黑像素，即该方向的阴影半径（px）。</summary>
    private static int ShadowRadiusAlong(byte[] buffer, int cx, int cy, int dx, int dy, int maxRadius)
    {
        for (int step = 1; step <= maxRadius; step++)
        {
            int x = cx + dx * step;
            int y = cy + dy * step;
            if (x < 0 || y < 0 || x >= Size || y >= Size)
            {
                return step;
            }

            if (Brightness(buffer, x, y) > 8)
            {
                return step;
            }
        }

        return maxRadius;
    }

    private static int CountBlackPixels(byte[] buffer)
    {
        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i] <= 8 && buffer[i + 1] <= 8 && buffer[i + 2] <= 8)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void GeodesicProbe_Compiles()
    {
        using var ps = _renderer.CreatePixelShader("GeodesicProbePS");

        Assert.NotNull(ps);
        Assert.Contains("bool TraceGeodesic(", ShaderSources.FullSource);
    }

    [Fact]
    public void ShadowCenter_IsPureBlack()
    {
        var pixels = RenderGeodesic();
        var center = Pixel(pixels, Size / 2, Size / 2);

        Assert.True(center.R <= 2, $"阴影中心应为纯黑，实测 R={center.R}");
        Assert.True(center.G <= 2, $"阴影中心应为纯黑，实测 G={center.G}");
        Assert.True(center.B <= 2, $"阴影中心应为纯黑，实测 B={center.B}");
    }

    [Fact]
    public void FarFromTheHole_ShowsTheBackground()
    {
        var pixels = RenderGeodesic();

        Assert.True(Brightness(pixels, 4, 4) > 8, "远离黑洞处应显示桌面背景");
        Assert.True(Brightness(pixels, Size - 5, Size - 5) > 8, "远离黑洞处应显示桌面背景");
    }

    [Fact]
    public void ShadowRadius_MatchesHoleSize()
    {
        var look = new BlackHoleLook { HoleSize = 0.15, Roll = 0.0 };
        var pixels = RenderGeodesic(look);

        int measured = ShadowRadiusAlong(pixels, Size / 2, Size / 2, 1, 0, 120);
        double expected = LensMath.ShadowRadiusPixels(look.HoleSize, Size);

        // b_crit 阴影的角尺寸：允许 15% 误差（离散积分 + 8bit 量化）
        Assert.True(Math.Abs(measured - expected) <= expected * 0.15 + 2,
            $"阴影半径应约为 {expected:F1}px，实测 {measured}px");
    }

    [Fact]
    public void LargerHoleSize_ProducesLargerShadow()
    {
        var small = new BlackHoleLook { HoleSize = 0.06 };
        var large = new BlackHoleLook { HoleSize = 0.15 };

        int smallCount = CountBlackPixels(RenderGeodesic(small));
        int largeCount = CountBlackPixels(RenderGeodesic(large));

        Assert.True(largeCount > smallCount * 2,
            $"HoleSize 从 6% 到 15% 面积应约 6 倍（{smallCount} → {largeCount}）");
    }

    [Fact]
    public void Shadow_IsRoughlyCircular()
    {
        var look = new BlackHoleLook { HoleSize = 0.15 };
        var pixels = RenderGeodesic(look);
        int c = Size / 2;

        int right = ShadowRadiusAlong(pixels, c, c, 1, 0, 120);
        int left = ShadowRadiusAlong(pixels, c, c, -1, 0, 120);
        int down = ShadowRadiusAlong(pixels, c, c, 0, 1, 120);
        int up = ShadowRadiusAlong(pixels, c, c, 0, -1, 120);

        Assert.True(Math.Abs(right - left) <= 2, $"水平方向应对称（{left} vs {right}）");
        Assert.True(Math.Abs(up - down) <= 2, $"垂直方向应对称（{up} vs {down}）");
        Assert.True(Math.Abs(right - up) <= 3, $"阴影应近似圆形（横 {right} vs 纵 {up}）");
    }

    [Fact]
    public void ShadowRadius_DoesNotDependOnStepCount()
    {
        var coarse = new BlackHoleLook { HoleSize = 0.15, StepCount = 24 };
        var fine = new BlackHoleLook { HoleSize = 0.15, StepCount = 88 };

        int coarseRadius = ShadowRadiusAlong(RenderGeodesic(coarse), Size / 2, Size / 2, 1, 0, 120);
        int fineRadius = ShadowRadiusAlong(RenderGeodesic(fine), Size / 2, Size / 2, 1, 0, 120);

        Assert.True(Math.Abs(coarseRadius - fineRadius) <= 3,
            $"积分步数不应改变阴影大小（24 步 {coarseRadius}px vs 88 步 {fineRadius}px）");
    }

    [Fact]
    public void Shadow_FollowsTheCursorPosition()
    {
        var pixels = RenderGeodesic(new BlackHoleLook { HoleSize = 0.15 }, centerX: 80, centerY: 160);

        Assert.True(Brightness(pixels, 80, 160) <= 8, "阴影应出现在指定中心");
        Assert.True(Brightness(pixels, Size / 2, Size / 2) > 8, "原中心处不应再有阴影");
    }

    [Fact]
    public void Roll_DoesNotChangeShadowSize()
    {
        var straight = new BlackHoleLook { HoleSize = 0.15, Roll = 0.0 };
        var rolled = new BlackHoleLook { HoleSize = 0.15, Roll = 1.0 };

        int a = ShadowRadiusAlong(RenderGeodesic(straight), Size / 2, Size / 2, 1, 0, 120);
        int b = ShadowRadiusAlong(RenderGeodesic(rolled), Size / 2, Size / 2, 1, 0, 120);

        Assert.True(Math.Abs(a - b) <= 3, $"整体旋转不改变阴影大小（{a} vs {b}）");
    }

    [Fact]
    public void Reprojection_NeverShrinksTheDarkRegion()
    {
        // 弯折过度（到不了天空平面）的光线会被淡出成黑，
        // 所以开启反投影后暗区只会变大、不会变小。
        var look = new BlackHoleLook { HoleSize = 0.15 };

        int pureShadow = CountBlackPixels(RenderGeodesic(look));
        int withLensing = CountBlackPixels(RenderGeodesic(look, withReprojection: true));

        Assert.True(withLensing >= pureShadow,
            $"反投影不应缩小暗区（纯阴影 {pureShadow} vs 含透镜 {withLensing}）");
    }

    [Fact]
    public void ShadowIsMuchSmallerThanTheFrame()
    {
        var pixels = RenderGeodesic(new BlackHoleLook { HoleSize = 0.06 });

        int black = CountBlackPixels(pixels);
        int total = Size * Size;

        Assert.True(black < total * 0.05,
            $"6% 的阴影不应覆盖大片画面（黑色占比 {100.0 * black / total:F1}%）");
    }

    public void Dispose()
    {
        _background.Dispose();
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
