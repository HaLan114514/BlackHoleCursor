using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.4.4：v2 主流程 <c>CompositePS</c>（测地线 + 盘穿越 + 反投影 + tonemap + 远场淡出）。
/// 这里不再用探针入口，而是直接走真正上屏的那条路径。
/// </summary>
public class GeodesicCompositeTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _background;

    public GeodesicCompositeTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _background = PreviewBackground.CreateView(_device.Device, Size, Size);
        _preview.SetBackground(_background);
    }

    private byte[] Render(BlackHoleLook? look = null, double desktopReady = 0.0)
    {
        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size,
            DesktopReady: desktopReady);

        return _preview.Render(description);
    }

    private static (double R, double G, double B, double A) At(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buffer[i + 2], buffer[i + 1], buffer[i], buffer[i + 3]);
    }

    private static double Brightness(byte[] buffer, int x, int y)
    {
        var p = At(buffer, x, y);
        return (p.R + p.G + p.B) / 3.0;
    }

    /// <summary>离中心 dist 处一圈像素的平均 alpha。</summary>
    private static double MeanAlphaAt(byte[] buffer, int dist)
    {
        double sum = 0;
        int count = 0;
        int cx = Size / 2;
        int cy = Size / 2;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double d = Math.Sqrt((dx * dx) + (dy * dy));
                if (Math.Abs(d - dist) < 1.5)
                {
                    sum += buffer[((y * Size) + x) * 4 + 3];
                    count++;
                }
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    private static double MaxBrightness(byte[] buffer)
    {
        double max = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            double b = (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;
            if (b > max)
            {
                max = b;
            }
        }

        return max;
    }

    private static int ShadowRadiusPx(BlackHoleLook look)
        => (int)Math.Round(LensMath.ShadowRadiusPixels(look.HoleSize, Size));

    [Fact]
    public void CompositePS_CompilesAndRenders()
    {
        var pixels = Render();

        Assert.Equal(Size * Size * 4, pixels.Length);
    }

    /// <summary>
    /// prd-v2 §7.1：阴影中心必须纯黑且不透明。
    ///
    /// 这里把盘的 <c>Gain</c> 设为 0 来隔离「纯阴影」：默认是近侧视（incl≈1.52），
    /// 中心那条视线会在 z≈+3 r_s 穿过盘的近侧并发光（Gargantua 造型里横穿阴影的亮带），
    /// 若不关发光，中心是否全黑会随分辨率漂移，断言会变得很脆。
    /// </summary>
    [Fact]
    public void ShadowCenter_IsOpaqueBlack()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        look.Gain = 0.0;

        var pixels = Render(look);
        var center = At(pixels, Size / 2, Size / 2);

        Assert.True(center.R <= 8 && center.G <= 8 && center.B <= 8,
            $"阴影中心应为纯黑，实际 ({center.R}, {center.G}, {center.B})");
        Assert.True(center.A > 200, $"阴影中心应不透明，实际 alpha={center.A}");
    }

    /// <summary>tonemap：曝光调高不会让盘变暗（HDR 压缩方向正确）。</summary>
    [Fact]
    public void HigherExposure_DoesNotDimDisk()
    {
        var low = LookPresets.Create(LookPresets.Gargantua);
        low.Exposure = 0.3;
        var high = LookPresets.Create(LookPresets.Gargantua);
        high.Exposure = 2.5;

        double dim = MaxBrightness(Render(low));
        double bright = MaxBrightness(Render(high));

        Assert.True(bright > dim, $"提高曝光后盘峰值亮度不应降低：0.3 → {dim:F1}，2.5 → {bright:F1}");
    }

    /// <summary>
    /// fixes-v2 问题 5：折射层必须**完全盖住**未折射的桌面。
    ///
    /// 旧语义是「alpha 由内向外单调衰减，远处透出桌面」（alpha 用的是 `window`），
    /// 于是屏幕上同时出现「折射过的」和「没折射过的」两层，折射那层偏浅。
    /// 现在 alpha 只由「这里确实画了折射内容」决定 —— 有内容就拉满，
    /// 只有走到窗口边缘羽化处才让桌面透出来。
    /// </summary>
    [Fact]
    public void LensLayer_CoversTheUnrefractedDesktop()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        int shadow = ShadowRadiusPx(look);

        var pixels = Render(look);

        double near = MeanAlphaAt(pixels, shadow);
        double middle = MeanAlphaAt(pixels, shadow * 4);
        double outer = MeanAlphaAt(pixels, shadow * 8);

        Assert.True(near > 240, $"折射层应完全盖住桌面（未折射层被压在下面），实际 alpha={near:F1}");
        Assert.True(middle > 240, $"中距同样应完全盖住，实际 alpha={middle:F1}");
        Assert.True(outer < 90, $"只有窗口边缘才该透出桌面，实际 alpha={outer:F1}");
    }

    /// <summary>
    /// alpha 沿半径必须平滑 —— 特别要盯住**阴影外侧那一圈**：
    /// 那里的取样点会跑到抓屏区之外（折射层没有桌面内容），
    /// 早期版本 alpha 会掉到 0.3，与紧邻的满不透明区形成一道硬边（实测跳变 128）。
    /// 现在由星云接管那圈，alpha 保持拉满。
    /// </summary>
    [Fact]
    public void FarField_HasNoHardEdge()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        var pixels = Render(look);

        int y = Size / 2;
        double maxJump = 0;
        for (int x = Size / 2 + 1; x < Size - 1; x++)
        {
            double jump = Math.Abs(At(pixels, x + 1, y).A - At(pixels, x, y).A);
            if (jump > maxJump)
            {
                maxJump = jump;
            }
        }

        Assert.True(maxJump < 12, $"沿半径方向 alpha 不应突变，实测最大跳变 {maxJump:F1}");
    }

    /// <summary>
    /// v3（P1.4）：覆盖层窗口 v3 起是**整块显示器**，所以**视口边界就是屏幕边界** ——
    /// 边缘处 alpha 必须为 0，否则会在屏幕四周看到一圈矩形硬边。
    ///
    /// 这个性质以前由「窗口只有 418²」天然保证（窗口外本来就没有像素）；
    /// 全屏之后只能靠 <c>composite.hlsl</c> 的 <c>fadeR = 0.5 / Hole.x</c> 那一项：
    /// 它把 alpha 在**视口半高**处收完，正好是屏幕的上下边缘。这条把它锁住。
    /// </summary>
    [Fact]
    public void OverlayAlpha_VanishesAtTheViewportEdge()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        var pixels = Render(look);

        int last = Size - 1;
        (int X, int Y)[] edge =
        {
            (0, 0), (Size / 2, 0), (last, 0),
            (0, last), (Size / 2, last), (last, last),
            (0, Size / 2), (last, Size / 2),
        };

        foreach (var (x, y) in edge)
        {
            double alpha = At(pixels, x, y).A;
            Assert.True(alpha < 8, $"视口边缘 ({x},{y}) 的 alpha 应为 0，实测 {alpha:F1}");
        }
    }

    /// <summary>
    /// W7（抄参考实现）：**抓屏可用时整屏不透明** —— 连视口四角都必须是 alpha=1。
    ///
    /// 这是「折射区慢半拍」的根治：整屏都由我们画 ⇒ 内容年龄一致 ⇒
    /// 不再有「我们画的老一帧」与「透传的实时桌面」并排对比（fixes-v3 问题 4）。
    /// </summary>
    [Fact]
    public void OverlayAlpha_FillsTheWholeViewportWhenTheDesktopIsReady()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        var pixels = Render(look, desktopReady: 1.0);

        int last = Size - 1;
        (int X, int Y)[] edge =
        {
            (0, 0), (Size / 2, 0), (last, 0),
            (0, last), (Size / 2, last), (last, last),
            (0, Size / 2), (last, Size / 2),
        };

        foreach (var (x, y) in edge)
        {
            double alpha = At(pixels, x, y).A;
            Assert.True(alpha > 247, $"抓屏可用时视口边缘 ({x},{y}) 应当不透明，实测 {alpha:F1}");
        }
    }

    /// <summary>
    /// W7 的**兜底**（比参考实现严格）：抓屏不可用时**必须退回透明**。
    /// 否则受保护内容 / HDR / 锁屏 / 抓挂了会变成整屏死黑 —— 参考实现是拿程序化图案顶着。
    /// </summary>
    [Fact]
    public void OverlayAlpha_FallsBackToTransparentWhenTheDesktopIsNotReady()
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        var pixels = Render(look, desktopReady: 0.0);

        // 四角：没有抓屏内容可画，必须完全透明（与 OverlayAlpha_VanishesAtTheViewportEdge 同语义）
        Assert.True(At(pixels, 0, 0).A < 8, "抓屏不可用时不该整屏不透明（会整屏死黑）");
        Assert.True(At(pixels, Size - 1, Size - 1).A < 8, "抓屏不可用时不该整屏不透明");
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
