using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using BlackHoleCursor.Core.Screenshots;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M4.2（prd-v2 F8-1）：把黑洞合成回一张全屏位图。
/// 验收点：输出图像中心区域确实含黑洞，且补丁不会越界、不会糊到整张图。
/// </summary>
public class ScreenshotCompositorTests : IDisposable
{
    private const int W = 400;
    private const int H = 300;
    private const byte BaseGray = 128;

    private readonly ScreenshotCompositor _compositor = new(preferHardware: false);

    private static byte[] SolidImage(int width, int height, byte gray)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = gray;
            pixels[i + 1] = gray;
            pixels[i + 2] = gray;
            pixels[i + 3] = 0xFF;
        }

        return pixels;
    }

    private static int Luma(byte[] pixels, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
    }

    [Fact]
    public void Composite_DrawsTheHoleIntoTheBitmap()
    {
        byte[] pixels = SolidImage(W, H, BaseGray);

        Assert.True(_compositor.Composite(pixels, W, H, W / 2, H / 2, new SettingsModel(), 1080));

        int changed = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (Luma(pixels, W, x, y) != BaseGray)
                {
                    changed++;
                }
            }
        }

        Assert.True(changed > 0, "合成后位图应当有变化，否则说明根本没画上去");
    }

    /// <summary>真阴影必须出现：补丁里要有显著暗于底图的像素。</summary>
    [Fact]
    public void Composite_ProducesADarkShadowSomewhereInThePatch()
    {
        byte[] pixels = SolidImage(W, H, BaseGray);
        _compositor.Composite(pixels, W, H, W / 2, H / 2, new SettingsModel(), 1080);

        int darkest = 255;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                darkest = Math.Min(darkest, Luma(pixels, W, x, y));
            }
        }

        Assert.True(darkest < BaseGray / 2,
            $"应当出现明显暗于底图的阴影区域，实际最暗 {darkest}（底图 {BaseGray}）");
    }

    /// <summary>补丁之外不能被波及 —— 防「整个画面被提亮」的假通过。</summary>
    [Fact]
    public void Composite_LeavesTheFarCornersUntouched()
    {
        byte[] pixels = SolidImage(W, H, BaseGray);
        _compositor.Composite(pixels, W, H, W / 2, H / 2, new SettingsModel(), 1080);

        // 补丁是居中裁的，本图宽度 400 > 高度 300，补丁边长被限到 300 → 左右各留 50px
        Assert.Equal(BaseGray, Luma(pixels, W, 2, 2));
        Assert.Equal(BaseGray, Luma(pixels, W, W - 3, 2));
    }

    /// <summary>黑洞贴在图像边缘时不能越界、不能崩。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(W - 1, H - 1)]
    [InlineData(0, H - 1)]
    public void Composite_AtTheImageEdge_DoesNotOverflow(int centerX, int centerY)
    {
        byte[] pixels = SolidImage(W, H, BaseGray);

        Assert.True(_compositor.Composite(pixels, W, H, centerX, centerY, new SettingsModel(), 1080));
        Assert.True(pixels.Length == W * H * 4, "缓冲区长度不应被改变");
    }

    /// <summary>透镜关掉时仍要画出黑洞本体（只少了背景折射）。</summary>
    [Fact]
    public void Composite_WithLensDisabled_StillDrawsTheHole()
    {
        var settings = new SettingsModel { LensEnabled = false };
        byte[] pixels = SolidImage(W, H, BaseGray);

        Assert.True(_compositor.Composite(pixels, W, H, W / 2, H / 2, settings, 1080));

        int darkest = 255;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                darkest = Math.Min(darkest, Luma(pixels, W, x, y));
            }
        }

        Assert.True(darkest < BaseGray / 2, $"关掉透镜也应有阴影，实际最暗 {darkest}");
    }

    [Fact]
    public void Composite_RejectsDegenerateInputs()
    {
        var settings = new SettingsModel();

        Assert.False(_compositor.Composite(null!, W, H, 0, 0, settings, 1080));
        Assert.False(_compositor.Composite(new byte[16], 0, 0, 0, 0, settings, 1080));
        Assert.False(_compositor.Composite(SolidImage(W, H, BaseGray), W, H, 0, 0, settings, 0));
    }

    // ---- 补丁尺寸（纯函数）----

    /// <summary>
    /// v3（P3）：补丁必须覆盖**全屏的远场范围**。
    /// `composite.hlsl` 的 `fadeR = 0.5 / Hole.x` 单位是阴影半径，
    /// 换算成像素：`fadeR × shadowPx = (0.5/HoleSize) × (HoleSize × 屏高) = 0.5 × 屏高`
    /// —— 也就是视口半高。屏高 1080 ⇒ 远场半径 540 ⇒ 边长 1080。
    ///
    /// 这是「截图里的黑洞与桌面一致」的关键：全屏后远场一直显示到视口半高，
    /// 补丁若还按旧的窗口几何算（约 460），补回去就会少一圈远场透镜。
    /// </summary>
    [Fact]
    public void PatchExtent_CoversTheFullScreenFarField()
    {
        var look = LookPresets.FromSettings(new SettingsModel());

        Assert.Equal(1080, ScreenshotCompositor.PatchExtent(look, 1080, 4096, 4096));
    }

    /// <summary>补丁不得超出被合成的图，也不得超过性能上限。</summary>
    [Fact]
    public void PatchExtent_IsLimitedByTheImageAndTheCap()
    {
        var look = LookPresets.FromSettings(new SettingsModel());

        Assert.Equal(300, ScreenshotCompositor.PatchExtent(look, 2160, 400, 300));
        Assert.True(ScreenshotCompositor.PatchExtent(look, 4320, 4096, 4096) <= ScreenshotCompositor.MaxPatchExtent);
    }

    /// <summary>
    /// v3（P3）：补丁尺寸由**屏幕远场范围**决定，所以**不随洞径变化** ——
    /// 上面那条公式里 HoleSize 被约掉了，远场半径恒等于视口半高。
    /// 这与小窗口时代（补丁跟着洞径走）是相反的语义，锁在这里防回退。
    /// </summary>
    [Fact]
    public void PatchExtent_DoesNotDependOnHoleSize()
    {
        // 注意取值范围：盘的像素尺寸 ∝ HoleSize × OuterRadius，
        // 洞大到一定程度盘就会**超过**远场范围，那时补丁会跟着盘变大（见下一条断言）。
        // 所以「与洞径无关」只在盘还没超过远场范围时成立（这里 0.02 / 0.06 都满足）。
        var small = new BlackHoleLook { HoleSize = 0.02, OuterRadius = 7.0 };
        var typical = new BlackHoleLook { HoleSize = 0.06, OuterRadius = 7.0 };

        Assert.Equal(1080, ScreenshotCompositor.PatchExtent(small, 1080, 4096, 4096));
        Assert.Equal(1080, ScreenshotCompositor.PatchExtent(typical, 1080, 4096, 4096));
    }

    /// <summary>盘特别大时补丁必须让盘装得下 —— 不能因为「远场范围固定」就把盘切了。</summary>
    [Fact]
    public void PatchExtent_GrowsWhenTheDiskIsHuge()
    {
        var normal = new BlackHoleLook { HoleSize = 0.06, OuterRadius = 7.0 };
        var huge = new BlackHoleLook { HoleSize = 0.15, OuterRadius = 20.0 };

        int normalExtent = ScreenshotCompositor.PatchExtent(normal, 1080, 8192, 8192);
        int hugeExtent = ScreenshotCompositor.PatchExtent(huge, 1080, 8192, 8192);

        Assert.True(hugeExtent > normalExtent, $"大盘补丁应更大：{normalExtent} → {hugeExtent}");
    }

    public void Dispose()
    {
        _compositor.Dispose();
        GC.SuppressFinalize(this);
    }
}
