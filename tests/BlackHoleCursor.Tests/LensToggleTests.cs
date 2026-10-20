using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 **问题 4**：关闭引力透镜后，黑洞周边出现一大圈黑色。
///
/// 根因（`composite.hlsl`）：`alpha = max(s.window, diskAlpha)` 里的 `s.window`
/// 描述的是**折射层的覆盖范围**（铺到约 7 个阴影半径），而折射层的**内容** `bg`
/// 却被 `Timing.w > 0.5` 门控。关掉透镜后内容归零、范围仍在 ——
/// 覆盖层就用一个「没有内容的窗口」把桌面涂黑了。
///
/// 修复：`bgAlpha` 与 `bg` 同生共死；阴影改用 `captured` 单独保证不透明
/// （它属于黑洞本体，不该靠 `window` 间接变黑 —— 那会让黑的范围扩大到整个圆盘）。
/// </summary>
public class LensToggleTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public LensToggleTests()
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

    private byte[] Render(bool lensEnabled, Action<BlackHoleLook>? configure = null)
    {
        _preview.SetBackground(CreateTexture((_, _) => 0xFFFFFFFFu));   // 纯白桌面：黑圈最容易被看出来

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.HoleSize = 0.06;
        look.StepCount = 48;
        configure?.Invoke(look);

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, lensEnabled,
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description);
    }

    /// <summary>半径 radius（单位：阴影半径）那一圈像素的平均 alpha。</summary>
    private static double MeanAlpha(byte[] buffer, double radius)
    {
        double shadowRadius = 0.06 * Size;
        double total = 0;
        int count = 0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - Size / 2.0;
                double dy = y + 0.5 - Size / 2.0;
                double r = Math.Sqrt((dx * dx) + (dy * dy)) / shadowRadius;

                if (Math.Abs(r - radius) < 0.08)
                {
                    total += buffer[(((y * Size) + x) * 4) + 3];
                    count++;
                }
            }
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// 关掉透镜后，阴影之外**不得再占 alpha** —— 否则就是把桌面涂成黑圈。
    /// 盘 / 星云 / 星场全关，这样画面里除了阴影没有任何自发光能掩盖问题。
    /// </summary>
    [Fact]
    public void LensOff_DoesNotBlanketTheDesktop()
    {
        var pixels = Render(lensEnabled: false, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
            l.NebulaGain = 0;
            l.StarGain = 0;
        });

        double at2 = MeanAlpha(pixels, 2.0);
        double at4 = MeanAlpha(pixels, 4.0);
        double at6 = MeanAlpha(pixels, 6.0);

        Assert.True(at2 < 8, $"关透镜后 2 个阴影半径处不应再覆盖桌面，实测 alpha={at2:F1}");
        Assert.True(at4 < 8, $"关透镜后 4 个阴影半径处不应再覆盖桌面，实测 alpha={at4:F1}");
        Assert.True(at6 < 8, $"关透镜后 6 个阴影半径处不应再覆盖桌面，实测 alpha={at6:F1}");
    }

    /// <summary>关掉透镜后，阴影本身仍然是**不透明**的黑（它属于黑洞本体）。</summary>
    [Fact]
    public void LensOff_StillShowsAnOpaqueShadow()
    {
        var pixels = Render(lensEnabled: false, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
            l.NebulaGain = 0;
            l.StarGain = 0;
        });

        int i = (((Size / 2) * Size) + Size / 2) * 4;

        Assert.True(pixels[i + 3] > 200, $"阴影必须仍是不透明的，实测 alpha={pixels[i + 3]}");
        Assert.True(pixels[i] <= 8 && pixels[i + 1] <= 8 && pixels[i + 2] <= 8, "阴影必须是黑的");
    }

    /// <summary>
    /// 开着透镜时行为**不变**：折射层仍按 `window` 覆盖桌面。
    /// 这条同时保证上一条不是靠「alpha 整体归零」蒙混过关的。
    /// </summary>
    [Fact]
    public void LensOn_StillCoversTheDesktop()
    {
        var pixels = Render(lensEnabled: true, l =>
        {
            l.Gain = 0;
            l.Opacity = 0;
            l.NebulaGain = 0;
            l.StarGain = 0;
        });

        double at2 = MeanAlpha(pixels, 2.0);
        double at4 = MeanAlpha(pixels, 4.0);

        Assert.True(at2 > 100, $"开透镜时 2 个阴影半径处应仍被折射层覆盖，实测 alpha={at2:F1}");
        Assert.True(at4 > 20, $"开透镜时 4 个阴影半径处应仍有一定覆盖，实测 alpha={at4:F1}");
    }

    /// <summary>关透镜时盘的炫彩（自发光）要保留 —— 它不依赖抓屏。</summary>
    [Fact]
    public void LensOff_KeepsTheDiskGlow()
    {
        var off = Render(lensEnabled: false);

        double maxBrightness = 0;
        for (int i = 0; i < off.Length; i += 4)
        {
            maxBrightness = Math.Max(maxBrightness, (off[i] + off[i + 1] + off[i + 2]) / 3.0);
        }

        Assert.True(maxBrightness > 60, $"关透镜后盘的自发光应当仍在，实测全图最亮 {maxBrightness:F1}");
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
