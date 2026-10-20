using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M2.4：预览区与桌面覆盖层必须「所调即所见」（prd-v2 F7-2）。
///
/// 关键不变量（M2.4）：两边共用 <see cref="SceneParamsBuilder"/> 与
/// <see cref="LookPresets.FromSettings"/>，因此**外观参数**（色温/倾角/内外径/多普勒……）
/// 必须逐项相同，改设置后预览立刻跟着变。
///
/// ⚠️ **尺寸不再要求一致**（fixes-v2 问题 3）：预览改成了**缩略图**语义 ——
/// 把实机整块覆盖层窗口等比缩放塞进 184px 的小框，好让人一眼看到全貌。
/// 原先「预览与实机像素半径相同」的断言按设计就该失效，改由
/// <c>ThumbnailScale_FitsTheWholeOverlayWindowIntoThePreviewBox</c> 守住缩放正确性。
/// </summary>
public class PreviewSyncTests : IDisposable
{
    private const int PreviewSize = 184;
    private const int OverlaySize = 872;
    private const double ScreenHeight = 1080.0;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _backgroundView;

    public PreviewSyncTests()
    {
        _device = RenderDevice.Create(preferHardware: false);   // 强制 WARP，结果稳定
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, PreviewSize, PreviewSize);
        _backgroundView = PreviewBackground.CreateView(_device.Device, PreviewSize, PreviewSize);
    }

    private static SceneConstants Build(SettingsModel s, double size, double screenHeight)
        => SceneParamsBuilder.Build(new SceneDescription(
            size, size, size / 2.0, size / 2.0, s, 0.0, true,
            Look: LookPresets.FromSettings(s),
            ScreenHeight: screenHeight));

    /// <summary>阴影半径（像素）= 换算后的 uv 比例 × 视口高。</summary>
    private static double ShadowPixels(SceneConstants c, double viewportHeight)
        => c.Hole.X * viewportHeight;

    [Fact]
    public void PreviewAndOverlay_ShareEveryAppearanceConstant()
    {
        var settings = new SettingsModel { DiskTemp = 12345, Spin = 0.6, DiskOuter = 12.0 };
        var preview = Build(settings, PreviewSize, ScreenHeight);
        var overlay = Build(settings, OverlaySize, ScreenHeight);

        Assert.Equal(overlay.Look0, preview.Look0);
        Assert.Equal(overlay.Look1, preview.Look1);
        Assert.Equal(overlay.Look2, preview.Look2);
        Assert.Equal(overlay.Look3, preview.Look3);
        Assert.Equal(overlay.Tint, preview.Tint);
        Assert.Equal(overlay.Hole.Z, preview.Hole.Z);      // 积分步数

        // 透镜开关也要一致：预览关了透镜却和桌面不一样，等于预览没用
        Assert.Equal(overlay.Timing.W, preview.Timing.W);
    }

    /// <summary>
    /// fixes-v2 问题 3（续）：默认洞径下预览按**真实比例**渲染 —— 不缩小，
    /// 且阴影在框里的大小确实等于「屏上尺寸 × 框的缩放比」。
    /// </summary>
    [Fact]
    public void PreviewFit_DefaultHole_UsesRealScale()
    {
        var look = LookPresets.FromSettings(new SettingsModel());

        var (hole, scale) = OverlayGeometry.PreviewFit(look, PreviewSize);

        Assert.Equal(1.0, scale, 6);
        Assert.Equal(look.HoleSize / OverlayGeometry.PreviewScreenSpan, hole, 6);

        // 阴影在框里的像素半径 = 屏上半径 × (框边长 / 框覆盖的屏高像素数)
        double expectedPx = look.HoleSize * ScreenHeight * (PreviewSize / (OverlayGeometry.PreviewScreenSpan * ScreenHeight));
        Assert.Equal(expectedPx, hole * PreviewSize, 3);
    }

    /// <summary>
    /// 这条是本次修复的**核心**：预览必须反映屏上的实际大小。
    /// 旧的「缩略图」是尺度无关的，洞设多大框里都一样 —— 用户实测后说「黑洞太大时基本没用」。
    /// </summary>
    [Fact]
    public void PreviewFit_ReflectsTheOnScreenHoleSize()
    {
        var small = LookPresets.FromSettings(new SettingsModel { HoleSize = 0.02 });
        var large = LookPresets.FromSettings(new SettingsModel { HoleSize = 0.06 });

        var (smallHole, smallScale) = OverlayGeometry.PreviewFit(small, PreviewSize);
        var (largeHole, largeScale) = OverlayGeometry.PreviewFit(large, PreviewSize);

        Assert.Equal(1.0, smallScale, 6);
        Assert.Equal(1.0, largeScale, 6);
        Assert.True(largeHole > smallHole * 2.5,
            $"洞越大框里就该越大：0.02 → {smallHole:F3}，0.06 → {largeHole:F3}");
    }

    /// <summary>
    /// 洞大到盘都装不下时才整体缩小 —— 且缩得刚刚好（盘外圈贴着框边），
    /// 并把倍率返回给 UI 标注（否则用户会以为洞真的变小了）。
    /// </summary>
    [Fact]
    public void PreviewFit_ShrinksOnlyWhenTheDiskDoesNotFit()
    {
        var huge = LookPresets.FromSettings(new SettingsModel { HoleSize = 0.15 });

        var (hole, scale) = OverlayGeometry.PreviewFit(huge, PreviewSize);

        Assert.True(scale < 1.0, $"大盘大洞应当触发缩小，实际倍率 {scale:F3}");

        double shadowPx = hole * PreviewSize;
        double diskPx = OverlayGeometry.DiskOuterRadiusPixels(huge.OuterRadius, shadowPx)
                        * OverlayGeometry.DiskMarginFactor;

        Assert.Equal(PreviewSize / 2.0, diskPx, 1);
    }

    /// <summary>盘越大越容易触发缩小（同样的洞径下，外径 20 的盘比外径 3 的更早装不下）。</summary>
    [Fact]
    public void PreviewFit_ShrinksMoreWhenTheDiskIsLarger()
    {
        var tight = LookPresets.FromSettings(new SettingsModel { DiskOuter = 7.0 });
        var wide = LookPresets.FromSettings(new SettingsModel { DiskOuter = 20.0, HoleSize = 0.15 });

        var (tightHole, _) = OverlayGeometry.PreviewFit(tight, PreviewSize);
        var (wideHole, wideScale) = OverlayGeometry.PreviewFit(wide, PreviewSize);

        Assert.True(wideScale < 1.0, "大盘必然要缩");
        Assert.True(wideHole < tightHole, $"盘更大时洞应占比更小：{tightHole:F3} → {wideHole:F3}");
    }

    /// <summary>任何预览框尺寸下都不能退化：比例落在合法区间，且盘一定装得进框（要么不缩、要么刚好装下）。</summary>
    [Theory]
    [InlineData(64)]
    [InlineData(184)]
    [InlineData(320)]
    public void PreviewFit_StaysSaneAtAnyPreviewSize(int previewSize)
    {
        var look = LookPresets.FromSettings(new SettingsModel { HoleSize = 0.15 });
        var (hole, scale) = OverlayGeometry.PreviewFit(look, previewSize);

        Assert.InRange(hole, 1e-4, 0.5);
        Assert.InRange(scale, 0.0, 1.0);

        double diskPx = OverlayGeometry.DiskOuterRadiusPixels(look.OuterRadius, hole * previewSize)
                        * OverlayGeometry.DiskMarginFactor;

        Assert.True(diskPx <= previewSize / 2.0 + 1e-6,
            $"盘必须装得进框：盘半径 {diskPx:F1} > 半边长 {previewSize / 2.0:F1}");
    }

    private static double WindowRadius(BlackHoleLook look, double shadowPx)
        => OverlayGeometry.ComputeV2WindowRadius(
            shadowPx,
            OverlayGeometry.DiskOuterRadiusPixels(look.OuterRadius, shadowPx));

    /// <summary>
    /// 端到端：改一个 v2 参数，预览画面必须真的变。
    /// 这条同时守住 M1.8.1 踩过的坑 —— 只传 SettingsModel 而不传 Look 时，
    /// 渲染器用的是默认 Gargantua，画面纹丝不动，断言会「假通过」。
    /// </summary>
    [Fact]
    public void ChangingAV2Parameter_ChangesThePreviewImage()
    {
        _preview.SetBackground(_backgroundView);

        byte[] warm = Render(new SettingsModel { DiskTemp = 3000 });
        byte[] cool = Render(new SettingsModel { DiskTemp = 16000 });

        Assert.NotEqual(warm, cool);
    }

    /// <summary>预览缓冲区必须完整，否则 WriteableBitmap 回读会越界。</summary>
    [Fact]
    public void PreviewRender_ReturnsAFullBuffer()
    {
        _preview.SetBackground(_backgroundView);

        Assert.Equal(PreviewSize * PreviewSize * 4, Render(new SettingsModel()).Length);
    }

    /// <summary>
    /// 新增的 <c>MonitorFromWindow</c> P/Invoke 签名错了会静默返回 0
    /// （进而让预览回落到 WPF 的逻辑像素口径，在高 DPI 下比实机偏小）。
    /// </summary>
    [Fact]
    public void PrimaryMonitorHeight_IsPositiveWhenADesktopIsPresent()
    {
        int height = ScreenMetrics.PrimaryMonitorHeight();

        Assert.True(height > 0,
            $"有桌面会话时主显示器高度必须 > 0，实际 {height}（Win32 调用失败会让预览回落口径）");
    }

    private byte[] Render(SettingsModel settings)
        => _preview.Render(new SceneDescription(
            PreviewSize, PreviewSize, PreviewSize / 2.0, PreviewSize / 2.0,
            settings, 0.0, true,
            Look: LookPresets.FromSettings(settings),
            ScreenHeight: ScreenHeight));

    public void Dispose()
    {
        _backgroundView.Dispose();
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
