using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BlackHoleCursor.Core;

/// <summary>
/// 所有配置项的合法范围。UI 滑块与加载时的钳制共用同一份定义，
/// 避免出现「界面限制了但手改配置文件仍能写入非法值」的不一致。
/// </summary>
public static class SettingsRanges
{
    // ---- 性能 ----
    public const double TargetFrameRateMin = 30.0;
    public const double TargetFrameRateMax = 240.0;

    /// <summary>不超过这个帧率就走垂直同步（Present(1)）；超过则必须关掉，否则会被锁在显示器刷新率。</summary>
    public const double VSyncFrameRateLimit = 60.0;

    // ---- N4 出现 / 消失动画 ----
    public const double TransitionDurationMin = 0.1;
    public const double TransitionDurationMax = 1.5;

    public const double TransitionOvershootMin = 1.0;
    public const double TransitionOvershootMax = 1.3;

    public const double FollowStiffnessMin = 1.0;
    public const double FollowStiffnessMax = 200.0;

    public const double FollowDampingMin = 1.0;
    public const double FollowDampingMax = 40.0;

    public const double FollowMaxSpeedMin = 100.0;
    public const double FollowMaxSpeedMax = 20000.0;

    public const double FollowMaxAccelMin = 500.0;
    public const double FollowMaxAccelMax = 200000.0;

    public const double FollowSnapDistanceMin = 0.2;
    public const double FollowSnapDistanceMax = 10.0;

    // ---- N1 静止游荡 ----
    public const double IdleWanderDelayMin = 0.2;
    public const double IdleWanderDelayMax = 10.0;

    public const double IdleWanderRadiusMin = 5.0;
    public const double IdleWanderRadiusMax = 120.0;

    public const double IdleWanderSpeedMin = 0.05;
    public const double IdleWanderSpeedMax = 1.5;

    // ⚠️ 原先这里有一个 `OverlayMaxExtent = 1024`（「跟随光标的小窗口」的边长上限）。
    // v3（P1.5）已删除：窗口改成整块显示器后必然超过它，而 P0 实测整屏 1920×1080
    // 仍有 56~57 FPS（渲染只占 0.01ms），当初那条性能前提已不成立。

    // ---- v2 视觉参数（prd-v2 §6.1）----
    // 范围与 BlackHoleLook 的 Clamp 完全一致，放在这里是为了让 UI 滑块直接用。

    /// <summary>阴影半径占屏高的比例。</summary>
    public const double HoleSizeMin = 0.02;
    public const double HoleSizeMax = 0.15;

    public const double DiskTempMin = 1500.0;
    public const double DiskTempMax = 20000.0;

    public const double TintStrengthMin = 0.0;
    public const double TintStrengthMax = 1.0;

    /// <summary>盘倾角（弧度）：0 正视，1.57 侧视。</summary>
    public const double DiskInclMin = 0.0;
    public const double DiskInclMax = 1.57;

    public const double DiskRollMin = -3.14;
    public const double DiskRollMax = 3.14;

    public const double DiskInnerMin = 1.6;
    public const double DiskInnerMax = 6.0;

    public const double DiskOuterMin = 3.0;
    public const double DiskOuterMax = 20.0;

    public const double DiskOpacityMin = 0.0;
    public const double DiskOpacityMax = 1.0;

    public const double DopplerMixMin = 0.0;
    public const double DopplerMixMax = 1.0;

    public const double BeamExponentMin = 0.0;
    public const double BeamExponentMax = 6.0;

    public const double DiskGainMin = 0.0;
    public const double DiskGainMax = 4.0;

    public const double DiskContrastMin = 0.0;
    public const double DiskContrastMax = 2.0;

    public const double DiskWindingMin = 0.0;
    public const double DiskWindingMax = 12.0;

    /// <summary>条纹速度，负值表示反向公转。</summary>
    public const double DiskSpeedMin = -8.0;
    public const double DiskSpeedMax = 8.0;

    public const double ExposureMin = 0.2;
    public const double ExposureMax = 3.0;

    public const double StarGainMin = 0.0;
    public const double StarGainMax = 1.0;

    /// <summary>fixes-v2 问题 1：程序化背景光（星云）强度，0–1，默认 0.35（常开）。</summary>
    public const double NebulaGainMin = 0.0;
    public const double NebulaGainMax = 1.0;

    /// <summary>
    /// fixes-v3 问题 2：星空闪烁与星云流动的**速度倍率**，0–3，默认 1（= 原来的速度）。
    /// 0 = 完全冻结（星空不闪、星云不漂）；3 = 三倍速。
    /// </summary>
    public const double AmbientFlowSpeedMin = 0.0;
    public const double AmbientFlowSpeedMax = 3.0;

    public const double SpinMin = 0.0;
    public const double SpinMax = 0.98;
}

/// <summary>
/// 积分步数档位。数值即 Schwarzschild 下的步数（Auto 由运行时按帧率决定，M6.1）。
/// </summary>
public enum QualitySteps
{
    Auto = 0,
    Low = 24,
    Medium = 48,
    High = 88,
}

/// <summary>
/// 强类型配置模型。不依赖 WPF，便于单元测试与后台渲染线程直接读取。
/// </summary>
public sealed class SettingsModel : INotifyPropertyChanged, ICloneable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _enabled = true;

    // ---- F4 光标接管 ----
    private bool _hideSystemCursor = true;
    private bool _autoFallback = true;

    // ---- F5 引力透镜（v2 只剩开关；强度/半径由测地线自然决定）----
    private bool _lensEnabled = true;

    // ---- F5 惯性跟随 ----
    private bool _followEnabled;
    private double _followStiffness = 60.0;
    private double _followDamping = 12.0;
    private double _followMaxSpeed = 2500.0;
    private double _followMaxAccel = 60000.0;
    private double _followSnapDistance = 1.5;
    private bool _followAdaptiveSpeed;

    // ---- N4 出现 / 消失动画 ----
    private bool _transitionEnabled = true;
    private double _transitionDuration = 0.35;
    private double _transitionOvershoot = 1.1;

    // ---- N1 静止游荡 ----
    private bool _idleWanderEnabled;
    private double _idleWanderDelay = 1.5;
    private double _idleWanderRadius = 40.0;
    private double _idleWanderSpeed = 0.3;

    // ---- 性能 ----
    private double _targetFrameRate = 60.0;

    // ---- F7 程序设置 ----
    private bool _autoStart;

    /// <summary>
    /// F8：截图补偿。因为覆盖层被排除出捕获（否则会镜像回廊），
    /// 全屏截图里没有黑洞；开启后检测到剪贴板出现全屏位图就把黑洞补回去。
    /// </summary>
    private bool _screenshotCompensation = true;

    // ---- v2 外观（prd-v2 §6.1，默认值 = Gargantua 预设）----
    // 这些是「用户可调」的唯一来源；渲染层只认 BlackHoleLook，由 FromSettings 映射。
    private double _holeSize = 0.06;
    private double _diskTemp = 4500.0;
    private Color _tintColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private double _tintStrength;
    private double _diskIncl = 1.52;
    private double _diskRoll = 0.10;
    private double _diskInner = 2.2;
    private double _diskOuter = 7.0;
    private double _diskOpacity = 0.85;
    private double _dopplerMix = 0.35;
    private double _beamExponent = 2.0;
    private double _diskGain = 1.4;
    private double _diskContrast = 0.5;
    private double _diskWinding = 7.0;
    private double _diskSpeed = 5.0;
    private double _exposure = 1.20;
    private double _starGain;
    private double _nebulaGain = 0.45;

    /// <summary>fixes-v3 问题 2：氛围流速。默认 1 —— 老配置文件里没有这一项，
    /// 反序列化不会调用 setter，于是保持这个默认值（速度不变），不会变成 0 冻住。</summary>
    private double _ambientFlowSpeed = 1.0;

    /// <summary>fixes-v2 问题 6：星云亮部配色（默认暖橙，与旧行为一致）。</summary>
    private Color _nebulaColor = Color.FromArgb(0xFF, 0xFF, 0x8C, 0x47);
    private double _spin;
    private QualitySteps _qualitySteps = QualitySteps.Auto;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool HideSystemCursor
    {
        get => _hideSystemCursor;
        set => SetField(ref _hideSystemCursor, value);
    }

    public bool AutoFallback
    {
        get => _autoFallback;
        set => SetField(ref _autoFallback, value);
    }

    /// <summary>
    /// 是否抓取桌面做引力透镜。关闭后不抓屏，只画黑洞本体（性能占用最低）。
    /// v2 的偏折量由测地线积分天然决定，不再有「强度 / 半径」这类可调近似参数。
    /// </summary>
    public bool LensEnabled
    {
        get => _lensEnabled;
        set => SetField(ref _lensEnabled, value);
    }

    public bool FollowEnabled
    {
        get => _followEnabled;
        set => SetField(ref _followEnabled, value);
    }

    public double FollowStiffness
    {
        get => _followStiffness;
        set => SetField(ref _followStiffness, value);
    }

    public double FollowDamping
    {
        get => _followDamping;
        set => SetField(ref _followDamping, value);
    }

    public double FollowMaxSpeed
    {
        get => _followMaxSpeed;
        set => SetField(ref _followMaxSpeed, value);
    }

    public double FollowMaxAccel
    {
        get => _followMaxAccel;
        set => SetField(ref _followMaxAccel, value);
    }

    public double FollowSnapDistance
    {
        get => _followSnapDistance;
        set => SetField(ref _followSnapDistance, value);
    }

    public bool FollowAdaptiveSpeed
    {
        get => _followAdaptiveSpeed;
        set => SetField(ref _followAdaptiveSpeed, value);
    }

    /// <summary>
    /// 渲染目标帧率（FPS）。超过显示器刷新率时会自动关掉垂直同步（否则会被 vsync 锁在 60）。
    /// 注意：帧率越高，CPU/GPU 占用越高，笔记本电池模式下建议 60。
    /// </summary>
    public double TargetFrameRate
    {
        get => _targetFrameRate;
        set => SetField(ref _targetFrameRate, value);
    }

    /// <summary>N4：切换开关时播放出现 / 消失动画（弹性过冲）。退出时不播，保证零延迟。</summary>
    public bool TransitionEnabled
    {
        get => _transitionEnabled;
        set => SetField(ref _transitionEnabled, value);
    }

    /// <summary>N4：动画时长（秒）。</summary>
    public double TransitionDuration
    {
        get => _transitionDuration;
        set => SetField(ref _transitionDuration, value);
    }

    /// <summary>N4：过冲峰值（1.0 = 不过冲）。出现时先胀到这个倍率再落回 1。</summary>
    public double TransitionOvershoot
    {
        get => _transitionOvershoot;
        set => SetField(ref _transitionOvershoot, value);
    }

    /// <summary>N1：鼠标静止一段时间后，黑洞在鼠标附近自动游荡。</summary>
    public bool IdleWanderEnabled
    {
        get => _idleWanderEnabled;
        set => SetField(ref _idleWanderEnabled, value);
    }

    /// <summary>N1：静止多久后开始游荡（秒）。</summary>
    public double IdleWanderDelay
    {
        get => _idleWanderDelay;
        set => SetField(ref _idleWanderDelay, value);
    }

    /// <summary>N1：游荡半径（px）。黑洞永远不会偏离鼠标超过这个距离。</summary>
    public double IdleWanderRadius
    {
        get => _idleWanderRadius;
        set => SetField(ref _idleWanderRadius, value);
    }

    /// <summary>N1：游荡快慢（无量纲频率系数）。</summary>
    public double IdleWanderSpeed
    {
        get => _idleWanderSpeed;
        set => SetField(ref _idleWanderSpeed, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetField(ref _autoStart, value);
    }

    /// <summary>
    /// F8：是否补偿全屏截图（把黑洞合成回剪贴板里的图）。默认开启。
    /// 只处理剪贴板中的**全屏位图**；区域截图与保存到磁盘的文件不处理。
    /// </summary>
    public bool ScreenshotCompensation
    {
        get => _screenshotCompensation;
        set => SetField(ref _screenshotCompensation, value);
    }

    // ---- v2 外观 ----

    /// <summary>阴影半径占屏高的比例（大小）。</summary>
    public double HoleSize
    {
        get => _holeSize;
        set => SetField(ref _holeSize, value);
    }

    /// <summary>最热环温度 K（颜色·色温）。</summary>
    public double DiskTemp
    {
        get => _diskTemp;
        set => SetField(ref _diskTemp, value);
    }

    /// <summary>叠加在黑体色之上的自定义染色。</summary>
    public Color TintColor
    {
        get => _tintColor;
        set => SetField(ref _tintColor, value);
    }

    /// <summary>染色强度，0 = 纯黑体色。</summary>
    public double TintStrength
    {
        get => _tintStrength;
        set => SetField(ref _tintStrength, value);
    }

    /// <summary>盘倾角（弧度）：0 正视，1.57 侧视。</summary>
    public double DiskIncl
    {
        get => _diskIncl;
        set => SetField(ref _diskIncl, value);
    }

    /// <summary>屏幕平面内的旋转（弧度）。</summary>
    public double DiskRoll
    {
        get => _diskRoll;
        set => SetField(ref _diskRoll, value);
    }

    /// <summary>盘内径（r_s）。</summary>
    public double DiskInner
    {
        get => _diskInner;
        set => SetField(ref _diskInner, value);
    }

    /// <summary>盘外径（r_s）。</summary>
    public double DiskOuter
    {
        get => _diskOuter;
        set => SetField(ref _diskOuter, value);
    }

    /// <summary>盘对背景的不透明度。</summary>
    public double DiskOpacity
    {
        get => _diskOpacity;
        set => SetField(ref _diskOpacity, value);
    }

    public double DopplerMix
    {
        get => _dopplerMix;
        set => SetField(ref _dopplerMix, value);
    }

    public double BeamExponent
    {
        get => _beamExponent;
        set => SetField(ref _beamExponent, value);
    }

    public double DiskGain
    {
        get => _diskGain;
        set => SetField(ref _diskGain, value);
    }

    public double DiskContrast
    {
        get => _diskContrast;
        set => SetField(ref _diskContrast, value);
    }

    public double DiskWinding
    {
        get => _diskWinding;
        set => SetField(ref _diskWinding, value);
    }

    /// <summary>条纹速度，负值 = 反向公转。</summary>
    public double DiskSpeed
    {
        get => _diskSpeed;
        set => SetField(ref _diskSpeed, value);
    }

    public double Exposure
    {
        get => _exposure;
        set => SetField(ref _exposure, value);
    }

    public double StarGain
    {
        get => _starGain;
        set => SetField(ref _starGain, value);
    }

    /// <summary>
    /// fixes-v2 问题 1：程序化背景光（星云 / 辉光）强度，0–1。
    /// 默认 0.45（常开）—— 让黑洞在**任何**桌面亮度下都有可被弯折的内容。
    /// </summary>
    public double NebulaGain
    {
        get => _nebulaGain;
        set => SetField(ref _nebulaGain, value);
    }

    /// <summary>
    /// fixes-v3 问题 2：星空闪烁与星云流动的**速度倍率**（1 = 原速，0 = 冻结，最大 3 倍速）。
    /// 管的是「变化快慢」，不是亮度 —— 亮度由星空强度 / 星云强度各自管。
    /// 刻意**不进预设**（与 <see cref="NebulaGain"/> 一致）：快慢是用户口味，不是外观。
    /// </summary>
    public double AmbientFlowSpeed
    {
        get => _ambientFlowSpeed;
        set => SetField(ref _ambientFlowSpeed, value);
    }

    /// <summary>
    /// fixes-v2 问题 6：星云的**亮部配色**（暗部固定冷蓝）。
    /// 这是唯一会被预设改写的「色调类」参数（用户确认给 M5.2 开例外）。
    /// </summary>
    public Color NebulaColor
    {
        get => _nebulaColor;
        set => SetField(ref _nebulaColor, value);
    }

    /// <summary>Kerr 自旋 0–0.98。</summary>
    public double Spin
    {
        get => _spin;
        set => SetField(ref _spin, value);
    }

    public QualitySteps QualitySteps
    {
        get => _qualitySteps;
        set => SetField(ref _qualitySteps, value);
    }

    /// <summary>实际使用的积分步数（Auto 目前按 Medium 处理，自动降档留给 M6.1）。</summary>
    [JsonIgnore]
    public int EffectiveStepCount => _qualitySteps == QualitySteps.Auto
        ? (int)QualitySteps.Medium
        : (int)_qualitySteps;

    /// <summary>
    /// 把所有数值钳制回合法范围。加载外部文件、UI 输入越界后都必须调用。
    /// 返回是否发生了修改，便于测试与日志。
    /// </summary>
    public bool Clamp()
    {
        bool changed = false;

        changed |= ClampValue(nameof(TargetFrameRate), _targetFrameRate, SettingsRanges.TargetFrameRateMin, SettingsRanges.TargetFrameRateMax, v => _targetFrameRate = Math.Round(v));
        changed |= ClampValue(nameof(TransitionDuration), _transitionDuration, SettingsRanges.TransitionDurationMin, SettingsRanges.TransitionDurationMax, v => _transitionDuration = v);
        changed |= ClampValue(nameof(TransitionOvershoot), _transitionOvershoot, SettingsRanges.TransitionOvershootMin, SettingsRanges.TransitionOvershootMax, v => _transitionOvershoot = v);
        changed |= ClampValue(nameof(FollowStiffness), _followStiffness, SettingsRanges.FollowStiffnessMin, SettingsRanges.FollowStiffnessMax, v => _followStiffness = v);
        changed |= ClampValue(nameof(FollowDamping), _followDamping, SettingsRanges.FollowDampingMin, SettingsRanges.FollowDampingMax, v => _followDamping = v);
        changed |= ClampValue(nameof(FollowMaxSpeed), _followMaxSpeed, SettingsRanges.FollowMaxSpeedMin, SettingsRanges.FollowMaxSpeedMax, v => _followMaxSpeed = v);
        changed |= ClampValue(nameof(FollowMaxAccel), _followMaxAccel, SettingsRanges.FollowMaxAccelMin, SettingsRanges.FollowMaxAccelMax, v => _followMaxAccel = v);
        changed |= ClampValue(nameof(FollowSnapDistance), _followSnapDistance, SettingsRanges.FollowSnapDistanceMin, SettingsRanges.FollowSnapDistanceMax, v => _followSnapDistance = v);
        changed |= ClampValue(nameof(IdleWanderDelay), _idleWanderDelay, SettingsRanges.IdleWanderDelayMin, SettingsRanges.IdleWanderDelayMax, v => _idleWanderDelay = v);
        changed |= ClampValue(nameof(IdleWanderRadius), _idleWanderRadius, SettingsRanges.IdleWanderRadiusMin, SettingsRanges.IdleWanderRadiusMax, v => _idleWanderRadius = v);
        changed |= ClampValue(nameof(IdleWanderSpeed), _idleWanderSpeed, SettingsRanges.IdleWanderSpeedMin, SettingsRanges.IdleWanderSpeedMax, v => _idleWanderSpeed = v);

        // ---- v2 外观 ----
        changed |= ClampValue(nameof(HoleSize), _holeSize, SettingsRanges.HoleSizeMin, SettingsRanges.HoleSizeMax, v => _holeSize = v);
        changed |= ClampValue(nameof(DiskTemp), _diskTemp, SettingsRanges.DiskTempMin, SettingsRanges.DiskTempMax, v => _diskTemp = v);
        changed |= ClampValue(nameof(TintStrength), _tintStrength, SettingsRanges.TintStrengthMin, SettingsRanges.TintStrengthMax, v => _tintStrength = v);
        changed |= ClampValue(nameof(DiskIncl), _diskIncl, SettingsRanges.DiskInclMin, SettingsRanges.DiskInclMax, v => _diskIncl = v);
        changed |= ClampValue(nameof(DiskRoll), _diskRoll, SettingsRanges.DiskRollMin, SettingsRanges.DiskRollMax, v => _diskRoll = v);
        changed |= ClampValue(nameof(DiskInner), _diskInner, SettingsRanges.DiskInnerMin, SettingsRanges.DiskInnerMax, v => _diskInner = v);
        changed |= ClampValue(nameof(DiskOuter), _diskOuter, SettingsRanges.DiskOuterMin, SettingsRanges.DiskOuterMax, v => _diskOuter = v);
        changed |= ClampValue(nameof(DiskOpacity), _diskOpacity, SettingsRanges.DiskOpacityMin, SettingsRanges.DiskOpacityMax, v => _diskOpacity = v);
        changed |= ClampValue(nameof(DopplerMix), _dopplerMix, SettingsRanges.DopplerMixMin, SettingsRanges.DopplerMixMax, v => _dopplerMix = v);
        changed |= ClampValue(nameof(BeamExponent), _beamExponent, SettingsRanges.BeamExponentMin, SettingsRanges.BeamExponentMax, v => _beamExponent = v);
        changed |= ClampValue(nameof(DiskGain), _diskGain, SettingsRanges.DiskGainMin, SettingsRanges.DiskGainMax, v => _diskGain = v);
        changed |= ClampValue(nameof(DiskContrast), _diskContrast, SettingsRanges.DiskContrastMin, SettingsRanges.DiskContrastMax, v => _diskContrast = v);
        changed |= ClampValue(nameof(DiskWinding), _diskWinding, SettingsRanges.DiskWindingMin, SettingsRanges.DiskWindingMax, v => _diskWinding = v);
        changed |= ClampValue(nameof(DiskSpeed), _diskSpeed, SettingsRanges.DiskSpeedMin, SettingsRanges.DiskSpeedMax, v => _diskSpeed = v);
        changed |= ClampValue(nameof(Exposure), _exposure, SettingsRanges.ExposureMin, SettingsRanges.ExposureMax, v => _exposure = v);
        changed |= ClampValue(nameof(StarGain), _starGain, SettingsRanges.StarGainMin, SettingsRanges.StarGainMax, v => _starGain = v);
        changed |= ClampValue(nameof(NebulaGain), _nebulaGain, SettingsRanges.NebulaGainMin, SettingsRanges.NebulaGainMax, v => _nebulaGain = v);
        changed |= ClampValue(nameof(AmbientFlowSpeed), _ambientFlowSpeed, SettingsRanges.AmbientFlowSpeedMin, SettingsRanges.AmbientFlowSpeedMax, v => _ambientFlowSpeed = v);
        changed |= ClampValue(nameof(Spin), _spin, SettingsRanges.SpinMin, SettingsRanges.SpinMax, v => _spin = v);

        // 外径必须大于内径，否则盘不存在
        if (_diskOuter <= _diskInner + 0.5)
        {
            _diskOuter = _diskInner + 0.5;
            OnPropertyChanged(nameof(DiskOuter));
            changed = true;
        }

        // 枚举越界（含 JSON 里被写成未定义数值）回落到 Auto
        if (!Enum.IsDefined(_qualitySteps))
        {
            _qualitySteps = QualitySteps.Auto;
            OnPropertyChanged(nameof(QualitySteps));
            changed = true;
        }

        return changed;
    }

    private bool ClampValue(string propertyName, double current, double min, double max, Action<double> setter)
    {
        if (double.IsNaN(current) || double.IsInfinity(current))
        {
            setter(min);
            OnPropertyChanged(propertyName);
            return true;
        }

        double clamped = Math.Clamp(current, min, max);
        if (Math.Abs(clamped - current) > double.Epsilon)
        {
            setter(clamped);
            OnPropertyChanged(propertyName);
            return true;
        }

        return false;
    }

    public SettingsModel Clone()
    {
        var copy = (SettingsModel)MemberwiseClone();
        copy.PropertyChanged = null;
        return copy;
    }

    object ICloneable.Clone() => Clone();

    /// <summary>把另一个实例的值整体拷入本实例（用于「应用预设」）。</summary>
    public void CopyFrom(SettingsModel other)
    {
        Enabled = other.Enabled;
        HideSystemCursor = other.HideSystemCursor;
        AutoFallback = other.AutoFallback;
        LensEnabled = other.LensEnabled;
        FollowEnabled = other.FollowEnabled;
        FollowStiffness = other.FollowStiffness;
        FollowDamping = other.FollowDamping;
        FollowMaxSpeed = other.FollowMaxSpeed;
        FollowMaxAccel = other.FollowMaxAccel;
        FollowSnapDistance = other.FollowSnapDistance;
        FollowAdaptiveSpeed = other.FollowAdaptiveSpeed;
        TargetFrameRate = other.TargetFrameRate;
        TransitionEnabled = other.TransitionEnabled;
        TransitionDuration = other.TransitionDuration;
        TransitionOvershoot = other.TransitionOvershoot;
        IdleWanderEnabled = other.IdleWanderEnabled;
        HoleSize = other.HoleSize;
        DiskTemp = other.DiskTemp;
        TintColor = other.TintColor;
        TintStrength = other.TintStrength;
        DiskIncl = other.DiskIncl;
        DiskRoll = other.DiskRoll;
        DiskInner = other.DiskInner;
        DiskOuter = other.DiskOuter;
        DiskOpacity = other.DiskOpacity;
        DopplerMix = other.DopplerMix;
        BeamExponent = other.BeamExponent;
        DiskGain = other.DiskGain;
        DiskContrast = other.DiskContrast;
        DiskWinding = other.DiskWinding;
        DiskSpeed = other.DiskSpeed;
        Exposure = other.Exposure;
        StarGain = other.StarGain;
        NebulaGain = other.NebulaGain;
        Spin = other.Spin;
        QualitySteps = other.QualitySteps;
        IdleWanderDelay = other.IdleWanderDelay;
        IdleWanderRadius = other.IdleWanderRadius;
        IdleWanderSpeed = other.IdleWanderSpeed;
        // AutoStart 属于「程序设置」，不随外观预设变化，故不拷贝。
    }

    /// <summary>
    /// M5.2：把另一个实例的**外观部分**打成补丁应用到本实例（「应用预设」走这条路径，
    /// 不再是 <see cref="CopyFrom"/> 的整体覆盖）。
    ///
    /// 只写预设会给出的观感参数 —— 与
    /// <see cref="Rendering.LookPresets.ApplyTo"/> 的字段清单完全一致（改一边就要改另一边）。
    /// 刻意不碰：
    /// <list type="bullet">
    /// <item><see cref="HoleSize"/>（大小）、<see cref="QualitySteps"/>（性能档位）与
    /// <see cref="NebulaGain"/>（增益）：用户选择，不是外观；</item>
    /// <item><see cref="TintColor"/> / <see cref="TintStrength"/>：预设表里没有染色这一项；</item>
    /// <item>跟随 / 游荡 / 动画 / 光标接管 / 自启等一切系统行为。</item>
    /// </list>
    /// ⚠️ 唯一的例外是 <see cref="NebulaColor"/>（fixes-v2 问题 6，用户确认）：
    /// 星云色调**允许**由预设带，否则 Pure lens 这类预设没法有自己的配色。
    /// </summary>
    public void ApplyAppearanceFrom(SettingsModel other)
    {
        DiskTemp = other.DiskTemp;
        DiskIncl = other.DiskIncl;
        DiskRoll = other.DiskRoll;
        DiskInner = other.DiskInner;
        DiskOuter = other.DiskOuter;
        DiskOpacity = other.DiskOpacity;
        DopplerMix = other.DopplerMix;
        BeamExponent = other.BeamExponent;
        DiskGain = other.DiskGain;
        DiskContrast = other.DiskContrast;
        DiskWinding = other.DiskWinding;
        DiskSpeed = other.DiskSpeed;
        Exposure = other.Exposure;
        StarGain = other.StarGain;
        NebulaColor = other.NebulaColor;    // 问题 6：唯一允许预设改写的色调类参数
        Spin = other.Spin;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
