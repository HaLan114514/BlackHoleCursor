using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

/// <summary>内存版开机自启注册表，避免测试污染真实注册表。</summary>
public sealed class FakeAutoStartRegistry : IAutoStartRegistry
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public int WriteCount { get; private set; }

    public bool IsEnabled(string appName) => _values.ContainsKey(appName);

    public void SetEnabled(string appName, bool enabled, string executablePath)
    {
        WriteCount++;
        if (enabled)
        {
            _values[appName] = executablePath;
        }
        else
        {
            _values.Remove(appName);
        }
    }
}

/// <summary>可抛异常的注册表，用于验证写入失败不会导致程序崩溃。</summary>
public sealed class ThrowingAutoStartRegistry : IAutoStartRegistry
{
    public bool IsEnabled(string appName) => throw new InvalidOperationException("registry unavailable");

    public void SetEnabled(string appName, bool enabled, string executablePath)
        => throw new InvalidOperationException("registry unavailable");
}
