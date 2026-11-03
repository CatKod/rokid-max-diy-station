# Issue 1 — the definitive diagnosis

**Symptom.** On a machine with 1 or 2 physical monitors, closing the
spatial display (or quitting the app) leaves the IddCx virtual display
on the Windows desktop in Extended mode, often displaced upwards or to
the left. With 3 physical monitors it works.

---

## 1. The forensic evidence: the position was in the registry all along

`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration`
holds one composite key per connected-display topology, and a subkey
per display path in it. On this machine:

```
KEY: UGD130220200316_0E_07E4_5E+CMN15210_01_07E4_80+LBT47530_0D_07E7_BC^B42221A38B7D1FA9BDB7ECF1148DC354
  00: Position.cx = 1920                Position.cy = 0
  01: Position.cx = 0                   Position.cy = 0
  02: Position.cx = 1915                Position.cy = 4294966216   <== the virtual display
  03: Position.cx = 3840                Position.cy = 0
```

`4294966216` read as a signed 32-bit integer is `-1080`.

**So the virtual display was persisted at (1915, −1080)** — above the
physical monitors and shifted sideways. That is *exactly* the
"repositioned upward or to the left" the user reported. It was not
random Windows behaviour; it was a stored value being restored.

The same key appears twice, under two different composite keys
(`..._LBT47530_0D_07E7_BC^...` and `..._LBT47530_0D_07E7_86^...`),
i.e. two different monitor topologies, both holding the stale
position.

## 2. Why the stale position came back

Windows rebuilds the desktop layout **from that registry database**
whenever the set of connected targets changes — a monitor is plugged
or unplugged, a resolution changes, a display is added or removed.
That rebuild is `dxgkrnl.sys` → `win32kbase.sys`, driven by the
IddCx monitor-arrival/departure notifications the driver sends.

The app's detach used `SetDisplayConfig` **without**
`SDC_SAVE_TO_DATABASE`, so it changed only the *session*. The
database kept saying "the virtual display is active, at (1915, −1080)".

Result:

* the session dropped the virtual path → it looked detached;
* the database still had it → the next topology rebuild restored it,
  at the stale position.

That is the whole bug. The number of physical monitors only decided
*when* the rebuild happened, which is why the symptom looked
topology-dependent.

## 3. The measured A/B/C comparison

`tools/ProbeVirtualStability` — attach, force a topology rebuild,
detach with each strategy, force another rebuild, and read the
database back.

| Strategy | Mechanism | `SDC_SAVE_TO_DATABASE` | Virtual path in the database after the detach |
|---|---|---|---|
| **A** | omit the path | no | **1** — still recorded as active |
| **B** | omit the path | yes | **0** |
| **C** | **disconnect** the path (`flags = 0`, mode indices `INVALID`) | yes | **0** |

Strategy A is the old code, and it is the only one that leaves the
database claiming the virtual display is active. A and C differ in
one more way, and it matters:

* **Omitting** the path says *"I am not telling you about this
  display."* Windows is left with a **connected** target the supplied
  configuration does not account for, so it falls back to its own
  database.
* **Disconnecting** the path — keeping it in the array with
  `DISPLAYCONFIG_PATH_ACTIVE` cleared and both mode indices set to
  `DISPLAYCONFIG_PATH_MODE_IDX_INVALID` — says *"this display is
  connected, and the user has turned it off."*

That is precisely what the Windows **Display Settings** app does when
the user turns a display off, and it is the form Windows understands.

## 4. The second bug, visible in the app's own log

`diag.log` shows the attach not sticking:

```
[19:37:58.498] The virtual display went away.
[19:37:59.134] The display layout changed and the spatial display went with it — putting it back.
[19:37:59.181] The spatial display is back.
[19:37:59.991] Capturing the spatial display at 1920x1080.
[19:38:00.089] The virtual display went away.        <-- gone again 900 ms later
```

The reconciler re-attaches, and the display vanishes again — a fight
loop that ran roughly once a second for ten minutes. The cause is
the same database disagreement: the attach was also session-only, so
any rebuild (and the running app generates display changes constantly
— WGC capture, the panel window, the mouse barrier) dropped the
virtual path again.

The attach therefore has to persist too. Attach and detach then
become the same operation on the same persistent state, which is what
makes the lifecycle reversible.

## 5. The A/B proof, with the real app

`tools/ProbeAppTeardown` starts the actual `RokidDiyStation.exe`,
asks it to quit through its own single-instance pipe, and watches the
virtual display every 100 ms while the teardown runs.

**With the old code** (`dbPaths` = the virtual paths in the persistent
database):

```
   0ms  ON   dbPaths=0        <-- the database already disagrees with the session
 107ms  off  dbPaths=0
 ...
```

The database says the virtual display is **not** part of the
configuration, while the session has it on the desktop. That
disagreement is the bug, and it is there from the first frame.

**With the fix**:

```
   0ms  ON   dbPaths=1
 104ms  off  dbPaths=0        <-- the detach persisted
 ...
```

The display goes off 104 ms after the quit, the database agrees with
the session the whole time, and:

```
  the longest run with the display OFF: 5785ms
  [PASS] the display went off during the teardown
  [PASS] it stayed off for at least 2500ms (so a topology rebuild cannot resurrect it)
  [PASS] the database did not record it as active while it was off
  [PASS] the virtual display is off the desktop
  [PASS] the database does not record it as active
=== ALL CHECKS PASSED ===
```

and after the app has exited, a forced topology rebuild leaves it
off.

The registry tells the same story. Before the fix, the stale keys
still held the old position:

```
KEY ...LBT47530_0D_07E7_BC^B42221A38B7D1FA9BDB7ECF1148DC354
  02: (1915,-1080)          <-- the stale position, still there
```

After the fix, the key for the current topology holds the correct
position:

```
KEY ...MTT1337518463207_1C_07E8_C2^E1F35F8C4046617FFBFFEDFF84841703
  00: (0,0)
  01: (-1920,0)             <-- where the virtual display actually is
```

The `(1915, -1080)` entries under the *other* composite keys are the
ones for monitor topologies this machine has not been in since; they
are inert, because the key is selected by the connected topology.

## 6. The fix

`src/RokidDiyStation.App/Spatial/DisplayConfigController.cs`

1. **Every apply carries `SDC_SAVE_TO_DATABASE`.** The session and the
   database stop disagreeing, so a topology rebuild no longer
   resurrects the display.
2. **The detach disconnects rather than omits.** The virtual path
   stays in the array with `PATH_ACTIVE` cleared and its mode indices
   set to `DISPLAYCONFIG_PATH_MODE_IDX_INVALID` — the Settings app's
   own mechanism.
3. **Every virtual path is handled**, not just the first. The IddCx
   driver can leave two active paths for the same virtual target
   after a re-publish; the old code dropped the first and kept the
   second, so the display stayed on the desktop while the code
   reported success.
4. **The detach is idempotent.** "Already off the desktop" is a
   success, not the false failure the old code reported.
5. **The detach is verified over a settle window**, not checked once.
   A single check cannot see a re-attach that happens after the
   removal.
6. **Positions are matched by identity, not by index.** A path's
   `modeInfoIdx` indexes the mode array of the query that *produced*
   it; reading it against the database's mode array was reading the
   wrong mode, which is why the detach sent positions Windows had
   already refused and answered 87.

`src/RokidDiyStation.App/Spatial/SpatialStation.cs`

7. The teardown order: the intent flag is cleared and the topology
   watcher unhooked **before** the detach, so the `WM_DISPLAYCHANGE`
   the detach itself provokes cannot start a re-attach.

## 6. What was ruled out

* **`DisplayConfigSetDeviceState`** does not exist. Checked against
  `wingdi.h` in the Windows 10 SDK. Path activation is only
  controllable through `SetDisplayConfig`.
* **`SDC_ALLOW_PATH_ORDER_CHANGES` (0x2000) with
  `SDC_USE_SUPPLIED_DISPLAY_CONFIG` (0x20)** is rejected with 87. It
  is only legal with `SDC_TOPOLOGY_SUPPLIED` (0x10). The app's log
  shows this on every call: `validate[0x24A0] = 87`, then
  `validate[0x4A0] = 0`.
* **The driver's control pipe** (`\\.\pipe\MTTVirtualDisplayPipe`,
  `SETDISPLAYCOUNT 0`) is a one-way door that leaves the adapter in
  error code 43. It is not used, and `AGENTS.md` forbids it.
* **The legacy `ChangeDisplaySettingsEx` detach** is also a one-way
  door: a zero-width mode persisted under the display device's key
  makes `EnumDisplaySettings` fail from then on, so the display can
  never be reopened.
* **A duplicate-path defect** was real in the code but is *not* the
  cause here: the live topology shows exactly one path for the virtual
  target. It was fixed anyway, because it is a latent way to fail.
