namespace BlackHoleCursor.Core.Rendering;

/// <summary>
/// M6.1（prd-v2 F6-4）：**自动档**的积分步数调节器。
///
/// 目标帧率跑不动时自动降步数（88 → 48 → 24），宽裕时再升回去。
/// 只在 <see cref="QualitySteps.Auto"/> 下生效 —— 用户显式选了档位就不该被覆盖。
///
/// ⚠️ **喂进来的必须是「一段时间窗口的平均帧率」，不是逐帧瞬时值。**
/// 实测踩过：启动头几秒抓屏还没就绪，瞬时值能冲到 117 FPS，
/// 而同一时段的 5 秒平均只有 41 FPS（抓屏降级）—— 用瞬时值会在最卡的时候反而升到最高档。
/// 生产侧由 <see cref="FrameTiming.TryTakeLog"/> 每 5 秒喂一次。
///
/// 三条防抖规则（缺一条就会看到画面质量来回跳）：
/// <list type="number">
/// <item>升档门槛（58）明显高于降档门槛（45），中间是**死区**，不累积也不动作；</item>
/// <item>换档必须持续满足一段时间（降 3s / 升 5s，升档更苛刻，避免刚升上去又掉下来）；</item>
/// <item>一次只挪一档，动作后计时清零，下一档要重新累积。</item>
/// </list>
/// </summary>
public sealed class AutoQualityController
{
    /// <summary>阶梯：低 24 / 中 48 / 高 88（prd-v2 §6.1，与 <see cref="QualitySteps"/> 一致）。</summary>
    public static readonly int[] Ladder = [(int)QualitySteps.Low, (int)QualitySteps.Medium, (int)QualitySteps.High];

    /// <summary>低于该帧率并持续足够久 → 降一档（与 <see cref="PerformanceMonitor"/> 的低帧率判据一致）。</summary>
    public const double DowngradeFramesPerSecond = 45.0;

    /// <summary>高于该帧率并持续足够久 → 升一档。比降档门槛高一截，中间是死区。</summary>
    public const double UpgradeFramesPerSecond = 58.0;

    public const double DowngradeSustainedSeconds = 3.0;

    /// <summary>升档要求更久：升上去容易掉下来，频繁换档比一直停在低档更难受。</summary>
    public const double UpgradeSustainedSeconds = 5.0;

    /// <summary>至少采样这么多次才允许换档，避开启动瞬间的预热假象（M1.8.5 记过：头几秒 FPS 是假的）。</summary>
    public const int MinimumSamples = 3;

    private int _index = 1; // 默认中档（48），与 QualitySteps.Auto 的旧行为一致
    private double _downgradeSeconds;
    private double _upgradeSeconds;
    private int _sampleCount;

    /// <summary>当前自动档选出的步数。</summary>
    public int StepCount => Ladder[_index];

    /// <summary>最近一次 <see cref="Update"/> 是否真的换了档（供日志）。</summary>
    public bool Changed { get; private set; }

    /// <summary>已经累计的「该降档」时长（秒），供测试观察。</summary>
    public double DowngradeSeconds => _downgradeSeconds;

    /// <summary>已经累计的「该升档」时长（秒），供测试观察。</summary>
    public double UpgradeSeconds => _upgradeSeconds;

    /// <summary>
    /// 喂入一次采样。
    /// </summary>
    /// <param name="framesPerSecond">实测帧率；&lt;= 0 或 NaN 表示还没测出来，直接忽略。</param>
    /// <param name="deltaSeconds">距上次采样的时间间隔。</param>
    /// <returns>本次是否换了档。</returns>
    public bool Update(double framesPerSecond, double deltaSeconds)
    {
        Changed = false;

        if (double.IsNaN(framesPerSecond) || framesPerSecond <= 0)
        {
            ClearTimers();
            return false;
        }

        _sampleCount++;
        double step = Math.Max(deltaSeconds, 0.0);

        if (framesPerSecond < DowngradeFramesPerSecond)
        {
            _upgradeSeconds = 0;
            _downgradeSeconds += step;

            if (_sampleCount >= MinimumSamples && _downgradeSeconds >= DowngradeSustainedSeconds)
            {
                Changed = TryMove(-1);
                ClearTimers();
            }

            return Changed;
        }

        if (framesPerSecond >= UpgradeFramesPerSecond)
        {
            _downgradeSeconds = 0;
            _upgradeSeconds += step;

            if (_sampleCount >= MinimumSamples && _upgradeSeconds >= UpgradeSustainedSeconds)
            {
                Changed = TryMove(+1);
                ClearTimers();
            }

            return Changed;
        }

        // 死区：两边都不累积。否则帧率在门槛上下抖动时，计时会一点点攒够并误换档。
        ClearTimers();
        return false;
    }

    /// <summary>
    /// 把自动档的结果写进外观：只有 <see cref="QualitySteps.Auto"/> 才覆盖步数，
    /// 用户显式选的档位原样保留。
    /// </summary>
    public BlackHoleLook Apply(BlackHoleLook look, SettingsModel settings)
    {
        if (settings.QualitySteps == QualitySteps.Auto)
        {
            look.StepCount = StepCount;
        }

        return look;
    }

    /// <summary>回到初始状态（中档）。覆盖层重启时用。</summary>
    public void Reset()
    {
        _index = 1;
        ClearTimers();
        Changed = false;
    }

    private bool TryMove(int direction)
    {
        int next = Math.Clamp(_index + direction, 0, Ladder.Length - 1);
        if (next == _index)
        {
            return false;
        }

        _index = next;
        return true;
    }

    private void ClearTimers()
    {
        _downgradeSeconds = 0;
        _upgradeSeconds = 0;
        _sampleCount = 0;
    }
}
