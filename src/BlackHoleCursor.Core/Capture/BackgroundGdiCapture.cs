using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace BlackHoleCursor.Core.Capture;

/// <summary>
/// fixes-v2 问题 7：把 GDI 抓屏搬到**独立线程**，让渲染循环不再等它。
///
/// 为什么必须搬走：GDI 的 <c>CopyFromScreen</c> 每次要付一次 DWM 合成同步等待 ——
/// 实测约 17ms，且**与面积无关、与是否复用 DC 无关**（3 种调用方式 × 2 种面积都是这个数）。
/// 放在渲染主循环里，帧时间就随它在 5~24ms 之间波动，
/// 「读鼠标 → 显示出来」的滞后量忽长忽短，表现为移动时的抖动。
///
/// 搬走之后：后台线程持续抓（约 57Hz），主线程每帧只做一次
/// <see cref="TryUpload"/>（一次 UpdateSubresource，约 0.3ms），帧时间重新变稳定。
///
/// 参考实现 singularity 也是这么做的（独立线程 + 零拷贝 + 渲染侧从不等待），
/// 我们这里用 GDI + 双缓冲达到同样的「渲染不等待」效果。
///
/// ⚠️ **崩溃教训（务必记住）**：抓取区域必须**跟着那一帧一起快照**。
/// 最初版本让 <see cref="TryUpload"/> 直接读「当前」的区域字段去拼 Box，
/// 而 <c>_ready</c> 里的像素是后台线程用**当时**的区域抓的 ——
/// 两者不一致时就会按新尺寸去读旧缓冲区。<c>_ready</c> 是 <see cref="Marshal.AllocHGlobal"/>
/// 的非托管内存，越界读直接 <c>AccessViolationException</c>（进程级崩溃，catch 不到）。
/// Box 超出目标纹理边界时还会顺带搞掉 D3D 设备。
/// </summary>
public sealed class BackgroundGdiCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Thread _thread;

    private volatile bool _stopRequested;

    /// <summary>
    /// 一次抓屏的区域。做成**不可变小对象**并整体换引用，
    /// 是为了避免「逐字段写多个 volatile int」造成的撕裂（新 width 配旧 height 之类）。
    /// </summary>
    private sealed class Region
    {
        public int Left;
        public int Top;
        public int Width = 1;
        public int Height = 1;
        public int DestX;
        public int DestY;
    }

    /// <summary>主线程请求的区域（受 <see cref="_gate"/> 保护）。</summary>
    private Region _requested = new();

    /// <summary>与 <c>_ready</c> 缓冲区**严格对应**的那一份区域 —— 上传时必须用它。</summary>
    private Region _readyRegion = new();

    private int _readyStride;
    private volatile bool _regionChanged = true;

    /// <summary>临时诊断：上次上传的区域签名，避免每帧刷日志。</summary>
    private string? _lastUploadSignature;

    // 后台线程独占
    private Bitmap? _bitmap;
    private Graphics? _graphics;

    // 双缓冲：后台写 _writing，写满后与 _ready 交换；主线程只从 _ready 上传。
    // 交换与上传都在锁内，所以主线程绝不会读到正在被写的那块。
    private IntPtr _ready = IntPtr.Zero;
    private IntPtr _writing = IntPtr.Zero;
    private int _bytesPerFrame;

    private long _version;
    private long _uploadedVersion;

    public BackgroundGdiCapture()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "bhc-capture",
        };
    }

    /// <summary>最近一次 GDI 抓屏的耗时（ms）。后台线程写，主线程只读来记日志。</summary>
    public double LastCostMs { get; private set; }

    /// <summary>已完成的抓屏次数（测试用）。</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Start() => _thread.Start();

    /// <summary>更新抓取区域（主线程调用，非阻塞）。</summary>
    public void SetRegion(int left, int top, int width, int height, int destX, int destY)
    {
        int w = Math.Max(width, 1);
        int h = Math.Max(height, 1);

        lock (_gate)
        {
            if (w == _requested.Width && h == _requested.Height
                && left == _requested.Left && top == _requested.Top
                && destX == _requested.DestX && destY == _requested.DestY)
            {
                return;
            }

            _requested = new Region
            {
                Left = left,
                Top = top,
                Width = w,
                Height = h,
                DestX = destX,
                DestY = destY,
            };

            _regionChanged = true;
        }
    }

    /// <summary>
    /// 把最新一帧上传到目标纹理（主线程调用）。
    /// 没有新帧时直接返回 false —— 复用上一张纹理，不阻塞。
    /// </summary>
    public bool TryUpload(ID3D11DeviceContext context, ID3D11Texture2D destination)
    {
        lock (_gate)
        {
            if (_ready == IntPtr.Zero || _readyStride <= 0)
            {
                return false;
            }

            long v = Interlocked.Read(ref _version);
            if (v == Interlocked.Read(ref _uploadedVersion))
            {
                return false;
            }

            // —— 关键：Box 与源缓冲区必须来自**同一份**区域快照 ——
            var region = _readyRegion;
            var description = destination.Description;
            int textureWidth = (int)description.Width;
            int textureHeight = (int)description.Height;

            // 兜底：区域与纹理尺寸对不上（例如刚换了显示器、纹理还没重建）时宁可跳过，
            // 也不能硬写 —— 越界会直接崩进程或搞掉设备。
            if (region.DestX < 0 || region.DestY < 0
                || region.DestX + region.Width > textureWidth
                || region.DestY + region.Height > textureHeight)
            {
                return false;
            }

            bool partial = region.DestX != 0
                || region.DestY != 0
                || region.Width != textureWidth
                || region.Height != textureHeight;

            Box? box = partial
                ? new Box(region.DestX, region.DestY, 0,
                          region.DestX + region.Width, region.DestY + region.Height, 1)
                : null;

            // ---- 临时诊断（定位 TryUpload 的 AV 用，定位后删除）----
            string signature = $"{region.Width}x{region.Height}@{region.DestX},{region.DestY}"
                + $" stride={_readyStride} bytes={_bytesPerFrame} tex={textureWidth}x{textureHeight}";
            if (signature != _lastUploadSignature)
            {
                _lastUploadSignature = signature;
                RuntimeLog.Info($"UPLOAD {signature}");
            }

            if (_readyStride * region.Height > _bytesPerFrame)
            {
                RuntimeLog.Info($"UPLOAD-SKIP 缓冲区不足：需要 {_readyStride * region.Height}，实际 {_bytesPerFrame}");
                return false;
            }

            context.UpdateSubresource(destination, 0, box, _ready, (uint)_readyStride, 0);
            Interlocked.Exchange(ref _uploadedVersion, v);
            return true;
        }
    }

    private void Loop()
    {
        while (!_stopRequested)
        {
            try
            {
                CaptureOnce();
            }
            catch (Exception ex)
            {
                // 抓屏失败不能让线程死掉：等一下继续试（分辨率切换、锁屏都可能触发）
                RuntimeLog.Error("后台抓屏异常，稍后重试", ex);
                Thread.Sleep(50);
            }
        }
    }

    private void CaptureOnce()
    {
        // 整份区域一次性取出，避免读到撕裂的组合
        Region region;
        lock (_gate)
        {
            region = _requested;
        }

        int w = region.Width;
        int h = region.Height;

        if (_regionChanged || _bitmap is null || _bitmap.Width != w || _bitmap.Height != h)
        {
            Rebuild(w, h);
            _regionChanged = false;
        }

        var watch = Stopwatch.StartNew();

        // 必须写全 System.Drawing.Size：using 里同时有 Vortice.Mathematics，它也有个 Size
        _graphics!.CopyFromScreen(
            region.Left,
            region.Top,
            0,
            0,
            new System.Drawing.Size(w, h),
            CopyPixelOperation.SourceCopy);

        var data = _bitmap!.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        int stride;
        try
        {
            stride = Math.Abs(data.Stride);
            int bytes = Math.Min(stride * h, _bytesPerFrame);

            unsafe
            {
                Buffer.MemoryCopy((void*)data.Scan0, (void*)_writing, _bytesPerFrame, bytes);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        watch.Stop();
        LastCostMs = watch.Elapsed.TotalMilliseconds;

        lock (_gate)
        {
            (_ready, _writing) = (_writing, _ready);

            // 区域与 stride 必须跟着这一帧一起换，否则上传时会拿错尺寸去读缓冲区
            _readyRegion = region;
            _readyStride = stride;

            Interlocked.Increment(ref _version);
        }
    }

    private void Rebuild(int w, int h)
    {
        _graphics?.Dispose();
        _bitmap?.Dispose();

        _bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);

        // GDI 的行按 4 字节对齐（32bpp 下自然满足，这里写清楚以防将来改格式）
        int stride = ((w * 32 + 31) / 32) * 4;
        int bytes = stride * h;

        // ⚠️ 缓冲区的释放/重分配**必须持锁**：
        // TryUpload 是持锁调用 UpdateSubresource(_ready) 的，
        // 若这里不锁，后台线程会在主线程还在上传时把那块非托管内存 free 掉
        // → use-after-free → AccessViolationException（进程直接死，catch 不到）。
        // 区域尺寸一变就会走到这里，所以启动后很快就撞上。
        lock (_gate)
        {
            if (bytes != _bytesPerFrame)
            {
                if (_ready != IntPtr.Zero) { Marshal.FreeHGlobal(_ready); }
                if (_writing != IntPtr.Zero) { Marshal.FreeHGlobal(_writing); }

                _ready = Marshal.AllocHGlobal(bytes);
                _writing = Marshal.AllocHGlobal(bytes);
                _bytesPerFrame = bytes;
            }
        }
    }

    public void Dispose()
    {
        _stopRequested = true;

        if (_thread.IsAlive)
        {
            _thread.Join(3000);
        }

        _graphics?.Dispose();
        _bitmap?.Dispose();
        _graphics = null;
        _bitmap = null;

        // 与后台线程同一把锁：Join 超时时也不会出现「边释放边写」
        lock (_gate)
        {
            if (_ready != IntPtr.Zero) { Marshal.FreeHGlobal(_ready); _ready = IntPtr.Zero; }
            if (_writing != IntPtr.Zero) { Marshal.FreeHGlobal(_writing); _writing = IntPtr.Zero; }
            _readyStride = 0;
        }

        GC.SuppressFinalize(this);
    }
}
