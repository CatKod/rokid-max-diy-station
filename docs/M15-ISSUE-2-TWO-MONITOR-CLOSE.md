# Issue 2 — the two-monitor close

**Symptom.** On a machine with **two** physical monitors — the laptop
panel and the XP-Pen Artist — closing the spatial display did **not**
take the virtual display off the Windows desktop. With one monitor it
worked. With three it worked.

**Status:** fixed, verified end to end on the user's own machine.
**Date:** 2026-11-03

---

## 1. The number of monitors was never the cause

Issue 1 ([M14-ISSUE-1-CCD-DETACH-DIAGNOSIS.md](M14-ISSUE-1-CCD-DETACH-DIAGNOSIS.md))
explained the *displacement*: the virtual display was persisted at
(1915, −1080) in the registry, and Windows rebuilt the desktop from
that record.

But that fix was about **where** the display came back. It did not
explain why, on a two-monitor machine, the detach did not take the
display away **at all** — the app logged success and the rectangle
stayed on the desktop.

The obvious hypothesis is that "two monitors" was a special case that
needed its own branch. It is not. What actually differs between one,
two and three monitors is **whether the display-configuration database
happens to hold a self-consistent record of the current topology**, and
the two-monitor topology is the one where it did not.

## 2. Two views of the desktop, and the code only asked one

There are two ways to ask Windows what is on the desktop, and they do
not agree:

* **the CCD's view** — `QueryDisplayConfig`. This is what the attach
  and the detach are built on.
* **the shell's view** — `EnumDisplayMonitors`. This is what the user
  SEES: the rectangle they can drag a window onto.

The detach declared success on the CCD's view alone:

```csharp
if (Apply(kept, modes)) return !IsVirtualActive();
```

If the IddCx driver has not yet torn down its shell monitor object,
the CCD says "gone" while the shell is still drawing the rectangle.
The user sees a display the code has already written off.

The `Detach()` path now asks **both** views, in
`WaitForVirtualGone` and in the "already detached" case:

```csharp
private static bool ShellHasVirtualMonitor()
    => VirtualDisplayLocator.FindVirtual() is not null;
```

The reconciler in `SpatialStation.OnTopologyChanged` had the same
blind spot — it compared the desired state against
`DisplayConfigController.IsVirtualActive()` and, when the two views
disagreed, concluded that everything was in order and did nothing.
It now compares against `VirtualDisplayController.IsAttached()`, which
asks both.

## 3. The real cause of the *failure*: a missing position became the origin

This is the part that only ever showed up with two monitors.

The detach sends the survivors' positions from the **database**
(`QDC_DATABASE_CURRENT`), because that is the arrangement Windows
will apply. The database holds one record per topology the machine has
ever been in, and which record is current depends on which monitors
are plugged in.

The old code looked the position up like this:

```csharp
private static (int X, int Y) SourcePositionOf(
    DISPLAYCONFIG_MODE_INFO[] modes, DISPLAYCONFIG_PATH_INFO path)
{
    for (int m = 0; m < modes.Length; m++) { /* ... */ }

    return (0, 0);        // <-- the origin
}
```

When the database's current record held no source mode for a
survivor, the answer was **(0, 0)** — and (0, 0) is not "unknown",
it is **the origin, which is where the laptop panel is**. So the
survivor was sent **on top of the laptop panel**.

Windows forbids overlapping monitor rectangles outright: a
configuration whose displays overlap is rejected with **87**
(`ERROR_INVALID_PARAMETER`), and there is no way to apply it. So the
detach sent an invalid configuration, Windows refused it, and the
virtual display stayed on the desktop while the app reported success.

The lookup now **reports the failure** instead of inventing a
position, and the caller keeps the position the live session already
has:

```csharp
private static bool TrySourcePositionOf(
    IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes,
    DISPLAYCONFIG_PATH_INFO path,
    out int x, out int y)
```

A position that is known to be wrong is worse than one that is merely
unknown — and the live position is the one the user is looking at.

### Why two monitors, and not one or three

With one monitor, or with three, the record that happened to be
current held a path for every survivor, so the lookup always
succeeded and the configuration was valid. With two, it did not. That
is the whole asymmetry — no special case, just a database record that
happened to be incomplete.

## 4. Stale positions can overlap on their own

Even when every survivor's position IS found, the database's record
can be internally inconsistent. The record for the topology that
included the Rokid Max holds the virtual display at (1915, −1080),
which is a row **above** the physical monitors and shifted sideways —
and a stale position is very often an overlapping one.

So the layout is now **repaired before it is sent**
(`DisplayConfigController.RepairLayout`, which calls
`SpatialDisplayPlacement.RepairOverlaps`).

The repair is **row-aware**, and that detail matters. The first
version of it collapsed every display onto a single row, which fixed
the overlap but broke the user's arrangement: a display that is
*above* the others is on a row of its own, and dragging it down onto
the physical monitors' row moved the user's own screens. Now the
displays are grouped by the row they are on, and only the displays
that **share** a row are rearranged among themselves:

```csharp
private static bool SharesRow(DesktopRect a, DesktopRect b)
    => a.Top < b.Bottom && b.Top < a.Bottom;
```

## 5. The fallback that reversed the user's screens

If the first attempt is refused, the detach retries with the survivors
**packed from the origin**, so that no two of them overlap. The old
code packed them in the **array's** order:

```csharp
int cursorX = 0;
foreach (var p in paths) { SetSourcePosition(packedModes, p, cursorX, 0); cursorX += w; }
```

The path array's order is not the left-to-right order the user sees,
so this could put the XP-Pen to the **left** of the laptop panel. A
rearranged desktop is a much worse answer to "take the virtual display
away" than a gap on the left.

The survivors are now sorted by their **live left coordinate** before
they are packed:

```csharp
survivors.Sort((a, b) => a.Left.CompareTo(b.Left));
```

## 6. The A/B proof, on the user's own machine

The machine was in exactly the reported topology: the laptop panel at
(0, 0), the XP-Pen at (1920, 0), the Rokid Max **not** plugged in.

`tools/ProbeTwoMonitorClose` starts the real
`artifacts/release/RokidDiyStation.exe`, asks it to close the spatial
display through the app's own single-instance pipe, and then asks
Windows what is on the desktop — the shell's view, without any
attached-to-desktop filter, because a monitor Windows has taken off the
desktop still appears in `EnumDisplayMonitors` for a while.

**Against the old code (the fix stashed):**

```
=== asking the app to CLOSE THE SPATIAL DISPLAY ===
  the signal was delivered: True
      \\.\DISPLAY2             (1920,0)-(3840,1080)
      \\.\DISPLAY1             (0,0)-(1920,1080)
      \\.\DISPLAY9             (-1920,0)-(0,1080)   <== THE VIRTUAL DISPLAY

=== after a topology rebuild ===
      \\.\DISPLAY2             (1920,0)-(3840,1080)
      \\.\DISPLAY1             (0,0)-(1920,1080)
      \\.\DISPLAY9             (-1920,0)-(0,1080)   <== THE VIRTUAL DISPLAY
  [FAIL] the SHELL no longer lists the virtual monitor
  [FAIL] the CCD view agrees the virtual display is off

=== 2 CHECK(S) FAILED ===
```

**Against the fixed code:**

```
=== asking the app to CLOSE THE SPATIAL DISPLAY ===
  the signal was delivered: True
      \\.\DISPLAY2             (1920,0)-(3840,1080)
      \\.\DISPLAY1             (0,0)-(1920,1080)

  [PASS] the SHELL no longer lists the virtual monitor
  [PASS] the CCD view agrees the virtual display is off

=== asking the app to OPEN THE SPATIAL DISPLAY AGAIN ===
      \\.\DISPLAY9             (-1920,0)-(0,1080)   <== THE VIRTUAL DISPLAY
  [PASS] the virtual display is back on the desktop

=== and CLOSING IT A SECOND TIME ===
      \\.\DISPLAY2             (1920,0)-(3840,1080)
      \\.\DISPLAY1             (0,0)-(1920,1080)
  [PASS] the SHELL no longer lists the virtual monitor after the second close

=== and once more, to be sure it is not a one-shot ===
  [PASS] the virtual display survives three open/close cycles

=== ALL CHECKS PASSED ===
```

The close is only half of the contract: a display that goes away and
can never come back is a broken product. The probe therefore drives
**three full open/close cycles** and checks both views at each step.

### A note on the probe's own first version

The probe's first run reported `[FAIL] the virtual display is on the
desktop` while the shell was plainly listing it. The probe was wrong,
not the app: it passed the shell's device name (`\\.\DISPLAY9`) to
`VirtualDisplayLocator.IsVirtualMonitorId`, which matches on the
**EDID**. The shell's device name carries no EDID, so the identity has
to be resolved through the adapter — which is exactly what
`VirtualDisplayLocator.Enumerate()` already does.

A probe that cannot tell a virtual monitor from a physical one
measures nothing, so this is worth writing down.

## 7. The changes

* `DisplayConfigController.SourcePositionOf` no longer answers (0, 0)
  when the lookup fails; `TrySourcePositionOf` reports it and the
  detach keeps the live position.
* `DisplayConfigController.RepairLayout` repairs an overlapping layout
  before it is sent, using the row-aware
  `SpatialDisplayPlacement.RepairOverlaps`.
* The detach's fallback packs the survivors in their **live** left-to-
  right order, so the user's own screens are never reversed.
* `DisplayConfigController.WaitForVirtualGone` and the "already
  detached" case ask the **shell** as well as the CCD.
* `SpatialStation.OnTopologyChanged` compares the desired state
  against `VirtualDisplayController.IsAttached()` — both views — and
  not against the CCD alone.
* `SingleInstance` grew a `close-spatial` command, and then an
  `open-spatial` one, so the close and the reopen can both be driven
  without a mouse click. The whole round trip is now scriptable.

## 8. The tests

`tests/RokidDiyStation.Core.Tests/SpatialDisplayPlacementTests.cs`:

* `TouchingDisplaysDoNotOverlap` — sharing an edge is not an overlap.
* `ADisplayOnItsOwnRowIsNotMoved` — the row-awareness, and the
  regression it exists to prevent.
* `OverlappingDisplaysOnTheSameRowAreSeparated`.
* `TheRepairNeverLeavesAnOverlap`.
* `ACleanLayoutIsNotTouchedAtAll` — the common case allocates
  nothing.

333 passing (130 app + 203 core).

## Sources

- Microsoft Learn: [SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)
- Microsoft Learn: [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- Microsoft Learn: [EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)
- SDK header read directly: `Windows Kits 10.0.26100.0\um\wingdi.h`
