// SPDX-License-Identifier: MIT
//
// Tiny global hotkey helper for the tray app.  Wraps the Win32
// RegisterHotKey / UnregisterHotKey API and pumps WM_HOTKEY on a
// hidden message-only window.  H.NotifyIcon.Wpf doesn't ship its
// own hotkey component, and pulling in another NuGet package for
// three hotkeys would be overkill.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RokidDiyStation.App;

public sealed class HotkeyBinding : IDisposable
{
    public int Id { get; }
    public uint Modifiers { get; }
    public uint Key { get; }
    public event Action? Pressed;

    private bool _registered;
    private readonly HwndSource _source;

    public HotkeyBinding(int id, uint modifiers, uint key, Window hostWindow)
    {
        Id = id;
        Modifiers = modifiers;
        Key = key;
        var helper = new WindowInteropHelper(hostWindow);
        _source = HwndSource.FromHwnd(helper.Handle) ?? throw new InvalidOperationException("Window handle not ready");
        _source.AddHook(WndProc);
    }

    public bool Register()
    {
        if (_registered) return true;
        _registered = NativeMethods.RegisterHotKey(_source.Handle, Id, Modifiers, Key);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            try { Pressed?.Invoke(); } catch { }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_NONE  = 0x0000;
    public const uint MOD_ALT   = 0x0001;
    public const uint MOD_CTRL  = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN   = 0x0008;
}
