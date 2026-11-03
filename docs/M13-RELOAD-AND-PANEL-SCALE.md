# M13 — Reload, and a panel that can be resized

**Status:** implemented
**Date:** 2026-11-03

---

## 1. Why this document exists

Two things the user asked for, and the reasoning behind each of them.

The first is a "reload" button on the tray menu. The second is a slider
for the panel's size.

They are in one document because they share a theme: both are settings
that were **hard-coded** before, and both turned out to need more care
than the code that used the hard-coded value.

---

## 2. The reload button

### 2.1 Why the user wants it

The user's words, in substance: *several small faults only clear when
the app is cycled, and chasing each one individually is a losing game.*

That is a legitimate engineering position, and it is worth taking
seriously rather than dismissing. A capture session that has stopped
delivering frames, a display path left in a state that a fresh attach
resolves — these are the kinds of fault that are easy to fix once and
tedious to fix twice.

The alternative to a reload button is not "fix every bug". It is "the
user learns to quit and start the app by hand, every time, for a
class of faults nobody has catalogued". That is a worse product.

### 2.2 Why it is harder than it looks

The obvious implementation is "start the exe again and exit". That is
wrong, and wrong in a way that leaves the user with **no app at all**,
which is worse than the fault the reload was meant to clear.

There are two reasons.

**The virtual display.** The outgoing instance owns the IddCx virtual
monitor. If it exits without releasing it, the replacement instance
finds the display already attached and its own attach fails. The
outgoing instance must therefore close the spatial display *before* the
replacement starts.

**The single-instance mutex.** This is the subtle one. The app enforces
single-instance behaviour with a named mutex. A second copy that finds
the mutex held does the sensible thing: it signals the first to show
its window, and exits.

But the replacement is started *before* the outgoing instance has
finished exiting — that is the whole point of a reload, it must be
seamless. So at the moment the replacement looks at the mutex, the
outgoing instance **still holds it**. The replacement would conclude
"another copy is running", signal the outgoing instance to show its
window, and exit. The user would be left with the old instance, which
is about to quit, and then with nothing.

### 2.3 The fix: an explicit takeover

A replacement instance is started with a `--reload` flag. An instance
started with that flag does not signal and exit: it **waits** for the
mutex to become free, takes it over, and carries on.

```csharp
public SingleInstance(bool takeOver = false)
{
    _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
    if (createdNew) { IsOwner = true; StartPipeServer(); return; }

    if (takeOver && TryTakeOver())
    {
        IsOwner = true;
        TookOver = true;
        StartPipeServer();
        return;
    }
    // otherwise: signal the running instance and exit
}
```

The wait has a deadline (15 s). If the outgoing instance is wedged and
never releases the mutex, the replacement must not hang forever: it
falls back to the normal signal-and-exit behaviour, which at least
brings the old instance's window to the front.

`AbandonedMutexException` is treated as success, because that is
exactly what it means: the outgoing instance died without releasing
the mutex, and we own it now.

### 2.4 The release has to be explicit

The mutex must be released **deterministically**, not left to the
finalizer.

```csharp
app.Exit += (_, _) =>
{
    tray.Quit();
    single.ReleaseOwnership();
};
```

If the release happened at an unpredictable moment — or never, because
the finalizer does not run before the process is killed — the
replacement would sit there until its deadline expired, and then give
up.

### 2.5 The named pipe has the same problem

The "show the window" pipe is a `NamedPipeServerStream` with
`maxNumberOfServerInstances: 1`. Creating a second server on the same
name fails while the first one is alive — which is precisely the
situation during a reload, because the replacement takes over the
mutex a moment before the old process has actually exited.

The pipe server therefore **retries** the creation instead of giving
up. Without that, a later double-click on the exe would find no
listener and the window would never come to the front — a small
failure, but one that looks like "the app is broken".

### 2.6 The order of operations

```
1.  save the settings           nothing the user changed is lost
2.  close the spatial display    the replacement can attach it
3.  stop the tracker            the HID handle is released
4.  start the replacement        with --minimised --reload
5.  quit                        releases the mutex
```

Step 4 happens on the dispatcher *after* step 5, because the
replacement must not find the mutex still held.

If the relaunch fails for any reason, the app says so in the log and
**stays alive**. An app that has torn itself down and cannot start
again is worse than the fault the reload was meant to clear.

### 2.7 The BOM that made the button do nothing

The first version of the reload **did not work at all**, and the
reason is worth recording, because it is the kind of bug that unit
tests cannot find.

The pipe's payload is written with:

```csharp
using var writer = new StreamWriter(client, Encoding.UTF8);
```

`Encoding.UTF8` **carries a byte-order mark**. So the client writes
three bytes — `EF BB BF` — before the payload, and the server reads:

```
"\uFEFFreload"
```

rather than:

```
"reload"
```

The comparison was exact, so it never matched and the reload never
fired.

**Why it survived so long:** the "show the window" path was unaffected
*by accident*. `int.TryParse` rejects the BOM, the code falls through
to "show hwnd 0", and 0 means "no particular window" — which is the
right answer anyway. So the show path kept working perfectly, and the
reload path, which needed an exact match, silently did nothing. The
button looked wired up. It just never fired.

Two changes fix it, and both are kept:

```csharp
// The clients: no BOM at all.
using var writer = new StreamWriter(client, new UTF8Encoding(false));

// The server: tolerate a BOM from a client written against the old
// behaviour.
var command = payload.Trim().TrimStart('\uFEFF').Trim();
```

### 2.8 Verifying it end to end

Because the hard part is the interaction between two processes, the
reload is verified by a **live** test
(`ReloadEndToEndTests.The_App_Reloads_Itself_In_Place`): it starts
the real app, asks it to reload through the same pipe the tray menu
uses, and checks that the original process exited, that a replacement
took over, and that the replacement's pipe still answers.

The tray menu's action and the pipe's command go through exactly the
same code, so the test covers the button.

A diagnostic tool (`tools/ProbeReload`) does the same thing with
printed output, for when the app is misbehaving and the test output
is not enough.

---

## 3. The panel's scale

### 3.1 What was hard-coded

The panel's angular size was 50 % of the glasses' horizontal field of
view. That number appeared in the anchor service's default options and
nowhere else — there was no way to change it, and no way even to
express the preference.

### 3.2 The range: 25 % to 100 %

**25 % is the practical floor.** Below it the panel is small enough
that reading text on it becomes uncomfortable, and it stops being
useful as a reading surface.

**100 % is a hard ceiling, not a preference.** The panel may cover the
*entire* horizontal field of view, which puts its edges exactly on the
bezel. It cannot go above 100 %, because the panel would then extend
past what the glasses can show: the content would simply be clipped
off, and the user would be looking at a cropped picture while
believing they could see all of it.

The clamp lives in the geometry itself:

```csharp
public static float PanelWidth(float distance, float fovFraction, ...)
{
    var thetaH = ToRadians(HorizontalFovDegrees(diagonalFovDegrees));
    return 2f * distance * MathF.Tan(thetaH * PanelScale.Clamp(fovFraction) * 0.5f);
}
```

### 3.3 The step: 5 %

A 1 % step would give a slider that is fiddly to aim at, and that moves
the panel in barely-perceptible steps. A 10 % step would be too
coarse: the user would have to settle for a size that is visibly wrong
rather than one that is nearly right.

5 % is small enough that "just a bit bigger" is reachable, and large
enough that every step is a visible change.

### 3.4 The scale is an ANGULAR size

The fraction is the panel's **angular** width: the panel subtends this
fraction of the glasses' field of view. It is not a size in metres,
and it does not depend on the panel's distance.

This is not a detail — it is the property that makes the setting
mean something. From `RokidDisplayProfile.PanelWidth`:

```
W = 2 D tan(f * theta_h / 2)
```

The width grows with the distance in exactly the ratio that keeps the
subtended angle constant. So:

* changing the **scale** changes the panel's size and nothing else —
  its distance, its centre and its world pose are untouched;
* changing the **distance** does not change the panel's apparent size
  at all, only how far away it is.

That is why the anchor service re-sizes the panel **in place** when
the scale changes, rather than re-pinning it: the panel stays where it
is in the room and only changes size. Re-pinning would move it to
wherever the user happens to be looking — the same "it recentres
itself" complaint this project has been fighting since M8.

### 3.5 Two persisted forms, one truth

The settings file carries the scale in two forms:

| Field | Unit | Why |
|---|---|---|
| `PanelFovFraction` | fraction (0.5) | what the maths uses; the original form |
| `PanelScalePercent` | percent (50) | what the slider shows; the user's choice |

Only one can be authoritative, and the precedence has to be
well-defined or the two would silently fight each other. The rule
lives in one place, `PanelScale.Resolve`:

* the **percentage wins** whenever it differs from its default — that
  is the value the user chose on the slider, and it is already snapped
  to a 5 % step;
* otherwise the **fraction is honoured**, which is what an older
  `settings.json` written before the percentage existed carries;
* otherwise the default (50 %) wins.

Both forms are written on save, so they never drift apart.

### 3.6 The comfort cone has to follow

The comfort cone's maximum width depends on how much of the field of
view the panel covers: a bigger panel leaves less room for the cone to
move in before the panel's edge would be clipped by the bezel.

So the cone slider's maximum is re-derived when the panel scale
changes, and the current value is re-clamped. A cone that is too wide
would break its own promise — the panel would be clipped while the
gaze was still inside the "rigid" cone, which is the whole contract of
the comfort-cone mode.

---

## 4. The cursor's size is measured against the PANEL

This is the part the user explicitly asked about: *"các phần như công
thức vẽ chuột nếu như đang hardcode theo đúng 50% thì bạn cũng sửa
lại giúp tôi"*.

The arrow used to have a **fixed size in metres** — 20 mm by 26 mm,
hard-coded in the renderer, later wrapped in a user-facing scale
factor.

### 4.1 The hidden assumption

A fixed size in metres bakes in an assumption that the panel never
changes size. But the panel's size is now a user setting, and a
fixed-size arrow would **silently change its size relative to the
content** when that setting moved:

* at 100 % panel scale, the arrow would be half the relative size it
  was at 50 %;
* at 25 %, it would be twice as big.

The user would have to re-tune the cursor size every time they changed
the panel scale, and the two settings would silently undo each other.

### 4.2 The fix: a fraction of the panel's width

The arrow's size is now expressed as a **fraction of the panel's own
width**, and the renderer measures it against the pose it is already
holding:

```csharp
var (cursorWidth, cursorHeight) =
    Core.Spatial.CursorSize.SizeMetres(_cursorScale, pose.Width);
```

The reference numbers (`DefaultWidthMetres` and friends) still describe
the arrow at the **reference panel** — 2 m away at 50 % of the field of
view, which is what they were originally measured against — so the
default appearance is bit-for-bit what it has always been:

```csharp
public static readonly float ReferencePanelWidthMetres =
    RokidDisplayProfile.PanelWidth(ReferencePanelDistanceMetres,
                                    ReferencePanelFovFraction);
```

### 4.3 Why the WIDTH and not the height

The panel's width does not depend on the content's aspect ratio; its
height does. So measuring against the width means:

* changing the content's shape does **not** change the arrow;
* changing the panel's scale **does**, proportionally, keeping the
  arrow the same size relative to the content.

This is the same reasoning that made the cursor a scale factor rather
than a size in metres in the first place (M11): the arrow must not
change size when something unrelated changes.

### 4.4 Why the distance does not affect it

The panel's angular size does not depend on the distance (section 3.4),
so scaling the arrow with the panel keeps its **angular** size
constant. The user sees no change when they move the panel further
away, which is correct: a screen moved further away looks the same
size.

---

## 5. Verification

* 247 → **294 tests**, all passing (117 app + 179 core).
* The new tests cover: the scale's range, step, clamping and snapping;
  the round trip between the percent and fraction forms; the panel's
  width following the fraction; the ceiling at 100 %; the angle being
  distance-independent; the arrow's size relative to the panel; and
  the reload's takeover plumbing.
* `PanelScaleTests` asserts that **every** supported percentage is
  reachable by snapping, and that a resolved fraction is always on a
  5 % step — the two ways the setting can get out of step with the
  slider.
* `ReloadEndToEndTests` runs the **real app** and checks the reload
  end to end. This is the test that would have caught the BOM bug in
  section 2.7, and it is the reason that bug is fixed rather than
  worked around.

### A note on a flaky test

`YawStutterRegressionTests.The_Panel_Does_Not_Alternate_Between_Frozen_And_Leaping`
failed once during this work. It is **pre-existing** and unrelated: it
uses `Thread.Sleep` to simulate 60 FPS frames and asserts that no
frame has *exactly* zero motion. Under CPU load, a frame's elapsed
time can quantise to zero and the assertion trips. It passes 3/3 in
isolation and 3/3 in the full suite, and the same flake was observed
on the unmodified baseline under parallel load.
