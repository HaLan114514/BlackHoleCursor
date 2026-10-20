using BlackHoleCursor.Core.Capture;

namespace BlackHoleCursor.Tests;

public class BlackFrameDetectorTests
{
    private static byte[] Bgra(byte b, byte g, byte r, byte a = 0xFF) => new[] { b, g, r, a };

    private static byte[] Repeat(int count, byte b, byte g, byte r, byte a = 0xFF)
    {
        var pixels = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            pixels[i * 4] = b;
            pixels[(i * 4) + 1] = g;
            pixels[(i * 4) + 2] = r;
            pixels[(i * 4) + 3] = a;
        }

        return pixels;
    }

    [Fact]
    public void UniformlyBlack_IsDetected()
        => Assert.True(BlackFrameDetector.IsUniformlyDark(Repeat(16, 0, 0, 0), BlackFrameDetector.DefaultBlackThreshold));

    [Fact]
    public void AnyBrightChannel_IsNotBlack()
        => Assert.False(BlackFrameDetector.IsUniformlyDark(Repeat(16, 0, 0, 40), BlackFrameDetector.DefaultBlackThreshold));

    [Fact]
    public void SingleBrightPixel_IsNotBlack()
    {
        var pixels = Repeat(16, 0, 0, 0);
        pixels[3 * 4 + 1] = 200; // 第 4 个像素的 G 通道

        Assert.False(BlackFrameDetector.IsUniformlyDark(pixels, BlackFrameDetector.DefaultBlackThreshold));
    }

    [Fact]
    public void AlphaChannelIsIgnored()
        => Assert.True(BlackFrameDetector.IsUniformlyDark(Repeat(4, 0, 0, 0, 0x00), BlackFrameDetector.DefaultBlackThreshold));

    [Fact]
    public void EmptyBuffer_IsNotTreatedAsBlack()
        => Assert.False(BlackFrameDetector.IsUniformlyDark(Array.Empty<byte>(), BlackFrameDetector.DefaultBlackThreshold));

    [Fact]
    public void BelowThresholdIsStillBlack()
        => Assert.True(BlackFrameDetector.IsUniformlyDark(Repeat(4, 6, 6, 6), 6));

    [Fact]
    public void RequiresConsecutiveBlackFrames_BeforeActivating()
    {
        var detector = new BlackFrameDetector(threshold: 6, requiredConsecutive: 3);

        Assert.False(detector.Update(true));
        Assert.False(detector.Update(true));
        Assert.False(detector.IsBlack);

        Assert.True(detector.Update(true));
        Assert.True(detector.IsBlack);
    }

    [Fact]
    public void SingleBrightFrame_ResetsCounter()
    {
        var detector = new BlackFrameDetector(threshold: 6, requiredConsecutive: 3);

        detector.Update(true);
        detector.Update(true);
        detector.Update(false);
        detector.Update(true);
        detector.Update(true);

        Assert.False(detector.IsBlack);
        Assert.Equal(2, detector.ConsecutiveBlackFrames);
    }

    [Fact]
    public void ReturnsTrueOnlyWhenStateFlips()
    {
        var detector = new BlackFrameDetector(threshold: 6, requiredConsecutive: 2);

        Assert.False(detector.Update(true));
        Assert.True(detector.Update(true));   // 进入全黑
        Assert.False(detector.Update(true));  // 仍是全黑，不重复上报
        Assert.True(detector.Update(false));  // 恢复
        Assert.False(detector.Update(false));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var detector = new BlackFrameDetector(threshold: 6, requiredConsecutive: 1);
        detector.Update(true);

        Assert.True(detector.IsBlack);

        detector.Reset();

        Assert.False(detector.IsBlack);
        Assert.Equal(0, detector.ConsecutiveBlackFrames);
    }

    [Fact]
    public void NonBlackPixels_KeepDetectorInactive()
    {
        var detector = new BlackFrameDetector(threshold: 6, requiredConsecutive: 2);

        for (int i = 0; i < 10; i++)
        {
            detector.Update(Bgra(0, 0, 128));
        }

        Assert.False(detector.IsBlack);
    }
}
