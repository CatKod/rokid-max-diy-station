# M8 — Making the pinned panel interactive: why a virtual display driver is the wrong answer

**Date:** November 2026
**Research input:** `.research/i3/findings.md`
**Verification:** every load-bearing claim below was checked against a primary
source during this session — Microsoft Learn, the Chromium source, the Rokid
protocol in our own repo, and the live hardware on this machine.

---

## 0. The short answer

**Direction D (a virtual display driver) is not the right answer, and it is
worth saying so plainly before anything else: it solves a different problem
than the one you have.**

Your problem is not *"the content is not on a real screen"*. Your problem is
*"I cannot reach the content"*. Those are separable, and an IddCx driver only
addresses the first one.

An IddCx virtual monitor gives Windows a synthetic display. Windows then
behaves exactly as if a physical monitor were attached — which means the
cursor, the focus, the window management, everything moves there. **But you
still have to physically move a pointing device onto that display.** The
driver does not add a single new input capability. It moves the *target*
further from your hand, not the *hand* closer to the content.

And for your specific setup it makes things **worse**:

- Your pointer is an **XP-Pen Artist 13.3 digitiser**. A pen *display* maps
  its digitiser to its own embedded screen only — mapping across the virtual
  desktop is a pen-*tablet* feature, and pen displays "cannot span multiple
  displays" (Windows PnP tablet docs). A virtual monitor would be a fourth
  screen your pen **cannot reach at all**. You would lose the ability to
  interact with the content you are pinning, in the very act of adding it.
- The glasses are optical see-through: you can see your laptop through them.
  A cursor that teleports to an invisible coordinate space is *less* usable
  than one on the screen you can actually see.

The correct diagnosis is one the earlier report did not make, because it was
asked the wrong question:

> **The panel is view-only by design, not by capability.**
> `PanelRenderer.cs:264` creates the render window with
> `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`, and the window procedure answers
> `WM_MOUSEACTIVATE` with `MA_NOACTIVATE`. Clicks pass straight through the
> panel to whatever is behind it, and it never takes focus. There is no input
> path at all — not because Windows forbids one, but because **we never
> built one**.

That is a design decision, it is ours, and it is the cheapest thing on this
list to change.

---

## 1. What you can build right now, with no driver and no admin rights

The good news: **you already have 90 % of the geometry.** `AnchorView`
(`SpatialAnchorService.cs:228`) already carries `EyePosition`,
`GazeDirection`, `PanelCorners` and `HeadOrientation` every frame. A
ray-cast from the eye through the panel is a dozen lines of arithmetic.

### The shape of the feature

```
IMU orientation ──► gaze ray ──► intersect panel plane ──► (u,v) on the quad
                                                              │
                             ┌────────────────────────────────┴──────────────┐
                             ▼                                               ▼
                  render a cursor on the 3D quad              map (u,v) → window client px
                                                                       │
                             trigger (hotkey / clicker) ─────────────────┤
                                                                       ▼
                                                        deliver the click to the target
```

### Step 1 — cast the gaze ray

The eye position and the panel's plane are both known:

```
d = forward(headOrientation)                        // unit gaze ray
n = panel normal (points back at the user)
t = dot(panelCentre - eye, n) / dot(d, n)          // ray-plane distance
I = eye + t * d                                     // hit point
u = dot(I - panelCentre, right) / panelWidth  + 0.5 // -0.5..+0.5
v = dot(I - panelCentre, up)    / panelHeight + 0.5
```

Reject the hit if `t <= 0` (the ray points away) or if `u`/`v` fall outside
`[0,1]` (the ray misses the panel). The panel's `Right`/`Up`/`Normal` all come
from `PinnedPanel.Pose`, which is already rigid and gravity-locked.

### Step 2 — draw a cursor on the quad

Add a second small quad to the vertex buffer, positioned at `(u,v)` on the
panel's surface, slightly offset along `n` so it does not z-fight. A ring or
crosshair, drawn with its own tiny solid-colour shader, or as a second draw
call with the existing pixel shader and a 1x1 white texture.

### Step 3 — deliver the click

This is where the options genuinely differ, and they differ a lot in
reliability.

| Delivery | How | Works with a browser? | Notes |
|---|---|---|---|
| **`SetCursorPos` + `SendInput`** | Move the real OS cursor to the target's screen coords, then inject a left click | **Yes, perfectly** | This is what VR desktop runtimes do. But the OS cursor leaves your physical screen. |
| **`PostMessage` mouse messages** | Synthesise `WM_MOUSEMOVE`/`WM_LBUTTONDOWN`/`UP` to the target HWND | **Fragile** | Chromium discards hover/capture state; text inputs never get focus; `SetCapture` fails on an inactive thread. |
| **UI Automation** | `IUIAutomation::ElementFromPoint` + `InvokePattern::Invoke()` | Partial | Blind to `<canvas>`, WebGL, Google Docs; 50-300 ms cross-process latency. |
| **CDP `Input.dispatchMouseEvent`** | Speak the DevTools protocol to the browser | **Yes, perfectly** | The best answer *if the pinned window is a browser*. See section 2. |
| **`Windows.UI.Input.Preview.Injection`** | WinRT input injector | Yes | Requires the `inputInjectionBrokered` capability — **unavailable to an unpackaged Win32 app**. |

### Step 4 — the trigger

A gaze ray needs a click trigger. Options, in order of practicality:

- **A global hotkey** (e.g. `Ctrl+Alt+Enter`). Zero hardware, works today —
  you already have `HotkeyBinding` doing exactly this.
- **The mouse side-buttons**, if you have a mouse with them: `RegisterHotKey`
  does not cover mouse buttons, but `GetAsyncKeyState(VK_XBUTTON1)` polled on
  the tracking thread does.
- **A Bluetooth presentation clicker** — a ~$12 BLE HID remote. It appears as
  a normal keyboard HID device, so it works with `RegisterHotKey` for free.
  You already have Rokid's HID plumbing; a clicker is far simpler than any
  Rokid-side work.
- **Rokid temple buttons — not usable.** The glasses' physical buttons are
  brightness and volume, and the MCU consumes them internally. There is no
  spare button to repurpose. Our own `PacketParser` reads a 3-byte
  keys/proximity region at bytes 45-47, but that region carries wear
  detection, not button state.

### On dwell-clicking

Do **not** ship dwell as the primary mechanism. Dwell is the classic Midas
touch: it fires while you are reading. Published comparisons of gaze
pointing for small targets found magnification beat dwell on hit rate, and
dwell required 500-800 ms per target. It is a fallback at best.

### On accuracy — this is the part that decides the design

Head-pointing in an HMD is measurably coarse. A 2023 TVCG comparison of
selection modalities reported head pointing at a **4.5 % error rate** versus
**3.4 % for gaze** and **1.9 % for a controller**; an AR target-selection
study measured head-pointing mean error at **0.32 degrees (SD 0.22)**.
Dynamic control-display gain cut head-pointing error from **14.0 % to about
10.3 %**.

Translate that to your panel: the panel subtends 22.12 degrees across
roughly 1 920 px of texture, so **0.32 degrees is about 28 px of error**. A
12 px button is therefore *not* reliably hittable with a raw 1:1 gaze ray.
Two mitigations, both standard in the literature:

1. **Control-display gain** — decouple cursor movement from head rotation so
   small head motions produce small cursor motions. This is what the HeadShift
   paper does, and it is a ~20-line change to the ray-cast step.
2. **Bubble-cursor magnification** — expand the cursor as it approaches a
   target. Bubble-cursor and Bubble-Lens research in AR found magnification
   gave higher hit rates than dwell or zoom, and the Bubble Lens reduced
   error rates by **37.9 %** versus a plain bubble cursor.

Neither needs a driver. Both need to know where the targets are — which for a
browser is available from the accessibility tree, and which is a hint toward
section 2.

---

## 2. If the pinned content is a browser tab — use CDP, not mouse synthesis

Your case is explicitly *"a browser tab"*. That matters a great deal, because
there is a protocol designed for exactly this:

**Chrome DevTools Protocol, `Input.dispatchMouseEvent`.** You connect to the
browser's WebSocket debugger endpoint, send a mouse event at CSS pixel
coordinates, and Chromium synthesises a genuine, trusted `PointerEvent` —
with correct focus, hover, text selection, and drag semantics. Every problem
that makes `PostMessage` fail disappears, because the event never has to
travel through the Win32 input queue at all.

**The mechanics:**

```
GET http://127.0.0.1:9222/json/list     -> list of open tabs
                                          -> each has webSocketDebuggerUrl
ws://.../devtools/page/<id>              -> send Input.dispatchMouseEvent
```

**Requirements and costs, honestly:**

- Chrome must be started with `--remote-debugging-port=9222` and a
  **non-default `--user-data-dir`**. You **cannot** attach to an
  already-running browser that was launched without the flag. This is a real
  constraint on your workflow: you would need to launch the pinned browser
  from the app (or from a shortcut the app controls), not from the taskbar.
- The port has **no authentication** and grants full control of the browser
  including signed-in sessions. It must stay bound to localhost, and it
  should use a dedicated profile.
- Chrome's own position is that CDP is **not a supported API for third-party
  products** — "not a public or supported API, and we do not guarantee
  backwards compatibility". It works, and it is widely used, but it is not
  contractually stable.

**Is it worth it?** For a browser-only product, yes — nothing else gets you
correct browser input. For a product that must pin *any* app, it is a
special case, and `SetCursorPos` + `SendInput` remains the general answer.

**The alternative if you want a supported API and full web compatibility:**
embed an Edge **WebView2** inside your app and use
`CoreWebView2Controller.SendMouseInput`. It is a documented, supported API,
needs no debug port, and gives perfect input. But it means the pinned content
is a browser *you* host — so it is no longer "pin my existing Chrome tab".
That is a product decision, and it is the same trade-off as the CEF option
from the previous report.

---

## 3. The direction-D deep dive — what it would actually buy

For completeness, since you asked for it explicitly.

### What IddCx gives you technically

- A real display device in Windows Display Settings. Any app can be moved
  to it, maximised, snapped with Snap Layouts. Multiple monitors per adapter
  are supported via `IddCxMonitorCreate`.
- **Lower latency than WGC.** The driver receives the composited desktop
  directly through `IddCxSwapChainReleaseAndAcquireBuffer`, so the frame
  arrives in a buffer the driver owns — one frame of capture latency and one
  GPU copy can be removed relative to the WGC path. The API sequence is
  `IddCxSwapChainReleaseAndAcquireBuffer` -> process ->
  `IddCxSwapChainFinishedProcessingFrame`. **You must call the second one**,
  or the driver host is terminated after a 10-second stall.

### What it costs

| Item | Cost |
|---|---|
| EV code-signing certificate | **$350-$700/year**, plus a D-U-N-S number and business validation |
| HSM to hold the key | $60-$100 one-time, or a cloud HSM at $30-$50/month |
| Microsoft Hardware Dev Center | $99 one-time |
| Administrator rights | Mandatory. **Per-user install is impossible.** |
| Secure Boot | Test-signed drivers need it **off** — which triggers anti-cheat bans and an OS watermark |
| Real-world risk | A kernel-adjacent driver in a consumer app: BSOD surface, driver-store pollution, AV false positives |

There is a **pre-signed open-source alternative** —
`VirtualDrivers/Virtual-Display-Driver`, installable with
`winget install --id=VirtualDrivers.Virtual-Display-Driver` — which sidesteps
the certificate cost entirely. That removes the biggest objection to
direction D. It does not remove the input problem.

### The verdict on D

Direction D is a **real** and **legitimate** piece of work, and it is the
right answer to a different question: *"I want a full desktop on the glasses
with proper window management, and I want my laptop screen free."* If that
is the product, do D — and use the pre-signed driver rather than writing
your own.

It is **not** the answer to *"I cannot click the pinned tab without reaching
over to my other screen."*

---

## 4. Recommendation — a staged plan

### Stage 1 — ship this next, no driver, no admin (the highest value per line of code)

1. **Gaze ray-cast -> cursor on the quad.** The geometry is already in
   `AnchorView`. Add control-display gain so small head motions move the
   cursor slowly.
2. **Trigger via a global hotkey.** `HotkeyBinding` already exists.
3. **Deliver with `SetCursorPos` + `SendInput`.** It is what production VR
   runtimes do, it works with every app, and it is a few dozen lines.

This alone removes the "reach over to the Artist 13.3" problem. It is
**not** a virtual monitor — the panel stays a floating quad in space, which
is the thing you actually like about the product.

### Stage 2 — make it precise

4. **Bubble-cursor magnification** for small targets (37.9 % error reduction
   in the literature).
5. **A $12 Bluetooth clicker** for the trigger, so you do not have to reach
   for the keyboard either. It registers as a normal HID keyboard.

### Stage 3 — if the pinned content is a browser (optional, high quality)

6. **CDP `Input.dispatchMouseEvent`** instead of `SendInput`, when the target
   is a Chromium browser we launched with a debugging port. Perfect focus,
   hover, and text-input behaviour.

### Stage 4 — only if the product becomes "a whole desktop on the glasses"

7. **Pre-signed virtual display driver** (`VirtualDrivers/Virtual-Display-Driver`),
   then capture it with the existing WGC path and drive it with the Stage 1
   gaze cursor. Note that the pen cannot reach it, so input must come from
   gaze — which is another reason Stages 1-2 come first.

---

## 5. The one-line summary

> Direction D changes **where the content is**. Your problem is **how you
> reach it**. The reach problem is solved by a gaze ray-cast plus a trigger,
> which we can build on top of what already exists, with no driver, no admin
> rights, no Secure Boot changes, and no EV certificate — and it preserves
> the floating-panel spatial experience that a virtual monitor would throw
> away.

**The ONE thing to try first:** the gaze cursor with a hotkey trigger
(Stage 1). If it feels good, everything after it is incremental. If it does
not feel good, no driver in the world will fix it — and you will have learned
that without spending a weekend on a signed kernel driver.

---

## 6. Sources

**Opened and verified during this session:**
- [`IDXGIFactory2::CreateSwapChainForHwnd`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd) — one flip swapchain per HWND
- [`IDCompositionDevice::CreateTargetForHwnd`](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-createtargetforhwnd) — cross-process access denied
- [`DwmRegisterThumbnail`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail)
- [`Indirect Display Driver Model Overview`](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)
- [`IddCxSwapChainReleaseAndAcquireBuffer`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/nf-iddcx-iddcxswapchainreleaseandacquirebuffer)
- [`IddCxSwapChainFinishedProcessingFrame`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/nf-iddcx-iddcxswapchainfinishedprocessingframe)
- [`CoreWebView2Controller.SendMouseInput`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.sendmouseinput)
- [Chrome DevTools Protocol — Input domain](https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchMouseEvent)
- [Chromium `LegacyRenderWidgetHostHWND`](https://chromium.googlesource.com/chromium/src/+/main/content/browser/renderer_host/legacy_render_widget_host_win.cc) — the child HWND that actually handles web input
- [Windows PnP support for drawing tablets](https://docs.sevenpens.com/drawtab/guides/platforms/windows/windows-pnp-support) — pen *displays* cannot span multiple displays
- [Active area mapping](https://docs.sevenpens.com/drawtab/core/active-area/active-area-mapping)
- [Comparing Gaze, Head and Controller Selection of Dynamically Revealed Targets in HMDs](https://doi.org/10.1109/tvcg.2023.3320235) — head 4.5 %, gaze 3.4 %, controller 1.9 % error
- [Pinpointing: Precise Head- and Eye-Based Target Selection for AR](https://3dvar.com/Kyt%C3%B62018Pinpointing.pdf) — head-pointing error 0.32 deg (SD 0.22)
- [HeadShift: Head Pointing with Dynamic Control-Display Gain](https://doi.org/10.1145/3689434) — 14.0 % -> ~10.3 % error
- [Beating the Bubble: Bubble Lens](https://dl.acm.org/doi/pdf/10.1145/2556288.2557410) — 37.9 % error reduction
- [Evaluating gaze-based interface tools for small targets](https://www.tandfonline.com/doi/abs/10.1080/0144929X.2011.563801) — magnification > dwell on hit rate
- [WGC occlusion / minimised-window behaviour](https://ryanzh.me/en/blog/pc-window-capture) and [capturing a covered window](https://pioneering.app/blog/capturing-a-covered-window/)

**Open source:** [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)

**Our own code:** `src/RokidDiyStation.App/Spatial/PanelRenderer.cs` (window
styles), `src/RokidDiyStation.App/Spatial/SpatialStation.cs` (mediation),
`src/RokidDiyStation.App/Spatial/WindowPicker.cs`,
`src/RokidDiyStation.App/HotkeyBinding.cs`,
`src/RokidDiyStation.Core/Spatial/SpatialAnchorService.cs` (`AnchorView`),
`src/RokidDiyStation.Core/Spatial/PinnedPanel.cs` (`Pose`),
`src/RokidDiyStation.Core/Protocol/PacketParser.cs` (HID keys region).
