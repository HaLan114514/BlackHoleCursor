using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// 真实 GPU（WARP 软件渲染）端到端测试：从 <c>SettingsModel</c> 出发走**产品主流程**
/// <c>CompositePS</c>，验证「设置 → 渲染」这条链路真的通。
///
/// M1.8.1：本文件原先 30 个用例里有 28 个显式走 <c>LegacyCompositePS</c>，
/// 断言的是 v1 几何（视界 14px、盘 33px）。现在统一切到 v2，并按语义重新归类：
///   · v2 有对应语义的 → 改写（时间动画、透镜开关、盘旋转、N4 淡入淡出、渲染冒烟）
///   · 已被 v2 专用测试文件覆盖的 → 不重复（真阴影 / 盘发光 / 遮挡 / 远场淡出 /
///     色温 / 染色见 GeodesicCompositeTests、GeodesicDiskTests、DiskShadingTests、
///     FarFieldLensingTests、KerrTests、StarFieldTests）
///   · 只属于 v1 的 → 删除（碎石带 N2、光子环调参、透镜强度/半径、GlobalScale）
/// </summary>
public class SceneRendererTests : IDisposable
{
    private const int Size = 128;
    private const int Center = Size / 2;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _backgroundView;

    public SceneRendererTests()
    {
        // 强制 WARP：保证在没有独显/独显被占用的机器上结果依然稳定
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _backgroundView = PreviewBackground.CreateView(_device.Device, Size, Size);
    }

    /// <summary>
    /// 走产品主流程渲染一帧。
    ///
    /// 两个必须注意的点：
    /// 1. 必须传 <c>ScreenHeight</c>：HoleSize 是「占屏幕高度的比例」，不传屏高时换算会偏。
    /// 2. 必须显式传 <c>Look</c>：<c>SceneParamsBuilder</c> 里是
    ///    <c>d.Look ?? BlackHoleLook.Default</c> —— 只给 SettingsModel 的话，
    ///    v2 参数（DiskSpeed / DiskRoll / HoleSize …）**根本不会进着色器**，
    ///    渲染出来的永远是一份默认的 Gargantua。这里用 <c>FromSettings</c> 映射，
    ///    顺便把「设置 → 渲染模型」这条链路也纳入覆盖。
    /// </summary>
    private byte[] RenderFrame(
        SettingsModel settings,
        double time = 0.0,
        bool lensEnabled = false,
        bool withBackground = false,
        double scale = 1.0,
        double fade = 1.0)
    {
        _preview.SetBackground(withBackground ? _backgroundView : null);

        var description = new SceneDescription(
            Size, Size, Center, Center, settings, time, lensEnabled, scale, fade,
            Look: LookPresets.FromSettings(settings),
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
    }

    private static int CountOpaque(byte[] buffer)
    {
        int count = 0;
        for (int i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 128)
            {
                count++;
            }
        }

        return count;
    }

    private static int Luminance(byte[] buffer, int x, int y)
    {
        var p = Pixel(buffer, x, y);
        return (p.R + p.G + p.B) / 3;
    }

    private static int MaxLuminance(byte[] buffer)
    {
        int max = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                max = Math.Max(max, Luminance(buffer, x, y));
            }
        }

        return max;
    }

    private static int DifferingBytes(byte[] a, byte[] b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void ShadersCompileSuccessfully()
    {
        Assert.NotNull(_renderer);
        Assert.Contains("float4 CompositePS(", ShaderSources.FullSource);
        Assert.Contains("FullscreenVS", ShaderSources.FullSource);
        Assert.Contains("LensSample ResolveLens(", ShaderSources.FullSource);
    }

    [Fact]
    public void PreviewRenderer_ReturnsFullFrameBuffer()
    {
        var buffer = RenderFrame(new SettingsModel(), withBackground: true);

        Assert.Equal(Size * Size * 4, buffer.Length);
    }

    [Fact]
    public void PreviewRenderer_SupportsResizing()
    {
        _preview.Resize(64, 64);
        var buffer = _preview.Render(
            new SceneDescription(64, 64, 32, 32, new SettingsModel(), 0, false, ScreenHeight: 64));

        Assert.Equal(64 * 64 * 4, buffer.Length);
    }

    /// <summary>渲染冒烟：中心确实画出东西，且远处让真实桌面透出来。</summary>
    [Fact]
    public void DefaultSettings_DrawAHoleAndLetTheFarFieldThrough()
    {
        var pixels = RenderFrame(new SettingsModel(), withBackground: true);

        Assert.True(CountOpaque(pixels) > 0, "中心必须被绘制，否则说明渲染根本没跑起来");
        Assert.True(Pixel(pixels, 2, 2).A < 30, $"远离黑洞处必须透明，实际 A={Pixel(pixels, 2, 2).A}");
    }

    /// <summary>盘条纹随时间流动（DiskSpeed ≠ 0 时画面必须动）。</summary>
    [Fact]
    public void DiskSpeed_AnimatesTheStreaksOverTime()
    {
        var settings = new SettingsModel { DiskSpeed = 5.0 };

        var first = RenderFrame(settings, time: 0.0);
        var later = RenderFrame(settings, time: 1.5);

        // 盘只占画面的一小块（128 视口下盘外径约 21px），且条纹是细节纹理，
        // 所以这里只要求「确实变了」，不要求大面积变化
        int diff = DifferingBytes(first, later);
        Assert.True(diff > first.Length * 0.005,
            $"时间推进后盘条纹应发生变化，实测不同字节 {diff} / {first.Length}");
    }

    /// <summary>
    /// 盘的条纹速度为 0 时冻结**盘**：盘与星场都不再动，画面逐字节相同。
    ///
    /// ⚠️ fixes-v2 问题 4 之后这条要显式关掉星云：星云走的是**不受门控**的时间
    /// （`Timing.y`），用户要求「鼠标不动时星云也要缓慢流动」，
    /// 所以默认设置下画面本来就不再静止了 —— 那是刻意设计，不是回归。
    /// 星云自己的时间演化由 `NebulaTests.Nebula_EvolvesOverTime` 覆盖。
    /// </summary>
    [Fact]
    public void ZeroDiskSpeed_WithNebulaOff_KeepsImageStatic()
    {
        var settings = new SettingsModel { DiskSpeed = 0, NebulaGain = 0 };

        var first = RenderFrame(settings, time: 0.0);
        var later = RenderFrame(settings, time: 1.5);

        Assert.Equal(0, DifferingBytes(first, later));
    }

    /// <summary>透镜开关：开启时把抓到的桌面画进画面，关闭时只有黑洞本体。</summary>
    [Fact]
    public void LensEnabled_PaintsTheCapturedBackground()
    {
        var off = RenderFrame(new SettingsModel(), lensEnabled: false, withBackground: true);
        var on = RenderFrame(new SettingsModel(), lensEnabled: true, withBackground: true);

        int diff = DifferingBytes(off, on);
        Assert.True(diff > off.Length * 0.05,
            $"开启透镜后画面应明显不同，实测不同字节 {diff} / {off.Length}");
    }

    /// <summary>
    /// 盘在屏幕平面内的旋转（DiskRoll）会改变盘的朝向。
    /// 注意阴影本身是旋转不变的（撞击参数 b 不受 roll 影响），
    /// 所以差异必须来自盘而不是阴影 —— 这也顺带验证了 roll 没有误伤阴影。
    /// </summary>
    [Fact]
    public void DiskRoll_RotatesTheDiskInScreenSpace()
    {
        var upright = RenderFrame(new SettingsModel { DiskRoll = 0.0 });
        var rolled = RenderFrame(new SettingsModel { DiskRoll = 1.0 });

        int diff = DifferingBytes(upright, rolled);
        Assert.True(diff > upright.Length * 0.02,
            $"改变 DiskRoll 后画面应发生变化，实测不同字节 {diff} / {upright.Length}");
    }

    [Fact]
    public void TransitionFade_ZeroMakesEverythingTransparent()
    {
        var pixels = RenderFrame(new SettingsModel(), fade: 0.0);

        Assert.Equal(0, CountOpaque(pixels));
        Assert.True(Pixel(pixels, Center, Center).A < 4, "fade=0 时连阴影都不该画出来");
    }

    /// <summary>输出是预乘 alpha：淡出时 rgb 与 a 必须一起变，否则会留下「颜色还在」的脏边。</summary>
    [Fact]
    public void TransitionFade_DimsRgbAndAlphaTogether()
    {
        var solid = RenderFrame(new SettingsModel(), fade: 1.0);
        var half = RenderFrame(new SettingsModel(), fade: 0.5);

        Assert.True(Pixel(half, Center, Center).A < Pixel(solid, Center, Center).A,
            $"透明度应下降（{Pixel(solid, Center, Center).A} → {Pixel(half, Center, Center).A}）");

        int solidMax = MaxLuminance(solid);
        int halfMax = MaxLuminance(half);
        Assert.True(halfMax < solidMax * 0.75, $"rgb 也必须一起变暗（{solidMax} → {halfMax}）");
    }

    public void Dispose()
    {
        _backgroundView.Dispose();
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
