using System.Drawing;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// v2 黑洞外观参数（与参考实现 singularity 的 uniform 一一对应）。
///
/// 刻意独立于 SettingsModel：渲染层只认这份数据，
/// M2.1 再把 SettingsModel 映射到它，避免渲染核心与 UI 模型互相纠缠。
/// 所有长度以 r_s（史瓦西半径）为单位，除 HoleSize 为「屏高比例」。
/// </summary>
public sealed class BlackHoleLook : ICloneable
{
    // ---- 合法范围（与 prd-v2 §6.1 一致）----
    public const double HoleSizeMin = 0.02;
    public const double HoleSizeMax = 0.15;

    public const double TempMin = 1500.0;
    public const double TempMax = 20000.0;

    public const double InclinationMin = 0.0;
    public const double InclinationMax = 1.57;

    public const double RollMin = -Math.PI;
    public const double RollMax = Math.PI;

    public const double InnerMin = 1.6;
    public const double InnerMax = 6.0;

    public const double OuterMin = 3.0;
    public const double OuterMax = 20.0;

    public const double BeamMin = 0.0;
    public const double BeamMax = 6.0;

    public const double GainMin = 0.0;
    public const double GainMax = 4.0;

    public const double ContrastMin = 0.0;
    public const double ContrastMax = 2.0;

    public const double WindingMin = 0.0;
    public const double WindingMax = 12.0;

    public const double SpeedMin = -8.0;
    public const double SpeedMax = 8.0;

    public const double ExposureMin = 0.2;
    public const double ExposureMax = 3.0;

    public const double StarMin = 0.0;
    public const double StarMax = 1.0;

    /// <summary>fixes-v2 问题 1：程序化背景光（星云）强度 0–1，默认 0.45（常开）。</summary>
    public const double NebulaMin = 0.0;
    public const double NebulaMax = 1.0;

    public const double SpinMin = 0.0;
    public const double SpinMax = 0.98;

    public const int StepsMin = 8;
    public const int StepsMax = 128;

    // ---- 取值（默认 = Gargantua 预设，具体属性定义在下方，用字段支撑 ref 钳制）----

    /// <summary>自定义染色（在黑体色之上叠加）。</summary>
    public Color Tint { get; set; } = Color.FromArgb(255, 255, 255, 255);

    /// <summary>
    /// fixes-v2 问题 6：星云的**亮部配色**（暗部固定为冷蓝）。
    /// 默认暖橙，与旧行为一致；预设可以带自己的色调（用户已确认给 M5.2 开这个例外）。
    /// </summary>
    public Color NebulaColor { get; set; } = Color.FromArgb(255, 255, 140, 71);

    /// <summary>测地线积分步数（质量档位）。</summary>
    public int StepCount { get; set; } = 48;

    public static BlackHoleLook Default => new();

    /// <summary>
    /// M5.2：两份外观之间的线性插值，用于预设切换时的 1 秒交叉淡入。
    ///
    /// 刻意**不插值** <see cref="StepCount"/>：它是离散的质量档位，
    /// 中间值没有意义，切换时直接取目标档位（最多跳一次，不影响观感）。
    /// 染色 <see cref="Tint"/> 按通道插值。
    /// </summary>
    public static BlackHoleLook Lerp(BlackHoleLook from, BlackHoleLook to, double t)
    {
        double k = Math.Clamp(t, 0.0, 1.0);

        return new BlackHoleLook
        {
            HoleSize = from.HoleSize + ((to.HoleSize - from.HoleSize) * k),
            TempKelvin = from.TempKelvin + ((to.TempKelvin - from.TempKelvin) * k),
            TintStrength = from.TintStrength + ((to.TintStrength - from.TintStrength) * k),
            Inclination = from.Inclination + ((to.Inclination - from.Inclination) * k),
            Roll = from.Roll + ((to.Roll - from.Roll) * k),
            InnerRadius = from.InnerRadius + ((to.InnerRadius - from.InnerRadius) * k),
            OuterRadius = from.OuterRadius + ((to.OuterRadius - from.OuterRadius) * k),
            Opacity = from.Opacity + ((to.Opacity - from.Opacity) * k),
            DopplerMix = from.DopplerMix + ((to.DopplerMix - from.DopplerMix) * k),
            BeamExponent = from.BeamExponent + ((to.BeamExponent - from.BeamExponent) * k),
            Gain = from.Gain + ((to.Gain - from.Gain) * k),
            Contrast = from.Contrast + ((to.Contrast - from.Contrast) * k),
            Winding = from.Winding + ((to.Winding - from.Winding) * k),
            Speed = from.Speed + ((to.Speed - from.Speed) * k),
            Exposure = from.Exposure + ((to.Exposure - from.Exposure) * k),
            StarGain = from.StarGain + ((to.StarGain - from.StarGain) * k),
            NebulaGain = from.NebulaGain + ((to.NebulaGain - from.NebulaGain) * k),
            Spin = from.Spin + ((to.Spin - from.Spin) * k),
            Tint = LerpColor(from.Tint, to.Tint, k),
            NebulaColor = LerpColor(from.NebulaColor, to.NebulaColor, k),
            StepCount = to.StepCount,
        };
    }

    private static Color LerpColor(Color a, Color b, double k)
        => Color.FromArgb(
            LerpByte(a.A, b.A, k),
            LerpByte(a.R, b.R, k),
            LerpByte(a.G, b.G, k),
            LerpByte(a.B, b.B, k));

    private static byte LerpByte(byte a, byte b, double k)
        => (byte)Math.Round(a + ((b - a) * k), MidpointRounding.AwayFromZero);

    /// <summary>把所有取值钳回合法范围。</summary>
    public bool Clamp()
    {
        bool changed = false;

        changed |= ClampValue(ref _holeSize, HoleSizeMin, HoleSizeMax);
        changed |= ClampValue(ref _tempKelvin, TempMin, TempMax);
        changed |= ClampValue(ref _tintStrength, 0.0, 1.0);
        changed |= ClampValue(ref _inclination, InclinationMin, InclinationMax);
        changed |= ClampValue(ref _roll, RollMin, RollMax);
        changed |= ClampValue(ref _innerRadius, InnerMin, InnerMax);
        changed |= ClampValue(ref _outerRadius, OuterMin, OuterMax);
        changed |= ClampValue(ref _opacity, 0.0, 1.0);
        changed |= ClampValue(ref _dopplerMix, 0.0, 1.0);
        changed |= ClampValue(ref _beamExponent, BeamMin, BeamMax);
        changed |= ClampValue(ref _gain, GainMin, GainMax);
        changed |= ClampValue(ref _contrast, ContrastMin, ContrastMax);
        changed |= ClampValue(ref _winding, WindingMin, WindingMax);
        changed |= ClampValue(ref _speed, SpeedMin, SpeedMax);
        changed |= ClampValue(ref _exposure, ExposureMin, ExposureMax);
        changed |= ClampValue(ref _starGain, StarMin, StarMax);
        changed |= ClampValue(ref _nebulaGain, NebulaMin, NebulaMax);
        changed |= ClampValue(ref _spin, SpinMin, SpinMax);

        int steps = Math.Clamp(StepCount, StepsMin, StepsMax);
        if (steps != StepCount)
        {
            StepCount = steps;
            changed = true;
        }

        // 外径必须大于内径，否则盘根本不存在
        if (OuterRadius <= InnerRadius + 0.5)
        {
            OuterRadius = InnerRadius + 0.5;
            changed = true;
        }

        return changed;
    }

    public BlackHoleLook Clone()
    {
        var copy = (BlackHoleLook)MemberwiseClone();
        return copy;
    }

    object ICloneable.Clone() => Clone();

    // 字段支持 ref 钳制（属性无法按引用传递）
    private double _holeSize = 0.06;
    private double _tempKelvin = 4500.0;
    private double _tintStrength;
    private double _inclination = 1.52;
    private double _roll = 0.10;
    private double _innerRadius = 2.2;
    private double _outerRadius = 7.0;
    private double _opacity = 0.85;
    private double _dopplerMix = 0.35;
    private double _beamExponent = 2.0;
    private double _gain = 1.4;
    private double _contrast = 0.5;
    private double _winding = 7.0;
    private double _speed = 5.0;
    private double _exposure = 1.20;
    private double _starGain;

    /// <summary>
    /// fixes-v2 问题 1：程序化背景光（星云 / 辉光）强度。
    /// 默认 0.45 —— 「常开」，让黑洞的观感不随桌面明暗跳变。
    /// 实机实测（1080p，环带 1.5~3.5 个阴影半径）：纯黑桌面上把它从 0 开到 0.45，
    /// 环带平均亮度 14.4 → 约 30；白桌面上只 +6%（不会雾蒙蒙）。
    /// </summary>
    private double _nebulaGain = 0.45;
    private double _spin;

    public double HoleSize
    {
        get => _holeSize;
        set => _holeSize = value;
    }

    public double TempKelvin
    {
        get => _tempKelvin;
        set => _tempKelvin = value;
    }

    public double TintStrength
    {
        get => _tintStrength;
        set => _tintStrength = value;
    }

    public double Inclination
    {
        get => _inclination;
        set => _inclination = value;
    }

    public double Roll
    {
        get => _roll;
        set => _roll = value;
    }

    public double InnerRadius
    {
        get => _innerRadius;
        set => _innerRadius = value;
    }

    public double OuterRadius
    {
        get => _outerRadius;
        set => _outerRadius = value;
    }

    public double Opacity
    {
        get => _opacity;
        set => _opacity = value;
    }

    public double DopplerMix
    {
        get => _dopplerMix;
        set => _dopplerMix = value;
    }

    public double BeamExponent
    {
        get => _beamExponent;
        set => _beamExponent = value;
    }

    public double Gain
    {
        get => _gain;
        set => _gain = value;
    }

    public double Contrast
    {
        get => _contrast;
        set => _contrast = value;
    }

    public double Winding
    {
        get => _winding;
        set => _winding = value;
    }

    public double Speed
    {
        get => _speed;
        set => _speed = value;
    }

    public double Exposure
    {
        get => _exposure;
        set => _exposure = value;
    }

    public double StarGain
    {
        get => _starGain;
        set => _starGain = value;
    }

    /// <summary>fixes-v2 问题 1：程序化背景光（星云）强度，0 = 关闭。</summary>
    public double NebulaGain
    {
        get => _nebulaGain;
        set => _nebulaGain = value;
    }

    public double Spin
    {
        get => _spin;
        set => _spin = value;
    }

    private static bool ClampValue(ref double field, double min, double max)
    {
        if (double.IsNaN(field) || double.IsInfinity(field))
        {
            field = min;
            return true;
        }

        double clamped = Math.Clamp(field, min, max);
        if (Math.Abs(clamped - field) > double.Epsilon)
        {
            field = clamped;
            return true;
        }

        return false;
    }
}

/// <summary>
/// 8 个内置外观预设（数值照搬参考实现 singularity 的 PRESETS，prd-v2 §6.3）。
/// </summary>
public static class LookPresets
{
    /// <summary>
    /// 把用户配置（prd-v2 §6.1 的 v2 参数）映射成渲染层的外观模型（M2.1）。
    /// 渲染层只认 BlackHoleLook，映射集中放在这里，避免 UI 模型与渲染核心互相纠缠。
    /// </summary>
    public static BlackHoleLook FromSettings(SettingsModel settings)
    {
        var look = new BlackHoleLook
        {
            HoleSize = settings.HoleSize,
            TempKelvin = settings.DiskTemp,
            Tint = settings.TintColor,
            TintStrength = settings.TintStrength,
            Inclination = settings.DiskIncl,
            Roll = settings.DiskRoll,
            InnerRadius = settings.DiskInner,
            OuterRadius = settings.DiskOuter,
            Opacity = settings.DiskOpacity,
            DopplerMix = settings.DopplerMix,
            BeamExponent = settings.BeamExponent,
            Gain = settings.DiskGain,
            Contrast = settings.DiskContrast,
            Winding = settings.DiskWinding,
            Speed = settings.DiskSpeed,
            Exposure = settings.Exposure,
            StarGain = settings.StarGain,
            NebulaGain = settings.NebulaGain,
            NebulaColor = settings.NebulaColor,
            Spin = settings.Spin,
            StepCount = settings.EffectiveStepCount,
        };

        look.Clamp();
        return look;
    }

    /// <summary>
    /// 把某个外观写回用户配置（<see cref="FromSettings"/> 的逆映射，M1.8.4）。
    /// 刻意<b>不</b>改 HoleSize（大小）与 QualitySteps（性能档位）——它们不是「外观」，
    /// 切换预设时保留用户当前选择，与旧版 PresetLibrary 的行为一致。
    /// </summary>
    public static void ApplyTo(BlackHoleLook look, SettingsModel settings)
    {
        settings.DiskTemp = look.TempKelvin;
        settings.DiskIncl = look.Inclination;
        settings.DiskRoll = look.Roll;
        settings.DiskInner = look.InnerRadius;
        settings.DiskOuter = look.OuterRadius;
        settings.DiskOpacity = look.Opacity;
        settings.DopplerMix = look.DopplerMix;
        settings.BeamExponent = look.BeamExponent;
        settings.DiskGain = look.Gain;
        settings.DiskContrast = look.Contrast;
        settings.DiskWinding = look.Winding;
        settings.DiskSpeed = look.Speed;
        settings.Exposure = look.Exposure;
        settings.StarGain = look.StarGain;

        // fixes-v2 问题 6：**唯一**允许预设改写的「色调类」参数。
        // M5.2 定过「预设不改增益类参数」，用户已确认给星云色调开这个例外 ——
        // 于是 Pure lens 这类预设能有自己的配色，而 NebulaGain 仍跟随用户选择。
        settings.NebulaColor = look.NebulaColor;
        settings.Spin = look.Spin;
    }

    public const string Inferno = "Inferno";
    public const string Gargantua = "Gargantua";
    public const string Quasar = "Quasar";
    public const string M87Donut = "M87* donut";
    public const string Blazar = "Blazar";
    public const string FaceOnEmber = "Face-on ember";
    public const string PureLens = "Pure lens";
    public const string Zen = "Zen";

    public const string DefaultName = Gargantua;

    public static IReadOnlyList<string> Names { get; } = new[]
    {
        Inferno, Gargantua, Quasar, M87Donut, Blazar, FaceOnEmber, PureLens, Zen,
    };

    /// <summary>
    /// fixes-v2 问题 6：每个内置预设的**星云亮部配色**。
    /// 这是 M5.2「预设不改增益类参数」的唯一例外（用户确认）——
    /// 只改色调，`NebulaGain` 仍由用户自己选。
    /// </summary>
    public static Color NebulaColorFor(string name) => name switch
    {
        Inferno => Color.FromArgb(255, 255, 120, 60),
        Quasar => Color.FromArgb(255, 120, 190, 255),
        M87Donut => Color.FromArgb(255, 255, 190, 120),
        Blazar => Color.FromArgb(255, 150, 140, 255),
        FaceOnEmber => Color.FromArgb(255, 255, 150, 80),
        PureLens => Color.FromArgb(255, 200, 225, 255),
        Zen => Color.FromArgb(255, 170, 220, 205),
        _ => Color.FromArgb(255, 255, 140, 71),   // Gargantua 与未知名称：默认暖橙
    };

    public static BlackHoleLook Create(string name)
    {
        var look = new BlackHoleLook();

        switch (name)
        {
            case Inferno:
                Set(look, 5500.0, 1.50, 0.35, 1.8, 8.0, 0.90, 0.60, 2.5, 2.2, 1.6, 7.0, 5.0, 1.40, 0.0);
                break;

            case Gargantua:
                Set(look, 4500.0, 1.52, 0.10, 2.2, 7.0, 0.85, 0.35, 2.0, 1.4, 0.5, 7.0, 5.0, 1.20, 0.0);
                break;

            case Quasar:
                Set(look, 15000.0, 1.30, 0.35, 3.0, 14.0, 0.35, 1.00, 4.0, 1.2, 1.3, 8.0, 5.0, 0.80, 0.0);
                break;

            case M87Donut:
                Set(look, 3800.0, 0.55, -0.30, 2.2, 6.0, 0.45, 0.90, 3.5, 1.6, 0.4, 3.0, 2.5, 1.10, 0.0);
                break;

            case Blazar:
                Set(look, 18000.0, 1.05, 0.55, 3.0, 16.0, 0.30, 1.00, 5.0, 1.0, 1.5, 9.0, 6.0, 0.75, 0.0);
                break;

            case FaceOnEmber:
                Set(look, 6500.0, 0.30, 0.00, 3.0, 10.0, 0.50, 0.80, 2.5, 1.0, 1.1, 7.0, 5.0, 1.00, 0.0);
                break;

            case PureLens:
                Set(look, 5500.0, 1.50, 0.35, 1.8, 8.0, 0.00, 1.00, 2.5, 0.0, 1.6, 7.0, 5.0, 1.00, 0.6);
                break;

            case Zen:
                Set(look, 7000.0, 1.45, 0.15, 3.5, 7.0, 0.40, 0.50, 2.0, 0.5, 0.3, 3.0, 1.5, 0.70, 0.0);
                break;

            default:
                Set(look, 4500.0, 1.52, 0.10, 2.2, 7.0, 0.85, 0.35, 2.0, 1.4, 0.5, 7.0, 5.0, 1.20, 0.0);
                break;
        }

        look.NebulaColor = NebulaColorFor(name);
        look.Clamp();
        return look;
    }

    /// <summary>参数顺序：temp incl roll inner outer opac dopp beam gain contr wind speed expo star</summary>
    private static void Set(
        BlackHoleLook look,
        double temp, double incl, double roll,
        double inner, double outer, double opac, double dopp,
        double beam, double gain, double contr, double wind,
        double speed, double expo, double star)
    {
        look.TempKelvin = temp;
        look.Inclination = incl;
        look.Roll = roll;
        look.InnerRadius = inner;
        look.OuterRadius = outer;
        look.Opacity = opac;
        look.DopplerMix = dopp;
        look.BeamExponent = beam;
        look.Gain = gain;
        look.Contrast = contr;
        look.Winding = wind;
        look.Speed = speed;
        look.Exposure = expo;
        look.StarGain = star;
    }
}
