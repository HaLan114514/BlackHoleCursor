using System.Runtime.CompilerServices;
using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

/// <summary>
/// 测试隔离：单测一律不许碰用户的真实目录（日志 / 配置 / 预设）。
///
/// 背景：`RuntimeLog.FilePath` 以前是写死的，单测跑一次就往用户真实的 log.txt 里
/// 灌进一堆假的「首帧渲染完成」「抓屏失败」，而且用的是默认参数（盘外半径 36），
/// 与实机日志混在一起根本分不清哪条是真的 —— 排查问题时被它误导过不止一次。
/// 配置与预设服务本身支持传入目录，但同样必须确认没有测试漏传。
/// </summary>
internal static class TestIsolation
{
    /// <summary>本次测试运行专用的临时目录。</summary>
    public static string Directory { get; } = CreateDirectory();

    /// <summary>用户的真实配置目录（用于断言「没被写」）。</summary>
    public static string UserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SettingsService.AppName);

    /// <summary>程序集加载时执行，早于任何测试。</summary>
    [ModuleInitializer]
    internal static void RedirectRuntimeLog() => RuntimeLog.RedirectToDirectory(Directory);

    private static string CreateDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bhc-tests-runtime", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
}

public class TestIsolationTests
{
    [Fact]
    public void RuntimeLog_IsRedirectedAwayFromTheUserProfile()
    {
        Assert.False(RuntimeLog.IsWritingToUserProfile, "单测不允许写用户的真实日志");
        Assert.StartsWith(Path.GetTempPath(), RuntimeLog.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrittenLogLines_NeverReachTheUsersLogFile()
    {
        // 真的写一条，再确认它没出现在用户目录里 —— 只断言路径容易被将来的改动绕过
        string marker = "隔离验证-" + Guid.NewGuid().ToString("N");

        RuntimeLog.Info(marker);

        Assert.Contains(marker, File.ReadAllText(RuntimeLog.FilePath));

        string userLog = Path.Combine(TestIsolation.UserDirectory, "log.txt");
        if (File.Exists(userLog))
        {
            Assert.DoesNotContain(marker, File.ReadAllText(userLog));
        }
    }

    [Fact]
    public void UserConfigDirectory_IsNotInsideTheTempPath()
    {
        // 反向确认：UserDirectory 确实是用户目录，否则上面的断言就失去意义了
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TestIsolation.UserDirectory,
            StringComparison.OrdinalIgnoreCase);
    }
}
