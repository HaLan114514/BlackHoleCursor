using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 问题 8：给**星空**与**星云**都加上变化 ——
/// 静止时随时间变化，移动时随时间 + 随光标位置变化。
///
/// 隔离方式与 <c>StarFieldTests</c> 相同：纯黑底图 + 关掉盘（Gain = 0、Opacity = 0），
/// 再靠 `NebulaGain` / `StarGain` 二选一，把两层分开量。
/// </summary>
public class ParallaxTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public ParallaxTests()
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

    /// <param name="parallax">光标位置（屏幕坐标）；内部按 ScreenHeight 归一化。</param>
    /// <param name="diskSpeed">盘速度；为 0 时 <c>Timing.x</c> 会被门控冻结（<c>Timing.y</c> 不受影响）。</param>
    private byte[] Render(
        double nebulaGain,
        double starGain,
        double timeSeconds = 0.0,
        double parallaxX = 0.0,
        double parallaxY = 0.0,
        double diskSpeed = 5.0)
    {
        var look = LookPresets.Create(LookPresets.Gargantua);
        look.NebulaGain = nebulaGain;
        look.StarGain = starGain;
        look.Gain = 0.0;        // 关掉盘发光
        look.Opacity = 0.0;     // 关掉盘遮挡
        look.StepCount = 48;

        // 门控看的是 SettingsModel.DiskSpeed（不是 look.Speed）
        var settings = new SettingsModel { DiskSpeed = diskSpeed };

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            settings, timeSeconds, true,
            Look: look,
            ScreenHeight: Size,
            ParallaxX: parallaxX,
            ParallaxY: parallaxY);

        return _preview.Render(description);
    }

    /// <summary>两幅图的平均像素距离（灰阶 0~255）。适合量星云这种成片的变化。</summary>
    private static double MeanDifference(byte[] a, byte[] b)
    {
        double total = 0;
        int count = 0;

        for (int i = 0; i < a.Length; i += 4)
        {
            total += (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0;
            count++;
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// 两幅图里变化超过阈值的像素数。星点是**稀疏**的，
    /// 平均距离会被大量没变的背景稀释，必须数像素。
    /// </summary>
    private static int ChangedPixels(byte[] a, byte[] b, double threshold = 4.0)
    {
        int count = 0;

        for (int i = 0; i < a.Length; i += 4)
        {
            double diff = (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0;
            if (diff > threshold)
            {
                count++;
            }
        }

        return count;
    }

    // -----------------------------------------------------------------------
    // 星云
    // -----------------------------------------------------------------------

    /// <summary>移动鼠标时，星云图案应随位置变化（而不是钉在天球上不动）。</summary>
    [Fact]
    public void Nebula_ChangesWithCursorPosition()
    {
        byte[] atOrigin = Render(nebulaGain: 1.0, starGain: 0.0, parallaxX: 0.0);
        byte[] moved = Render(nebulaGain: 1.0, starGain: 0.0, parallaxX: Size * 1.5);

        double diff = MeanDifference(atOrigin, moved);

        Assert.True(diff > 1.0,
            $"星云应随光标位置变化：原点 vs 横移 1.5 个屏高，平均像素差 {diff:F2}");
    }

    /// <summary>静止（位置不变）时，星云应随时间缓慢演化（问题 4 已做，这里补断言）。</summary>
    [Fact]
    public void Nebula_ChangesWithTimeWhileStill()
    {
        byte[] t0 = Render(nebulaGain: 1.0, starGain: 0.0, timeSeconds: 0.0);
        byte[] t12 = Render(nebulaGain: 1.0, starGain: 0.0, timeSeconds: 12.0);

        double diff = MeanDifference(t0, t12);

        Assert.True(diff > 0.5,
            $"静止时星云应随时间变化：0s vs 12s 平均像素差 {diff:F2}");
    }

    // -----------------------------------------------------------------------
    // 星空
    // -----------------------------------------------------------------------

    /// <summary>移动鼠标时，星点应缓缓掠过。</summary>
    [Fact]
    public void Stars_ChangeWithCursorPosition()
    {
        byte[] atOrigin = Render(nebulaGain: 0.0, starGain: 1.0, parallaxX: 0.0);
        byte[] moved = Render(nebulaGain: 0.0, starGain: 1.0, parallaxX: Size * 1.5);

        // 实测约 131 px：星点约 40 颗、每颗 1~3 px，横移 1.5 个屏高后基本整体挪位。
        // 阈值取 20 —— 远高于量化噪声，又留足余量给系数微调。
        int changed = ChangedPixels(atOrigin, moved);

        Assert.True(changed > 20,
            $"星空应随光标位置变化：原点 vs 横移 1.5 个屏高，变化像素 {changed}");
    }

    /// <summary>
    /// 星空闪烁**不应**被盘速度门控 —— 原来它用 <c>Timing.x</c>，
    /// 盘速设 0 时时间被冻结，星星就全都不闪了，恰好违反「静止时也要随时间变化」。
    /// 现在改用 <c>Timing.y</c>（不受门控）。
    /// </summary>
    [Fact]
    public void Stars_TwinkleEvenWhenDiskSpeedIsZero()
    {
        byte[] t0 = Render(nebulaGain: 0.0, starGain: 1.0, timeSeconds: 0.0, diskSpeed: 0.0);
        byte[] t8 = Render(nebulaGain: 0.0, starGain: 1.0, timeSeconds: 8.0, diskSpeed: 0.0);

        // 实测约 39 px（阈值 2 灰阶）。只有约 40 颗星，闪烁幅度 0.7~1.0 倍，
        // 所以变化像素数与星点数量同量级 —— 阈值取 10 足以区分「有闪」和「没闪」。
        int changed = ChangedPixels(t0, t8, threshold: 2.0);

        Assert.True(changed > 10,
            $"盘速为 0（时间被门控冻结）时星空仍应闪烁：0s vs 8s 变化像素 {changed}");
    }

    /// <summary>位置不变、时间不变 → 画面必须完全一致（防上面的断言靠噪声蒙混过关）。</summary>
    [Fact]
    public void SamePositionAndTime_IsIdentical()
    {
        byte[] a = Render(nebulaGain: 1.0, starGain: 1.0, timeSeconds: 3.0, parallaxX: 100.0);
        byte[] b = Render(nebulaGain: 1.0, starGain: 1.0, timeSeconds: 3.0, parallaxX: 100.0);

        Assert.Equal(0, ChangedPixels(a, b, threshold: 0.5));
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
