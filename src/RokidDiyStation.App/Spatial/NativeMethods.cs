// SPDX-License-Identifier: MIT
//
// The small pile of Win32 calls the renderer needs.
using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

public static class NativeMethods
{
    // -----------------------------------------------------------------
    // Window creation
    // -----------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string? lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The window procedure used by the panel window.
    /// </summary>
    /// <remarks>
    /// A window class MUST have a non-NULL lpfnWndProc.  Passing null
    /// makes CreateWindowEx succeed but the first message dispatch
    /// jumps to a null address, which kills the process with
    /// 0xC0000409 (STATUS_STACK_BUFFER_OVERRUN / fast-fail).
    ///
    /// DefWindowProc handles everything the panel does not care about.
    /// The panel is WS_POPUP with no caption and never takes focus, so
    /// the only messages it really sees are WM_PAINT and WM_SIZE.
    /// </remarks>
    public delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static WindowProc? _defaultWindowProc;

    /// <summary>
    /// Register a window class whose class name is supplied separately.
    /// </summary>
    /// <remarks>
    /// WNDCLASSEXW's two string members are plain LPCWSTR pointers,
    /// not inline buffers, so they cannot be marshalled as managed
    /// strings — a struct with string fields is not a blittable
    /// unmanaged structure and Marshal.SizeOf throws.  We therefore
    /// keep the struct pointer-only and marshal the class name by
    /// hand.
    /// </remarks>
    public static ushort RegisterClass(string className, WindowProc? windowProc = null)
    {
        var namePtr = Marshal.StringToHGlobalUni(className);
        // Keep the delegate alive for the lifetime of the process: the
        // window class holds a raw pointer to it, and a collected
        // delegate would leave that pointer dangling.
        _defaultWindowProc ??= DefWindowProc;
        var proc = windowProc ?? _defaultWindowProc;
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0008 /* CS_DBLCLKS */,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = namePtr,
            };
            return RegisterClassEx(ref wc);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    // -----------------------------------------------------------------
    // Display-change detection
    // -----------------------------------------------------------------

    /// <summary>
    /// The monitor a window is on, or NULL if it is on none of them.
    /// </summary>
    /// <remarks>
    /// <c>MONITOR_DEFAULTTONULL</c> (0) is the important part: it
    /// makes the call return NULL rather than falling back to the
    /// nearest monitor, which is exactly the "the display this window
    /// was on has gone away" case.
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;

    /// <summary>
    /// The rectangle of a monitor, in virtual-desktop coordinates.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RECT
    {
        public int left, top, right, bottom;

        public int Width => right - left;
        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// The rectangle of a window, in virtual-desktop coordinates.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    // -----------------------------------------------------------------
    // Window messages
    // -----------------------------------------------------------------

    /// <summary>The display resolution or topology changed.</summary>
    public const uint WM_DISPLAYCHANGE = 0x007E;

    /// <summary>A hardware device was plugged in or removed.</summary>
    public const uint WM_DEVICECHANGE = 0x0219;

    /// <summary>The mouse was pressed over a window that is not active.</summary>
    public const uint WM_MOUSEACTIVATE = 0x0021;

    /// <summary>Do not activate the window on a mouse click.</summary>
    public static readonly IntPtr MA_NOACTIVATE = new(3);

    /// <summary>Show the window.</summary>
    public const int SW_SHOW = 5;

    /// <summary>Hide the window.</summary>
    public const int SW_HIDE = 0;

    /// <summary>Do not change the owner window's Z order.</summary>
    public const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>Do not change the window's Z order.</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>Do not move the window.</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>Do not resize the window.</summary>
    public const uint SWP_NOSIZE = 0x0001;

    // -----------------------------------------------------------------
    // Mouse input
    // -----------------------------------------------------------------

    /// <summary>The mouse moved over the window.</summary>
    public const uint WM_MOUSEMOVE = 0x0200;

    /// <summary>The left mouse button went down.</summary>
    public const uint WM_LBUTTONDOWN = 0x0201;

    /// <summary>The left mouse button came up.</summary>
    public const uint WM_LBUTTONUP = 0x0202;

    /// <summary>The right mouse button went down.</summary>
    public const uint WM_RBUTTONDOWN = 0x0204;

    /// <summary>The right mouse button came up.</summary>
    public const uint WM_RBUTTONUP = 0x0205;

    /// <summary>The middle mouse button went down.</summary>
    public const uint WM_MBUTTONDOWN = 0x0207;

    /// <summary>The middle mouse button came up.</summary>
    public const uint WM_MBUTTONUP = 0x0208;

    /// <summary>The mouse wheel was turned.</summary>
    public const uint WM_MOUSEWHEEL = 0x020A;

    /// <summary>The window is asked to set the mouse cursor shape.</summary>
    public const uint WM_SETCURSOR = 0x0020;

    /// <summary>The mouse entered the window's client area.</summary>
    public const uint WM_MOUSEENTER = 0x02A3;

    /// <summary>The mouse left the window's client area.</summary>
    public const uint WM_MOUSELEAVE = 0x02A4;

    /// <summary>The left button is held down (wParam of a mouse message).</summary>
    public const int MK_LBUTTON = 0x0001;

    /// <summary>The right button is held down (wParam of a mouse message).</summary>
    public const int MK_RBUTTON = 0x0002;

    /// <summary>The middle button is held down (wParam of a mouse message).</summary>
    public const int MK_MBUTTON = 0x0010;

    /// <summary>The SHIFT key is held down.</summary>
    public const int MK_SHIFT = 0x0004;

    /// <summary>The CTRL key is held down.</summary>
    public const int MK_CONTROL = 0x0008;

    /// <summary>The ALT key is held down.</summary>
    public const int MK_ALT = 0x0020;

    /// <summary>
    /// Set the cursor shape for the current window.
    /// </summary>
    /// <remarks>
    /// Passing <see cref="IntPtr.Zero"/> removes the cursor from the
    /// screen, but the mouse is still tracked normally - GetCursorPos
    /// keeps working and raw input keeps flowing.  That is exactly what
    /// the virtual cursor needs: the physical mouse stays a real input
    /// device, we simply stop drawing the OS arrow over the glasses.
    ///
    /// The effect is per-display: the cursor image lives on the GPU's
    /// hardware cursor plane of whichever output the pointer is on, so
    /// the arrow reappears the instant the pointer crosses onto
    /// another screen.
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    /// <summary>The current cursor position, in virtual-desktop pixels.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>Virtual key codes we care about.</summary>
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;

    /// <summary>
    /// Whether a key is currently held down.
    /// </summary>
    /// <remarks>
    /// This queries the calling thread's key state, which is updated
    /// as the thread's message queue is pumped.  It is the same source
    /// Chromium itself consults, which is why it is the right way to
    /// build the modifier bitmask for a synthetic click.
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>
    /// Which monitor a virtual-desktop point is on.
    /// </summary>
    /// <param name="x">The point's X coordinate.</param>
    /// <param name="y">The point's Y coordinate.</param>
    /// <param name="hwnd">
    /// The window to fall back on when the point is on no monitor.
    /// </param>
    /// <param name="flags">Which monitor to pick when the point is on none.</param>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>Return the monitor nearest the given point.</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>A point in virtual-desktop coordinates.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// The X coordinate of a mouse message's lParam.
    /// </summary>
    public static int GetXLParam(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

    /// <summary>
    /// The Y coordinate of a mouse message's lParam.
    /// </summary>
    public static int GetYLParam(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    /// <summary>
    /// The signed wheel delta of a WM_MOUSEWHEEL message's wParam.
    /// </summary>
    /// <remarks>
    /// A multiple of 120 (WHEEL_DELTA) per detent.  Positive means the
    /// wheel was turned away from the user.
    /// </remarks>
    public static int GetWheelDelta(IntPtr wParam) => (short)((wParam.ToInt64() >> 16) & 0xFFFF);

    // -----------------------------------------------------------------
    // Mouse tracking
    // -----------------------------------------------------------------

    /// <summary>
    /// Ask for WM_MOUSELEAVE when the pointer leaves the window.
    /// </summary>
    /// <remarks>
    /// Without this, the panel window would never learn that the
    /// pointer had left the glasses' display — and that matters,
    /// because the virtual cursor has to be deactivated when the
    /// pointer goes back to the XP-Pen, and the OS arrow has to come
    /// back with it.
    ///
    /// The TRACKMOUSEEVENT structure's size varies with the platform
    /// (64 bytes on x64, 40 on x86), and the API that reports it takes
    /// a pointer whose first field IS the size.  So we probe: fill in a
    /// candidate size and see whether the system accepts it.
    /// </remarks>
    /// <param name="hwnd">The window to track.</param>
    /// <param name="leave">True to request WM_MOUSELEAVE.</param>
    public static void TrackMouseEvent(IntPtr hwnd, bool leave)
    {
        var size = (uint)GetTrackMouseEventSize();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // cbSize
            Marshal.WriteInt32(buffer, 0, (int)size);
            // dwFlags: TME_LEAVE
            Marshal.WriteInt32(buffer, 4, leave ? 0x00000002 : 0);
            // hwndTrack
            Marshal.WriteIntPtr(buffer, 8, hwnd);
            // dwHoverTime
            Marshal.WriteInt32(buffer, 16, 0);
            TrackMouseEventNative(buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEventNative(IntPtr lpTme, uint cbSize);

    /// <summary>
    /// The size of the TRACKMOUSEEVENT structure on this machine.
    /// </summary>
    private static int GetTrackMouseEventSize()
    {
        for (var size = 16; size <= 128; size += 8)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buffer, 0, size);
                if (TrackMouseEventNative(buffer, (uint)size))
                    return size;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return 64;
    }

    // -----------------------------------------------------------------
    // Message pump
    // -----------------------------------------------------------------

    /// <summary>
    /// A Windows message, as the message pump sees it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    /// <summary>
    /// Fetch the next message, without blocking.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    /// <summary>Dispatch a message to the window's procedure.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>Translate a message.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>
    /// Remove the message from the queue.
    /// </summary>
    /// <remarks>
    /// PM_REMOVE is 0x0001.  PM_NOREMOVE (0x0000) would leave the
    /// message in the queue, so the pump loop would dispatch the same
    /// message over and over and never terminate — which stalls the
    /// render thread completely.
    /// </remarks>
    public const uint PM_REMOVE = 0x0001;

    /// <summary>
    /// Keep this window out of every OS capture surface.
    /// </summary>
    /// <remarks>
    /// WDA_EXCLUDEFROMCAPTURE (0x00000011).  If the user ever puts
    /// Windows into "clone" display mode, the glasses' display mirrors
    /// the primary one, and a capture of the primary display would then
    /// include our own output — an optical feedback loop that stalls
    /// the GPU.  Excluding the window outright removes the possibility.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>
    /// Whether a window is minimised.
    /// </summary>
    /// <remarks>
    /// A minimised window stops being composited by DWM, so a
    /// Windows.Graphics.Capture of it freezes on its last frame.  The
    /// renderer shows an on-panel hint when this is true.
    /// </remarks>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    /// <summary>
    /// The title of a window, for the window picker's list.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// The class name of a window, used to filter out our own windows
    /// and other non-capturable surfaces.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // -----------------------------------------------------------------
    // Power management
    // -----------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Keeps the display and the system awake while the glasses are in
    /// use.  The glasses' DP bridge drops the display out of the
    /// desktop's power state aggressively, and the panel would go dark
    /// mid-session.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);

    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;

    // -----------------------------------------------------------------
    // Structs
    // -----------------------------------------------------------------

    /// <summary>
    /// The Win32 WNDCLASSEXW structure.
    /// </summary>
    /// <remarks>
    /// Two details bite here, and both were found by running the code:
    ///
    /// 1. Both of its string members are LPCWSTR POINTERS in the real
    ///    structure — they are not inline fixed-size buffers.  Declaring
    ///    them as managed strings would make this a non-blittable type
    ///    and Marshal.SizeOf would throw, so they are IntPtr here and
    ///    the class name is marshalled by hand in RegisterClass.
    ///
    /// 2. The lpfnWndProc member must NOT be annotated with
    ///    MarshalAs(UnmanagedType.FunctionPtr).  It is an IntPtr field,
    ///    and the annotation makes the struct non-blittable, which
    ///    again makes Marshal.SizeOf throw.  The annotation is only
    ///    meaningful for a real delegate-typed field.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }
}
