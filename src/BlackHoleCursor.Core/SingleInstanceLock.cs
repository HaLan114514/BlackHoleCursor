namespace BlackHoleCursor.Core;

/// <summary>
/// 跨进程单实例锁。第二个实例启动时应唤醒已有实例后退出。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    public const string DefaultName = @"Global\BlackHoleCursor_SingleInstance";

    private readonly Mutex _mutex;
    private readonly bool _acquired;
    private bool _disposed;

    public SingleInstanceLock(string name = DefaultName)
    {
        Name = name;

        _mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
        _acquired = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
        }
    }

    public string Name { get; }

    /// <summary>当前进程是否为首个（主）实例。</summary>
    public bool IsPrimary => _acquired;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_acquired)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
