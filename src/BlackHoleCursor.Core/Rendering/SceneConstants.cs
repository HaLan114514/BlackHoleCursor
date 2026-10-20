using System.Numerics;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// 与 Shaders/common.hlsl 中 SceneParams 完全对应的常量数据（11 × float4）。
/// 由 <see cref="SceneParamsBuilder"/> 从 SettingsModel 生成一次，渲染循环内只读。
///
/// ⚠️ 这份结构与 HLSL 的 cbuffer 是**按字节顺序**隐式对应的（不是按名字）：
/// 增删字段必须同时改 <c>common.hlsl</c> 的 cbuffer 与 <c>FloatCount</c>，
/// 否则编译不报错、运行时静默错位，症状是「画面参数全乱」。
/// 每行固定一个 float4，未使用的分量填 0（对齐 + 预留）。
/// </summary>
public struct SceneConstants
{
    public Vector4 Viewport;      // xy = 宽高(px)      zw = 1/宽, 1/高
    public Vector4 CenterRadius;  // xy = 黑洞中心(px)  zw = 保留
    public Vector4 Timing;        // x  = 时间(秒，受盘速度门控)  y = 星云时间(秒，不受门控)  w = 透镜开关  z = 氛围流速倍率(fixes-v3 问题 2)
    public Vector4 Extra;         // w  = 淡入淡出(N4)  x,y = 视差（光标位置/屏高，fixes-v2 问题 8）  z = 抓屏可用（W7 整屏不透明）
    public Vector4 Debug;         // 验证入口用：x = 色温(K) / spin  y = 位移放大 / 噪声缩放  z = rin / 角向周期  w = 旋转角

    // ---- v2 测地线渲染专用（M1.2 骨架，M1.4 起真正使用）----
    public Vector4 Hole;          // x = 阴影半径(屏高单位)  y = W = B_CRIT/半径  z = 积分步数  w = 星云强度(fixes-v2 问题 1)
    public Vector4 Look0;         // x = 色温K  y = 倾角rad  z = roll rad  w = 染色强度
    public Vector4 Look1;         // x = 内径r_s  y = 外径r_s  z = 盘不透明度  w = 多普勒混合
    public Vector4 Look2;         // x = beam  y = gain  z = contrast  w = winding
    public Vector4 Look3;         // x = speed  y = exposure  z = star  w = spin
    public Vector4 Tint;          // 自定义染色 rgba(0..1)

    /// <summary>
    /// fixes-v2 问题 5：窗口 uv → 抓屏纹理 uv 的仿射映射
    /// （xy = 缩放，zw = 偏移）。未使用时传 (1,1,0,0) 即恒等 ——
    /// 预览与单元测试的底图本来就是"窗口那一块"，不需要换算。
    /// </summary>
    public Vector4 CaptureMap;

    /// <summary>
    /// fixes-v2 问题 6：星云**亮部**配色（rgb），预设可以带自己的色调。
    /// 星云原本是写死的「暖橙 ↔ 冷蓝」lerp，现在亮端由它决定 ——
    /// 于是 Pure lens 这类预设能有自己的配色，而暗端（冷色）保持统一。
    /// </summary>
    public Vector4 NebulaTint;

    public const int FloatCount = 52;

    public float[] ToFloatArray() => new[]
    {
        Viewport.X, Viewport.Y, Viewport.Z, Viewport.W,
        CenterRadius.X, CenterRadius.Y, CenterRadius.Z, CenterRadius.W,
        Timing.X, Timing.Y, Timing.Z, Timing.W,
        Extra.X, Extra.Y, Extra.Z, Extra.W,
        Debug.X, Debug.Y, Debug.Z, Debug.W,
        Hole.X, Hole.Y, Hole.Z, Hole.W,
        Look0.X, Look0.Y, Look0.Z, Look0.W,
        Look1.X, Look1.Y, Look1.Z, Look1.W,
        Look2.X, Look2.Y, Look2.Z, Look2.W,
        Look3.X, Look3.Y, Look3.Z, Look3.W,
        Tint.X, Tint.Y, Tint.Z, Tint.W,
        CaptureMap.X, CaptureMap.Y, CaptureMap.Z, CaptureMap.W,
        NebulaTint.X, NebulaTint.Y, NebulaTint.Z, NebulaTint.W,
    };
}

/// <summary>
/// 着色器工具函数的验证参数（M1.1）。
/// 正常渲染走默认即可；单元测试用它把 blackbody / vnoiseWrapY / rot 的结果直接画出来断言。
/// </summary>
public readonly record struct ShaderProbe(
    double TemperatureKelvin = 5500.0,
    double NoiseScale = 8.0,
    double AngularPeriod = 19.0,
    double AngleRadians = 0.7);

/// <summary>
/// 渲染一帧所需的全部输入。
/// <paramref name="Scale"/> 与 <paramref name="Fade"/> 供 N4 出现/消失动画使用，
/// 默认 1（预览区与单元测试不播动画，传默认值即可）。
/// </summary>
public readonly record struct SceneDescription(
    double Width,
    double Height,
    double CenterX,
    double CenterY,
    SettingsModel Settings,
    double TimeSeconds,
    bool LensEnabled,
    double Scale = 1.0,
    double Fade = 1.0,
    ShaderProbe Probe = default,
    BlackHoleLook? Look = null,
    double ScreenHeight = 0.0,
    Vector4 CaptureMap = default,

    /// <summary>fixes-v2 问题 8：光标（黑洞）在**屏幕**坐标系里的位置，用于星空 / 星云随位置变化。</summary>
    double ParallaxX = 0.0,

    /// <summary>fixes-v2 问题 8：同上，y 分量。</summary>
    double ParallaxY = 0.0,

    /// <summary>
    /// W7：抓屏纹理是否可用（1 = 可用）。可用时整屏由**我们**重绘（不透明）⇒
    /// 全屏内容年龄一致，折射区不再比周围慢一拍（fixes-v3 问题 4）。
    /// 默认 0 ⇒ 预览区与单元测试保持原来的透明合成，行为不变。
    /// </summary>
    double DesktopReady = 0.0);

/// <summary>
/// 把设置与几何信息翻译成着色器常量。放在这里而不是渲染器内部，
/// 是为了让「参数映射」这件事可以被单元测试覆盖。
/// </summary>
public static class SceneParamsBuilder
{
    public static SceneConstants Build(in SceneDescription d)
    {
        var s = d.Settings;

        // v2 外观：未显式提供时用默认（Gargantua）。M2.1 之后由 SettingsModel 映射而来。
        var look = d.Look ?? BlackHoleLook.Default;

        // HoleSize 是「占**屏幕**高度的比例」，而着色器工作在**视口** uv 上，
        // 必须换算成占视口高度的比例，否则黑洞会随覆盖层窗口大小而变化。
        double holeFraction = d.ScreenHeight > 0 && d.Height > 0
            ? look.HoleSize * d.ScreenHeight / d.Height
            : look.HoleSize;
        // N4 出现/消失动画的缩放：整个黑洞（阴影 + 盘）一起缩放。
        // 盘的内外径是以 r_s 为单位的相对量，缩放阴影半径即可带动整体（B4）。
        double scale = Math.Clamp(d.Scale, 0.0, 4.0);
        holeFraction = Math.Clamp(holeFraction * scale, 1e-4, 4.0);

        // 盘的条纹速度（也是星场闪烁与降帧判据的依据）为 0 时冻结时间：
        // 画面完全静止，渲染层可安全降帧。
        double time = Math.Abs(s.DiskSpeed) > 0.0 ? d.TimeSeconds : 0.0;

        // fixes-v2 问题 8：视差归一化 —— 位置 / 屏高，与 HoleSize 同一口径，
        // 这样不同分辨率下「移动同样比例的距离」产生的漂移一致。
        // ScreenHeight 为 0（未传）时退化为无漂移，保持旧行为。
        double invScreen = d.ScreenHeight > 0 ? 1.0 / d.ScreenHeight : 0.0;

        return new SceneConstants
        {
            Viewport = new Vector4(
                (float)d.Width,
                (float)d.Height,
                (float)(1.0 / Math.Max(d.Width, 1.0)),
                (float)(1.0 / Math.Max(d.Height, 1.0))),
            CenterRadius = new Vector4((float)d.CenterX, (float)d.CenterY, 0f, 0f),
            Timing = new Vector4(
                (float)time,
                (float)d.TimeSeconds,                          // y = 星云时间：**不过**盘速度门控
                (float)Math.Clamp(s.AmbientFlowSpeed, SettingsRanges.AmbientFlowSpeedMin, SettingsRanges.AmbientFlowSpeedMax),
                d.LensEnabled && s.LensEnabled ? 1f : 0f),     // w = 透镜开关
            Extra = new Vector4(
                (float)(d.ParallaxX * invScreen),              // x = 视差（光标位置 / 屏高）
                (float)(d.ParallaxY * invScreen),              // y
                (float)Math.Clamp(d.DesktopReady, 0.0, 1.0),   // z = 抓屏可用（W7：整屏不透明的开关）
                (float)Math.Clamp(d.Fade, 0.0, 1.0)),          // w = 淡入淡出（N4）：预乘 alpha 下 rgb 与 a 一起乘
            Debug = new Vector4(
                (float)d.Probe.TemperatureKelvin,
                (float)Math.Max(d.Probe.NoiseScale, 0.01),
                (float)Math.Max(d.Probe.AngularPeriod, 1.0),
                (float)d.Probe.AngleRadians),
            Hole = new Vector4(
                (float)holeFraction,                                    // 阴影半径（视口高度单位）
                (float)LensMath.WorldScale(holeFraction),                // W = B_CRIT / 半径
                look.StepCount,
                (float)look.NebulaGain),
            Look0 = new Vector4(
                (float)look.TempKelvin,
                (float)look.Inclination,
                (float)look.Roll,
                (float)look.TintStrength),
            Look1 = new Vector4(
                (float)look.InnerRadius,
                (float)look.OuterRadius,
                (float)look.Opacity,
                (float)look.DopplerMix),
            Look2 = new Vector4(
                (float)look.BeamExponent,
                (float)look.Gain,
                (float)look.Contrast,
                (float)look.Winding),
            Look3 = new Vector4(
                (float)look.Speed,
                (float)look.Exposure,
                (float)look.StarGain,
                (float)look.Spin),
            Tint = ToVector4(look.Tint, 1.0),

            // 未给（X ≤ 0）时按恒等处理：预览与单测的底图就是窗口那一块
            CaptureMap = d.CaptureMap.X > 0.0 && d.CaptureMap.Y > 0.0
                ? d.CaptureMap
                : new Vector4(1.0f, 1.0f, 0.0f, 0.0f),

            // fixes-v2 问题 6：星云亮部配色（预设可带）
            NebulaTint = ToVector4(look.NebulaColor, 1.0),
        };
    }

    private static Vector4 ToVector4(System.Drawing.Color color, double opacityScale)
    {
        float a = (color.A / 255f) * (float)Math.Clamp(opacityScale, 0.0, 1.0);
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, a);
    }
}
