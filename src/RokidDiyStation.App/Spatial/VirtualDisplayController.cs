// SPDX-License-Identifier: MIT
//
// The virtual display's lifecycle: opening it and closing it.
//
// THE PROBLEM THIS SOLVES
// -----------------------
// The user's complaint was that the virtual display - the canvas the
// Rokid Max panel is drawn on - stayed on the desktop even when the
// app was not running.  A window dragged onto it could then be lost
// behind the glasses, effectively unreachable.
//
// So the virtual display has to be GONE when the spatial display is
// closed, and it has to come BACK reliably when it is opened again.
//
// WHY THE FIRST TWO ATTEMPTS AT THIS DID NOT WORK
// -----------------------------------------------
// Attempt 1: the legacy ChangeDisplaySettingsEx with dmPelsWidth = 0.
// It works - and it is a ONE-WAY DOOR:
//
//   * the change is applied with CDS_UPDATEREGISTRY, so a zero-width
//     mode is PERSISTED in the registry under the display device's
//     key;
//   * a zero-width mode is not a mode, so EnumDisplaySettings on that
//     adapter fails from then on;
//   * and every later attempt to put the monitor back fails with
//     DISP_CHANGE_FAILED (-1), because the API now has no valid
//     current mode to modify.
//
// Attempt 2: the driver's own control pipe, "\\.\pipe\
// MTTVirtualDisplayPipe", with SETDISPLAYCOUNT 0.
//
// THAT ONE IS WORSE THAN A ONE-WAY DOOR - IT BREAKS THE DRIVER.
// The driver tears the adapter down and re-creates it to publish a
// different number of monitors, and in that state it comes back in
// error code 43 (the device is started but not working).  The pipe
// then stops answering, and the monitor cannot be brought back at
// all without an ELEVATED driver re-install.
//
// It was tried, it was verified to be lethal, and it is not used here.
//
// THE MECHANISM THAT ACTUALLY WORKS
// --------------------------------
// The Windows display-configuration API - CCD - QueryDisplayConfig
// and SetDisplayConfig.
//
// The key difference from the legacy API: CCD removes a display PATH
// rather than tearing the adapter out of the desktop.  The adapter
// stays attached to the machine, it keeps publishing its monitor, and
// the path can be added back later with the same code.  That makes
// the lifecycle genuinely reversible.
//
// CCD also needs no administrator rights, which is what makes
// "open the spatial display" a plain user action.
//
// THE POSITION
// -------------
// The virtual monitor goes immediately to the LEFT of the leftmost
// display that is on the desktop.  It must not overlap the Rokid
// Max's own display: Windows forbids overlapping monitor rectangles
// outright, and our own WS_EX_TOPMOST opaque AR panel would occlude
// the capture.
//
// The position is COMPUTED, not hardcoded - see
// ComputeAttachPosition() for why a fixed coordinate is a bug.

using RokidDiyStation.Core.Spatial;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Opens and closes the IddCx virtual display.
/// </summary>
/// <remarks>
/// The lifecycle has two halves, and both are needed:
///
///   * <see cref="Close"/> removes the display's PATH from the
///     desktop, so the rectangle does not appear in Display Settings,
///     nothing can be dragged onto it, and nothing can get lost behind
///     the glasses.
///
///   * <see cref="Open"/> adds the path back, at the expected
///     position.
///
/// Both go through <see cref="DisplayConfigController"/>, which is the
/// verified implementation of the CCD calls.
/// </remarks>
public static class VirtualDisplayController
{
    /// <summary>
    /// The virtual desktop's left edge.
    /// </summary>
    /// <remarks>
    /// The virtual monitor is placed immediately to the LEFT of the
    /// primary display, so this is -1920 on a 1920-wide primary.
    /// </remarks>
    public const int OffsetX = -1920;

    /// <summary>
    /// The virtual display's pixel width.  It matches the Rokid Max's
    /// per-eye viewport exactly, so the cursor mapping and the
    /// captured texture line up one-to-one with no letterboxing.
    /// </summary>
    public const int Width = 1920;

    /// <summary>
    /// The virtual display's pixel height.
    /// </summary>
    public const int Height = 1080;

    /// <summary>
    /// Place the virtual display where nothing else is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The position used to be a constant, (-1920, 0), and that was a
    /// second, quieter version of the same bug the user reported: a
    /// hardcoded coordinate is a guess about the machine's layout, and
    /// the guess is wrong whenever the layout is different from the
    /// one the constant was written for.
    /// </para>
    /// <para>
    /// With one or two physical monitors, (-1920, 0) can land ON a
    /// physical display - Windows forbids overlapping monitor
    /// rectangles outright - and the attach then fails with 87
    /// (ERROR_INVALID_PARAMETER).  With three, it happened to be free.
    /// </para>
    /// <para>
    /// So the position is COMPUTED: the virtual display is placed to
    /// the LEFT of the leftmost display that is on the desktop, with a
    /// gap.  That is where it belongs anyway - it is the spatial
    /// canvas, it must not overlap the Rokid Max (our own topmost
    /// panel would occlude the capture), and it must not overlap any
    /// physical display (Windows forbids it).
    /// </para>
    /// <para>
    /// If there is nothing on the desktop at all, the historical
    /// position is used.
    /// </para>
    /// </remarks>
    /// <returns>The position to place the virtual display at.</returns>
    public static (int X, int Y) ComputeAttachPosition()
    {
        var monitors = VirtualDisplayLocator.Enumerate();

        // The virtual display itself is excluded: it may still be
        // listed from a previous session, and anchoring to itself
        // would make the position depend on where it used to be.
        var rects = new System.Collections.Generic.List<DesktopRect>();
        foreach (var m in monitors)
        {
            if (m.IsVirtual) continue;
            rects.Add(new DesktopRect(
                m.Bounds.Left, m.Bounds.Top,
                m.Bounds.Right, m.Bounds.Bottom));
        }

        return SpatialDisplayPlacement.Compute(rects, Width, Height, OffsetX, 0);
    }

    /// <summary>How long to wait for the monitor to appear, in ms.</summary>
    private const int AppearTimeoutMs = 8000;

    /// <summary>How long to wait for the monitor to vanish, in ms.</summary>
    private const int VanishTimeoutMs = 4000;

    /// <summary>
    /// Whether the virtual display is currently on the desktop.
    /// </summary>
    public static bool IsAttached()
        => VirtualDisplayLocator.FindVirtual() is not null
           || DisplayConfigController.IsVirtualActive();

    /// <summary>
    /// Open the virtual display.
    /// </summary>
    /// <returns>True when the display is on the desktop afterwards.</returns>
    public static bool Open()
    {
        // The position is COMPUTED, not hardcoded.  A fixed (-1920, 0)
        // lands on a physical display when the desktop layout is
        // different from the one the constant was written for, and
        // Windows refuses overlapping displays outright - so the
        // attach would fail on exactly the machines whose layout had
        // changed.
        var (x, y) = ComputeAttachPosition();

        // The CCD attach is the whole mechanism: the adapter is
        // always attached to the machine, so there is nothing to
        // "publish" first.
        if (!DisplayConfigController.Attach(x, y))
        {
            // It may already be attached - which is a success, not a
            // failure.  The user asked for it to be open, and it is.
            return IsAttached();
        }

        return WaitForMonitor(AppearTimeoutMs) is not null;
    }

    /// <summary>
    /// Close the virtual display.
    /// </summary>
    /// <remarks>
    /// This is the operation that had to be reversible, and it is:
    /// the adapter stays attached to the machine, so
    /// <see cref="Open"/> can bring the monitor straight back.
    /// </remarks>
    /// <returns>True when the display is off the desktop afterwards.</returns>
    public static bool Close()
    {
        if (!DisplayConfigController.Detach())
        {
            // THE LEGACY DETACH IS NOT USED AS A FALLBACK.
            //
            // It used to be.  The legacy detach takes the monitor off
            // the desktop by setting a zero-width mode, and that is a
            // ONE-WAY DOOR: the adapter's StateFlags drop from 0x1 to
            // 0x0, EnumDisplaySettings fails for that adapter from
            // then on, and every later re-attach attempt - through
            // the legacy API OR through CCD - answers DISP_CHANGE_FAILED
            // (-1).
            //
            // So if the CCD detach fails, the virtual display stays
            // where it is.  A visible virtual display is a cosmetic
            // problem; a virtual display that can never come back is
            // a broken product.
            return false;
        }

        return WaitForMonitorGone(VanishTimeoutMs);
    }

    // -----------------------------------------------------------------
    // Waiting
    // -----------------------------------------------------------------

    private static MonitorInfo? WaitForMonitor(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var monitor = VirtualDisplayLocator.FindVirtual();
            if (monitor is not null) return monitor;
            Thread.Sleep(150);
        }
        return null;
    }

    private static bool WaitForMonitorGone(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (VirtualDisplayLocator.FindVirtual() is null) return true;
            Thread.Sleep(150);
        }
        return VirtualDisplayLocator.FindVirtual() is null;
    }
}
