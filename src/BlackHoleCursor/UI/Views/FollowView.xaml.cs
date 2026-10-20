using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.UI.Views;

public partial class FollowView : UserControl
{
    private SettingsModel? _bound;

    public FollowView()
    {
        InitializeComponent();
        Loaded += (_, _) => Bind();
        DataContextChanged += (_, _) => Bind();
        Unloaded += (_, _) => Unbind();
    }

    private SettingsModel? Settings => DataContext as SettingsModel;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnSettingsChanged;
        }

        _bound = DataContext as SettingsModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnSettingsChanged;
        }

        UpdateHint();
        UpdateWanderHint();
    }

    private void Unbind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnSettingsChanged;
            _bound = null;
        }
    }

    /// <summary>游荡开 / 关与三个参数变化都要刷新提示文案。</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsModel.IdleWanderEnabled)
            or nameof(SettingsModel.IdleWanderDelay)
            or nameof(SettingsModel.IdleWanderRadius)
            or nameof(SettingsModel.IdleWanderSpeed))
        {
            Dispatcher.BeginInvoke(UpdateWanderHint);
        }
    }

    private void UpdateWanderHint()
    {
        if (Settings is null)
        {
            return;
        }

        if (!Settings.IdleWanderEnabled)
        {
            WanderHint.Text = "未启用：黑洞严格停在鼠标位置。";
            return;
        }

        WanderHint.Text =
            $"鼠标静止 {Settings.IdleWanderDelay:F1} 秒后，黑洞在 {Settings.IdleWanderRadius:F0} px 范围内飘动；" +
            "鼠标一动即平滑回位。注意：预览区的黑洞本来就在巡游，游荡效果要在桌面上看。";
    }

    /// <summary>N4：手动重播一次「出现」动画，方便调时长与过冲（v4：随动画组从设置页搬来）。</summary>
    private void ReplayTransition_Click(object sender, RoutedEventArgs e)
        => RuntimeHost.Overlay?.ReplayTransition();

    private void Underdamped_Click(object sender, RoutedEventArgs e) => Apply(DampingPresetKind.Underdamped);

    private void Critical_Click(object sender, RoutedEventArgs e) => Apply(DampingPresetKind.Critical);

    private void Overdamped_Click(object sender, RoutedEventArgs e) => Apply(DampingPresetKind.Overdamped);

    private void Apply(DampingPresetKind kind)
    {
        if (Settings is null)
        {
            return;
        }

        var parameters = FollowState.Preset(Settings, kind);
        Settings.FollowEnabled = true;
        Settings.FollowDamping = parameters.Damping;
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (Settings is null)
        {
            return;
        }

        double ratio = FollowState.DampingRatio(Settings.FollowStiffness, Settings.FollowDamping);
        string kind = ratio < 0.9 ? "欠阻尼（会轻微过冲）" : ratio > 1.15 ? "过阻尼（厚重迟滞）" : "临界阻尼（顺滑无过冲）";
        DampingHint.Text = $"当前阻尼比 ζ = {ratio:F2} → {kind}；临界阻尼建议值 {FollowState.CriticalDamping(Settings.FollowStiffness):F1}。";
    }
}
