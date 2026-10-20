using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BlackHoleCursor.UI.Controls;

public partial class SliderRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SliderRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SliderRow), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SliderRow), new PropertyMetadata(100.0));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SliderRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty IsIntegerProperty = DependencyProperty.Register(
        nameof(IsInteger), typeof(bool), typeof(SliderRow),
        new PropertyMetadata(false, OnValueChanged));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(SliderRow),
        new PropertyMetadata("F1", OnValueChanged));

    public SliderRow()
    {
        InitializeComponent();
        UpdateText();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>true 时按整数显示与吸附（例如「碎石数量 8」而不是「8.0」）。</summary>
    public bool IsInteger
    {
        get => (bool)GetValue(IsIntegerProperty);
        set => SetValue(IsIntegerProperty, value);
    }

    /// <summary>
    /// 数值显示格式（默认 F1）。取值范围很小的参数要用百分比，
    /// 例如 HoleSize 的 0.02–0.15 用 F1 会全显示成「0.1」，用 P1 才是「2.0% / 15.0%」。
    /// </summary>
    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SliderRow)d).UpdateText();

    private void UpdateText()
    {
        if (ValueText is null)
        {
            return;
        }

        if (IsInteger)
        {
            ValueText.Text = Math.Round(Value).ToString("F0", CultureInfo.CurrentCulture);
            return;
        }

        string format = string.IsNullOrEmpty(ValueFormat) ? "F1" : ValueFormat;
        ValueText.Text = Value.ToString(format, CultureInfo.CurrentCulture);
    }
}
