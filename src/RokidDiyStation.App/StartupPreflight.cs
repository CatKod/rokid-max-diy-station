// SPDX-License-Identifier: MIT
//
// The startup gate.
//
// THE PRODUCT'S CONTRACT
// ----------------------
// The Rokid Max is not a peripheral the app can limp along without:
// the spatial panel is drawn on the glasses' own display, and the
// head pose comes from the glasses' IMU.  With no glasses there is
// no panel, no tracking and no spatial display - the app would sit
// in a "searching for device…" state forever, doing nothing.
//
// So the glasses have to be plugged in BEFORE the app starts, and the
// app says so plainly instead of pretending to work.
//
// WHY THE VIRTUAL DISPLAY IS TURNED OFF ON THIS PATH
// ---------------------------------------------------
// This is the part that matters.
//
// The virtual display is attached by the app when it opens the
// spatial display, and detached when it closes it.  But the app can
// also DIE - a crash, a Task Manager "End task", a power cut - and
// then the detach never runs.  The next start finds a virtual
// monitor already on the desktop, in Extended mode, at a position the
// user cannot explain.
//
// A leftover virtual monitor is a trap: it is a rectangle that
// swallows windows, and a window dragged onto it ends up behind the
// glasses where it is very hard to get back.  The user's own complaint
// about the virtual display was exactly this.
//
// So the startup path checks the virtual display's REAL state - the
// same CCD detach the rest of the app uses - and takes it off the
// desktop if it is still there.  That makes the lifecycle
// self-healing: the desktop is put back the way it was found, even
// after a crash.
//
// WHY "DETECTED" AND NOT "OPENED"
// -------------------------------
// The presence check ENUMERATES the USB HID devices, it does not open
// one.  That is deliberate:
//
//   * enumeration is a pure query - it cannot interfere with anything
//     the user is doing, and it cannot fail because another program
//     holds the device;
//
//   * opening the device CAN legitimately fail while the Rokid Max is
//     perfectly plugged in - the official Rokid Station runtime holds
//     the IMU interface exclusively, and the app's own tracking
//     service reports that case separately.  If the startup gate
//     opened the device, a machine with Rokid Station running would
//     be told "plug in your glasses" while the glasses were in the
//     user's face.
//
// So the gate answers "are the glasses there?" and the tracking
// service answers "can I talk to them?".
using System.IO;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.HidCapture;

namespace RokidDiyStation.App;

/// <summary>
/// What the startup gate decided.
/// </summary>
/// <param name="CanStart">Whether the app may proceed with its normal startup.</param>
/// <param name="Title">The title of the dialog shown when the app refuses to start.</param>
/// <param name="Message">The message shown to the user.</param>
/// <param name="Detail">A technical summary, written to the diagnostic log.</param>
public sealed record StartupOutcome(
    bool CanStart,
    string Title,
    string Message,
    string Detail);

/// <summary>
/// What happened to the virtual display on the way out of a start
/// that was refused.
/// </summary>
/// <param name="WasAttached">Whether the virtual display was on the desktop when the app started.</param>
/// <param name="IsDetached">Whether it is off the desktop now.</param>
/// <param name="Detail">A technical summary for the diagnostic log.</param>
/// <param name="StateKnown">
/// False when the display's state could not be read at all.  The two
/// flags are then meaningless, and the message has to say that
/// instead of guessing.
/// </param>
public sealed record VirtualDisplayCleanup(
    bool WasAttached,
    bool IsDetached,
    string Detail,
    bool StateKnown = true);

/// <summary>
/// The check that runs before the tray app is built.
/// </summary>
/// <remarks>
/// It lives in the app layer rather than the core because it is a
/// PRODUCT decision, not a maths one: the core knows how to read an
/// IMU and how to place a panel, and it has no opinion about whether
/// the app is allowed to start.
/// </remarks>
public static class StartupPreflight
{
    /// <summary>
    /// The exit code the app returns when the startup gate refuses to
    /// let it run.
    /// </summary>
    /// <remarks>
    /// It is not zero, so a launcher script can tell "the app ran" from
    /// "the app refused to start because the glasses were missing".
    /// </remarks>
    public const int ExitCodeNoDevice = 2;

    /// <summary>
    /// Run the gate against the real machine.
    /// </summary>
    /// <param name="forceDeviceAbsent">
    /// Skip the hardware probe and behave as if the glasses were not
    /// plugged in.
    /// </param>
    /// <remarks>
    /// The override exists so the refusal path can be exercised on a
    /// machine where the glasses ARE plugged in - which is the normal
    /// development machine.  Without it, the "no glasses" branch could
    /// only ever be tested by unplugging the hardware.
    ///
    /// It is deliberately not a hidden switch: it is a command-line
    /// flag, so it is visible in the process list and in the
    /// documentation.
    /// </remarks>
    public static StartupOutcome Evaluate(bool forceDeviceAbsent = false) => Evaluate(
        forceDeviceAbsent
            ? () => (false, "The device probe was skipped (--no-device).")
            : ProbeDevice,
        EnsureVirtualDisplayDisabled);

    /// <summary>
    /// The gate's decision, with the probes injected.
    /// </summary>
    /// <remarks>
    /// The probes are parameters so the DECISION can be tested without
    /// the hardware: a test cannot unplug the glasses, but it can
    /// certainly say "pretend the glasses are absent" and check that
    /// the app refuses to start and asks for the virtual display to be
    /// taken off the desktop.
    /// </remarks>
    /// <param name="probeDevice">Reports whether the Rokid Max is attached.</param>
    /// <param name="cleanupVirtualDisplay">
    /// Makes sure the virtual display is off the desktop.
    /// </param>
    internal static StartupOutcome Evaluate(
        Func<(bool Present, string Detail)> probeDevice,
        Func<VirtualDisplayCleanup> cleanupVirtualDisplay)
    {
        var (present, deviceDetail) = probeDevice();

        if (present)
        {
            // NOTHING ELSE HAPPENS ON THIS PATH.  The virtual display
            // is NOT touched: the app is about to open it, and the
            // whole point of the gate is to leave the machine exactly
            // as the normal startup expects to find it.
            return new StartupOutcome(
                CanStart: true,
                Title: "Rokid DiyStation",
                Message: "The Rokid Max is connected.",
                Detail: deviceDetail);
        }

        // THE GLASSES ARE NOT THERE.
        //
        // The virtual display is checked and taken off the desktop
        // BEFORE the user is told anything, because the user has to be
        // able to trust the message: "the virtual display is gone" has
        // to be TRUE when the dialog appears, not something that
        // happens afterwards, and not something that never happens
        // because the app quit first.
        var cleanup = cleanupVirtualDisplay();

        var message = new StringBuilder();
        message.AppendLine("The Rokid Max is not connected.");
        message.AppendLine();
        message.AppendLine(
            "Plug the Rokid Max into the USB-C port of this PC, then start "
            + "Rokid DiyStation again.");
        message.AppendLine();
        message.AppendLine(DescribeCleanup(cleanup));

        var detail = new StringBuilder();
        detail.AppendLine(deviceDetail);
        detail.AppendLine(cleanup.Detail);

        return new StartupOutcome(
            CanStart: false,
            Title: "Rokid DiyStation - the Rokid Max is not connected",
            Message: message.ToString(),
            Detail: detail.ToString());
    }

    private static string DescribeCleanup(VirtualDisplayCleanup cleanup)
    {
        if (!cleanup.StateKnown)
        {
            return "The virtual display's state could not be read, so it was "
                 + "left alone.  Please check Display Settings.";
        }

        if (!cleanup.WasAttached)
            return "The virtual display is already off the desktop.";

        return cleanup.IsDetached
            ? "The virtual display has been taken off the desktop."
            : "The virtual display could NOT be taken off the desktop - "
              + "please turn it off in Display Settings.";
    }

    // -----------------------------------------------------------------
    // The probes
    // -----------------------------------------------------------------

    /// <summary>
    /// Whether the Rokid Max is attached, detected from the real
    /// hardware.
    /// </summary>
    /// <remarks>
    /// Two independent signals are used, and EITHER of them is enough:
    ///
    ///   * the USB HID device - the glasses' IMU interface, vendor
    ///     0x04D2.  This is the signal the tracking service uses, so
    ///     the gate and the tracker can never disagree about whether
    ///     the glasses are there.
    ///
    ///   * the display - the glasses enumerate as a DisplayPort
    ///     secondary monitor with the Lontium bridge's EDID.  This
    ///     catches the case where the HID stack has not enumerated the
    ///     device yet but the display already has.
    ///
    /// The HID probe identifies the glasses by their PRODUCT ID or by
    /// their real product name - never by the vendor ID alone, which
    /// is shared with a lot of unrelated HID devices.  See
    /// <see cref="RokidDeviceProbe"/> for why that matters.
    /// </remarks>
    private static (bool Present, string Detail) ProbeDevice()
    {
        var usb = RokidDeviceProbe.Check();
        if (usb.Present) return (true, usb.Detail);

        try
        {
            foreach (var d in DisplayLocator.Enumerate())
            {
                if (!IsRokidDisplay(d)) continue;
                return (true, $"Display: {d.DeviceName} {d.Width}x{d.Height} ({d.MonitorId})");
            }
        }
        catch { /* fall through to "not present" */ }

        return (false, usb.Detail);
    }

    /// <summary>
    /// Whether a display is the Rokid Max's own display.
    /// </summary>
    /// <remarks>
    /// The fingerprint is deliberately NARROWER than
    /// <see cref="DisplayLocator"/>'s, which also accepts the generic
    /// "USB Display" description.  For the renderer's purposes that
    /// is a reasonable guess; for the startup gate it is not, because
    /// a wrong guess here means the app refuses to start on a machine
    /// that has some other USB display plugged in.
    ///
    /// "LBT4753" is the Lontium DisplayPort bridge chip inside the
    /// Rokid Max, and it is the one piece of hardware-specific
    /// identity the glasses expose.
    /// </remarks>
    private static bool IsRokidDisplay(DisplayTarget display)
    {
        foreach (var text in new[] { display.DeviceName, display.MonitorId })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (text.Contains("Rokid", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("LBT", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("Lontium", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Make sure the virtual display is off the desktop.
    /// </summary>
    /// <remarks>
    /// This is the app's normal, reversible CCD detach - not the
    /// legacy zero-width mode, which is a one-way door that would
    /// leave the virtual display unable to come back on the next
    /// start.  See <see cref="VirtualDisplayController.Close"/>.
    ///
    /// It needs no administrator rights: the detach removes the
    /// display's PATH from the desktop and leaves the adapter attached.
    /// </remarks>
    public static VirtualDisplayCleanup EnsureVirtualDisplayDisabled()
    {
        bool wasAttached;
        try { wasAttached = VirtualDisplayController.IsAttached(); }
        catch (Exception ex)
        {
            return new VirtualDisplayCleanup(
                WasAttached: false,
                IsDetached: false,
                StateKnown: false,
                Detail: $"The virtual display's state could not be read: {ex.Message}");
        }

        if (!wasAttached)
        {
            return new VirtualDisplayCleanup(
                WasAttached: false,
                IsDetached: true,
                Detail: "The virtual display was already off the desktop.");
        }

        bool closed;
        try { closed = VirtualDisplayController.Close(); }
        catch (Exception ex)
        {
            return new VirtualDisplayCleanup(
                WasAttached: true,
                IsDetached: false,
                Detail: $"The virtual display could not be taken off the desktop: {ex.Message}");
        }

        // The CCD's view and the shell's view can disagree, and the
        // shell's is the one the user can see, so the shell is asked
        // as well before the app claims the display is gone.
        bool gone;
        try { gone = !VirtualDisplayController.IsAttached(); }
        catch { gone = closed; }

        return new VirtualDisplayCleanup(
            WasAttached: true,
            IsDetached: closed && gone,
            Detail: closed
                ? "The virtual display was on the desktop and was taken off it."
                : "The virtual display was on the desktop and could NOT be taken off it.");
    }

    // -----------------------------------------------------------------
    // Diagnostics
    // -----------------------------------------------------------------

    /// <summary>
    /// Tell the user why the app is not going to run.
    /// </summary>
    /// <remarks>
    /// The Win32 MessageBox is used rather than WPF's, and the reason
    /// is the ORDER of things: the gate runs BEFORE the WPF
    /// Application exists, so there is no dispatcher and no visual
    /// tree to hang a WPF window on.  WPF's MessageBox would have to
    /// spin up a dispatcher of its own, and it does so by showing a
    /// window that does not inherit the app's visual style and, worse,
    /// may not appear at all when the process has no message pump
    /// yet.
    ///
    /// The native box always appears, always has an owner window (so
    /// it cannot end up behind the fullscreen panel on the glasses'
    /// display), and it is modal to the thread that shows it.
    /// </remarks>
    public static void ShowRefusal(StartupOutcome outcome)
    {
        try
        {
            NativeMessageBox.Show(
                outcome.Message,
                outcome.Title,
                NativeMessageBox.IconWarning);
        }
        catch
        {
            // A dialog is not worth crashing over.  The diagnostic log
            // already carries the reason.
        }
    }

    /// <summary>
    /// Append a line to the diagnostic log, so a start that refused to
    /// run can still be explained afterwards.
    /// </summary>
    public static void Log(string line)
    {
        try
        {
            var path = TrackingService.DiagnosticLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }
}
