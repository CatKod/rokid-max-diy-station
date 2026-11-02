// SPDX-License-Identifier: MIT
//
// Diagnostic: list every top-level window with its rectangle, so a
// window that was supposed to land on the virtual display at
// negative coordinates can be found (or proven not to be there).
//
// Usage:
//   dotnet run --project tools/ListWindows
using System.Text;
using System.Runtime.InteropServices;

internal static class Program
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly List<string> Lines = new();

    private static int Main()
    {
        EnumWindows((h, _) =>
        {
            GetWindowRect(h, out var r);
            GetWindowThreadProcessId(h, out var pid);
            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);

            Lines.Add(
                $"hwnd=0x{h.ToInt64():X} pid={pid,-6} vis={IsWindowVisible(h),-5} "
                + $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) "
                + $"w={r.Right - r.Left,-6} h={r.Bottom - r.Top,-6} "
                + $"title='{title}'");

            return true;
        }, IntPtr.Zero);

        Lines.Sort(StringComparer.Ordinal);

        Console.WriteLine($"=== {Lines.Count} top-level windows ===");
        foreach (var line in Lines) Console.WriteLine(line);
        return 0;
    }
}
