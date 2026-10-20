namespace BlackHoleCursor.Core;

/// <summary>覆盖层生命周期抽象，便于在未初始化渲染器时也能安全调用。</summary>
public interface IOverlayController
{
    bool IsRunning { get; }

    void Start();

    void Stop();

    /// <summary>配置变化后通知渲染层刷新（参数即时生效，无需重启）。</summary>
    void NotifySettingsChanged();

    /// <summary>
    /// 主界面是否处于可见状态。可见时暂停降帧策略，保证预览流畅（plan.md 5.3）。
    /// </summary>
    void SetUiActive(bool active);

    /// <summary>
    /// N4：从头重播一次「出现」动画（主界面调时长参数时用）。
    /// 桌面覆盖层播的是真实动画；预览区常驻不播，所以只能在主界面手动触发一次来看效果。
    /// </summary>
    void ReplayTransition();

    /// <summary>最近一次渲染的帧率统计（用于主界面状态显示与降级提示）。</summary>
    double FramesPerSecond { get; }

    /// <summary>
    /// 黑洞当前的**视觉**位置（虚拟桌面坐标，像素）。命中点始终是真实鼠标，
    /// 但惯性跟随会让画出来的黑洞滞后于它 —— 截图补偿要补的是画出来的这个位置（F8）。
    /// </summary>
    (double X, double Y) VisualPosition { get; }

    /// <summary>物理已推进的累计时间（秒），供截图补偿对齐盘纹相位（F8）。</summary>
    double ElapsedSeconds { get; }

    /// <summary>
    /// M5.2：开始一次 1 秒交叉淡入。必须在应用预设**之前**调用（抓住当前外观作为起点）。
    /// </summary>
    void BeginLookFade();
}

/// <summary>渲染不可用时的空实现，保证程序仍可运行（仅接管光标）。</summary>
public sealed class NullOverlayController : IOverlayController
{
    public bool IsRunning => false;

    public double FramesPerSecond => 0;

    public (double X, double Y) VisualPosition => (0, 0);

    public double ElapsedSeconds => 0;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void NotifySettingsChanged()
    {
    }

    public void SetUiActive(bool active)
    {
    }

    public void ReplayTransition()
    {
    }

    public void BeginLookFade()
    {
    }
}
