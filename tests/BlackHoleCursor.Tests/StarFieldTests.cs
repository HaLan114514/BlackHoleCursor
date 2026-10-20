using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.7：透镜星场接入主流程 + 曝光 + 质量档位（步数）。
///
/// 星场的隔离方式：底图用**纯黑**，盘关掉（Gain = 0、Opacity = 0），
/// **星云也关掉**（fixes-v2 问题 1 之后它默认常开，不关就不再「只有星点」）——
/// 于是画面里除了星点就没有别的光源 —— 星点数量与亮度可以直接量化。
/// 曝光与色温的断言在 <c>GeodesicCompositeTests</c> / <c>DiskShadingTests</c> 里已覆盖（M1.4.4 / M1.3）。
/// </summary>
public class StarFieldTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public StarFieldTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _preview.SetBackground(CreateSolidBackground(0, 0, 0));
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

    private byte[] Render(double starGain, double holeSize = 0.10, int steps = 48)
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        look.StarGain = starGain;
        look.HoleSize = holeSize;
        look.Gain = 0.0;        // 关掉盘发光
        look.Opacity = 0.0;     // 关掉盘遮挡
        look.NebulaGain = 0.0;  // fixes-v2 问题 1：星云是**常开**的，隔离星场时必须一并关掉
        look.StepCount = steps;

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    private static double Brightness(byte[] buffer, int i)
        => (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;

    private static int CountBrightPixels(byte[] buffer, double threshold)
    {
        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (Brightness(buffer, i) > threshold)
            {
                count++;
            }
        }

        return count;
    }

    private static double TotalBrightness(byte[] buffer)
    {
        double total = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            total += Brightness(buffer, i);
        }

        return total;
    }

    [Fact]
    public void Stars_AreWiredIntoTheMainFlow()
    {
        // 定义早就有了（M1.1），这里保证它真的被主流程调用
        Assert.Contains("s.star = stars(", ShaderSources.FullSource);
        Assert.Contains("bg += s.star;", ShaderSources.FullSource);

        Assert.Equal(Size * Size * 4, Render(0.0).Length);
    }

    /// <summary>star 增益为 0 时，纯黑底图上不应出现任何星点。</summary>
    [Fact]
    public void StarGainZero_ShowsNothing()
    {
        int bright = CountBrightPixels(Render(0.0), 8.0);

        Assert.True(bright == 0, $"star=0 时不应有任何星点，实测亮像素 {bright}");
    }

    /// <summary>
    /// star=1 时应该出现星点，而且是**稀疏**的（不是整片提亮）。
    /// 程序化星场每格只有约 8% 的概率出星，且每颗只占 1~2 px。
    /// </summary>
    [Fact]
    public void StarGainOne_ShowsSparsePoints()
    {
        var pixels = Render(1.0);
        int bright = CountBrightPixels(pixels, 15.0);

        Assert.True(bright > 0, "star=1 时应能看到星点");
        Assert.True(bright < Size * Size * 0.08,
            $"星点应是稀疏的，实测亮像素 {bright} / {Size * Size}");
    }

    /// <summary>增益越大越亮（星场强度确实受 StarGain 控制）。</summary>
    [Fact]
    public void HigherStarGain_IsBrighter()
    {
        double dim = TotalBrightness(Render(0.3));
        double bright = TotalBrightness(Render(1.0));

        Assert.True(bright > dim * 1.5, $"star 0.3 → 1.0 的总亮度应显著上升：{dim:F0} → {bright:F0}");
    }

    /// <summary>星场不得漏进阴影里（落入视界的光线不产生星点）。</summary>
    [Fact]
    public void Stars_DoNotLeakIntoTheShadow()
    {
        var pixels = Render(1.0);
        int center = ((Size / 2) * Size + Size / 2) * 4;

        Assert.True(Brightness(pixels, center) <= 8,
            $"阴影中心应仍为纯黑，实测 {Brightness(pixels, center):F1}");
    }

    /// <summary>三档质量（24 / 48 / 88 步）都要能正常渲染，且阴影依旧是黑的。</summary>
    [Theory]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(88)]
    public void AllQualitySteps_Render(int steps)
    {
        var pixels = Render(1.0, steps: steps);

        Assert.Equal(Size * Size * 4, pixels.Length);
        Assert.True(Brightness(pixels, ((Size / 2) * Size + Size / 2) * 4) <= 8, "阴影中心应为纯黑");
        Assert.True(CountBrightPixels(pixels, 15.0) > 0, $"{steps} 步时应仍能看到星点");
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
