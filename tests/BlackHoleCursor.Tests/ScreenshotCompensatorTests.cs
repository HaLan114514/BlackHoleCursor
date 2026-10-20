using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;
using BlackHoleCursor.Core.Screenshots;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M4（prd-v2 F8）的编排层：判定 → 合成 → 写回。
/// 用内存假剪贴板跑通整条链路 —— 绝不碰系统剪贴板，否则会污染用户正在复制的内容。
/// </summary>
public class ScreenshotCompensatorTests : IDisposable
{
    private readonly List<ScreenshotCompensator> _compensators = new();

    /// <summary>内存版剪贴板：序列号随每次写入递增，与 Windows 的行为一致。</summary>
    private sealed class FakeClipboard : IClipboardImages
    {
        private uint _sequence = 1000;

        public ClipboardBitmap Current { get; set; }

        public int WriteCount { get; private set; }

        public uint SequenceNumber => _sequence;

        public bool TryGetBitmap(out ClipboardBitmap bitmap)
        {
            bitmap = Current;
            return !Current.IsEmpty;
        }

        public void SetBitmap(in ClipboardBitmap bitmap)
        {
            Current = bitmap;
            _sequence++;              // 写回会推进序列号，也会再触发一次剪贴板通知
            WriteCount++;
        }
    }

    private static ClipboardBitmap SolidBitmap(int width, int height, byte gray = 128)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = gray;
            pixels[i + 1] = gray;
            pixels[i + 2] = gray;
            pixels[i + 3] = 0xFF;
        }

        return new ClipboardBitmap(width, height, pixels);
    }

    private ScreenshotCompensator Create(
        out FakeClipboard clipboard,
        bool enabled = true,
        int holeX = 960,
        int holeY = 540)
    {
        clipboard = new FakeClipboard();
        var settings = new SettingsModel { ScreenshotCompensation = enabled };

        var compensator = new ScreenshotCompensator(
            settings,
            clipboard,
            () => (holeX, holeY),
            () => 0.0,
            preferHardware: false);
        _compensators.Add(compensator);

        return compensator;
    }

    private static (int Width, int Height) VirtualScreen => ScreenMetrics.VirtualScreenSize();

    [Fact]
    public void FullScreenBitmap_IsCompensated()
    {
        var (vw, vh) = VirtualScreen;
        var compensator = Create(out var clipboard, holeX: vw / 2, holeY: vh / 2);
        clipboard.Current = SolidBitmap(vw, vh);

        Assert.True(compensator.HandleClipboardUpdate());
        Assert.Equal(1, compensator.CompensatedCount);
        Assert.Equal(1, clipboard.WriteCount);
    }

    /// <summary>F8-2：区域截图不处理。</summary>
    [Fact]
    public void RegionCapture_IsLeftAlone()
    {
        var compensator = Create(out var clipboard);
        clipboard.Current = SolidBitmap(800, 600);

        Assert.False(compensator.HandleClipboardUpdate());
        Assert.Equal(0, clipboard.WriteCount);
    }

    /// <summary>M4.3：开关关掉后什么都不做（连全屏图也不补）。</summary>
    [Fact]
    public void Disabled_DoesNothingEvenForFullScreen()
    {
        var (vw, vh) = VirtualScreen;
        var compensator = Create(out var clipboard, enabled: false, holeX: vw / 2, holeY: vh / 2);
        clipboard.Current = SolidBitmap(vw, vh);

        Assert.False(compensator.HandleClipboardUpdate());
        Assert.Equal(0, clipboard.WriteCount);
    }

    /// <summary>剪贴板里是文本（没有位图）时安静跳过。</summary>
    [Fact]
    public void NoBitmap_IsSkipped()
    {
        var compensator = Create(out var clipboard);
        clipboard.Current = default;

        Assert.False(compensator.HandleClipboardUpdate());
        Assert.Equal(0, clipboard.WriteCount);
    }

    /// <summary>
    /// 写回剪贴板会再触发一次通知。不认出「这是自己刚写进去的」就会无限自我补偿 ——
    /// 这条是本功能最容易踩的坑，必须锁死。
    /// </summary>
    [Fact]
    public void TheNotificationCausedByOurOwnWrite_IsIgnored()
    {
        var (vw, vh) = VirtualScreen;
        var compensator = Create(out var clipboard, holeX: vw / 2, holeY: vh / 2);
        clipboard.Current = SolidBitmap(vw, vh);

        Assert.True(compensator.HandleClipboardUpdate());      // 用户按了 PrtScn
        Assert.False(compensator.HandleClipboardUpdate());     // 自己写回引发的通知 → 忽略

        Assert.True(clipboard.WriteCount == 1,
            $"只应写回一次，否则是无限自我补偿（实际 {clipboard.WriteCount} 次）");
        Assert.Equal(1, compensator.CompensatedCount);
    }

    /// <summary>用户再截一张新图（序列号推进）必须再次补偿。</summary>
    [Fact]
    public void ANewScreenshotAfterOurs_IsCompensatedAgain()
    {
        var (vw, vh) = VirtualScreen;
        var compensator = Create(out var clipboard, holeX: vw / 2, holeY: vh / 2);
        clipboard.Current = SolidBitmap(vw, vh);

        Assert.True(compensator.HandleClipboardUpdate());
        Assert.False(compensator.HandleClipboardUpdate());

        clipboard.SetBitmap(SolidBitmap(vw, vh));             // 模拟用户又截了一张
        Assert.True(compensator.HandleClipboardUpdate());

        Assert.Equal(2, compensator.CompensatedCount);
    }

    /// <summary>合成出来的图里，黑洞所在处必须真的变了，且图本身尺寸不变。</summary>
    [Fact]
    public void CompensatedBitmap_ContainsTheHoleNearItsCenter()
    {
        var (vw, vh) = VirtualScreen;
        int holeX = vw / 2;
        int holeY = vh / 2;
        var compensator = Create(out var clipboard, holeX: holeX, holeY: holeY);
        clipboard.Current = SolidBitmap(vw, vh, gray: 128);

        Assert.True(compensator.HandleClipboardUpdate());

        var result = clipboard.Current;
        Assert.Equal(vw, result.Width);
        Assert.Equal(vh, result.Height);

        int darkest = 255;
        for (int y = Math.Max(holeY - 200, 0); y < Math.Min(holeY + 200, vh); y++)
        {
            for (int x = Math.Max(holeX - 200, 0); x < Math.Min(holeX + 200, vw); x++)
            {
                int i = (y * vw + x) * 4;
                darkest = Math.Min(darkest, (result.Pixels[i] + result.Pixels[i + 1] + result.Pixels[i + 2]) / 3);
            }
        }

        Assert.True(darkest < 64, $"黑洞附近应出现明显更暗的像素（真阴影），实际最暗 {darkest}");
    }

    public void Dispose()
    {
        foreach (var compensator in _compensators)
        {
            compensator.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
