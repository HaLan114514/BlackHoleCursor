using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BlackHoleCursor;

/// <summary>
/// 程序化生成托盘图标（黑洞 + 吸积盘），无需外部资源文件。
/// ICO 容器使用 PNG 压缩格式（Vista+ 支持），避免 GDI 句柄泄漏。
/// </summary>
public static class IconFactory
{
    public static Icon CreateBlackHoleIcon(int size = 64, bool enabled = true)
        => PngToIcon(RenderIconPng(size, enabled), size);

    /// <summary>
    /// 把黑洞画到指定的画布上 —— v4：抽出来是为了让 `tools/IconGen` 能用同一份绘制代码
    /// 生成 .ico，避免「运行时的托盘图标」和「文件里的程序图标」长得不一样。
    /// </summary>
    /// <param name="size">画布边长（正方形）。</param>
    /// <param name="enabled">启用为彩色、禁用为灰暗。</param>
    public static void DrawBlackHoleIcon(Graphics g, int size, bool enabled)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float cx = size / 2f;
        float cy = size / 2f;
        float outer = size * 0.46f;
        float inner = size * 0.24f;
        float horizon = size * 0.17f;

        Color ringOuter = enabled ? Color.FromArgb(255, 255, 106, 26) : Color.FromArgb(140, 110, 116, 128);
        Color ringInner = enabled ? Color.FromArgb(255, 255, 216, 150) : Color.FromArgb(140, 176, 182, 194);

        // 风险 R4：16 / 32 下光晕与内环会糊成一团，只留「黑核 + 一圈亮环」
        bool simplified = size <= 32;

        if (!simplified)
        {
            // 外层光晕
            using var halo = new GraphicsPath();
            halo.AddEllipse(cx - outer, cy - outer, outer * 2, outer * 2);
            using var haloBrush = new PathGradientBrush(halo)
            {
                CenterColor = Color.FromArgb(enabled ? 90 : 40, ringOuter),
                SurroundColors = new[] { Color.Transparent },
            };
            g.FillEllipse(haloBrush, cx - outer, cy - outer, outer * 2, outer * 2);
        }

        // 吸积盘（倾斜椭圆）—— 简化版加粗一点，小尺寸才看得出是环
        float tilt = size * 0.30f;
        using var diskPenOuter = new Pen(
            Color.FromArgb(simplified ? 255 : 210, ringOuter),
            Math.Max(size * 0.10f, 2f));
        g.DrawEllipse(diskPenOuter, cx - outer * 0.86f, cy - tilt, outer * 1.72f, tilt * 2);

        if (!simplified)
        {
            using var diskPenInner = new Pen(Color.FromArgb(230, ringInner), Math.Max(size * 0.05f, 1f));
            g.DrawEllipse(diskPenInner, cx - inner * 0.9f, cy - tilt * 0.72f, inner * 1.8f, tilt * 1.44f);
        }

        // 事件视界
        using var coreBrush = new SolidBrush(enabled ? Color.Black : Color.FromArgb(255, 32, 34, 40));
        g.FillEllipse(coreBrush, cx - horizon, cy - horizon, horizon * 2, horizon * 2);

        using var rimPen = new Pen(Color.FromArgb(160, 150, 190, 255), Math.Max(size * 0.035f, 1f));
        g.DrawEllipse(rimPen, cx - horizon, cy - horizon, horizon * 2, horizon * 2);
    }

    /// <summary>渲染成 PNG 字节（生成多图 .ico 时用）。</summary>
    public static byte[] RenderIconPng(int size, bool enabled)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            DrawBlackHoleIcon(g, size, enabled);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// <summary>
    /// 把多张 PNG 打包成一个多图 ICO（v4：程序图标需要 256 / 48 / 32 / 16，
    /// 单图 ICO 在高 DPI 下会被拉伸得很难看）。
    /// </summary>
    public static Icon PngsToIcon(params (int Size, byte[] Png)[] images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // type: icon
        writer.Write((ushort)images.Length);     // image count

        int offset = 6 + (16 * images.Length);
        foreach ((int size, byte[] png) in images)
        {
            byte dimension = size >= 256 ? (byte)0 : (byte)size;
            writer.Write(dimension);             // width
            writer.Write(dimension);             // height
            writer.Write((byte)0);               // palette
            writer.Write((byte)0);               // reserved
            writer.Write((ushort)1);             // color planes
            writer.Write((ushort)32);            // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write(offset);

            offset += png.Length;
        }

        foreach ((int _, byte[] png) in images)
        {
            writer.Write(png);
        }

        writer.Flush();
        stream.Position = 0;
        return new Icon(stream);
    }

    /// <summary>
    /// v4（V4-7）：从资源里加载程序图标 —— 启用 / 禁用两张，由 `tools/IconGen` 生成。
    /// 托盘不再每次启动时现画，也就不会和 exe 文件里的图标长得不一样。
    /// </summary>
    public static Icon LoadAppIcon(bool enabled)
    {
        string name = enabled ? "app.ico" : "app-off.ico";
        var info = System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/Resources/{name}", UriKind.Absolute));

        if (info is null)
        {
            throw new FileNotFoundException($"程序图标资源缺失：Resources/{name}");
        }

        // ⚠️ 不能 using：Icon 构造后仍然持有这个流，提前释放会让它读不到数据
        return new Icon(info.Stream);
    }

    /// <summary>把 PNG 字节包装成单图 ICO。</summary>
    public static Icon PngToIcon(byte[] pngBytes, int size)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);          // reserved
        writer.Write((ushort)1);          // type: icon
        writer.Write((ushort)1);          // image count

        byte dimension = size >= 256 ? (byte)0 : (byte)size;
        writer.Write(dimension);          // width
        writer.Write(dimension);          // height
        writer.Write((byte)0);            // palette
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // color planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write((uint)pngBytes.Length);
        writer.Write(6 + 16);             // offset of image data
        writer.Write(pngBytes);
        writer.Flush();

        stream.Position = 0;
        return new Icon(stream);
    }
}
