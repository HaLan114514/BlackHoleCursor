using System.Runtime.InteropServices;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Capture;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>记录每次抓屏请求的矩形，并把目标纹理填成中灰（避免被判成全黑帧）。</summary>
public sealed class RecordingCapture : IDesktopCapture
{
    public RecordingCapture(string name = "recording") => Name = name;

    public string Name { get; }

    public bool IsAvailable => true;

    public List<(int Left, int Top, int Width, int Height)> Requests { get; } = new();

    /// <summary>每次抓屏在**目标纹理**内的落点（局部增量更新用），与 Requests 一一对应。</summary>
    public List<(int X, int Y)> Dests { get; } = new();

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
        Requests.Add((left, top, width, height));
        Dests.Add((destX, destY));

        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)128);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // 目标纹理是整块显示器大小，本次只抓窗口附近一块。
            // 传 null 会让 D3D 按整块纹理的尺寸去读源缓冲 → 越界 → AccessViolation，必须传 Box。
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

/// <summary>可以在测试中移动的鼠标替身。</summary>
public sealed class MovableMouse : IMousePositionSource
{
    public MovableMouse(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; set; }

    public int Y { get; set; }

    public (int X, int Y) GetPosition() => (X, Y);
}

/// <summary>
/// fixes-v2 **问题 5：抖动** —— 抓屏架构改造的回归测试。
///
/// 根因：旧实现抓的是「窗口那一块」，于是窗口移到 P(t)、抓屏还是 P(t−1) 的内容，
/// 背景相对黑洞每帧滑一个 Δ，鼠标动得越快抖得越狠。
/// 修复（照参考实现 singularity 的做法）：抓**整块显示器**，区域只由光标所在的
/// 显示器决定，与黑洞 / 窗口位置完全解耦；着色器取样前用 `CaptureMap` 把
/// 窗口 uv 换算到显示器 uv。
/// </summary>
public class CaptureStabilityTests
{
    /// <summary>
    /// 核心不变量：**写进抓屏纹理的落点，必须等于该区域在屏幕上的位置**（相对显示器原点）。
    ///
    /// 这条直接锁住「背景不会相对黑洞滑动」—— 纹理锚在显示器坐标上，
    /// 无论窗口/黑洞怎么移动，同一块屏幕内容在纹理里的位置都不变，所以不会滑。
    ///
    /// ⚠️ 这里**不再**断言「抓屏矩形不随光标变化」：
    /// fixes-v2 问题 5 的第三刀改成每帧只增量更新窗口附近那一块（为了便宜到能每帧抓），
    /// 抓取矩形当然跟着窗口走；真正的不变量是**落点与源位置严格对应**，而不是矩形固定。
    /// </summary>
    [Fact]
    public void CaptureRegion_IsWrittenToTheMatchingPlaceInTheTexture()
    {
        var capture = new RecordingCapture();
        var window = new RecordingOverlayWindow(new Win32OverlayWindow());
        var mouse = new MovableMouse(500, 400);
        var settings = new SettingsModel { LensEnabled = true, FollowEnabled = false };

        double now = 0.0;
        var controller = new OverlayController(
            settings,
            window,
            mouse,
            preferHardware: false,
            captureFactory: _ => new CaptureCoordinator(capture, new RecordingCapture("fallback")),
            clock: () => now);

        try
        {
            controller.Start();

            // 每帧间隔取 0.2s：抓屏有自适应节流（上限 100ms），
            // 间隔太小会被跳过，取 0.2s 保证每帧都真的抓一次，才能比对多次抓屏请求。
            const double step = 0.2;

            // 前 3 帧：光标在原位
            for (int i = 1; i <= 3; i++)
            {
                now = i * step;
                controller.Tick();
            }

            // 后 3 帧：光标大幅移动（覆盖层窗口会跟着走）
            mouse.X = 900;
            mouse.Y = 700;
            for (int i = 4; i <= 6; i++)
            {
                now = i * step;
                controller.Tick();
            }
        }
        finally
        {
            controller.Stop();
            window.Dispose();
        }

        Assert.True(capture.Requests.Count >= 2, $"应当抓到多帧，实测 {capture.Requests.Count} 次");
        Assert.Equal(capture.Requests.Count, capture.Dests.Count);

        var monitor = ScreenMetrics.MonitorRectAt(500, 400);

        for (int i = 0; i < capture.Requests.Count; i++)
        {
            var (left, top, _, _) = capture.Requests[i];
            var (destX, destY) = capture.Dests[i];

            // 落点 == 源区域相对显示器原点的位置。这条一旦错，背景就会相对黑洞滑动。
            Assert.Equal(left - monitor.Left, destX);
            Assert.Equal(top - monitor.Top, destY);
        }

        // 最后一次抓取必须覆盖**窗口在显示器内的那部分**（局部增量更新不能漏掉可见区域）。
        // 注意不能要求覆盖整个窗口：窗口 872px 高，光标靠近屏幕下边缘时
        // 它本来就有半截在显示器外面，抓屏自然取不到。
        var last = capture.Requests[^1];
        int wx0 = Math.Max(window.LastX, monitor.Left);
        int wy0 = Math.Max(window.LastY, monitor.Top);
        int wx1 = Math.Min(window.LastX + window.LastWidth, monitor.Right);
        int wy1 = Math.Min(window.LastY + window.LastHeight, monitor.Bottom);

        Assert.True(last.Left <= wx0, $"抓取区域应覆盖窗口可见左边界：{last.Left} vs {wx0}");
        Assert.True(last.Top <= wy0, $"抓取区域应覆盖窗口可见上边界：{last.Top} vs {wy0}");
        Assert.True(last.Left + last.Width >= wx1,
            $"抓取区域应覆盖窗口可见右边界：{last.Left + last.Width} vs {wx1}");
        Assert.True(last.Top + last.Height >= wy1,
            $"抓取区域应覆盖窗口可见下边界：{last.Top + last.Height} vs {wy1}");
    }

    /// <summary>窗口 uv → 抓屏纹理 uv 的映射（纯函数）。</summary>
    [Fact]
    public void CaptureMap_MapsWindowUvIntoTheCaptureTexture()
    {
        // 窗口 400×400 位于屏幕 (1000, 500)；抓的是整块 1920×1080 显示器（原点 0,0）
        var map = OverlayGeometry.CaptureMap(
            windowLeft: 1000, windowTop: 500, windowWidth: 400, windowHeight: 400,
            captureLeft: 0, captureTop: 0, captureWidth: 1920, captureHeight: 1080);

        Assert.Equal(400.0 / 1920.0, map.ScaleX, 9);
        Assert.Equal(400.0 / 1080.0, map.ScaleY, 9);
        Assert.Equal(1000.0 / 1920.0, map.OffsetX, 9);
        Assert.Equal(500.0 / 1080.0, map.OffsetY, 9);

        // 窗口中心 (uv=0.5) 应落在屏幕 (1200, 700) → 显示器 uv
        double uvX = 0.5 * map.ScaleX + map.OffsetX;
        double uvY = 0.5 * map.ScaleY + map.OffsetY;
        Assert.Equal(1200.0 / 1920.0, uvX, 9);
        Assert.Equal(700.0 / 1080.0, uvY, 9);
    }

    /// <summary>
    /// v3（P1.5）：窗口 == 整块显示器 ⇒ 映射必须退化为**恒等**（缩放 1、偏移 0）。
    /// 全屏后窗口 uv 就是显示器 uv，不再需要换算；这条锁住「万一没退化」能被立刻发现。
    /// </summary>
    [Fact]
    public void CaptureMap_IsIdentityWhenWindowIsTheWholeMonitor()
    {
        var map = OverlayGeometry.CaptureMap(
            windowLeft: 0, windowTop: 0, windowWidth: 1920, windowHeight: 1080,
            captureLeft: 0, captureTop: 0, captureWidth: 1920, captureHeight: 1080);

        Assert.Equal(1.0, map.ScaleX, 9);
        Assert.Equal(1.0, map.ScaleY, 9);
        Assert.Equal(0.0, map.OffsetX, 9);
        Assert.Equal(0.0, map.OffsetY, 9);
    }

    /// <summary>
    /// 同上，但显示器原点**非零**（副屏在左侧时坐标是负的）——
    /// 窗口与抓取区域同一个原点，仍应恒等。这是最容易写错的一种情形。
    /// </summary>
    [Fact]
    public void CaptureMap_IsIdentityOnASecondaryMonitor()
    {
        var map = OverlayGeometry.CaptureMap(
            windowLeft: -1920, windowTop: 100, windowWidth: 1920, windowHeight: 1080,
            captureLeft: -1920, captureTop: 100, captureWidth: 1920, captureHeight: 1080);

        Assert.Equal(1.0, map.ScaleX, 9);
        Assert.Equal(1.0, map.ScaleY, 9);
        Assert.Equal(0.0, map.OffsetX, 9);
        Assert.Equal(0.0, map.OffsetY, 9);
    }

    /// <summary>
    /// 未给出映射时按**恒等**处理 —— 预览与单元测试的底图本来就是窗口那一块。
    /// </summary>
    [Fact]
    public void CaptureMap_DefaultsToIdentity()
    {
        var description = new SceneDescription(256, 256, 128, 128, new SettingsModel(), 0.0, true);
        var constants = SceneParamsBuilder.Build(description);

        Assert.Equal(1.0f, constants.CaptureMap.X);
        Assert.Equal(1.0f, constants.CaptureMap.Y);
        Assert.Equal(0.0f, constants.CaptureMap.Z);
        Assert.Equal(0.0f, constants.CaptureMap.W);
    }

    /// <summary>给了映射就必须原样传进着色器（防"算了但没传"）。</summary>
    [Fact]
    public void CaptureMap_IsForwardedToTheShader()
    {
        var description = new SceneDescription(
            256, 256, 128, 128, new SettingsModel(), 0.0, true,
            CaptureMap: new System.Numerics.Vector4(0.25f, 0.5f, 0.1f, 0.2f));

        var constants = SceneParamsBuilder.Build(description);

        Assert.Equal(0.25f, constants.CaptureMap.X, 6);
        Assert.Equal(0.5f, constants.CaptureMap.Y, 6);
        Assert.Equal(0.1f, constants.CaptureMap.Z, 6);
        Assert.Equal(0.2f, constants.CaptureMap.W, 6);
    }

    /// <summary>着色器必须真的用上这层映射，而不是把它当摆设。</summary>
    [Fact]
    public void Background_AppliesTheCaptureMap()
    {
        Assert.Contains("CaptureMap", ShaderSources.FullSource);
        Assert.Contains("uvWindow * CaptureMap.xy + CaptureMap.zw", ShaderSources.FullSource);
    }
}
