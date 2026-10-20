using System.Drawing;
using BlackHoleCursor;

// v4（V4-6）：生成程序图标。产物提交到 src/BlackHoleCursor/Resources/，
// 编译**不依赖**这个工具 —— 它只是生成器，改画法时手动跑一次然后把产物提交。
//
//   dotnet run --project tools/IconGen/IconGen.csproj

// 尺寸从大到小：256 用于高 DPI 与「大图标」视图，16 用于标题栏与托盘
int[] sizes = [256, 48, 32, 16];

// ⚠️ `dotnet run --project ... --nologo` 会把 `--nologo` 也当成**本程序**的参数传进来，
// 直接取 args[0] 就会把输出目录变成一个叫「--nologo」的文件夹。所以只认不是开关的参数。
string? requested = Array.Find(args, a => !a.StartsWith('-'));
string outputDirectory = requested ?? Path.Combine(FindRepoRoot(), "src", "BlackHoleCursor", "Resources");

// 数「..\\」很容易数错（bin/Release/<tfm> 的深度会因配置而变），
// 直接往上找到有 sln 的那一层 —— 与测试里那套 Locate 的做法一致。
static string FindRepoRoot()
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

    throw new DirectoryNotFoundException("找不到仓库根（没找到 BlackHoleCursor.sln）");
}

Directory.CreateDirectory(outputDirectory);

foreach ((string fileName, bool enabled) in new[] { ("app.ico", true), ("app-off.ico", false) })
{
    var images = sizes
        .Select(size => (Size: size, Png: IconFactory.RenderIconPng(size, enabled)))
        .ToArray();

    using Icon icon = IconFactory.PngsToIcon(images);

    string path = Path.Combine(outputDirectory, fileName);
    using FileStream file = File.Create(path);
    icon.Save(file);

    Console.WriteLine(
        $"{fileName}  {string.Join(" / ", sizes)}  共 {images.Length} 张，"
        + $"{new FileInfo(path).Length / 1024.0:F1} KB");
}

Console.WriteLine($"已写入 {outputDirectory}");
