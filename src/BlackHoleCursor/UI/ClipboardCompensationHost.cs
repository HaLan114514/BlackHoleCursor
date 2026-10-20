using System.Windows.Interop;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Screenshots;

namespace BlackHoleCursor.UI;

/// <summary>
/// M4（prd-v2 F8）：监听剪贴板变化并在必要时补偿全屏截图。
///
/// 需要一个窗口句柄才能收到 <c>WM_CLIPBOARDUPDATE</c>，这里开一个 1×1 的隐藏
/// 工具窗口（WS_EX_TOOLWINDOW，不进任务栏也不进 Alt+Tab）专门收消息。
/// 它不参与布局、不显示，只作为消息接收端。
/// </summary>
public sealed class ClipboardCompensationHost : IDisposable
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly ScreenshotCompensator _compensator;
    private HwndSource? _source;
    private bool _disposed;
    private int _messages;

    public ClipboardCompensationHost(
        SettingsModel settings,
        Func<(double X, double Y)> holePosition,
        Func<double> timeSeconds)
    {
        _compensator = new ScreenshotCompensator(
            settings,
            new SystemClipboardImages(),
            holePosition,
            timeSeconds);
    }

    public bool IsListening { get; private set; }

    public int CompensatedCount => _compensator.CompensatedCount;

    public void Start()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("BlackHoleCursorClipboardListener")
        {
            Width = 1,
            Height = 1,
            WindowStyle = 0,                    // 无边框、不可见
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };

        // HwndSource 会立即创建 HWND；不调用 ShowWindow 所以它始终不可见，
        // 但仍然能收到剪贴板通知。
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        IsListening = NativeMethods.AddClipboardFormatListener(_source.Handle);
        if (!IsListening)
        {
            RuntimeLog.Error(
                $"注册剪贴板监听失败（GetLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}），截图补偿不可用");
        }
        else
        {
            // R7 取证：注册成功也要记一笔 —— 否则「没日志」既可能是没注册、也可能是没收到消息
            RuntimeLog.Info($"截图补偿：已注册剪贴板监听（hwnd=0x{_source.Handle:X}）");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            _messages++;

            // R7 取证：整条链有四个环节（注册 → 收消息 → 调度回调 → 处理），
            // 只在「未补偿」时看日志分不清断在哪一环，这里每环各记一次（只记头几次）。
            if (_messages <= 3)
            {
                RuntimeLog.Info($"截图补偿：收到 WM_CLIPBOARDUPDATE（第 {_messages} 次）");
            }

            // 不要在消息处理里直接读写剪贴板（可能重入），推到下一个调度周期再做
            int index = _messages;
            _ = _source?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (index <= 3)
                    {
                        RuntimeLog.Info($"截图补偿：调度回调已执行（第 {index} 次）");
                    }

                    _compensator.HandleClipboardUpdate();
                }
                catch (Exception ex)
                {
                    // 剪贴板是用户的数据：补偿失败绝不能影响它，也不能让程序崩
                    RuntimeLog.Error("截图补偿失败（剪贴板内容未改动）", ex);
                }
            });
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_source is not null)
        {
            if (IsListening)
            {
                NativeMethods.RemoveClipboardFormatListener(_source.Handle);
            }

            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _compensator.Dispose();
        GC.SuppressFinalize(this);
    }
}
