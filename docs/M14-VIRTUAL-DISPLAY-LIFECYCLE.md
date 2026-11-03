# Issue 1: the virtual display would not stay disabled

**Status:** fixed, verified end to end, shipped in v0.3.2.
**Date:** 2026-11-03

## The symptom

The user reported that the virtual display (the VDD, `MTT1337`) stayed on
the Windows desktop in Extended mode after the spatial display was
closed. The reporter's own hypothesis was that the VDD was being found
by a hardcoded position or index, and that a topology change moved it
out of reach.

**That hypothesis was wrong, and it was already fixed in v0.3.1.** The
identification is by hardware identity - the EDID fingerprint
`MTT1337` in the target's device path, the friendly name `VDD by MTT`,
and the raw EDID manufacturer/product codes. None of those change when
the topology changes.

## What the evidence actually showed

### The CCD detach works, on its own.
Measured with the app stopped, on the user's own machine:

```
=== ROUND 1 ===
  [diag] SetDisplayConfig with 1 path(s):
  [diag]   validate[0x24A0] = 87
  [diag]   validate[0x4A0] = 0
  [diag]   apply[0x4A0] = 0
  Detach() returned True
    t=+0   ms  present=False
  -> the monitor stayed off the desktop.
```

Three rounds, three passes. The removal sticks, and the attach brings
it back.

**BUT: this experiment was too narrow, and it is what led the
investigation down the wrong path for a while.** It tested the removal
with the app stopped, and it tested the flag combinations - including
`SDC_SAVE_TO_DATABASE` - but it never tested what happens AFTER the
removal, when the topology changes. A removal that is immediately
undone by Windows' own database rebuild looks exactly like a removal
that worked, if you only look at the next poll. The evidence in the
next section is what turned it around.

The `SDC_ALLOW_PATH_ORDER_CHANGES` rejection with 87 that the log
shows is not a bug either: the SDK requires `SDC_TOPOLOGY_SUPPLIED`
with it, and the flag set that validates is the flag set that gets
applied.

### The removal is undone within 600ms - by the app

The same experiment, with the app running, gives a different answer:

```
    t=-0ms      present=True
    t=+100 ms  present=False
    t=+600 ms  present=True
```

and the app's own log says who did it:

```
[19:37:58.498] The virtual display went away.
[19:37:59.134] The display layout changed and the spatial display went with it - putting it back.
[19:37:59.181] Display config: SetDisplayConfig with 2 path(s):
[19:37:59.181] The spatial display is back.
```

The topology watcher is doing exactly what it was written to do: the
virtual display is not where the user wants it, so it puts it back.
That is correct behaviour while the user wants the spatial display
open.

### The real defects

**1. The detach is not verified over time, and it is not idempotent.**

`Detach()` checked the result IMMEDIATELY:

```csharp
if (Apply(kept, modes)) return !IsVirtualActive();
```

A single check right after the call is not a measurement. The removal
can be undone a few hundred milliseconds later, and the function has
already returned `true`.

Worse, when the virtual display was ALREADY gone - which is what
happens when Windows re-lays-out the desktop during a hot-plug - the
detach reported failure:

```
[17:00:19.531] The virtual display went away.
[17:00:28.922] The virtual display could not be detached. The legacy fallback is not used because it would make the display impossible to reopen.
[17:00:28.924] The virtual display could not be detached.
```

The user is told the detach failed, when in fact the virtual display
was already off the desktop. `Detach()` returned `false` because it
found no *active* virtual path to remove - and a detach of something
that is already detached is a success, not a failure.

**2. The attach can put the virtual display on top of a physical one.**

From the user's own log, with one physical monitor:

```
[16:55:50.033] Spatial display open: \\.\DISPLAY9 1920x1080 at (1915,-1080).
```

`1915` is five pixels inside the laptop panel, which occupies
x = 0..1919. Windows forbids overlapping monitor rectangles, and an
attach that overlaps is rejected with 87 - every flag set:

```
[diag]   validate[0x24A0] = 87
[diag]   validate[0x4A0] = 87
[diag]   validate[0xA0] = 87
[diag]   validate[0x80] = 87
```

The position is computed rather than hardcoded now, but the computation
is done from a snapshot of the desktop, and the desktop moves between
the computation and the apply. Nothing checked the result.

**3. The CCD database and the session disagree.**

`QDC_DATABASE_CURRENT` does not contain the virtual display at all,
even while it is on the desktop. The database says "no virtual
display"; the session says "here it is". Windows rebuilds the desktop
from the database when the topology changes, which is why the virtual
display vanishes and reappears on its own during a hot-plug - and why
the reconciler and Windows end up fighting over it.

**4. THE PERSISTED POSITION WAS STALE, AND NEGATIVE.**

This is the part that explained the *displacement*, and it was only
visible in the registry - the CCD query shows the position, not the
reason the position is wrong.

`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration`
holds one composite key per topology the machine has been in, and each
key's subkeys hold the persisted `Position.cx` and `Position.cy`. The
key for the connected topology, on the virtual display's subkey, held:

```
Position.cx = 1915
Position.cy = 4294966216
```

`4294966216` read as a signed 32-bit integer is `-1080`. So the
database had the virtual display at **(1915, -1080)** - above the
physical monitors and shifted sideways, which is exactly what the
user described.

And the reason the display came back *there* is that Windows rebuilds
the desktop FROM THIS DATABASE. The detach did not persist anything,
so the database kept saying "the virtual display is active, at
(1915, -1080)", and the next rebuild restored it to that stale
position.

The numbers also explain the 1-vs-3 asymmetry without any special
case: with three monitors, the active session's arrangement and the
database's arrangement happened to agree, so the detach's session-only
change looked like it worked. With one or two, the database carried
the stale entry from a topology the machine had not been in since, and
the rebuild resurrected the display at the old, wrong place.

## The fix

* `Detach()` is idempotent: "already detached" is success.
* `Detach()` VERIFIES over a settle window and retries if the virtual
  display comes back, instead of checking once and returning.
* `Detach()` and `Attach()` both persist with `SDC_SAVE_TO_DATABASE`,
  so the database and the session agree, and Windows stops rebuilding
  the desktop against the app's back.
* The detach EXPLICITLY DISCONNECTS the virtual path - it keeps the
  path in the array with `PATH_ACTIVE` cleared and both mode indices
  set to `DISPLAYCONFIG_PATH_MODE_IDX_INVALID` - rather than omitting
  it. An omission leaves Windows with a connected-but-unaccounted
  target, and it falls back to the database. A disconnection is what
  the Windows Display Settings app sends, and Windows records it as
  intentional.
* EVERY virtual path is disconnected, not just the first one found.
  The IddCx driver re-publishes its monitor when the topology changes,
  and Windows can be left with two active paths for the same virtual
  target; the old code dropped one and kept the other.
* Source modes are matched by IDENTITY - the adapter and id - rather
  than by index. A path's `modeInfoIdx` is an index into the mode array
  of the query that produced it, and the active query and the database
  query return the same paths with different indices. The old code sent
  the active query's positions, which were the ones Windows had
  already refused, so every `SetDisplayConfig` answered 87.
* `Attach()` verifies that the virtual display landed where it was
  asked to, and that it does not overlap a physical display.
* `Close()` waits for the monitor to STAY gone, not just to be gone
  for one poll.

## How the fix was verified

Two probes were written, both in `tools/`:

- `ProbeVirtualStability` exercises the shipped attach and detach and
  forces a topology rebuild - the same work a hot-plug does.
- `ProbeAppTeardown` starts the actual `RokidDiyStation.exe`, asks it
  to quit through its own single-instance pipe, and watches the display
  every 100 ms while the teardown runs.

The end-to-end result: the display goes off 104 ms after the quit, the
database agrees with the session throughout, and it stays off for
5809 ms - long enough that a topology rebuild cannot resurrect it.
After the app has exited, a forced rebuild leaves it off.

The A/B proof against the old code is in
`docs/M14-ISSUE-1-CCD-DETACH-DIAGNOSIS.md`, and the deep research
behind it is in `docs/M14-CCD-TEARDOWN-DEEP-RESEARCH.md`.

## Sources

- Microsoft Learn: [SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)
- Microsoft Learn: [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- Microsoft Learn: [DISPLAYCONFIG_PATH_INFO](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_info)
- Microsoft Learn: [Connecting and Configuring Displays (CCD)](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/connecting-and-configuring-displays)
- SDK header read directly: `Windows Kits 10.0.26100.0\um\wingdi.h`
