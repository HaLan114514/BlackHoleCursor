using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v3 问题 2：星空 / 星云的变化速度太慢，需要**可调速度**。
///
/// 修法是一个「氛围流速」倍率（`AmbientFlowSpeed`，0–3，默认 1），
/// 只乘在**时间项**上（星云平移 / 呼吸 + 星空闪烁），一个滑块同时管两层：
/// 语义是「变化快慢」，不是亮度（亮度由星空强度 / 星云强度各自管）。
///
/// 隔离方式沿用 `NebulaTests`：纯黑底 + 盘关掉 + 只留氛围层，于是画面里的变化只可能来自流速。
/// </summary>
public class AmbientFlowTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _black;

    public AmbientFlowTests()
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

    /// <summary>只留氛围层（星云 + 星空）渲染一帧。</summary>
    private byte[] Render(double flowSpeed, double timeSeconds, double starGain = 0.6, double nebulaGain = 0.45)
    {
        _preview.SetBackground(_black);

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.HoleSize = 0.10;
        look.NebulaGain = nebulaGain;
        look.StarGain = starGain;
        look.Gain = 0.0;        // 关掉盘发光
        look.Opacity = 0.0;     // 关掉盘遮挡
        look.StepCount = 48;

        var settings = new SettingsModel { DiskSpeed = 5.0, AmbientFlowSpeed = flowSpeed };

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

    /// <summary>同样一段真实时间里，画面变化了多少。</summary>
    private double EvolutionOver(double flowSpeed, double seconds)
        => MeanDifference(Render(flowSpeed, 0.0), Render(flowSpeed, seconds));

    [Fact]
    public void Default_IsTheOriginalSpeed()
    {
        Assert.Equal(1.0, new SettingsModel().AmbientFlowSpeed, 6);
    }

    [Fact]
    public void Clamp_KeepsItWithinZeroAndThree()
    {
        var settings = new SettingsModel { AmbientFlowSpeed = 9.0 };
        settings.Clamp();
        Assert.Equal(SettingsRanges.AmbientFlowSpeedMax, settings.AmbientFlowSpeed, 6);

        settings.AmbientFlowSpeed = -2.0;
        settings.Clamp();
        Assert.Equal(SettingsRanges.AmbientFlowSpeedMin, settings.AmbientFlowSpeed, 6);
    }

    /// <summary>参数必须真的送到着色器（`Timing.z`），否则滑块白摆。</summary>
    [Fact]
    public void Builder_PutsTheSpeedInTimingZ()
    {
        var settings = new SettingsModel { AmbientFlowSpeed = 2.5 };

        var constants = SceneParamsBuilder.Build(new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0, settings, 0.0, true));

        Assert.Equal(2.5f, constants.Timing.Z, 5);
    }

    /// <summary>核心判据：流速调大，同样一段真实时间里的变化必须更多。</summary>
    [Fact]
    public void HigherSpeed_EvolvesFaster()
    {
        double normal = EvolutionOver(1.0, 6.0);
        double fast = EvolutionOver(2.5, 6.0);

        Assert.True(normal > 0.5, $"原速下也必须在变化（实测 {normal:F2}）—— 否则这条测不出快慢");
        Assert.True(
            fast > normal * 1.4,
            $"2.5 倍速在同样 6 秒里的变化应明显更多：{normal:F2} → {fast:F2}");
    }

    /// <summary>0 = 冻结：星空不闪、星云不漂（有些用户就是要完全静止的画面）。</summary>
    [Fact]
    public void ZeroSpeed_FreezesTheAmbient()
    {
        Assert.True(EvolutionOver(0.0, 25.0) < 0.5, "流速 0 时氛围层应当完全静止");
    }

    /// <summary>反向保护：调快的是**变化**，不是亮度 —— 画面整体亮度不该跟着变。</summary>
    [Fact]
    public void SpeedDoesNotChangeTheOverallBrightness()
    {
        static double Total(byte[] buffer)
        {
            double sum = 0;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                sum += Brightness(buffer, i);
            }

            return sum;
        }

        double slow = Total(Render(0.5, 12.0));
        double fast = Total(Render(2.5, 12.0));

        // 同一时刻、不同流速下画面的**内容**确实不同，但总亮度只应轻微波动
        // （云絮图案不同带来的差异），不能成倍变化
        Assert.True(
            Math.Abs(fast - slow) < slow * 0.35,
            $"流速不该改变整体亮度：{slow:F0} → {fast:F0}");
    }
}
