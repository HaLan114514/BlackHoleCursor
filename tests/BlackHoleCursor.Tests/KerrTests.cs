using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.6：Kerr 自旋（ISCO 收缩、帧拖拽漩涡、spin &lt; 0.005 的 Schwarzschild 快速路径）。
///
/// 标量部分用验证入口 <c>KerrProbePS</c> 直接画出来：
///   R = ISCO / 4，G = 视界半径 / 2，B = 盘内径 rinK / 8（Debug.x = spin，Debug.y = rin）。
/// 画面部分走真正上屏的 <c>CompositePS</c>。
/// </summary>
public class KerrTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public KerrTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _preview.SetBackground(CreateMirrorSymmetricBackground());
    }

    /// <summary>
    /// 只随 y 变化的竖直渐变。
    /// 用它做底图时，纯径向的透镜位移不会破坏**左右镜像对称**（镜像两点取样的 y 相同），
    /// 于是「不对称程度」就成了帧拖拽的干净度量 ——
    /// 换成棋盘底图的话，棋盘自身的左右不对称（约 6 个灰阶）会把信号完全淹没。
    /// </summary>
    private ID3D11ShaderResourceView CreateMirrorSymmetricBackground()
    {
        var pixels = new uint[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            byte v = (byte)(y * 255 / (Size - 1));
            for (int x = 0; x < Size; x++)
            {
                pixels[y * Size + x] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | v;
            }
        }

        var texture = _device.Device.CreateTexture2D(
            pixels,
            Vortice.DXGI.Format.B8G8R8A8_UNorm,
            (uint)Size, (uint)Size, 1, 1,
            BindFlags.ShaderResource,
            ResourceOptionFlags.None,
            ResourceUsage.Immutable,
            CpuAccessFlags.None);

        return _device.Device.CreateShaderResourceView(texture, null);
    }

    /// <summary>用 KerrProbePS 取一组 Kerr 标量。</summary>
    private (double Isco, double Horizon, double DiskInner) Probe(double spin, double rin)
    {
        using var ps = _renderer.CreatePixelShader("KerrProbePS");

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Probe: new ShaderProbe(TemperatureKelvin: spin, NoiseScale: rin));

        byte[] pixels = _preview.Render(description, ps);
        int i = (Size / 2 * Size + Size / 2) * 4;      // 中心像素即可（全屏常量）

        return (pixels[i + 2] / 255.0 * 4.0,
                pixels[i + 1] / 255.0 * 2.0,
                pixels[i] / 255.0 * 8.0);
    }

    private byte[] Render(double spin, double gain = 1.4, double opacity = 0.85)
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        look.Spin = spin;
        look.Gain = gain;
        look.Opacity = opacity;
        look.StepCount = 88;      // 高画质档 = 参考实现的 KERR_STEPS

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    /// <summary>
    /// 两幅图的平均像素距离（0~255）。
    /// 默认只统计**近场**（4 个阴影半径以内）：远场被 M1.5 的淡出窗口压到几乎为 0，
    /// 全图平均会把近场的真实差异稀释成 3 个灰阶。
    /// </summary>
    private static double Distance(byte[] a, byte[] b, int? maxRadius = null)
    {
        int limit = maxRadius ?? (Size / 2);
        double total = 0;
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - Size / 2.0;
                double dy = y + 0.5 - Size / 2.0;
                if (Math.Sqrt((dx * dx) + (dy * dy)) > limit)
                {
                    continue;
                }

                int i = (y * Size + x) * 4;
                total += (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0;
                count++;
            }
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>画面关于竖直轴的不对称程度（左右镜像像素的平均差）。</summary>
    private static double MirrorAsymmetry(byte[] pixels)
    {
        double total = 0;
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int d = 4; d < Size / 2 - 2; d++)
            {
                int li = (y * Size + (Size / 2 - d)) * 4;
                int ri = (y * Size + (Size / 2 + d)) * 4;

                total += (Math.Abs(pixels[li] - pixels[ri])
                        + Math.Abs(pixels[li + 1] - pixels[ri + 1])
                        + Math.Abs(pixels[li + 2] - pixels[ri + 2])) / 3.0;
                count++;
            }
        }

        return total / count;
    }

    [Fact]
    public void KerrFunctions_ArePresentInShader()
    {
        Assert.Contains("float KerrIscoRs(", ShaderSources.FullSource);
        Assert.Contains("float3 KerrDpdl(", ShaderSources.FullSource);
        Assert.Contains("float DragTwist(", ShaderSources.FullSource);

        using var ps = _renderer.CreatePixelShader("KerrProbePS");
        Assert.NotNull(ps);
    }

    /// <summary>Bardeen 1972：spin=0 → 3 r_s（= 6M），spin→1 时收缩到约 0.6 r_s。</summary>
    [Fact]
    public void Isco_ShrinksWithSpin()
    {
        var zero = Probe(0.0, 2.2);
        var half = Probe(0.5, 2.2);
        var fast = Probe(0.9, 2.2);

        Assert.True(Math.Abs(zero.Isco - 3.0) < 0.05, $"spin=0 的 ISCO 应为 3 r_s，实测 {zero.Isco:F3}");
        Assert.True(half.Isco < zero.Isco - 0.5, $"spin=0.5 的 ISCO 应明显更小：{zero.Isco:F3} → {half.Isco:F3}");
        Assert.True(fast.Isco < half.Isco - 0.5, $"spin=0.9 的 ISCO 应继续收缩：{half.Isco:F3} → {fast.Isco:F3}");
        Assert.True(fast.Isco < 1.5, $"spin=0.9 的 ISCO 应在 1.2 r_s 附近（盘贴住阴影），实测 {fast.Isco:F3}");
    }

    /// <summary>
    /// 盘内径随自旋收缩（"盘贴住阴影"），且在 spin→0 时必须**连续等于用户设定的 rin** ——
    /// 参考实现写的是 max(rin·ISCO/3, ISCO)，在 spin 刚过 0.005 时会把 2.2 突跳成 3.0。
    /// </summary>
    [Fact]
    public void DiskInnerEdge_HugsShadowAndStaysContinuousAtZeroSpin()
    {
        const double rin = 2.2;

        var zero = Probe(0.0, rin);
        var fast = Probe(0.9, rin);

        Assert.True(Math.Abs(zero.DiskInner - rin) < 0.05,
            $"spin→0 时盘内径应等于设定的 rin（无跳变），实测 {zero.DiskInner:F3} vs {rin}");
        Assert.True(fast.DiskInner < rin - 0.5,
            $"高自旋时盘内径应收缩到 ISCO 附近，实测 {fast.DiskInner:F3} vs ISCO {fast.Isco:F3}");
        Assert.True(fast.DiskInner <= fast.Isco + 0.05, "盘内径不应钻进 ISCO 以内");
    }

    /// <summary>Kerr 视界半径 r+/r_s = 0.5 + sqrt(0.25 − a²)，随自旋收缩。</summary>
    [Fact]
    public void HorizonRadius_ShrinksWithSpin()
    {
        var zero = Probe(0.0, 2.2);
        var fast = Probe(0.9, 2.2);

        Assert.True(Math.Abs(zero.Horizon - 1.0) < 0.05, $"spin=0 的视界应为 1 r_s，实测 {zero.Horizon:F3}");
        Assert.True(fast.Horizon < zero.Horizon - 0.1,
            $"高自旋视界应更小（0.718 r_s）：{zero.Horizon:F3} → {fast.Horizon:F3}");
    }

    /// <summary>自旋必须真的改变画面（阴影位移 + 非对称扭曲）。</summary>
    [Fact]
    public void Spin_ChangesTheImage()
    {
        const int nearField = 61;   // ≈ 4 × 阴影半径（HoleSize 0.06，屏高 256）
        double distance = Distance(Render(0.0), Render(0.9), nearField);

        Assert.True(distance > 5.0, $"spin=0.9 与 spin=0 的画面应明显不同，近场平均像素距离 {distance:F2}");
    }

    /// <summary>
    /// 帧拖拽：关掉盘的发光与遮挡，只留透镜。
    /// spin=0 时画面关于竖直轴镜像对称；spin≠0 时背景取样被整体旋转 → 对称性被打破。
    /// </summary>
    [Fact]
    public void FrameDragging_BreaksMirrorSymmetry()
    {
        double symmetric = MirrorAsymmetry(Render(0.0, gain: 0.0, opacity: 0.0));
        double dragged = MirrorAsymmetry(Render(0.9, gain: 0.0, opacity: 0.0));

        Assert.True(dragged > symmetric * 1.2,
            $"自旋应打破左右镜像对称：spin=0 {symmetric:F3} vs spin=0.9 {dragged:F3}");
    }

    /// <summary>
    /// 阈值两侧（0.004 / 0.006）都不能出现跳变 ——
    /// 否则滑块从 0 往上推时会"啪"地变一下脸。
    /// </summary>
    [Fact]
    public void NoPop_AcrossTheFastPathThreshold()
    {
        const int nearField = 61;
        var baseline = Render(0.0);
        double full = Distance(baseline, Render(0.9), nearField);
        double below = Distance(baseline, Render(0.004), nearField);   // 仍走 Schwarzschild
        double above = Distance(baseline, Render(0.006), nearField);   // 改走 Kerr

        Assert.True(full > 5.0, $"高自旋本就应明显不同，实测 {full:F2}");
        Assert.True(above < full * 0.5,
            $"刚越过阈值不应有明显跳变：0.006 → {above:F2}，而 0.9 → {full:F2}");
        Assert.True(below < full * 0.5,
            $"阈值下方也不应有跳变：0.004 → {below:F2}，而 0.9 → {full:F2}");
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
