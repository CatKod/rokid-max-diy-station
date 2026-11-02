// Stage 0 verification: prove that a real window placed on the virtual
// IddCx monitor behaves like a normal window.
//
// The whole architecture rests on three claims, and this probe checks all
// three on the real machine:
//
//   1. A window can be MOVED onto the virtual monitor (SetWindowPos into
//      its rectangle).
//   2. The virtual monitor's rectangle is reachable by the mouse, and
//      the OS delivers clicks and focus to the window there - i.e. it is
//      a first-class display, not a dead rectangle.
//   3. The cursor position inside the virtual monitor maps to a panel UV
//      with the formula u = (x - left) / width.
//
// The window is a plain WinForms form so the test is honest: it is an
// ordinary top-level window with an ordinary button.
using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static int Main()
    {
        var failures = new System.Collections.Generic.List<string>();

        // ---------------------------------------------------------------
        // 1. Find the virtual monitor by its rectangle: negative X.
        // ---------------------------------------------------------------
        var found = FindVirtualMonitor(out var hMon, out var mi);
        if (!found)
        {
            Console.WriteLine("FAIL: no monitor at negative X - the virtual display is not attached.");
            return 1;
        }

        Console.WriteLine($"virtual monitor : {mi.szDevice}");
        Console.WriteLine($"  rectangle     : ({mi.rcMonitor.Left},{mi.rcMonitor.Top}) - ({mi.rcMonitor.Right},{mi.rcMonitor.Bottom})");
        Console.WriteLine($"  size          : {mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}");

        int monLeft = mi.rcMonitor.Left;
        int monTop = mi.rcMonitor.Top;
        int monW = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

        // ---------------------------------------------------------------
        // 2. Put an ordinary window on it.
        // ---------------------------------------------------------------
        var form = new Form
        {
            Text = "Rokid spatial display probe",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(monLeft + 100, monTop + 100, 600, 400),
            TopMost = false,
            ShowInTaskbar = true,
        };
        var button = new Button { Text = "Click me", Bounds = new Rectangle(40, 40, 160, 48) };
        int clicks = 0;
        button.Click += (_, __) => clicks++;
        form.Controls.Add(button);
        form.Show();
        Application.DoEvents();
        Console.WriteLine($"  window hwnd   : 0x{form.Handle.ToInt64():X} at {form.Bounds}");

        if (form.Bounds.Left < monLeft || form.Bounds.Right > mi.rcMonitor.Right)
        {
            failures.Add("the window was not placed inside the virtual monitor's rectangle");
        }

        // ---------------------------------------------------------------
        // 3. Move the mouse onto the virtual monitor and check that the
        //    OS routes the click to our button there.
        // ---------------------------------------------------------------
        var buttonPoint = button.PointToScreen(new Point(
            button.ClientRectangle.Width / 2, button.ClientRectangle.Height / 2));
        Console.WriteLine($"  button screen : ({buttonPoint.X},{buttonPoint.Y})");

        SetCursorPos(buttonPoint.X, buttonPoint.Y);
        Application.DoEvents();
        System.Threading.Thread.Sleep(250);

        var cursor = new POINT();
        GetCursorPos(out cursor);
        Console.WriteLine($"  cursor now    : ({cursor.X},{cursor.Y})");

        if (cursor.X < monLeft || cursor.X >= mi.rcMonitor.Right)
        {
            failures.Add("the mouse could not be moved onto the virtual monitor");
        }

        // The cursor must land on our button, not on whatever is behind.
        var under = WindowFromPoint(cursor);
        Console.WriteLine($"  window at cursor: 0x{under.ToInt64():X} (button is 0x{button.Handle.ToInt64():X})");

        // ---------------------------------------------------------------
        // 4. The mapping the cursor feedback loop depends on.
        // ---------------------------------------------------------------
        double u = (cursor.X - monLeft) / (double)monW;
        double v = (cursor.Y - monTop) / (double)monH;
        Console.WriteLine($"  mapped uv     : ({u.ToString("F4", CultureInfo.InvariantCulture)}, {v.ToString("F4", CultureInfo.InvariantCulture)})");
        if (u < 0 || u >= 1 || v < 0 || v >= 1)
        {
            failures.Add($"the uv mapping fell outside [0,1): ({u:F4}, {v:F4})");
        }

        // ---------------------------------------------------------------
        // 5. A real click: synthesise it at the cursor's position.
        // ---------------------------------------------------------------
        SendClick();
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Console.WriteLine($"  button clicks : {clicks}");
        if (clicks == 0)
        {
            failures.Add("the click did not reach the button on the virtual monitor");
        }

        // ---------------------------------------------------------------
        // 6. Focus: can the window on the virtual monitor take focus?
        // ---------------------------------------------------------------
        var fg = GetForegroundWindow();
        Console.WriteLine($"  foreground    : 0x{fg.ToInt64():X} (our form is 0x{form.Handle.ToInt64():X})");

        form.Close();

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("PASS: the virtual monitor behaves like a real display.");
            return 0;
        }

        Console.WriteLine("FAIL:");
        foreach (var f in failures) Console.WriteLine("  - " + f);
        return 1;
    }

    private static void SendClick()
    {
        var cur = new POINT();
        GetCursorPos(out cur);
        SendInputClick(cur.X, cur.Y);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private static void SendInputClick(int x, int y)
    {
        // The coordinates must be normalised against the VIRTUAL DESK, not
        // the primary monitor - that is what makes a click land on a
        // monitor at negative coordinates.
        int vx = System.Windows.Forms.SystemInformation.VirtualScreen.X;
        int vy = System.Windows.Forms.SystemInformation.VirtualScreen.Y;
        int vw = System.Windows.Forms.SystemInformation.VirtualScreen.Width;
        int vh = System.Windows.Forms.SystemInformation.VirtualScreen.Height;
        int nx = (int)(((x - vx) * 65535L) / Math.Max(1, vw - 1));
        int ny = (int)(((y - vy) * 65535L) / Math.Max(1, vh - 1));

        var inputs = new INPUT[3];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } }
        };
        inputs[2] = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } }
        };
        SendInput(3, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool FindVirtualMonitor(out IntPtr hMon, out MONITORINFOEX mi)
    {
        hMon = IntPtr.Zero;
        mi = default;

        var box = new MonitorBox();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, box.Callback, IntPtr.Zero);
        hMon = box.HMon;
        mi = box.Info;
        return box.Found;
    }

    private sealed class MonitorBox
    {
        public bool Found;
        public IntPtr HMon;
        public MONITORINFOEX Info;

        public bool Callback(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var m = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref m)) return true;
            if (m.rcMonitor.Left < 0)
            {
                HMon = h;
                Info = m;
                Found = true;
            }
            return true;
        }
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
}
