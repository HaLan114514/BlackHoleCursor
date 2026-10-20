using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v3 问题 1：透镜边缘的重影模糊（`bugs/边缘bug.jpg`）。
///
/// 根因是**两条淡出曲线的时序错配**：
/// 位移用 `exp(-(plen/7)²)`（到屏幕边还有约 24%），alpha 的羽化却在
/// [0.70·fadeR, fadeR] 就往下掉 —— 于是存在一圈「画面还有偏移、但已经半透明」的环带，
/// 偏移的画面半透明地叠在真实桌面上，就是重影。
///
/// 修法是给位移一个**专用**的淡出窗口 `windowDisp`，让它比 alpha 的羽化**先归零**。
/// 这里用验证入口 <c>LensFadeProbePS</c> 把两条曲线同时读出来，把「顺序」变成断言：
///   r = windowDisp，g = alpha 羽化系数，b = 位移幅度（放大 gain 倍居中在 0.5）。
///
/// ⚠️ 为什么不能直接读 CompositePS 的画面来判断：重影是**观感**，
/// 要判「半透明区有没有偏移」必须同时拿到位移与 alpha 两个数 —— 单看颜色分不出来。
/// </summary>
public class LensFadeOrderingTests : IDisposable
{
    private const int Size = 256;

    /// <summary>
    /// 阴影半径占视口高度的比例。取 0.0625 ⇒ 阴影半径 16px、`fadeR = 0.5/0.0625 = 8` 个阴影半径
    /// = 128px，正好是视口半高 —— 与真机全屏（64.8/1080 ≈ 0.06）同一量级。
    /// </summary>
    private const double HoleSize = 0.0625;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public LensFadeOrderingTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }

    private byte[] Render(double gain)
    {
        using var pixelShader = _renderer.CreatePixelShader("LensFadeProbePS");

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.HoleSize = HoleSize;
        look.Roll = 0.0;   // 位移退化为纯径向，沿一条射线取样即可

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Probe: new ShaderProbe(NoiseScale: gain),
            Look: look,
            ScreenHeight: Size);

        return _preview.Render(description, pixelShader);
    }

    private static (double DispWindow, double Feather, double DispPx) At(byte[] buffer, int x, int y, double gain)
    {
        int i = ((y * Size) + x) * 4;

        // 输出是 BGRA：R = windowDisp，G = 羽化系数，B = 位移幅度
        double dispWindow = buffer[i + 2] / 255.0;
        double feather = buffer[i + 1] / 255.0;
        double dispUv = ((buffer[i] / 255.0) - 0.5) / gain;

        return (dispWindow, feather, dispUv * Size);
    }

    /// <summary>沿 +x 方向从中心向外取样，`radius` 单位是像素（视口 256²、半高 128）。</summary>
    private static (double DispWindow, double Feather, double DispPx) Sample(byte[] buffer, int radius, double gain)
        => At(buffer, (Size / 2) + radius, Size / 2, gain);

    /// <summary>
    /// 主判据：**只要 alpha 开始羽化（半透明），位移就必须是 0** —— 半透明区里画面没有偏移，
    /// 也就不存在「偏移画面叠在真实桌面上」的重影。
    /// </summary>
    [Fact]
    public void NoDisplacement_WhereTheAlphaIsSemiTransparent()
    {
        const double gain = 8.0;   // 位移读数分辨率 ≈ 0.125 px
        byte[] buffer = Render(gain);

        for (int radius = 0; radius <= Size / 2 - 1; radius++)
        {
            var s = Sample(buffer, radius, gain);

            if (s.Feather >= 0.98)
            {
                continue;   // 还没进入羽化区，位移可以是任意值
            }

            Assert.True(
                Math.Abs(s.DispPx) <= 1.0,
                $"半径 {radius}px 处 alpha 已羽化到 {s.Feather:F3}，位移却仍有 {s.DispPx:F2}px "
                + "—— 半透明的偏移画面会叠在真实桌面上形成重影");
        }
    }

    /// <summary>顺序判据：位移**先**归零，羽化**后**开始（两者不能反过来，也不能重叠）。</summary>
    [Fact]
    public void DisplacementEndsBeforeTheFeatheringStarts()
    {
        const double gain = 8.0;
        byte[] buffer = Render(gain);

        int dispEnds = -1;
        int featherStarts = -1;

        for (int radius = 0; radius <= Size / 2 - 1; radius++)
        {
            var s = Sample(buffer, radius, gain);

            if (dispEnds < 0 && Math.Abs(s.DispPx) <= 0.5 && s.DispWindow <= 0.02)
            {
                dispEnds = radius;
            }

            if (featherStarts < 0 && s.Feather < 0.98)
            {
                featherStarts = radius;
            }
        }

        Assert.True(dispEnds >= 0, "位移淡出窗口从未归零 ⇒ 没接上 `windowDisp`");
        Assert.True(featherStarts >= 0, "alpha 羽化从未开始 ⇒ 取样范围不对");
        Assert.True(
            dispEnds < featherStarts,
            $"位移必须在羽化开始之前归零：位移归零于 {dispEnds}px，羽化开始于 {featherStarts}px");
    }

    /// <summary>
    /// 反向保护：不能为了去重影把透镜整个关掉 —— 阴影附近位移必须仍然是**满强度**。
    /// （`windowDisp` 在 0.45·fadeR 以内恒为 1，这里验的就是这一档。）
    /// </summary>
    [Fact]
    public void TheLensIsStillFullStrengthNearTheShadow()
    {
        const double gain = 1.0;   // 内侧位移大，用小量程避免读数饱和
        byte[] buffer = Render(gain);

        int shadow = (int)Math.Round(LensMath.ShadowRadiusPixels(HoleSize, Size));   // 16px

        var inner = Sample(buffer, (int)(shadow * 1.5), gain);

        Assert.True(
            Math.Abs(inner.DispPx) > 3.0,
            $"阴影附近（{shadow * 1.5}px）位移只有 {inner.DispPx:F2}px —— 透镜被误伤了");
        Assert.True(inner.DispWindow > 0.98, "阴影附近的位移窗口应当仍是满值 1");
        Assert.True(inner.Feather > 0.98, "阴影附近 alpha 不该开始羽化");
    }

    /// <summary>羽化本身必须还在（视口边缘收完），否则会回到「屏幕四周一圈矩形硬边」。</summary>
    [Fact]
    public void TheAlphaStillFadesOutAtTheViewportEdge()
    {
        const double gain = 8.0;
        byte[] buffer = Render(gain);

        int edge = (Size / 2) - 2;

        Assert.True(Sample(buffer, edge, gain).Feather < 0.10, "视口边缘的 alpha 应当已经羽化到接近 0");
    }
}
