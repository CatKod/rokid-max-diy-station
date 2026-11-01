# M9 — Bridging the mouse to the spatial panel, and what the product actually is

**Date:** November 2026
**Research input:** `.research/i4/findings.md`
**Companion docs:** `M7-FULL-SCREEN-ANCHORING-LIMITS.md`, `M8-INTERACTIVE-PANEL.md`

---

## 0. Executive summary

Three problems, three answers, and they are not the same amount of work.

| # | Problem | Answer | Cost |
|---|---|---|---|
| 1 | The OS cursor floats head-locked over the glasses and can leave the tab | Suppress the OS cursor **only on the Rokid display** (`WM_SETCURSOR` → `SetCursor(NULL)`), and draw our own pointer on the quad | ~1 day |
| 2 | The pinned tab stops playing when it is minimised / hidden | Keep it **on the Rokid display itself, behind our opaque panel**, and defeat Chromium's occlusion pause with two launch flags. A virtual display driver is *not* required | ~2 days (plus browser launch UX) |
| 3 | Cannot click the pinned content with the mouse | Map the mouse to a **panel-relative pointer** (differential, so it does not slide when the head turns) and deliver the click through **Chrome DevTools Protocol** — no OS cursor movement, no jump | ~3-4 days |

**The one thing that is wrong with the current code**, and it is small: the panel
window creates itself with `WS_EX_TRANSPARENT`, and the comment above that
constant says it lets clicks "pass straight through". That is **not what
`WS_EX_TRANSPARENT` does** — it is a *painting order* flag. Hit-testing is
governed by `WM_NCHITTEST` and by layered/disabled state, none of which we use.
So the panel window **already receives** `WM_MOUSEMOVE`; we simply throw the
information away. The pointer bridge needs no new input plumbing at all, only a
`WM_SETCURSOR` handler and a ray-cast.

---

## 1. Problem 1 — the head-locked OS cursor

### 1.1 Why it happens

The Rokid Max is a plain secondary monitor (`\\.\DISPLAY5`). Windows draws the
cursor on a **hardware cursor plane** that is composited at a fixed pixel
coordinate on that output. Because the Micro-OLED panel is rigidly attached to
the user's skull, the cursor image moves with the head. The 3D quad, by
contrast, is **world-locked** — it is drawn where the panel actually is in the
room. The two drift apart the moment the head turns. That is the whole
complaint, and it is not fixable by moving anything: the OS cursor is simply on
the wrong plane.

Second, the quad covers ~50 % of the horizontal FOV (`RokidDisplayProfile`),
so the remainder of the 1920×1080 output is black — optically transparent on
Micro-OLED. The OS cursor happily wanders across that transparent region.

### 1.2 The fix

Our borderless panel window already covers **all** of `\\.\DISPLAY5`. So:

```
WM_SETCURSOR  ->  SetCursor(NULL);  return TRUE;
```

* The cursor disappears the moment the pointer crosses onto the glasses'
  display.
* It comes back the instant the pointer crosses onto the XP-Pen or the
  desktop, because the OS asks whichever window is under the pointer, and that
  window sets the normal arrow.
* No global state is touched: `SetSystemCursor` / `SPI_SETCURSORS` would
  affect every monitor and would survive a crash, so we do **not** use them.
* There is no `SetCursorInfo` in Win32 — only `GetCursorInfo`. A background
  app cannot suppress the cursor for a monitor it does not own; the correct
  mechanism is exactly the one above, and it works because we *do* own a
  full-screen window on that monitor.

**Refinement worth doing at the same time:** once the OS cursor is gone, the
user needs *some* indication of where the mouse is. Draw two things:

* a **bright reticle on the quad** (world-locked, part of the 3D scene), and
* a **dim screen-space tick** at the mouse's position in the 1920×1080 output,
  so the user can find the mouse when the pointer is off the quad.

The screen-space tick is head-locked like the OS cursor was — that is
*correct*, because it reports the mouse, not the content. The complaint was
never about the mouse being head-locked; it was about the mouse being drawn
*as if it were the panel's pointer*.

---

## 2. Problem 2 — keeping the pinned window alive and out of the way

### 2.1 What actually stops the video

Two independent mechanisms, and both must be defeated:

1. **WGC.** A minimised window loses its DWM redirection surface, so
   `Direct3D11CaptureFramePool` stops delivering frames. Microsoft's own
   sample README says it outright: *"Minimized windows can be enumerated, but
   they cannot be captured."* Hidden (`SW_HIDE`) and cloaked windows behave the
   same way. Moving a window entirely off the virtual desktop is **not**
   reliable either: DWM stops updating a surface that intersects no monitor.
2. **Chromium.** Independently of WGC, Chromium stops rendering a window it
   believes is fully occluded (`ui/aura/native_window_occlusion_tracker_win.cc`),
   pauses `requestAnimationFrame`, and throttles timers.

So even if WGC kept delivering frames, the *content* would be frozen.

### 2.2 The window-state matrix

| State | Renders? | WGC frames? | Keeps playing? | Verdict |
|---|---|---|---|---|
| Minimised | No | No | Audio only | Dead |
| Hidden (`SW_HIDE`) | No | No | Audio only | Dead |
| Cloaked (foreign process) | — | — | — | `E_ACCESSDENIED`; cannot even try |
| Off-screen at (-32000, -32000) | No (Chromium: zero monitor intersection ⇒ occluded) | Unreliable | Audio only | Dead, and undocumented behaviour |
| Shrunk to 1×1 / 200×200 | Degraded (CSS reflows, ABR drops to 144p) | Yes, but a 1×1 texture | No | Dead for video |
| Covered, default Chromium | No (occlusion tracker) | Surface kept, content stale | Audio only | Dead |
| **Covered + Chromium occlusion flags** | **Yes** | **Yes** | **Yes** | **Works** |
| **On the Rokid display, behind our panel + flags** | **Yes** | **Yes** | **Yes** | **Works, and invisible to the user** |
| IddCx virtual display | Yes | Yes | Yes | Works, but needs a driver |

### 2.3 The zero-driver answer

**Put the pinned window on the Rokid display, underneath our panel.**

Our panel window is a `WS_POPUP | WS_VISIBLE` topmost window that covers the
entire 1920×1080 output, and it clears the back buffer to opaque black with
`AlphaMode.Ignore` on a flip-model swapchain. Everything underneath it is
therefore **completely invisible to the user** — while still being a fully
visible, fully composited window to DWM.

Consequences:

* The user's XP-Pen display is freed. Problem 2's literal ask ("close it on the
  other screens") is satisfied by *relocating* it, not by hiding it.
* WGC keeps delivering frames: the window is not minimised and not hidden.
* Chromium must be told not to pause. Two switches, both verified in the
  Chromium source:
  * `--disable-backgrounding-occluded-windows`
  * `--disable-features=CalculateNativeWinOcclusion`
* The window is *behind* our topmost panel, so it is genuinely covered —
  which is exactly the case Chromium would normally pause on. Hence the flags.

**The cost is a workflow change:** the pinned browser has to be launched by us
(or through a shortcut we provide) so the flags are applied. For a browser
this is unavoidable in any design, because the same flags are needed for the
CDP bridge in problem 3.

**Alternative if the user insists on an arbitrary app** (not a browser we
launch): an IddCx virtual display, using the pre-signed
`VirtualDrivers/Virtual-Display-Driver`. Same effect, but a signed kernel
driver, admin rights, and Secure-Boot friction. Only worth it if we drop the
"browser-first" assumption.

### 2.4 Hiding it from Alt+Tab

Yes, and it is nearly free:

* `WS_EX_TOOLWINDOW` on the source window removes it from **both** the taskbar
  and the Alt+Tab switcher, while leaving it running and visible. Cross-process
  `SetWindowLongPtr(GWL_EXSTYLE)` is enough. Chromium does not normally reset
  its own ex-styles, so this survives.
* `ITaskbarList3::DeleteTab` removes only the **taskbar button**, not the
  Alt+Tab entry. It is the weaker tool; `WS_EX_TOOLWINDOW` is the right one.

Note the ordering constraint: `WS_EX_TOOLWINDOW` must be applied **after** the
window is created, and it must be re-applied if the app recreates the window.

---

## 3. Problem 3 — clicking the pinned content

### 3.1 The mapping: a panel-relative pointer, not a gaze ray

The obvious mapping — mouse pixel → ray → plane intersection — is **wrong** for
this product, and it is worth being explicit about why.

The mouse lives in the display's pixel space (fixed to the glasses). The panel
lives in world space. Any absolute mapping between them makes the pointer
*slide across the panel* whenever the head turns, because the same display
pixel corresponds to a different world ray. That is the same complaint as
problem 1, just moved onto the quad.

The correct mapping is **differential and panel-relative**:

```
pointerUV  ←  pointerUV + (mouseDelta_display × gain) / panelSize
```

* The pointer's authoritative position is a material coordinate on the panel —
  world-locked, so it stays glued to the content when the head turns.
* The mouse only ever contributes **displacement**, never absolute position.
* On **entering** the Rokid display, re-seed the pointer from the mouse's
  current absolute position via the quad's screen-space homography, so the
  pointer appears under the mouse instead of jumping to an arbitrary point.
  After that, motion is relative.

This is the same decoupling that `M8-INTERACTIVE-PANEL.md` arrived at from the
gaze side ("control-display gain"), applied to a mouse instead of to the head.

Implementation of the absolute re-seed needs the quad's four corners
projected into display space each frame, then a projective homography
(display space → texture space) solved from the four corner correspondences.
A naive axis-aligned rectangle is wrong: the quad is perspective-projected.

### 3.2 Delivering the click

| Mechanism | Cursor jump? | Works with Chromium? | Effort |
|---|---|---|---|
| `SetCursorPos` + `SendInput` | **Yes** — teleports the real cursor | Yes | Low |
| …and restore the position immediately | Visible flicker, breaks hover/drag | **No** — aborts YouTube scrub drags | Low |
| `PostMessage` mouse messages | No | **No** — Chromium checks real input state; text fields and canvas clicks fail | Low |
| UI Automation `InvokePattern` | No | Partial — blind to canvas/WebGL/video scrubbers | High |
| Virtual HID mouse driver | **No** — Windows has exactly one system cursor; injected HID moves *it* | Yes | Very high, and pointless |
| **CDP `Input.dispatchMouseEvent`** | **No** | **Yes — perfect** | Medium |

Only CDP survives the "no cursor jump" constraint for a browser. It injects
genuine trusted `PointerEvent`s into Blink, with correct hover, focus, text
selection and drag semantics.

**However — CDP is a *browser-only* answer.** For code editors, terminals,
media players, Office and arbitrary Win32 apps there are other mechanisms, and
some of them are better. See `M10-INPUT-LAYER-STACK.md` for the full
four-layer stack (app-native channels → browser protocol → Win32 messages →
hardware warp).

> **Correction (added after the I5 research):** the hardware-warp fallback is
> **not** invisible. Windows keeps a single cursor position for the whole
> virtual desktop, so warping it to `\\.\DISPLAY5` *removes* the arrow from
> `\\.\DISPLAY1` and `\\.\DISPLAY2` — it vanishes from under the user's hand
> on the XP-Pen, hover there collapses with `WM_MOUSELEAVE`, and it pops back
> on restore. The warp is invisible **on the glasses**, not **on the desk**.
> Treat it as a real cost, not a free universal fallback.

### 3.3 The CDP bridge, and its one real complication

Chrome must be started with `--remote-debugging-port` and a **non-default
`--user-data-dir`**. A browser already running without the flag cannot be
attached to; we therefore have to launch the pinned browser ourselves.

The complication: WGC captures the **whole browser window**, chrome and all,
while CDP dispatches coordinates in the **page viewport's CSS pixels**. The
two coordinate systems differ by the browser chrome height and by the device
pixel ratio.

The clean way out is to launch the pinned page in **app mode**:

```
chrome --app=https://… --remote-debugging-port=9222 --user-data-dir=…
```

An app-mode window has **no tab strip, no address bar, no bookmark bar**. Its
client area *is* the page viewport, so the panel UV maps directly onto CDP
coordinates with a single scale factor. This also solves the user's earlier
observation that "pinning a tab" really means pinning a *window* — app mode
makes the window and the tab the same thing.

Two further CDP details worth knowing:

* Hover needs a `mouseMoved` dispatch before the click, otherwise the first
  click lands on a page that has never seen the pointer.
* The port has no authentication and exposes the signed-in session. Bind it to
  localhost only and use a dedicated profile.

---

## 4. The end goal, stated plainly

The user could not name what he wanted. Here it is.

### The product

> **A spatially anchored second screen for the desktop, driven by the mouse.**
>
> Any application window can be lifted out of the physical desktop and pinned
> into the room as a world-locked panel in the Rokid Max. It keeps running and
> playing whether or not it is visible on a physical monitor. The mouse works
> on it directly: the pointer is part of the panel, not part of the desktop,
> and it never has to leave the panel to do its job.

### The name

The established terms are:

| Term | Meaning | Fits us? |
|---|---|---|
| **Head-Mounted Extended Display (0DoF HUD)** | A head-locked second monitor. The mouse works natively. | Fits problem 2, loses the spatial anchoring |
| **Spatial Slate** / *Physicalized Window* | A world-anchored 2D surface behaving like a physical tablet. | Fits the visual, not the input |
| **Spatially Anchored Virtual Panel with Decoupled Raycast Pointer** | A world-locked panel whose interaction is a decoupled spatial pointer. | **Fits best** |
| **Spatial Virtual Monitor** | An OS-level virtual display anchored in space. | The architecture underneath, not the product |

The best single name is **Spatial Slate** — a slate pinned in the room that
you can point at. The precise technical name is **spatially anchored virtual
panel with a decoupled pointer**.

### One product or two?

**Two, and they should not be confused.**

* **Product A — "the glasses are a second monitor."** Zero 3D. Native cursor,
  native text, native everything. Cheap to build, and the honest answer to
  "I want to watch YouTube while I work". But it is head-locked, which in
  optical see-through is genuinely uncomfortable after 20-30 minutes (the
  vestibulo-ocular reflex conflict), and it throws away the thing that made
  this project interesting.
* **Product B — "a slate pinned in the room you can point at."** The one we
  are building. World-locked, 3DoF, with its own pointer.

The current app is Product B, minus the pointer. Everything in this document
serves Product B. Product A remains a legitimate, much cheaper fallback and
should stay as a *mode*, not a competitor.

### What Product B is NOT

* It is **not** a virtual monitor. There is no OS-level display, no window
  management on the glasses, no cursor that lives there.
* It is **not** a streaming client. The content is rendered locally, at full
  resolution, by the application itself.
* It is **not** a "cast" — nothing is transcoded or mirrored.

---

## 5. Staged plan

### Stage 1 — kill the head-locked cursor, and put a pointer on the panel
1. Add `WM_SETCURSOR` → `SetCursor(NULL)`, return `TRUE`.
   *Do not remove `WS_EX_TRANSPARENT` for this — it is not a hit-testing flag
   and removing it changes painting order for no benefit.*
2. Handle `WM_MOUSEMOVE` on the panel window; keep the last position.
3. Compute the quad's screen-space corners (project with the same MVP the
   renderer uses) and solve the display→texture homography.
4. Render a reticle quad on the panel at the pointer's UV, plus a dim
   screen-space tick at the raw mouse position.
5. Add the differential gain control (mouse displacement → UV displacement).

**Test:** the OS cursor must be gone on the glasses, the reticle must stay
glued to the panel when the head turns, and moving the mouse must move the
reticle.

### Stage 2 — make the pinned window survive
6. Move the pinned window onto the Rokid display, behind our panel.
7. Provide a launcher for the pinned browser with
   `--disable-backgrounding-occluded-windows` and
   `--disable-features=CalculateNativeWinOcclusion`.
8. Apply `WS_EX_TOOLWINDOW` to the source window so it leaves Alt+Tab.

**Test:** a YouTube video must keep playing, at full frame rate, with the
browser window completely invisible to the user on every physical display.

### Stage 3 — deliver the clicks
9. Connect a WebSocket client to `http://127.0.0.1:9222/json/list`.
10. Map panel UV → page viewport CSS pixels (trivially, in app mode).
11. Dispatch `mouseMoved` / `mousePressed` / `mouseReleased`.

**Test:** clicking the video's play/pause button in the glasses must work
without the desktop cursor moving at all, and dragging the seek bar must
work.

> **Note (added after the I5 research):** this stage is *browser-specific*.
> For a cheaper, protocol-free win that covers the user's original "pause the
> video" complaint, use WinRT **GSMTC** (`TryTogglePlayPause`) instead — see
> `M10-INPUT-LAYER-STACK.md`, which also covers code editors, terminals and
> arbitrary Win32 apps.

### Stage 4 — generalise
12. `PostMessage`-based click delivery as a fallback for non-browser windows.
13. IddCx virtual display only if the product genuinely needs arbitrary apps
    to be relocated without our cooperation.

---

## 6. Sources

**Verified during this session:**

- [Windows Graphics Capture HWND sample README](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/cpp/ScreenCaptureforHWND/README.md) — *"Minimized windows can be enumerated, but they cannot be captured."*
- [Chromium `native_window_occlusion_tracker_win.cc`](https://chromium.googlesource.com/chromium/src/+/main/ui/aura/native_window_occlusion_tracker_win.cc)
- [Chromium `content_switches.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/public/common/content_switches.cc)
- [Chromium `ui_base_features.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/base/ui_base_features.cc)
- [CDP Input domain — `Input.dispatchMouseEvent`](https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchMouseEvent)
- [`WM_SETCURSOR`](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-setcursor) · [`SetCursor`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursor) · [`GetCursorInfo`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorinfo)
- [`SendInput`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) · [`SetForegroundWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)
- [`DWMWINDOWATTRIBUTE`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) · [`ShowWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow) · [`SetLayeredWindowAttributes`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes)
- [`GraphicsCaptureSession.IsCursorCaptureEnabled`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled)
- [YouTube IFrame Player API — viewport constraints](https://developers.google.com/youtube/iframe_api_reference)
- [Valve OpenVR — `IVROverlay::ComputeOverlayIntersection`](https://github.com/ValveSoftware/openvr/blob/master/headers/openvr.h)
- [Windows Mixed Reality — input switching](https://learn.microsoft.com/en-us/windows/mixed-reality/enthusiast-guide/input-switching)
- [Indirect Display Driver Model overview](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)
- [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)

**Our own code:** `src/RokidDiyStation.App/Spatial/PanelRenderer.cs`,
`src/RokidDiyStation.App/Spatial/WindowCapture.cs`,
`src/RokidDiyStation.App/Spatial/WindowPicker.cs`,
`src/RokidDiyStation.App/Spatial/SpatialStation.cs`,
`src/RokidDiyStation.Core/Spatial/SpatialAnchorService.cs`,
`src/RokidDiyStation.Core/Spatial/PinnedPanel.cs`,
`src/RokidDiyStation.Core/Spatial/PanelPose.cs`,
`src/RokidDiyStation.Core/Spatial/RokidDisplayProfile.cs`.
