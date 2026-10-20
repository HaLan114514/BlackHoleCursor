using BlackHoleCursor.Core;

namespace BlackHoleCursor.Tests;

public class SingleInstanceLockTests
{
    private static string UniqueName() => @"Local\BlackHoleCursor_Test_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstInstance_IsPrimary()
    {
        using var first = new SingleInstanceLock(UniqueName());

        Assert.True(first.IsPrimary);
    }

    [Fact]
    public void SecondInstance_IsNotPrimary()
    {
        string name = UniqueName();
        using var first = new SingleInstanceLock(name);
        using var second = new SingleInstanceLock(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary, "第二个实例必须识别为从实例，避免重复渲染覆盖层");
    }

    [Fact]
    public void AfterDispose_SlotIsReleased()
    {
        string name = UniqueName();
        var first = new SingleInstanceLock(name);
        first.Dispose();

        using var second = new SingleInstanceLock(name);
        Assert.True(second.IsPrimary, "主实例退出后，新的实例应能接管");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var instance = new SingleInstanceLock(UniqueName());

        instance.Dispose();
        instance.Dispose();

        Assert.True(true);
    }

    [Fact]
    public void DefaultName_IsGlobal()
    {
        using var instance = new SingleInstanceLock();

        Assert.StartsWith(@"Global\", instance.Name);
    }
}
