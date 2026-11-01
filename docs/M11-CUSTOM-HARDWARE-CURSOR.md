# M11 — The custom hardware cursor: mapping the physical mouse onto the spatial panel

**Date:** November 2026
**Research input:** `.research/i6/findings.md`
**Supersedes:** the pointer-mapping section of `M9-SPATIAL-POINTER-BRIDGE.md`
**Companion:** `M10-INPUT-LAYER-STACK.md` (the click-delivery layers)

---

## 0. Verdict

**The idea is feasible and it is the right one.** It is also, importantly,
*better* than gaze pointing: a physical mouse has no tremor, no saccades and
no dwell problem, and it reuses muscle memory.

But there is **one hard architectural consequence** that decides the whole
design, and it is worth stating up front:

> **Because our panel is an opaque `WS_EX_TOPMOST` window covering the whole
> Rokid display, Win32 cannot deliver a click to the window behind it.**
> `SendInput` and `PostMessage` both die on our panel. So the click must go
> through an **application-level channel** — CDP for a browser, and the
> app-native channels from M10 for everything else.

That is the price of the spatial panel, and it is the same price the previous
two directions paid. It is not a flaw in the cursor idea.

---

## 1. Correcting two things in the current code

### 1.1 `WS_EX_TRANSPARENT` does not do what the comment says

The comment above `WS_EX_TRANSPARENT` in `PanelRenderer.cs` claims it lets
clicks "pass straight through to whatever is behind it". **It does not.**

`WS_EX_TRANSPARENT` is a **painting-order** flag: it makes a window paint
after its *siblings* (same thread, same parent). It has nothing to do with
hit-testing. The rule that makes a window click-through is
**`WS_EX_LAYERED` + `WS_EX_TRANSPARENT` together** — a layered window
hit-tests by alpha, and `WS_EX_TRANSPARENT` makes it transparent to the
hit-test walk entirely.

Our panel is **not** layered (a D3D11 flip-model swapchain cannot be), so:

> **The panel window already receives `WM_MOUSEMOVE`, `WM_LBUTTONDOWN` and
> `WM_SETCURSOR` today.**

Removing `WS_EX_TRANSPARENT` would change *nothing* about input — only GDI
paint sequencing among siblings, and there are none. **So the user's step 1
("remove `WS_EX_TRANSPARENT`") is unnecessary.** There is no new input
plumbing to build at all; the plumbing is already there and we are throwing
the data away.

Keep `WS_EX_NOACTIVATE` and keep answering `WM_MOUSEACTIVATE` with
`MA_NOACTIVATE`: `WS_EX_NOACTIVATE` windows still receive every mouse message
(verified against the extended-window-styles documentation); the flag only
suppresses activation. And `WM_SETCURSOR` is sent **before**
`WM_MOUSEACTIVATE` and on every mouse move, so `MA_NOACTIVATE` cannot
interfere with hiding the cursor.

### 1.2 WGC is baking a cursor into our panel right now

`GraphicsCaptureSession.IsCursorCaptureEnabled` **defaults to `true`**. With
the source window on `\\.\DISPLAY5` — the same display as our panel — DWM
composites the system cursor into the captured frame whenever the cursor
falls inside the source window's rectangle.

Consequence: a ghost cursor, scaled to 50 %, perspective-distorted and lagging,
baked permanently into the panel texture. That is exactly the artifact we are
trying to avoid.

**Fix: `captureSession.IsCursorCaptureEnabled = false`.** One line, in
`WindowCapture.Start`, next to `TrySetIsBorderRequired`.

---

## 2. Hiding the OS cursor over the Rokid display only

### The mechanism

Our panel window covers the entire 1920×1080 output. When the pointer is over
it, Windows sends `WM_SETCURSOR` to our window procedure. We answer:

```csharp
case WM_SETCURSOR:
    SetCursor(IntPtr.Zero);
    return new IntPtr(1);   // TRUE: we set the cursor, stop processing
```

* `SetCursor(NULL)` — *"If the hCursor parameter is NULL, the cursor is
  removed from the screen."* The cursor is **hidden but still tracked**:
  `GetCursorPos` keeps working, raw mouse events keep flowing. That is
  exactly what we want — the physical mouse is still a real input device,
  we just stop drawing the OS arrow.
* **Scope is one monitor.** The cursor image is drawn by the GPU's hardware
  cursor plane on whichever output the pointer is on. `SetCursor(NULL)` hides
  it *now*, on this output. When the pointer crosses onto the XP-Pen, that
  display's window gets its own `WM_SETCURSOR`, calls `SetCursor(arrow)`, and
  the arrow is back — **instantly, with no flicker**.
* **Crash-safe.** If our process dies while the cursor is nulled, Windows
  destroys our window, the pointer lands on whatever is now topmost, and that
  window's `WM_SETCURSOR` restores the arrow. The cursor is never
  permanently lost.

### What to avoid

| API | Why not |
|---|---|
| `SetSystemCursor` | Replaces the cursor **globally**, for every monitor and every app. If we crash before restoring, the whole session loses its arrow until reboot. |
| `SPI_SETCURSORS` | Reloads the cursor scheme from the registry. Global, slow, overkill. |
| `SetCursorInfo` | **Does not exist.** Only `GetCursorInfo` does. |
| `ClipCursor` | Confines the cursor to a rectangle — a *hard barrier* the user cannot push past. Worse, it **breaks the XP-Pen**: the Artist 13.3 is an absolute digitiser mapped to `DISPLAY2`, and while the cursor is clipped to `DISPLAY5` the kernel clamps the pen's absolute coordinates to the clip edge, so the user cannot draw at all. |

---

## 3. The mathematics

### 3.1 Conventions

* **Row-vector** (`System.Numerics`): `v' = v · M`; `M1 · M2` applies `M1`
  first. Full chain: `v_clip = v_world · V · P`.
* **NDC**: `x ∈ [-1, 1]` left→right, `y ∈ [-1, 1]` bottom→top, `z ∈ [0, 1]`.
* **Display pixels**: `(mx, my)`, origin top-left, 0..1920 / 0..1080.
* Panel basis: `R = normalize(cross(Normal, Up))`, `U` = gravity-locked up,
  `N = -WorldDirection` (points back at the viewer).

### 3.2 Pixel → NDC

```
x_ndc = 2 * mx / 1920 - 1
y_ndc = 1 - 2 * my / 1080
```

Check: top-left `(0,0)` → `(-1, +1)`; centre → `(0,0)`; bottom-right → `(+1,-1)`.
Matches `CreatePerspectiveFieldOfView`.

### 3.3 Unproject

```
M = V · P                       // world → clip
Matrix4x4.Invert(M, out invM)   // guard: returns false if singular
```

With the row-vector convention, the inverse chain is `M⁻¹ = P⁻¹ · V⁻¹`, and
transforming a clip-space point back to world is `p_world = p_clip · M⁻¹`:

```
p_near = Vector4.Transform(new Vector4(x_ndc, y_ndc, 0, 1), invM)
p_far  = Vector4.Transform(new Vector4(x_ndc, y_ndc, 1, 1), invM)
// perspective divide by W
O = eye position
D = normalize(p_far.xyz/W - p_near.xyz/W)
```

### 3.4 Ray–plane intersection

```
t = dot(C - O, N) / dot(D, N)
```

Reject when `dot(D, N) >= -1e-5` (ray parallel to, or behind, the panel) or
`t <= 0.01` (panel behind the eye). Hit point `P = O + t·D`.

### 3.5 Hit → UV (signs derived from the vertex layout)

Our vertex buffer maps corners to UVs as: corner 0 `(C − hx − hy)` → uv
`(1,1)`; corner 1 `(C + hx − hy)` → `(0,1)`; corner 2 `(C + hx + hy)` →
`(0,0)`; corner 3 `(C − hx + hy)` → `(1,0)`.

So **u increases toward `−R`** and **v increases toward `−U`**. Let
`Δ = P − C`, then `x = Δ·R`, `y = Δ·U`, with `x ∈ [−W/2, W/2]` and
`y ∈ [−H/2, H/2]`. Interpolating the corner data gives slope `−1/W` and
`−1/H`, passing through `0.5` at the centre:

```
u = 0.5 - (Δ · R) / W
v = 0.5 - (Δ · U) / H
```

Verification against all four corners (with `hx = R·(W/2)`, `hy = U·(H/2)`):

| Corner | `Δ·R` | `Δ·U` | u | v | expected | ✓ |
|---|---|---|---|---|---|---|
| 0 `C−hx−hy` | `−W/2` | `−H/2` | 1.0 | 1.0 | (1,1) | ✓ |
| 1 `C+hx−hy` | `+W/2` | `−H/2` | 0.0 | 1.0 | (0,1) | ✓ |
| 2 `C+hx+hy` | `+W/2` | `+H/2` | 0.0 | 0.0 | (0,0) | ✓ |
| 3 `C−hx+hy` | `−W/2` | `+H/2` | 1.0 | 0.0 | (1,0) | ✓ |

The cursor is on the panel iff `0 ≤ u ≤ 1` and `0 ≤ v ≤ 1`.

### 3.6 What the 50 % scale actually does

**It does not appear in the ray-cast.** The ray-cast is exact for any panel
size; there is no 0.5 factor anywhere in the formulas above.

What the 50 % *does* affect:

1. **Angular extent.** The panel subtends 22.12° of the glasses' 44.24° FOV.
2. **Hit footprint.** Only the central ~960 × 540 display pixels land on the
   panel. The outer ~75 % of the display produces rays that miss entirely
   (`u,v ∉ [0,1]`).
3. **Control–display gain** — see below.

### 3.7 Absolute vs. differential mapping — the decisive design choice

The mouse lives in the **display** frame (head-locked). The panel lives in the
**world** frame (world-locked). Any mapping between them makes one slide
relative to the other.

| Policy | Stores | Head turns, mouse still | Mouse moves, head still | Zero-slide? | Complexity |
|---|---|---|---|---|---|
| **1. Absolute** | display pixels | **FAIL** — the ray from a fixed pixel sweeps across the panel, so the cursor slides over the content | cursor moves | **No** | Low |
| **2. Differential** | panel UV | **PASS** — mouse delta is 0, so UV is unchanged | cursor moves | **Yes** | Medium |
| **3. Re-seeded on entry, differential after** | panel UV | **PASS** | cursor moves | **Yes** | Medium |
| **4. Head-locked absolute** | display pixels | cursor doesn't slide — but **the panel now moves with the head**, destroying the 3DoF experience | cursor moves | No (kills the product) | Very low |

**Policy 3 is the answer.** The user's requirement — *"the cursor must stay
glued to the content and must not slide across the panel when the head turns"*
— is satisfied only by 2 and 3, and 3 is strictly better because the cursor
*starts* under the mouse.

#### The re-seed

When the pointer crosses into `\\.\DISPLAY5`:

1. Ray-cast from the entry pixel.
2. If it hits the panel (`u,v ∈ [0,1]`) → set `u_cursor = u`, `v_cursor = v`.
3. If it misses (the user entered through the black border) → **clamp** the
   unclamped `u,v` into `[0,1]`, so the cursor starts at the nearest edge
   rather than at an arbitrary point.
4. From then on, integrate differentially forever.

The edge case worth naming: the panel is a tilted rectangle, so its screen
footprint is a thin quadrilateral when viewed at an angle. A user entering
from the side will usually miss. The clamp handles it.

### 3.8 The gain — and a correction to the research

**This is where I disagree with the research report, and it matters.**

The research proposed `Δu = −Δmx / W_source`. Working it through from the
signs I derived above, that sign is wrong.

Take a concrete case: camera looking down `−Z`, so `WorldDirection = (0,0,−1)`,
`Normal = (0,0,+1)`, `Up = (0,+1,0)`, and therefore
`R = normalize(cross(Normal, Up)) = cross((0,0,1),(0,1,0)) = (−1,0,0)`.

* The mouse moves **right** in display space: `Δmx > 0`. The ray tilts toward
  `+X` in the world. The hit point moves to `+X`, so `Δ = +X·a` and
  `Δ·R = +X·(−X)·a = −a`.
* Then `u = 0.5 − (−a)/W = 0.5 + a/W` → **u increases**. The mouse moving
  right must move the cursor right, so `Δu` must be **positive**.
* The mouse moves **down**: `Δmy > 0`. The hit point moves down (`−Y`), so
  `Δ·U = −b`, and `v = 0.5 + b/H` → **v increases**. On the texture, `v = 0`
  is the top and `v = 1` the bottom, so moving down increasing `v` is correct.

Therefore the correct mapping is:

```
Δu = +Δmx / W_source
Δv = +Δmy / H_source
```

with `W_source, H_source` = the **captured window's** pixel size.

**Why the source window's size and not the panel's:** the captured window is
1920×1080 and the panel shows all of it. Making one source pixel correspond
to one mouse count gives the familiar 1:1 feel of a normal monitor. If we
instead scaled by the panel's screen-space width (960 display pixels), the
cursor would feel roughly twice as fast, because the same mouse travel would
cross the content twice as often.

**This sign must still be confirmed empirically** — see the test list in §6.
It is a two-minute check and it is exactly the kind of thing that is
embarrassing to get wrong.

**Should the gain follow the panel's local screen-space scale?** No. The
Jacobian of the display→texture mapping varies across the panel (foreshortening
at the edges), and the research suggests using it. But that would make the
pointer speed *change* as you move it, which feels broken. A **constant gain in
content coordinates** is what every real product does: the content does not
change size as you drag across it, so the pointer should not change speed
either. The panel's perspective is a *rendering* concern; the pointer is a
*content* concern.

### 3.9 Getting the mouse delta

Two options:

* **`WM_MOUSEMOVE` `lParam`** — the client coordinates of our panel window.
  Since the panel covers the whole Rokid display and sits at virtual-desktop
  offset `(3840, 0)`, the client coordinates *are* the display-local pixels.
  Delta = current − previous. Simple, no extra plumbing.
  *But:* these are **accelerated** coordinates (Windows applies the mouse
  acceleration curve and the pointer-speed setting).
* **`WM_INPUT` raw input** — gives the HID-level delta, **unaccelerated**.
  For a 1:1 "this feels like a real screen" feel, raw is the better source.

Recommendation: **raw input**, with `WM_MOUSEMOVE` as the fallback if raw
registration fails. Note that raw input gives relative deltas with no
absolute position, so we still need `GetCursorPos` for the re-seed.

### 3.10 Clamping

Under differential mapping the cursor is clamped to `[0,1] × [0,1]`. If the
user keeps pushing the mouse past the edge, the cursor stops at the border
with **no accumulation** — so reversing direction moves it immediately
(no "wind-up" rubber-banding).

---

## 4. Rendering the virtual cursor

### 4.1 A second draw call, not a second texture

Do **not** draw the cursor into the captured texture: that would need a GPU
copy per frame, and sampling a small cursor at 50 % scale blurs its edges.

Instead, a **second draw call** in the same pass:

```
P(u,v) = C + (0.5 − u)·W·R + (0.5 − v)·H·U + δ·N
```

with `δ = 0.0015 m` (1.5 mm) along the normal, toward the eye. The sign
convention is the same one derived in §3.5, so `u = 0` lands on the corner
whose texture UV is `u = 0`.

The quad's world size: a standard Windows arrow subtends roughly 1° of
visual angle at reading distance. At 1.5 m, 1° is
`2·1.5·tan(0.5°) ≈ 26 mm`. So a **20 × 26 mm** quad reproduces the familiar
perceived size.

### 4.2 The pixel shader problem — and the fix

Our existing fragment shader clamps any pixel with luma ≤ 16/255 to pure
black, because the Lontium DP bridge in 120 Hz mode interprets video range as
16–235, and a nominally-black pixel would land at 16 and light the panel up as
a faint grey box.

**A cursor drawn with that shader loses its dark outline** — it becomes an
illegible blob.

Fix: a **separate cursor pixel shader** with no luma clamp, outputting
full-range RGBA. Keep the panel shader untouched.

### 4.3 Depth and blending

* **Depth**: `DepthEnable = TRUE`, `DepthWriteMask = ZERO`,
  `DepthFunc = LESS_EQUAL`. The 1.5 mm normal offset removes any z-fighting
  at 1.5 m; no depth bias is needed.
* **Blend**: standard alpha —
  `SrcBlend = SRC_ALPHA`, `DestBlend = INV_SRC_ALPHA`, `BlendOp = ADD`.

### 4.4 Legibility over arbitrary content — the Micro-OLED twist

The Rokid's Micro-OLED panels are **additive**: they emit light, they cannot
project darkness. "Black" is clear transparent glass showing the room.

So on a white web page, a white cursor is invisible; over the dark
see-through room, a black cursor is invisible. We need **both** at once:

> A **white core with a solid black outline** (2 px in source pixels).

Over bright content the black outline reads as a dark shape; over the
transparent room the white core glows. This is the standard solution and it
works with an additive display precisely because the outline is *emitted
darkness* — which on Micro-OLED means "transparency", so it still separates
the white core from bright content.

---

## 5. Delivering the click — and why the fallback is dead

### 5.1 The architectural fact

`SendInput` inserts events into the kernel's hardware input stream.
`win32k` routes **mouse** events by **spatial hit-testing at the cursor
coordinates**, walking the Z-order from the top. It does *not* route them by
foreground focus (that is how *keyboard* events work).

Our panel is `WS_EX_TOPMOST` and opaque, and it covers the entire Rokid
display. **All topmost windows sit permanently above all non-topmost windows.**
So:

> A warped click lands on **our panel**. It can never reach the browser
> behind it.

`SetForegroundWindow` does not help — foreground does not change the Z-order.
Making the browser `HWND_TOPMOST` to get above our panel would mean it
**paints over the AR panel**, destroying the effect.

And `PostMessage` to `Chrome_RenderWidgetHostHWND` fails for the reasons
established in M10: Chromium checks the real `GetKeyState` and
`::GetCursorPos()` and discards the synthetic event.

**So for a browser, CDP is not the best option — it is the only option.**
For non-browser apps, the answer is the app-native channels from M10
(GSMTC, VS Code IPC, JetBrains HTTP, `AttachConsole`), with the warp
fallback available only for apps we have no channel for.

### 5.2 The CDP mapping

Chrome in `--app` mode has **no tab strip, no address bar, no bookmark bar**,
so the client area *is* the viewport. Given the client size and
`devicePixelRatio`:

```
CSS_x = u * W_client / DPR
CSS_y = v * H_client / DPR
```

At 100 % scaling on a 1920×1080 window, `DPR = 1` and `CSS_x = u · 1920`.

### 5.3 Does CDP need the browser to be foreground?

**No.** `InputHandler::DispatchMouseEvent` builds a `blink::WebMouseEvent`
and calls `RenderWidgetHostImpl::ForwardMouseEvent` directly, which goes over
Mojo IPC to the renderer. **It never touches the `user32` message queue** and
never consults `GetForegroundWindow`. Blink hit-tests the DOM and fires
`:hover`, click handlers and everything else normally.

This is the key enabler: our panel is topmost, the browser is behind it and
never foreground, and CDP still works.

### 5.4 The command sequences

```
hover:   mouseMoved
click:   mouseMoved → mousePressed(left, clickCount=1) → mouseReleased(left, clickCount=1)
dblclk:  mousePressed(1) → mouseReleased(1) → mousePressed(2) → mouseReleased(2)
right:   mouseMoved → mousePressed(right) → mouseReleased(right)
wheel:   mouseWheel(deltaX, deltaY)
drag:    mouseMoved → mousePressed(buttons=1) → mouseMoved(buttons=1)* → mouseReleased(buttons=0)
```

`modifiers` is a bitmask: Alt=1, Ctrl=2, Meta/Win=4, Shift=8.

---

## 6. Staged implementation plan

### Stage 1 — Cursor suppression + WGC hygiene
**Change**
1. `WM_SETCURSOR` → `SetCursor(IntPtr.Zero)`, return `TRUE`.
2. `captureSession.IsCursorCaptureEnabled = false` in `WindowCapture.Start`.
3. Handle `WM_MOUSEMOVE`; keep the last client position.

**Risk** — low.
**Test** — move the mouse onto the Rokid display: the arrow must vanish
instantly and return instantly on the XP-Pen. Through the glasses: no ghost
cursor anywhere on the panel.

### Stage 2 — The cursor quad
**Change**
1. A second draw call: a 20 × 26 mm quad at `P(u,v)`.
2. A dedicated cursor pixel shader (no luma clamp) + alpha blending.
3. A white-core/black-outline cursor texture.

**Risk** — z-fighting, or the cursor vanishing against bright content.
**Test** — a sharp pointer floats at the centre of the panel; it stays legible
over both white and dark content; it stays coplanar with the panel as you
turn your head.

### Stage 3 — Differential tracking + re-seed
**Change**
1. Register `WM_INPUT` for raw mouse deltas (fallback: `WM_MOUSEMOVE` deltas).
2. Integrate `Δu += Δmx / W_source`, `Δv += Δmy / H_source`, clamped to `[0,1]`.
3. Re-seed on entry into `\\.\DISPLAY5` via the ray-cast; clamp if the entry
   point misses the panel.
4. Move the quad each frame from the integrated UV.

**Risk** — the gain feels wrong; **the sign of Δu/Δv needs confirming**.
**Test** — move the mouse right: the cursor must move **right**. Move it
down: the cursor must move **down**. Then stop the mouse and turn your head:
the cursor must stay **rock-solid glued** to the same word on the page,
travelling with the panel.

### Stage 4 — CDP client and clicks
**Change**
1. Launch Chrome with `--app=<url> --remote-debugging-port=9222
   --user-data-dir=<dedicated> --disable-backgrounding-occluded-windows
   --disable-features=CalculateNativeWinOcclusion`.
2. A WebSocket client to `http://127.0.0.1:9222/json/list`.
3. `mouseMoved` on UV change; `mousePressed`/`mouseReleased` on the physical
   button.

**Risk** — WebSocket latency; the browser must be launched by us.
**Test** — hover a link in the glasses: it highlights. Click it: the page
navigates, with the desktop cursor never moving.

### Stage 5 — The full suite
Wheel → `mouseWheel`; right-click → `button="right"`; `Ctrl`/`Shift`/`Alt` →
the `modifiers` bitmask; drag → the `buttons` field.

**Plus**, from M10, in parallel or after:
- **GSMTC** for media play/pause — a first-party Windows API, no CDP needed.
- **VS Code CLI IPC** for code editing.

### Stage 6 (optional) — non-browser apps
For windows with no app-native channel, the warp fallback requires the panel
to stop being topmost for the duration of the click, which will flicker.
Worth **one experiment** before committing: does a temporary
`SetWindowPos(panel, HWND_NOTOPMOST)` + `SetWindowPos(target, HWND_TOPMOST)`
+ click + restore sequence produce a visible flash? If yes, the honest answer
for arbitrary Win32 apps is the virtual display driver (direction D) after
all.

---

## 7. Empirical experiments to run

These are the things documentation cannot settle:

1. **The sign of Δu/Δv** — Stage 3's test. Two minutes, and it is the most
   likely thing to get wrong.
2. **CDP round-trip latency** over a local WebSocket in .NET. Must stay under
   ~8 ms to keep up with a 120 Hz render loop. If it is too slow, throttle
   `mouseMoved` to 60–120 Hz and keep clicks instantaneous.
3. **Cursor outline legibility** — 1 px vs 2 px black border against pure
   white through the see-through optics in bright ambient light.
4. **The topmost dance** (Stage 6) — does temporarily demoting the panel
   produce a visible flash on the Rokid display?

---

## 8. Sources

- [Extended Window Styles (`WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`)](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)
- [Raymond Chen — *Like the cake, WS_EX_TRANSPARENT is a lie…*](https://devblogs.microsoft.com/oldnewthing/20121217-00/?p=5843)
- [Raymond Chen — *WM_NCHITTEST is for hit-testing…*](https://devblogs.microsoft.com/oldnewthing/20120518-00/?p=7553)
- [Layered Windows hit-testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
- [`WindowFromPoint`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint)
- [`SetCursor`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursor) · [`WM_SETCURSOR`](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-setcursor) · [`WM_MOUSEACTIVATE`](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)
- [`SetForegroundWindow` restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) · [`SendInput`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) · [`ClipCursor`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clipcursor)
- [`GraphicsCaptureSession.IsCursorCaptureEnabled`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled)
- [CDP `Input.dispatchMouseEvent`](https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchMouseEvent)
- [Chromium `content/browser/devtools/protocol/input_handler.cc`](https://source.chromium.org/chromium/chromium/src/+/main:content/browser/devtools/protocol/input_handler.cc)
- [Chromium `ui/views/win/hwnd_message_handler.cc`](https://source.chromium.org/chromium/chromium/src/+/main:ui/views/win/hwnd_message_handler.cc)
