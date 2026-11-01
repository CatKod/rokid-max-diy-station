// SPDX-License-Identifier: MIT
//
// The physical keyboard, as seen from the panel.
//
// The mouse has a hook (PointerInput); the keyboard needs the same
// thing, for the same reason.
//
// WHY A HOOK AND NOT WINDOW MESSAGES
// ----------------------------------
// The panel window is WS_EX_NOACTIVATE and never takes focus, so it
// never receives WM_KEYDOWN.  WM_KEYDOWN is delivered to the FOCUSED
// window - and the focused window is whatever the user was typing
// into on the Artist 13.3, which must keep working normally.  So
// there is no window message to listen to.
//
// A WH_KEYBOARD_LL hook sees every keystroke on the machine.  Like
// the mouse hook, it FILTERS BY WHETHER THE PANEL IS ACTIVE: the
// keyboard is only captured while the pointer is over the glasses'
// display, which is the user's way of saying "I am working in the
// glasses now".  Outside that, every keystroke is passed straight
// down the chain, untouched.
//
// WHAT IS CAPTURED
// ----------------
// The hook gives us the scan code, the virtual key code and the
// extended flag.  From those we derive the DOM's `key` and `code`
// names (see VirtualKeyMap) and the character the key produced.
//
// The CHARACTER comes from the operating system, not from us:
// ToUnicodeEx translates a virtual key plus the current keyboard
// state into the character that the user's actual layout produces.
// That is what makes this work on a Vietnamese or German keyboard
// without any layout table in our code - and it also gets the shifted
// character right, which is the whole point of asking the OS rather
// than guessing.
//
// WHY THE CHARACTER IS RESOLVED ON THE HOOK'S THREAD
// ---------------------------------------------------
// The hook runs on the thread that installed it, and the callback
// runs on that thread too.  ToUnicodeEx is bit awkward to call from
// there - it mutates the keyboard state and wants a dead-key buffer -
// but the hook thread is the ONLY thread that calls it, so the
// keyboard state stays consistent.
//
// Ordering is therefore free: the character for a key press is
// resolved before the hook callback returns, so the press reaches the
// app before the release of that key, and the presses keep their
// order.
using System.Runtime.InteropServices;
using RokidDiyStation.Core.Spatial;

namespace RokidDiyStation.App.Spatial;

/// <summary>A key that was pressed or released.</summary>
/// <param name="VirtualKey">The Win32 virtual key code.</param>
/// <param name="ScanCode">The hardware scan code.</param>
/// <param name="Extended">Whether the key was reported with the extended flag.</param>
/// <param name="IsDown">True for a press, false for a release.</param>
/// <param name="IsAutoRepeat">True when the key is auto-repeating.</param>
public readonly record struct KeyEvent(
    int VirtualKey,
    int ScanCode,
    bool Extended,
    bool IsDown,
    bool IsAutoRepeat);

/// <summary>
/// Captures the physical keyboard while the pointer is over the
/// glasses' display, and resolves each keystroke into the shape the
/// DevTools protocol wants.
/// </summary>
public sealed class KeyboardInput : IDisposable
{
    // -----------------------------------------------------------------
    // Win32
    // -----------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookExNative(IntPtr hhk);

    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern IntPtr CallNextHookExNative(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetKeyStateNative(int nVirtKey);

    [DllImport("user32.dll", EntryPoint = "ToUnicodeEx")]
    private static extern int ToUnicodeExNative(
        uint wVirtKey, uint wScanCode,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 256)] byte[]? lpKeyState,
        [Out] char[] lpChar, int cchBuff, uint wFlags, IntPtr lpKeyStateEx);

    [DllImport("user32.dll", EntryPoint = "GetKeyboardState")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardStateNative(byte[] lpKeyState);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyEx")]
    private static extern uint MapVirtualKeyExNative(uint uCode, uint uMapType, IntPtr dwLocale, uint dwMapFlags);

    /// <summary>A keyboard hook callback.</summary>
    public delegate IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>WH_KEYBOARD_LL - the low-level keyboard hook.</summary>
    public const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    /// <summary>
    /// The payload of a low-level keyboard hook callback.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // -----------------------------------------------------------------
    // State
    // -----------------------------------------------------------------

    private IntPtr _hook;
    private Thread? _hookThread;
    private readonly CancellationTokenSource _cts = new();
    private KeyboardHookProc? _hookProc;

    /// <summary>
    /// Whether the keyboard is currently captured.
    /// </summary>
    /// <remarks>
    /// This follows the POINTER's position, not the focus.  The
    /// pointer being over the glasses is the user's signal that they
    /// are working in the glasses; the focus is still wherever it was
    /// on the other display, and stealing it would break their
    /// typing.
    /// </remarks>
    public bool IsCapturing { get; set; }

    /// <summary>
    /// Whether a captured keystroke is taken away from the focused
    /// application.
    /// </summary>
    /// <remarks>
    /// This has to be TRUE, and the reason is the double-typing bug.
    ///
    /// The projected application - VS Code, a browser - is a REAL
    /// WINDOW on the desktop, and it usually has the OS focus,
    /// because the panel never takes focus (it is WS_EX_NOACTIVATE).
    /// So a keystroke the user makes while working in the glasses
    /// reaches that window TWICE:
    ///
    ///   * NATIVELY, through the operating system's own input queue,
    ///     which is why the duplicate arrives with an EMPTY `code` -
    ///     the native path does not fill that field in; and
    ///   * over the DevTools protocol, which is the path we control
    ///     and the one that puts the character where the user's CURSOR
    ///     is pointing.
    ///
    /// The application cannot tell them apart, so it types the
    /// character twice.  We measured exactly that against VS Code:
    /// keydown, keypress, keydown, keypress, keyup, keyup for ONE
    /// physical key press.
    ///
    /// So while the glasses are capturing, the hook TAKES the key away
    /// from the operating system and delivers it over the protocol
    /// only.  Outside that, the hook does not even look at it, so the
    /// user's real typing on their other monitor is completely
    /// unaffected.
    ///
    /// This is the same rule the MOUSE hook follows: it swallows the
    /// event when it is over the glasses' display, because the panel
    /// window would otherwise receive the same click and dispatch it
    /// twice.
    /// </remarks>
    public bool SwallowWhileCapturing { get; set; } = true;

    /// <summary>
    /// Whether the keystroke has somewhere to go.
    /// </summary>
    /// <remarks>
    /// A key must only be swallowed when it is actually going to be
    /// delivered over the protocol.  If the projected window is not
    /// interactive - no DevTools endpoint, or the attachment has not
    /// completed - then swallowing the key would make the user's
    /// typing go NOWHERE, which is far worse than a duplicate
    /// character.
    ///
    /// The station supplies this from the input coordinator's
    /// connectivity, so the two stay in step.
    /// </remarks>
    public Func<bool>? HasDeliveryTarget { get; set; }

    /// <summary>
    /// Whether a captured keystroke should be taken away from the
    /// operating system.
    /// </summary>
    /// <remarks>
    /// Two conditions have to hold:
    ///
    ///  * the hook is capturing, which means the pointer is over the
    ///    glasses; and
    ///  * the keystroke has somewhere to go, which means the projected
    ///    application is interactive.
    ///
    /// The second condition is the safety net: swallowing a key with
    /// no destination would make the user's typing go nowhere, which
    /// is worse than a duplicate character.
    ///
    /// It is a separate method so it can be tested without a live
    /// hook - the rule is the whole point of the change, so it should
    /// be checked, not just hoped for.
    /// </remarks>
    public bool ShouldSwallow()
    {
        if (!IsCapturing) return false;
        if (!SwallowWhileCapturing) return false;
        return HasDeliveryTarget?.Invoke() ?? true;
    }

    /// <summary>Raised for each keystroke, already resolved.</summary>
    public event Action<ResolvedKey>? OnKey;

    /// <summary>Raised for problems worth showing to the user.</summary>
    public event Action<string>? OnLog;

    // -----------------------------------------------------------------
    // The hook
    // -----------------------------------------------------------------

    /// <summary>
    /// Install the low-level keyboard hook on a dedicated thread.
    /// </summary>
    public void StartHook()
    {
        if (_hookThread is { IsAlive: true }) return;

        _hookProc = OnKeyboardHook;

        _hookThread = new Thread(HookThreadLoop)
        {
            IsBackground = true,
            Name = "KeyboardHook",
            Priority = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
    }

    private void HookThreadLoop()
    {
        try
        {
            // The module handle has to point at the module that
            // contains the callback, or the hook fails to install.
            // NativeMethods.GetModuleHandle(null) returns the handle
            // of the executable that contains the calling code, which
            // is exactly right - the callback lives in this assembly.
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc!, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                OnLog?.Invoke(
                    "Could not install the low-level keyboard hook - typing into the "
                    + "glasses may not reach the projected app.");
            }

            while (!_cts.IsCancellationRequested)
            {
                if (!NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                {
                    Thread.Sleep(4);
                    continue;
                }
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookExNative(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsCapturing)
        {
            var message = (uint)wParam.ToInt64();
            var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = message is WM_KEYUP or WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // The extended flag is bit 0.  It distinguishes the
                // numpad's Enter from the main keyboard's, and the
                // cursor keys from the navigation cluster.
                var extended = (data.flags & 0x01) != 0;

                // Bit 30 is the "previous key state": it is set on an
                // auto-repeat, which arrives as a down while the key
                // is already down.
                var autoRepeat = (data.flags & 0x40000000) != 0;

                var e = new KeyEvent(
                    (int)data.vkCode, (int)data.scanCode, extended, isDown, autoRepeat);

                OnKey?.Invoke(Resolve(e));

                // Take the key away from the operating system.
                //
                // The projected application is a real window and it
                // usually holds the OS focus, because our panel never
                // takes focus.  So without this, the keystroke would
                // reach the application TWICE: natively through the
                // operating system's own input queue, and over the
                // DevTools protocol.  The native copy arrives with an
                // empty `code`, which is how we identified it.
                //
                // The application types the character twice.
                //
                // Returning 1 tells the system the key was handled and
                // the event must not travel any further down the chain.
                // Outside the capture the hook does not even look at
                // the event, so the user's real typing on their other
                // monitor is untouched.
                //
                // The key is only taken when it is actually going to
                // be delivered over the protocol.  Swallowing it
                // without a destination would make the user's typing
                // go nowhere, which is worse than a duplicate.
                if (ShouldSwallow())
                    return new IntPtr(1);
            }
        }

        // Not captured: the key belongs to whatever the user was
        // typing into.
        return CallNextHookExNative(_hook, nCode, wParam, lParam);
    }

    // -----------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------

    /// <summary>
    /// Turn a raw key event into the shape the protocol wants.
    /// </summary>
    /// <remarks>
    /// The CHARACTER is resolved here, on the hook's thread, using
    /// ToUnicodeEx.  ToUnicodeEx is not thread-safe with respect to
    /// the keyboard state buffer, but the hook thread is the only
    /// thread calling it, so the state stays consistent.
    ///
    /// ToUnicodeEx also MUTATES the keyboard state, so the state is
    /// re-read from the system for every key.  That is what makes the
    /// shifted character come out right: the state buffer it reads
    /// has the shift key set when the user is holding shift.
    /// </remarks>
    private ResolvedKey Resolve(KeyEvent e)
    {
        var virtualKey = e.VirtualKey;

        // A modifier has no character, and its descriptor names the
        // side ("ShiftRight" rather than "ShiftLeft").
        if (VirtualKeyMap.IsModifier(virtualKey))
        {
            var modifier = VirtualKeyMap.DescribeModifier(virtualKey);
            if (modifier is not null)
            {
                return new ResolvedKey(
                    modifier.Value.Key,
                    modifier.Value.Code,
                    null,
                    virtualKey,
                    e.IsDown,
                    e.IsAutoRepeat,
                    IsKeypad: false,
                    Location: LocationOf(modifier.Value.Code));
            }
        }

        if (!VirtualKeyMap.TryDescribe(virtualKey, e.Extended, out var descriptor))
        {
            // An unknown key: report it with a best-effort name so
            // the log can say which key was ignored.
            OnLog?.Invoke($"Unknown virtual key code {virtualKey} (0x{virtualKey:X}) - ignored.");
            return new ResolvedKey("", "", null, virtualKey, e.IsDown, e.IsAutoRepeat, false, 0);
        }

        // A modifier makes the key a SHORTCUT, not a character.
        //
        // Ctrl+S must arrive as a key event WITHOUT the character "s",
        // or the application's editor would also receive a stray "s"
        // every time the user saves.  Only a plain key - no Ctrl, no
        // Alt - carries its character.  Shift is the exception: it
        // changes WHICH character the key produces, and that character
        // is exactly what the user wants typed.
        var ctrlDown = (GetKeyStateNative(VirtualKeyMap.VK_CONTROL) & 0x8000) != 0
                       || (GetKeyStateNative(VirtualKeyMap.VK_RCONTROL) & 0x8000) != 0
                       || (GetKeyStateNative(VirtualKeyMap.VK_LCONTROL) & 0x8000) != 0;
        var altDown = (GetKeyStateNative(VirtualKeyMap.VK_MENU) & 0x8000) != 0
                      || (GetKeyStateNative(VirtualKeyMap.VK_RMENU) & 0x8000) != 0
                      || (GetKeyStateNative(VirtualKeyMap.VK_LMENU) & 0x8000) != 0;

        var text = descriptor.IsPrintable && e.IsDown && !ctrlDown && !altDown
            ? ResolveCharacter(virtualKey, e.ScanCode, e.Extended)
            : null;

        // The unmodified character is what the key produces without the
        // shift key.  It is only needed when the shifted one differs,
        // which is the only case worth a second call to the operating
        // system.
        var unmodified = text is null
            ? null
            : ResolveUnmodifiedCharacter(virtualKey, e.ScanCode);

        // The DOM `key` is the CHARACTER the key produces, so for a
        // printable key it is the character the operating system
        // resolved - not the map's unshifted default.  Without this,
        // Shift+A would arrive with key="a" and text="A", which is a
        // contradiction the application cannot make sense of.
        var domKey = text is { Length: 1 } ? text : descriptor.Key;

        return new ResolvedKey(
            domKey,
            descriptor.Code,
            text,
            virtualKey,
            e.IsDown,
            e.IsAutoRepeat,
            IsKeypad: IsKeypadCode(descriptor.Code),
            Location: LocationOf(descriptor.Code),
            UnmodifiedText: unmodified);
    }

    /// <summary>
    /// The character the key produced, according to the user's layout.
    /// </summary>
    /// <remarks>
    /// ToUnicodeEx is given the scan code and the key state, and
    /// returns the character.  It is told not to disturb the keyboard
    /// state (the 0x0004 flag) so that a dead key - the accent key on
    /// a French or Brazilian layout - does not consume the state the
    /// next key needs.
    ///
    /// The result is null when the key produces no text: a modifier,
    /// a function key, or a navigation key.
    /// </remarks>
    private static string? ResolveCharacter(int virtualKey, int scanCode, bool extended)
    {
        var mappedScan = MapVirtualKeyExNative((uint)virtualKey, 0, IntPtr.Zero, 0);
        if (mappedScan == 0) return null;

        var buffer = new char[8];
        var state = BuildKeyState(withShift: true);

        // The state buffer is what tells ToUnicodeEx that the shift
        // key is held, which is why the shifted character comes out
        // right without a layout table of our own.
        //
        // The flag 0x0004 is MAPVK_VK_TO_VSC_EX.  It is NOT "do not
        // disturb the state" - that behaviour is what a NULL state
        // buffer means.  0x0004 keeps the layout's DEAD KEYS alive: an
        // accent key - the "^" on a French or Brazilian layout - must
        // survive until the next key composes with it, otherwise typing
        // "^" then "e" would produce "e" instead of "ê".
        var length = ToUnicodeExNative(
            (uint)virtualKey, mappedScan, state,
            buffer, buffer.Length, 0x0004, IntPtr.Zero);

        if (length <= 0) return null;

        var text = new string(buffer, 0, length);
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The character the key produces with the shift key released.
    /// </summary>
    /// <remarks>
    /// The UNMODIFIED character is what the key produces without shift
    /// - the "a" of a key that produces "A".  Chromium's protocol
    /// carries both, and an application that wants to know what the
    /// key MEANS - rather than what it typed - reads the unmodified
    /// one.
    ///
    /// It is resolved by asking the operating system again, with the
    /// shift key cleared from the state buffer.  The dead-key state
    /// has to be flushed between the two calls, or the second sees the
    /// first's pending accent.
    /// </remarks>
    private static string? ResolveUnmodifiedCharacter(int virtualKey, int scanCode)
    {
        var mappedScan = MapVirtualKeyExNative((uint)virtualKey, 0, IntPtr.Zero, 0);
        if (mappedScan == 0) return null;

        // Cancel any pending dead key before asking again.
        FlushDeadKeys();

        var buffer = new char[8];
        var state = BuildKeyState(withShift: false);

        var length = ToUnicodeExNative(
            (uint)virtualKey, mappedScan, state,
            buffer, buffer.Length, 0x0004, IntPtr.Zero);

        if (length <= 0) return null;

        var text = new string(buffer, 0, length);
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Clear any pending dead key.
    /// </summary>
    /// <remarks>
    /// ToUnicodeEx queues an accent key - the "^" on a French layout -
    /// and the NEXT call to it composes that accent with whatever key
    /// comes next.  Calling it with the control key held is the
    /// documented way to cancel the pending accent, and it produces no
    /// character of its own.
    /// </remarks>
    private static void FlushDeadKeys()
    {
        var buffer = new char[8];
        var mappedScan = MapVirtualKeyExNative((uint)VirtualKeyMap.VK_CONTROL, 0, IntPtr.Zero, 0);
        if (mappedScan == 0) return;

        ToUnicodeExNative(
            (uint)VirtualKeyMap.VK_CONTROL, mappedScan, null,
            buffer, buffer.Length, 0x0004, IntPtr.Zero);
    }

    /// <summary>
    /// Build the keyboard state buffer that ToUnicodeEx reads.
    /// </summary>
    /// <remarks>
    /// The state buffer has to say which modifiers are held, or
    /// ToUnicodeEx would produce the unshifted character even when the
    /// user is holding shift.
    /// </remarks>
    private static byte[] BuildKeyState(bool withShift)
    {
        var keyState = new byte[256];
        if (!GetKeyboardStateNative(keyState)) return keyState;

        if (withShift && (GetKeyStateNative(VirtualKeyMap.VK_SHIFT) & 0x8000) != 0)
        {
            keyState[VirtualKeyMap.VK_SHIFT] = 0x80;
            keyState[VirtualKeyMap.VK_LSHIFT] = 0x80;
            keyState[VirtualKeyMap.VK_RSHIFT] = 0x80;
        }
        else if (!withShift)
        {
            // Explicitly clear the shift keys: the buffer came from
            // the system, so it reflects the real state, and the
            // unmodified character must not see the shift.
            keyState[VirtualKeyMap.VK_SHIFT] = 0;
            keyState[VirtualKeyMap.VK_LSHIFT] = 0;
            keyState[VirtualKeyMap.VK_RSHIFT] = 0;
        }

        if ((GetKeyStateNative(VirtualKeyMap.VK_CONTROL) & 0x8000) != 0)
        {
            keyState[VirtualKeyMap.VK_CONTROL] = 0x80;
            keyState[VirtualKeyMap.VK_LCONTROL] = 0x80;
            keyState[VirtualKeyMap.VK_RCONTROL] = 0x80;
        }
        if ((GetKeyStateNative(VirtualKeyMap.VK_MENU) & 0x8000) != 0)
        {
            keyState[VirtualKeyMap.VK_MENU] = 0x80;
            keyState[VirtualKeyMap.VK_LMENU] = 0x80;
            keyState[VirtualKeyMap.VK_RMENU] = 0x80;
        }

        return keyState;
    }

    private static bool IsKeypadCode(string code)
        => code.StartsWith("Numpad", StringComparison.Ordinal)
           || code == "NumpadEnter";

    private static int LocationOf(string code)
    {
        if (IsKeypadCode(code)) return 3;
        if (code.EndsWith("Left", StringComparison.Ordinal)) return 1;
        if (code.EndsWith("Right", StringComparison.Ordinal)) return 2;
        return 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _hookThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _hookProc = null;
        _cts.Dispose();
    }
}

/// <summary>
/// A keystroke, resolved into the DevTools protocol's vocabulary.
/// </summary>
/// <param name="Key">The DOM <c>KeyboardEvent.key</c> value.</param>
/// <param name="Code">The DOM <c>KeyboardEvent.code</c> value.</param>
/// <param name="Text">The character the key produced, or null.</param>
/// <param name="VirtualKey">The Win32 virtual key code.</param>
/// <param name="IsDown">True for a press, false for a release.</param>
/// <param name="IsAutoRepeat">True when the key is auto-repeating.</param>
/// <param name="IsKeypad">True for the numeric keypad.</param>
/// <param name="Location">0 standard, 1 left, 2 right, 3 numpad.</param>
/// <param name="UnmodifiedText">
/// The character the key produces WITHOUT the modifiers - the
/// unshifted letter for a shifted key.  Null when the key produces no
/// text.
/// </param>
public readonly record struct ResolvedKey(
    string Key,
    string Code,
    string? Text,
    int VirtualKey,
    bool IsDown,
    bool IsAutoRepeat,
    bool IsKeypad,
    int Location,
    string? UnmodifiedText = null);
