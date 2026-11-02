// SPDX-License-Identifier: MIT
//
// Capturing a whole MONITOR with Windows.Graphics.Capture (WGC).
//
// This is the capture half of the True Spatial Display.  Where
// WindowCapture grabs one HWND, this grabs an entire display, so the
// texture contains the real Windows desktop of that monitor: its
// wallpaper, every window in its z-order, and its taskbar.
//
// The pipeline is the same as the window one, with one difference:
//
//   HMONITOR
//     -> IGraphicsCaptureItemInterop::CreateForMonitor
//     -> GraphicsCaptureItem
//     -> Direct3D11CaptureFramePool.CreateFreeThreaded(device, ...)
//     -> Direct3D11CaptureFrame  (an IDirect3DSurface)
//     -> .Surface  -> IDirect3DDxgiInterfaceAccess -> ID3D11Texture2D
//
// The one trap worth stating up front is the vtable ORDER of
// IGraphicsCaptureItemInterop.  CreateForWindow is slot 3 and
// CreateForMonitor is slot 4, and the CLR binds the DECLARED C#
// methods to consecutive slots starting at 3.  If CreateForMonitor is
// declared on its own - or declared first - the CLR calls
// CreateForWindow with a monitor handle, which fails with
// E_INVALIDARG or captures the wrong thing.  Both methods must be
// declared, in that order.  See <see cref="IGraphicsCaptureItemInterop"/>.
//
// Why a monitor and not a window
// -----------------------------
// A window capture gives one window.  A monitor capture gives a whole
// desktop, with the OS's own z-order and taskbar, which is the entire
// point of the spatial display: the user wants a real Windows desktop
// in the air, not a projected app.
//
// Occlusion is not a concern.  WGC captures the DWM-composed surface of
// the monitor, so an unrelated window floating over it does not appear
// in the capture - unlike DXGI Desktop Duplication, which grabs a whole
// output including everything on it.
//
// The virtual monitor this is designed for sits at NEGATIVE coordinates
// (typically (-1920, 0)), off to the side of the physical desktop.  It
// must not overlap the glasses' display, because our own topmost AR
// panel lives there and would occlude the capture - and because Windows
// forbids overlapping monitor rectangles outright.
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Captures an entire display into a D3D11 texture.
/// </summary>
public sealed class DisplayCapture : IDisposable
{
    private readonly object _lock = new();

    /// <summary>
    /// The capture item's raw ABI pointer.
    /// </summary>
    /// <remarks>
    /// GraphicsCaptureItem is a plain WinRT object with no IClosable,
    /// so the projection gives it no Dispose().  We therefore keep the
    /// ABI pointer and release it ourselves in <see cref="Stop"/>.
    /// </remarks>
    private IntPtr _itemAbi;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    /// <summary>
    /// Our OWN copy of the captured image.
    /// </summary>
    /// <remarks>
    /// This is not optional and not an optimisation - it is a
    /// correctness requirement.  A Direct3D11CaptureFrame's underlying
    /// ID3D11Texture2D belongs to the frame pool, which recycles it as
    /// soon as the frame is disposed.  If we handed that texture
    /// straight to the renderer as a shader resource, the renderer
    /// would sample a buffer that DWM is concurrently overwriting -
    /// which shows up as torn or flickering content, and can crash the
    /// GPU driver.
    ///
    /// So we do a GPU-side CopyResource into a texture we own, while
    /// the frame is still alive.  The copy stays on the GPU: no CPU
    /// readback, no staging buffer.  The cost is one full-screen blit
    /// per frame, which on a 1080p panel is well under a millisecond.
    /// </remarks>
    private ID3D11Texture2D? _copyTexture;
    private ID3D11ShaderResourceView? _srv;
    private int _width;
    private int _height;

    /// <summary>The shader resource view the renderer samples.</summary>
    public ID3D11ShaderResourceView? ShaderResourceView
    {
        get { lock (_lock) return _srv; }
    }

    /// <summary>
    /// The most recent captured frame, as a shader resource view.
    /// </summary>
    /// <remarks>
    /// Named to match WindowCapture.Texture, so the renderer does not
    /// have to care which kind of capture is feeding it.
    /// </remarks>
    public ID3D11ShaderResourceView? Texture
    {
        get { lock (_lock) return _srv; }
    }

    /// <summary>
    /// Our own copy of the captured frame, as a texture.
    /// </summary>
    /// <remarks>
    /// The renderer samples the shader resource view; a diagnostic tool
    /// that wants to look at the pixels needs the texture itself,
    /// because a shader resource view cannot be the source of a
    /// CopyResource.
    /// </remarks>
    public ID3D11Texture2D? CapturedTexture
    {
        get { lock (_lock) return _copyTexture; }
    }

    /// <summary>The width of the captured display, in pixels.</summary>
    public int Width { get { lock (_lock) return _width; } }

    /// <summary>The height of the captured display, in pixels.</summary>
    public int Height { get { lock (_lock) return _height; } }

    /// <summary>The monitor being captured.</summary>
    public IntPtr SourceMonitor { get; private set; }

    /// <summary>Raised when the source display goes away.</summary>
    public event Action? OnSourceClosed;

    /// <summary>
    /// Start capturing the given monitor.
    /// </summary>
    /// <param name="hmonitor">The monitor to capture.</param>
    /// <param name="device">The D3D device the renderer uses.</param>
    public void Start(IntPtr hmonitor, ID3D11Device device)
    {
        Stop();

        var (item, itemAbi) = CreateItemForMonitor(hmonitor)
            ?? throw new InvalidOperationException(
                $"Could not create a capture item for monitor {hmonitor}. "
                + "The display may have been detached, or the app lacks the "
                + "graphics capture capability.");

        // Build the WinRT device that wraps our D3D device, so the
        // frame pool's frames can be used by our renderer.
        var graphicsDevice = CreateGraphicsDevice(device);

        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            graphicsDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size.Width > 0 && item.Size.Height > 0
                ? item.Size
                : new SizeInt32 { Width = 1920, Height = 1080 });

        var session = framePool.CreateCaptureSession(item);

        // The yellow capture border is a security feature of WGC.  It
        // would look like a glowing rectangle around the panel in the
        // glasses, which defeats the whole point.  It can be turned off
        // on Windows 11 21H2 and later.
        TrySetIsBorderRequired(session, false);

        // Do NOT bake the OS cursor into the captured frame.
        //
        // The spatial display draws its OWN cursor - a 3D quad floating
        // in the glasses' space, positioned by mapping the OS cursor's
        // absolute coordinates into the panel's UV space.  If the OS
        // arrow were also baked into the texture, the user would see two
        // cursors: a lagging, perspective-distorted ghost in the
        // texture, and the crisp 3D one in the air.  The ghost is
        // exactly the artifact the custom cursor exists to replace.
        TrySetIsCursorCaptureEnabled(session, false);

        // Windows 11 24H2 (build 26100) throttles unconfigured WGC
        // sessions to 60 Hz.  Ask for the full rate so the capture
        // keeps up with the glasses' panel.
        TrySetMinUpdateInterval(session, TimeSpan.FromMilliseconds(1));

        // StartCapture is REQUIRED - without it the session never
        // produces frames at all, and the symptom is a silently empty
        // capture rather than an error.
        session.StartCapture();

        lock (_lock)
        {
            _itemAbi = itemAbi;
            _session = session;
            _framePool = framePool;
            _device = device;
            SourceMonitor = hmonitor;
        }
    }

    /// <summary>
    /// Pull the newest frame, if there is one, and refresh the shader
    /// resource view.
    /// </summary>
    /// <remarks>
    /// THIS MUST BE CALLED ON THE RENDER THREAD.
    ///
    /// The copy is issued on the device's immediate context, and an
    /// ID3D11DeviceContext is NOT thread-safe.  If the capture ran on
    /// a different thread from the renderer, the two would interleave
    /// their calls on the same context and the result was undefined
    /// behaviour - in practice the renderer's draw calls were
    /// silently dropped, and the panel came out completely black
    /// while the capture itself reported a perfectly good frame.
    ///
    /// So the station hands the renderer a callback and the renderer
    /// pulls the frame itself, on its own thread, just before it
    /// draws.  See <see cref="RokidDiyStation.App.Spatial.PanelRenderer.BeforeDraw"/>.
    /// </remarks>
    public void TryUpdate()
    {
        Direct3D11CaptureFramePool? pool;
        lock (_lock) pool = _framePool;
        if (pool is null) return;

        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;

        var surface = frame.Surface;
        if (surface is null) return;

        // IDirect3DDxgiInterfaceAccess::GetInterface<ID3D11Texture2D>()
        //
        // The WinRT projection does not surface this interface, so we
        // go through raw COM.  See GetD3D11Texture for the traps.
        var texture = GetD3D11Texture(surface);
        if (texture is null) return;

        try
        {
            var desc = texture.Description;

            lock (_lock)
            {
                if (_device is null) return;

                // Re-create our copy when the source display's size
                // changes (the user changed the virtual monitor's
                // resolution).  A CopyResource requires identical
                // formats and dimensions, so the copy has to be resized
                // too.
                if (_copyTexture is null
                    || _copyTexture.Description.Width != desc.Width
                    || _copyTexture.Description.Height != desc.Height)
                {
                    _copyTexture?.Dispose();
                    _copyTexture = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                    _srv?.Dispose();
                    _srv = _device.CreateShaderResourceView(_copyTexture);
                    _width = (int)desc.Width;
                    _height = (int)desc.Height;
                }

                // The GPU-side copy.  This MUST happen while the frame
                // is alive, because the frame pool recycles the source
                // texture the moment the frame is released.
                //
                // The context is the device's own immediate context.
                // It is BORROWED, never disposed: it belongs to the
                // device, and disposing it would tear the renderer's
                // pipeline down.
                _context ??= _device.ImmediateContext;
                _context.CopyResource(_copyTexture, texture);
            }
        }
        finally
        {
            texture.Dispose();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
            if (_itemAbi != IntPtr.Zero)
            {
                Marshal.Release(_itemAbi);
                _itemAbi = IntPtr.Zero;
            }
            _srv?.Dispose();
            _srv = null;
            _copyTexture?.Dispose();
            _copyTexture = null;
            // NOTE: _context is BORROWED from the device - it is
            // _device.ImmediateContext, which the renderer also uses.
            // Disposing it here would break the renderer's pipeline.
            _context = null;
            SourceMonitor = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // -----------------------------------------------------------------
    // Version-sensitive session settings
    // -----------------------------------------------------------------

    /// <summary>
    /// Turn off the yellow capture border, where the OS supports it.
    /// </summary>
    /// <remarks>
    /// IsBorderRequired arrived in Windows 11 21H2 (build 22000).  On
    /// older builds the property does not exist and touching it throws,
    /// so we probe for it first.
    /// </remarks>
    private static void TrySetIsBorderRequired(GraphicsCaptureSession session, bool required)
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                session.IsBorderRequired = required;
            }
        }
        catch
        {
            // The property is not settable on this build.  The border
            // stays; nothing we can do about it.
        }
    }

    /// <summary>
    /// Stop baking the OS cursor into the captured frame, where the OS
    /// supports it.
    /// </summary>
    /// <remarks>
    /// IsCursorCaptureEnabled exists from Windows 10 1803 onwards, so
    /// it is always present on the builds we target; the probe is
    /// belt-and-braces, and a failure is not fatal (the only symptom
    /// would be a ghost cursor in the panel).
    /// </remarks>
    private static void TrySetIsCursorCaptureEnabled(GraphicsCaptureSession session, bool enabled)
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = enabled;
            }
        }
        catch
        {
            // Not settable on this build; the cursor may appear baked
            // into the capture.  Not fatal.
        }
    }

    /// <summary>
    /// Ask for the full capture rate, where the OS supports it.
    /// </summary>
    /// <remarks>
    /// MinUpdateInterval arrived in Windows 11 24H2 (build 26100) and
    /// exists precisely to defeat the 60 Hz capture throttle that
    /// build introduced.
    /// </remarks>
    private static void TrySetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan interval)
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
            {
                session.MinUpdateInterval = interval;
            }
        }
        catch
        {
            // Not available on this build; the default rate applies.
        }
    }

    // -----------------------------------------------------------------
    // Interop shims
    // -----------------------------------------------------------------

    /// <summary>
    /// Create a capture item for a monitor handle.
    /// </summary>
    /// <remarks>
    /// The .NET WinRT projection does not surface
    /// IGraphicsCaptureItemInterop, so we activate it by hand through
    /// RoGetActivationFactory and call CreateForMonitor through the
    /// vtable.
    ///
    /// Note the ordering: we ask the activation factory for the
    /// *interop* IID directly, so the returned factory object IS the
    /// interop object.  Querying the factory for
    /// IGraphicsCaptureItemInterop afterwards would give us the item
    /// factory instead, which has no CreateForMonitor.
    /// </remarks>
    private static (GraphicsCaptureItem Item, IntPtr Abi)? CreateItemForMonitor(IntPtr hmonitor)
    {
        var iid = GraphicsCaptureItemInteropIid;

        // RoGetActivationFactory needs the activatable class id as an
        // HSTRING.  Passing a null pointer here fails with E_INVALIDARG
        // (0x80070057) and is very easy to get wrong.
        const string classIdText = "Windows.Graphics.Capture.GraphicsCaptureItem";
        if (WindowsCreateString(classIdText, classIdText.Length, out var classId) != 0)
            return null;

        int hr;
        IntPtr factory;
        try
        {
            hr = RoGetActivationFactory(classId, ref iid, out factory);
        }
        finally
        {
            WindowsDeleteString(classId);
        }
        if (hr != 0 || factory == IntPtr.Zero) return null;

        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);

            // NOTE: do NOT use typeof(GraphicsCaptureItem).GUID here.  The
            // .NET projection's GUID for the runtime class is not the same
            // thing as IID_IGraphicsCaptureItem, and passing it makes
            // CreateForMonitor fail with E_NOTIMPL (0x80004002).  Verified
            // empirically on Windows 11 build 26100.
            var itemIid = GraphicsCaptureItemIid;
            var hr2 = interop.CreateForMonitor(hmonitor, ref itemIid, out var result);
            if (hr2 != 0 || result == IntPtr.Zero) return null;

            try
            {
                var item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
                return (item, result);
            }
            catch
            {
                Marshal.Release(result);
                throw;
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>
    /// Pull the ID3D11Texture2D out of a WinRT capture surface.
    /// </summary>
    /// <remarks>
    /// Two traps here, both verified on Windows 11 build 26100:
    ///
    /// 1. <c>Marshal.GetIUnknownForObject(surface)</c> returns the IUnknown
    ///    of the *managed CCW wrapper*, which does not implement
    ///    IDirect3DDxgiInterfaceAccess.  We need the real IInspectable
    ///    underneath the CsWinRT projection, which is
    ///    <c>((IWinRTObject)surface).NativeObject</c>.
    ///
    /// 2. The raw ID3D11Texture2D pointer must be wrapped with
    ///    SharpGen's MarshallingHelpers.FromPointer.  A plain
    ///    (ID3D11Texture2D) cast of the __ComObject throws
    ///    InvalidCastException, because Vortice's ID3D11Texture2D is a
    ///    class, not a COM interface type.
    /// </remarks>
    private static ID3D11Texture2D? GetD3D11Texture(IDirect3DSurface surface)
    {
        WinRT.IObjectReference? accessRef = null;
        IntPtr texPtr = IntPtr.Zero;
        try
        {
            var native = ((WinRT.IWinRTObject)surface).NativeObject;
            accessRef = native.As(DxgiInterfaceAccessIid);
            if (accessRef is null || accessRef.ThisPtr == IntPtr.Zero) return null;

            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessRef.ThisPtr);
            var texIid = typeof(ID3D11Texture2D).GUID;
            texPtr = access.GetInterface(ref texIid);
            if (texPtr == IntPtr.Zero) return null;

            // The caller owns this reference; hand it over and stop
            // releasing it ourselves.
            var texture = SharpGen.Runtime.MarshallingHelpers.FromPointer<ID3D11Texture2D>(texPtr);
            texPtr = IntPtr.Zero;
            return texture;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (texPtr != IntPtr.Zero) Marshal.Release(texPtr);
            accessRef?.Dispose();
        }
    }

    /// <summary>
    /// The vtable layout of IGraphicsCaptureItemInterop.  This is a
    /// pure-IUnknown-derived interface, NOT IInspectable, so the
    /// marshalling signature has an explicit this pointer.
    /// </summary>
    /// <remarks>
    /// ORDER IS LOAD-BEARING.  CreateForWindow is vtable slot 3 and
    /// CreateForMonitor is slot 4.  The CLR binds the declared methods
    /// to consecutive slots starting at 3, so declaring CreateForMonitor
    /// alone - or declaring it first - makes the CLR call
    /// CreateForWindow with an HMONITOR.
    /// </remarks>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr hwnd, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr hmonitor, ref Guid iid, out IntPtr result);
    }

    /// <summary>
    /// IDirect3DDxgiInterfaceAccess, the interface that hands out the
    /// underlying ID3D11Texture2D of a capture surface.
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    /// <summary>The IID of the interop interface.</summary>
    private static readonly Guid GraphicsCaptureItemInteropIid =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>
    /// IID_IGraphicsCaptureItem.  NOT typeof(GraphicsCaptureItem).GUID -
    /// see the note in <see cref="CreateItemForMonitor"/>.
    /// </summary>
    private static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>The IID of IDirect3DDxgiInterfaceAccess.</summary>
    private static readonly Guid DxgiInterfaceAccessIid =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        string sourceString, int length, out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    /// <summary>
    /// Wrap our D3D11 device in a WinRT IDirect3DDevice.
    /// </summary>
    /// <remarks>
    /// A native ID3D11Device does NOT implement
    /// IDirect3DDxgiInterfaceAccess - that interface lives on the WinRT
    /// side of the bridge.  Asking the native device for it fails with
    /// E_NOINTERFACE.  The native path is: ask the device for its
    /// IDXGIDevice, and hand that to
    /// CreateDirect3D11DeviceFromDXGIDevice.
    /// </remarks>
    private static IDirect3DDevice CreateGraphicsDevice(ID3D11Device device)
    {
        var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtPtr);
        if (hr != 0 || winrtPtr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr); }
        finally { Marshal.Release(winrtPtr); }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
