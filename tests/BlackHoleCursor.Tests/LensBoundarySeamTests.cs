using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v3 问题 6：开星云后画面上有一条**跟着黑洞走**的割裂缝。
///
/// 根因（用径向扫描量出来的，不是猜的）：亮度在 **r ≈ bmax / B_CRIT** 处一步掉 13/255 ——
/// 那正是 `b = bmax = rout + 3` 的**远场 / 近场交界圆**。
///
/// 交界圆两侧算「天球方向」的两套算法不同：
///   近场 = 测地线积分终态 `normalize(finalV)`；远场 = 解析弱偏折反推 `α = 2/b`。
/// 位移当年已经校准到连续（M1.5），但**方向**没管 ——
/// 而星云 / 星空是**按方向索引**的 ⇒ 那一圈上取到两片互不相干的值 ⇒ 硬缝。
///
/// 修法：在 b 接近 bmax 时把方向平滑地让给解析式（`smoothstep(0.75·bmax, bmax, b)`），
/// 交界处两侧就接得上。
///
/// 这里直接渲染主流程（`CompositePS`）并沿半径扫描，用「交界处的跳变 vs 邻域正常起伏」
/// 做**自适应**判据 —— 写死绝对阈值会被云絮本身的梯度晃到。
/// </summary>
public class LensBoundarySeamTests : IDisposable
{
    private const int Size = 256;

    /// <summary>与你配置同量级（HoleSize 0.056 ⇒ 1080p 下阴影半径 60.6px）。</summary>
    private const double HoleSize = 0.056;

    private const double OuterRadius = 7.0;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _black;

    public LensBoundarySeamTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);

        var pixels = new uint[Size * Size];
        Array.Fill(pixels, 0xFF000000u);
        var texture = _device.Device.CreateTexture2D(
            pixels,
            Vortice.DXGI.Format.B8G8R8A8_UNorm,
            (uint)Size, (uint)Size, 1, 1,
            BindFlags.ShaderResource,
            ResourceOptionFlags.None,
            ResourceUsage.Immutable,
            CpuAccessFlags.None);
        _black = _device.Device.CreateShaderResourceView(texture, null);
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _black.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>纯黑底 + 关掉盘 ⇒ 画面里只剩星云 / 星空，径向剖面干净。</summary>
    private byte[] Render(double nebulaGain, double starGain)
    {
        _preview.SetBackground(_black);

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.HoleSize = HoleSize;
        look.OuterRadius = OuterRadius;
        look.NebulaGain = nebulaGain;
        look.StarGain = starGain;
        look.Gain = 0.0;
        look.Opacity = 0.0;
        look.StepCount = 48;

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    private static double L(byte[] b, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return (b[i] + b[i + 1] + b[i + 2]) / 3.0;
    }

    /// <summary>交界圆半径（像素）：b = bmax = rout + 3 处（与 `FarFieldLensingTests` 同一口径）。</summary>
    private static int BoundaryRadius()
        => (int)Math.Round(
            LensMath.MaxImpactParameter(OuterRadius) / LensMath.CriticalImpactParameter
            * LensMath.ShadowRadiusPixels(HoleSize, Size));

    /// <summary>
    /// 径向亮度剖面（对 `rays` 条射线取平均）。
    ///
    /// 为什么要多条：星点是稀疏的，单条射线上可能一颗星都没有 ⇒ 剖面全 0，测不出连续性。
    /// 多条一起平均后每层都有值，而且**所有射线都在同一个半径穿过交界圆**，
    /// 交界处的台阶不会被平均掉（信号反而更稳）。
    /// </summary>
    private static double[] RadialProfile(byte[] buffer, int rays = 32)
    {
        var values = new double[Size / 2];

        for (int r = 0; r < values.Length; r++)
        {
            double sum = 0;
            for (int k = 0; k < rays; k++)
            {
                double angle = (2.0 * Math.PI * k) / rays;
                int x = (int)Math.Round((Size / 2.0) + (r * Math.Cos(angle)));
                int y = (int)Math.Round((Size / 2.0) + (r * Math.Sin(angle)));

                if (x >= 0 && y >= 0 && x < Size && y < Size)
                {
                    sum += L(buffer, x, y);
                }
            }

            values[r] = sum / rays;
        }

        return values;
    }

    /// <summary>
    /// 判据：交界圆处的跳变，不得明显大于**邻域**本来的起伏。
    /// 取「内侧 3 像素均值 vs 外侧 3 像素均值」的差，避开单像素噪声。
    /// </summary>
    private static void AssertNoStepAtTheBoundary(double[] profile, int radius, string what)
    {
        double Mean(int from, int to)
        {
            double sum = 0;
            for (int r = from; r <= to; r++)
            {
                sum += profile[r];
            }

            return sum / (to - from + 1);
        }

        double jump = Math.Abs(Mean(radius - 3, radius - 1) - Mean(radius + 1, radius + 3));

        // 邻域的正常起伏（跳过交界处本身）
        double neighbor = 0.0;
        for (int r = radius - 12; r <= radius + 11; r++)
        {
            if (r < 1 || r >= profile.Length - 1 || Math.Abs(r - radius) <= 3)
            {
                continue;
            }

            neighbor = Math.Max(neighbor, Math.Abs(profile[r + 1] - profile[r]));
        }

        Assert.True(
            neighbor > 0.0,
            "邻域应当有起伏（否则这条测不出连续性 —— 剖面可能是常数）");

        Assert.True(
            jump <= (neighbor * 2.0) + 1.0,
            $"{what}：交界圆（r={radius}px）处出现跳变 {jump:F1}，"
            + $"而邻域本来的最大起伏只有 {neighbor:F1}" +
            "（修复前是 13 左右的单像素突跳）");
    }

    [Fact]
    public void TheNebulaHasNoStepAtTheFieldBoundary()
    {
        int radius = BoundaryRadius();
        AssertNoStepAtTheBoundary(RadialProfile(Render(1.0, 0.0)), radius, "星云");
    }

    /// <summary>星空走的是同一个方向，同样不能在那儿断（你配置里 StarGain=0，所以这条是预防）。</summary>
    [Fact]
    public void TheStarFieldHasNoStepAtTheFieldBoundary()
    {
        int radius = BoundaryRadius();
        AssertNoStepAtTheBoundary(RadialProfile(Render(0.0, 0.6)), radius, "星空");
    }
}
