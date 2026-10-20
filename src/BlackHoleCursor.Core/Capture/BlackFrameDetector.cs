namespace BlackHoleCursor.Core.Capture;

/// <summary>
/// 全黑帧检测（plan.md 5.2 / 风险 R5）：受 DRM / HDR 保护的窗口抓屏结果恒为纯黑，
/// 若照常合成会出现一块死黑的矩形，因此改为跳过扭曲层、只画黑洞本体。
///
/// 误判代价极低：画面本来就是全黑时，透镜层没有任何可折射的内容，
/// 关掉它与保留它在视觉上完全一致。
/// </summary>
public sealed class BlackFrameDetector
{
    /// <summary>通道值不超过该阈值即视为「黑」。</summary>
    public const byte DefaultBlackThreshold = 6;

    /// <summary>连续多少帧采样为黑才切换到「跳过扭曲」。</summary>
    public const int DefaultConsecutiveFrames = 3;

    private readonly byte _threshold;
    private readonly int _requiredConsecutive;
    private int _consecutive;

    public BlackFrameDetector(
        byte threshold = DefaultBlackThreshold,
        int requiredConsecutive = DefaultConsecutiveFrames)
    {
        _threshold = threshold;
        _requiredConsecutive = Math.Max(requiredConsecutive, 1);
    }

    /// <summary>是否处于「抓到的画面全黑」状态（此时应跳过扭曲层）。</summary>
    public bool IsBlack { get; private set; }

    public int ConsecutiveBlackFrames => _consecutive;

    public byte Threshold => _threshold;

    public void Reset()
    {
        _consecutive = 0;
        IsBlack = false;
    }

    /// <summary>采样 BGRA 像素并推进状态机；返回值表示状态是否翻转。</summary>
    public bool Update(ReadOnlySpan<byte> bgraPixels) => Update(IsUniformlyDark(bgraPixels, _threshold));

    /// <summary>推进状态机；返回值表示状态是否翻转（用于写日志 / 提示）。</summary>
    public bool Update(bool frameIsDark)
    {
        bool wasBlack = IsBlack;

        _consecutive = frameIsDark ? _consecutive + 1 : 0;
        IsBlack = frameIsDark && _consecutive >= _requiredConsecutive;

        return IsBlack != wasBlack;
    }

    /// <summary>BGRA 像素是否全部低于阈值（忽略 alpha 通道）。</summary>
    public static bool IsUniformlyDark(ReadOnlySpan<byte> bgraPixels, byte threshold)
    {
        if (bgraPixels.Length < 4)
        {
            // 没有可判断的数据时按「不黑」处理，避免误关扭曲层
            return false;
        }

        for (int i = 0; i + 3 < bgraPixels.Length; i += 4)
        {
            if (bgraPixels[i] > threshold ||
                bgraPixels[i + 1] > threshold ||
                bgraPixels[i + 2] > threshold)
            {
                return false;
            }
        }

        return true;
    }
}
