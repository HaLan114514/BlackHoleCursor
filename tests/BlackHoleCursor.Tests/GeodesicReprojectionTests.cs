using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M1.4.2：逃逸光线反投影到桌面天空平面（爱因斯坦环 / 镜像二次像）。
///
/// 背景用一张「坐标编码」纹理：R = x 渐变、G = y 渐变、B = 常量 255。
/// 于是 R/B 与 G/B 就是该像素实际取样的 uv —— 且这个比值不受
/// 反投影淡出系数 toward 的整体缩放影响，可以直接反推出位移量。
/// </summary>
public class GeodesicReprojectionTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;
    private readonly ID3D11ShaderResourceView _gradient;

    public GeodesicReprojectionTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
        _gradient = CreateCoordinateTexture();
        _preview.SetBackground(_gradient);
    }

    /// <summary>R = uv.x·255，G = uv.y·255，B = 255（作为分母，使比值不受整体缩放影响）。</summary>
    private ID3D11ShaderResourceView CreateCoordinateTexture()
    {
        var pixels = new uint[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                byte r = (byte)(x * 255 / (Size - 1));
                byte g = (byte)(y * 255 / (Size - 1));

                // BGRA8 的字节顺序是 B,G,R,A —— 这里要让「R 通道 = x 渐变、
                // G 通道 = y 渐变、B 通道 = 常量 255」
                pixels[y * Size + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | 255u;
            }
        }

        var texture = _device.Device.CreateTexture2D(
            pixels,
            Format.B8G8R8A8_UNorm,
            (uint)Size, (uint)Size, 1, 1,
            BindFlags.ShaderResource,
            ResourceOptionFlags.None,
            ResourceUsage.Immutable,
            CpuAccessFlags.None);

        return _device.Device.CreateShaderResourceView(texture, null);
    }

    /// <param name="disableReprojection">true 时 Debug.x &lt; 0，着色器直接按原位置取样。</param>
    private byte[] Render(bool disableReprojection = false, double holeSize = 0.05, int steps = 48)
    {
        using var pixelShader = _renderer.CreatePixelShader("GeodesicProbePS");
        var look = new BlackHoleLook { HoleSize = holeSize, StepCount = steps, Roll = 0.0 };
        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Probe: new ShaderProbe(TemperatureKelvin: disableReprojection ? -1 : 5500),
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description, pixelShader);
    }

    private static (double X, double Y) SampledUv(byte[] buffer, int x, int y)
    {
        int i = (y * Size + x) * 4;
        double b = buffer[i];
        double g = buffer[i + 1];
        double r = buffer[i + 2];

        if (b < 20)
        {
            return (-1, -1);   // 被淡出到几乎全黑，无法反推
        }

        return (r / b, g / b);
    }

    /// <summary>该像素的取样位置与它自身位置的偏移（像素）。无法反推时返回 -1。</summary>
    private static double DisplacementAt(byte[] buffer, int x, int y)
    {
        var sampled = SampledUv(buffer, x, y);
        if (sampled.X < 0)
        {
            return -1;
        }

        double uvx = (x + 0.5) / Size;
        double uvy = (y + 0.5) / Size;

        double dx = sampled.X - uvx;
        double dy = sampled.Y - uvy;
        return Math.Sqrt(dx * dx + dy * dy) * Size;
    }

    private static double MeanDisplacement(byte[] buffer, int cx, int cy, int fromRadius, int toRadius)
    {
        double total = 0;
        int count = 0;

        for (int radius = fromRadius; radius <= toRadius; radius++)
        {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int x = cx + dx * radius;
                int y = cy + dy * radius;
                if (x < 0 || y < 0 || x >= Size || y >= Size)
                {
                    continue;
                }

                double d = DisplacementAt(buffer, x, y);
                if (d >= 0)
                {
                    total += d;
                    count++;
                }
            }
        }

        return count == 0 ? 0 : total / count;
    }

    [Fact]
    public void ReprojectToSky_IsPresentInShader()
    {
        Assert.Contains("float2 ReprojectToSky(", ShaderSources.FullSource);
        using var ps = _renderer.CreatePixelShader("GeodesicProbePS");
        Assert.NotNull(ps);
    }

    [Fact]
    public void WithoutReprojection_SamplingMatchesPixelPosition()
    {
        var pixels = Render(disableReprojection: true);

        double displacement = MeanDisplacement(pixels, Size / 2, Size / 2, 60, 100);

        Assert.True(displacement < 0.5,
            $"关闭反投影时应按原位置取样，位移应≈0，实测 {displacement:F3}px");
    }

    [Fact]
    public void Reprojection_ChangesTheImage()
    {
        var on = Render();
        var off = Render(disableReprojection: true);

        int difference = on.Where((b, i) => b != off[i]).Count();

        Assert.True(difference > 100, $"开启反投影后画面应发生明显变化，实测不同字节 {difference}");
    }

    [Fact]
    public void Displacement_DecreasesWithDistance()
    {
        var pixels = Render();
        int c = Size / 2;
        int rh = (int)LensMath.ShadowRadiusPixels(0.05, Size);   // ≈ 13px

        double near = MeanDisplacement(pixels, c, c, rh * 2, rh * 2 + 2);
        double middle = MeanDisplacement(pixels, c, c, rh * 4, rh * 4 + 2);
        double far = MeanDisplacement(pixels, c, c, rh * 8, rh * 8 + 2);

        Assert.True(near > middle, $"位移应随距离衰减（近 {near:F2} vs 中 {middle:F2}）");
        Assert.True(middle > far, $"位移应随距离衰减（中 {middle:F2} vs 远 {far:F2}）");
    }

    [Fact]
    public void FarFieldDisplacement_IsSmallComparedToNearField()
    {
        // 远场仍有约 2~3px 的位移（弱场偏折本身不为零，且 sky 平面距离会放大它），
        // M1.4.4 的远场淡出窗口会把它进一步压下去。这里只要求它远小于近场。
        var pixels = Render();
        int c = Size / 2;
        int rh = (int)LensMath.ShadowRadiusPixels(0.05, Size);

        double near = MeanDisplacement(pixels, c, c, rh * 2, rh * 2 + 2);
        double far = MeanDisplacement(pixels, c, c, rh * 9, rh * 10);

        Assert.True(far * 5 < near,
            $"远场位移应远小于近场（近 {near:F2}px vs 远 {far:F2}px）");
    }

    [Fact]
    public void NearRingDisplacement_IsMuchLargerThanFarField()
    {
        var pixels = Render();
        int c = Size / 2;
        int rh = (int)LensMath.ShadowRadiusPixels(0.05, Size);

        double nearRing = MeanDisplacement(pixels, c, c, (int)(rh * 1.5), (int)(rh * 2.0));
        double farField = MeanDisplacement(pixels, c, c, rh * 7, rh * 8);

        Assert.True(nearRing > farField * 3,
            $"环附近的弯折应远强于远场（环 {nearRing:F2}px vs 远场 {farField:F2}px）");
    }

    [Fact]
    public void EinsteinRing_SamplesFromTheOppositeSide()
    {
        // 镜像二次像：紧贴阴影外侧的光线绕黑洞半圈后飞出，
        // 它取到的背景应该在黑洞的「另一边」。
        var pixels = Render();
        int c = Size / 2;
        int rh = (int)LensMath.ShadowRadiusPixels(0.05, Size);

        bool foundMirrored = false;
        for (int radius = rh + 2; radius <= (int)(rh * 2.2); radius++)
        {
            int x = c + radius;                 // 黑洞右侧
            var sampled = SampledUv(pixels, x, c);
            if (sampled.X >= 0 && sampled.X * Size < c - rh)
            {
                foundMirrored = true;
                break;
            }
        }

        Assert.True(foundMirrored, "环外侧应存在取到黑洞另一侧的镜像二次像");
    }

    [Fact]
    public void LargerHole_BendsMoreAtTheSameScreenDistance()
    {
        int c = Size / 2;

        var small = Render(holeSize: 0.04);
        var large = Render(holeSize: 0.12);

        double smallDisplacement = MeanDisplacement(small, c, c, 40, 44);
        double largeDisplacement = MeanDisplacement(large, c, c, 40, 44);

        Assert.True(largeDisplacement > smallDisplacement,
            $"更大的黑洞在同样的屏幕距离上应弯折更强（{smallDisplacement:F2} → {largeDisplacement:F2}）");
    }

    [Fact]
    public void StepCount_DoesNotBreakReprojection()
    {
        var coarse = Render(steps: 24);
        var fine = Render(steps: 88);

        int difference = coarse.Where((b, i) => b != fine[i]).Count();
        int total = coarse.Length;

        Assert.True((double)difference / total < 0.25,
            $"24 步与 88 步的结果应大体一致，实测差异 {100.0 * difference / total:F1}%");
    }

    public void Dispose()
    {
        _gradient.Dispose();
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
