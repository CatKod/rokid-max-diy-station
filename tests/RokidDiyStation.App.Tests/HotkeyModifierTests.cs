// SPDX-License-Identifier: MIT
//
// Regression tests for the global hotkeys' modifiers.
//
// THE REQUEST
// -----------
// The user asked for the hotkeys to change from Ctrl+<key> to
// Fn+<key>, e.g. Ctrl+C -> Fn+C.
//
// WHY Fn IS NOT POSSIBLE
// ----------------------
// The Fn key is not a key the operating system ever sees.  It is
// handled by the laptop's EMBEDDED CONTROLLER (EC) - a microcontroller
// in the keyboard firmware.  On the ASUS TUF this was developed on:
// pressing Fn alone produces NO keyboard event whatsoever (measured
// with a low-level keyboard hook: the hook sees nothing at all for
// Fn), and there is no VK_FN virtual-key code for RegisterHotKey to
// use.  The EC only intercepts Fn+<key> COMBINATIONS and converts them
// into the laptop's own functions - brightness, volume, the numeric
// keypad, Airplane mode - and it does so in firmware, before Windows
// ever sees the keystroke.  Which combinations it intercepts is a
// BIOS setting, not something an application can influence.
//
// The user then chose Ctrl+Shift+Win+Alt as the replacement, which is
// the most-modified combination Windows offers that still produces a
// real key event.
//
// WHAT THESE TESTS PIN DOWN
// -------------------------
//   * the app registers the four-modifier combination,
//   * the old, easily-colliding single-modifier shortcuts are gone,
//   * and the combination still actually registers on a real machine.
//
// The last one matters: RegisterHotKey can fail with
// ERROR_HOTKEY_ALREADY_REGISTERED, and a combination nobody else uses
// is the whole point of adding three more modifiers.

using System.Reflection;
using System.Reflection.Emit;
using Xunit;
using NativeMethods = RokidDiyStation.App.HotkeyNativeMethods;

namespace RokidDiyStation.App.Tests;

public class HotkeyModifierTests
{
    /// <summary>
    /// The modifiers every action's hotkey uses.
    /// </summary>
    private const uint AllModifiers =
        NativeMethods.MOD_CTRL |
        NativeMethods.MOD_SHIFT |
        NativeMethods.MOD_ALT |
        NativeMethods.MOD_WIN;

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// The modifier constants themselves.
    /// </summary>
    [Fact]
    public void The_Four_Modifier_Constants_Have_Their_Win32_Values()
    {
        // These are the values from the Win32 API.  If they are wrong
        // the combinations register as something else entirely, and
        // the failure is silent - the hotkey just never fires.
        Assert.Equal(0x0001u, NativeMethods.MOD_ALT);
        Assert.Equal(0x0002u, NativeMethods.MOD_CTRL);
        Assert.Equal(0x0004u, NativeMethods.MOD_SHIFT);
        Assert.Equal(0x0008u, NativeMethods.MOD_WIN);
        Assert.Equal(0x4000u, NativeMethods.MOD_NOREPEAT);
    }

    /// <summary>
    /// The string literals a method body contains.
    /// </summary>
    /// <remarks>
    /// The hotkey combinations are named in the log messages, so a
    /// leftover "Ctrl+C" literal means a leftover Ctrl+C shortcut.
    /// </remarks>
    private static IReadOnlyList<string> StringLiteralsIn(MethodInfo method)
    {
        var body = method.GetMethodBody();
        Assert.NotNull(body);

        var il = body!.GetILAsByteArray();
        Assert.NotNull(il);

        var module = method.Module;
        var found = new List<string>();

        var oneByte = new OpCode[0x100];
        var twoByte = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;
            var value = (ushort)op.Value;
            if (value < 0x100) oneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00) twoByte[value & 0xFF] = op;
        }

        var index = 0;
        while (index < il.Length)
        {
            OpCode op;
            var first = il[index];
            if (first == 0xFE)
            {
                op = twoByte[il[index + 1]];
                index += 2;
            }
            else
            {
                op = oneByte[first];
                index += 1;
            }

            switch (op.OperandType)
            {
                case OperandType.InlineString:
                {
                    var token = BitConverter.ToInt32(il, index);
                    index += 4;
                    try { found.Add(module.ResolveString(token)); }
                    catch (ArgumentException) { }
                    break;
                }

                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineSig:
                    index += 4;
                    break;

                case OperandType.InlineI:
                case OperandType.ShortInlineR:
                    index += 4;
                    break;

                case OperandType.ShortInlineI:
                    index += 1;
                    break;

                case OperandType.InlineI8:
                case OperandType.InlineR:
                    index += 8;
                    break;

                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, index);
                    index += 4 + (4 * count);
                    break;
                }

                case OperandType.ShortInlineBrTarget:
                    index += 1;
                    break;

                case OperandType.InlineBrTarget:
                    index += 4;
                    break;

                case OperandType.InlineVar:
                    index += 2;
                    break;

                case OperandType.ShortInlineVar:
                    index += 1;
                    break;

                default:
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// The tray context must register the four-modifier combination.
    /// </summary>
    [Fact]
    public void The_Tray_Registers_The_Four_Modifier_Combination()
    {
        var tray = typeof(RokidDiyStation.App.TrayApplicationContext);
        var register = tray.GetMethod(
            "RegisterHotkeys",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(register);

        // The log messages name the combination, so the literals in the
        // method tell us which combinations it actually binds.
        var literals = StringLiteralsIn(register!);

        Assert.Contains(literals, l => l.Contains("Ctrl+Shift+Win+Alt+Space"));
        Assert.Contains(literals, l => l.Contains("Ctrl+Shift+Win+Alt+C"));
        Assert.Contains(literals, l => l.Contains("Ctrl+Shift+Win+Alt+R"));
        Assert.Contains(literals, l => l.Contains("Ctrl+Shift+Win+Alt+H"));
        Assert.Contains(literals, l => l.Contains("Ctrl+Shift+Win+Alt+G"));
    }

    /// <summary>
    /// The single-modifier shortcut must no longer be used.
    /// </summary>
    /// <remarks>
    /// Ctrl+Space, Ctrl+R and Ctrl+C are all combinations that other
    /// applications want too - which is exactly why the user asked for
    /// them to change.  If one of them comes back, the app will
    /// silently fail to register it on a busy machine.
    /// </remarks>
    [Theory]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+R")]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+Alt+R")]
    public void The_Old_Combinations_Are_Not_Used_Anymore(string label)
    {
        var tray = typeof(RokidDiyStation.App.TrayApplicationContext);
        var register = tray.GetMethod(
            "RegisterHotkeys",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(register);

        // The label is a string literal in the IL, so a leftover label
        // means a leftover shortcut.
        var literals = StringLiteralsIn(register!);

        Assert.DoesNotContain(literals, l => l.Contains(label));
    }

    /// <summary>
    /// Every action must have a fallback, because a combination can
    /// always be owned by another application.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical: measured on the ASUS TUF this was
    /// developed on, Ctrl+Shift+Win+Alt+Space was already taken by
    /// the OS, and without a fallback the "pin panel here" action
    /// would have had no shortcut at all.
    /// </remarks>
    [Fact]
    public void Every_Action_Has_More_Than_One_Candidate()
    {
        var tray = typeof(RokidDiyStation.App.TrayApplicationContext);
        var register = tray.GetMethod(
            "RegisterHotkeys",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(register);

        var literals = StringLiteralsIn(register!);

        // Each action's labels name a combination.  Count the distinct
        // actions that have a fallback list: "pin panel here",
        // "forget the panel", "recenter", "show/hide window" and
        // "bring the mouse back".
        var actions = new[]
        {
            "pin panel here",
            "forget the panel",
            "recenter",
            "show/hide window",
            "bring the mouse back",
        };

        foreach (var action in actions)
        {
            var count = literals.Count(l => l.Contains(action));
            Assert.True(count >= 2,
                $"the \"{action}\" action must offer more than one candidate "
                + "combination, so a shortcut is not lost when one is owned");
        }
    }

    /// <summary>
    /// The four-modifier combination must actually register.
    /// </summary>
    /// <remarks>
    /// This is the property that matters in practice: a combination
    /// that another application already owns fails with
    /// ERROR_HOTKEY_ALREADY_REGISTERED, and the hotkey then simply
    /// never fires.  The four-modifier combination is chosen because
    /// nothing else takes it.
    ///
    /// The test uses a function key that is not one the app binds, so
    /// it does not collide with the app's own registrations.
    /// </remarks>
    [Fact]
    public void The_Four_Modifier_Combination_Can_Be_Registered()
    {
        RunOnSta(() =>
        {
            using var binding = new RokidDiyStation.App.HotkeyBinding(
                id: 9601,
                modifiers: AllModifiers,
                key: 0x7Bu);   // F12

            Assert.True(binding.Register(),
                "Ctrl+Shift+Win+Alt+F12 must be registerable - it is the "
                + "combination the app's own hotkeys use, so if it cannot "
                + "be registered, none of them can");
        });
    }

    /// <summary>
    /// The no-repeat flag must be accepted by RegisterHotKey.
    /// </summary>
    /// <remarks>
    /// MOD_NOREPEAT is used for the "bring the mouse back" action.
    /// Without it, holding the combination down yanks the cursor to
    /// the primary display over and over.
    /// </remarks>
    [Fact]
    public void The_No_Repeat_Flag_Is_Accepted()
    {
        RunOnSta(() =>
        {
            using var binding = new RokidDiyStation.App.HotkeyBinding(
                id: 9602,
                modifiers: AllModifiers | NativeMethods.MOD_NOREPEAT,
                key: 0x7Bu);   // F12

            Assert.True(binding.Register(),
                "the no-repeat flag must be accepted by RegisterHotKey");
        });
    }
}
