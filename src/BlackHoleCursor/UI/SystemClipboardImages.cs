using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlackHoleCursor.Core;
using BlackHoleCursor.Core.Screenshots;

namespace BlackHoleCursor.UI;

/// <summary>
/// 真实剪贴板（WPF <c>Clipboard</c>）的 <see cref="IClipboardImages"/> 实现。
/// Core 不引用 WPF，所以这层适配放在 UI 项目里；单测用内存替身，不碰系统剪贴板。
/// </summary>
public sealed class SystemClipboardImages : IClipboardImages
{
    public bool TryGetBitmap(out ClipboardBitmap bitmap)
    {
        bitmap = default;

        try
        {
            // 剪贴板访问必须在 STA 线程；WPF 的 UI 线程天然满足
            if (!System.Windows.Clipboard.ContainsImage())
            {
                return false;
            }

            var source = System.Windows.Clipboard.GetImage();
            if (source is null)
            {
                return false;
            }

            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            bitmap = new ClipboardBitmap(width, height, pixels);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被别的程序占用是常态，安静跳过即可
            return false;
        }
    }

    public void SetBitmap(in ClipboardBitmap bitmap)
    {
        try
        {
            int stride = bitmap.Width * 4;
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bitmap.Pixels,
                stride);

            System.Windows.Clipboard.SetImage(source);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 写不回去就算了，不能因为补偿失败影响用户原本的截图
        }
    }

    /// <summary>
    /// 剪贴板序列号。写回会推进它、并再触发一次 WM_CLIPBOARDUPDATE，
    /// 靠它识别「这是自己刚写进去的」，否则会无限自我补偿。
    /// </summary>
    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();
}
