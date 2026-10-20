using System.Drawing;

namespace BlackHoleCursor.Tests;

/// <summary>
/// v4（V4-6 / V4-7 / V4-8）：程序图标与版本号。
///
/// 为什么值得测：ICO 是手工拼的二进制格式，头写错了不会有任何编译错误，
/// 只是「图标在某些尺寸下糊掉 / 干脆不显示」—— 而且只在真机上才看得出来。
/// 这里直接读文件解析 ICO 头，把尺寸表钉住。
/// </summary>
public class IconTests
{
    private static readonly string ResourcesDirectory = Locate("Resources");
    private static readonly string AppDirectory = Locate(".");
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Locate(string folder)
    {
        string path = Path.Combine(FindRepoRoot(), "src", "BlackHoleCursor", folder);
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new DirectoryNotFoundException($"找不到 {path}");
        }

        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlackHoleCursor.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到仓库根（没有 BlackHoleCursor.sln）");
    }

    // -------------------------------------------------------------------------
    // 两张 ICO 本身
    // -------------------------------------------------------------------------

    [Fact]
    public void BothIconsExistAndCanBeConstructed()
    {
        foreach (string name in new[] { "app.ico", "app-off.ico" })
        {
            string path = Path.Combine(ResourcesDirectory, name);

            Assert.True(File.Exists(path),
                $"缺少图标资源 {name} —— 需要先跑 `dotnet run --project tools/IconGen/IconGen.csproj`");

            using var icon = new Icon(path);

            Assert.True(icon.Width > 0 && icon.Height > 0, $"{name} 构造出来的图标尺寸为 0");
        }
    }

    /// <summary>
    /// ICO 头 + 尺寸表。多图 ICO 的意义就在于「系统按需要挑一张」，
    /// 缺了 16 就会在标题栏被拉花，缺了 256 就会在高 DPI 下模糊。
    /// </summary>
    [Fact]
    public void IconsAreMultiResolution()
    {
        foreach (string name in new[] { "app.ico", "app-off.ico" })
        {
            var (header, sizes) = ReadIcoHeader(Path.Combine(ResourcesDirectory, name));

            Assert.Equal(0, header.Reserved);
            Assert.Equal(1, header.Type);
            Assert.True(header.Count >= 3, $"{name} 只有 {header.Count} 张图，多图 ICO 才有意义");

            foreach (int expected in new[] { 16, 32, 48, 256 })
            {
                Assert.Contains(expected, sizes);
            }
        }
    }

    /// <summary>
    /// 启用 / 禁用必须是**看起来**不同的两张（托盘靠它区分状态）。
    /// 只比字节相等不够 —— 两张都生成自同一份代码，改坏了也可能一起变。
    /// 所以这里比的是渲染后的平均颜色。
    /// </summary>
    [Fact]
    public void EnabledAndDisabledIconsLookDifferent()
    {
        using var on = new Icon(Path.Combine(ResourcesDirectory, "app.ico"));
        using var off = new Icon(Path.Combine(ResourcesDirectory, "app-off.ico"));

        using var onBitmap = on.ToBitmap();
        using var offBitmap = off.ToBitmap();

        var (r1, g1, b1) = AverageColor(onBitmap);
        var (r2, g2, b2) = AverageColor(offBitmap);

        double difference = (Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2)) / 3.0;

        // 实测平均色差 25.33（启用是橙黑、禁用是灰暗），阈值取 10 —— 远高于噪声，留足余量
        Assert.True(difference > 10.0,
            $"启用 / 禁用两张图标看起来几乎一样（平均色差 {difference:F2}），托盘会分不出状态");
    }

    // -------------------------------------------------------------------------
    // 版本号（V4-8）：三处必须一致，否则用户看到 v4.0、安装包叫 1.0.0
    // -------------------------------------------------------------------------

    [Fact]
    public void VersionIsBumpedEverywhere()
    {
        string mainWindow = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));
        string installer = File.ReadAllText(Path.Combine(RepoRoot, "installer", "BlackHoleCursor.iss"));
        string project = File.ReadAllText(Path.Combine(AppDirectory, "BlackHoleCursor.csproj"));

        Assert.Contains("CURSOR v4.0", mainWindow, StringComparison.Ordinal);
        Assert.Contains("4.0.0", installer, StringComparison.Ordinal);
        Assert.Contains("ApplicationIcon", project, StringComparison.Ordinal);

        // 顺手保证没有残留的旧版本号
        Assert.DoesNotContain("v1.0", mainWindow, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------

    private static ((ushort Reserved, ushort Type, ushort Count) Header, List<int> Sizes) ReadIcoHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        ushort reserved = reader.ReadUInt16();
        ushort type = reader.ReadUInt16();
        ushort count = reader.ReadUInt16();

        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            byte width = reader.ReadByte();
            _ = reader.ReadByte();          // height（与 width 相同）
            _ = reader.ReadByte();          // palette
            _ = reader.ReadByte();          // reserved
            _ = reader.ReadUInt16();        // color planes
            _ = reader.ReadUInt16();        // bits per pixel
            _ = reader.ReadUInt32();        // data size
            _ = reader.ReadUInt32();        // data offset

            // ICO 里 256 存成 0
            sizes.Add(width == 0 ? 256 : width);
        }

        return ((reserved, type, count), sizes);
    }

    private static (double R, double G, double B) AverageColor(Bitmap bitmap)
    {
        double r = 0, g = 0, b = 0;
        int count = 0;

        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        return (r / count, g / count, b / count);
    }
}
