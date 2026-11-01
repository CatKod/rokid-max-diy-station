// SPDX-License-Identifier: MIT
//
// Tests for the keyboard: the key map and the shape of the events.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The keyboard has three naming systems to keep straight, and getting
// any of them wrong produces a bug that is confusing rather than
// obvious:
//
//   * the Win32 virtual key code, which is what the hook hands us;
//   * the DOM `code`, which names the PHYSICAL key ("KeyA",
//     "Digit1", "ArrowLeft");
//   * the DOM `key`, which names the CHARACTER ("a", "A", "Enter").
//
// A shortcut bound to Ctrl+S looks at `code`; a text field looks at
// `key` and at the text the event carries.  If the code names the
// wrong physical key, Ctrl+S turns into Ctrl+something else, and if a
// printable key's press does not carry its character, nothing is
// typed at all.
//
// And there is one shape bug that is worth catching here rather than
// on hardware: a RELEASE must be a "keyUp" event.  Sending it as a
// "rawKeyDown" - which is what a naive "no text means raw" rule
// produces - tells the application that the key went down AGAIN, and
// the key is never released.
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class KeyboardMappingTests
{
    [Fact]
    public void ALetterNamesItsPhysicalPositionAndItsCharacter()
    {
        Assert.True(VirtualKeyMap.TryDescribe(0x41, false, out var a));
        Assert.Equal("KeyA", a.Code);
        Assert.Equal("a", a.Key);
        Assert.True(a.IsPrintable);
    }

    [Fact]
    public void ADigitRowKeyIsNamedByItsPositionNotItsCharacter()
    {
        // The DOM code is the physical position, so it does not change
        // with the layout - which is exactly why a shortcut bound to
        // Ctrl+1 works on a German keyboard too.
        Assert.True(VirtualKeyMap.TryDescribe(0x31, false, out var one));
        Assert.Equal("Digit1", one.Code);
        Assert.Equal("1", one.Key);
    }

    [Fact]
    public void TheNumpadIsNamedAsTheNumpad()
    {
        Assert.True(VirtualKeyMap.TryDescribe(0x61, false, out var one));
        Assert.Equal("Numpad1", one.Code);
        Assert.Equal("1", one.Key);
    }

    [Fact]
    public void TheExtendedEnterIsTheNumpadEnter()
    {
        // The main keyboard's Enter is not extended; the numpad's is.
        Assert.True(VirtualKeyMap.TryDescribe(0x0D, false, out var main));
        Assert.Equal("Enter", main.Code);

        Assert.True(VirtualKeyMap.TryDescribe(0x0D, true, out var numpad));
        Assert.Equal("NumpadEnter", numpad.Code);
        Assert.Equal("Enter", numpad.Key);
    }

    [Fact]
    public void TheArrowsAreArrows()
    {
        Assert.True(VirtualKeyMap.TryDescribe(0x25, false, out var left));
        Assert.Equal("ArrowLeft", left.Code);
        Assert.Equal("ArrowLeft", left.Key);
        Assert.False(left.IsPrintable);
    }

    [Fact]
    public void TheFunctionKeysAreNumberedFromOne()
    {
        Assert.True(VirtualKeyMap.TryDescribe(0x70, false, out var f1));
        Assert.Equal("F1", f1.Code);

        Assert.True(VirtualKeyMap.TryDescribe(0x7B, false, out var f12));
        Assert.Equal("F12", f12.Code);

        // F13 onwards live at 0x7C, NOT at 0x90 - 0x90 is the numlock
        // key, and putting F13 there made F13 indistinguishable from
        // numlock.
        Assert.True(VirtualKeyMap.TryDescribe(0x7C, false, out var f13));
        Assert.Equal("F13", f13.Code);

        Assert.True(VirtualKeyMap.TryDescribe(0x90, false, out var numLock));
        Assert.Equal("NumLock", numLock.Code);
    }

    [Fact]
    public void TheModifiersAreNamedByTheirSide()
    {
        var left = VirtualKeyMap.DescribeModifier(VirtualKeyMap.VK_LCONTROL);
        Assert.NotNull(left);
        Assert.Equal("ControlLeft", left!.Value.Code);
        Assert.Equal("Control", left.Value.Key);

        var right = VirtualKeyMap.DescribeModifier(VirtualKeyMap.VK_RCONTROL);
        Assert.NotNull(right);
        Assert.Equal("ControlRight", right!.Value.Code);
        Assert.Equal("Control", right.Value.Key);
    }

    [Fact]
    public void EveryModifierIsRecognisedAsAModifier()
    {
        // The generic codes (0x10, 0x11, 0x12) are what the hook
        // reports when it cannot tell left from right, so they have to
        // be recognised too - otherwise a modifier arrives at the
        // application as an unknown key.
        Assert.True(VirtualKeyMap.IsModifier(VirtualKeyMap.VK_SHIFT));
        Assert.True(VirtualKeyMap.IsModifier(VirtualKeyMap.VK_CONTROL));
        Assert.True(VirtualKeyMap.IsModifier(VirtualKeyMap.VK_MENU));
        Assert.True(VirtualKeyMap.IsModifier(VirtualKeyMap.VK_LWIN));
        Assert.True(VirtualKeyMap.IsModifier(VirtualKeyMap.VK_RCONTROL));

        Assert.False(VirtualKeyMap.IsModifier(0x41));
        Assert.False(VirtualKeyMap.IsModifier(0x20));
    }

    [Fact]
    public void TheSpaceBarIsPrintable()
    {
        Assert.True(VirtualKeyMap.TryDescribe(0x20, false, out var space));
        Assert.Equal("Space", space.Code);
        Assert.True(space.IsPrintable);
    }

    [Fact]
    public void AnUnknownKeyIsRejectedRatherThanGuessed()
    {
        Assert.False(VirtualKeyMap.TryDescribe(0x07, false, out _));
    }

    [Fact]
    public void TheKeyEventShapeFollowsTheProtocol()
    {
        // A PRINTABLE key's press carries the character, so the text
        // field is the character's own text.
        var printable = new ResolvedKey("a", "KeyA", "a", 0x41, IsDown: true, false, false, 0);
        Assert.Equal("keyDown", BrowserInputRouter.KeyEventType(printable));
        Assert.Equal("a", BrowserInputRouter.KeyEventText(printable));

        // A NON-PRINTABLE key's press carries NO text at all.
        var shortcut = new ResolvedKey("Escape", "Escape", null, 0x1B, IsDown: true, false, false, 0);
        Assert.Equal("rawKeyDown", BrowserInputRouter.KeyEventType(shortcut));
        Assert.Null(BrowserInputRouter.KeyEventText(shortcut));

        // A RELEASE is a release, whatever the key is.
        var release = new ResolvedKey("a", "KeyA", "a", 0x41, IsDown: false, false, false, 0);
        Assert.Equal("keyUp", BrowserInputRouter.KeyEventType(release));
        Assert.Null(BrowserInputRouter.KeyEventText(release));
    }

    [Fact]
    public void TheShortcutKeyArrivesWithoutTheCharacter()
    {
        // Ctrl+S: the "s" key's press must NOT carry the character
        // "s", or the application's editor would receive a stray "s"
        // every time the user saves.
        var withCtrl = new ResolvedKey("s", "KeyS", null, 0x53, IsDown: true, false, false, 0);
        Assert.Equal("rawKeyDown", BrowserInputRouter.KeyEventType(withCtrl));
        Assert.Null(BrowserInputRouter.KeyEventText(withCtrl));

        // The control key itself is a modifier: not printable, so it
        // arrives as a raw press too.
        var control = new ResolvedKey("Control", "ControlLeft", null, 0x11, IsDown: true, false, false, 1);
        Assert.Equal("rawKeyDown", BrowserInputRouter.KeyEventType(control));
    }

    [Fact]
    public void AKeystrokeIsTakenAwayFromTheFocusedApplicationOnlyWhileCapturing()
    {
        // THE REGRESSION TEST FOR THE DOUBLE-TYPING BUG.
        //
        // The projected application is a REAL window and it usually
        // holds the OS focus, because our panel never takes focus.  So
        // a keystroke reaches it twice: natively through the
        // operating system's own input queue, and over the DevTools
        // protocol.  We measured that against VS Code - one physical
        // key press produced keydown, keypress, keydown, keypress,
        // keyup, keyup.
        //
        // The native copy is the one with the EMPTY `code`, because
        // the native path does not fill that field in.
        //
        // So while the glasses are capturing, the hook MUST take the
        // key away from the operating system.
        using var keyboard = new KeyboardInput
        {
            HasDeliveryTarget = () => true,
        };

        keyboard.IsCapturing = true;
        Assert.True(keyboard.ShouldSwallow(),
            "the keystroke must be taken away from the focused application, or it is delivered twice");

        // Outside the capture the user's real typing has to reach
        // whatever they were typing into.
        keyboard.IsCapturing = false;
        Assert.False(keyboard.ShouldSwallow(),
            "the keystroke must not be taken away when the glasses are not capturing");
    }

    [Fact]
    public void AKeystrokeIsNotTakenAwayWhenThereIsNowhereToDeliverIt()
    {
        // The safety net: if the projected window is not interactive,
        // swallowing the key would make the user's typing go NOWHERE,
        // which is far worse than a duplicate character.
        using var keyboard = new KeyboardInput
        {
            HasDeliveryTarget = () => false,
        };

        keyboard.IsCapturing = true;
        Assert.False(keyboard.ShouldSwallow(),
            "the keystroke must still reach the focused application when there is no protocol target");
    }

    [Fact]
    public void WithoutADeliveryTargetTheKeyIsStillTakenAway()
    {
        // No target check means "no information", and the default is
        // to take the key away - because the double character is the
        // bug we are fixing, and a missing destination is the
        // situation where the user's attention is on the glasses.
        using var keyboard = new KeyboardInput();
        keyboard.IsCapturing = true;
        Assert.True(keyboard.ShouldSwallow());
    }
}
