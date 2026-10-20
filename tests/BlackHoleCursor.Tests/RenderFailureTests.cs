using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>present 失败分类决定了「重建设备 / 跳过本帧 / 停止渲染」三条不同路径，必须准确。</summary>
public class RenderFailureTests
{
    [Theory]
    [InlineData(RenderFailure.DeviceRemoved)]
    [InlineData(RenderFailure.DeviceHung)]
    [InlineData(RenderFailure.DeviceReset)]
    [InlineData(RenderFailure.WasStillDrawing)]
    public void DeviceLostCodes_AreClassifiedAsDeviceLost(int hresult)
        => Assert.Equal(PresentFailureKind.DeviceLost, RenderFailure.Classify(hresult));

    [Fact]
    public void DriverInternalError_IsFatal()
        => Assert.Equal(PresentFailureKind.Fatal, RenderFailure.Classify(RenderFailure.DriverInternalError));

    [Theory]
    [InlineData(RenderFailure.DeviceRemoved - 1)]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(0)]
    public void UnknownCodes_AreRecoverable(int hresult)
        => Assert.Equal(PresentFailureKind.Recoverable, RenderFailure.Classify(hresult));

    [Fact]
    public void DeviceRemovedCode_MatchesDxgiDefinition()
        => Assert.Equal(unchecked((int)0x887A0005), RenderFailure.DeviceRemoved);
}
