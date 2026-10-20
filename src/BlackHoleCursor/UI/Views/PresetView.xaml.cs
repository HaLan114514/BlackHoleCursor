using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.UI.Views;

/// <summary>
/// v4（V4-1 / V4-2）：预设页。内置与「我的」**分开两块**，自定义预设可以
/// 应用 / 覆盖保存 / 重命名 / 删除。
///
/// 逻辑全在 <see cref="PresetService"/>（Core 层，有单测），这里只做入口与提示文案。
/// </summary>
public partial class PresetView : UserControl
{
    public PresetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildLists();
        Loaded += (_, _) => BuildLists();
    }

    /// <summary>
    /// 预设作用于**当前设置**。页面本身不做数据绑定，所以从 <c>DataContext</c> 取不到时
    /// 退回 <see cref="App.Settings"/>（主窗口的 DataContext 就是它）。
    /// </summary>
    private SettingsModel Settings => (DataContext as SettingsModel) ?? App.Current.Settings;

    private void BuildLists()
    {
        BuildBuiltIn();
        BuildCustom();
    }

    private void BuildBuiltIn()
    {
        BuiltInPanel.Children.Clear();

        foreach (var preset in App.Current.Presets.All.Where(p => p.IsBuiltIn))
        {
            var button = new Button
            {
                Content = preset.Name,
                Style = Ghost(),
                Margin = new Thickness(0, 0, 10, 8),
                Tag = preset,
            };

            button.Click += (_, _) => Apply(preset);
            BuiltInPanel.Children.Add(button);
        }
    }

    private void BuildCustom()
    {
        CustomPanel.Children.Clear();

        var custom = App.Current.Presets.Custom;
        foreach (var preset in custom)
        {
            CustomPanel.Children.Add(CreateCustomRow(preset));
        }

        EmptyHint.Visibility = custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>一行 = 名称（可就地改名）+ 应用 / 覆盖保存 / 重命名 / 删除。</summary>
    private FrameworkElement CreateCustomRow(SettingsPreset preset)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        var nameText = new TextBlock
        {
            Text = preset.Name,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
        };

        // 就地改名：平时收起，点「重命名」才出现（Enter 确定 / Esc 取消）
        var nameBox = new TextBox
        {
            Text = preset.Name,
            Width = 150,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var apply = new Button { Content = "应用", Style = Ghost(), Tag = preset };
        var overwrite = new Button
        {
            Content = "覆盖保存",
            Style = Ghost(),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "用当前外观更新这个预设",
        };
        var rename = new Button { Content = "重命名", Style = Ghost(), Margin = new Thickness(6, 0, 0, 0) };
        var delete = new Button { Content = "删除", Style = Ghost(), Margin = new Thickness(6, 0, 0, 0) };

        apply.Click += (_, _) => Apply(preset);

        overwrite.Click += (_, _) =>
        {
            var answer = MessageBox.Show(
                $"用当前外观覆盖预设「{preset.Name}」？",
                "覆盖保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            Hint(App.Current.Presets.TrySaveOver(preset.Name, Settings)
                ? $"已用当前外观覆盖预设「{preset.Name}」"
                : $"覆盖预设「{preset.Name}」失败");

            BuildLists();
        };

        rename.Click += (_, _) =>
        {
            if (nameBox.Visibility == Visibility.Visible)
            {
                CommitRename(preset.Name, nameBox, rename);
                return;
            }

            nameText.Visibility = Visibility.Collapsed;
            nameBox.Visibility = Visibility.Visible;
            rename.Content = "确定";
            nameBox.Focus();
            nameBox.SelectAll();
        };

        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRename(preset.Name, nameBox, rename);
            }
            else if (e.Key == Key.Escape)
            {
                nameBox.Text = preset.Name;
                nameBox.Visibility = Visibility.Collapsed;
                nameText.Visibility = Visibility.Visible;
                rename.Content = "重命名";
            }
        };

        delete.Click += (_, _) => Delete(preset.Name);

        row.Children.Add(nameText);
        row.Children.Add(nameBox);
        row.Children.Add(apply);
        row.Children.Add(overwrite);
        row.Children.Add(rename);
        row.Children.Add(delete);

        return row;
    }

    private void CommitRename(string oldName, TextBox box, Button renameButton)
    {
        string target = box.Text.Trim();

        if (App.Current.Presets.TryRename(oldName, target))
        {
            Hint($"已重命名为「{target}」");
            BuildLists();
            return;
        }

        // 失败时**保留编辑状态**，让用户接着改而不是把输入丢掉
        Hint(target.Length == 0
            ? "请输入预设名称"
            : $"无法重命名为「{target}」：与已有预设重名，或是内置预设名");

        box.Focus();
        box.SelectAll();
    }

    private void Delete(string name)
    {
        var answer = MessageBox.Show(
            $"确定要删除自定义预设「{name}」吗？{Environment.NewLine}此操作不可撤销。",
            "删除预设",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        Hint(App.Current.Presets.TryDelete(name) ? $"已删除预设「{name}」" : $"删除预设「{name}」失败");
        BuildLists();
    }

    /// <summary>
    /// 应用预设。顺序**不能反**（M5.2 的老坑）：先抓当前外观做淡入起点，再改设置。
    /// </summary>
    private void Apply(SettingsPreset preset)
    {
        App.Current.BeginPresetFade();
        preset.ApplyTo(Settings);
        Hint($"已应用预设「{preset.Name}」");
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        string name = PresetNameBox.Text.Trim();

        if (name.Length == 0)
        {
            Hint("请输入预设名称");
            return;
        }

        if (PresetLibrary.IsBuiltIn(name))
        {
            Hint($"「{name}」是内置预设名称，请换一个");
            return;
        }

        // 另存为只负责**新建**；要更新已有的请用那个预设的「覆盖保存」——
        // 否则点一下就静默覆盖掉旧的，用户根本不知道
        if (App.Current.Presets.Custom.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Hint($"已存在预设「{name}」—— 要更新它请点该预设的「覆盖保存」");
            return;
        }

        Hint(App.Current.Presets.TrySave(name, Settings)
            ? $"已保存预设「{name}」"
            : $"保存预设「{name}」失败");

        BuildLists();
    }

    /// <summary>v4（V4-3）：把「我的预设」导出成一个 .json 文件。</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var names = App.Current.Presets.Custom.Select(p => p.Name).ToList();

        if (names.Count == 0)
        {
            Hint("还没有自定义预设可以导出");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出预设",
            Filter = "预设文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = names.Count == 1 ? names[0] : "我的预设",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Hint(App.Current.Presets.TryExport(names, dialog.FileName)
            ? $"已导出 {names.Count} 个预设到 {Path.GetFileName(dialog.FileName)}"
            : "导出失败");
    }

    /// <summary>
    /// v4（V4-3）：从 .json 合并预设。**只在有重名时**才问策略 ——
    /// 没重名还弹一次窗口，只会让人习惯性地点掉。
    /// </summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入预设",
            Filter = "预设文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!App.Current.Presets.TryParsePresetFile(dialog.FileName, out var incoming, out string error))
        {
            Hint($"导入失败：{error}");
            return;
        }

        if (incoming.Count == 0)
        {
            Hint("这个文件里没有可导入的预设");
            return;
        }

        var existing = App.Current.Presets.Custom
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var policy = ConflictPolicy.Overwrite;

        if (incoming.Any(p => existing.Contains(p.Name)))
        {
            var answer = MessageBox.Show(
                $"文件里有 {incoming.Count} 个预设，其中有的与现在同名。{Environment.NewLine}{Environment.NewLine}" +
                "「是」= 用导入的覆盖；「否」= 保留当前的，导入的另存为副本。",
                "导入预设",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);

            if (answer == MessageBoxResult.Cancel)
            {
                Hint("已取消导入");
                return;
            }

            policy = answer == MessageBoxResult.Yes ? ConflictPolicy.Overwrite : ConflictPolicy.KeepBoth;
        }

        var result = App.Current.Presets.ApplyImport(incoming, policy);
        Hint(DescribeImport(result));
        BuildLists();
    }

    private static string DescribeImport(ImportResult result)
    {
        var parts = new List<string>();

        if (result.Added > 0) { parts.Add($"新增 {result.Added}"); }
        if (result.Updated > 0) { parts.Add($"覆盖 {result.Updated}"); }
        if (result.Skipped > 0) { parts.Add($"跳过 {result.Skipped}（内置名或空名）"); }

        string text = parts.Count == 0 ? "导入完成，但没有预设被写入" : $"导入完成：{string.Join("，", parts)}";

        if (result.Renamed > 0)
        {
            text += $"（其中 {result.Renamed} 个因重名另存为副本）";
        }

        return text;
    }

    private void Hint(string message) => PresetHint.Text = message;

    private Style Ghost() => (Style)FindResource("GhostButton");
}
