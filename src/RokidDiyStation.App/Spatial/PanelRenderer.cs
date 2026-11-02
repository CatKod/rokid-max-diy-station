// SPDX-License-Identifier: MIT
//
// The native render window and its Direct3D 11 pipeline.
//
// This is deliberately NOT a WPF window.  WPF's render thread
// (wpfgfx) synchronises to the primary display's refresh rate, and
// embedding a D3D surface inside WPF (HwndHost, D3DImage) subjects
// it to that thread's cadence - which would clamp the Rokid Max's
// panel down to 60 Hz and add one to two frames of buffering.  So we
// create a plain top-level Win32 window and drive it from our own
// thread with a flip-model swapchain.
//
// The window is:
//   WS_POPUP | WS_VISIBLE                          - borderless, no caption
//   WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
//                                                - always on top, not in
//                                                  the taskbar or Alt+Tab,
//                                                  and never steals focus
//
// Positioning it exactly on the Rokid's monitor bounds lets DWM use
// Independent Flip, which is the lowest-latency presentation path.
//
// Threading: the render thread owns the D3D objects exclusively.  The
// tracking thread writes the head pose into the anchor service; the
// render thread reads the latest snapshot.  No locks on the hot path.
//
// See .research/d2/findings.md for the measurements and the reasoning
// behind the choices made here.
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using RokidDiyStation.Core.Spatial;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Owns the render window, the D3D device and the swapchain, and draws
/// the pinned panel into them.
/// </summary>
public sealed class PanelRenderer : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Let mouse clicks pass straight through to whatever is behind.
    /// </summary>
    /// <remarks>
    /// The panel covers the whole glasses' display, and the glasses'
    /// display is a secondary monitor — the user is not looking at it
    /// with a mouse.  Without this, a stray click over the glasses'
    /// screen would land on our window instead of on the app behind
    /// it, and (with WS_EX_NOACTIVATE) the click would be swallowed
    /// entirely.
    ///
    /// NOTE: this flag does NOT make the window click-through.  It is
    /// a painting-order flag: a layered window hit-tests by alpha, and
    /// WS_EX_TRANSPARENT makes it transparent to the hit-test walk.
    /// Our panel is not layered — a D3D11 flip-model swapchain cannot
    /// be — so the panel has always been receiving WM_MOUSEMOVE,
    /// WM_LBUTTONDOWN and WM_SETCURSOR.  That is exactly what we
    /// want now: the mouse messages are the input channel for the
    /// virtual cursor.
    /// </remarks>
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Keep this window out of every OS capture surface.
    /// </summary>
    /// <remarks>
    /// If the user ever puts Windows into "clone" display mode, the
    /// glasses' display mirrors the primary one, and a capture of the
    /// primary display would then include our own panel - an optical
    /// feedback loop that stalls the GPU.  Excluding the window
    /// outright removes the possibility.
    /// </remarks>
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const string WindowClassName = "RokidDiyStationPanel";

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly object _lock = new();
    private IntPtr _hwnd;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private ID3D11RenderTargetView? _backBuffer;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;

    // --- The virtual cursor ------------------------------------------
    private ID3D11Buffer? _cursorVertexBuffer;
    private ID3D11PixelShader? _cursorPixelShader;
    private ID3D11ShaderResourceView? _cursorSrv;
    private ID3D11BlendState? _cursorBlendState;
    private ID3D11DepthStencilState? _cursorDepthState;
    private ID3D11Texture2D? _cursorTexture;

    /// <summary>
    /// The virtual cursor the renderer draws.
    /// </summary>
    /// <remarks>
    /// The renderer only READS it — it draws the cursor at whatever
    /// position the cursor currently holds.  The cursor's position is
    /// owned by <see cref="SpatialPointerFeedback"/>, which reads the
    /// OS cursor and maps it into the panel's texture space.
    /// </remarks>
    public VirtualCursor? Cursor
    {
        get => _cursor;
        set => _cursor = value;
    }

    private VirtualCursor? _cursor;

    private uint _backBufferWidth;
    private uint _backBufferHeight;
    private WaitHandle? _frameLatencyWaitable;

    /// <summary>
    /// The width of ONE eye's viewport, in back-buffer pixels.
    /// </summary>
    /// <remarks>
    /// In the glasses' side-by-side 3D mode the back buffer is
    /// 3840x1080 and the bridge chip splits it at the halfway point,
    /// sending each half to one eye.  In the plain 2D mode the buffer
    /// is 1920x1080 and the bridge clones it to both eyes.
    /// </remarks>
    private uint _eyeViewportWidth;

    private volatile bool _running;
    private int _diagFrames;
    private DisplayTarget? _display;
    private SpatialAnchorService? _anchor;
    private volatile ID3D11ShaderResourceView? _panelSrv;

    /// <summary>Raised on the render thread when a fatal error occurs.</summary>
    public event Action<string>? OnError;

    /// <summary>Raised on the render thread with the measured frame rate.</summary>
    public event Action<double>? OnFrameRate;

    /// <summary>
    /// The texture to display on the panel, set from outside the render
    /// thread.  Ownership stays with the caller.
    /// </summary>
    public ID3D11ShaderResourceView? PanelTexture
    {
        get => _panelSrv;
        set => _panelSrv = value;
    }

    /// <summary>
    /// Called on the RENDER THREAD, just before each frame is drawn.
    /// </summary>
    /// <remarks>
    /// This is where the capture pulls its newest frame.
    ///
    /// It has to be HERE, on the render thread, because the copy from
    /// the captured surface into our own texture is issued on the
    /// device's immediate context - and an ID3D11DeviceContext is not
    /// thread-safe.  When the capture ran on a separate thread, its
    /// CopyResource calls interleaved with the renderer's draw calls
    /// on the same context.  The symptom was baffling: the capture
    /// reported a perfect, live frame, the renderer's frame rate was
    /// 60 fps, and the panel was completely black.
    ///
    /// The callback is expected to be cheap and non-blocking.  If it
    /// throws, the frame is simply drawn without new content rather
    /// than taking the render loop down.
    /// </remarks>
    public Action? BeforeDraw { get; set; }

    /// <summary>True once the D3D pipeline is up.</summary>
    public bool IsReady => _device is not null;

    /// <summary>The D3D device, so the capture pipeline can share it.</summary>
    public ID3D11Device? Device => _device;

    /// <summary>The render window's handle, once it exists.</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>
    /// The panel window's message handler needs no input hook.
    /// </summary>
    /// <remarks>
    /// The window procedure used to forward the mouse messages to a
    /// WH_MOUSE_LL tracker.  That is gone: the virtual display is a
    /// real Windows display, so the OS delivers the clicks itself, and
    /// the only thing we needed the messages for was the old
    /// differential cursor mapping, which the absolute mapping in
    /// <see cref="SpatialPointerFeedback"/> replaces.
    ///
    /// What the window procedure still handles is the display topology
    /// (WM_DISPLAYCHANGE / WM_DEVICECHANGE), which is about keeping
    /// the window on the glasses - not about input.
    /// </remarks>

    /// <summary>
    /// Ask the render thread to measure how much of the panel is lit,
    /// and return the result.
    /// </summary>
    /// <remarks>
    /// The measurement has to happen ON the render thread, between the
    /// draw and the present.  A flip-model swapchain's back buffer
    /// contents are undefined once <c>Present</c> has been called — the
    /// driver is free to discard them — so reading the buffer from
    /// another thread after the fact returns garbage (measured: 0 %
    /// coverage on a perfectly good frame).
    ///
    /// The render thread therefore does the copy into a staging texture
    /// while it still owns the back buffer, and this method just waits
    /// for the answer.
    /// </remarks>
    public float MeasurePanelCoverage()
    {
        // Wait for the render thread to answer the request we are
        // about to make.  The generation counter tells us which
        // answer we are getting: the render thread bumps it once per
        // completed measurement, so a stale result from an earlier
        // request cannot be mistaken for this one.
        var startGeneration = _coverageGeneration;
        _coverageRequested = true;

        var deadline = Environment.TickCount64 + 3000;
        while (_coverageGeneration == startGeneration && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

        if (_coverageGeneration == startGeneration)
        {
            // The render thread never answered — it is not running, or
            // it is wedged.
            return -1f;
        }

        return _coverageResult;
    }

    /// <summary>The most recent coverage fraction, or -1 if never measured.</summary>
    private volatile float _coverageResult = -1f;

    /// <summary>Set by the caller to request a measurement.</summary>
    private volatile bool _coverageRequested;

    /// <summary>Bumped by the render thread once per completed measurement.</summary>
    private volatile int _coverageGeneration;

    public void Start(SpatialAnchorService anchor, DisplayTarget display)
    {
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;
            _anchor = anchor;
            _display = display;
            _cts = new CancellationTokenSource();
            _running = true;
            _thread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "PanelRenderer",
                Priority = ThreadPriority.Highest,
            };
            _thread.Start();
        }
    }    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _cts?.Cancel();
        }
    }

    /// <summary>Re-position the window when the monitor layout changes.</summary>
    public void Reposition(DisplayTarget display)
    {
        lock (_lock) _display = display;
        if (_hwnd != IntPtr.Zero) PositionWindow(_hwnd, display);
    }

    private void RenderLoop()
    {
        var token = _cts!.Token;
        try
        {
            CreateWindowAndDevice();
            if (!token.IsCancellationRequested) FrameLoop(token);
        }
        catch (Exception ex)
        {
            _running = false;
            // The full exception, not just its message: a D3D failure
            // says nothing useful without the frame that produced it.
            OnError?.Invoke($"Renderer failed: {ex}");
        }
        finally
        {
            Cleanup();
        }
    }

    // -----------------------------------------------------------------
    // Window creation
    // -----------------------------------------------------------------

    private void CreateWindowAndDevice()
    {
        var display = _display ?? throw new InvalidOperationException("No display selected.");

        // Register the window class on the render thread, so the window
        // belongs to it.  Without a registered class CreateWindowEx
        // fails with a bogus error code.
        //
        // The class gets a custom window procedure so the renderer can
        // see WM_DISPLAYCHANGE and WM_DEVICECHANGE.  Those messages
        // have to be handled INSIDE the message handler: by the time
        // DWM has finished relaying out the desktop after a display
        // disconnect, it has already moved our fullscreen window onto
        // the primary display.  See DisplayGuard for the details.
        RegisterWindowClass();

        // Create the window on the render thread, so it belongs to it.
        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            WindowClassName,
            "Rokid DiyStation Panel",
            WS_POPUP | WS_VISIBLE,
            display.Bounds.X, display.Bounds.Y,
            display.Bounds.Width, display.Bounds.Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()}).");

        // Keep the panel out of screen captures, so a clone-mode display
        // arrangement cannot feed our own output back into itself.
        NativeMethods.SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

        PositionWindow(_hwnd, display);

        // Ask to be told when the pointer leaves, so the virtual cursor
        // can go dormant and the OS arrow can come back on the other
        // display.
        NativeMethods.TrackMouseEvent(_hwnd, leave: true);

        // Keep the glasses' display awake while we are rendering.
        NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS
            | NativeMethods.ES_DISPLAY_REQUIRED
            | NativeMethods.ES_SYSTEM_REQUIRED);

        // --- Device ---------------------------------------------------
        // BgraSupport is required so that the same device can be
        // wrapped in a WinRT IDirect3DDevice, which is what the
        // Windows.Graphics.Capture frame pool needs.
        //
        // The device must also live on the adapter that DRIVES the
        // glasses' display.  On a hybrid-graphics laptop the USB-C
        // port is usually wired to the iGPU while the dGPU is faster;
        // creating the device on the wrong adapter forces a
        // cross-adapter blit every frame and adds 8-16 ms.
        _device = CreateDeviceForOutput(display);

        if (_device is null)
            throw new InvalidOperationException("D3D11CreateDevice returned no device.");

        _context = _device.ImmediateContext;

        // --- Swapchain ------------------------------------------------
        using var factory = CreateFactory();

        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)display.Bounds.Width,
            Height = (uint)display.Bounds.Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription { Count = 1 },
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, swapChainDesc, null, null);

        // The waitable-object swapchain lives on IDXGISwapChain2.
        _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        _swapChain2.MaximumFrameLatency = 1;

        var waitable = _swapChain2.FrameLatencyWaitableObject;
        if (waitable != IntPtr.Zero)
        {
            // The swapchain's waitable object is a real Win32 event
            // handle, so we can wait on it directly.
            var handle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(waitable, false);
            _frameLatencyWaitable = new AutoResetEvent(false);
            _frameLatencyWaitable.SafeWaitHandle = handle;
        }

        CreateBackBuffer();
        CreatePipeline();
    }

    private static void RegisterWindowClass()
    {
        // The custom window procedure is what lets the renderer see
        // WM_DISPLAYCHANGE and WM_DEVICECHANGE.  The delegate has to
        // stay alive for as long as the window class does, because the
        // class holds a raw pointer to it — hence the static field.
        _windowProc ??= PanelWindowProc;
        NativeMethods.RegisterClass(WindowClassName, _windowProc);
    }

    /// <summary>
    /// Keeps the window procedure delegate alive.
    /// </summary>
    /// <remarks>
    /// The window class stores a raw function pointer to the delegate.
    /// If the delegate were collected, that pointer would dangle and
    /// the next message dispatch would jump into freed memory.  A
    /// static reference is the simplest way to keep it alive for the
    /// lifetime of the process.
    /// </remarks>
    private static NativeMethods.WindowProc? _windowProc;

    /// <summary>
    /// The panel window's message handler.
    /// </summary>
    /// <remarks>
    /// Three things matter here:
    ///
    ///  * WM_MOUSEACTIVATE — the user clicks on the glasses' display.
    ///   Returning MA_NOACTIVATE stops Windows from giving our window
    ///   the focus, which would pull the user out of whatever they
    ///   were typing in.
    ///
    ///  * WM_SETCURSOR — the moment the pointer is over our window,
    ///   Windows asks us what shape the cursor should be.  Answering
    ///   with SetCursor(NULL) removes the OS arrow from the glasses'
    ///   display, and ONLY from that display: the arrow lives on the
    ///   GPU's hardware cursor plane of whichever output the pointer
    ///   is on, so the moment the pointer crosses onto the XP-Pen,
    ///   that display's own window sets the arrow again and it comes
    ///   back instantly.  The mouse is still fully tracked while
    ///   hidden, which is what the virtual cursor needs.
    ///
    ///  * WM_DISPLAYCHANGE / WM_DEVICECHANGE — the glasses were
    ///   unplugged or re-plugged.  The check has to happen HERE,
    ///   synchronously, because DWM relocates windows on a vanished
    ///   display to (0, 0) on the primary display as part of handling
    ///   this message.  By the time a timer or a poll noticed, our
    ///   borderless 1920x1080 topmost window would already be
    ///   covering the user's laptop screen.
    ///
    /// The mouse messages are deliberately not handled: the pointer
    /// over the glasses' display is not the pointer over the virtual
    /// display the panel shows, and the OS handles the interaction
    /// with the latter on its own.
    /// </remarks>
    private static IntPtr PanelWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                return NativeMethods.MA_NOACTIVATE;

            case NativeMethods.WM_SETCURSOR:
                // Hide the OS arrow over the glasses' display.
                //
                // SetCursor(NULL) removes the cursor from the screen
                // but leaves the mouse fully tracked: GetCursorPos
                // keeps working and the mouse messages keep arriving.
                // We are not disabling the mouse - we are replacing
                // the arrow with our own 3D cursor on the panel.
                NativeMethods.SetCursor(IntPtr.Zero);
                return new IntPtr(1); // TRUE: we set the cursor.

            case NativeMethods.WM_MOUSEMOVE:
            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_MBUTTONDOWN:
            case NativeMethods.WM_MBUTTONUP:
            case NativeMethods.WM_MOUSEWHEEL:
            case NativeMethods.WM_MOUSELEAVE:
                // Deliberately NOT handled.
                //
                // The mouse messages that arrive here are the pointer
                // moving over the GLASSES' display, which is where our
                // 3D panel is drawn - not over the virtual display the
                // panel SHOWS.  The real interaction happens on the
                // virtual display, where the OS delivers it natively.
                //
                // Forwarding these to an input hook used to be how the
                // cursor was tracked.  Now the cursor is read from the
                // OS's own cursor position, so there is nothing to do
                // with them.
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
                // The display topology changed.  This is the message
                // DWM uses to relocate windows off a vanished
                // display, so the window has to be hidden NOW —
                // synchronously, inside the handler — or it ends up
                // covering the user's primary display.
                //
                // The follow-up check then decides whether the glasses
                // are still there and re-shows the window if they are.
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                ThreadPool.QueueUserWorkItem(_ => DisplayChanged?.Invoke());
                return IntPtr.Zero;

            case NativeMethods.WM_DEVICECHANGE:
                // A device was plugged in or removed.  This fires for
                // every USB event — including the IMU's own — so it
                // must NOT hide the window; a hide here would make the
                // panel flicker every time a device is touched.
                //
                // It is still worth a check, because the glasses'
                // re-enumeration usually comes with a display change
                // shortly afterwards.
                ThreadPool.QueueUserWorkItem(_ => DisplayChanged?.Invoke());
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Raised when the display configuration changed.  The subscriber
    /// is expected to re-check where the panel should be and re-show or
    /// re-hide it.
    /// </summary>
    /// <remarks>
    /// It is static because the window procedure is static: the
    /// window class holds a raw pointer to the delegate, and there is
    /// only ever one panel window in the process.
    /// </remarks>
    public static event Action? DisplayChanged;

    /// <summary>
    /// Create the D3D device on the adapter that drives the given
    /// display, falling back to the default hardware adapter.
    /// </summary>
    private static ID3D11Device? CreateDeviceForOutput(DisplayTarget display)
    {
        try
        {
            using var factory = CreateFactory();

            for (uint i = 0; ; i++)
            {
                var enumResult = factory.EnumAdapters(i, out var adapter);
                if (enumResult.Failure) break;
                if (adapter is null) break;

                using (adapter)
                {
                    for (uint j = 0; ; j++)
                    {
                        var outputResult = adapter.EnumOutputs(j, out var output);
                        if (outputResult.Failure) break;
                        if (output is null) break;

                        using (output)
                        {
                            var desc = output.Description;
                            // The output's device name is the GDI display
                            // name, e.g. "\\.\DISPLAY5".
                            if (!string.Equals(desc.DeviceName, display.DevicePath, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var hr = D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.BgraSupport,
                                null,
                                out var device);
                            if (hr.Failure) continue;
                            return device;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to the default adapter below.
        }

        // The overload that takes no adapter returns the device
        // directly; a null device means creation failed.
        return D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null);
    }

    private static IDXGIFactory2 CreateFactory()
    {
        var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        return factory;
    }

    private void PositionWindow(IntPtr hwnd, DisplayTarget display)
    {
        NativeMethods.SetWindowPos(
            hwnd, HWND_TOPMOST,
            display.Bounds.X, display.Bounds.Y,
            display.Bounds.Width, display.Bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void CreateBackBuffer()
    {
        if (_swapChain is null) return;

        var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferWidth = buffer.Description.Width;
        _backBufferHeight = buffer.Description.Height;

        // How wide is ONE eye?  In the side-by-side 3D mode the
        // bridge splits the buffer down the middle and each eye gets
        // a 1920x1080 half.
        _eyeViewportWidth = (uint)RokidDisplayProfile.EyeViewportWidth((int)_backBufferWidth);

        _backBuffer = _device!.CreateRenderTargetView(buffer, null);
        buffer.Dispose();

        // A depth buffer is not strictly needed (the panel is the only
        // geometry), but it keeps the pipeline well-formed and costs
        // little.
        var depthDesc = new Texture2DDescription
        {
            Width = _backBufferWidth,
            Height = _backBufferHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D16_UNorm,
            SampleDescription = new SampleDescription { Count = 1 },
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var depthTexture = _device.CreateTexture2D(depthDesc);
        _depthView = _device.CreateDepthStencilView(depthTexture, null);
    }

    // -----------------------------------------------------------------
    // Pipeline
    // -----------------------------------------------------------------

    private void CreatePipeline()
    {
        var vsBytes = LoadShader("RokidDiyStation.App.Spatial.Shaders.PanelVertex.cso");
        var psBytes = LoadShader("RokidDiyStation.App.Spatial.Shaders.PanelPixel.cso");

        _vertexShader = _device!.CreateVertexShader(vsBytes);
        _pixelShader = _device.CreatePixelShader(psBytes);

        var inputElements = new[]
        {
            new InputElementDescription
            {
                SemanticName = "POSITION",
                SemanticIndex = 0,
                Format = Format.R32G32B32_Float,
                Slot = 0,
                AlignedByteOffset = 0,
                Classification = InputClassification.PerVertexData,
                InstanceDataStepRate = 0,
            },
            new InputElementDescription
            {
                SemanticName = "TEXCOORD",
                SemanticIndex = 0,
                Format = Format.R32G32_Float,
                Slot = 0,
                AlignedByteOffset = 12,
                Classification = InputClassification.PerVertexData,
                InstanceDataStepRate = 0,
            },
        };

        _inputLayout = _device.CreateInputLayout(inputElements, vsBytes);

        // Constant buffer: 64 bytes of matrix + 16 bytes of UV transform.
        var cbDesc = new BufferDescription
        {
            ByteWidth = 80,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        };
        _constantBuffer = _device.CreateBuffer(cbDesc, IntPtr.Zero);

        // Vertex buffer: 6 vertices (two triangles), each 20 bytes.
        var vbDesc = new BufferDescription
        {
            ByteWidth = 6 * 20,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 20,
        };
        _vertexBuffer = _device.CreateBuffer(vbDesc, IntPtr.Zero);

        // The panel is a single-sided plane, but the user will sometimes
        // be looking at it from behind, so we cull nothing.
        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
            FrontCounterClockwise = false,
        });

        // Anisotropic filtering keeps the captured text legible when the
        // panel is seen at a grazing angle, which is the common case as
        // soon as the user looks away from it.
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0f,
            MaxAnisotropy = 16,
            ComparisonFunc = ComparisonFunction.Never,
            BorderColor = new Vortice.Mathematics.Color4(0, 0, 0, 0),
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        });

        CreateCursorPipeline();
    }

    /// <summary>
    /// The virtual cursor's own pipeline: a texture, a pixel shader
    /// without the limited-range clamp, a blend state and a depth
    /// state.
    /// </summary>
    /// <remarks>
    /// The cursor is drawn as a SECOND draw call in the same pass as
    /// the panel, not as a copy into the panel's texture.  Baking it
    /// into the captured bitmap would need a GPU copy every frame, and
    /// sampling a small cursor at 50 % scale blurs its edges.
    ///
    /// The depth state writes NO depth (the cursor must not occlude the
    /// panel), but it does TEST depth, so the cursor is hidden when it
    /// falls behind the panel's plane - which happens when the user
    /// turns far enough that the panel is edge-on.
    /// </remarks>
    private void CreateCursorPipeline()
    {
        var cursorBytes = LoadShader("RokidDiyStation.App.Spatial.Shaders.CursorPixel.cso");
        _cursorPixelShader = _device!.CreatePixelShader(cursorBytes);

        // Its own vertex buffer: 6 vertices, 20 bytes each, same
        // layout as the panel's.
        var vbDesc = new BufferDescription
        {
            ByteWidth = 6 * 20,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 20,
        };
        _cursorVertexBuffer = _device.CreateBuffer(vbDesc, IntPtr.Zero);

        // Standard alpha blending: the cursor texture has transparent
        // pixels around the arrow, and they must not darken the panel.
        //
        // The two-argument constructor sets the blend factors for every
        // render target and turns blending on, which is exactly what
        // the cursor needs.
        _cursorBlendState = _device.CreateBlendState(
            new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));

        // Depth TEST on, depth WRITE off.  The cursor is lifted 1.5 mm
        // off the panel's surface so it never z-fights with it.
        _cursorDepthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = new DepthStencilOperationDescription
            {
                StencilFailOp = StencilOperation.Keep,
                StencilDepthFailOp = StencilOperation.Keep,
                StencilPassOp = StencilOperation.Keep,
                StencilFunc = ComparisonFunction.Always,
            },
            BackFace = new DepthStencilOperationDescription
            {
                StencilFailOp = StencilOperation.Keep,
                StencilDepthFailOp = StencilOperation.Keep,
                StencilPassOp = StencilOperation.Keep,
                StencilFunc = ComparisonFunction.Always,
            },
        });

        // The cursor texture: a white arrow with a black outline,
        // generated in code so the app carries no image assets.
        using var bitmap = CursorTexture.Create();
        _cursorTexture = CreateTextureFromBitmap(bitmap);
        _cursorSrv = _device.CreateShaderResourceView(_cursorTexture);
    }

    /// <summary>
    /// Upload a 32-bit BGRA bitmap into a D3D11 texture.
    /// </summary>
    /// <remarks>
    /// The bitmap is locked and copied row by row.  System.Drawing's
    /// Format32bppArgb is BGRA in memory, which is exactly the
    /// B8G8R8A8_UNorm layout D3D wants, so no swizzle is needed.
    /// </remarks>
    private unsafe ID3D11Texture2D CreateTextureFromBitmap(System.Drawing.Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        // The texture has to be MAPPABLE, because that is how the
        // pixels get in.  A texture created with Usage.Default and
        // CpuAccessFlags.None cannot be mapped at all, and the Map
        // call fails with E_INVALIDARG - which killed the whole
        // renderer, cursor pipeline included, the first time it ran.
        //
        // D3D11_USAGE_DYNAMIC is the usage that gives us both: the
        // texture can be mapped for writing AND bound as a shader
        // resource.  A staging texture can be mapped but NOT bound -
        // it would have to be copied into a second resource, which is
        // more machinery for no benefit here.
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription { Count = 1 },
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };

        var device = _device!;
        var texture = device.CreateTexture2D(desc);

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var context = device.ImmediateContext;
            var mapped = context.Map(texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = (uint)(width * 4);
                var src = data.Scan0;
                for (var y = 0; y < height; y++)
                {
                    var row = (byte*)src + (long)y * data.Stride;
                    var dst = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
                    Buffer.MemoryCopy(row, dst, rowBytes, rowBytes);
                }
            }
            finally
            {
                context.Unmap(texture, 0);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return texture;
    }

    private static byte[] LoadShader(string resourceName)
    {
        var assembly = typeof(PanelRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader resource '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // -----------------------------------------------------------------
    // The frame loop
    // -----------------------------------------------------------------

    private void FrameLoop(CancellationToken token)
    {
        var fpsTimer = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;

        while (!token.IsCancellationRequested && _running)
        {
            // Pump the window's message queue.  The panel window lives
            // on THIS thread, so its messages are only dispatched when
            // this thread pumps — and the ones that matter are
            // WM_DISPLAYCHANGE and WM_DEVICECHANGE, which the display
            // guard depends on.  Without this the guard would never
            // fire, and the window would be relocated onto the primary
            // display when the glasses are unplugged.
            PumpMessages();

            // Wait for the flip so we present in step with the panel's
            // VBlank rather than free-running.
            if (_frameLatencyWaitable is not null)            {
                try
                {
                    // Wait with a timeout so a missed VBlank (the
                    // display was unplugged, or the driver stalled)
                    // cannot wedge the thread forever.
                    _frameLatencyWaitable.WaitOne(100);
                }
                catch
                {
                    // The handle was closed underneath us.
                    break;
                }
            }

            if (token.IsCancellationRequested) break;

            try
            {
                DrawFrame();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Draw failed: {ex.Message}");
                Thread.Sleep(50);
                continue;
            }

            frames++;
            if (fpsTimer.Elapsed.TotalSeconds >= 1.0)
            {
                var fps = frames / fpsTimer.Elapsed.TotalSeconds;
                OnFrameRate?.Invoke(fps);
                frames = 0;
                fpsTimer.Restart();
            }
        }
    }
    /// <summary>
    /// Dispatch any pending window messages.
    /// </summary>
    /// <remarks>
    /// The panel window is created on the render thread, so its
    /// messages are queued to that thread and only dispatched here.
    /// Without this pump the window never receives WM_DISPLAYCHANGE,
    /// and the display guard never runs.
    ///
    /// <c>PeekMessage</c> rather than <c>GetMessage</c>, because the
    /// latter blocks.  The loop is already paced by the swapchain's
    /// frame-latency wait, so a non-blocking poll is right.
    /// </remarks>
    private void PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }
    private void DrawFrame()
    {
        if (_context is null || _swapChain is null || _backBuffer is null) return;

        // Pull the newest captured frame.  This runs on the render
        // thread because the capture's GPU copy shares this device's
        // immediate context, and that context is not thread-safe.
        //
        // The callback sets PanelTexture, which is read a few lines
        // below - so by the time the draw happens, the panel's
        // texture is the frame that was just captured.
        try { BeforeDraw?.Invoke(); }
        catch (Exception ex)
        {
            OnError?.Invoke($"Capture update failed: {ex.Message}");
        }

        // Pure black: on the Micro-OLED panels a black pixel emits no
        // light, so the area around the panel is optically transparent
        // and the user sees the room through it.
        _context.ClearRenderTargetView(_backBuffer, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
        if (_depthView is not null)
            _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        var anchor = _anchor;
        if (anchor is not null && anchor.HasPanel)
        {
            var view = anchor.GetView();
            var corners = view.PanelCorners;

            if (corners.Length == 4)
            {
                var projection = anchor.GetProjection();

                // System.Numerics uses the ROW-VECTOR convention: a
                // point is transformed as v' = v * M, and a product
                // M1 * M2 applies M1 FIRST and M2 second.
                //
                // The full chain is world -> eye -> clip, so:
                //
                //     v_clip = v_world * view * projection
                //
                // The ORDER matters, and getting it wrong is invisible
                // in the easy cases and catastrophic in the hard ones.
                // The previous code built `projection * view`, which
                // applies the projection BEFORE the view transform.
                // At zero pitch that still produced a plausible-looking
                // panel; at a realistic reading pitch it threw the
                // panel to NDC y = 2.7 (twice the top of the viewport)
                // and depth z = -0.46 (behind the near plane), so it was
                // clipped away and the renderer drew nothing at all.
                //
                // UpdateConstants() transposes once when it uploads,
                // which converts the row-major System.Numerics layout
                // into the column-major layout HLSL expects.  That
                // single transpose is the only one needed; an extra
                // transpose here produced NDC coordinates of
                // ~(130000, 130000) and also drew nothing.
                var mvp = view.View * projection;

                UpdateVertices(corners);
                UpdateConstants(mvp);

                _context.IASetInputLayout(_inputLayout);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.OMSetRenderTargets(_backBuffer, _depthView);
                _context.RSSetState(_rasterizer);

                // The panel is drawn ONCE PER EYE.
                //
                // The glasses' bridge chip splits the back buffer down
                // the middle in the side-by-side 3D mode, and sends
                // each half to one eye.  So a single draw across the
                // whole 3840-wide buffer would put half the picture in
                // the left eye and half in the right - the user would
                // see two halves of one image, one per eye, and could
                // not fuse them.
                //
                // Instead each eye gets its own 1920x1080 viewport and
                // the SAME camera, so both eyes see the identical image
                // of the panel.  That is deliberate: the panel is a
                // flat plane at a fixed distance, and rendering it
                // with a real inter-pupillary offset would introduce
                // 74 pixels of uncrossed disparity at 2 m, which the
                // birdbath optics cannot converge on.  Zero disparity
                // also means the virtual cursor lands on the same pixel
                // in both eyes, so it stays crisp instead of doubled.
                //
                // The projection is unchanged: it is always the 16:9
                // frustum for ONE eye, which is why the content comes
                // out the right shape now - the earlier 2x horizontal
                // stretch was the 16:9 frustum being stretched across
                // the whole 32:9 buffer.
                //
                // The panel's own render state is re-applied inside the
                // loop, because the cursor's draw changes the pixel
                // shader, the shader resource, the blend state, the
                // depth state AND the vertex buffer.  Without
                // re-applying them, the SECOND eye's panel would be
                // drawn with the cursor's texture, the cursor's shader
                // and the cursor's six vertices - which is exactly
                // what happened: the right eye came out black,
                // carrying only a stray cursor triangle.
                var eyeWidth = _eyeViewportWidth;
                for (uint eye = 0; eye * eyeWidth < _backBufferWidth; eye++)
                {
                    var eyeX = eye * eyeWidth;
                    _context.RSSetViewport(
                        eyeX, 0f,
                        Math.Min(eyeWidth, _backBufferWidth - eyeX),
                        _backBufferHeight,
                        0f, 1f);

                    _context.OMSetBlendState(null);
                    _context.OMSetDepthStencilState(null, 0);
                    _context.IASetVertexBuffer(0, _vertexBuffer, 20, 0);
                    _context.VSSetShader(_vertexShader);

                    // The constant buffer holds the MVP matrix, and the
                    // VERTEX shader is what reads it:
                    //
                    //     output.position = mul(float4(position, 1.0f), mvp);
                    //
                    // So it has to be bound to the VERTEX stage.  Binding
                    // it only to the pixel stage - which is what this
                    // used to do - leaves the vertex shader reading an
                    // unbound register, which D3D11 defines as ZERO.  A
                    // zero matrix collapses the whole quad to a single
                    // degenerate point at the origin, so nothing is
                    // rasterised and the panel comes out completely
                    // black: a 0 % coverage reading, with the capture
                    // reporting a perfectly good frame right next to it.
                    //
                    // The pixel stage also needs the buffer, for the UV
                    // transform.
                    _context.VSSetConstantBuffer(0, _constantBuffer);
                    _context.PSSetShader(_pixelShader);
                    _context.PSSetConstantBuffer(0, _constantBuffer);
                    _context.PSSetSampler(0, _sampler);
                    _context.PSSetShaderResource(0, _panelSrv);

                    if (_panelSrv is not null)
                    {
                        _context.Draw(6, 0);
                    }

                    // The virtual cursor, drawn as a second quad in the
                    // same pass.  It has to come after the panel so it
                    // blends over the content.
                    DrawCursor(view.PanelPose, mvp);
                }
            }
        }

        // If a coverage measurement was requested, do it HERE, while
        // the render thread still owns the back buffer.  After
        // Present() the flip-model back buffer's contents are
        // undefined and the driver is free to discard them.
        if (_coverageRequested)
        {
            _coverageResult = MeasureCoverageOnRenderThread();
            _coverageRequested = false;
            _coverageGeneration++;
        }

        // The snapshot has the same constraint as the coverage
        // measurement: it must happen here, while the back buffer is
        // still valid.
        var snapshot = _snapshotRequest;
        if (snapshot is not null)
        {
            _snapshotRequest = null;
            SnapshotOnRenderThread(snapshot);
        }

        _swapChain.Present(1, 0);
    }

    /// <summary>
    /// The cursor's world size, in metres.
    /// </summary>
    /// <remarks>
    /// A standard Windows arrow subtends roughly 1 degree of visual
    /// angle at reading distance.  At the panel's default distance of
    /// 2 m, 1 degree is about 35 mm; we use a slightly smaller quad
    /// so the cursor does not dominate the content it is pointing at.
    /// </remarks>
    private const float CursorWidthMetres = 0.020f;
    private const float CursorHeightMetres = 0.026f;

    /// <summary>
    /// How far the cursor floats off the panel's surface, in metres.
    /// </summary>
    /// <remarks>
    /// 1.5 mm along the normal, towards the viewer.  Without it the
    /// cursor z-fights with the panel, which shows up as a flickering
    /// edge; with it, the cursor is unambiguously in front.
    /// </remarks>
    private const float CursorNormalOffset = 0.0015f;

    /// <summary>
    /// Draw the virtual cursor, if it is active.
    /// </summary>
    /// <param name="pose">The panel's pose.</param>
    /// <param name="mvp">The world-to-clip matrix.</param>
    private void DrawCursor(PanelPose pose, Matrix4x4 mvp)
    {
        var cursor = _cursor;
        if (cursor is null || !cursor.IsActive) return;
        if (_cursorVertexBuffer is null || _cursorSrv is null || _cursorPixelShader is null) return;

        var (u, v) = cursor.Position;

        // The cursor's centre on the panel's surface.
        var center = Core.Spatial.PanelPointer.FromUv(u, v, pose, CursorNormalOffset);

        // The quad is built in the VIEWER's axes.
        //
        // PanelPose.Right is cross(normal, up), which points to the
        // viewer's LEFT - deliberately, because a panel facing the
        // viewer shows its own right edge on the viewer's left.  So
        // the viewer's right is -Right.
        //
        // Getting this wrong is what produced the mirrored cursor: the
        // quad was built along +Right with the texture's u = 1 on
        // that edge, which put the texture's u = 0 on the viewer's
        // right and drew the arrow back-to-front.
        var viewerRight = -pose.Right;
        var up = Vector3.Normalize(pose.Up);

        var hx = viewerRight * (CursorWidthMetres * 0.5f);
        var hy = up * (CursorHeightMetres * 0.5f);

        // The arrow's HOT POINT is its tip, which in the texture is at
        // the top-left - i.e. at texture coordinates (0, 0) of the
        // arrow's own 64x64 box, but the box is not filled: the
        // polygon starts at (8, 6) and ends at (40, 32) in pixels.
        //
        // The cursor's position has to land on the TIP, not on the
        // corner of the quad, so the quad is offset by the tip's
        // offset within the texture.  Without this the cursor's hot
        // point is one full quad-half off from where the user is
        // pointing, which is the difference between clicking a button
        // and clicking the gap next to it.
        //
        // Tip offset, in the texture's own units (0..1 of the quad):
        var tipU = 8f / 64f;
        var tipV = 6f / 64f;

        // The quad's bottom-left corner in texture space, such that
        // the texture's (tipU, tipV) lands exactly on `center`.
        var origin = center - hx * tipU * 2f + hy * tipV * 2f;

        UpdateCursorVertices(origin, hx, hy);

        _context!.OMSetBlendState(_cursorBlendState);
        _context.OMSetDepthStencilState(_cursorDepthState, 0);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _cursorVertexBuffer, 20, 0);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_cursorPixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResource(0, _cursorSrv);
        _context.Draw(6, 0);
    }

    /// <summary>
    /// Write the cursor quad's six vertices.
    /// </summary>
    /// <remarks>
    /// Same vertex format as the panel's: position (3 floats) plus a
    /// texture coordinate (2 floats).
    ///
    /// The UVs run 0..1 across the quad, with (0,0) at the top-left of
    /// the arrow texture.  The panel's own UVs are flipped in V, so
    /// this one is not - it is a separate texture with its own
    /// orientation.
    ///
    /// <paramref name="origin"/> is the corner that carries the
    /// texture's (0,0) - the TOP-LEFT of the arrow.  The texture's u
    /// runs along +hx (the viewer's right) and its v runs along -hy
    /// (downwards), so the quad is built as two triangles:
    /// (0,0)-(1,0)-(1,1) and (0,0)-(1,1)-(0,1).
    /// </remarks>
    /// <param name="origin">The world position of the texture's (0,0) corner.</param>
    /// <param name="hx">Half the quad's width, along the viewer's right.</param>
    /// <param name="hy">Half the quad's height, along the panel's up.</param>
    private unsafe void UpdateCursorVertices(Vector3 origin, Vector3 hx, Vector3 hy)
    {
        var data = new float[]
        {
            origin.X, origin.Y, origin.Z, 0f, 0f,
            (origin + hx * 2f).X, (origin + hx * 2f).Y, (origin + hx * 2f).Z, 1f, 0f,
            (origin + hx * 2f - hy * 2f).X, (origin + hx * 2f - hy * 2f).Y, (origin + hx * 2f - hy * 2f).Z, 1f, 1f,

            origin.X, origin.Y, origin.Z, 0f, 0f,
            (origin + hx * 2f - hy * 2f).X, (origin + hx * 2f - hy * 2f).Y, (origin + hx * 2f - hy * 2f).Z, 1f, 1f,
            (origin - hy * 2f).X, (origin - hy * 2f).Y, (origin - hy * 2f).Z, 0f, 1f,
        };

        var mapped = _context!.Map(_cursorVertexBuffer!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (float* p = data)
            {
                var bytes = new ReadOnlySpan<byte>(p, data.Length * sizeof(float));
                bytes.CopyTo(new Span<byte>((void*)mapped.DataPointer, bytes.Length));
            }
        }
        finally
        {
            _context.Unmap(_cursorVertexBuffer!, 0);
        }
    }

    /// <summary>
    /// Fraction of the back buffer that is lit, measured on the render
    /// thread while the back buffer is still valid.
    /// </summary>
    private float MeasureCoverageOnRenderThread()
    {
        if (_context is null || _swapChain is null || _device is null) return -1f;

        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var desc = backBuffer.Description;

            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription { Count = 1 },
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopyResource(staging, backBuffer);

            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowPitch = mapped.RowPitch;
                var data = mapped.DataPointer;
                var width = (int)desc.Width;
                var height = (int)desc.Height;

                long lit = 0;
                var total = (long)width * height;

                unsafe
                {
                    for (var y = 0; y < height; y++)
                    {
                        var row = (byte*)data + (long)y * rowPitch;
                        for (var x = 0; x < width; x++)
                        {
                            // B8G8R8A8: any non-zero channel counts as
                            // lit.  The area around the panel is pure
                            // black (an unlit Micro-OLED pixel is
                            // optically transparent), so any deviation
                            // from black is the panel itself.
                            var p = row + (long)x * 4;
                            if (p[0] != 0 || p[1] != 0 || p[2] != 0) lit++;
                        }
                    }
                }

                return total > 0 ? (float)lit / total : 0f;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Coverage measurement failed: {ex.Message}");
            return -1f;
        }
    }

    /// <summary>
    /// Write the current back buffer out as a PNG.
    /// </summary>
    /// <remarks>
    /// The coverage measurement tells us how MUCH of the panel is lit,
    /// but not whether the content is the right way up or mirrored
    /// left-to-right — and both of those read as a perfectly healthy
    /// coverage number.  This is the objective way to check the
    /// orientation of what actually reaches the Micro-OLED.
    ///
    /// Like the coverage measurement, it has to run on the render thread
    /// while the back buffer is still valid, so the copy is requested
    /// and the result is collected on the next frame.
    /// </remarks>
    /// <param name="path">Where to write the PNG.</param>
    /// <returns>The path written, or null on failure.</returns>
    public string? SnapshotBackBuffer(string path)
    {
        var request = new SnapshotRequest(path);
        _snapshotRequest = request;
        var deadline = Environment.TickCount64 + 3000;
        while (!request.Completed && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
        return request.Error is null ? request.Path : null;
    }

    private sealed class SnapshotRequest
    {
        public SnapshotRequest(string path) { Path = path; }
        public string Path { get; }
        public bool Completed { get; set; }
        public string? Error { get; set; }
    }

    private volatile SnapshotRequest? _snapshotRequest;

    /// <summary>
    /// Copy the back buffer into a PNG, on the render thread.
    /// </summary>
    private unsafe void SnapshotOnRenderThread(SnapshotRequest request)
    {
        try
        {
            if (_context is null || _swapChain is null || _device is null)
            {
                request.Error = "No device.";
                return;
            }

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var desc = backBuffer.Description;

            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription { Count = 1 },
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopyResource(staging, backBuffer);

            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowPitch = mapped.RowPitch;
                var data = mapped.DataPointer;
                var width = (int)desc.Width;
                var height = (int)desc.Height;

                using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rect = new System.Drawing.Rectangle(0, 0, width, height);
                var bytes = new byte[width * height * 4];

                for (var y = 0; y < height; y++)
                {
                    var row = (byte*)data + (long)y * rowPitch;
                    for (var x = 0; x < width; x++)
                    {
                        var p = row + (long)x * 4;
                        // The back buffer is B8G8R8A8, so in memory the
                        // bytes are B, G, R, A.
                        //
                        // Format32bppArgb is ALSO stored as B, G, R, A
                        // (it is a little-endian ARGB), so the bytes are
                        // copied straight across.  Swizzling them here
                        // - writing R into the blue slot - makes every
                        // red pixel come out blue in the PNG, which
                        // looks like a rendering bug but is only a bug
                        // in this diagnostic readback.
                        var o = (y * width + x) * 4;
                        bytes[o + 0] = p[0];  // B
                        bytes[o + 1] = p[1];  // G
                        bytes[o + 2] = p[2];  // R
                        bytes[o + 3] = 255;   // A
                    }
                }

                var bitmapData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(bytes, 0, bitmapData.Scan0, bytes.Length);
                }
                finally
                {
                    bmp.UnlockBits(bitmapData);
                }

                bmp.Save(request.Path, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        catch (Exception ex)
        {
            request.Error = ex.Message;
            OnError?.Invoke($"Snapshot failed: {ex.Message}");
        }
        finally
        {
            request.Completed = true;
        }
    }

    private unsafe void UpdateVertices(Vector3[] corners)
    {
        // Bottom-left, bottom-right, top-right, top-left, matching the
        // order PanelPose.Corners() returns.
        //
        // Two triangles: (0,1,2) and (0,2,3).
        //
        // The UVs are mirrored in U.  PanelPose.Corners() returns
        // corner 0 as "Centre - hx - hy", where hx runs along
        // PanelPose.Right.  But Right is cross(normal, up), and with
        // the normal pointing back at the viewer that direction is the
        // viewer's LEFT — so corner 0 actually lands at POSITIVE
        // screen X (the right-hand side of the panel as the user sees
        // it).  Verified by projecting the corners: with the corrected
        // matrix chain, corner 0 projects to ndc.x = +0.481 and corner
        // 1 to ndc.x = -0.481.
        //
        // So the texture's u = 0 (its left edge) belongs on corner 1,
        // not corner 0.  Getting this wrong mirrors the captured
        // window left-to-right.
        //
        // The V coordinate is flipped (v = 1 at the bottom) so the
        // captured bitmap comes out the right way up rather than
        // upside-down.
        var data = new float[]
        {
            corners[0].X, corners[0].Y, corners[0].Z, 1f, 1f,
            corners[1].X, corners[1].Y, corners[1].Z, 0f, 1f,
            corners[2].X, corners[2].Y, corners[2].Z, 0f, 0f,

            corners[0].X, corners[0].Y, corners[0].Z, 1f, 1f,
            corners[2].X, corners[2].Y, corners[2].Z, 0f, 0f,
            corners[3].X, corners[3].Y, corners[3].Z, 1f, 0f,
        };

        var mapped = _context!.Map(_vertexBuffer!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (float* p = data)
            {
                var bytes = new ReadOnlySpan<byte>(p, data.Length * sizeof(float));
                bytes.CopyTo(new Span<byte>((void*)mapped.DataPointer, bytes.Length));
            }
        }
        finally
        {
            _context.Unmap(_vertexBuffer!, 0);
        }
    }

    private void UpdateConstants(Matrix4x4 mvp)
    {
        var data = new float[20];
        // Matrix4x4's fields are laid out row-major; D3D wants
        // column-major, which is the transpose.
        var t = Matrix4x4.Transpose(mvp);
        data[0]  = t.M11; data[1]  = t.M12; data[2]  = t.M13; data[3]  = t.M14;
        data[4]  = t.M21; data[5]  = t.M22; data[6]  = t.M23; data[7]  = t.M24;
        data[8]  = t.M31; data[9]  = t.M32; data[10] = t.M33; data[11] = t.M34;
        data[12] = t.M41; data[13] = t.M42; data[14] = t.M43; data[15] = t.M44;
        // uvTransform: identity scale, zero offset.
        data[16] = 1f; data[17] = 1f; data[18] = 0f; data[19] = 0f;

        _context!.UpdateSubresource(data, _constantBuffer!, 0, 0, 0, null);
    }

    // -----------------------------------------------------------------
    // Teardown
    // -----------------------------------------------------------------

    private void Cleanup()
    {
        _cursorSrv?.Dispose();
        _cursorSrv = null;
        _cursorTexture?.Dispose();
        _cursorTexture = null;
        _cursorBlendState?.Dispose();
        _cursorBlendState = null;
        _cursorDepthState?.Dispose();
        _cursorDepthState = null;
        _cursorPixelShader?.Dispose();
        _cursorPixelShader = null;
        _cursorVertexBuffer?.Dispose();
        _cursorVertexBuffer = null;
        _sampler?.Dispose();
        _sampler = null;
        _rasterizer?.Dispose();
        _rasterizer = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _depthView?.Dispose();
        _depthView = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain2?.Dispose();
        _swapChain2 = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;

        _frameLatencyWaitable?.Dispose();
        _frameLatencyWaitable = null;

        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        var thread = _thread;
        if (thread is not null && thread.IsAlive)
            thread.Join(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
