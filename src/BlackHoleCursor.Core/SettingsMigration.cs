using System.Text.Json;

namespace BlackHoleCursor.Core;

/// <summary>
/// M5.3（prd-v2 R3）：旧版（v1.0）配置文件的识别与迁移。
///
/// 背景：v2 换掉了整套视觉参数（prd-v2 §F2-1），旧 <c>settings.json</c> 里的
/// <c>HorizonRadius</c> / <c>RingSpinSpeed</c> / <c>LensRadius</c> 之类字段在
/// <see cref="SettingsModel"/> 上已经不存在 —— System.Text.Json 会**静默忽略**未知字段，
/// 于是用户的老配置会变成「一堆默认值」，而且完全没有任何提示。
///
/// 所以这里显式识别旧字段：备份原文件 → 回落 v2 默认 → 交给 UI 提示一次。
/// </summary>
public static class LegacySettingsFields
{
    /// <summary>
    /// v1 才有、v2 已删除的字段名（prd-v2 §F2-1 的移除清单 + N2 碎石带 + 渐变开关）。
    /// 大小写不敏感：手写或被其它工具改写过的配置文件未必保持 PascalCase。
    /// </summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "HorizonRadius",
        "RingRadius",
        "RingThickness",
        "RingInnerColor",
        "RingOuterColor",
        "RingTilt",
        "RingRoll",
        "RingOpacity",
        "RingSpinSpeed",
        "RingDebrisEnabled",
        "RingDebrisCount",
        "GlowIntensity",
        "GlobalScale",
        "LensStrength",
        "LensRadius",
        "GradientEnabled",
    ];

    public static bool IsLegacy(string fieldName)
        => Names.Any(n => string.Equals(n, fieldName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一次旧配置迁移的结果，供 UI 提示（<see cref="SettingsService.Migration"/>）。</summary>
public sealed class SettingsMigrationNotice
{
    public SettingsMigrationNotice(IReadOnlyList<string> legacyFields, string? backupPath)
    {
        LegacyFields = legacyFields;
        BackupPath = backupPath;
    }

    /// <summary>在旧文件里认出的 v1 字段名。</summary>
    public IReadOnlyList<string> LegacyFields { get; }

    /// <summary>旧文件的备份位置（备份失败时为 null）。</summary>
    public string? BackupPath { get; }

    /// <summary>给用户的提示文案。</summary>
    public string Message =>
        $"检测到旧版（v1）配置文件（{LegacyFields.Count} 个已废弃字段），已恢复默认外观。" +
        (BackupPath is null
            ? "（备份失败，原文件保持不变）"
            : $"原文件已备份为 {Path.GetFileName(BackupPath)}。");
}

public static class SettingsMigration
{
    /// <summary>
    /// 返回配置文本里出现的**旧版**字段名（按文件里的出现顺序，去重）。
    /// 不能解析时返回空 —— 解析失败是「文件损坏」那条路径的事，由调用方另行处理。
    /// </summary>
    public static IReadOnlyList<string> DetectLegacyFields(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var found = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (LegacySettingsFields.IsLegacy(property.Name) && !found.Contains(property.Name))
                {
                    found.Add(property.Name);
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
