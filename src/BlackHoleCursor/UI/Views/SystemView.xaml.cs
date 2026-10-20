using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.UI.Views;

public partial class SystemView : UserControl
{
    private readonly DispatcherTimer _timer;

    public SystemView()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTick, Dispatcher);
        Loaded += (_, _) => { _timer.Start(); Update(); UpdatePath(); };
        DataContextChanged += (_, _) => UpdatePath();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var cursor = RuntimeHost.Cursor;
        bool hidden = cursor?.IsHidden ?? false;
        bool fallback = cursor?.IsFallbackActive ?? false;

        CursorState.Text = $"系统光标：{(hidden ? "已隐藏（黑洞接管）" : "显示中")}";
        FallbackState.Text = $"智能兜底：{(fallback ? $"已触发（{cursor?.LastState}）" : "未触发")}";

        var overlay = RuntimeHost.Overlay as OverlayRunner;
        if (overlay is null || !overlay.IsRunning)
        {
            RenderState.Text = overlay?.IsDeviceLost == true
                ? "覆盖层：已停止（显卡设备丢失，重启程序可恢复）"
                : "覆盖层：未运行";
        }
        else if (overlay.StartupError is not null)
        {
            RenderState.Text = $"覆盖层：启动失败（{overlay.StartupError.Message}）";
        }
        else
        {
            var text = $"覆盖层：运行中（{overlay.FramesPerSecond:F0} FPS）";
            if (overlay.IsUsingSoftwareRendering)
            {
                text += " · 软件渲染";
            }

            if (overlay.IsDegraded)
            {
                text += " · 抓屏已降级";
            }

            if (overlay.IsLensSuppressed)
            {
                text += " · 抓屏全黑，扭曲已暂停";
            }

            if (overlay.DeviceRebuildCount > 0)
            {
                text += $" · 设备重建 {overlay.DeviceRebuildCount} 次";
            }

            var timing = overlay.Timing;
            if (timing is not null && timing.FrameMs > 0)
            {
                // 问题 1：把分段耗时直接摊在界面上，方便对着参数拖滑块看哪一项在涨
                text += $"{Environment.NewLine}单帧耗时：抓屏 {timing.CaptureMs:F2} / 渲染 {timing.RenderMs:F2}"
                        + $" / Present {timing.PresentMs:F2} / 整帧 {timing.FrameMs:F2} ms";
            }

            RenderState.Text = text;
        }
    }

    private void RestoreCursor_Click(object sender, RoutedEventArgs e)
    {
        RuntimeHost.Cursor?.Restore();
        Update();
    }

    // -------------------------------------------------------------------------
    // v4：下面这一组是从「程序设置」页合并过来的（两个页合成「系统与程序」）
    // -------------------------------------------------------------------------

    private void UpdatePath()
    {
        ConfigPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsService.AppName,
            SettingsService.ConfigFileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsService.AppName);
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要把所有参数恢复为默认值吗？",
            "BlackHole Cursor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        App.Current.SettingsService.ResetToDefaults();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.RequestExit();
}
