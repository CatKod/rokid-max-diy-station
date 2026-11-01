// SPDX-License-Identifier: MIT
//
// The Win32 keyboard, expressed in the DevTools protocol's terms.
//
// To deliver a keystroke over CDP we have to describe it with three
// different naming systems at once:
//
//   * the Win32 VIRTUAL KEY CODE, which is what the low-level
//     keyboard hook hands us;
//   * the DOM `code`, which names the PHYSICAL key ("KeyA",
//     "Digit1", "ArrowLeft") and is what a keyboard layout is
//     independent of;
//   * the DOM `key`, which names the CHARACTER the key produces
//     ("a", "A", "Enter", "ArrowLeft") and is therefore layout
//     dependent.
//
// Getting these confused is the classic way to end up with a cursor
// that types the wrong letters.  This class owns the mapping.
//
// Why the code and the key are both needed
// ----------------------------------------
// `code` is what a game's WASD handler and a shortcut like Ctrl+S
// look at; it does not change when the user switches keyboard
// layout.  `key` is what a text field wants, because it is the
// character the user actually typed.  So a keystroke needs both.
//
// The shifted character is NOT derived here.  It comes from the
// operating system, which knows the active keyboard layout - see
// <see cref="RokidDiyStation.App.Spatial.KeyboardInput"/> for the
// ToUnicodeEx call that does it.  Doing it ourselves would hard-code
// a US layout, and a user on a Vietnamese or German layout would type
// the wrong characters.
namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// How one physical key is described to the DevTools protocol.
/// </summary>
/// <param name="Key">
/// The DOM <c>KeyboardEvent.key</c> value - the character the key
/// produces, or a name for a non-printable key.
/// </param>
/// <param name="Code">
/// The DOM <c>KeyboardEvent.code</c> value - the physical key's
/// position, independent of the layout.
/// </param>
/// <param name="IsPrintable">
/// True when the key produces text, so a character event should
/// accompany it.
/// </param>
public readonly record struct KeyDescriptor(string Key, string Code, bool IsPrintable)
{
    public override string ToString() => $"{Key} ({Code})";
}

/// <summary>
/// Maps Win32 virtual key codes to the DevTools protocol's key and
/// code names.
/// </summary>
public static class VirtualKeyMap
{
    // -----------------------------------------------------------------
    // The DOM code names, indexed by virtual key code.
    //
    // The codes name the PHYSICAL key, so they are a property of the
    // keyboard, not of the layout.  A German keyboard's "Z" key is
    // still "KeyY" in DOM terms, and that is exactly why a shortcut
    // bound to Ctrl+Z works on every layout.
    // -----------------------------------------------------------------

    private static readonly string?[] CodeByVirtualKey = new string?[256];
    private static readonly string?[] KeyByVirtualKey = new string?[256];
    private static readonly bool[] PrintableByVirtualKey = new bool[256];

    /// <summary>The Win32 key that the left shift key produces.</summary>
    public const int VK_SHIFT = 0x10;

    /// <summary>The Win32 key for the LEFT shift key, when qualified.</summary>
    public const int VK_LSHIFT = 0xA0;

    /// <summary>The Win32 key that the right shift key produces.</summary>
    public const int VK_RSHIFT = 0xA1;

    /// <summary>The Win32 key that the left control key produces.</summary>
    public const int VK_CONTROL = 0x11;

    /// <summary>The Win32 key for the LEFT control key, when qualified.</summary>
    public const int VK_LCONTROL = 0xA2;

    /// <summary>The Win32 key that the right control key produces.</summary>
    public const int VK_RCONTROL = 0xA3;

    /// <summary>The Win32 key that the left alt key produces.</summary>
    public const int VK_MENU = 0x12;

    /// <summary>The Win32 key for the LEFT alt key, when qualified.</summary>
    public const int VK_LMENU = 0xA4;

    /// <summary>The Win32 key that the right alt key produces.</summary>
    public const int VK_RMENU = 0xA5;

    /// <summary>The Win32 key that the left Windows key produces.</summary>
    public const int VK_LWIN = 0x5B;

    /// <summary>The Win32 key that the right Windows key produces.</summary>
    public const int VK_RWIN = 0x5C;

    /// <summary>The Win32 key for the Caps Lock key.</summary>
    public const int VK_CAPITAL = 0x14;

    /// <summary>The Win32 key for the Num Lock key.</summary>
    public const int VK_NUMLOCK = 0x90;

    /// <summary>The Win32 key for the Scroll Lock key.</summary>
    public const int VK_SCROLL = 0x91;

    /// <summary>The Win32 key for the Print Screen key.</summary>
    public const int VK_SNAPSHOT = 0x2C;

    /// <summary>The Win32 key for the Pause key.</summary>
    public const int VK_PAUSE = 0x13;

    /// <summary>The Win32 key for the Applications (context menu) key.</summary>
    public const int VK_APPS = 0x5D;

    /// <summary>
    /// The Windows key, which the protocol calls Meta.
    /// </summary>
    public const int VK_META = VK_LWIN;

    static VirtualKeyMap()
    {
        // Letters.  The DOM code is the physical position, so it is
        // just the letter; the DOM key is the character, which the
        // layout and the shift state decide - the default here is the
        // unshifted letter and the shifted one is applied later.
        for (var i = 0; i < 26; i++)
        {
            var letter = (char)('A' + i);
            CodeByVirtualKey[0x41 + i] = "Key" + letter;
            KeyByVirtualKey[0x41 + i] = letter.ToString().ToLowerInvariant();
            PrintableByVirtualKey[0x41 + i] = true;
        }

        // The digits row.  The DOM code is Digit0..Digit9 regardless of
        // the layout, and the DOM key is the digit itself.
        for (var i = 0; i < 10; i++)
        {
            CodeByVirtualKey[0x30 + i] = "Digit" + i;
            KeyByVirtualKey[0x30 + i] = i.ToString();
            PrintableByVirtualKey[0x30 + i] = true;
        }

        // The numpad.  The codes are Numpad0..Numpad9 and the
        // arithmetic keys.
        for (var i = 0; i < 10; i++)
        {
            CodeByVirtualKey[0x60 + i] = "Numpad" + i;
            KeyByVirtualKey[0x60 + i] = i.ToString();
            PrintableByVirtualKey[0x60 + i] = true;
        }
        Set(0x6A, "NumpadMultiply", "*", true);
        Set(0x6B, "NumpadAdd", "+", true);
        Set(0x6C, "NumpadDecimal", ".", true);
        Set(0x6D, "NumpadSubtract", "-", true);
        Set(0x6E, "NumpadDecimal", ".", true);
        Set(0x6F, "NumpadDivide", "/", true);

        // The function keys.  F1-F12 are 0x70-0x7B and F13-F24 are
        // 0x7C-0x87 - NOT 0x90, which is the numlock key.
        for (var i = 0; i < 24; i++)
        {
            var vk = 0x70 + i;
            Set(vk, "F" + (i + 1), "F" + (i + 1), false);
        }

        // The navigation cluster and the arrows.  The DOM codes for
        // these are the same names as the DOM keys, which is the one
        // place the two agree.
        Set(0x25, "ArrowLeft", "ArrowLeft", false);
        Set(0x26, "ArrowUp", "ArrowUp", false);
        Set(0x27, "ArrowRight", "ArrowRight", false);
        Set(0x28, "ArrowDown", "ArrowDown", false);
        Set(0x21, "PageUp", "PageUp", false);
        Set(0x22, "PageDown", "PageDown", false);
        Set(0x23, "End", "End", false);
        Set(0x24, "Home", "Home", false);
        Set(0x2D, "Insert", "Insert", false);
        Set(0x2E, "Delete", "Delete", false);

        // Editing and control keys.
        Set(0x08, "Backspace", "Backspace", false);
        Set(0x09, "Tab", "Tab", false);
        Set(0x0D, "Enter", "Enter", false);
        Set(0x1B, "Escape", "Escape", false);
        Set(0x20, "Space", " ", true);
        Set(0x2C, "PrintScreen", "PrintScreen", false);
        Set(0x13, "Pause", "Pause", false);
        Set(0x14, "CapsLock", "CapsLock", false);
        Set(0x5D, "ContextMenu", "ContextMenu", false);
        Set(0x90, "NumLock", "NumLock", false);
        Set(0x91, "ScrollLock", "ScrollLock", false);

        // The punctuation row, US positions.  The DOM code is the
        // physical position; the DOM key is the unshifted US
        // character, and the layout's actual character is supplied
        // by the operating system later.
        Set(0xBA, "Semicolon", ";", true);
        Set(0xBB, "Equal", "=", true);
        Set(0xBC, "Comma", ",", true);
        Set(0xBD, "Minus", "-", true);
        Set(0xBE, "Period", ".", true);
        Set(0xBF, "Slash", "/", true);
        Set(0xC0, "Backquote", "`", true);
        Set(0xDB, "BracketLeft", "[", true);
        Set(0xDC, "Backslash", "\\", true);
        Set(0xDD, "BracketRight", "]", true);
        Set(0xDE, "Quote", "'", true);
        Set(0xE2, "IntlBackslash", "\\", true);

        // The modifiers.  Their DOM key names are "Shift", "Control",
        // "Alt" and "Meta" - note that CDP calls the Windows key
        // "Meta", which is what the DOM calls it too.
        Set(0xA0, "ShiftLeft", "Shift", false);
        Set(0xA1, "ShiftRight", "Shift", false);
        Set(0xA2, "ControlLeft", "Control", false);
        Set(0xA3, "ControlRight", "Control", false);
        Set(0xA4, "AltLeft", "Alt", false);
        Set(0xA5, "AltRight", "Alt", false);
        Set(0x5B, "MetaLeft", "Meta", false);
        Set(0x5C, "MetaRight", "Meta", false);

        // The generic (unqualified) modifier codes, which the hook
        // reports when it cannot tell left from right.
        Set(0x10, "ShiftLeft", "Shift", false);
        Set(0x11, "ControlLeft", "Control", false);
        Set(0x12, "AltLeft", "Alt", false);
    }

    private static void Set(int virtualKey, string code, string key, bool printable)
    {
        CodeByVirtualKey[virtualKey] = code;
        KeyByVirtualKey[virtualKey] = key;
        PrintableByVirtualKey[virtualKey] = printable;
    }

    /// <summary>
    /// Whether a virtual key code is one this application knows.
    /// </summary>
    public static bool IsKnown(int virtualKey)
        => virtualKey >= 0 && virtualKey < 256 && CodeByVirtualKey[virtualKey] is not null;

    /// <summary>
    /// Describe a virtual key code.
    /// </summary>
    /// <param name="virtualKey">The Win32 virtual key code.</param>
    /// <param name="extended">
    /// True when the key was reported with the extended flag, which
    /// distinguishes the numpad's Enter from the main keyboard's, and
    /// the cursor keys from the navigation cluster.
    /// </param>
    /// <param name="descriptor">The description, when the key is known.</param>
    /// <returns>False when the key is not one we can describe.</returns>
    public static bool TryDescribe(int virtualKey, bool extended, out KeyDescriptor descriptor)
    {
        descriptor = default;

        if (virtualKey < 0 || virtualKey >= 256) return false;

        // The extended flag distinguishes the DUPLICATED keys - the
        // ones that exist both on the main keyboard and on the numpad:
        //
        //   * VK_RETURN: extended means the numpad's Enter, whose DOM
        //     code is "NumpadEnter".  The main keyboard's Enter is not
        //     extended.
        //   * The arrows and the navigation cluster: extended means the
        //     ARROW KEYS, which on a full keyboard are a separate
        //     cluster from the navigation block.
        //   * The numpad's Insert/Home/PageUp/... row: NOT extended,
        //     so they must keep the navigation cluster's codes.
        //
        // Chromium's own key handling treats the extended numpad
        // Enter as a distinct key, so we do too.
        var code = CodeByVirtualKey[virtualKey];
        var key = KeyByVirtualKey[virtualKey];
        if (code is null || key is null) return false;

        if (extended && virtualKey == 0x0D)
        {
            code = "NumpadEnter";
            key = "Enter";
        }

        descriptor = new KeyDescriptor(key, code, PrintableByVirtualKey[virtualKey]);
        return true;
    }

    /// <summary>
    /// The DOM key name for a virtual key code, or null.
    /// </summary>
    public static string? KeyName(int virtualKey)
        => virtualKey >= 0 && virtualKey < 256 ? KeyByVirtualKey[virtualKey] : null;

    /// <summary>
    /// The DOM code name for a virtual key code, or null.
    /// </summary>
    public static string? CodeName(int virtualKey)
        => virtualKey >= 0 && virtualKey < 256 ? CodeByVirtualKey[virtualKey] : null;

    /// <summary>
    /// Whether the virtual key code is a modifier.
    /// </summary>
    public static bool IsModifier(int virtualKey) => virtualKey switch
    {
        VK_SHIFT or VK_RSHIFT or VK_LSHIFT
            or VK_CONTROL or VK_RCONTROL or VK_LCONTROL
            or VK_MENU or VK_RMENU or VK_LMENU
            or VK_LWIN or VK_RWIN => true,
        _ => false,
    };

    /// <summary>
    /// Whether the virtual key code is a modifier on the RIGHT side.
    /// </summary>
    /// <remarks>
    /// The distinction matters for the DOM code: the right control key
    /// is "ControlRight", not "ControlLeft", and an app that shows
    /// which key was pressed needs the right answer.
    /// </remarks>
    public static bool IsRightModifier(int virtualKey) => virtualKey switch
    {
        VK_RSHIFT or VK_RCONTROL or VK_RMENU or VK_RWIN => true,
        _ => false,
    };

    /// <summary>
    /// The DOM key name for a modifier, qualified by side.
    /// </summary>
    /// <remarks>
    /// The hook reports the qualified virtual key codes (VK_LSHIFT is
    /// 0xA0, VK_RSHIFT is 0xA1), so the side is known by the time we
    /// get here and the DOM code can name it exactly.
    /// </remarks>
    public static KeyDescriptor? DescribeModifier(int virtualKey) => virtualKey switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT
            => new KeyDescriptor("Shift", "Shift" + (IsRightModifier(virtualKey) ? "Right" : "Left"), false),
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL
            => new KeyDescriptor("Control", "Control" + (IsRightModifier(virtualKey) ? "Right" : "Left"), false),
        VK_MENU or VK_LMENU or VK_RMENU
            => new KeyDescriptor("Alt", "Alt" + (IsRightModifier(virtualKey) ? "Right" : "Left"), false),
        VK_LWIN or VK_RWIN
            => new KeyDescriptor("Meta", "Meta" + (IsRightModifier(virtualKey) ? "Right" : "Left"), false),
        _ => null,
    };
}
