// SPDX-License-Identifier: MIT
//
// Probe: which of the intended hotkey combinations can actually be
// registered on this machine?
//
// RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED when
// another process - or the OS itself - already owns a combination.
// On this machine, Ctrl+Shift+Win+Alt+Space turned out to be owned,
// so the app needs to know which alternatives are free before it
// promises a shortcut.
//
// The OS owns some combinations outright: Win+Space switches the
// input language, Win+Ctrl+Space is the emoji picker, and so on.
using System;
using System.Runtime.InteropServices;

namespace ProbeHotkeySlots;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private static int Main()
    {
        Console.WriteLine("=== hotkey slot probe ===");
        Console.WriteLine();
        Console.WriteLine("F = free, X = already owned");
        Console.WriteLine();

        var all = MOD_CTRL | MOD_SHIFT | MOD_ALT | MOD_WIN;

        var keys = new (string Name, uint Vk)[]
        {
            ("Space", 0x20), ("C", 0x43), ("R", 0x52), ("P", 0x50),
            ("H", 0x48), ("S", 0x53), ("O", 0x4F), ("G", 0x47),
            ("F1", 0x70), ("F2", 0x71), ("F12", 0x7B),
        };

        foreach (var (name, vk) in keys)
        {
            var ok = RegisterHotKey(IntPtr.Zero, 9900, all, vk);
            if (ok) UnregisterHotKey(IntPtr.Zero, 9900);
            Console.WriteLine($"  Ctrl+Shift+Win+Alt+{name,-6} {(ok ? "F (free)" : "X (owned)")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== with Shift dropped (Ctrl+Win+Alt) ===");
        var three = MOD_CTRL | MOD_ALT | MOD_WIN;
        foreach (var (name, vk) in keys)
        {
            var ok = RegisterHotKey(IntPtr.Zero, 9901, three, vk);
            if (ok) UnregisterHotKey(IntPtr.Zero, 9901);
            Console.WriteLine($"  Ctrl+Win+Alt+{name,-6} {(ok ? "F (free)" : "X (owned)")}");
        }

        return 0;
    }
}
