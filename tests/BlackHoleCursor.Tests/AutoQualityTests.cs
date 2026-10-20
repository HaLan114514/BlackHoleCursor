using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M6.1（prd-v2 F6-4）：自动档按实测帧率升降积分步数。
///
/// 这一组主要锁的是**防抖**：帧率在门槛附近抖动时不能让画质来回跳，
/// 否则「自动」比「固定低档」更难受。
/// </summary>
public class AutoQualityTests
{
    private static AutoQualityController Create() => new();

    /// <summary>连续喂入若干次同样的采样。</summary>
    private static bool Feed(AutoQualityController controller, double fps, double delta, int times)
    {
        bool changed = false;
        for (int i = 0; i < times; i++)
        {
            changed |= controller.Update(fps, delta);
        }

        return changed;
    }

    [Fact]
    public void Default_IsTheMediumStepCount()
    {
        Assert.Equal(48, Create().StepCount);
    }

    [Fact]
    public void SustainedLowFrameRate_Downgrades()
    {
        var controller = Create();

        bool changed = Feed(controller, 30, 1.0, 4);

        Assert.True(changed);
        Assert.Equal(24, controller.StepCount);
    }

    /// <summary>一次只挪一档：88 → 48 后要再持续低帧才到 24。</summary>
    [Fact]
    public void Downgrade_MovesOneRungAtATime()
    {
        var controller = Create();
        Feed(controller, 60, 1.0, 6); // 先升到 88
        Assert.Equal(88, controller.StepCount);

        Feed(controller, 30, 1.0, 3);
        Assert.Equal(48, controller.StepCount);

        Feed(controller, 30, 1.0, 3);
        Assert.Equal(24, controller.StepCount);

        // 已经到底，再怎么低也不会越界
        Feed(controller, 5, 1.0, 6);
        Assert.Equal(24, controller.StepCount);
    }

    [Fact]
    public void SustainedHighFrameRate_Upgrades()
    {
        var controller = Create();
        Feed(controller, 30, 1.0, 4); // 先降到 24
        Assert.Equal(24, controller.StepCount);

        bool changed = Feed(controller, 60, 1.0, 6);

        Assert.True(changed);
        Assert.Equal(48, controller.StepCount);

        Feed(controller, 60, 1.0, 6);
        Assert.Equal(88, controller.StepCount);

        Feed(controller, 60, 1.0, 6);
        Assert.Equal(88, controller.StepCount); // 已经到顶
    }

    /// <summary>升档比降档苛刻（5s vs 3s），避免刚升上去又立刻掉下来。</summary>
    [Fact]
    public void UpgradingTakesLongerThanDowngrading()
    {
        var controller = Create();
        Feed(controller, 30, 1.0, 4);
        Assert.Equal(24, controller.StepCount);

        Feed(controller, 60, 1.0, 4);
        Assert.Equal(24, controller.StepCount); // 才 4 秒，不该升档

        Feed(controller, 60, 1.0, 2);
        Assert.Equal(48, controller.StepCount);
    }

    [Fact]
    public void ABlinkOfLowFrameRate_DoesNotDowngrade()
    {
        var controller = Create();

        Feed(controller, 30, 1.0, 2); // 只低了 2 秒
        Feed(controller, 60, 1.0, 1); // 又回来了

        Assert.Equal(48, controller.StepCount);
        Assert.Equal(0.0, controller.DowngradeSeconds, 6);
    }

    /// <summary>45–58 之间是死区：既不累积降档也不累积升档。</summary>
    [Fact]
    public void TheDeadBandLeavesTheStepCountAlone()
    {
        var controller = Create();

        Feed(controller, 50, 1.0, 30);

        Assert.Equal(48, controller.StepCount);
        Assert.Equal(0.0, controller.DowngradeSeconds, 6);
        Assert.Equal(0.0, controller.UpgradeSeconds, 6);
    }

    [Fact]
    public void NoFrameRateData_IsIgnored()
    {
        var controller = Create();

        Assert.False(controller.Update(0, 1.0));
        Assert.False(controller.Update(double.NaN, 1.0));
        Assert.False(controller.Update(-5, 1.0));

        Assert.Equal(48, controller.StepCount);
    }

    /// <summary>
    /// 启动头几秒的帧率是假的（M1.8.5 记过：预热窗口里只有 0.2 FPS），
    /// 必须靠「至少采样几次」挡住，否则一开程序就被打到最低档。
    /// </summary>
    [Fact]
    public void StartupBlip_DoesNotDowngradeImmediately()
    {
        var controller = Create();

        Assert.False(controller.Update(1.0, 10.0), "单次采样不算数");
        Assert.Equal(48, controller.StepCount);
    }

    [Fact]
    public void Auto_IsTheOnlyModeItOverrides()
    {
        var controller = Create();
        Feed(controller, 30, 1.0, 4);
        Assert.Equal(24, controller.StepCount);

        var auto = new SettingsModel { QualitySteps = QualitySteps.Auto };
        var high = new SettingsModel { QualitySteps = QualitySteps.High };
        var low = new SettingsModel { QualitySteps = QualitySteps.Low };

        // 走的是真实映射链：FromSettings 按档位给出步数，Apply 再决定要不要覆盖
        Assert.Equal(24, controller.Apply(LookPresets.FromSettings(auto), auto).StepCount);

        // 用户显式选的档位不能被覆盖
        Assert.Equal(88, controller.Apply(LookPresets.FromSettings(high), high).StepCount);
        Assert.Equal(24, controller.Apply(LookPresets.FromSettings(low), low).StepCount);
    }

    /// <summary>
    /// 实机踩过的坑：启动预热阶段抓屏还没就绪，头一个窗口的瞬时读数能到 117 FPS，
    /// 而同期 5 秒平均只有 41 FPS。喂瞬时值会在最卡的时候反而升到最高档 ——
    /// 所以喂的是窗口平均值，且换档要连续几个窗口都成立。
    /// </summary>
    [Fact]
    public void AStartupBurstDoesNotLockInTheHighStepCount()
    {
        var controller = Create();

        controller.Update(117, 5.0); // 预热的假高帧率，就一个窗口
        Feed(controller, 41, 5.0, 6);

        Assert.Equal(24, controller.StepCount);
    }

    [Fact]
    public void Reset_ReturnsToMedium()
    {
        var controller = Create();
        Feed(controller, 30, 1.0, 4);

        controller.Reset();

        Assert.Equal(48, controller.StepCount);
        Assert.False(controller.Changed);
    }

    [Fact]
    public void Update_ReportsChangedOnlyWhenItReallyMoved()
    {
        var controller = Create();

        Assert.False(controller.Update(60, 1.0), "第一次采样不换档");
        Assert.False(controller.Update(60, 1.0));
        Assert.False(controller.Update(60, 1.0));
        Assert.False(controller.Update(60, 1.0), "4 秒还不够升档");
        Assert.True(controller.Update(60, 1.0), "第 5 秒升档");
        Assert.False(controller.Update(60, 1.0), "换档后计时清零，不会连着升");
    }

    [Fact]
    public void TheLadderMatchesTheQualityEnum()
    {
        Assert.Equal([24, 48, 88], AutoQualityController.Ladder);
        Assert.Equal(24, (int)QualitySteps.Low);
        Assert.Equal(48, (int)QualitySteps.Medium);
        Assert.Equal(88, (int)QualitySteps.High);
    }
}
