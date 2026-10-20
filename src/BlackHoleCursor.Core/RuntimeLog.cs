namespace BlackHoleCursor.Core;

/// <summary>
/// 极简运行日志：写到 %LOCALAPPDATA%\BlackHoleCursor\log.txt。
/// 覆盖层跑在独立线程，出问题时用户无法在界面上看到原因，必须落到文件。
/// </summary>
public static class RuntimeLog
{
    private const int MaxChars = 512 * 1024;

    private const string FileName = "log.txt";

    private static readonly object Gate = new();

    private static readonly string UserFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SettingsService.AppName,
        FileName);

    private static string _filePath = UserFilePath;

    /// <summary>当前日志文件的完整路径。默认是用户目录下的 log.txt。</summary>
    public static string FilePath => _filePath;

    /// <summary>
    /// 把日志重定向到指定目录 —— 只给单元测试用。
    /// 以前这个路径是写死的，单测运行时会往用户真实的 log.txt 里灌进一堆假的
    /// 「首帧渲染完成」「抓屏失败」，排查实机问题时根本分不清哪条是真的。
    /// </summary>
    public static void RedirectToDirectory(string directory)
    {
        lock (Gate)
        {
            _filePath = Path.Combine(directory, FileName);
        }
    }

    /// <summary>恢复默认路径（用户目录）。生产代码不需要调用它。</summary>
    public static void UseDefaultPath()
    {
        lock (Gate)
        {
            _filePath = UserFilePath;
        }
    }

    /// <summary>是否仍指向用户的真实日志（单测用它做隔离断言）。</summary>
    public static bool IsWritingToUserProfile
        => string.Equals(_filePath, UserFilePath, StringComparison.OrdinalIgnoreCase);

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) => Write(
        "ERROR",
        exception is null ? message : $"{message}{Environment.NewLine}{Describe(exception)}");

    private static string Describe(Exception exception)
    {
        var builder = new System.Text.StringBuilder();
        var current = (Exception?)exception;
        int depth = 0;

        while (current is not null && depth < 6)
        {
            builder.Append(depth == 0 ? string.Empty : "  <- ")
                   .Append(current.GetType().Name)
                   .Append(": ")
                   .Append(current.Message)
                   .AppendLine();
            current = current.InnerException;
            depth++;
        }

        builder.Append(exception.StackTrace);
        return builder.ToString();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxChars)
                {
                    File.WriteAllText(FilePath, string.Empty);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // 日志失败不影响运行
        }
    }
}
