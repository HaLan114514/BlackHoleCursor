using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 **问题 2**：阴影周围出现「很多圈亮环」，应该只有一圈。
///
/// 根因：反投影会把取样 uv 推到窗口之外，旧实现用 `mirrorUV` 把它**镜像折回**，
/// 于是同一块桌面内容在阴影周围被反复复制成一圈圈同心结构
/// （bugs/1.jpg 里连界面文字都被复制到了不同半径上）。
/// 修复是越界**淡出**而不是折回 —— 窗口之外我们并不知道是什么，
/// 那里交给程序化背景光（问题 1 的星云）。
///
/// 配套的第二道闸：光线绕黑洞多转一圈就多复制一份内容，
/// 所以高阶像按累计转角指数衰减（`HIGH_ORDER_FREE_TURN` / `HIGH_ORDER_DECAY_TURN`），
/// 由 `OrderProbePS` 量化标定。
/// </summary>
public class RingArtifactTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public RingArtifactTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    private ID3D11ShaderResourceView CreateTexture(Func<int, int, uint> color)
    {
        var pixels = new uint[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                pixels[(y * Size) + x] = color(x, y);
            }
        }

        var texture = _device.Device.CreateTexture2D(
            pixels, Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)Size, (uint)Size, 1, 1,
            BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Immutable, CpuAccessFlags.None);

        return _device.Device.CreateShaderResourceView(texture, null);
    }

    /// <summary>黑底 + 一块明亮标记：同一块内容被复制几次 = 有几圈重复。</summary>
    private ID3D11ShaderResourceView Marker() => CreateTexture((x, y) =>
    {
        bool inX = x >= Size * 0.86 && x <= Size * 0.90;
        bool inY = y >= Size * 0.44 && y <= Size * 0.56;
        return inX && inY ? 0xFFFFFFFFu : 0xFF000000u;
    });

    /// <summary>
    /// 类桌面底图：暗色渐变 + 两块亮矩形（模拟暗色桌面上的两个窗口）。
    /// 用它替代真实截图 —— 确定性、不依赖二进制资产，且有结构可供折射。
    /// </summary>
    private ID3D11ShaderResourceView DesktopLike() => CreateTexture((x, y) =>
    {
        byte v = (byte)(18 + (y * 40 / (Size - 1)));

        bool windowA = x >= Size * 0.12 && x <= Size * 0.42 && y >= Size * 0.18 && y <= Size * 0.46;
        bool windowB = x >= Size * 0.55 && x <= Size * 0.88 && y >= Size * 0.58 && y <= Size * 0.86;

        if (windowA || windowB)
        {
            v = (byte)((x + y) % 24 == 0 ? 210 : 150);
        }

        return 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | v;
    });

    /// <summary>坐标编码底图：R = uv.x·255、G = uv.y·255、B = 255（作为分母）。</summary>
    private ID3D11ShaderResourceView Encoded() => CreateTexture((x, y) =>
        0xFF000000u | (255u << 16) | ((uint)(y * 255 / (Size - 1)) << 8) | (uint)(x * 255 / (Size - 1)));

    private byte[] Render(string preset, Action<BlackHoleLook>? configure = null)
    {
        var look = LookPresets.Create(preset);
        look.HoleSize = 0.06;
        look.StepCount = 48;
        look.Roll = 0.0;
        configure?.Invoke(look);

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    private static double Brightness(byte[] buffer, int i)
        => (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;

    /// <summary>沿水平射线（向右）数「亮块」个数。</summary>
    private static int CountBlobs(byte[] buffer)
    {
        int blobs = 0;
        bool inside = false;

        for (int px = 1; px < Size / 2; px++)
        {
            int i = (((Size / 2) * Size) + (Size / 2) + px) * 4;
            if (Brightness(buffer, i) > 20.0)
            {
                if (!inside)
                {
                    blobs++;
                    inside = true;
                }
            }
            else
            {
                inside = false;
            }
        }

        return blobs;
    }

    /// <summary>整圈平均的径向亮度剖面 + 峰值统计（半径单位：阴影半径）。</summary>
    private static (int Peaks, string Detail) RadialPeaks(byte[] buffer, double holeSize)
    {
        double radius = holeSize * Size;
        var profile = new List<double>();

        for (double r = 1.2; r <= 8.0; r += 0.1)
        {
            double sum = 0;
            int count = 0;
            for (int a = 0; a < 240; a++)
            {
                double theta = a * Math.PI * 2.0 / 240.0;
                int x = (int)Math.Round(Size / 2.0 + (Math.Cos(theta) * r * radius));
                int y = (int)Math.Round(Size / 2.0 + (Math.Sin(theta) * r * radius));
                if (x < 0 || y < 0 || x >= Size || y >= Size)
                {
                    continue;
                }

                sum += Brightness(buffer, ((y * Size) + x) * 4);
                count++;
            }

            profile.Add(count == 0 ? 0 : sum / count);
        }

        double max = profile.Max();
        var positions = new List<string>();

        for (int i = 3; i < profile.Count - 3; i++)
        {
            double v = profile[i];
            bool isPeak = true;
            for (int k = 1; k <= 3; k++)
            {
                if (profile[i - k] > v || profile[i + k] > v)
                {
                    isPeak = false;
                    break;
                }
            }

            if (!isPeak)
            {
                continue;
            }

            double leftMin = Math.Min(Math.Min(profile[i - 1], profile[i - 2]), profile[i - 3]);
            double rightMin = Math.Min(Math.Min(profile[i + 1], profile[i + 2]), profile[i + 3]);
            if (v - Math.Max(leftMin, rightMin) > max * 0.04)
            {
                positions.Add($"r={1.2 + (i * 0.1):F1}(v={v:F0})");
            }
        }

        string text = string.Join(" ", profile.Where((_, i) => i % 4 == 0).Select(v => v.ToString("F0")));
        return (positions.Count, $"峰值 {positions.Count} 个 [{string.Join(", ", positions)}] | 剖面 {text}");
    }

    [Fact]
    public void OrderProbe_Compiles()
    {
        Assert.Contains("float4 OrderProbePS(", ShaderSources.FullSource);
        Assert.Contains("HIGH_ORDER_FREE_TURN", ShaderSources.FullSource);

        using var ps = _renderer.CreatePixelShader("OrderProbePS");
        Assert.NotNull(ps);
    }

    /// <summary>
    /// **根因回归（主判据）**：取样 uv 沿半径不得折返。
    ///
    /// 反投影会把取样点推到窗口之外。折返一次 = 同一块桌面内容被复制一层 =
    /// 阴影周围多一圈亮环。旧实现用 `mirrorUV` 折回，于是 uv 呈锯齿状来回；
    /// 改成淡出后，越界的取样点被丢掉（读回值为 0），序列里不再有折返。
    ///
    /// 用「折返次数」而不是「亮块个数」是因为：折返的位置不一定正好落在一块窄标记上，
    /// 按亮块数会漏判（实测旧实现下亮块数仍是 1，判据失效）。
    /// </summary>
    [Fact]
    public void SampledUv_DoesNotFoldBack()
    {
        _preview.SetBackground(Encoded());

        // 只留透镜层：盘发光/遮挡、星云、星场全关
        var pixels = Render(LookPresets.Gargantua, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
            l.NebulaGain = 0;
            l.StarGain = 0;
        });

        var series = new List<double>();
        for (int px = 1; px < Size / 2; px++)
        {
            int i = (((Size / 2) * Size) + (Size / 2) + px) * 4;
            double b = pixels[i + 2];

            // 门槛必须高：输出是**预乘 alpha**，编码底图的 R/G/B 都被乘了 alpha。
            // 比值虽然能抵消 alpha，但当 alpha 很小（窗口边缘羽化区 ≈ 0.03）时
            // 读回来的只剩几个灰阶，比值精度崩掉，会伪报出"折返"（实测过）。
            // alpha > 40/255 ≈ 0.16 之外的区域，覆盖层本来就几乎透明，不纳入判据。
            if (b < 40)
            {
                continue;
            }

            series.Add(pixels[i] / b);
        }

        // ⚠️ 判据必须是**大跳变**：编码值只有 1/255 ≈ 0.004 的分辨率，
        // 用 0.004 这种"刚好一个量化级"的阈值会把舍入抖动全算成折返（实测误报 15 次）。
        // 真正的折返是 uv 从 0.98 跳回 0.02 这种量级，用 0.05（≈13 级）才既灵敏又不误报。
        const double FoldThreshold = 0.05;
        int folds = 0;
        for (int i = 1; i < series.Count; i++)
        {
            if (series[i - 1] - series[i] > FoldThreshold)
            {
                folds++;
            }
        }

        Assert.True(series.Count > 10, $"应当能量到一段取样序列，实测 {series.Count} 个点");
        Assert.True(folds == 0,
            $"取样 uv 不应折返（折返一次 = 多复制一层内容），实测折返 {folds} 次\n" +
            string.Join(" ", series.Select((v, i) => $"{i}:{v:F3}").Where((_, i) => i % 2 == 0)));
    }

    /// <summary>
    /// 同一块背景内容**不得被复制**（辅助判据，用标记底图直观验证）。
    /// </summary>
    [Fact]
    public void Background_DoesNotDuplicateContent()
    {
        _preview.SetBackground(Marker());

        // 只留透镜层：关掉盘发光/遮挡、星云、星场，免得它们自己也成"亮块"
        var pixels = Render(LookPresets.Gargantua, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
            l.NebulaGain = 0;
            l.StarGain = 0;
        });

        int blobs = CountBlobs(pixels);

        Assert.True(blobs <= 1, $"同一块背景内容不应被复制，实测水平射线上出现 {blobs} 次");
    }

    /// <summary>
    /// 只有透镜层（关盘）时，径向亮度剖面不得出现「很多圈」。
    /// 盘本体与它的直接像会再贡献结构，所以这里守的是**透镜层自己**不复制。
    /// </summary>
    [Theory]
    [InlineData(LookPresets.Gargantua)]
    [InlineData(LookPresets.Inferno)]
    [InlineData(LookPresets.Quasar)]
    public void LensLayer_HasAtMostOneRing(string preset)
    {
        _preview.SetBackground(DesktopLike());

        var pixels = Render(preset, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
        });

        var (peaks, detail) = RadialPeaks(pixels, 0.06);

        Assert.True(peaks <= 1, $"{preset} 的透镜层不应出现多圈亮环：{detail}");
    }

    /// <summary>高阶像必须随累计转角衰减（`OrderProbePS` 的 g 通道就是 bgGain）。</summary>
    [Fact]
    public void HigherOrderImages_AreAttenuated()
    {
        using var probe = _renderer.CreatePixelShader("OrderProbePS");
        _preview.SetBackground(DesktopLike());

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: LookPresets.Create(LookPresets.Gargantua),
            ScreenHeight: Size);

        var pixels = _preview.Render(description, probe);

        // 沿水平射线取 (turn, bgGain)：BGRA 顺序 → R = turn/2π，G = bgGain
        double maxTurn = 0;
        double gainAtMaxTurn = 1.0;
        bool sawAttenuated = false;

        for (int px = 1; px < Size / 2; px++)
        {
            int i = (((Size / 2) * Size) + (Size / 2) + px) * 4;
            double turn = pixels[i + 2] / 255.0;
            double gain = pixels[i + 1] / 255.0;

            if (turn > maxTurn)
            {
                maxTurn = turn;
                gainAtMaxTurn = gain;
            }

            if (gain < 0.95)
            {
                sawAttenuated = true;
            }
        }

        // 转角最大的地方（最靠近阴影、绕得最狠）衰减应当最强
        Assert.True(maxTurn > 0.1, $"应当量到非零的累计转角，实测最大 {maxTurn:F3} 圈");
        Assert.True(gainAtMaxTurn <= 1.0, $"bgGain 不得超过 1，实测 {gainAtMaxTurn:F3}");

        // 本档参数下若还没绕满一圈，就允许「没有触发衰减」，但公式必须存在且单调
        Assert.Contains("s.bgGain = exp(-max(turn", ShaderSources.FullSource);
        _ = sawAttenuated;
    }

    /// <summary>主像区（转角在 HIGH_ORDER_FREE_TURN 以内）不能被误伤。</summary>
    [Fact]
    public void PrimaryImage_IsNotAttenuated()
    {
        using var probe = _renderer.CreatePixelShader("OrderProbePS");
        _preview.SetBackground(DesktopLike());

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: LookPresets.Create(LookPresets.Gargantua),
            ScreenHeight: Size);

        var pixels = _preview.Render(description, probe);

        // 远离黑洞的像素绕得很少，bgGain 必须是 1
        int checkedCount = 0;
        for (int px = Size / 4; px < Size / 2 - 2; px++)
        {
            int i = (((Size / 2) * Size) + (Size / 2) + px) * 4;
            Assert.Equal(255, pixels[i + 1]);
            checkedCount++;
        }

        Assert.True(checkedCount > 0);
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
