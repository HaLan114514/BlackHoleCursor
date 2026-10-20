using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.UI.Views;

/// <summary>
/// v4：引力透镜页。
///
/// 除了开关本身，这里还显示**抓屏状态** —— 扭曲画面是靠抓屏做的，
/// 抓屏一出问题（降级、全黑、帧率掉）最先坏的就是这一页负责的效果，
/// 所以状态应该摆在能看见的地方（照 <see cref="SystemView"/> 的每秒轮询写法）。
/// </summary>
public partial class LensView : UserControl
{
    private readonly DispatcherTimer _timer;

    public LensView()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTick, Dispatcher);
        Loaded += (_, _) => { _timer.Start(); Update(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, System.EventArgs e) => Update();

    private void Update()
    {
        var overlay = RuntimeHost.Overlay as OverlayRunner;
        if (overlay is null || !overlay.IsRunning || overlay.StartupError is not null)
        {
            CaptureState.Text = "抓屏：未运行";
            PerfState.Text = string.Empty;
            StepState.Text = string.Empty;
            return;
        }

        // 主抓屏器是桌面复制（DXGI），降级后走 GDI 兼容路径 —— 后者每帧多一次合成同步等待，
        // 帧率上限就是这么被压下来的（见 fixes-v2「性能：已归因」）。
        CaptureState.Text = overlay.IsDegraded
            ? "抓屏：GDI 兼容模式（桌面复制不可用，已降级）"
            : "抓屏：桌面复制（DXGI）";

        if (overlay.IsLensSuppressed)
        {
            CaptureState.Text += " · 画面全黑，扭曲已暂停";
        }

        string perf = $"{overlay.FramesPerSecond:F0} FPS";
        if (overlay.IsUsingSoftwareRendering)
        {
            perf += " · 软件渲染";
        }

        PerfState.Text = perf;

        // 自动档的实际步数是**实测帧率**决定的，所以放在状态里才有意义；
        // 固定档直接显示档位对应的步数即可。
        var settings = DataContext as SettingsModel;
        StepState.Text = settings is not null && settings.QualitySteps == QualitySteps.Auto
            ? $"积分步数：自动 {App.Current.AutoQuality.StepCount} 步"
            : $"积分步数：固定 {(int)(settings?.QualitySteps ?? QualitySteps.Auto)} 步";
    }
}
