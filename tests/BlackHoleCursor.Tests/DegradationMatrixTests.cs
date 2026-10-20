using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Capture;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M6.2：异常与降级矩阵 —— 每种失败都要有**明确行为**，且**不崩**。
///
/// 矩阵的三条路径：
/// <list type="bullet">
/// <item>设备丢失 → 重建（最多 3 次）→ 超限则停止渲染并恢复系统光标；</item>
/// <item>可恢复的 Present 失败 → 跳过本帧，连续 120 次才认输；</item>
/// <item>驱动内部错误 / 着色器编译失败 → 明确失败原因，不重试。</item>
/// </list>
/// </summary>
public class DegradationMatrixTests
{
    [Theory]
    [InlineData(0, FailureAction.Rebuild)]
    [InlineData(1, FailureAction.Rebuild)]
    [InlineData(2, FailureAction.Rebuild)]
    [InlineData(3, FailureAction.Stop)]
    public void DeviceLost_RebuildsUntilTheCap(int rebuildsAlreadyDone, FailureAction expected)
        => Assert.Equal(
            expected,
            RenderFailure.Decide(PresentFailureKind.DeviceLost, 1, rebuildsAlreadyDone));

    [Fact]
    public void Fatal_StopsImmediately()
        => Assert.Equal(FailureAction.Stop, RenderFailure.Decide(PresentFailureKind.Fatal, 1, 0));

    [Theory]
    [InlineData(1, FailureAction.SkipFrame)]
    [InlineData(2, FailureAction.SkipFrame)]
    [InlineData(119, FailureAction.SkipFrame)]
    [InlineData(120, FailureAction.Stop)]
    public void Recoverable_SkipsFramesUntilTheCap(int consecutiveFailures, FailureAction expected)
        => Assert.Equal(
            expected,
            RenderFailure.Decide(PresentFailureKind.Recoverable, consecutiveFailures, 0));

    /// <summary>整张矩阵按「真实错误码」走一遍，防止分类与处置两层之间对不上。</summary>
    [Theory]
    [InlineData(RenderFailure.DeviceRemoved, 1, 0, FailureAction.Rebuild)]
    [InlineData(RenderFailure.DeviceHung, 1, 2, FailureAction.Rebuild)]
    [InlineData(RenderFailure.DeviceReset, 1, 3, FailureAction.Stop)]
    [InlineData(RenderFailure.DriverInternalError, 1, 0, FailureAction.Stop)]
    [InlineData(unchecked((int)0x80004005), 1, 0, FailureAction.SkipFrame)]
    [InlineData(unchecked((int)0x80004005), 120, 0, FailureAction.Stop)]
    public void TheMatrix_HoldsForRealHresults(int hresult, int failures, int rebuilds, FailureAction expected)
        => Assert.Equal(expected, RenderFailure.Decide(RenderFailure.Classify(hresult), failures, rebuilds));

    [Fact]
    public void DeviceRebuildCap_IsSharedWithTheController()
    {
        // 矩阵里的上限必须与 OverlayController 实际使用的同一个值，否则测试测的是另一套规则
        Assert.Equal(OverlayController.MaxDeviceRebuilds, RenderFailure.MaxDeviceRebuilds);
        Assert.Equal(OverlayController.MaxConsecutivePresentFailures, RenderFailure.MaxConsecutivePresentFailures);
    }

    /// <summary>
    /// 抓屏彻底不可用（主方案与备用方案都失败）时：降级标记要置上，
    /// 但黑洞本体**仍然要画** —— 只丢透镜层，不能整个覆盖层消失。
    /// </summary>
    [Fact]
    public void CaptureFailure_StillDrawsTheBlackHole()
    {
        var settings = new SettingsModel { LensEnabled = true };

        // 必须是真实窗口：DirectComposition 的 CreateSurfaceFromHwnd 需要一个真 HWND
        var window = new RecordingOverlayWindow(new Win32OverlayWindow());
        var mouse = new FakeMousePositionSource(600, 400);

        var controller = new OverlayController(
            settings,
            window,
            mouse,
            preferHardware: false,
            captureFactory: _ =>
            {
                var primary = new ScriptedCapture("primary", CaptureStatus.Unavailable);
                var fallback = new ScriptedCapture("fallback", CaptureStatus.Unavailable);
                return new CaptureCoordinator(primary, fallback);
            });

        try
        {
            controller.Start();
            controller.Tick();

            // 抓屏失败不该连黑洞一起不画
            Assert.Equal(1, controller.RenderCount);
            Assert.True(controller.IsDegraded, "必须标记为已降级，主界面才能提示");
            Assert.True(controller.CaptureFailureCount > 0);
            Assert.False(controller.IsDeviceLost, "抓屏失败不是设备丢失，不该停止渲染");
        }
        finally
        {
            controller.Stop();
            window.Dispose();
        }
    }

    /// <summary>
    /// 着色器编译失败必须是一个**可识别**的异常：覆盖层线程靠它决定
    /// 「记录原因 + 恢复系统光标 + 主界面显示渲染不可用」，而不是吞掉或崩掉。
    /// </summary>
    [Fact]
    public void ShaderCompilationFailure_IsAnIdentifiableException()
    {
        using var device = RenderDevice.Create(preferHardware: false);
        using var renderer = new SceneRenderer(device.Device);

        var error = Assert.Throws<ShaderCompilationException>(
            () => renderer.CreatePixelShader("DefinitelyNotAnEntryPoint"));

        Assert.Equal("DefinitelyNotAnEntryPoint", error.EntryPoint);
        Assert.Contains("DefinitelyNotAnEntryPoint", error.Message);
        Assert.NotNull(error.InnerException);
    }

    /// <summary>正常入口必须仍然编译通过（防上面的包装把成功路径也变成异常）。</summary>
    [Fact]
    public void TheProductionShader_StillCompiles()
    {
        using var device = RenderDevice.Create(preferHardware: false);
        using var renderer = new SceneRenderer(device.Device);

        using var shader = renderer.CreatePixelShader(ShaderSources.PixelEntryPoint);

        Assert.NotNull(shader);
    }
}
