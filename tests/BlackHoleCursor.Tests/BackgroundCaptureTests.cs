using System.Diagnostics;
using BlackHoleCursor.Core.Capture;
using BlackHoleCursor.Core.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v2 问题 7：后台 GDI 抓屏。
///
/// 这里只验证**机制**（线程产出帧、上传非阻塞、能停止），不验证桌面内容 ——
/// 内容正确性由 <c>CaptureStabilityTests</c> 等既有用例覆盖。
/// </summary>
public class BackgroundCaptureTests : IDisposable
{
    private const int Size = 64;

    private readonly RenderDevice _device;
    private readonly ID3D11Texture2D _texture;

    public BackgroundCaptureTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _texture = _device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private static void WaitFirstFrame(BackgroundGdiCapture capture, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (capture.Version == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void ProducesFramesOnBackgroundThread()
    {
        using var capture = new BackgroundGdiCapture();
        capture.SetRegion(0, 0, Size, Size, 0, 0);
        capture.Start();

        WaitFirstFrame(capture);

        Assert.True(capture.Version > 0, "后台线程应该能产出抓屏帧");
        Assert.True(capture.LastCostMs >= 0, "抓屏耗时应被记录");
    }

    /// <summary>
    /// 核心断言：上传**不能**阻塞。
    /// 同步 GDI 抓屏一次约 17ms；后台方案里上传只是 UpdateSubresource，
    /// 所以连续调用 50 次的平均耗时必须远低于 17ms。
    /// </summary>
    [Fact]
    public void TryUploadIsNonBlocking()
    {
        using var capture = new BackgroundGdiCapture();
        capture.SetRegion(0, 0, Size, Size, 0, 0);
        capture.Start();

        WaitFirstFrame(capture);

        // 先上传一次把 _uploadedVersion 追上，避免把等待首帧的时间算进来
        capture.TryUpload(_device.Context, _texture);

        const int calls = 50;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < calls; i++)
        {
            capture.TryUpload(_device.Context, _texture);
        }

        watch.Stop();
        double perCall = watch.Elapsed.TotalMilliseconds / calls;

        Assert.True(perCall < 5.0,
            $"TryUpload 必须是非阻塞的（同步 GDI 抓屏约 17ms/次），实测 {perCall:F2}ms/次");
    }

    /// <summary>
    /// 崩溃回归：抓完一帧后**改区域再上传**。
    /// 最初版本用「当前」区域字段拼 Box、却读旧缓冲区，区域尺寸一变就越界
    /// （_ready 是非托管内存 → AccessViolationException，进程级崩溃）。
    /// 现在区域随帧快照，上传用的是与缓冲区匹配的那一份。
    /// </summary>
    [Fact]
    public void UploadAfterChangingTheRegion_DoesNotThrow()
    {
        using var capture = new BackgroundGdiCapture();
        capture.SetRegion(0, 0, Size, Size, 0, 0);
        capture.Start();

        WaitFirstFrame(capture);
        capture.TryUpload(_device.Context, _texture);

        // 抓屏区域尺寸变了（仍落在纹理内）—— 旧实现会在这里用新尺寸读旧缓冲区
        capture.SetRegion(0, 0, Size / 2, Size / 2, 0, 0);

        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                capture.TryUpload(_device.Context, _texture);
            }
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// 兜底断言：区域与纹理尺寸对不上（例如刚换显示器、纹理还没重建）时
    /// 必须**跳过上传**，而不是硬写 —— 越界会崩进程或搞掉 D3D 设备。
    /// </summary>
    [Fact]
    public void RegionBiggerThanTheTexture_IsSkipped()
    {
        using var capture = new BackgroundGdiCapture();

        // 纹理只有 Size×Size，却要抓 Size×2
        capture.SetRegion(0, 0, Size * 2, Size * 2, 0, 0);
        capture.Start();

        WaitFirstFrame(capture);

        Assert.False(capture.TryUpload(_device.Context, _texture));
    }

    [Fact]
    public void DisposeStopsTheThread()
    {
        var capture = new BackgroundGdiCapture();
        capture.SetRegion(0, 0, Size, Size, 0, 0);
        capture.Start();

        WaitFirstFrame(capture);
        capture.Dispose();

        // 释放后不应再产出帧（线程已退出）
        long after = capture.Version;
        Thread.Sleep(300);

        Assert.Equal(after, capture.Version);
    }

    public void Dispose()
    {
        _texture.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
