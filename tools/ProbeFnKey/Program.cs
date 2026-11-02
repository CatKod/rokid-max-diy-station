// SPDX-License-Identifier: MIT
//
// Probe: does the Fn key reach Windows as a key at all?
//
// WHY THIS PROBE EXISTS
// ---------------------
// The user asked for the hotkeys to change from Ctrl+<key> to
// Fn+<key>.  That request looks trivial and is not, because on most
// laptops the Fn key is NOT a key the operating system ever sees.
//
// The Fn key is handled by the laptop's EMBEDDED CONTROLLER (EC), a
// microcontroller in the keyboard's firmware.  On an ASUS TUF (the
// machine this was written on), pressing Fn on its own does nothing to
// the OS at all - the EC only intercepts Fn+<key> COMBINATIONS and
// turns them into the laptop's own functions (brightness, volume,
// the numeric keypad, Airplane mode, and so on).  The Fn+<key>
// combinations are usually CONFIGURED IN THE BIOS, and they are
// consumed by the EC before Windows sees them.
//
// So there are two questions this probe answers:
//
//   1. Does pressing Fn ALONE produce a key event in Windows?
//   2. If the user is asked to press Fn+C, what does Windows
//      actually receive - "Fn" and "C" separately, or a single
//      modified key, or nothing at all?
//
// The probe installs a low-level keyboard hook (WH_KEYBOARD_LL) and
// prints every key event it sees, then asks the user to press the
// combinations in question.
//
// WHY A LOW-LEVEL HOOK
// -------------------
// A global hotkey via RegisterHotKey cannot be used for the probe:
// RegisterHotKey needs a VIRTUAL KEY CODE for the modifier, and there
// is no VK code for Fn.  The hook sees the RAW scan codes, which is
// exactly what we need to find out whether the EC forwards Fn to the
// OS at all.
//
// This probe is a diagnostic tool, not part of the product.  The
// product does not install a low-level hook - the app's
// SpatialDisplayArchitectureTests enforce that.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProbeFnKey;

internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// The VK codes Windows knows for a modifier.  There is no
    /// "VK_FN" - that is the whole point of this probe.
    /// </summary>
    private static string DescribeVk(uint vk) => vk switch
    {
        0x10 => "VK_SHIFT",
        0x11 => "VK_CONTROL",
        0x12 => "VK_MENU (Alt)",
        0x5B => "VK_LWIN",
        0x5C => "VK_RWIN",
        0x14 => "VK_CAPITAL",
        0xA0 => "VK_LSHIFT",
        0xA1 => "VK_RSHIFT",
        0xA2 => "VK_LCONTROL",
        0xA3 => "VK_RCONTROL",
        0xA4 => "VK_LMENU",
        0xA5 => "VK_RMENU",
        0x13 => "VK_PAUSE",
        0x15 => "VK_SCROLL",
        0x1C => "VK_CONVERT",
        0x1D => "VK_NONCONVERT",
        0xDB => "VK_OEM_102 (the key next to left Shift)",
        _ => vk switch
        {
            >= 0x30 and <= 0x39 => $"digit '{vk - 0x30}'",
            >= 0x41 and <= 0x5A => $"letter '{(char)vk}'",
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            _ => $"VK 0x{vk:X2}",
        },
    };

    private static readonly List<string> Seen = new List<string>();

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var down = msg != WM_KEYUP;
                var injected = (info.flags & 0x10) != 0;

                var text = down
                    ? $"DOWN vk=0x{info.vkCode:X2} ({DescribeVk(info.vkCode)}) scan=0x{info.scanCode:X2}"
                    : $"UP   vk=0x{info.vkCode:X2} ({DescribeVk(info.vkCode)}) scan=0x{info.scanCode:X2}";
                if (injected) text += " [injected]";

                lock (Seen) Seen.Add(text);
                Console.WriteLine(text);
                Console.Out.Flush();
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static int Main()
    {
        Console.WriteLine("=== Fn key probe ===");
        Console.WriteLine();
        Console.WriteLine("This probe prints every key event Windows receives.");
        Console.WriteLine();
        Console.WriteLine("Please press, in order:");
        Console.WriteLine("  1. the Fn key ON ITS OWN");
        Console.WriteLine("  2. Fn + C");
        Console.WriteLine("  3. Ctrl + C          (for comparison - this must work)");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C in this console to finish.");
        Console.WriteLine();

        var module = GetModuleHandle(null);
        using var hook = new HookScope(SetWindowsHookEx(
            WH_KEYBOARD_LL, HookProc, module, 0));

        if (hook.Handle == IntPtr.Zero)
        {
            Console.WriteLine($"Could not install the hook: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        Console.WriteLine("Hook installed. Listening...");
        Console.WriteLine();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // The message loop is what keeps the hook alive.
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        lock (Seen)
        {
            foreach (var line in Seen) Console.WriteLine("  " + line);
        }
        return 0;
    }

    private sealed class HookScope : IDisposable
    {
        public IntPtr Handle { get; }
        public HookScope(IntPtr handle) => Handle = handle;
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) UnhookWindowsHookEx(Handle);
        }
    }
}
