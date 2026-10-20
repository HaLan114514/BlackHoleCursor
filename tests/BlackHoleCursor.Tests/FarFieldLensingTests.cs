using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.5：远场解析弱偏折 + 交界圆无缝 + 轻微色差。
///
/// 验证入口 <c>FarFieldProbePS</c> 不画颜色，而是把该像素的**背景取样位移**直接输出：
///   rg = 绿通道（无色差基准），ba = 蓝通道（弯得最多）；
///   位移放大 gain 倍并居中在 0.5，以充分利用 8 位输出的分辨率。
///
/// 精度说明（两个都踩过坑，值得记住）：
/// 1. 不能直接输出取样 uv —— 8 位 UNORM 会把 uv 变成 round(255·uv)/255，
///    相当于按 256/255 缩放整张图，测出的位移带上 r/255 px 的**外向**偏差，
///    在远处（真实位移≈0）这个偏差比信号还大（r=107px 时高达 0.42px）。
/// 2. 位移在同一圈上是**常数**，量化误差不会因取平均而抵消（只会保留一个固定的舍入偏差）。
///    所以这里做**自动量程**：先粗测一次，再按实测值把增益调到接近满量程，
///    把量化步长压到远小于被测值。
/// </summary>
public class FarFieldLensingTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public FarFieldLensingTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    private byte[] Render(double holeSize, double outerRadius, double gain, int steps = 48)
    {
        using var pixelShader = _renderer.CreatePixelShader("FarFieldProbePS");

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.HoleSize = holeSize;
        look.OuterRadius = outerRadius;
        look.StepCount = steps;
        look.Roll = 0.0;   // 位移退化为纯径向，便于按半径统计

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Probe: new ShaderProbe(NoiseScale: gain),
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description, pixelShader);
    }

    /// <summary>交界圆半径（像素）：b = bmax = rout + 3 处。</summary>
    private static double BoundaryRadius(double holeSize, double outerRadius)
        => LensMath.MaxImpactParameter(outerRadius) / LensMath.CriticalImpactParameter
           * LensMath.ShadowRadiusPixels(holeSize, Size);

    private static double ShadowPixels(double holeSize) => LensMath.ShadowRadiusPixels(holeSize, Size);

    /// <summary>该像素的背景取样位移（uv 单位，向外为正）。</summary>
    /// <param name="channel">0 = 绿通道（基准），1 = 蓝通道（色差）。</param>
    private static (double X, double Y) DisplacementUv(byte[] buffer, int x, int y, double gain, int channel)
    {
        int i = (y * Size + x) * 4;

        // 输出是 float4(dG, dB)：R = dG.x，G = dG.y，B = dB.x，A = dB.y
        double vx = (channel == 0 ? buffer[i + 2] : buffer[i]) / 255.0;
        double vy = (channel == 0 ? buffer[i + 1] : buffer[i + 3]) / 255.0;

        return ((vx - 0.5) / gain, (vy - 0.5) / gain);
    }

    /// <summary>
    /// 该像素的**径向**位移（px，指向黑洞中心为负）。
    /// 位移本身是纯径向的，取有符号分量后非径向的量化噪声会正负相消。
    /// </summary>
    private static double RadialDisplacementAt(byte[] buffer, int x, int y, double gain, int channel)
    {
        var d = DisplacementUv(buffer, x, y, gain, channel);

        double ux = (x + 0.5) / Size - 0.5;
        double uy = (y + 0.5) / Size - 0.5;
        double r = Math.Sqrt((ux * ux) + (uy * uy));
        if (r < 1e-6)
        {
            return 0;
        }

        return ((d.X * ux) + (d.Y * uy)) / r * Size;
    }

    /// <summary>半径 radius 那一圈像素的平均径向位移（px，负值 = 向黑洞中心偏折）。</summary>
    private static double MeanDisplacement(byte[] buffer, double radius, double gain, int channel = 0)
    {
        double cx = Size / 2.0;
        double cy = Size / 2.0;
        double total = 0;
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - cx;
                double dy = y + 0.5 - cy;
                if (Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius) < 0.5)
                {
                    total += RadialDisplacementAt(buffer, x, y, gain, channel);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// 自动量程地测出某半径处的径向位移（px）。
    /// 先用 gain=5 粗测，再按实测偏移把增益调到接近满量程（编码偏移约 0.4/0.5）。
    /// </summary>
    private double DisplacementAt(double holeSize, double outerRadius, double radius, int channel = 0, int steps = 48)
    {
        double gain = 5.0;
        double value = 0;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var pixels = Render(holeSize, outerRadius, gain, steps);
            value = MeanDisplacement(pixels, radius, gain, channel);

            // 编码后的偏移量（满量程为 0.5）
            double offset = Math.Abs(value) / Size * gain;

            if (offset > 0.45)
            {
                gain *= 0.25;                       // 快被裁剪了，收小重测
                continue;
            }

            if (offset < 0.15)
            {
                gain *= Math.Min(0.4 / Math.Max(offset, 1e-6), 64.0);   // 太小，放到接近满量程
                continue;
            }

            break;
        }

        return value;
    }

    [Fact]
    public void ResolveLens_IsPresentInShader()
    {
        Assert.Contains("LensSample ResolveLens(", ShaderSources.FullSource);
        Assert.Contains("float4 FarFieldProbePS(", ShaderSources.FullSource);

        using var ps = _renderer.CreatePixelShader("FarFieldProbePS");
        Assert.NotNull(ps);
    }

    /// <summary>
    /// prd-v2 §7 / plan-v2 M1.5：交界圆上不得有接缝。
    /// 「跳变」用单位距离的变化量衡量，并与交界圆**内侧**的自然梯度对比 ——
    /// 若远场分支算错（例如位移直接归零），跳变会远大于梯度。
    /// </summary>
    [Fact]
    public void NoSeam_AtTheHandoffCircle()
    {
        const double hole = 0.03;
        double rb = BoundaryRadius(hole, 7.0);

        double innerGradient = Math.Abs(DisplacementAt(hole, 7.0, rb - 6) - DisplacementAt(hole, 7.0, rb - 2)) / 4.0;
        double jump = Math.Abs(DisplacementAt(hole, 7.0, rb - 1) - DisplacementAt(hole, 7.0, rb + 1)) / 2.0;

        Assert.True(innerGradient > 0.05, $"交界圆内侧应有可测量的梯度，实测 {innerGradient:F3}px/px");
        Assert.True(jump < innerGradient * 1.5 + 0.05,
            $"交界圆处不应有接缝：跳变 {jump:F3}px/px，内侧自然梯度 {innerGradient:F3}px/px");
    }

    /// <summary>
    /// 交界圆两侧必须真正接得上。
    ///
    /// 注意不能直接比「rb-1 与 rb+1 的位移」：那 2px 的间隔本身就含有自然梯度
    /// （实测每 px 约 0.46px 的变化，2px 就是 0.92px，比真实跳变大 6 倍）。
    /// 这里用近侧两点的**线性外推**作为「假如继续积分会得到什么」，
    /// 再和远场解析值比较 —— 差值才是真正的接缝。
    /// 实测约 0.15px（亚像素），肉眼不可见。
    /// </summary>
    [Fact]
    public void BothBranches_AgreeOnDisplacementAtHandoff()
    {
        const double hole = 0.03;
        double rb = BoundaryRadius(hole, 7.0);

        double near1 = DisplacementAt(hole, 7.0, rb - 1);
        double near2 = DisplacementAt(hole, 7.0, rb - 2);
        double extrapolated = near1 + 2.0 * (near1 - near2);   // 外推到 rb + 1
        double actual = DisplacementAt(hole, 7.0, rb + 1);

        Assert.True(Math.Abs(near1) > 0.5, $"交界圆处应仍有可测量的位移，实测 {near1:F3}px");
        Assert.True(Math.Abs(actual - extrapolated) < 0.35,
            $"远场解析值应接得上近场积分的外推：外推 {extrapolated:F3}px vs 实测 {actual:F3}px，" +
            $"跳变 {Math.Abs(actual - extrapolated):F3}px");
    }

    /// <summary>远场位移随距离单调衰减。</summary>
    [Fact]
    public void FarFieldDisplacement_DecaysMonotonically()
    {
        const double hole = 0.03;
        double rb = BoundaryRadius(hole, 7.0);

        double near = Math.Abs(DisplacementAt(hole, 7.0, rb + 5));
        double middle = Math.Abs(DisplacementAt(hole, 7.0, rb + 15));
        double far = Math.Abs(DisplacementAt(hole, 7.0, rb + 30));

        Assert.True(near > middle, $"位移应随距离衰减（{near:F3} → {middle:F3}）");
        Assert.True(middle > far, $"位移应随距离衰减（{middle:F3} → {far:F3}）");
        Assert.True(far < near * 0.5, $"更远处应显著更小（{near:F3} → {far:F3}）");
    }

    /// <summary>14 个阴影半径之外位移基本归零 —— 远处不抖。</summary>
    [Fact]
    public void FarFieldDisplacement_VanishesBeyond14ShadowRadii()
    {
        const double hole = 0.03;
        double rb = BoundaryRadius(hole, 7.0);

        double distant = Math.Abs(DisplacementAt(hole, 7.0, 14.0 * ShadowPixels(hole)));
        double atHandoff = Math.Abs(DisplacementAt(hole, 7.0, rb + 1));

        Assert.True(distant < 0.05,
            $"14 个阴影半径之外位移应几乎为 0（不抖动），实测 {distant:F4}px");
        Assert.True(distant < atHandoff * 0.02,
            $"远处位移应远小于交界圆处（{distant:F4} vs {atHandoff:F3}）");
    }

    /// <summary>轻微色差：蓝比绿弯得多一点（远场）。</summary>
    [Fact]
    public void ChromaticAberration_BlueBendsMoreThanGreen()
    {
        const double hole = 0.06;
        const double outer = 3.0;          // bmax = 6，让 b/bmax = 2 落在视口内
        double rb = BoundaryRadius(hole, outer);

        // b/bmax = 2 → smoothstep(1,2,2) = 1，色差取到最大值 ab = 0.035
        double green = Math.Abs(DisplacementAt(hole, outer, rb * 2.0, 0));
        double blue = Math.Abs(DisplacementAt(hole, outer, rb * 2.0, 1));

        Assert.True(green > 1.0, $"该处应仍有可测量的位移，实测 {green:F3}px");
        Assert.True(blue > green * 1.01,
            $"蓝光应比绿光弯得更多：绿 {green:F4}px vs 蓝 {blue:F4}px");
    }

    /// <summary>色差在交界圆上必须消失（ab = 0.035·smoothstep(1,2,b/bmax)）。</summary>
    [Fact]
    public void ChromaticAberration_VanishesAtHandoff()
    {
        const double hole = 0.06;
        const double outer = 3.0;
        double rb = BoundaryRadius(hole, outer);

        double green = Math.Abs(DisplacementAt(hole, outer, rb * 1.02, 0));
        double blue = Math.Abs(DisplacementAt(hole, outer, rb * 1.02, 1));

        Assert.True(Math.Abs(blue - green) < green * 0.005,
            $"交界圆处不应有色差：绿 {green:F4}px vs 蓝 {blue:F4}px");
    }

    /// <summary>
    /// 远场走解析式，不跑积分 —— 改积分步数对远场必须**完全没有影响**（逐字节相同）；
    /// 近场则会受影响（否则本断言没有意义）。
    /// </summary>
    [Fact]
    public void FarField_IsIndependentOfStepCount()
    {
        const double hole = 0.03;
        double rb = BoundaryRadius(hole, 7.0);

        var coarse = Render(hole, 7.0, gain: 5.0, steps: 24);
        var fine = Render(hole, 7.0, gain: 5.0, steps: 88);

        int farDiff = 0;
        int nearDiff = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - Size / 2.0;
                double dy = y + 0.5 - Size / 2.0;
                double d = Math.Sqrt((dx * dx) + (dy * dy));

                int i = (y * Size + x) * 4;
                bool changed = coarse[i + 2] != fine[i + 2] || coarse[i + 1] != fine[i + 1];

                if (d > rb + 4 && changed) { farDiff++; }
                if (d < rb - 4 && d > 2 && changed) { nearDiff++; }
            }
        }

        Assert.True(nearDiff > 0, "近场应随积分步数变化，否则本用例失去意义");
        Assert.True(farDiff == 0, $"远场走解析式，不应受步数影响，实测差异像素 {farDiff}");
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
