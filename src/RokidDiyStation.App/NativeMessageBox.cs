// SPDX-License-Identifier: MIT
//
// The native message box.
//
// WHY NOT WPF's MessageBox
// ------------------------
// The startup gate runs BEFORE the WPF Application exists - there is
// no dispatcher, no visual tree and no message pump.  WPF's
// MessageBox.Show would have to create a window of its own, and a
// WPF window created before Application.Run() is exactly the kind of
// thing that never paints: the tray app's own comment says the
// initial window show is deferred to Application.Startup for that
// reason.
//
// The native box has none of that trouble.  It is a plain Win32
// dialog, it always appears, and it is modal to the calling thread.
//
// THE OWNER WINDOW
// ----------------
// The panel window is a fullscreen, topmost, borderless window on the
// glasses' display.  A dialog with no owner could end up BEHIND it,
// on a display the user is not looking at, and the app would look
// like it had hung.  Passing the foreground window as the owner
// keeps the dialog in front of whatever the user was doing.
//
// The owner is looked up at the moment of the call, so the dialog
// lands on the thread that shows it.
using System.Runtime.InteropServices;

namespace RokidDiyStation.App;

/// <summary>
/// A thin wrapper over the Win32 message box.
/// </summary>
internal static class NativeMessageBox
{
    public const uint IconError = 0x00000010;
    public const uint IconWarning = 0x00000030;
    public const uint IconInformation = 0x00000040;

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Show a modal message box.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="caption">The window title.</param>
    /// <param name="icon">One of the icon constants.</param>
    public static void Show(string text, string caption, uint icon)
    {
        var owner = GetForegroundWindow();

        var style = MB_OK | MB_TOPMOST | MB_SETFOREGROUND;
        if (icon == IconWarning) style |= MB_ICONWARNING;

        MessageBoxW(owner, text, caption, style);
    }
}
