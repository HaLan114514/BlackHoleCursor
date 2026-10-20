using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M3.1（prd-v2 F6-2 / 缺陷 B1）：**物理必须按累计真实时间步进，渲染节流只决定画不画**。
///
/// 症状回顾：修复前每一帧都推进 <c>_lastFrameSeconds</c>，而跟随物理只在**真正渲染**的帧推进。
/// 于是被节流跳过的那些帧的时间被白白丢掉 —— 渲染线程跑得越快（Tick 频率越高）、跳过的帧越多，
/// 黑洞就「走得越慢」。帧率从 32 提到 57 之后这个 bug 才暴露出来。
///
/// 难点：宿主与帧调度原本都用真实 <see cref="System.Diagnostics.Stopwatch"/>，
/// 想证明「帧率无关」就必须真的去等待。这里注入虚拟时钟让两者共用同一个时间源，
/// 从而能在毫秒内推进任意长度的仿真时间。
///
/// ⚠️ 每个 <see cref="OverlayController"/> 都持有一整套 D3D 设备 + 交换链 + 抓屏资源，
/// 必须**用完立刻 Stop()**：晚些版本把它们攒到类级 Dispose 才释放，直接把测试进程撑到 OOM。
///
/// ⚠️ **别拿「不同帧率下终点坐标相同」当断言**（第一版就是这么写的，实测失败）：
/// 鼠标是**每 Tick 采样**的，60Hz 下目标每 16.7ms 跳一次且跳到已提前的位置，
/// 240Hz 下每 4.2ms 跳一次 —— 弹簧追的阶梯粒度不同，滞后量必然不同（实测差 14px）。
/// 这是采样的固有性质，不是 bug，改不动也不该改。
/// 真正能断言、也确实是 B1 的不变量是「**累计物理时间 == 真实经过的时间**」。
/// </summary>
public class FrameRateIndependenceTests
{
    private const double StartX = 400.0;
    private const double StartY = 300.0;

    /// <summary>仿真时长（秒）。</summary>
    private const double DurationSeconds = 0.5;

    /// <summary>
    /// 鼠标移动速度（px/s）。取 1000 是为了让**每个 Tick 的整像素位置都发生变化** ——
    /// 速度太低时取整会让相邻 Tick 位置相同，<c>moving</c> 判定抖动，节流行为就不确定了。
    /// </summary>
    private const double MouseSpeed = 1000.0;

    private readonly record struct Run(double X, double Y, int Renders, double PhysicsSeconds);

    /// <summary>
    /// 按给定的 Tick 频率跑一段仿真。鼠标位置是**虚拟时间的函数**，
    /// 所以不论 Tick 多密，走的一定是同一条轨迹。
    /// </summary>
    /// <param name="targetFrameRate">设置里的目标帧率（决定节流阈值）。</param>
    /// <param name="tickRate">驱动 Tick 的频率（Hz）。高于 <paramref name="targetFrameRate"/> 时会被节流。</param>
    private static Run Simulate(double targetFrameRate, double tickRate)
    {
        double now = 0.0;

        var settings = new SettingsModel
        {
            TargetFrameRate = targetFrameRate,
            LensEnabled = false,
            FollowEnabled = true,        // 必须开惯性跟随：关掉时是硬吸附，没有物理可言
            TransitionEnabled = false,   // 不播出现动画，避免它强制满帧干扰节流
            IdleWanderEnabled = false,
            DiskSpeed = 0,               // 盘静止 → 允许进入降帧分支
        };

        using var window = new Win32OverlayWindow();
        var mouse = new TimelineMouse(
            () => now,
            t => (int)(StartX + MouseSpeed * t),
            _ => (int)StartY);

        // OverlayController 用 Stop() 释放（不是 IDisposable）
        var controller = new OverlayController(
            settings,
            window,
            mouse,
            preferHardware: false,
            captureFactory: null,
            clock: () => now);

        try
        {
            controller.Start();

            double tickStep = 1.0 / tickRate;
            int ticks = (int)Math.Round(DurationSeconds * tickRate);

            for (int i = 1; i <= ticks; i++)
            {
                now = i * tickStep;
                controller.Tick();
            }

            var (x, y) = controller.VisualPosition;
            return new Run(x, y, controller.RenderCount, controller.ElapsedSeconds);
        }
        finally
        {
            controller.Stop();
        }
    }

    /// <summary>
    /// B1 的真正不变量：**推进的物理时间 == 真实经过的时间**，
    /// 与帧率无关、与被节流跳过多少帧无关。
    /// </summary>
    [Theory]
    [InlineData(60.0, 60.0)]
    [InlineData(165.0, 165.0)]
    [InlineData(240.0, 240.0)]
    [InlineData(60.0, 1000.0)]      // Tick 空转到 1000Hz，大量帧被节流跳过
    [InlineData(165.0, 1000.0)]
    [InlineData(240.0, 1000.0)]
    public void PhysicsTime_MatchesElapsedTime_RegardlessOfFrameRate(double targetFrameRate, double tickRate)
    {
        var run = Simulate(targetFrameRate, tickRate);

        // 缺口 = 最后一次渲染之后到仿真结束之间、还攒在 _pendingSeconds 里没消费的时间。
        // 它的上限是一个**目标帧间隔**（不是 Tick 间隔）：节流越狠，末尾没消费的越多。
        // 这是正确行为 —— 那段时间的画面还没画出来，物理也不该提前走。
        double tolerance = 1.0 / targetFrameRate + 1.0 / tickRate + 1e-6;

        Assert.True(Math.Abs(run.PhysicsSeconds - DurationSeconds) <= tolerance,
            $"目标 {targetFrameRate}Hz / Tick {tickRate}Hz：物理推进 {run.PhysicsSeconds:F4}s，" +
            $"应约等于经过的 {DurationSeconds:F4}s（容差 {tolerance:F4}s）");
    }

    /// <summary>
    /// 行为层面的佐证：Tick 被大量节流（1000Hz 空转）与不被节流相比，
    /// 终点位置应当一致 —— 修复前这里会明显「走得更慢」。
    /// </summary>
    [Theory]
    [InlineData(60.0)]
    [InlineData(165.0)]
    [InlineData(240.0)]
    public void WhenTheLoopTicksFasterThanItRenders_PhysicsStillAdvancesByRealTime(double targetFrameRate)
    {
        const double tickRate = 1000.0;
        var throttled = Simulate(targetFrameRate, tickRate);
        var unthrottled = Simulate(targetFrameRate, targetFrameRate);

        // 确认这个场景真的在节流，否则下面那条断言是空转
        Assert.True(throttled.Renders < (int)Math.Round(DurationSeconds * tickRate * 0.9),
            $"{targetFrameRate}Hz 目标在 {tickRate}Hz 的 Tick 下应当有大量帧被跳过，实际渲染 {throttled.Renders} 帧");

        AssertSamePlace(unthrottled, throttled, $"{targetFrameRate}Hz 节流 vs 不节流");
    }

    /// <summary>
    /// 单步上限：一次卡顿不应该让黑洞瞬移过去，
    /// 超过 <see cref="FollowState.MaxTimeStep"/> 的部分必须丢弃。
    /// </summary>
    [Fact]
    public void AHugeTimeStall_DoesNotTeleportTheBlackHole()
    {
        double now = 0.0;

        var settings = new SettingsModel
        {
            LensEnabled = false,
            FollowEnabled = true,
            FollowMaxSpeed = 500,
            TransitionEnabled = false,
            DiskSpeed = 0,
        };

        using var window = new Win32OverlayWindow();

        // 起点之后跳到 3000px 外：如果不限步，一次 2 秒的推进会直接把它拉过去
        var mouse = new TimelineMouse(
            () => now,
            t => t <= 0.0 ? (int)StartX : (int)StartX + 3000,
            _ => (int)StartY);

        var controller = new OverlayController(
            settings,
            window,
            mouse,
            preferHardware: false,
            clock: () => now);

        try
        {
            controller.Start();
            now = 2.0;                   // 模拟卡了两秒
            controller.Tick();

            double travelled = controller.VisualPosition.X - StartX;
            double ceiling = FollowState.MaxTimeStep * settings.FollowMaxSpeed;

            Assert.True(travelled <= ceiling + 1e-6,
                $"卡顿后单帧位移 {travelled:F1}px 不得超过 MaxTimeStep×MaxSpeed = {ceiling:F1}px");
        }
        finally
        {
            controller.Stop();
        }
    }

    private static void AssertSamePlace(Run expected, Run actual, string scenario)
    {
        double distance = Math.Abs(actual.X - expected.X);

        // 同一目标帧率下两种 Tick 节奏的终点应当一致（差一点点来自最后一帧的余量）
        Assert.True(distance <= MouseSpeed * DurationSeconds * 0.02,
            $"{scenario}：终点横坐标应基本一致，" +
            $"实际 {expected.X:F2} vs {actual.X:F2}（差 {distance:F2}px）");
    }

    /// <summary>
    /// 位置由**虚拟时间**决定 —— 这是「同样鼠标轨迹」在所有 Tick 频率下都成立的前提。
    /// 若直接按 Tick 计数推进鼠标，高频 Tick 下的轨迹就与低频不同，断言会失去意义。
    /// </summary>
    private sealed class TimelineMouse : IMousePositionSource
    {
        private readonly Func<double> _clock;
        private readonly Func<double, int> _x;
        private readonly Func<double, int> _y;

        public TimelineMouse(Func<double> clock, Func<double, int> x, Func<double, int> y)
        {
            _clock = clock;
            _x = x;
            _y = y;
        }

        public (int X, int Y) GetPosition()
        {
            double t = _clock();
            return (_x(t), _y(t));
        }
    }
}
