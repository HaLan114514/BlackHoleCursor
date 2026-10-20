namespace BlackHoleCursor.Core.Rendering;

/// <summary>覆盖层窗口抽象（Win32 实现 + 测试替身）。</summary>
public interface IOverlayWindow : IDisposable
{
    IntPtr Handle { get; }

    bool IsCreated { get; }

    void Move(int x, int y, int width, int height);

    void SetVisible(bool visible);

    /// <summary>
    /// 重申「置顶」层级。覆盖层创建时带了 WS_EX_TOPMOST，但任务栏（explorer）重建、
    /// 或别的窗口抢到最顶层后，覆盖层会被压到下面 —— 尤其是任务栏所在的位置。
    /// 需要周期性重申才能稳定盖住任务栏。
    /// </summary>
    void EnsureTopmost();
}

/// <summary>鼠标位置来源抽象（钩子实现 + 测试替身）。</summary>
public interface IMousePositionSource
{
    (int X, int Y) GetPosition();
}

/// <summary>使用 GetCursorPos 轮询真实鼠标位置（物理像素）。</summary>
public sealed class SystemMousePositionSource : IMousePositionSource
{
    public (int X, int Y) GetPosition()
    {
        NativeMethods.GetCursorPos(out var point);
        return (point.X, point.Y);
    }
}

/// <summary>测试与预览用的固定/可编程鼠标位置。</summary>
public sealed class FakeMousePositionSource : IMousePositionSource
{
    public int X { get; set; }

    public int Y { get; set; }

    public FakeMousePositionSource(int x = 0, int y = 0)
    {
        X = x;
        Y = y;
    }

    public (int X, int Y) GetPosition() => (X, Y);
}
