using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace BlackHoleCursor.Core.Capture;

/// <summary>
/// 从 WinRT 的抓屏帧里取出 D3D11 纹理用的 ABI 接口。
///
/// WGC 给的 <c>IDirect3DSurface</c> **不直接**实现 <c>ID3D11Texture2D</c>，
/// 必须经这个接口的 <c>GetInterface(IID_ID3D11Texture2D)</c> 拿。
/// 这个接口只在 ABI 层存在（不在 WinRT 元数据里），所以得手工 ComImport 声明。
/// </summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(ref Guid iid);
}

/// <summary>
/// <c>Windows.Graphics.Capture.GraphicsCaptureItem</c> 的工厂互操作接口。
///
/// 用它按 **HMONITOR** 创建 item（<c>CreateFromDisplayId</c> 需要 DisplayId，
/// 而我们从 Win32 拿到的是监视器句柄）。同样只在 ABI 层，需手工声明。
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    /// <summary>
    /// ⚠️ **vtable 顺序不能对调**：SDK 的 `IGraphicsCaptureItemInterop` 是
    /// `CreateForWindow` 在前、`CreateForMonitor` 在后。
    /// 一旦写反，就会把 HMONITOR 当成 HWND 传给 CreateForWindow，
    /// 于是拿到 `0x80070057 E_INVALIDARG`（"Value does not fall within the expected range"）——
    /// 这个错误信息完全指不到真正的原因，我在这上面卡了两轮。
    /// </summary>
    [PreserveSig]
    int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);

    /// <summary>用 <see cref="PreserveSig"/> 拿原始 HRESULT，失败时才有返回码可看。</summary>
    [PreserveSig]
    int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
}

/// <summary>
/// plan-wgc W2：用 **Windows.Graphics.Capture（WGC）** 抓屏。
///
/// 为什么换它（<c>fixes-v3</c> 问题 4）：GDI 每次抓屏都要付一次
/// **DWM 合成同步等待**（实测 ~17ms），与面积无关、与是否复用 DC 无关；
/// 搬后台线程只解决了「阻塞主循环」，并没有缩短延迟 —— 折射区画出来的仍是旧桌面，
/// 与旁边实时的桌面并排就是「慢半拍」，滚轮时表现为割裂。
///
/// WGC 是**回调驱动 + GPU 零拷贝**，内容年龄预期从「约 2 帧」降到「约 1 帧」。
///
/// ⚠️ **硬性要求（W1 定的）**：所有创建动作必须包在 try/catch 里。
/// 「最低系统要求抬高」的准确含义是「WGC 这条路径能否用」，不是「老系统拒绝启动」——
/// 老系统（&lt; build 19041）上激活失败必须表现为 **IsAvailable = false**，
/// 由 <see cref="CaptureCoordinator"/> 降级到 GDI，而不是让程序崩。
/// </summary>
public sealed class WgcDesktopCapture : IDesktopCapture
{
    /// <summary>帧池缓冲数。参考实现也是小缓冲（拿到最新那帧即可，不要积压）。</summary>
    private const int FramePoolBufferCount = 2;

    /// <summary>
    /// <c>ID3D11Texture2D</c> 的 IID。写死而不用 <c>typeof(...).GUID</c> ——
    /// 后者在接口没带 Guid 特性时会给出 Guid.Empty，故障现象完全没有指向性。
    /// </summary>
    /// <summary>
    /// ⚠️ **这里曾经写错，是本次卡住的直接原因**：前一个值
    /// `6F15AAF2-A208-4E52-9AC1-576A9FE2A29C` 只有第一段是对的，
    /// 于是 QI 一律返回 `E_NOINTERFACE`，而错误信息完全指不到「GUID 写错」上。
    /// 正确值来自 d3d11.h 的 `IID_ID3D11Texture2D`（0x6f15aaf2, 0xd208, 0x4e89, ...）。
    /// </summary>
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary><c>IDirect3DDxgiInterfaceAccess</c> 的 IID（与上面接口声明上的 Guid 必须一致）。</summary>
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly ID3D11Device _device;
    private IntPtr _monitor;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _frame;

    private bool _disposed;

    public WgcDesktopCapture(ID3D11Device device)
    {
        _device = device;
    }

    public string Name => "WGC";

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// 选中包含指定坐标的显示器。
    /// 与 GDI 实现不同：WGC 必须显式指定捕获哪一个显示器。
    /// </summary>
    public void SelectOutput(int centerX, int centerY)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = centerX, Y = centerY },
            NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (monitor == _monitor && _pool is not null)
        {
            return;
        }

        _monitor = monitor;
        Recreate();
    }

    /// <summary>重建 item / 帧池 / 会话（分辨率变化、访问丢失、换显示器时）。</summary>
    public void Recreate()
    {
        Teardown();

        if (_disposed || _monitor == IntPtr.Zero)
        {
            IsAvailable = false;
            return;
        }

        RuntimeLog.Info(
            $"WGC 起点：套间={Thread.CurrentThread.GetApartmentState()}，monitor=0x{_monitor:X}");

        try
        {
            bool supported;
            try
            {
                supported = GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex)
            {
                // 单独捕获：看清楚到底是不是 IsSupported 这一句在抛
                RuntimeLog.Info($"WGC 步骤0 抛异常（{ex.GetType().Name}）：{ex.Message}");
                throw;
            }

            if (!supported)
            {
                RuntimeLog.Info("WGC 步骤0：GraphicsCaptureSession.IsSupported() = false");
                IsAvailable = false;
                return;
            }

            RuntimeLog.Info("WGC 步骤0：IsSupported() = true");

            _item = CreateItemForMonitor(_monitor);
            if (_item is null)
            {
                RuntimeLog.Info("WGC 步骤1失败：拿不到 GraphicsCaptureItem");
                IsAvailable = false;
                return;
            }

            var size = _item.Size;
            RuntimeLog.Info($"WGC 步骤1：item OK，size={size.Width}x{size.Height}");

            IDirect3DDevice winrtDevice = CreateDirect3DDevice(_device);
            RuntimeLog.Info("WGC 步骤2：IDirect3DDevice OK");

            _pool = Direct3D11CaptureFramePool.Create(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                size);
            RuntimeLog.Info("WGC 步骤3：frame pool OK");

            _session = _pool.CreateCaptureSession(_item);

            // 我们本来就在隐藏系统光标，画面里不该再出现一个
            _session.IsCursorCaptureEnabled = false;

            _session.StartCapture();
            RuntimeLog.Info("WGC 步骤4：session started");

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // 老系统 / 无桌面会话 / 受保护内容：一律表现为「不可用」，交给上层降级
            RuntimeLog.Info($"WGC 初始化失败（{ex.GetType().Name}）：{ex.Message}");
            Teardown();
            IsAvailable = false;
        }
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
        if (_disposed || !IsAvailable || _pool is null)
        {
            return CaptureStatus.Unavailable;
        }

        try
        {
            Direct3D11CaptureFrame? frame = DrainToNewest(_pool);
            if (frame is null)
            {
                // 桌面没有新内容 ⇒ 复用上一张纹理（与 DXGI 的 Timeout 同语义）
                return CaptureStatus.Timeout;
            }

            ReleaseFrame();
            _frame = frame;

            ID3D11Texture2D source = GetTextureFromFrame(frame);

            // 抓屏纹理是整块显示器、锚在显示器坐标上（plan-v3 P2 之后区域恒定）。
            // 帧里的坐标同样是显示器坐标 ⇒ 源矩形直接用 left/top 减去显示器原点。
            var monitorInfo = MonitorRect(_monitor);
            int srcLeft = left - monitorInfo.Left;
            int srcTop = top - monitorInfo.Top;

            context.CopySubresourceRegion(
                destination,
                0,
                (uint)destX,
                (uint)destY,
                0,
                source,
                0,
                new Box(srcLeft, srcTop, 0, srcLeft + width, srcTop + height, 1));

            return CaptureStatus.Success;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info($"WGC 抓帧失败（{ex.GetType().Name}）：{ex.Message}");
            return CaptureStatus.AccessLost;
        }
    }

    public void ReleaseFrame()
    {
        _frame?.Dispose();
        _frame = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();
        GC.SuppressFinalize(this);
    }

    private void Teardown()
    {
        try
        {
            ReleaseFrame();
            _session?.Dispose();
            _pool?.Dispose();
        }
        catch
        {
            // 销毁阶段的异常不能往外抛
        }
        finally
        {
            _session = null;
            _pool = null;
            _item = null;
        }
    }

    /// <summary>按 HMONITOR 创建捕获 item（经工厂互操作接口）。</summary>
    private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr monitor)
    {
        // 1) 取 GraphicsCaptureItem 的激活工厂
        IntPtr classId = CreateHString("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            Guid factoryIid = new("00000035-0000-0000-C000-000000000046");   // IActivationFactory
            int hr = RoGetActivationFactory(classId, ref factoryIid, out IntPtr factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                RuntimeLog.Info($"WGC：取激活工厂失败 HRESULT=0x{hr:X8}");
                return null;
            }

            try
            {
                object factory = Marshal.GetObjectForIUnknown(factoryPtr);
                var interop = factory as IGraphicsCaptureItemInterop;
                if (interop is null)
                {
                    RuntimeLog.Info("WGC：工厂不支持 IGraphicsCaptureItemInterop");
                    return null;
                }

                // 2) 用它按监视器句柄创建 item。
                //
                //    ⚠️ 不要传 `typeof(GraphicsCaptureItem).GUID` —— 那个是**类**的 GUID，
                //    拿它当接口 IID 传，CreateForMonitor 会返回 E_NOINTERFACE（实测如此）。
                //    改传 **IInspectable** 的 IID：每个 WinRT 对象都实现它，一定拿得到；
                //    再用 CsWinRT 的 MarshalInspectable 转成强类型，彻底不用猜 GUID。
                Guid inspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
                int hrItem = interop.CreateForMonitor(monitor, ref inspectableIid, out IntPtr itemPtr);
                if (hrItem < 0 || itemPtr == IntPtr.Zero)
                {
                    RuntimeLog.Info($"WGC：CreateForMonitor 失败 HRESULT=0x{hrItem:X8}");
                    return null;
                }

                try
                {
                    return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            DeleteHString(classId);
        }
    }

    /// <summary>把 Vortice 的 D3D11 设备包成 WGC 需要的 <c>IDirect3DDevice</c>。</summary>
    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr devicePtr);

        if (hr < 0 || devicePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateDirect3D11DeviceFromDXGIDevice 失败 HRESULT=0x{hr:X8}");
        }

        try
        {
            // ⚠️ 不能用 `Marshal.GetObjectForIUnknown` + 强转：那样拿到的是 `System.__ComObject`，
            // 转不到 WinRT 投影接口（实测 InvalidCastException，CCW 建不起来）。
            // 必须走 CsWinRT 的 MarshalInspectable —— 与取 item 时同一套做法。
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            Marshal.Release(devicePtr);
        }
    }

    /// <summary>
    /// 从 WGC 帧里取出 D3D11 纹理。
    ///
    /// 三条路都留着，并记录实际走通的是哪一条 —— 这段是纯互操作，
    /// 出错时现象高度一致（都是 E_NOINTERFACE），没有日志根本没法分辨。
    /// </summary>
    private static ID3D11Texture2D GetTextureFromFrame(Direct3D11CaptureFrame frame)
    {
        object surface = frame.Surface;

        // ⚠️ 诊断**只记一次**：每帧两行会迅速撞上 RuntimeLog 的 512KB 上限，
        // 把「帧耗时」这些真正要看的日志全冲掉（实测跑 20 秒就把日志清空了）。
        if (!_diagnosticsLogged)
        {
            _diagnosticsLogged = true;
            LogSurfaceDiagnostics(surface);
        }

        // 路线 A（**微软官方示例的写法**）：直接把投影对象**强转**成 ComImport 接口。
        //
        // ⚠️ 关键差别：强转是让 .NET 通过 **RCW** 去做 QI，标识处理是对的；
        // 而我先前那套「GetIUnknownForObject + Marshal.QueryInterface」拿到的标识不对，
        // 三条路（直接 QI 纹理 / GetComInterfaceForObject / 手工 QI access）**全都**
        // 返回 E_NOINTERFACE，且错误信息完全指不到这里 —— 为此卡了三轮。
        int accessHr = 0;
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)surface;
            Guid iid = IID_ID3D11Texture2D;
            IntPtr texturePtr = access.GetInterface(ref iid);
            if (texturePtr != IntPtr.Zero)
            {
                RuntimeLog.Info("WGC：取纹理走路线 A（直接强转）");
                return WrapTexture(texturePtr);
            }

            Marshal.Release(texturePtr);
        }
        catch (Exception ex)
        {
            accessHr = Marshal.GetHRForException(ex);
        }

        IntPtr surfacePtr = GetAbiPointer(surface);
        try
        {
            // 路线 B：**直接** QI 出 ID3D11Texture2D（帧池是 D3D11 的，surface 本身就是一张纹理）
            Guid directIid = IID_ID3D11Texture2D;
            int directHr = Marshal.QueryInterface(surfacePtr, ref directIid, out IntPtr directPtr);
            if (directHr >= 0 && directPtr != IntPtr.Zero)
            {
                RuntimeLog.Info("WGC：取纹理走路线 B（直接 QI）");
                return WrapTexture(directPtr);
            }

            // 路线 C：手工 QI 出 IDirect3DDxgiInterfaceAccess
            Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
            int hr = Marshal.QueryInterface(surfacePtr, ref accessIid, out IntPtr accessPtr2);

            // 每一步都留痕（**只记一次**，理由同诊断）—— 上一轮就是因为
            // 「三条路都没打日志」而完全无法定位卡在哪
            if (!_routeLogged)
            {
                _routeLogged = true;
                RuntimeLog.Info($"WGC 路线 C：QI(access)=0x{hr:X8}（p=0x{accessPtr2:X}）");
            }

            if (hr >= 0 && accessPtr2 != IntPtr.Zero)
            {
                try
                {
                    var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr2);
                    Guid iid = IID_ID3D11Texture2D;
                    IntPtr texturePtr = access.GetInterface(ref iid);
                    if (texturePtr != IntPtr.Zero)
                    {
                        if (!_routeLogged)
                        {
                            _routeLogged = true;
                            RuntimeLog.Info("WGC：取纹理走路线 C（手工 QI access）");
                        }

                        return WrapTexture(texturePtr);
                    }

                    Marshal.Release(texturePtr);
                }
                finally
                {
                    Marshal.Release(accessPtr2);
                }
            }

            throw new InvalidOperationException(
                $"取纹理三条路都失败：A=0x{accessHr:X8}，B=0x{directHr:X8}，C=0x{hr:X8}");
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }

    /// <summary>
    /// 取**最新**的一帧：把排队里的旧帧全部丢掉，只留最后一张。
    ///
    /// ⚠️ `TryGetNextFrame()` 返回的是池里**最老**的那一帧，而且没有"取最新"的 API ——
    /// 于是只要有一次抖动（渲染比合成慢一点、或某帧耗时高），队列里就会**长期积压**一张旧帧，
    /// 稳态内容年龄被白白多压一帧（+16.7ms）。这正是「折射区慢半拍」里我们能省掉的那一部分。
    ///
    /// 参考实现（singularity）也是这么处理的：`GPU_BUFFERS = 3` 且注释写着
    /// "the newest frame lives in shared GPU texture i" —— 它总是画最新的那一帧。
    /// </summary>
    private static Direct3D11CaptureFrame? DrainToNewest(Direct3D11CaptureFramePool pool)
    {
        Direct3D11CaptureFrame? newest = null;

        while (true)
        {
            var next = pool.TryGetNextFrame();
            if (next is null)
            {
                break;
            }

            newest?.Dispose();
            newest = next;
        }

        return newest;
    }

    /// <summary>诊断只记一次（每帧记会把日志刷爆，见调用处的说明）。</summary>
    private static bool _diagnosticsLogged;

    /// <summary>「走的哪条路」也只记一次（同理，别把日志刷爆）。</summary>
    private static bool _routeLogged;

    /// <summary>
    /// 诊断：确认 QI 机制本身通不通（拿 IInspectable 当对照组 —— 每个 WinRT 对象必然实现它，
    /// 它若也失败，问题一定在「指针是假的」而不是「接口不存在」）。
    /// </summary>
    private static void LogSurfaceDiagnostics(object surface)
    {
        IntPtr probe = GetAbiPointer(surface);

        Guid inspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        Guid dxgiSurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
        int hrInspectable = Marshal.QueryInterface(probe, ref inspectable, out IntPtr p1);
        int hrDxgi = Marshal.QueryInterface(probe, ref dxgiSurface, out IntPtr p2);
        Guid textureIid = IID_ID3D11Texture2D;
        int hrTexture = Marshal.QueryInterface(probe, ref textureIid, out IntPtr p3);

        RuntimeLog.Info(
            $"WGC 诊断：surface 类型={surface.GetType().FullName}，" +
            $"QI(IInspectable)=0x{hrInspectable:X8}（p=0x{p1:X}），" +
            $"QI(IDXGISurface)=0x{hrDxgi:X8}（p=0x{p2:X}），" +
            $"QI(ID3D11Texture2D)=0x{hrTexture:X8}（p=0x{p3:X}）");

        // 只释放 QI 出来的那三个（它们各自 AddRef 过）。
        // ⚠️ `probe` **不能**释放：它是 marshaler 借给我们的，
        // 多放一次会把引用计数弄坏 —— 后面再建 marshaler 就会抛
        // `InvalidCastException`，看起来像「接口不存在」，极具误导性。
        if (p1 != IntPtr.Zero) { Marshal.Release(p1); }
        if (p2 != IntPtr.Zero) { Marshal.Release(p2); }
        if (p3 != IntPtr.Zero) { Marshal.Release(p3); }
    }

    /// <summary>
    /// 把原生指针包成 Vortice 的 <see cref="ID3D11Texture2D"/>。
    ///
    /// ⚠️ 两条都踩过：
    /// ① **不能**写成 `(ID3D11Texture2D)Marshal.GetObjectForIUnknown(ptr)` ——
    ///    Vortice 这些类型是**类**而不是接口，`System.__ComObject` 强转过去必抛
    ///    `InvalidCastException`（"cannot be cast to class type ..."）。
    ///    正确写法是 `new ID3D11Texture2D(ptr)`（DXGI 那条路用的是 `QueryInterface&lt;T&gt;()`，同理）。
    /// ② `new` 出来即**拥有**该引用（dispose 时 Release），
    ///    所以调用方**不能再** `Marshal.Release(ptr)` —— 否则多放一次。
    /// </summary>
    private static ID3D11Texture2D WrapTexture(IntPtr texturePtr) => new(texturePtr);

    /// <summary>
    /// 取 WinRT 对象的**真正 ABI 指针**。
    ///
    /// ⚠️ **这是本次调试最关键的坑**：`Marshal.GetIUnknownForObject(obj)` 对 CsWinRT 的
    /// `WinRT.IInspectable` 包装器**无效** —— 拿到的指针做 QI 一律返回 E_NOINTERFACE，
    /// **连 IInspectable 自己都取不到**（而每个 WinRT 对象必然实现它）。
    /// 现象因此极具误导性：看起来像「接口不存在」，实际是「指针是假的」。
    /// 判定方法就是拿 IInspectable 当对照组 —— 它若也失败，问题一定在指针，不在接口。
    ///
    /// 正确做法：走 CsWinRT 自己的 <c>MarshalInspectable</c> marshaler。
    /// </summary>
    private static IntPtr GetAbiPointer(object winrtObject)
    {
        using var marshaler = WinRT.MarshalInspectable<object>.CreateMarshaler(winrtObject);
        return marshaler.ThisPtr;
    }

    private static (int Left, int Top) MonitorRect(IntPtr monitor)
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info)
            ? (info.rcMonitor.Left, info.rcMonitor.Top)
            : (0, 0);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, EntryPoint = "WindowsCreateString")]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", EntryPoint = "WindowsDeleteString")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IntPtr CreateHString(string value)
    {
        int hr = WindowsCreateString(value, value.Length, out IntPtr hstring);
        return hr < 0 ? IntPtr.Zero : hstring;
    }

    private static void DeleteHString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero)
        {
            WindowsDeleteString(hstring);
        }
    }
}
