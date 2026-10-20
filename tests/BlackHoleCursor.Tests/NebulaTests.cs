using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 **问题 1**：暗背景（甚至抓屏被判全黑）时黑洞的光效大幅减少甚至消失。
///
/// 修法是补一层**程序化背景光**（星云 / 辉光）：它是自发光，不依赖抓屏内容，
/// 但仍按弯折后的光线方向索引 —— 所以会被透镜弯折、被盘遮挡，且不漏进阴影。
///
/// 隔离方式（沿用 M1.4.3 / M1.7 的手法）：底图纯黑、盘关掉（Gain=0、Opacity=0）、
/// 星场关掉（StarGain=0）—— 于是画面里唯一的光源就是这层星云，可直接量化。
/// </summary>
public class NebulaTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _black;
    private readonly ID3D11ShaderResourceView _white;

    public NebulaTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _black = CreateSolidBackground(0, 0, 0);
        _white = CreateSolidBackground(255, 255, 255);
    }

    private ID3D11ShaderResourceView CreateSolidBackground(byte r, byte g, byte b)
    {
        var pixels = new uint[Size * Size];
        uint value = 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
        Array.Fill(pixels, value);

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

    /// <param name="background">null = 纯黑（模拟「桌面上什么都没有」的最坏情况）。</param>
    private byte[] Render(
        double nebulaGain,
        ID3D11ShaderResourceView? background = null,
        double diskOpacity = 0.0,
        double holeSize = 0.10,
        double timeSeconds = 0.0,
        double diskSpeed = 5.0,
        System.Drawing.Color? nebulaColor = null)
    {
        _preview.SetBackground(background ?? _black);

        var look = LookPresets.Create(LookPresets.Gargantua);
        if (nebulaColor.HasValue)
        {
            look.NebulaColor = nebulaColor.Value;
        }

        look.NebulaGain = nebulaGain;
        look.HoleSize = holeSize;
        look.Gain = 0.0;            // 关掉盘发光
        look.Opacity = diskOpacity; // 默认也关掉盘遮挡，单独测遮挡时再开
        look.StarGain = 0.0;        // 关掉星场，只留星云
        look.StepCount = 48;

        var settings = new SettingsModel { DiskSpeed = diskSpeed };

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            settings, timeSeconds, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    /// <summary>两张图之间的平均像素差（0–255）。</summary>
    private static double MeanDifference(byte[] a, byte[] b)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(Brightness(a, i) - Brightness(b, i));
            count++;
        }

        return sum / count;
    }

    private static double Brightness(byte[] buffer, int i)
        => (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;

    private static double TotalBrightness(byte[] buffer)
    {
        double total = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            total += Brightness(buffer, i);
        }

        return total;
    }

    /// <summary>
    /// 环带的平均亮度。默认取 1.5~3.5 个阴影半径 —— 这是「光效层次」所在的区域，
    /// 且 alpha ≈ 1（窗口边缘羽化从 3.5 个阴影半径才开始），不会被 alpha 稀释。
    /// </summary>
    private double AnnulusBrightness(byte[] buffer, double holeSize, double innerRadii = 1.5, double outerRadii = 3.5)
    {
        double radius = holeSize * Size;     // ScreenHeight = Size
        double inner = radius * innerRadii;
        double outer = radius * outerRadii;
        double sum = 0;
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double d = Math.Sqrt(((x - Size / 2.0) * (x - Size / 2.0)) + ((y - Size / 2.0) * (y - Size / 2.0)));
                if (d < inner || d > outer)
                {
                    continue;
                }

                sum += Brightness(buffer, ((y * Size) + x) * 4);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    [Fact]
    public void Nebula_IsWiredIntoTheMainFlow()
    {
        string source = ShaderSources.FullSource;

        // 两个分支（远场解析 / 近场积分）都要给星云，否则一侧会突然没光
        Assert.Contains("s.nebula = nebula(", source);
        Assert.Contains("(bg + ambient) * s.trans", source);

        // 关键：**必须**在 Timing.w 门之外（自发光不能被「是否抓屏」掐掉）
        Assert.Contains("float3 ambient = s.nebula;", source);
        int gate = source.IndexOf("if (!s.captured && Timing.w > 0.5)", StringComparison.Ordinal);
        int ambient = source.IndexOf("float3 ambient = s.nebula;", StringComparison.Ordinal);
        Assert.True(gate >= 0 && ambient > gate, "星云必须在抓屏开关的判断之后（门之外）才生效");
        Assert.True(
            ambient > source.IndexOf("bg += s.star;", StringComparison.Ordinal),
            "星云要放在星场之后，也就是门体之外");
    }

    [Fact]
    public void NebulaGainZero_ShowsNothingOnABlackDesktop()
    {
        var pixels = Render(0.0);

        Assert.Equal(0.0, TotalBrightness(pixels), 1);
    }

    /// <summary>这条就是问题 1 本身：最坏情况下（桌面纯黑）也必须看得见光，不能塌成一个孤盘。</summary>
    [Fact]
    public void ADarkDesktopStillShowsLight()
    {
        double off = AnnulusBrightness(Render(0.0), 0.10);
        double on = AnnulusBrightness(Render(0.35), 0.10);

        Assert.True(off < 1.0, $"星云关掉时纯黑桌面上确实没有光（实测 {off:F2}）—— 这就是问题 1 的现象");
        Assert.True(on > 8.0, $"星云常开后暗桌面也该有可见的光效层次（实测环带亮度 {on:F2}）");
    }

    [Fact]
    public void HigherNebulaGain_IsBrighter()
    {
        double dim = TotalBrightness(Render(0.15));
        double bright = TotalBrightness(Render(0.60));

        Assert.True(bright > dim * 1.8, $"星云强度 0.15 → 0.60 的总亮度应显著上升：{dim:F0} → {bright:F0}");
    }

    /// <summary>落入视界的光线不能出光，否则阴影就不纯黑了（约束 4）。</summary>
    [Fact]
    public void Nebula_DoesNotLeakIntoTheShadow()
    {
        var pixels = Render(1.0);
        int center = ((Size / 2) * Size + Size / 2) * 4;

        Assert.True(Brightness(pixels, center) <= 8,
            $"阴影中心应仍为纯黑，实测 {Brightness(pixels, center):F1}");
    }

    /// <summary>盘前面的星云要被盘挡住（约束 3：星云必须乘 trans）。</summary>
    [Fact]
    public void Nebula_IsOccludedByTheDisk()
    {
        double transparent = TotalBrightness(Render(0.8, diskOpacity: 0.0));
        double opaque = TotalBrightness(Render(0.8, diskOpacity: 1.0));

        Assert.True(opaque < transparent,
            $"盘不透明时应挡住它前面的星云：{transparent:F0} → {opaque:F0}");
    }

    /// <summary>
    /// 亮桌面不该被这层光「弄脏」：星云填的是本来就空着的地方（alpha≈1 的内区），
    /// 亮桌面下它的增量应当远小于桌面自身亮度。
    /// </summary>
    [Fact]
    public void ABrightDesktopIsNotWashedOut()
    {
        double off = AnnulusBrightness(Render(0.0, _white), 0.10);
        double on = AnnulusBrightness(Render(0.35, _white), 0.10);

        // alpha ≈ 1 的内区：桌面本就被完全盖住，星云填的是本来就空着的地方。
        // 实测增量约 +5%（220 → 233）：可感但不至于「雾蒙蒙」，用**相对**增量断言。
        Assert.True(off > 180, $"白底图下环带本就该很亮（实测 {off:F1}）");
        Assert.True(on - off < off * 0.10,
            $"白底图上叠加的星云不该把画面明显提亮：{off:F1} → {on:F1}（+{(on - off) / off:P1}）");
    }

    /// <summary>远场淡出窗口同样作用在星云上，窗口边缘不能留一圈硬边。</summary>
    [Fact]
    public void Nebula_FadesTowardTheWindowEdge()
    {
        var pixels = Render(1.0);

        // 视口角落：plen 远大于 7 个阴影半径，window ≈ 0
        int corner = 4;
        Assert.True(Brightness(pixels, corner) < 6,
            $"窗口边缘应已淡出，实测角落亮度 {Brightness(pixels, corner):F1}");
    }

    /// <summary>同一个时刻渲染两次必须完全一致（否则时间项引入了随机性）。</summary>
    [Fact]
    public void Nebula_IsDeterministicForTheSameTime()
    {
        var first = Render(0.45, timeSeconds: 12.0);
        var second = Render(0.45, timeSeconds: 12.0);

        Assert.Equal(0.0, MeanDifference(first, second), 6);
    }

    /// <summary>
    /// fixes-v2 问题 4：炫彩要「时间上的流动」—— 鼠标不动、画面也不能完全静止。
    /// 同时要求它**缓慢**：差异要明显存在，但不能大到像闪屏。
    /// </summary>
    [Fact]
    public void Nebula_EvolvesOverTime()
    {
        var atStart = Render(0.45, timeSeconds: 0.0);
        var later = Render(0.45, timeSeconds: 25.0);

        double diff = MeanDifference(atStart, later);

        Assert.True(diff > 0.5, $"星云必须随时间演化，实测平均像素差 {diff:F2}");
        Assert.True(diff < 40.0, $"演化应当缓慢，不能像闪屏，实测平均像素差 {diff:F2}");
    }

    /// <summary>
    /// 星云的时间走 `Timing.y`（不受盘速度门控）：把盘冻结（DiskSpeed = 0）时，
    /// 星云依旧要缓慢演化 —— 否则「鼠标不动时画面也在动」这条就不成立。
    /// </summary>
    [Fact]
    public void Nebula_KeepsEvolvingWhenTheDiskIsFrozen()
    {
        var atStart = Render(0.45, timeSeconds: 0.0, diskSpeed: 0.0);
        var later = Render(0.45, timeSeconds: 25.0, diskSpeed: 0.0);

        Assert.True(
            MeanDifference(atStart, later) > 0.5,
            "盘冻结（DiskSpeed = 0）时星云仍应继续演化");
    }

    /// <summary>
    /// fixes-v3 问题 3：稀薄处（暗部）不再是一圈饱和的冷蓝。
    ///
    /// 取样「暗但不为 0」的像素 —— 那正是 `cloud` 落在亮度地板（0.12）上的稀薄区，
    /// 也就是全屏之后变成**一大圈**的那片蓝（旧的暗端是写死的 float3(0.26, 0.44, 1.00)）。
    /// </summary>
    [Fact]
    public void TheThinNebulaIsNotBlue()
    {
        var pixels = Render(1.0);

        double r = 0;
        double g = 0;
        double b = 0;
        int count = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            double brightness = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;

            // 排除阴影里的纯黑，也排除浓云（那是亮部，配色由 NebulaTint 决定、本来就该有色）
            if (brightness < 3.0 || brightness > 40.0)
            {
                continue;
            }

            r += pixels[i + 2];   // 缓冲是 BGRA：0 = B、1 = G、2 = R
            g += pixels[i + 1];
            b += pixels[i];
            count++;
        }

        Assert.True(count > 200, $"应当取到足够多的稀薄像素（实测 {count} 个）");

        double meanR = r / count;
        double meanB = b / count;

        Assert.True(
            meanB - meanR <= 8.0,
            $"星云稀薄处不该偏蓝：平均 R={meanR:F1} / G={g / count:F1} / B={meanB:F1}"
            + $"（旧值约 R=7 / B=27，蓝比红高 20）");
    }

    /// <summary>反向保护：去蓝不能顺手把星云改没 —— 稀薄处仍要看得见。</summary>
    [Fact]
    public void TheThinNebulaIsStillVisible()
    {
        var pixels = Render(1.0);

        double sum = 0;
        int count = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            double brightness = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
            if (brightness < 3.0 || brightness > 40.0)
            {
                continue;
            }

            sum += brightness;
            count++;
        }

        Assert.True(count > 200, $"应当取到足够多的稀薄像素（实测 {count} 个）");
        Assert.True(sum / count > 4.0, $"稀薄处仍应有可见的光（实测平均亮度 {sum / count:F1}）");
    }

    /// <summary>
    /// fixes-v2 问题 6：星云亮部配色要真的生效（预设带的色调靠它区分预设）。
    /// </summary>
    [Fact]
    public void NebulaColor_ChangesTheGlowColor()
    {
        var warm = Render(0.45, nebulaColor: System.Drawing.Color.FromArgb(255, 255, 80, 40));
        var cool = Render(0.45, nebulaColor: System.Drawing.Color.FromArgb(255, 80, 140, 255));

        double warmRed = ChannelSum(warm, 2);
        double coolRed = ChannelSum(cool, 2);
        double warmBlue = ChannelSum(warm, 0);
        double coolBlue = ChannelSum(cool, 0);

        Assert.True(warmRed > coolRed * 1.1, $"暖色配色的红通道应明显更高：{warmRed:F0} vs {coolRed:F0}");
        Assert.True(coolBlue > warmBlue * 1.1, $"冷色配色的蓝通道应明显更高：{coolBlue:F0} vs {warmBlue:F0}");
    }

    /// <summary>累加某个通道（缓冲是 BGRA：0 = B、1 = G、2 = R）。</summary>
    private static double ChannelSum(byte[] buffer, int channel)
    {
        double sum = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            sum += buffer[i + channel];
        }

        return sum;
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _black.Dispose();
        _white.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
