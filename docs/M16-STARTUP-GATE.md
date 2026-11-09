# M16 — The startup gate

## The problem

The Rokid Max is not a peripheral the app can limp along without. The
spatial panel is drawn on the glasses' own display, and the head pose
comes from the glasses' IMU. With no glasses there is no panel, no
tracking and no spatial display — the app would sit in a
"searching for device…" state forever, doing nothing.

So the glasses have to be plugged in **before** the app starts, and
the app has to say so plainly instead of pretending to work.

There was a second, quieter problem on the same path. The virtual
display is attached by the app when it opens the spatial display and
detached when it closes it — but the app can also *die*: a crash, a
Task Manager "End task", a power cut. Then the detach never runs, and
the next start finds a virtual monitor already on the desktop, in
Extended mode, at a position the user cannot explain.

A leftover virtual monitor is a trap. It is a rectangle that swallows
windows, and a window dragged onto it ends up behind the glasses where
it is very hard to get back. The user's own complaint about the virtual
display was exactly this.

## What the gate does

`StartupPreflight` runs in `Program.Main`, before a single window,
tray icon or thread exists:

1. **Probe the hardware.** Is the Rokid Max attached?
   * **Yes** → the app starts normally. The virtual display is
     *not* touched: the app is about to open it, and the point of the
     gate is to leave the machine exactly as the normal startup
     expects to find it.
   * **No** → go to step 2.
2. **Check the virtual display.** Is it still on the desktop?
   * If yes → take it off the desktop with the same reversible CCD
     detach the rest of the app uses.
   * If no → nothing to do.
3. **Tell the user and quit.** A native message box says the Rokid Max
   is not connected and asks for it to be plugged in, and the app
   exits with the code `2`.

The virtual display is taken off the desktop **before** the dialog
appears, because the user has to be able to trust the message:
"the virtual display is gone" has to be *true* when the dialog shows,
not something that happens afterwards and not something that never
happens because the app quit first.

## Identifying the glasses

The vendor ID alone is not enough. `0x04D2` is a **generic** USB
vendor ID — it is used by all sorts of HID devices, and it just
happens to be the ID the Rokid glasses have. Enumerating "every device
with VID `0x04D2`" finds docking stations, keyboards, mice and
whatever else the user has plugged in.

That is harmless for the tracking service, because it *opens* the
device and parses its reports — a non-Rokid device simply never
produces a valid IMU packet. But it is **not** harmless for the
startup gate: a false positive there means the app starts, finds no
glasses, and sits in "searching for device…" forever — exactly the
state the gate exists to prevent.

So `RokidDeviceProbe` identifies the glasses by:

* their real **product name** (`"Rokid Max"`, `"Rokid Air Plus"`, …)
  — the strongest signal, because a third-party HID device will not
  claim to be a Rokid; or
* their **product ID**, against the known list in `Constants`
  (`0x162F` = Rokid Max, `0x162C` = Air Plus, `0x2002` = Max 2, …).

The gate also accepts the glasses' **display** as a second,
independent signal: they enumerate as a DisplayPort secondary monitor
with the Lontium bridge's EDID (`LBT4753`). That catches the case
where the HID stack has not enumerated the device yet but the display
already has.

### The probe never opens the device

Opening the device CAN legitimately fail while the Rokid Max is
perfectly plugged in — the official Rokid Station runtime holds the
IMU interface exclusively, and the app's own tracking service reports
that case separately. If the gate opened the device, a machine with
Rokid Station running would be told "plug in your glasses" while the
glasses were in the user's face.

So the gate answers *"are the glasses there?"* (an enumeration), and
the tracking service answers *"can I talk to them?"* (an open).

## Why the dialog is native

The gate runs **before** the WPF `Application` exists — there is no
dispatcher, no visual tree and no message pump. WPF's `MessageBox`
would have to create a window of its own, and a WPF window created
before `Application.Run()` is exactly the kind of thing that never
paints: the tray app's own code defers the initial window show to
`Application.Startup` for that reason.

`NativeMessageBox` uses the Win32 box instead. It always appears, and
it takes the foreground window as its owner so it cannot end up
*behind* the fullscreen topmost panel on the glasses' display — the
app would look like it had hung.

## The command-line flags

Two flags exist so the refusal path can be exercised on a machine
where the glasses **are** plugged in, which is the normal development
machine:

| Flag | Effect |
|---|---|
| `--preflight` | Run only the gate, print the outcome on the console, and exit. No dialog, no tray, no window. |
| `--no-device` | Skip the hardware probe and behave as if the glasses were absent. |

`--preflight` returns `0` when the app may start and `2` when it
refuses, so a script can tell the two apart.

```
$ RokidDiyStation.exe --preflight
can_start=True
title=Rokid DiyStation
message:
The Rokid Max is connected.
detail:
USB HID: Rokid Max (PID=0x162F)

$ RokidDiyStation.exe --preflight --no-device
can_start=False
title=Rokid DiyStation - the Rokid Max is not connected
message:
The Rokid Max is not connected.

Plug the Rokid Max into the USB-C port of this PC, then start Rokid DiyStation again.

The virtual display has been taken off the desktop.
...
```

## The exit code

`StartupPreflight.ExitCodeNoDevice` is `2`. It is not zero, so a
launcher script can tell "the app ran" from "the app refused to start
because the glasses were missing".

## Where the gate sits

```
Program.Main
  ├─ --discover / --reset          helpers, no gate
  ├─ --preflight                   the gate alone, then exit
  ├─ SingleInstance                mutex; a second copy asks the
  │                                running one to show itself and exits
  ├─ StartupPreflight.Evaluate     ← THE GATE
  │    ├─ RokidDeviceProbe.Check   are the glasses there?
  │    ├─ (absent) → VirtualDisplayController.Close
  │    │              take the virtual display off the desktop
  │    ├─ NativeMessageBox          tell the user
  │    └─ exit(2)
  └─ the normal startup
       ├─ SettingsService
       ├─ TrackingService          opens the HID device for real
       ├─ TrayApplicationContext
       └─ SpatialStation           opens the virtual display
```

The gate runs **after** the single-instance check on purpose: a second
copy of a running app must never put a dialog in the user's face. It
just asks the running instance to show itself.

## Tests

`StartupPreflightTests` pins the decision, with the probes injected —
a test cannot unplug the glasses, but it can certainly say "pretend
the glasses are absent":

* the app starts when the glasses are attached, and the virtual
  display is **not** touched;
* the app refuses to start when the glasses are absent;
* the virtual display is taken off the desktop **before** the app
  quits;
* an already-clean desktop is not described as cleaned;
* a failed cleanup is reported to the user, because the app would
  otherwise be quietly leaving the trap it was supposed to remove;
* the refusal has its own, non-zero exit code.

`RokidDeviceProbeTests` pins the identification rule: the Rokid names
are recognised, an unrelated device sharing the vendor ID is not, the
known product IDs are the glasses and an unknown one is not.

## What was verified on the hardware

| Check | Result |
|---|---|
| `--preflight` with the glasses plugged in | `can_start=True`, `USB HID: Rokid Max (PID=0x162F)`, exit `0` |
| `--preflight --no-device` with the virtual display up | took the virtual display off the desktop, exit `2` |
| `--preflight --no-device` again | "already off the desktop", exit `2` |
| the app started with `--no-device` | the native dialog appeared with the right title and text, and the app quit when the dialog was dismissed |
| the app started normally | the glasses were found, the spatial display opened at `\\.\DISPLAY9 1920x1080 at (-1920,0)`, and the IMU streamed at 437 Hz |
| the app quit through the pipe | the virtual display was detached and the process exited |
