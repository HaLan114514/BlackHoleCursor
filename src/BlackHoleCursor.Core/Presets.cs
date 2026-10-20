using System.Text.Json;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Core;

/// <summary>一个外观预设（不含程序设置，如开机自启）。</summary>
public sealed class SettingsPreset
{
    public string Name { get; set; } = string.Empty;

    public SettingsModel Appearance { get; set; } = new();

    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// M5.2：把本预设**应用到**一份设置上（增量补丁，不是整体覆盖）。
    /// 大小与性能档位保持用户当前选择，详见 <see cref="SettingsModel.ApplyAppearanceFrom"/>。
    /// </summary>
    public void ApplyTo(SettingsModel target) => target.ApplyAppearanceFrom(Appearance);
}

/// <summary>
/// 内置预设：一键切换不同风格的黑洞。
/// v2（M1.8.4）起直接沿用 <see cref="LookPresets"/> 的 8 个预设（prd-v2 §6.3），
/// 不再自己维护第二份数值表 —— 渲染侧与配置侧共用同一个真源，避免两边漂移。
/// </summary>
public static class PresetLibrary
{
    /// <summary>Inferno / Gargantua / Quasar / M87* donut / Blazar / Face-on ember / Pure lens / Zen</summary>
    public static IReadOnlyList<string> BuiltInNames => LookPresets.Names;

    public static bool IsBuiltIn(string name) => LookPresets.Names.Contains(name);

    public static SettingsModel CreateBuiltIn(string name)
    {
        var settings = new SettingsModel();
        LookPresets.ApplyTo(LookPresets.Create(name), settings);
        settings.Clamp();
        return settings;
    }

    public static SettingsPreset Create(string name, SettingsModel appearance, bool isBuiltIn = false)
        => new()
        {
            Name = name,
            Appearance = (SettingsModel)appearance.Clone(),
            IsBuiltIn = isBuiltIn,
        };
}

/// <summary>预设的读写与持久化（自定义预设存于配置目录）。</summary>
public sealed class PresetService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,

        // v4（V4-3）：导入的文件**大小写不敏感**。
        // 原来只按 PascalCase 匹配，于是 `{"version": 99}` 这种小写字段根本读不进 `Version`，
        // 版本号悄悄退回默认值 1 —— 「版本不符」的防线等于形同虚设。
        PropertyNameCaseInsensitive = true,

        Converters = { new ColorJsonConverter() },
    };

    private readonly string _filePath;
    private readonly Dictionary<string, SettingsPreset> _custom = new(StringComparer.OrdinalIgnoreCase);

    public PresetService(string? directory = null)
    {
        _filePath = Path.Combine(
            directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SettingsService.AppName),
            "presets.json");

        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SettingsPreset> Custom => _custom.Values.OrderBy(p => p.Name).ToList();

    public IReadOnlyList<SettingsPreset> All => PresetLibrary.BuiltInNames
        .Select(name => PresetLibrary.Create(name, PresetLibrary.CreateBuiltIn(name), isBuiltIn: true))
        .Concat(Custom)
        .ToList();

    public bool IsCorrupt { get; private set; }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<SettingsPreset>>(File.ReadAllText(_filePath), SerializerOptions);
            if (loaded is null)
            {
                return;
            }

            foreach (var preset in loaded)
            {
                if (string.IsNullOrWhiteSpace(preset.Name) || PresetLibrary.IsBuiltIn(preset.Name))
                {
                    continue;
                }

                preset.Appearance.Clamp();
                _custom[preset.Name] = preset;
            }
        }
        catch (JsonException)
        {
            IsCorrupt = true;
        }
    }

    /// <summary>保存自定义预设。内置名称不允许覆盖。</summary>
    public bool TrySave(string name, SettingsModel settings)
    {
        if (string.IsNullOrWhiteSpace(name) || PresetLibrary.IsBuiltIn(name))
        {
            return false;
        }

        _custom[name] = PresetLibrary.Create(name, settings);
        Persist();
        return true;
    }

    /// <summary>
    /// v4（V4-2）：重命名「我的」里的预设。
    ///
    /// 拒绝：空名、内置名，以及**与另一个**预设重名（字典是 <c>OrdinalIgnoreCase</c>）。
    /// ⚠️ 只改大小写（MyPreset → mypreset）必须放行 —— 它命中的是**同一个**条目，
    /// 所以判重要用引用比较；写成「名字已存在就拒绝」会把改大小写也一起错杀。
    /// </summary>
    public bool TryRename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        string target = newName.Trim();

        if (PresetLibrary.IsBuiltIn(oldName) || PresetLibrary.IsBuiltIn(target))
        {
            return false;
        }

        if (!_custom.TryGetValue(oldName, out var preset))
        {
            return false;
        }

        if (_custom.TryGetValue(target, out var clash) && !ReferenceEquals(clash, preset))
        {
            return false;
        }

        _custom.Remove(oldName);
        preset.Name = target;
        _custom[target] = preset;
        Persist();
        return true;
    }

    /// <summary>
    /// v4（V4-2）：用当前设置**覆盖**「我的」里已存在的预设。
    ///
    /// 只覆盖、不新建：名字不存在（或落在内置上）就拒绝 ——
    /// 新建走 <see cref="TrySave"/>，否则用户点错一下就会静默多出一个预设。
    /// </summary>
    public bool TrySaveOver(string name, SettingsModel settings)
    {
        if (string.IsNullOrWhiteSpace(name) || PresetLibrary.IsBuiltIn(name))
        {
            return false;
        }

        string target = name.Trim();

        if (!_custom.ContainsKey(target))
        {
            return false;
        }

        _custom[target] = PresetLibrary.Create(target, settings);
        Persist();
        return true;
    }

    public bool TryGet(string name, out SettingsModel settings)
    {
        if (PresetLibrary.IsBuiltIn(name))
        {
            settings = PresetLibrary.CreateBuiltIn(name);
            return true;
        }

        if (_custom.TryGetValue(name, out var preset))
        {
            settings = preset.Appearance.Clone();
            return true;
        }

        settings = new SettingsModel();
        return false;
    }

    public bool TryDelete(string name)
    {
        if (PresetLibrary.IsBuiltIn(name))
        {
            return false;
        }

        if (!_custom.Remove(name))
        {
            return false;
        }

        Persist();
        return true;
    }

    /// <summary>v4（V4-3）：导出文件格式的版本号。字段将来变了靠它判断能不能读。</summary>
    public const int PresetFileVersion = 1;

    /// <summary>
    /// v4（V4-3）：把「我的」里的预设导出成文件。
    /// 只导自定义预设（内置的一直在程序里，导出去没意义）；名字不存在就跳过。
    /// </summary>
    public bool TryExport(IEnumerable<string> names, string path)
    {
        var list = new List<SettingsPreset>();

        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!_custom.TryGetValue(name, out var preset))
            {
                continue;
            }

            list.Add(PresetLibrary.Create(preset.Name, preset.Appearance));
        }

        if (list.Count == 0)
        {
            return false;
        }

        var file = new PresetExportFile
        {
            Version = PresetFileVersion,
            Presets = list,
        };

        try
        {
            WriteAtomically(path, JsonSerializer.Serialize(file, SerializerOptions));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// v4（V4-3）：只**解析**导入文件，不入库 —— 先给 UI 看有没有重名，再决定策略。
    /// </summary>
    /// <param name="error">失败原因，可直接显示给用户（已本地化文案）。</param>
    public bool TryParsePresetFile(string path, out IReadOnlyList<SettingsPreset> presets, out string error)
    {
        presets = Array.Empty<SettingsPreset>();
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = "文件不存在";
            return false;
        }

        try
        {
            var file = JsonSerializer.Deserialize<PresetExportFile>(File.ReadAllText(path), SerializerOptions);

            if (file is null)
            {
                error = "文件为空或格式不正确";
                return false;
            }

            // 版本不符一律不读：宁可让用户知道，也不要把读错的数值灌进库里
            if (file.Version != PresetFileVersion)
            {
                error = $"文件版本是 {file.Version}，当前只支持 {PresetFileVersion}";
                return false;
            }

            var list = new List<SettingsPreset>();
            foreach (var preset in file.Presets)
            {
                if (string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }

                preset.Appearance ??= new SettingsModel();
                preset.Appearance.Clamp();
                list.Add(preset);
            }

            presets = list;
            return true;
        }
        catch (JsonException)
        {
            error = "不是有效的预设文件（JSON 解析失败）";
            return false;
        }
        catch (IOException)
        {
            error = "文件读取失败";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有权限读取该文件";
            return false;
        }
    }

    /// <summary>
    /// v4（V4-3）：按策略把解析好的预设入库。
    /// 内置名一律跳过（与 <see cref="Load"/> 的规则一致），因此不需要在这里报错。
    /// </summary>
    public ImportResult ApplyImport(IReadOnlyList<SettingsPreset> presets, ConflictPolicy policy)
    {
        var result = new ImportResult();

        foreach (var incoming in presets)
        {
            if (string.IsNullOrWhiteSpace(incoming.Name) || PresetLibrary.IsBuiltIn(incoming.Name))
            {
                result.Skipped++;
                continue;
            }

            string target = incoming.Name.Trim();

            if (_custom.ContainsKey(target))
            {
                if (policy == ConflictPolicy.KeepBoth)
                {
                    // 另存为：原预设不动，来的这份换个名字存
                    target = UniqueName(target);
                    result.Renamed++;
                    result.Added++;
                }
                else
                {
                    result.Updated++;
                }
            }
            else
            {
                result.Added++;
            }

            _custom[target] = PresetLibrary.Create(target, incoming.Appearance);
        }

        if (result.Total > 0)
        {
            Persist();
        }

        return result;
    }

    /// <summary>生成「名字 (2)」「名字 (3)」……直到不与现有预设冲突。</summary>
    private string UniqueName(string baseName)
    {
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} ({i})";
            if (!_custom.ContainsKey(candidate) && !PresetLibrary.IsBuiltIn(candidate))
            {
                return candidate;
            }
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>原子落盘：先写临时文件再整体替换，避免写入中断把 presets.json 写坏（与 SettingsService 一致）。</summary>
    private void Persist()
        => WriteAtomically(_filePath, JsonSerializer.Serialize(Custom, SerializerOptions));
}

/// <summary>v4（V4-3）：导入时遇到重名的处理策略。</summary>
public enum ConflictPolicy
{
    /// <summary>用导入的覆盖已有的。</summary>
    Overwrite = 0,

    /// <summary>已有的不动，导入的另存为「名字 (2)」。</summary>
    KeepBoth = 1,
}

/// <summary>v4（V4-3）：一次导入的结果，供 UI 报告「新增 / 覆盖 / 跳过」了多少。</summary>
public sealed class ImportResult
{
    /// <summary>新增的条数（另存为产生的副本也计入）。</summary>
    public int Added { get; set; }

    /// <summary>覆盖的条数。</summary>
    public int Updated { get; set; }

    /// <summary>跳过的条数（内置名 / 空名）。</summary>
    public int Skipped { get; set; }

    /// <summary>因重名而另存为的条数（<c>KeepBoth</c> 时才可能 &gt; 0）。</summary>
    public int Renamed { get; set; }

    /// <summary>入库的条数。Renamed 已计入 Added，不重复累加。</summary>
    public int Total => Added + Updated + Skipped;
}

/// <summary>
/// v4（V4-3）：导出文件的结构。带 <see cref="Version"/> 是为了以后字段变了能**明确拒绝**，
/// 而不是拿旧格式硬解析出一堆错值（风险 R1）。
/// 未知字段交给 <c>JsonSerializer</c> 默认忽略，缺字段则落到 <c>SettingsModel</c> 的默认值。
/// </summary>
public sealed class PresetExportFile
{
    public int Version { get; set; } = PresetService.PresetFileVersion;

    public List<SettingsPreset> Presets { get; set; } = new();
}
