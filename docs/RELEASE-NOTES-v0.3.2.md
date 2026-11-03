## The virtual display really stays off

With one or two physical monitors, closing the spatial display - or quitting
the app - could leave the virtual monitor on the Windows desktop in Extended
mode, usually displaced upwards or to the left. With three, it worked.

The number of monitors was a symptom, not the cause. The cause was **where
Windows keeps the truth about your display layout**, and the app was only
telling Windows about the change in the one place it did not look.

### Windows rebuilds the desktop from its database

When the display topology changes - a monitor is plugged in, one is unplugged,
a resolution is changed - Windows rebuilds the desktop layout **from its own
persistent display-configuration database**, not from whatever the last
program told it.

The app's detach was applied as a session-only change, on the reasoning that
a session-only change is reversible and leaves your display settings alone.
Both halves of that are true, and together they caused the bug: the database
still recorded the virtual display as part of the active topology, so the
next rebuild put it straight back - at whatever position the database
remembered.

And the position was not a guess. It was in this machine's registry, under
`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration`:

```
Position.cx = 1915
Position.cy = 4294966216     <- read as a signed 32-bit integer: -1080
```

So the database had the virtual display at **(1915, -1080)** - above the
physical monitors and shifted sideways. Precisely what was reported.

The detach now saves to the database, so the session and the database agree,
and Windows stops rebuilding the desktop against the app's back. The attach
does the same, which makes the two halves of the lifecycle the same
operation on the same state - and that is what makes it reversible.

### Disconnect, do not omit

There are two things you can do to a path in a display configuration, and
only one of them means what we want:

- **Omit** the path, and you are saying *"I am not telling you about this
  display."* Windows is left with a connected target the configuration does
  not account for, so it falls back to its database - and the database still
  says the display is on.
- **Disconnect** the path - keep it, clear `DISPLAYCONFIG_PATH_ACTIVE`, and
  set both mode indices to `DISPLAYCONFIG_PATH_MODE_IDX_INVALID` - and you
  are saying *"this display is connected, and it has been turned off."*

That is exactly what the Windows Display Settings app sends for "disconnect
this display", and Windows records it as intentional. The detach now does
that.

It also disconnects **every** virtual path, not just the first one found.
The IddCx driver re-publishes its monitor when the topology changes, and
Windows can be left with two active paths for the same virtual target. The
old code dropped one and kept the other, so the display stayed on the
desktop while the app reported success.

### A path's mode index belongs to one query

A path's `modeInfoIdx` is an index into the mode array **of the query that
produced that path**. The active-path query and the database query return
the same paths with different mode arrays, and the indices do not line up.

The old code took the paths from the active query and the positions from the
database, which means it sent back positions that Windows had already
refused - and every `SetDisplayConfig` call answered 87
(`ERROR_INVALID_PARAMETER`). The detach silently did nothing.

Source modes are now matched by **identity** - the adapter and id, which are
stable across queries - instead of by index.

### Two monitors: the failure that survived the fix above

Closing the spatial display still failed on a **two-monitor** machine - the
laptop panel and a drawing display, with the Rokid Max not plugged in. The
app logged success and the rectangle stayed on the desktop.

The cause was the same database, read differently. The detach takes the
survivors' positions from the database, and the database holds one record per
topology the machine has ever been in. With two monitors, the current record
did not always hold a position for every survivor - and when the lookup
failed, the code answered **(0, 0)**. That is not "unknown": it is the
origin, which is where the laptop panel is. The survivor was sent **on top
of the laptop panel**, and Windows rejects overlapping monitor rectangles
outright, so the whole configuration was refused with 87 and the detach
silently did nothing.

An unknown position is now reported as unknown, and the position the live
session already has is kept. A position that is known to be wrong is worse
than one that is merely unknown.

And when the stale positions *are* found, they can still overlap - a
leftover record that puts the virtual display at (1915, -1080) is a row
above the physical monitors, shifted sideways. The layout is now repaired
before it is sent, and the repair respects **rows**: a display sitting above
the others is on a row of its own and is left exactly where it is. An
earlier version of the repair collapsed everything onto one row, which fixed
the overlap by moving the user's own screens.

The app also no longer trusts a single view of the desktop. The display
configuration and the Windows shell can disagree for a moment - the
configuration says the path is inactive while the shell is still drawing the
rectangle - and the old code declared success on the configuration's view
alone. Both are asked now, in the detach and in the topology reconciler.

### What changed for you

- Closing the spatial display takes the virtual monitor off the desktop, and
  it **stays** off - a hot-plug no longer brings it back.
- It now works the same way with **one, two or three** monitors. The number
  of monitors was never the cause; a half-written record in Windows' own
  display database was.
- The position the virtual display appears at is now the one the app
  actually used, not a stale one from an old layout.
- Your own screens are never rearranged as a side effect. The fallback
  layout packs them in the left-to-right order you already have them in.
- Quitting the app tears the display down the same way. The detach is
  idempotent, and a monitor hotplug that finds the display already off is no
  longer logged as an error.
- Everything still works the other way round: opening the spatial display
  brings the virtual monitor back.

### How it was verified

Three probes were written for this, all in `tools/`:

- `ProbeVirtualStability` exercises the shipped attach and detach, and
  forces a topology rebuild - the same work a hot-plug does - to check that
  a detached display does not come back, that the database does not record
  it as active, and that the whole thing is still reversible.
- `ProbeAppTeardown` starts the actual `RokidDiyStation.exe`, asks it to
  quit through its own single-instance pipe, and watches the display every
  100 ms while the teardown runs.
- `ProbeTwoMonitorClose` runs the **two-monitor** case specifically: it
  starts the real app on the reported topology, asks it to close the
  spatial display through the same pipe, and then asks Windows what is
  actually on the desktop - through the shell's view, which is the one you
  see. It then drives three full open/close cycles, because a display that
  goes away and can never come back is not a working product either.

The end-to-end result: the display goes off **104 ms** after the quit, the
database agrees with the session throughout, and it stays off for **5809 ms**
- long enough that a topology rebuild cannot resurrect it. After the app has
exited, a forced rebuild leaves it off.

On the two-monitor machine the old code leaves the virtual display on the
desktop through both the close and a forced rebuild; the fixed code takes it
off, brings it back, and takes it off again - three times over.

The full diagnosis, with the measurements, is in
`docs/M15-ISSUE-2-TWO-MONITOR-CLOSE.md`.

### Tests

333 passing (130 app + 203 core).

### Running it

Download `RokidDiyStation.exe`, run it, and it appears in the tray. It is
self-contained - no .NET install needed.

The app needs the Rokid Max connected over USB HID. It also needs the
**Virtual Display Driver** (`VirtualDrivers.Virtual-Display-Driver`) - that
one is a separate install and does require administrator rights. The
README has the details.
