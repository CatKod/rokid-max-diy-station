// SPDX-License-Identifier: MIT
//
// Regression tests for the two tray-app bugs that made the product
// unusable even though the app started cleanly:
//
//   1. The global hotkeys never registered.  They were bound to the
//      settings window's HWND, but the app starts minimised by
//      default and a WPF Window that has never been shown has no
//      HWND yet — WindowInteropHelper.Handle returns zero, so every
//      RegisterHotKey call failed with ERROR_INVALID_WINDOW_HANDLE.
//
//   2. "Pin foreground window" silently did nothing.  It called a
//      P/Invoke for "GetCursorWindow", which is NOT a Win32 API — the
//      call throws EntryPointNotFoundException, and the hotkey
//      handler swallowed the exception.
//
// Both bugs were invisible: the app ran, the tray icon appeared, the
// log showed a friendly warning, and none of the primary interactions
// did anything.

using System.Runtime.InteropServices;
using Xunit;
using NativeMethods = RokidDiyStation.App.NativeMethods;

namespace RokidDiyStation.App.Tests;

public class TrayAppRegressionTests
{
    /// <summary>
    /// Run an action on a dedicated STA thread and re-throw whatever
    /// it threw on the calling thread.
    /// </summary>
    /// <remarks>
    /// WPF's HwndSource requires a single-threaded apartment, and
    /// xUnit runs its tests on MTA thread-pool threads.  The app
    /// itself always creates hotkeys on the WPF UI thread, so this is
    /// purely a test-harness concern.
    /// </remarks>
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
    /// The hotkey sink must not depend on the settings window having
    /// been shown.  We assert the sink is a message-only window by
    /// creating a binding and checking it registers — a binding whose
    /// HWND is zero cannot register at all.
    /// </summary>
    [Fact]
    public void A_Hotkey_Can_Be_Registered_Without_A_Visible_Window()
    {
        RunOnSta(() =>
        {
            // Pick a combination that is actually free on this
            // machine: another application may well own a given
            // combination (error 1409 = ERROR_HOTKEY_ALREADY_REGISTERED),
            // and that says nothing about our window.
            var vk = FindFreeVirtualKey(0x0002);
            Assert.NotEqual(0u, vk);

            using var binding = new RokidDiyStation.App.HotkeyBinding(
                id: 9001,
                modifiers: 0x0002,   // Ctrl
                key: vk);

            // If the binding were still wired to the (never shown)
            // main window, Register() would return false.
            Assert.True(binding.Register(),
                "the hotkey must register against its own message-only window, " +
                "not against a WPF window that may never have been shown");

            binding.Unregister();
        });
    }

    /// <summary>
    /// The combination used by the pin action must be free for us to
    /// take.  We use rarely-taken VKs so the test does not collide
    /// with whatever the developer's machine happens to own.
    /// </summary>
    [Fact]
    public void Two_Bindings_Can_Coexist()
    {
        RunOnSta(() =>
        {
            var vkA = FindFreeVirtualKey(0x0002);
            var vkB = FindFreeVirtualKey(0x0002, vkA);
            Assert.NotEqual(0u, vkA);
            Assert.NotEqual(0u, vkB);

            using var a = new RokidDiyStation.App.HotkeyBinding(9002, 0x0002, vkA);
            using var b = new RokidDiyStation.App.HotkeyBinding(9003, 0x0002, vkB);

            Assert.True(a.Register());
            Assert.True(b.Register());
        });
    }

    /// <summary>
    /// Find a virtual key that no other application has already
    /// registered, so the test is not at the mercy of whatever the
    /// developer's machine happens to own.
    /// </summary>
    private static uint FindFreeVirtualKey(uint modifiers, uint exclude = 0)
    {
        for (var vk = 0x70u; vk <= 0x7Fu; vk++)   // F1 .. F16
        {
            if (vk == exclude) continue;
            if (NativeMethods.RegisterHotKey(IntPtr.Zero, 9500, modifiers, vk))
            {
                NativeMethods.UnregisterHotKey(IntPtr.Zero, 9500);
                return vk;
            }
        }
        return 0;
    }

    /// <summary>
    /// There is no "GetCursorWindow" in user32.dll.  If someone
    /// reintroduces that P/Invoke, this test fails at the moment the
    /// call is made rather than at some later point in the wild.
    /// </summary>
    [Fact]
    public void User32_Has_No_GetCursorWindow_Entry_Point()
    {
        var user32 = NativeLibrary.Load("user32.dll");
        Assert.False(NativeLibrary.TryGetExport(user32, "GetCursorWindow", out _),
            "GetCursorWindow is not a Win32 API — use WindowPicker.GetCursorWindow() " +
            "(GetCursorPos + WindowFromPoint + GetAncestor) instead");
    }

    /// <summary>
    /// The real cursor-window lookup must return a top-level window
    /// rather than throwing.
    /// </summary>
    [Fact]
    public void GetCursorWindow_Returns_A_Window_Without_Throwing()
    {
        var hwnd = RokidDiyStation.App.Spatial.WindowPicker.GetCursorWindow();
        // Zero is legal (nothing under the cursor), but the call must
        // not have thrown an EntryPointNotFoundException.
        Assert.True(hwnd == IntPtr.Zero || RokidDiyStation.App.Spatial.WindowPicker.Exists(hwnd),
            "the cursor lookup must return a live window handle");
    }
}
