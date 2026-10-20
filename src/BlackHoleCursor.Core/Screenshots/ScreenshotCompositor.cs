using BlackHoleCursor.Core.Rendering;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BlackHoleCursor.Core.Screenshots;

/// <summary>
/// M4.2（prd-v2 F8-1）：把黑洞重新画回一张已经抓好的全屏位图。
///
/// 因为 F5-3 把覆盖层排除出了捕获（`WDA_EXCLUDEFROMCAPTURE`），
/// 用户按 PrtScn 得到的图里**没有黑洞**。这里用**同一套渲染器**把它补回去。
///
/// 关键做法：把位图上黑洞所在的那块区域当作背景纹理喂给渲染器，
/// 于是透镜折射的是**真实截图内容**，而不是合成棋盘 —— 补出来的效果与桌面上看到的一致。
/// </summary>
public sealed class ScreenshotCompositor : IDisposable
{
    /// <summary>
    /// 合成区域的边长上限。
    ///
    /// v3（P3）从 1024 提到 4096：1024 是「小窗口」时代的量级，
    /// 全屏后补丁要覆盖到视口半高（1080p 屏就是 1080），1024 反而会把远场切掉一截。
    /// 放宽不会拖慢 —— 实测整屏 1920×1080 渲染只占 0.01ms，瓶颈从来不在渲染。
    /// </summary>
    public const int MaxPatchExtent = 4096;

    private readonly RenderDevice _device;
    private readonly SceneRenderer _sceneRenderer;
    private readonly PreviewRenderer _preview;
    private bool _disposed;

    public ScreenshotCompositor(bool preferHardware = true)
    {
        _device = RenderDevice.Create(preferHardware);
        _sceneRenderer = new SceneRenderer(_device.Device);
        _preview = new PreviewRenderer(_device, _sceneRenderer, 64, 64);
    }

    public bool IsUsingSoftwareRendering => _device.UsesWarp;

    /// <summary>
    /// 就地把黑洞合成到 <paramref name="pixels"/>（BGRA，行优先，尺寸 width×height）。
    /// </summary>
    /// <param name="centerX">黑洞中心在图中的横坐标（虚拟桌面坐标下的像素）。</param>
    /// <param name="centerY">黑洞中心在图中的纵坐标。</param>
    /// <param name="screenHeight">屏高（像素），用于把 HoleSize 换算成像素半径。</param>
    /// <param name="timeSeconds">动画时间，传覆盖层当前的时间即可让盘纹与桌面一致。</param>
    /// <returns>是否成功合成。</returns>
    public bool Composite(
        byte[] pixels,
        int width,
        int height,
        int centerX,
        int centerY,
        SettingsModel settings,
        int screenHeight,
        double timeSeconds = 0.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pixels is null || width <= 0 || height <= 0 || pixels.Length < (long)width * height * 4)
        {
            return false;
        }

        if (screenHeight <= 0)
        {
            return false;
        }

        var look = LookPresets.FromSettings(settings);
        int side = PatchExtent(look, screenHeight, width, height);

        // 以黑洞为中心裁一块，再按图像边界裁剪（贴边时不会越界）
        int left = Math.Clamp(centerX - side / 2, 0, Math.Max(width - 8, 0));
        int top = Math.Clamp(centerY - side / 2, 0, Math.Max(height - 8, 0));
        int patchW = Math.Clamp(Math.Min(side, width - left), 8, MaxPatchExtent);
        int patchH = Math.Clamp(Math.Min(side, height - top), 8, MaxPatchExtent);

        byte[] patch = Crop(pixels, width, height, left, top, patchW, patchH);

        // 补丁尺寸每次都可能不同（洞径不同、贴边被裁），必须先 Resize：
        // PreviewRenderer 的渲染目标是固定尺寸的，不 Resize 的话它仍按建时的大小输出，
        // 而下面按 patchW×patchH 去读，会直接越界。
        _preview.Resize(patchW, patchH);

        using var background = CreateTexture(_device.Device, patchW, patchH, patch);
        using var backgroundView = _device.Device.CreateShaderResourceView(background, null);
        _preview.SetBackground(backgroundView);

        var description = new SceneDescription(
            patchW,
            patchH,
            centerX - left,
            centerY - top,
            settings,
            timeSeconds,
            settings.LensEnabled,
            Look: look,
            ScreenHeight: screenHeight);

        byte[] rendered = _preview.Render(description);

        // 渲染输出是预乘 alpha：直接叠在位图上（out = 已乘过 alpha 的 rgb + 底图 × (1 − alpha)）
        BlendPremultiplied(pixels, width, height, left, top, patchW, patchH, rendered);
        return true;
    }

    /// <summary>
    /// 合成区域的边长。
    ///
    /// v3（P3）：窗口改成全屏后，透镜的远场能一直显示到**视口半高** ——
    /// `composite.hlsl` 的 `fadeR = 0.5 / Hole.x`（单位是阴影半径），
    /// 换算成像素正好是 `0.5 × 屏高`。补丁必须覆盖同样的范围，
    /// 否则补回截图的黑洞会比桌面上**少一圈远场透镜**。
    ///
    /// 旧公式只用 `ComputeV2WindowRadius`（约 6 个阴影半径 ≈ 389px），
    /// 那是「小窗口」时代的口径：那时窗口本身就只有 3.2 rs，远场本来就被裁掉了。
    /// 全屏之后那部分远场在桌面上是看得见的（P1.4 的意外收获），不补就会对不上。
    /// 这里仍保留它作为**下限**，避免大盘参数下补丁装不下盘。
    /// </summary>
    public static int PatchExtent(BlackHoleLook look, int screenHeight, int imageWidth, int imageHeight)
    {
        double shadowRadiusPx = look.HoleSize * screenHeight;
        double diskOuterPx = OverlayGeometry.DiskOuterRadiusPixels(look.OuterRadius, shadowRadiusPx);

        double radius = Math.Max(
            0.5 * screenHeight,                                                  // 全屏远场范围
            OverlayGeometry.ComputeV2WindowRadius(shadowRadiusPx, diskOuterPx));   // 盘要装得下

        int extent = (int)Math.Ceiling(radius * 2.0);
        int limit = Math.Min(MaxPatchExtent, Math.Min(imageWidth, imageHeight));
        return Math.Clamp(extent, 8, Math.Max(limit, 8));
    }

    private static byte[] Crop(byte[] pixels, int width, int height, int left, int top, int patchW, int patchH)
    {
        var patch = new byte[patchW * patchH * 4];

        for (int y = 0; y < patchH; y++)
        {
            int src = ((top + y) * width + left) * 4;
            Buffer.BlockCopy(pixels, src, patch, y * patchW * 4, patchW * 4);
        }

        return patch;
    }

    private static void BlendPremultiplied(
        byte[] target,
        int width,
        int height,
        int left,
        int top,
        int patchW,
        int patchH,
        byte[] layer)
    {
        for (int y = 0; y < patchH; y++)
        {
            int rowStart = ((top + y) * width + left) * 4;
            int layerStart = y * patchW * 4;

            for (int x = 0; x < patchW; x++)
            {
                int t = rowStart + x * 4;
                int l = layerStart + x * 4;

                byte a = layer[l + 3];
                if (a == 0)
                {
                    continue;
                }

                target[t] = Saturate(layer[l] + ScaleByInverseAlpha(target[t], a));
                target[t + 1] = Saturate(layer[l + 1] + ScaleByInverseAlpha(target[t + 1], a));
                target[t + 2] = Saturate(layer[l + 2] + ScaleByInverseAlpha(target[t + 2], a));
                target[t + 3] = 0xFF;                 // 全屏截图是不透明的
            }
        }
    }

    private static byte ScaleByInverseAlpha(byte value, byte alpha)
        => (byte)(value * (255 - alpha) / 255);

    private static byte Saturate(int value) => (byte)Math.Min(value, 255);

    private static ID3D11Texture2D CreateTexture(ID3D11Device device, int width, int height, byte[] bgra)
    {
        // 位图里 alpha 恒为 0xFF；着色器只读 rgb，alpha 不影响结果
        return device.CreateTexture2D(
            bgra,
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            1,
            1,
            BindFlags.ShaderResource,
            ResourceOptionFlags.None,
            ResourceUsage.Immutable,
            CpuAccessFlags.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _preview.Dispose();
        _sceneRenderer.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
