using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace BlackHoleCursor.Core.Capture;

/// <summary>
/// 备用抓屏方案：GDI BitBlt（Graphics.CopyFromScreen）。
/// 速度不如 Desktop Duplication，但在远程桌面 / 老显卡 / 多 GPU 等场景下更通用。
/// </summary>
public sealed class GdiDesktopCapture : IDesktopCapture
{
    // fixes-v2 问题 5：整屏抓屏每帧都要一块 1920×1080×4 ≈ 8MB 的 Bitmap。
    // 原来每帧 new 一块，光分配 + GC 就吃掉大量时间（实测整屏抓屏 46ms/帧）。
    // 尺寸不变时复用同一块，只重新 BitBlt 内容。
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private int _width;
    private int _height;

    public string Name => "GDI BitBlt";

    public bool IsAvailable { get; private set; } = true;

    public void Recreate() => IsAvailable = true;

    public void SelectOutput(int centerX, int centerY)
    {
        // GDI 走虚拟桌面坐标，无需选择输出
    }

    public CaptureStatus TryCapture(
        ID3D11DeviceContext context,
        ID3D11Texture2D destination,
        int left,
        int top,
        int width,
        int height,
        int destX = 0,
        int destY = 0)
    {
        try
        {
            if (_bitmap is null || _graphics is null || _width != width || _height != height)
            {
                _graphics?.Dispose();
                _bitmap?.Dispose();
                _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                _graphics = Graphics.FromImage(_bitmap);
                _width = width;
                _height = height;
            }

            var bitmap = _bitmap;
            _graphics!.CopyFromScreen(
                left,
                top,
                0,
                0,
                new System.Drawing.Size(width, height),
                CopyPixelOperation.SourceCopy);

            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                // 局部增量更新：只覆盖纹理里的那一小块（fixes-v2 问题 5）。
                // 传 Box 而不是 null，D3D11 就只写 [destX, destY, +width, +height]，
                // 纹理其余部分保留上一次的内容（桌面基本静止，本就不必重抓）。
                var description = destination.Description;
                bool partial = destX != 0 || destY != 0
                    || width != description.Width || height != description.Height;

                Box? box = partial
                    ? new Box(destX, destY, 0, destX + width, destY + height, 1)
                    : null;

                context.UpdateSubresource(
                    destination,
                    0,
                    box,
                    data.Scan0,
                    (uint)Math.Abs(data.Stride),
                    0);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return CaptureStatus.Success;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return CaptureStatus.Unavailable;
        }
        catch (ExternalException)
        {
            return CaptureStatus.Unavailable;
        }
    }

    public void ReleaseFrame()
    {
        // GDI 抓屏不持有帧，无需释放
    }

    public void Dispose()
    {
        _graphics?.Dispose();
        _bitmap?.Dispose();
        _graphics = null;
        _bitmap = null;
        GC.SuppressFinalize(this);
    }
}
