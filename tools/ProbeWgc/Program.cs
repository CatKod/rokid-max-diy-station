// SPDX-License-Identifier: MIT
//
// Hardware probe for the Windows.Graphics.Capture pipeline.
//
// It answers, empirically, the three questions that matter and that
// cannot be answered by reading code:
//
//   1. Can an UNPACKAGED app create a capture item for an arbitrary
//      HWND?  (No package identity, no MSIX, no broker.)
//   2. Does a frame actually arrive, and at what rate?
//   3. Can the captured ID3D11Texture2D be turned into a shader
//      resource view on a DIFFERENT GPU than the one the source
//      window is composited on?
//
// (3) is the one that decides whether the whole architecture works.
// The Rokid Max's display is driven by the NVIDIA dGPU, while the
// user's apps run on the Intel iGPU.  If WGC cannot hand us a texture
// across that boundary, the design has to change.
//
// Usage:  dotnet run --project tools/ProbeWgc
//         dotnet run --project tools/ProbeWgc -- <partial window title>
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

internal static class Program
{
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    // NOTE: typeof(GraphicsCaptureItem).GUID is NOT the right IID to pass to
    // IGraphicsCaptureItemInterop::CreateForWindow — using it returns
    // E_NOTIMPL (0x80004002).  The canonical IID_IGraphicsCaptureItem is
    // the one below.  Verified empirically on this machine.
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureItemAltIid = CaptureItemIid;
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;

        Console.WriteLine("=== Windows.Graphics.Capture hardware probe ===");
        Console.WriteLine($"OS: {Environment.OSVersion.Version}");
        Console.WriteLine($"Process is packaged: {IsPackaged()}");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 1. Find a target window.
        // -------------------------------------------------------------
        var candidates = new List<(IntPtr Hwnd, string Title)>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (h == GetShellWindow()) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            var t = sb.ToString();
            if (!string.IsNullOrWhiteSpace(t)) candidates.Add((h, t));
            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"Visible top-level windows ({candidates.Count}):");
        foreach (var (h, t) in candidates.Take(20))
            Console.WriteLine($"   0x{h.ToInt64():X}  {t}");

        var target = IntPtr.Zero;
        if (filter is not null)
        {
            target = candidates.FirstOrDefault(c => c.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).Hwnd;
        }
        else
        {
            target = candidates.FirstOrDefault(c => c.Title.Contains("Rokid", StringComparison.OrdinalIgnoreCase)).Hwnd;
            if (target == IntPtr.Zero)
                target = candidates.FirstOrDefault(c => c.Title.Contains("Terminal", StringComparison.OrdinalIgnoreCase)).Hwnd;
            if (target == IntPtr.Zero && candidates.Count > 0)
                target = candidates[0].Hwnd;
        }

        Console.WriteLine();
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: no target window found. Pass a title substring as the first argument.");
            return 1;
        }
        var targetTitle = candidates.First(c => c.Hwnd == target).Title;
        Console.WriteLine($"Target: 0x{target.ToInt64():X}  '{targetTitle}'");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 1b. Try every candidate window with every known IID, so a
        //     single odd window cannot mask a working pipeline.
        // -------------------------------------------------------------
        Console.WriteLine("[0] Sweep: CreateForWindow over all candidate windows");
        foreach (var (h, t) in candidates)
        {
            var r1 = TryCreate(h, CaptureItemIid);
            var r2 = TryCreate(h, CaptureItemAltIid);
            Console.WriteLine($"  0x{h.ToInt64():X} '{Trunc(t, 34)}'  sdkIid={(r1 == 0 ? "OK" : r1.ToString("X8"))}  altIid={(r2 == 0 ? "OK" : r2.ToString("X8"))}");
            if (r1 == 0 || r2 == 0) { target = h; targetTitle = t; break; }
        }
        Console.WriteLine();
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: CreateForWindow failed for every candidate window.");
            return 1;
        }
        Console.WriteLine($"Target after sweep: 0x{target.ToInt64():X}  '{targetTitle}'");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 2. Create the capture item via the interop path.
        // -------------------------------------------------------------
        Console.WriteLine("[1] IGraphicsCaptureItemInterop::CreateForWindow");
        var item = CreateItemForWindow(target);
        if (item is null)
        {
            Console.WriteLine("  FAIL: could not create a capture item.");
            return 1;
        }
        Console.WriteLine($"  OK: '{item.DisplayName}' {item.Size.Width}x{item.Size.Height}");

        // -------------------------------------------------------------
        // 3. Which adapter drives the glasses?
        // -------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[2] DXGI topology");
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        IDXGIAdapter glassesAdapter = null;
        IDXGIAdapter sourceAdapter = null;
        for (uint i = 0; ; i++)
        {
            if (factory.EnumAdapters(i, out var ad).Failure) break;
            if (ad is null) break;
            var name = ad.Description.Description;
            for (uint j = 0; ; j++)
            {
                if (ad.EnumOutputs(j, out var output).Failure) break;
                if (output is null) break;
                using (output)
                {
                    var d = output.Description;
                    Console.WriteLine($"  {name}: output '{d.DeviceName}' {d.DesktopCoordinates.Right - d.DesktopCoordinates.Left}x{d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top}");
                    if (d.DeviceName.Contains("DISPLAY5")) glassesAdapter = ad;
                    if (d.DeviceName.Contains("DISPLAY1") || d.DeviceName.Contains("DISPLAY2"))
                        sourceAdapter ??= ad;
                }
            }
        }

        // -------------------------------------------------------------
        // 4. Create the D3D device on the glasses' adapter.
        // -------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[3] D3D11 device on the glasses' adapter");
        var deviceAdapter = glassesAdapter ?? sourceAdapter;
        var device = D3D11.D3D11CreateDevice(
            deviceAdapter,
            Vortice.Direct3D.DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out var dev);
        if (device.Failure || dev is null)
        {
            Console.WriteLine("  FAIL: D3D11CreateDevice failed: " + device);
            return 1;
        }
        Console.WriteLine($"  OK on '{deviceAdapter?.Description.Description ?? "(default)"}'");

        // -------------------------------------------------------------
        // 5. WinRT device + frame pool.
        // -------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[4] WinRT IDirect3DDevice + frame pool");
        IDirect3DDevice winrtDevice;
        try
        {
            winrtDevice = CreateWinRtDevice(dev);
            Console.WriteLine("  OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32 { Width = item.Size.Width, Height = item.Size.Height });
        var session = pool.CreateCaptureSession(item);
        try { session.IsBorderRequired = false; Console.WriteLine("  IsBorderRequired=false OK"); }
        catch (Exception ex) { Console.WriteLine($"  IsBorderRequired unavailable: {ex.Message}"); }
        try { session.MinUpdateInterval = TimeSpan.FromMilliseconds(1); Console.WriteLine("  MinUpdateInterval=1ms OK"); }
        catch (Exception ex) { Console.WriteLine($"  MinUpdateInterval unavailable: {ex.Message}"); }
        session.StartCapture();
        Console.WriteLine("  StartCapture called.");

        // -------------------------------------------------------------
        // 6. Pull frames and test the cross-adapter SRV.
        // -------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[5] Pulling frames for 5 seconds");
        if (Environment.GetEnvironmentVariable("PROBE_DUMP_API") == "1")
        {
            DumpWinRtApi();
            Console.WriteLine();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frames = 0;
        var srvOk = false;
        var textureDesc = "";
        while (sw.Elapsed.TotalSeconds < 5.0)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) { Thread.Sleep(4); continue; }
            frames++;
            if (frames > 1) continue;

            Console.WriteLine($"  First frame: content {frame.ContentSize.Width}x{frame.ContentSize.Height}");
            try
            {
                var texPtr = IntPtr.Zero;
                WinRT.IObjectReference? accessRef = null;
                try
                {
                    // A CsWinRT-projected object is a MANAGED wrapper.  Asking
                    // Marshal.GetIUnknownForObject for its IUnknown gives us the
                    // CCW of the wrapper, which does NOT implement
                    // IDirect3DDxgiInterfaceAccess.  We need the real
                    // IInspectable underneath, which is IWinRTObject.NativeObject.
                    var nativeObj = ((WinRT.IWinRTObject)frame.Surface).NativeObject;
                    accessRef = nativeObj.As(DxgiAccessIid);
                    if (accessRef is null || accessRef.ThisPtr == IntPtr.Zero)
                    {
                        Console.WriteLine("  FAIL: As(IDirect3DDxgiInterfaceAccess) returned null");
                    }
                    else
                    {
                        var acc = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessRef.ThisPtr);
                        var texIid = typeof(ID3D11Texture2D).GUID;
                        Console.WriteLine($"  typeof(ID3D11Texture2D).GUID = {texIid}");
                        Console.WriteLine($"  IID_ID3D11Texture2D (canonical) = 6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                        texPtr = acc.GetInterface(ref texIid);
                        if (texPtr == IntPtr.Zero)
                        {
                            Console.WriteLine("  FAIL: GetInterface<ID3D11Texture2D> returned null");
                        }
                        else
                        {
                            var tex = SharpGen.Runtime.MarshallingHelpers.FromPointer<ID3D11Texture2D>(texPtr);
                            textureDesc = $"{tex.Description.Width}x{tex.Description.Height} {tex.Description.Format}";
                            Console.WriteLine($"  Texture: {textureDesc}");

                            // THE critical test.
                            try
                            {
                                var srv = dev.CreateShaderResourceView(tex);
                                srvOk = srv is not null;
                                Console.WriteLine(srvOk
                                    ? "  PASS: CreateShaderResourceView on the OTHER adapter succeeded."
                                    : "  FAIL: CreateShaderResourceView returned null.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  FAIL: CreateShaderResourceView threw {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    if (texPtr != IntPtr.Zero) Marshal.Release(texPtr);
                    accessRef?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: surface interop threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"  Frames in 5s: {frames}  ({frames / 5.0:0.0} fps)");

        // -------------------------------------------------------------
        // 7. The managed alternative.
        // -------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[6] GraphicsCaptureItem.TryCreateFromWindowId (managed projection)");
        try
        {
            var wid = new Windows.UI.WindowId((ulong)(long)target);
            var item2 = GraphicsCaptureItem.TryCreateFromWindowId(wid);
            Console.WriteLine(item2 is null
                ? "  -> NULL (unsupported or needs package identity)"
                : $"  -> OK {item2.Size.Width}x{item2.Size.Height}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== RESULT ===");
        Console.WriteLine("  unpackaged capture item : OK");
        Console.WriteLine($"  frames received          : {frames} ({frames / 5.0:0.0} fps)");
        Console.WriteLine($"  captured texture         : {textureDesc}");
        Console.WriteLine($"  cross-adapter SRV        : {(srvOk ? "PASS" : "FAIL")}");
        return srvOk && frames > 0 ? 0 : 1;
    }

    private static bool IsPackaged()
    {
        try
        {
            var t = Type.GetType("Windows.ApplicationModel.Package, Microsoft.Windows.SDK.NET");
            return t?.GetProperty("Current")?.GetValue(null) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static void DumpWinRtApi()
    {
        foreach (var asm in new[]
                 {
                     typeof(WinRT.MarshalInterface<>).Assembly,
                     typeof(Vortice.Direct3D11.ID3D11Texture2D).Assembly,
                     typeof(SharpGen.Runtime.ComObject).Assembly,
                 })
        {
            foreach (var t in asm.GetExportedTypes())
            {
                if (t.Name is not ("MarshallingHelpers" or "ComObject" or "MemoryHelpers")) continue;
                Console.WriteLine($"  TYPE {t.FullName}");
                foreach (var c in t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    Console.WriteLine($"     ctor({string.Join(", ", Array.ConvertAll(c.GetParameters(), p => p.ParameterType.Name))})");
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                    Console.WriteLine($"     static {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
                foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                    Console.WriteLine($"     prop {p.Name} : {p.PropertyType.Name}");
            }
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>
    /// Bridges a Vortice ID3D11Device to the WinRT IDirect3DDevice.
    /// A native ID3D11Device does NOT implement IDirect3DDxgiInterfaceAccess
    /// (that lives on the WinRT side); the native path is to ask for
    /// IDXGIDevice and hand it to CreateDirect3D11DeviceFromDXGIDevice.
    /// </summary>
    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtPtr);
        if (hr != 0 || winrtPtr == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr); }
        finally { Marshal.Release(winrtPtr); }
    }

    private static int TryCreate(IntPtr hwnd, Guid itemIid)
    {
        var iid = InteropIid;
        const string classIdText = "Windows.Graphics.Capture.GraphicsCaptureItem";
        if (WindowsCreateString(classIdText, classIdText.Length, out var classId) != 0) return -1;
        var hr = RoGetActivationFactory(classId, ref iid, out var factory);
        WindowsDeleteString(classId);
        if (hr != 0 || factory == IntPtr.Zero) return hr;
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var id = itemIid;
            var hr2 = interop.CreateForWindow(hwnd, ref id, out var result);
            if (result != IntPtr.Zero) Marshal.Release(result);
            return hr2;
        }
        finally { Marshal.Release(factory); }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var iid = InteropIid;
        // RoGetActivationFactory needs the activatable class id as an HSTRING.
        const string classIdText = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr0 = WindowsCreateString(classIdText, classIdText.Length, out var classId);
        if (hr0 != 0 || classId == IntPtr.Zero)
        {
            Console.WriteLine($"  WindowsCreateString failed: 0x{hr0:X8}");
            return null;
        }
        var hr = RoGetActivationFactory(classId, ref iid, out var factory);
        WindowsDeleteString(classId);
        Console.WriteLine($"  RoGetActivationFactory(interop) -> 0x{hr:X8}");
        if (hr != 0 || factory == IntPtr.Zero) return null;

        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemIid = CaptureItemIid;
            Console.WriteLine($"  IID_GraphicsCaptureItem = {itemIid}");
            var hr2 = interop.CreateForWindow(hwnd, ref itemIid, out var result);
            Console.WriteLine($"  CreateForWindow -> 0x{hr2:X8}");
            if (hr2 != 0 || result == IntPtr.Zero) return null;
            try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr hwnd, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }
}
