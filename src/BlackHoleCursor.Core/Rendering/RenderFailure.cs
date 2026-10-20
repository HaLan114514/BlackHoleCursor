using System.Diagnostics.CodeAnalysis;

namespace BlackHoleCursor.Core.Rendering;

/// <summary>Present 失败后的处置分类（见 plan.md 5.2 降级矩阵）。</summary>
public enum PresentFailureKind
{
    /// <summary>设备被移除 / 重置 / 挂起：必须重建 Device 与全部资源。</summary>
    DeviceLost,

    /// <summary>可恢复的失败（遮挡、临时性错误）：跳过本帧即可，连续失败过多才停止。</summary>
    Recoverable,

    /// <summary>驱动内部错误等不可恢复情况：直接终止覆盖层渲染。</summary>
    Fatal,
}

/// <summary>M6.2：降级矩阵给出的处置动作。抽出来是为了让矩阵能被单测覆盖。</summary>
public enum FailureAction
{
    /// <summary>重建设备与全部渲染资源（设备丢失，且还没到重建上限）。</summary>
    Rebuild,

    /// <summary>跳过本帧，下一帧继续尝试。</summary>
    SkipFrame,

    /// <summary>停止覆盖层渲染（并恢复系统光标）。</summary>
    Stop,
}

/// <summary>
/// M6.2：着色器编译失败。
///
/// 单独定义而不是让它冒一个原始的 D3DCompiler 异常出去：
/// 覆盖层线程 catch 到它之后要能明确地「记录 + 恢复系统光标 + 在主界面显示原因」，
/// 而调用方需要一个**可识别**的类型来判断这是不是编译失败。
/// </summary>
public sealed class ShaderCompilationException : Exception
{
    public ShaderCompilationException(string entryPoint, Exception inner)
        : base($"着色器编译失败（入口 {entryPoint}）：{inner.Message}", inner)
    {
        EntryPoint = entryPoint;
    }

    /// <summary>编译失败的入口点（主流程是 <c>CompositePS</c>，验证入口各有其名）。</summary>
    public string EntryPoint { get; }
}

/// <summary>
/// DXGI / D3D11 失败码的分类。抽成纯函数是为了让「降级矩阵」可以被单元测试覆盖，
/// 而不是散落在渲染循环的 catch 块里。
/// </summary>
public static class RenderFailure
{
    public const int DeviceRemoved = unchecked((int)0x887A0005);

    public const int DeviceHung = unchecked((int)0x887A0006);

    public const int DeviceReset = unchecked((int)0x887A0007);

    public const int WasStillDrawing = unchecked((int)0x887A000A);

    public const int DriverInternalError = unchecked((int)0x887A0020);

    /// <summary>设备丢失最多重建这么多次，超过就停止渲染（prd-v2 F5-1）。</summary>
    public const int MaxDeviceRebuilds = 3;

    /// <summary>可恢复的 Present 失败连续这么多次后停止渲染（约 2 秒 @60fps 之外再多给些余量）。</summary>
    public const int MaxConsecutivePresentFailures = 120;

    /// <summary>未知错误码（如异常转换而来）默认按可恢复处理。</summary>
    public static PresentFailureKind Classify(int hresult) => hresult switch
    {
        DeviceRemoved or DeviceHung or DeviceReset or WasStillDrawing => PresentFailureKind.DeviceLost,
        DriverInternalError => PresentFailureKind.Fatal,
        _ => PresentFailureKind.Recoverable,
    };

    /// <summary>
    /// M6.2 降级矩阵：把「失败类型 + 已失败次数」映射成一个明确动作。
    ///
    /// 抽成纯函数是为了让整张矩阵能被单元测试逐格覆盖，
    /// 而不是散落在渲染循环的 catch 块里靠实机运气验证。
    /// </summary>
    /// <param name="kind">失败类型。</param>
    /// <param name="consecutivePresentFailures">连续 Present 失败次数（含本次）。</param>
    /// <param name="deviceRebuilds">**已经**完成的设备重建次数。</param>
    public static FailureAction Decide(
        PresentFailureKind kind,
        int consecutivePresentFailures,
        int deviceRebuilds)
        => kind switch
        {
            // 设备丢失：先重建，重建到上限还不行才认输
            PresentFailureKind.DeviceLost =>
                deviceRebuilds >= MaxDeviceRebuilds ? FailureAction.Stop : FailureAction.Rebuild,

            // 驱动内部错误没有可恢复路径
            PresentFailureKind.Fatal => FailureAction.Stop,

            // 可恢复：跳过本帧，连续失败太多说明环境已经不正常了
            _ => consecutivePresentFailures >= MaxConsecutivePresentFailures
                ? FailureAction.Stop
                : FailureAction.SkipFrame,
        };
}
