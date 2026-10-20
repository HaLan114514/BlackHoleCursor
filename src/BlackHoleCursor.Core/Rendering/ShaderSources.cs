using System.Reflection;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>从嵌入资源读取 HLSL 并拼接成完整编译单元。</summary>
public static class ShaderSources
{
    public const string VertexEntryPoint = "FullscreenVS";
    public const string PixelEntryPoint = "CompositePS";
    public const string VertexProfile = "vs_5_0";
    public const string PixelProfile = "ps_5_0";

    private static readonly string[] Parts =
    {
        "common.hlsl",
        "disk.hlsl",
        "kerr.hlsl",
        "lens.hlsl",
        "composite.hlsl",
    };

    /// <summary>完整编译单元：公共定义 + 透镜 + 黑洞 + 合成。</summary>
    public static string FullSource { get; } = Concatenate();

    private static string Concatenate()
    {
        var assembly = typeof(ShaderSources).Assembly;
        var builder = new System.Text.StringBuilder();

        foreach (string part in Parts)
        {
            using var stream = assembly.GetManifestResourceStream($"BlackHoleCursor.Core.Shaders.{part}")
                               ?? throw new InvalidOperationException($"缺少嵌入的着色器资源：{part}");
            using var reader = new StreamReader(stream);
            builder.AppendLine(reader.ReadToEnd());
        }

        return builder.ToString();
    }
}
