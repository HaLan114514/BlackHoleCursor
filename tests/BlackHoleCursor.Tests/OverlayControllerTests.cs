using System.Runtime.InteropServices;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Capture;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// 把目标纹理填成纯色的抓屏替身。用于在真实 D3D11 设备上验证
/// 「抓屏成功 → 全黑帧检测」这条链路，不受当前桌面是否有可见内容影响。
/// </summary>
public sealed class SolidColorCapture : IDesktopCapture
{
    private readonly byte _b;
    private readonly byte _g;
    private readonly byte _r;

    public SolidColorCapture(string name, byte b, byte g, byte r)
    {
        Name = name;
        _b = b;
        _g = g;
        _r = r;
    }

    public string Name { get; }

    public bool IsAvailable => true;

    public int CaptureCount { get; private set; }

    public CaptureStatus TryCapture(
        ID3D11DeviceContext context,
        ID3D11Texture2D destination,
        int left,
        int top,
        int width,
        int height,
        int destX = 0,
        int destY = 0)
    {
        CaptureCount++;

        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = _b;
            pixels[i + 1] = _g;
            pixels[i + 2] = _r;
            pixels[i + 3] = 0xFF;
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // 抓屏改成「整块显示器纹理 + 局部增量更新」后，目标纹理比本次抓取区域大。
            // 传 null（写整块纹理）会**越界读源缓冲**并直接 AccessViolation —— 必须传 Box。
            var description = destination.Description;
            bool partial = destX != 0 || destY != 0
                || width != description.Width || height != description.Height;

            Vortice.Mathematics.Box? box = partial
                ? new Vortice.Mathematics.Box(destX, destY, 0, destX + width, destY + height, 1)
                : null;

            context.UpdateSubresource(
                destination,
                0,
                box,
                handle.AddrOfPinnedObject(),
                (uint)(width * 4),
                0);
        }
        finally
        {
            handle.Free();
        }

        return CaptureStatus.Success;
    }

    public void ReleaseFrame()
    {
    }

    public void Recreate()
    {
    }

    public void SelectOutput(int centerX, int centerY)
    {
    }

    public void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>包装真实覆盖层窗口并记录调用，用于断言窗口位置与可见性。</summary>
public sealed class RecordingOverlayWindow : IOverlayWindow
{
    private readonly IOverlayWindow? _inner;

    public RecordingOverlayWindow(IOverlayWindow? inner = null) => _inner = inner;

    public int LastX { get; private set; }

    public int LastY { get; private set; }

    public int LastWidth { get; private set; }

    public int LastHeight { get; private set; }

    public bool Visible { get; private set; }

    public int MoveCount { get; private set; }

    public IntPtr Handle => _inner?.Handle ?? IntPtr.Zero;

    public bool IsCreated => _inner?.IsCreated ?? true;

    public void Move(int x, int y, int width, int height)
    {
        LastX = x;
        LastY = y;
        LastWidth = width;
        LastHeight = height;
        MoveCount++;
        _inner?.Move(x, y, width, height);
    }

    public int TopmostCount { get; private set; }

    public void EnsureTopmost()
    {
        TopmostCount++;
        _inner?.EnsureTopmost();
    }

    public void SetVisible(bool visible)
    {
        Visible = visible;
        _inner?.SetVisible(visible);
    }

    public void Dispose() => _inner?.Dispose();
}

public class OverlayControllerTests
{
    private const int MouseX = 500;
    private const int MouseY = 400;

    private static (OverlayController controller, RecordingOverlayWindow window, FakeMousePositionSource mouse, SettingsModel settings) Create(
        Action<SettingsModel>? configure = null,
        Func<ID3D11Device, CaptureCoordinator>? captureFactory = null)
    {
        var settings = new SettingsModel();
        configure?.Invoke(settings);

        var real = new Win32OverlayWindow();
        var window = new RecordingOverlayWindow(real);
        var mouse = new FakeMousePositionSource(MouseX, MouseY);
        var controller = new OverlayController(settings, window, mouse, preferHardware: false, captureFactory);
        controller.Start();
        return (controller, window, mouse, settings);
    }

    /// <summary>构造一个「抓屏永远返回同一纯色」的工厂。</summary>
    private static Func<ID3D11Device, CaptureCoordinator> SolidCapture(byte b, byte g, byte r)
        => _ => new CaptureCoordinator(
            new SolidColorCapture("solid-primary", b, g, r),
            new SolidColorCapture("solid-fallback", b, g, r));

    private static void Dispose(OverlayController controller, RecordingOverlayWindow window)
    {
        controller.Stop();
        window.Dispose();
    }

    [Fact]
    public void Start_MarksControllerRunning()
    {
        var (controller, window, _, _) = Create();

        try
        {
            Assert.True(controller.IsRunning);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void Tick_RendersAndShowsWindowNearMouse()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();

            Assert.Equal(1, controller.RenderCount);
            Assert.True(window.Visible);
            Assert.Equal(1, window.MoveCount);

            // v3（P1.1）：窗口不再是「贴着鼠标的小窗」，而是整块显示器。
            // 所以判定从「窗口中心对齐鼠标」改成「窗口 == 鼠标所在显示器的矩形」，
            // 并且鼠标必须落在窗口内（黑洞现在靠 uniform 定位，见 P1.2）。
            var monitor = ScreenMetrics.MonitorRectAt(MouseX, MouseY);
            Assert.Equal(monitor.Left, window.LastX);
            Assert.Equal(monitor.Top, window.LastY);
            Assert.Equal(monitor.Right - monitor.Left, window.LastWidth);
            Assert.Equal(monitor.Bottom - monitor.Top, window.LastHeight);

            Assert.InRange(MouseX, window.LastX, window.LastX + window.LastWidth);
            Assert.InRange(MouseY, window.LastY, window.LastY + window.LastHeight);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// v3（P1.5）**防回归**：每帧必须刷新「本帧外观」与「显示器高度」。
    ///
    /// P1.1 把窗口尺寸的计算（`ComputeV2Radius`）从 Tick 里删掉时，
    /// 顺带把挂在它内部的 `_look = CurrentLook()` 与 `_screenHeight = ...` 也弄丢了 ——
    /// 黑洞会永远用默认外观、且屏高按 0 换算，而**窗口几何类断言一条都不会失败**。
    /// 这里用新加的只读诊断属性把这条锁住。
    /// </summary>
    [Fact]
    public void Tick_RefreshesTheLookAndScreenHeight()
    {
        var (controller, window, _, settings) = Create(s => s.DiskTemp = 3000);

        try
        {
            controller.Tick();

            Assert.True(controller.EffectiveScreenHeight > 0,
                "屏高必须每帧刷新（P1.1 曾把它弄丢，导致 HoleSize 按 0 换算）");
            Assert.Equal(3000.0, controller.EffectiveLook.TempKelvin, 6);

            settings.DiskTemp = 12000;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.Equal(12000.0, controller.EffectiveLook.TempKelvin, 6);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// v3（P1.1）**主判据**：窗口**不跟随光标**。
    ///
    /// 旧实现每帧按 `_follow` 重算矩形并 `Move`，而 Win32 只能按整数像素移动 ——
    /// 这个量化误差会传到黑洞定位上，并被透镜放大率放大，
    /// 正是 fixes-v2 问题 7「只有折射过的地方抖」的根因。
    /// 现在窗口固定为整块显示器，只应在显示器变化时动。
    /// </summary>
    [Fact]
    public void WindowRect_DoesNotFollowTheCursor()
    {
        var (controller, window, mouse, _) = Create();

        try
        {
            controller.Tick();
            int x0 = window.LastX;
            int y0 = window.LastY;
            int w0 = window.LastWidth;
            int h0 = window.LastHeight;
            int moves = window.MoveCount;

            // 把光标大幅移到同一显示器内的另一处，并连跑多帧
            mouse.X = MouseX + 120;
            mouse.Y = MouseY + 90;
            for (int i = 0; i < 5; i++)
            {
                controller.Tick();
            }

            Assert.Equal(x0, window.LastX);
            Assert.Equal(y0, window.LastY);
            Assert.Equal(w0, window.LastWidth);
            Assert.Equal(h0, window.LastHeight);
            Assert.Equal(moves, window.MoveCount);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// v3（P1.1）：窗口不再需要「装下盘与远场」—— 它本来就是整块显示器。
    /// 这里锁住那个矩形确实等于显示器，而不是某个推算出来的半径。
    /// </summary>
    [Fact]
    public void WindowRect_EqualsTheWholeMonitor()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();

            var monitor = ScreenMetrics.MonitorRectAt(MouseX, MouseY);
            Assert.Equal(monitor.Left, window.LastX);
            Assert.Equal(monitor.Top, window.LastY);
            Assert.Equal(monitor.Right - monitor.Left, window.LastWidth);
            Assert.Equal(monitor.Bottom - monitor.Top, window.LastHeight);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>v2 下「是否抓屏做透镜」与窗口尺寸无关（尺寸只看黑洞本身的大小）。</summary>
    [Fact]
    public void LensEnabled_DoesNotChangeWindowSize()
    {
        var (controllerWithLens, windowWithLens, _, _) = Create();

        int withLens;
        try
        {
            controllerWithLens.Tick();
            withLens = windowWithLens.LastWidth;
        }
        finally
        {
            Dispose(controllerWithLens, windowWithLens);
        }

        var (controllerNoLens, windowNoLens, _, _) = Create(s => s.LensEnabled = false);

        try
        {
            controllerNoLens.Tick();

            Assert.Equal(withLens, windowNoLens.LastWidth);
        }
        finally
        {
            Dispose(controllerNoLens, windowNoLens);
        }
    }

    /// <summary>
    /// v3（P1.1）：窗口尺寸**与 HoleSize 无关**。
    /// 旧实现里窗口半径由洞径推算（换来的代价是每次拖滑块都要重建交换链，且会顶到 1024 上限）；
    /// 全屏之后洞径只影响画面里的黑洞大小，不再影响窗口。
    /// </summary>
    [Fact]
    public void HoleSizeChange_DoesNotResizeWindow()
    {
        var (controller, window, _, _) = Create();

        try
        {
            // 直接注入外观：默认每帧会从 SettingsModel 重新映射
            controller.UseExplicitLook = true;

            controller.Look.HoleSize = 0.03;
            controller.Tick();
            int narrow = window.LastWidth;

            controller.Look.HoleSize = 0.09;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.Equal(narrow, window.LastWidth);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    private static double ShadowPixels(OverlayController controller)
    {
        int monitorHeight = ScreenMetrics.MonitorHeightAt(MouseX, MouseY);
        return controller.Look.HoleSize * (monitorHeight > 0 ? monitorHeight : 1080.0);
    }

    /// <summary>
    /// v3（P1.5）：`ComputeV2WindowRadius` 不再决定窗口尺寸，这里也不再有「peakScale」可传 ——
    /// 窗口是整块显示器。保留这个助手只为其它断言提供「黑洞影响半径」的参考值。
    /// </summary>
    private static double ExpectedInfluenceRadius(OverlayController controller)
    {
        double shadow = ShadowPixels(controller);
        double disk = OverlayGeometry.DiskOuterRadiusPixels(controller.Look.OuterRadius, shadow);
        return OverlayGeometry.ComputeV2WindowRadius(shadow, disk);
    }

    [Fact]
    public void Tick_ReassertsTopmostOnTheFirstFrame()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();

            // 覆盖层要盖住任务栏，必须主动重申置顶（WS_EX_TOPMOST 只是初始状态）
            Assert.Equal(1, window.TopmostCount);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void Tick_DoesNotReassertTopmostEveryFrame()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();
            int afterFirst = window.TopmostCount;

            // 连续几帧不应重复重申（间隔 TopmostReassertSeconds，避免每帧一次 SetWindowPos）
            controller.Tick();
            controller.Tick();
            controller.Tick();

            Assert.Equal(afterFirst, window.TopmostCount);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void Disabled_TakesOverNeitherCursorNorWindow()
    {
        // 关掉动画：这条测的是「禁用即收手」，不该被消失动画的时长牵扯
        var (controller, window, _, settings) = Create(s => s.TransitionEnabled = false);

        try
        {
            settings.Enabled = false;
            controller.Tick();

            Assert.False(window.Visible);
            Assert.Equal(0, controller.RenderCount);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void FollowDisabled_VisualPositionMatchesMouse()
    {
        var (controller, window, _, _) = Create(s => s.LensEnabled = false);

        try
        {
            controller.Tick();

            Assert.Equal(MouseX, controller.VisualPosition.X, 6);
            Assert.Equal(MouseY, controller.VisualPosition.Y, 6);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void FollowEnabled_VisualPositionLagsBehind()
    {
        var (controller, window, mouse, _) = Create(s =>
        {
            s.LensEnabled = false;
            s.FollowEnabled = true;
            s.FollowMaxSpeed = 60;
            s.FollowSnapDistance = 0.2;
        });

        try
        {
            controller.Tick();
            mouse.X = MouseX + 900;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.True(controller.VisualPosition.X < MouseX + 900,
                "开启惯性跟随后黑洞不应瞬间跳到新位置");
            Assert.True(controller.VisualPosition.X > MouseX - 1);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    /// <summary>
    /// 窗口尺寸只由 HoleSize 与盘外径决定；改「纯外观」参数（色温）不该动窗口，
    /// 否则每调一次滑块就要重建交换链，会明显卡顿。
    /// </summary>
    public void DiskTempChange_DoesNotResizeWindow()
    {
        var (controller, window, _, settings) = Create(s => s.DiskTemp = 3000);

        try
        {
            controller.Tick();
            int narrow = window.LastWidth;

            settings.DiskTemp = 18000;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.Equal(narrow, window.LastWidth);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void Stop_PreventsFurtherRendering()
    {
        var (controller, window, _, _) = Create();

        controller.Tick();
        Dispose(controller, window);

        int count = controller.RenderCount;
        controller.Tick();

        Assert.False(controller.IsRunning);
        Assert.Equal(count, controller.RenderCount);
    }

    [Fact]
    public void RepeatedTicks_AdvanceFramesPerSecond()
    {
        var (controller, window, _, _) = Create();

        try
        {
            for (int i = 0; i < 4; i++)
            {
                controller.NotifySettingsChanged();
                controller.Tick();
                Thread.Sleep(20);
            }

            Assert.True(controller.RenderCount >= 4);
            Assert.True(controller.FramesPerSecond > 0);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void SwapChain_UsesPremultipliedAlpha()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();

            Assert.Contains("Premultiplied", controller.ActiveSwapEffect);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void RenderingDoesNotThrow_WhenLensCapturesDesktop()
    {
        var (controller, window, _, _) = Create(s =>
        {
            s.LensEnabled = true;
        });

        try
        {
            controller.Tick();

            Assert.Equal(1, controller.RenderCount);
            Assert.False(controller.IsDeviceLost);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void Diagnostics_HaveSafeDefaults()
    {
        var (controller, window, _, _) = Create();

        try
        {
            Assert.Equal(0, controller.DeviceRebuildCount);
            Assert.False(controller.IsLensSuppressed);
            Assert.False(controller.IsDeviceLost);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>全黑帧检测要走 GPU→CPU 回读，必须确认这条链路真的能在真实设备上跑通。</summary>
    [Fact]
    public void BlackFrameSampling_RunsOnRealDevice()
    {
        var (controller, window, _, _) = Create(
            s =>
            {
                s.LensEnabled = true;
            },
            SolidCapture(0xFF, 0xFF, 0xFF));

        try
        {
            controller.Tick();

            Assert.Equal(1, controller.BlackFrameSampleCount);
            Assert.False(controller.IsLensSuppressed, "明亮画面不应被判定为全黑帧");
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>受保护内容（全黑帧）必须自动关掉扭曲层，避免出现死黑矩形。</summary>
    [Fact]
    public void AllBlackFrames_SuppressLens()
    {
        var (controller, window, _, _) = Create(
            s =>
            {
                s.LensEnabled = true;
            },
            SolidCapture(0, 0, 0));

        try
        {
            for (int i = 0; i <= OverlayController.BlackFrameSampleInterval * 2; i++)
            {
                controller.NotifySettingsChanged();
                controller.Tick();
            }

            Assert.True(controller.BlackFrameSampleCount >= BlackFrameDetector.DefaultConsecutiveFrames);
            Assert.True(controller.IsLensSuppressed, "连续全黑帧必须跳过扭曲层");
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    [Fact]
    public void SetUiActive_KeepsRenderingWithoutError()
    {
        var (controller, window, _, _) = Create(s => s.DiskSpeed = 0);

        try
        {
            controller.SetUiActive(true);
            controller.Tick();

            Assert.True(controller.IsRunning);
            Assert.Equal(1, controller.RenderCount);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// v3（P1.1）：改设置里的 <c>HoleSize</c> 不再改变窗口尺寸。
    /// 「界面滑块能控制黑洞大小」这件事改由渲染保证（洞径走 uniform），
    /// 窗口尺寸恒等于显示器。
    /// </summary>
    [Fact]
    public void SettingsHoleSize_DoesNotAffectWindowSize()
    {
        var (controller, window, _, settings) = Create(s => s.HoleSize = SettingsRanges.HoleSizeMin);

        try
        {
            controller.Tick();
            int small = window.LastWidth;

            settings.HoleSize = SettingsRanges.HoleSizeMax;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.Equal(small, window.LastWidth);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>v3（P1.1）：盘外径也不再影响窗口尺寸（窗口本来就是整块显示器）。</summary>
    [Fact]
    public void SettingsDiskOuter_DoesNotAffectWindowSize()
    {
        var (controller, window, _, settings) = Create(s =>
        {
            s.LensEnabled = false;
            s.HoleSize = SettingsRanges.HoleSizeMin;
            s.DiskOuter = SettingsRanges.DiskOuterMin;
        });

        try
        {
            controller.Tick();
            int small = window.LastWidth;

            settings.DiskOuter = SettingsRanges.DiskOuterMax;
            controller.NotifySettingsChanged();
            controller.Tick();

            Assert.Equal(small, window.LastWidth);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// v3（P1.1）：窗口矩形跨帧**完全稳定** —— 无论洞径、盘外径、过冲怎么变。
    ///
    /// 旧的「PRD 1024 性能上限」断言在这里作废：窗口现在是整块显示器，
    /// 必然超过 1024，而 P0 实测 1920×1080 全屏仍有 56~57 FPS（渲染只占 0.01ms），
    /// 当初那条上限的前提（「只渲染黑洞周围省性能」）已不成立。
    /// 换成的这条断言守的是另一件事：**连续多帧不得反复 Move / 重建**。
    /// </summary>
    [Fact]
    public void WindowRect_IsStableAcrossFrames()
    {
        var (controller, window, _, settings) = Create(s =>
        {
            s.HoleSize = SettingsRanges.HoleSizeMax;
            s.DiskOuter = SettingsRanges.DiskOuterMax;
            s.LensEnabled = true;
        });

        try
        {
            for (int i = 0; i < 40; i++)
            {
                settings.TransitionOvershoot = SettingsRanges.TransitionOvershootMax;
                controller.NotifySettingsChanged();
                controller.Tick();
            }

            Assert.Equal(1, window.MoveCount);

            var monitor = ScreenMetrics.MonitorRectAt(MouseX, MouseY);
            Assert.Equal(monitor.Right - monitor.Left, window.LastWidth);
            Assert.Equal(monitor.Bottom - monitor.Top, window.LastHeight);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>设备丢失的恢复路径就是 Stop() + Start()，必须能重新出帧。</summary>
    [Fact]
    public void StopThenStart_RebuildsRendererResources()
    {
        var (controller, window, _, _) = Create();

        try
        {
            controller.Tick();
            Assert.Equal(1, controller.RenderCount);

            controller.Stop();
            controller.Start();
            controller.Tick();

            Assert.True(controller.IsRunning);
            Assert.Equal(2, controller.RenderCount);
            Assert.False(controller.IsDeviceLost);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// N1 接入验证：开启静止游荡后，黑洞必须真的飘离鼠标；鼠标一动立刻回到鼠标位置。
    /// 覆盖层用内部 Stopwatch 计时，所以这里必须真实等待才能累积「静止时长」。
    /// </summary>
    [Fact]
    public void IdleWander_DriftsAwayThenReturnsWhenMouseMoves()
    {
        var (controller, window, mouse, _) = Create(s =>
        {
            s.LensEnabled = false;
            s.FollowEnabled = false;
            s.DiskSpeed = 0;
            s.IdleWanderEnabled = true;
            s.IdleWanderDelay = 0.2;
            s.IdleWanderRadius = 60;
            s.IdleWanderSpeed = 1.5;
        });

        try
        {
            controller.Tick();

            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(10);
                controller.Tick();
            }

            double drift = DistanceFromMouse(controller);
            Assert.True(drift > 1.0, $"静止足够久后黑洞应飘离鼠标，实际只偏了 {drift:F2} px");
            Assert.True(drift <= 61.0, $"位移不得超过游荡半径，实际 {drift:F2} px");

            // 鼠标一动：立刻退出游荡并平滑回位。
            // 这里让坐标持续变化模拟「用户正在移动鼠标」；若只动一下就停住，
            // 静止时长会重新累积，超过延迟后按设计又会开始游荡。
            for (int i = 0; i < 30; i++)
            {
                mouse.X = MouseX + 3 + (i % 2);
                Thread.Sleep(10);
                controller.Tick();
            }

            Assert.True(
                Math.Abs(controller.VisualPosition.X - mouse.X) <= 2.0,
                $"鼠标移动后黑洞应回到鼠标位置，实际 X={controller.VisualPosition.X:F2}，鼠标 {mouse.X}");
            Assert.True(Math.Abs(controller.VisualPosition.Y - MouseY) <= 2.0);
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>N1 默认关闭：静止再久也不许自己动，否则会破坏「命中点即黑洞中心」的直觉。</summary>
    [Fact]
    public void IdleWanderDisabled_NeverDrifts()
    {
        var (controller, window, _, _) = Create(s =>
        {
            s.LensEnabled = false;
            s.FollowEnabled = false;
            s.DiskSpeed = 0;
            s.IdleWanderEnabled = false;
            s.IdleWanderRadius = 120;
            s.IdleWanderSpeed = 1.5;
        });

        try
        {
            controller.Tick();

            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(10);
                controller.Tick();
            }

            Assert.True(DistanceFromMouse(controller) <= 1.0, "未开启游荡时黑洞必须严格停在鼠标位置");
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>
    /// N4：关掉总开关后必须先播完消失动画，再收起窗口 —— 否则动画根本看不见。
    /// 覆盖层用内部 Stopwatch 计时，所以这里要真实等待。
    /// </summary>
    [Fact]
    public void Disabled_KeepsTheWindowUpUntilTheExitAnimationFinishes()
    {
        var (controller, window, _, settings) = Create(s =>
        {
            s.LensEnabled = false;
            s.TransitionEnabled = true;
            s.TransitionDuration = 0.2;
        });

        try
        {
            controller.Tick();
            Assert.True(window.Visible);
            Assert.True(controller.TransitionProgress < 1.0, "启动首帧应正在播出现动画");

            // 等出现动画播完，避免干扰后面的计时
            for (int i = 0; i < 60 && !controller.IsTransitionFinished; i++)
            {
                Thread.Sleep(10);
                controller.Tick();
            }

            settings.Enabled = false;

            // 动画期间窗口必须还在：数一数收起之前撑了几帧
            int frames = 0;
            while (window.Visible && frames < 100)
            {
                Thread.Sleep(10);
                controller.Tick();
                frames++;
            }

            Assert.True(frames >= 2, $"消失动画应持续若干帧再收起窗口，实际只撑了 {frames} 帧");
            Assert.False(window.Visible, "消失动画播完后必须收起窗口");
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    /// <summary>N4：关掉动画开关时，收起必须立刻生效（退出路径要求零延迟）。</summary>
    [Fact]
    public void TransitionDisabled_HidesImmediately()
    {
        var (controller, window, _, settings) = Create(s =>
        {
            s.LensEnabled = false;
            s.TransitionEnabled = false;
        });

        try
        {
            controller.Tick();
            Assert.True(window.Visible);

            settings.Enabled = false;
            controller.Tick();

            Assert.False(window.Visible, "关闭动画后不应有任何延迟");
        }
        finally
        {
            Dispose(controller, window);
        }
    }

    // ⚠️ v3（P1.5）已删除 `WindowReservesRoomForTheOvershootPeak`：
    // 窗口是整块显示器，不需要为过冲预留空间（`TransitionState.PeakScale` 也随之删除）。
    // 过冲改由「视觉缩放」体现，断言在 `TransitionStateTests.Overshoot_ShowsUpInTheVisualScale`。

    private static double DistanceFromMouse(OverlayController controller)
    {
        double dx = controller.VisualPosition.X - MouseX;
        double dy = controller.VisualPosition.Y - MouseY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
