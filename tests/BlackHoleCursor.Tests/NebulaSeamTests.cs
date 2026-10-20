using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Rendering;

namespace BlackHoleCursor.Tests;

/// <summary>
/// fixes-v3 问题 6：开星云后出现一条**跟着黑洞走**的割裂缝。
///
/// 根因：`nebula()` 用 `sph = (atan2(d.x, -d.z), asin(d.y))` 索引噪声，
/// 而方位角每整圈是 `2π × 2.2 = 13.82` 格（**非整数**）——
/// 跨过 `atan2` 的分支切口（±π）时格子索引整块跳 ~13.8 格，
/// 两侧变成两片互不相干的云，接缝就是那条缝。
///
/// 修法：方位角改成「每整圈整数格」并用 `vnoiseWrapXY` 把 x 也 wrap 起来
/// （与 `vnoiseWrapY` 同源 —— 吸积盘条纹当年踩的就是这同一个坑，只是那里只 wrap 了 y）。
///
/// 这里用验证入口 <c>NebulaSeamProbePS</c> 把「方位角扫一整圈」的星云值画出来：
/// uv.x 的 0 与 1 在方位角上是 ±π，空间里是**相邻**的两个方向，值必须连续。
/// </summary>
public class NebulaSeamTests : IDisposable
{
    private const int Size = 256;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _renderer;
    private readonly PreviewRenderer _preview;

    public NebulaSeamTests()
    {
        _device = RenderDevice.Create(preferHardware: false);
        _renderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _renderer, Size, Size);
    }

    public void Dispose()
    {
        _preview.Dispose();
        _renderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>渲染一整条「方位角扫描线」（取 uv.y = 0.5，仰角 ≈ 0）。</summary>
    private double[] AzimuthScan()
    {
        using var pixelShader = _renderer.CreatePixelShader("NebulaSeamProbePS");

        var look = LookPresets.Create(LookPresets.Gargantua);
        look.NebulaGain = 1.0;   // 只是为了让值落在可测范围，探针本身不乘增益

        var description = new SceneDescription(
            Size, Size, Size / 2.0, Size / 2.0,
            new SettingsModel(), 0.0, true,
            Look: look,
            ScreenHeight: Size);

        byte[] buffer = _preview.Render(description, pixelShader);

        var values = new double[Size];
        int row = Size / 2;

        for (int x = 0; x < Size; x++)
        {
            int i = ((row * Size) + x) * 4;
            values[x] = (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3.0;
        }

        return values;
    }

    /// <summary>
    /// 主判据：跨过 atan2 的分支切口（扫描线的两端，方位角 ±π）时星云必须**连续**。
    /// 两端在空间里是相邻的两个方向，跳变只能来自「格子索引整块跳格」这个 bug。
    /// </summary>
    [Fact]
    public void TheNebulaIsContinuousAcrossTheAzimuthBranchCut()
    {
        double[] scan = AzimuthScan();

        // 切口处：扫描线两端（uv.x = 0 与 1）
        double cutJump = Math.Abs(scan[0] - scan[Size - 1]);

        // 相邻采样点之间的正常起伏（同一次扫描里应有的变化量）
        double maxNeighbor = 0.0;
        for (int x = 1; x < Size; x++)
        {
            maxNeighbor = Math.Max(maxNeighbor, Math.Abs(scan[x] - scan[x - 1]));
        }

        Assert.True(
            maxNeighbor > 0.0,
            "扫描线应当有起伏（否则这条测不出连续性 —— 可能整条都是常数）");

        Assert.True(
            cutJump <= (maxNeighbor * 2.0) + 1.0,
            $"跨方位角切口处不该出现跳变：切口跳变 {cutJump:F1}，"
            + $"而相邻采样的最大起伏只有 {maxNeighbor:F1}（修复前会是好几十）；"
            + $"两端的值 scan[0]={scan[0]:F1} / scan[last]={scan[Size - 1]:F1}");
    }

    /// <summary>反向保护：云本身仍然要有层次，不能为了「连续」把噪声抹成一片死平。</summary>
    [Fact]
    public void TheNebulaStillHasStructure()
    {
        double[] scan = AzimuthScan();

        double min = scan.Min();
        double max = scan.Max();

        Assert.True(
            max - min > 8.0,
            $"一整圈方位角上云絮仍应有明暗层次（实测 {min:F1} ~ {max:F1}）");
    }
}
