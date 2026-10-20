using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.UI.Views;

public partial class AppearanceView : UserControl
{
    private SettingsModel? _settings;

    public AppearanceView()
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

        RefreshSwatches();
    }

    // v4（V4-1）：预设的另存 / 应用 / 删除已整体搬到 `PresetView`
    // v4（V4-4）：「积分步数」下拉框连同它的选项填充、以及星云色调的取色按钮，
    //             都已随「渲染」那一块搬到 `RenderView`。
    //             ⚠️ 注释里**不要**再写那些控件的代码名 ——
    //             `AppearanceBindingTests` 是扫整个文件文本来断言「本页不该再出现它们」的，
    //             写在注释里也会被算作命中。

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsModel.TintColor))
        {
            RefreshSwatches();
        }
    }

    private void RefreshSwatches()
    {
        if (_settings is null)
        {
            return;
        }

        TintSwatch.Fill = new SolidColorBrush(ToMediaColor(_settings.TintColor));

        // v4（V4-4）：星云色块已随「渲染」那块搬到 `RenderView`，这里只管盘色调
    }

    private static System.Windows.Media.Color ToMediaColor(System.Drawing.Color color)
        => System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color color)
        => System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private void TintColor_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var picked = PickColor(_settings.TintColor);
        if (picked.HasValue)
        {
            _settings.TintColor = picked.Value;
        }
    }

    private System.Drawing.Color? PickColor(System.Drawing.Color current)
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
