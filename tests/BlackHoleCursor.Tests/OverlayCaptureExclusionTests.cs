using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// M3.3（prd-v2 F5-3 / 缺陷 B3）：覆盖层必须把自己从抓屏里排除掉。
///
/// 不设 <c>WDA_EXCLUDEFROMCAPTURE</c> 时，覆盖层会被自己抓进去：
/// 黑洞把「上一帧的自己」折射一遍，那一帧里又含有再上一帧的自己 ……
/// 逐帧自我放大，形成镜像回廊（而且会越来越亮）。
///
/// 这里只断言**机制**（亲和性设上了、且不会丢）—— 不做像素级的「抓屏里没有黑洞」对比：
/// 那需要真实桌面静止不动，而壁纸动效 / 任务栏时钟 / 其它窗口刷新都会让两次抓屏不同，
/// 断言会随机失败。这一条留给实机验收（见 plan-v2 M3.3）。
/// </summary>
public class OverlayCaptureExclusionTests
{
    [Fact]
    public void OverlayWindow_IsExcludedFromCapture()
    {
        using var window = new Win32OverlayWindow();

        Assert.True(window.ExcludeFromCapture,
            "覆盖层必须成功设置 WDA_EXCLUDEFROMCAPTURE，否则会形成镜像回廊");
        Assert.Equal(Win32OverlayWindow.WDA_EXCLUDEFROMCAPTURE, window.DisplayAffinity);
    }

    /// <summary>
    /// 防止上一条断言空转：回读值必须与「调用是否成功」一致。
    /// 若 <c>GetWindowDisplayAffinity</c> 恒返回某个值，这里会先炸。
    /// </summary>
    [Fact]
    public void DisplayAffinity_MatchesWhetherTheCallSucceeded()
    {
        using var window = new Win32OverlayWindow();

        bool readBackIsExcluded = window.DisplayAffinity == Win32OverlayWindow.WDA_EXCLUDEFROMCAPTURE;

        Assert.Equal(window.ExcludeFromCapture, readBackIsExcluded);
    }

    /// <summary>
    /// 亲和性是窗口属性，不随移动 / 显隐 / 重申置顶而丢失。
    /// 覆盖层每帧都在 Move、并周期性 EnsureTopmost，掉了就是回廊复发。
    /// </summary>
    [Fact]
    public void DisplayAffinity_SurvivesMoveShowHideAndTopmost()
    {
        using var window = new Win32OverlayWindow();

        window.Move(120, 140, 300, 300);
        window.SetVisible(true);
        window.EnsureTopmost();
        window.SetVisible(false);
        window.Move(400, 500, 300, 300);
        window.SetVisible(true);
        window.EnsureTopmost();

        Assert.Equal(Win32OverlayWindow.WDA_EXCLUDEFROMCAPTURE, window.DisplayAffinity);
    }

    /// <summary>窗口销毁后回读应安全返回 0，而不是抛异常。</summary>
    [Fact]
    public void DisplayAffinity_AfterDispose_ReturnsZero()
    {
        var window = new Win32OverlayWindow();
        window.Dispose();

        Assert.Equal(0u, window.DisplayAffinity);
    }
}
