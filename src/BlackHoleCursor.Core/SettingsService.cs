using System.ComponentModel;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace BlackHoleCursor.Core;

/// <summary>开机自启注册表读写抽象，便于测试与将来替换实现。</summary>
public interface IAutoStartRegistry
{
    bool IsEnabled(string appName);

    void SetEnabled(string appName, bool enabled, string executablePath);
}

/// <summary>写入 HKCU Run 项（无需管理员权限）。</summary>
public sealed class WindowsAutoStartRegistry : IAutoStartRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null)
        {
            return false;
        }

        var value = key.GetValue(appName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(string appName, bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(appName, $"\"{executablePath}\"");
        }
        else
        {
            // 值不存在时 DeleteValue 会抛异常，先判断。
            if (key.GetValue(appName) is not null)
            {
                key.DeleteValue(appName);
            }
        }
    }
}

/// <summary>System.Drawing.Color 的 JSON 序列化（#AARRGGBB）。</summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return ColorUtilities.FromHex(text);
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        => writer.WriteStringValue(ColorUtilities.ToHex(value));
}

public static class ColorUtilities
{
    public static string ToHex(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color FromHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Color.FromArgb(0, 0, 0, 0);
        }

        var span = text.Trim().TrimStart('#').Trim();
        try
        {
            if (span.Length == 8 &&
                uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
            {
                return Color.FromArgb(
                    (byte)((argb >> 24) & 0xFF),
                    (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
            }

            if (span.Length == 6 &&
                uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            {
                return Color.FromArgb(
                    0xFF,
                    (byte)((rgb >> 16) & 0xFF),
                    (byte)((rgb >> 8) & 0xFF),
                    (byte)(rgb & 0xFF));
            }
        }
        catch (ArgumentException)
        {
            return Color.FromArgb(0, 0, 0, 0);
        }

        return Color.FromArgb(0, 0, 0, 0);
    }
}

/// <summary>
/// 配置持久化：JSON 读写、越界钳制、损坏恢复、防抖保存、开机自启同步。
/// </summary>
public sealed class SettingsService : IDisposable
{
    public const string AppName = "BlackHoleCursor";
    public const string ConfigFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new ColorJsonConverter() },
    };

    /// <summary>撤销历史的最大条目数。</summary>
    public const int MaxHistoryEntries = 40;

    /// <summary>连续改动合并成一条撤销记录的静默时长。</summary>
    private static readonly TimeSpan HistoryCoalesceWindow = TimeSpan.FromMilliseconds(700);

    private readonly IAutoStartRegistry _autoStartRegistry;
    private readonly TimeSpan _saveDebounce;
    private readonly object _gate = new();
    private readonly List<SettingsModel> _history = new();
    private Timer? _debounceTimer;
    private Timer? _historyTimer;
    private bool _saving;
    private bool _suppressHistory;
    private bool _disposed;

    public SettingsService(
        string? configDirectory = null,
        IAutoStartRegistry? autoStartRegistry = null,
        TimeSpan? saveDebounce = null)
    {
        ConfigDirectory = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);

        _autoStartRegistry = autoStartRegistry ?? new WindowsAutoStartRegistry();
        _saveDebounce = saveDebounce ?? TimeSpan.FromMilliseconds(300);

        Settings = new SettingsModel();
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public SettingsModel Settings { get; }

    public string ConfigDirectory { get; }

    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>上一次加载是否遇到配置文件损坏（供 UI 提示一次）。</summary>
    public bool RecoveredFromCorruptFile { get; private set; }

    /// <summary>
    /// M5.3：上一次加载是否遇到**旧版（v1）**配置文件（供 UI 提示一次）。
    /// 旧字段会被 <see cref="System.Text.Json"/> 静默忽略，不显式识别就等于用户配置被悄悄丢掉。
    /// </summary>
    public SettingsMigrationNotice? Migration { get; private set; }

    /// <summary>取出迁移提示并标记为已提示（保证只提示一次）。</summary>
    public SettingsMigrationNotice? TakeMigrationNotice()
    {
        var notice = Migration;
        Migration = null;
        return notice;
    }

    /// <summary>已保存次数，便于测试与诊断。</summary>
    public int SaveCount { get; private set; }

    /// <summary>是否存在可撤销的改动。用于决定「撤销」按钮是否可用。</summary>
    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _history.Count > 1 || _historyTimer is not null;
            }
        }
    }

    /// <summary>
    /// 撤销上一次改动。连续拖动滑块这类高频改动会被合并成一条记录。
    /// 返回是否真的撤销了（无可撤销内容时返回 false）。
    /// </summary>
    public bool Undo()
    {
        lock (_gate)
        {
            // 先把还没落定的当前状态入栈，再退回它前面那一条
            CommitHistory();

            if (_history.Count < 2)
            {
                return false;
            }

            _history.RemoveAt(_history.Count - 1);
            var target = _history[^1];

            _suppressHistory = true;
            try
            {
                Settings.CopyFrom(target);
            }
            finally
            {
                _suppressHistory = false;
            }

            SaveCore();
            return true;
        }
    }

    /// <summary>把当前的改动记入撤销历史（短时间内连续改动合并成一条）。</summary>
    private void RecordHistory()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _historyTimer?.Dispose();
            _historyTimer = new Timer(
                _ => CommitHistory(),
                null,
                HistoryCoalesceWindow,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void CommitHistory()
    {
        lock (_gate)
        {
            _historyTimer?.Dispose();
            _historyTimer = null;

            var snapshot = Settings.Clone();
            if (_history.Count > 0 && SameValues(_history[^1], snapshot))
            {
                return;
            }

            _history.Add(snapshot);
            if (_history.Count > MaxHistoryEntries)
            {
                _history.RemoveAt(0);
            }
        }
    }

    /// <summary>两份配置是否完全一致（用于避免把无变化的快照塞进历史）。</summary>
    private bool SameValues(SettingsModel a, SettingsModel b)
        => JsonSerializer.Serialize(a, SerializerOptions) == JsonSerializer.Serialize(b, SerializerOptions);

    /// <summary>
    /// 载入配置。文件不存在 → 默认值；解析失败 → 备份后回落默认值。
    /// </summary>
    public void Load()
    {
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(ConfigFilePath))
        {
            Settings.Clamp();
            SyncAutoStartFromRegistry();
            ResetHistory();
            return;
        }

        string json = File.ReadAllText(ConfigFilePath);

        // M5.3：旧版配置先认出来 —— 直接反序列化会让旧字段被静默忽略，
        // 用户看到的是「一堆默认值」而不知道自己的配置去哪了。
        var legacyFields = SettingsMigration.DetectLegacyFields(json);
        if (legacyFields.Count > 0)
        {
            Migration = new SettingsMigrationNotice(legacyFields, BackupLegacyFile());
            Settings.Clamp();
            SyncAutoStartFromRegistry();
            ResetHistory();
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<SettingsModel>(json, SerializerOptions);
            if (loaded is null)
            {
                throw new JsonException("反序列化结果为 null");
            }

            ApplyLoaded(loaded);
        }
        catch (JsonException)
        {
            RecoveredFromCorruptFile = true;
            BackupCorruptFile();
            Settings.Clamp();
        }

        Settings.Clamp();
        SyncAutoStartFromRegistry();
        ResetHistory();
    }

    /// <summary>以当前状态作为撤销历史的起点。</summary>
    private void ResetHistory()
    {
        lock (_gate)
        {
            _historyTimer?.Dispose();
            _historyTimer = null;
            _history.Clear();
            _history.Add(Settings.Clone());
        }
    }

    /// <summary>立即写入磁盘（原子替换）。</summary>
    public void SaveNow()
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        Directory.CreateDirectory(ConfigDirectory);
        Settings.Clamp();

        string json = JsonSerializer.Serialize(Settings, SerializerOptions);

        if (WriteAtomic(json))
        {
            SaveCount++;
        }
    }

    /// <summary>等待并立即落盘所有待保存改动。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            if (_saving)
            {
                _saving = false;
                SaveCore();
            }
        }
    }

    /// <summary>恢复默认值（不重置 AutoStart 之外的程序设置以外的项：全部恢复）。</summary>
    public void ResetToDefaults()
    {
        // 「恢复默认」是一次明确的离散操作，先落定当前状态，保证它可以被撤销
        CommitHistory();

        var defaults = new SettingsModel();
        Settings.CopyFrom(defaults);
        Settings.AutoStart = defaults.AutoStart;
        Settings.Clamp();
        SaveNow();
    }

    private void ApplyLoaded(SettingsModel loaded)
    {
        loaded.Clamp();
        Settings.CopyFrom(loaded);
        Settings.AutoStart = loaded.AutoStart;
    }

    private void SyncAutoStartFromRegistry()
    {
        try
        {
            Settings.AutoStart = _autoStartRegistry.IsEnabled(AppName);
        }
        catch (Exception)
        {
            // 注册表不可用时忽略，保持配置文件里的值。
        }
    }

    /// <summary>
    /// M5.3：把旧版配置文件挪走（而不是留着）—— 留着的话每次启动都会再认一次、再提示一遍。
    /// </summary>
    private string? BackupLegacyFile()
    {
        try
        {
            string backup = Path.Combine(
                ConfigDirectory,
                $"settings.v1-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(ConfigFilePath, backup, overwrite: true);
            return backup;
        }
        catch (IOException)
        {
            // 备份失败不阻断启动（原文件保持不变）
            return null;
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            string backup = Path.Combine(
                ConfigDirectory,
                $"settings.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(ConfigFilePath, backup, overwrite: true);
        }
        catch (IOException)
        {
            // 备份失败不阻断启动。
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressHistory)
        {
            RecordHistory();
        }

        if (e.PropertyName == nameof(SettingsModel.AutoStart))
        {
            ApplyAutoStart(Settings.AutoStart);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _saving = true;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ => { Flush(); },
                null,
                _saveDebounce,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            _autoStartRegistry.SetEnabled(AppName, enabled, exePath);
        }
        catch (Exception)
        {
            // 注册表写入失败不影响程序运行。
        }
    }

    private void SaveCore()
    {
        Directory.CreateDirectory(ConfigDirectory);
        string json = JsonSerializer.Serialize(Settings, SerializerOptions);

        if (WriteAtomic(json))
        {
            SaveCount++;
        }
    }

    /// <summary>
    /// 原子落盘：先写临时文件再整体替换。
    ///
    /// M6.2 加固：`File.Move` 在**目标文件被瞬时占用**时（杀软扫描、另一个进程正打开配置文件）
    /// 会抛 <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>。
    /// 保存发生在防抖定时器线程上，抛出去就是未处理异常 —— 配置没存上事小，程序崩了事大。
    /// 这里重试一次后放弃并记日志：下一次改动还会再存。
    /// </summary>
    private bool WriteAtomic(string json)
    {
        string temp = ConfigFilePath + ".tmp";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, ConfigFilePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    Thread.Sleep(50);
                    continue;
                }

                RuntimeLog.Error($"配置落盘失败（{ex.GetType().Name}）：{ex.Message}");
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _historyTimer?.Dispose();
            _historyTimer = null;
        }

        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        GC.SuppressFinalize(this);
    }
}
