using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.UI.Views;

/// <summary>
/// v4（V4-4）：「渲染与性能」页 —— 从「外观」页搬过来的渲染类控件集中在这里。
///
/// ⚠️ 搬移时最容易漏的是 <see cref="EnsureQualityItems"/>：它负责填「积分步数」下拉框的选项，
/// 漏掉的话下拉框**不报错、只是没有选项**（典型的静默失败，只有实机点开才发现）。
/// </summary>
public partial class RenderView : UserControl
{
    private SettingsModel? _settings;

    public RenderView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindSettings();
        Loaded += (_, _) => BindSettings();
    }

    private void BindSettings()
    {
        if (_settings is not null)
        {
            _settings.PropertyChanged -= OnSettingsChanged;
        }

        _settings = DataContext as SettingsModel;
        if (_settings is not null)
        {
            _settings.PropertyChanged += OnSettingsChanged;
        }

        EnsureQualityItems();
        RefreshSwatch();
    }

    /// <summary>
    /// 质量档位的下拉项。选中项通过 <c>SelectedValue</c> + <c>SelectedValuePath="Tag"</c>
    /// 双向绑定到 <see cref="SettingsModel.QualitySteps"/>，所以这里只需填充一次。
    /// </summary>
    private void EnsureQualityItems()
    {
        if (QualityBox.Items.Count > 0)
        {
            return;
        }

        foreach (var (value, label) in QualityItems)
        {
            QualityBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
    }

    private static readonly (QualitySteps Value, string Label)[] QualityItems =
    [
        (QualitySteps.Auto, "自动（按帧率）"),
        (QualitySteps.Low, "低 24 步"),
        (QualitySteps.Medium, "中 48 步"),
        (QualitySteps.High, "高 88 步"),
    ];

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 预设应用后星云色调会变，色块要跟着更新
        if (e.PropertyName == nameof(SettingsModel.NebulaColor))
        {
            RefreshSwatch();
        }
    }

    private void RefreshSwatch()
    {
        if (_settings is null)
        {
            return;
        }

        NebulaSwatch.Fill = new SolidColorBrush(ToMediaColor(_settings.NebulaColor));
    }

    /// <summary>fixes-v2 问题 6：手动挑星云亮部配色（预设带的色调可以被它覆盖）。</summary>
    private void NebulaColor_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var picked = PickColor(_settings.NebulaColor);
        if (picked.HasValue)
        {
            _settings.NebulaColor = picked.Value;
            RefreshSwatch();
        }
    }

    private static Color ToMediaColor(System.Drawing.Color color)
        => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static System.Drawing.Color? PickColor(System.Drawing.Color current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = current,
            FullOpen = true,
            AnyColor = true,
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Color : null;
    }
}
