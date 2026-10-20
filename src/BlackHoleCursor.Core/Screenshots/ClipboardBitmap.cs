namespace BlackHoleCursor.Core.Screenshots;

/// <summary>
/// 剪贴板里的一张位图：BGRA、行优先、左上原点。
/// 抽成结构而不是直接用 WPF 的 <c>BitmapSource</c>，是为了让 Core 不依赖 WPF，
/// 从而能在单测里用假剪贴板跑通整条链路（prd-v2 F8）。
/// </summary>
public readonly record struct ClipboardBitmap(int Width, int Height, byte[] Pixels)
{
    public bool IsEmpty => Pixels is null || Width <= 0 || Height <= 0;

    /// <summary>像素数组长度是否配得上声明的尺寸（防止读到半截数据）。</summary>
    public bool HasCompletePixels => !IsEmpty && Pixels.Length >= (long)Width * Height * 4;
}

/// <summary>
/// 剪贴板位图的读写抽象。真实实现（WPF <c>Clipboard</c>）在 UI 层；
/// 单测用内存替身，不碰系统剪贴板，避免污染用户正在复制的内容。
/// </summary>
public interface IClipboardImages
{
    /// <summary>读取当前剪贴板里的位图；没有位图时返回 false（例如复制的是文本）。</summary>
    bool TryGetBitmap(out ClipboardBitmap bitmap);

    /// <summary>把位图写回剪贴板。</summary>
    void SetBitmap(in ClipboardBitmap bitmap);

    /// <summary>
    /// 剪贴板序列号（Windows 的 <c>GetClipboardSequenceNumber</c>）。
    /// 写回剪贴板会**再触发一次** WM_CLIPBOARDUPDATE，靠它识别「这是自己刚写进去的」，
    /// 否则会无限自我补偿。
    /// </summary>
    uint SequenceNumber { get; }
}
