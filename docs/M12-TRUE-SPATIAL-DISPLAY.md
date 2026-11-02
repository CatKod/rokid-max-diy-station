# M12 — True Spatial Display: the virtual monitor, and why Direction D is back

**Status:** architecture decided, implementation starting
**Supersedes:** the "Direction D is wrong" verdict in [M8](./M8-INTERACTIVE-PANEL.md)
**Research:** `.research/j1/` (3 Gemini deep-research tasks, 2026-11-02)

---

## 1. Why this document exists

Direction D — a virtual display driver — was rejected twice, and both times
for the same reason:

> *"An IddCx monitor adds ZERO input capability."*

That reason was correct, and it was decisive. It is also now **obsolete**.

The Custom Hardware Cursor (M11) works: a 3D virtual cursor quad, rendered
in the glasses' own space, positioned by mapping the physical mouse's ray
onto the anchored 3D panel quad. It has been verified on hardware, with a
trusted click landing at the right CSS pixel.

That is exactly the missing capability. A virtual monitor gives us the
*display* half of a true spatial computer; the custom cursor gives us the
*input* half. Together they are a real Windows desktop floating in the
air:

- a real taskbar,
- real z-order,
- real focus and activation,
- real drag-and-drop,
- **any** application, not just Chromium ones.

The M8 verdict ("not the right answer") was about the *input* problem. The
input problem is solved. The verdict no longer applies.

---

## 2. The architecture

```
   physical mouse
        │
        ▼
   ┌─────────────────────────────────────────────┐
   │  Windows desktop                             │
   │                                             │
   │  DISPLAY1 (primary)  DISPLAY2        VDD    │  ← IddCx virtual monitor
   │  0,0  1920x1080      1920,0         -1920,0 │     at NEGATIVE coords
   │  ┌──────────┐        ┌────────┐      ┌────┐ │
   │  │ desktop  │        │ Artist │      │ AR │ │  ← WGC captures THIS
   │  └──────────┘        └────────┘      │desk│ │     (the whole monitor)
   │                                       └────┘ │
   └─────────────────────────────────────────────┘
        │
        │  GraphicsCaptureItem.CreateForMonitor(HMONITOR)
        ▼
   ┌─────────────────────────────┐
   │  D3D11 texture (whole screen)│
   └─────────────────────────────┘
        │
        ▼
   ┌─────────────────────────────┐
   │  3D anchored quad in glasses │  ← the existing renderer, unchanged
   │  + our 3D cursor quad       │
   └─────────────────────────────┘
```

Input is now **purely feedback**. The OS handles everything: hit-testing,
focus, z-order, clicks, wheel, keyboard, window drags, OLE drag-and-drop.
Our job is only to draw the cursor where the OS thinks it is.

### 2.1 The mapping

With the process already PerMonitorV2-aware (see `app.manifest`), the
coordinate spaces agree, and the mapping is exact:

```
inside  = x >= monLeft && x < monRight && y >= monTop && y < monBottom
u       = (x - monLeft) / monWidth
v       = (y - monTop)  / monHeight
```

`GetCursorInfo()` gives the position *and* the current cursor shape, so
the 3D cursor can become an I-beam over text and a resize arrow over a
window edge.

### 2.2 Why the virtual monitor sits at negative coordinates

Windows' CCD rules **forbid overlapping active monitor rectangles** —
`ChangeDisplaySettingsEx` returns `DISP_CHANGE_BADPARAM`. The virtual
monitor therefore cannot share the Rokid Max's coordinates at (3840, 0).

That is fortunate, because if it could, our own `WS_EX_TOPMOST` opaque AR
panel would sit on top of it and occlude the capture — a hall-of-mirrors
feedback loop.

At (-1920, 0) the virtual monitor is a separate rectangle that our panel
never touches, and Windows has **no concept of an "off-screen" monitor**:
it is part of the virtual desktop, and the mouse reaches it normally.
Moving the mouse left past the edge of the laptop screen puts the cursor
on the virtual monitor — where the user sees it in the glasses.

---

## 3. The three pillars, assessed

### 3.1 The driver — feasible, with a caveat

| Option | Signing | Verdict |
|---|---|---|
| **VirtualDrivers/Virtual-Display-Driver** v25.7.23 | SignPath Authenticode (NOT WHQL) | **Chosen.** On winget as `VirtualDrivers.Virtual-Display-Driver`. |
| Microsoft IddSampleDriver | test-signed | Reference only; needs WDK to build. |
| Amyuni USBMMIDD | genuine WHQL | Commercial licence. The fallback if signing bites. |

Three real caveats, none of them fatal:

1. **It is not attested.** The SignPath certificate must be imported into
   `LocalMachine\TrustedPublisher` and `Root` (admin). Not a one-click
   install. It also **fails outright on Windows 11 ARM64**
   (`0x800B0109`).
2. **`pnputil` alone does not install it.** IddCx drivers are
   software-only root devices; the `Root\MttVDD` devnode must be created
   (`nefconw`, or `devgen` + `pnputil`).
3. **Multi-GPU risk.** This laptop has an RTX 3050 *and* an Intel UHD.
   An IddCx driver picks a render adapter, and getting it wrong can keep
   the dGPU awake or crash DWM. The driver lets the adapter be pinned in
   `vdd_settings.xml`.

Runtime attach/detach does **not** need admin: `ChangeDisplaySettingsEx`
with zeroed dimensions detaches the monitor, and restoring them
reattaches it. That is how "Open Spatial Display" becomes a user action.

### 3.2 WGC on a monitor — feasible

`IGraphicsCaptureItemInterop::CreateForMonitor(HMONITOR, ...)` is vtable
**slot 4**. `CreateForWindow` is slot 3 and **must be declared first** in
the C# interface — otherwise the CLR binds `CreateForMonitor` to slot 3
and calls `CreateForWindow` with a monitor handle.

The good news: our existing `WindowCapture.cs` already has the correct
IIDs and the activation dance. The change is one extra method in the
interface declaration and one different call.

WGC-on-monitor captures the composed desktop — wallpaper, all windows in
their z-order, the taskbar. It is immune to occlusion by unrelated
windows, which is why it beats DXGI Desktop Duplication here (that one
captures a single *output* and is exclusive).

One dependency: the IddCx driver must keep pumping its swapchain. If it
stalls, DWM stops presenting and the capture stalls with it.

### 3.3 The new input model — simpler, and better

The whole CDP stack — `SpatialInputCoordinator`, `BrowserInputRouter`,
`CdpClient`, `KeyboardInput`, the `WH_KEYBOARD_LL` hook — goes away. That
is roughly 2,000 lines of code whose entire job was to fake what the OS
does natively.

What replaces it is ~80 lines: read the cursor, map it, draw it.

`IsCursorCaptureEnabled` stays **false**, so the OS arrow is not baked
into the capture. Otherwise there would be two cursors: a lagging,
distorted OS ghost in the texture, and our 3D cursor in the air.

---

## 4. The stages

| Stage | Goal | Files | Verified by |
|---|---|---|---|
| **0** | Driver installed, monitor extends at (-1920, 0), 1920x1080 | external package; `docs/IDDCX-SETUP.md` | `tools/ProbeDisplays`: the rectangle is exactly (-1920, 0, 0, 1080) |
| **1** | WGC captures the *monitor* | new `src/RokidDiyStation.App/Spatial/DisplayCapture.cs` (sibling of `WindowCapture.cs`) | the panel shows the virtual monitor's whole desktop, taskbar included |
| **2** | Cursor feedback loop | `src/RokidDiyStation.App/Spatial/PointerInput.cs` (refactored) | `tools/ProbeVirtualCursor`: the 3D cursor tracks the OS cursor to < 1 px |
| **3** | CDP retired | delete `src/RokidDiyStation.App/Input/` (all 6 files) | a native app (Notepad, Explorer) is clickable in the glasses |
| **4** | Polish | window-snap hotkey, dynamic cursor shapes | `Win+Shift+Left` moves the window; the cursor becomes an I-beam over text |

Each stage is independently testable and each delivers something visible.
Stages 0–2 are the risky ones; stages 3–4 are mechanical.

### What is kept

`PanelRenderer` (the 3D quad and its shaders), `SpatialAnchorService` (the
IMU fusion and the anchor), `VirtualCursor` and `CursorTexture`, the WGC
frame plumbing, the whole `RokidDiyStation.Core` fusion stack.

### What is deleted

`src/RokidDiyStation.App/Input/` in its entirety: `AppLauncher`,
`BrowserInputRouter`, `CdpClient`, `CdpDiscovery`, `SpatialInputCoordinator`,
`SquirrelExecutable`. Plus `KeyboardInput.cs` and the tray menu's
"Open an interactive app…" submenu.

---

## 5. The honest costs

1. **"Lost mouse."** The cursor leaves the physical screen when it crosses
   onto the virtual monitor. If the user takes the glasses off, the mouse
   appears stuck. Mitigation: snap the cursor home when the glasses
   disconnect, plus a hotkey.
2. **Static cursor shape.** A fixed arrow loses hover feedback. Mitigated
   in Stage 4 by reading `GetCursorInfo().hCursor`.
3. **Windows spawn on the primary display.** The user drags them across
   (or uses `Win+Shift+Arrow`). A snap hotkey helps.
4. **Admin required once**, for the driver.
5. **The taskbar.** The system tray is hardcoded to the *primary* display.
   A secondary monitor gets `Shell_SecondaryTrayWnd` — Start button and app
   icons, but no system tray. The full taskbar can be enabled with
   `ShowTaskbarOnAllDisplays = 1`.

None of these is worse than the status quo, which is "only Chromium
apps launched with a flag can be interacted with, at all".

---

## 6. What would make this worse than CDP

Honesty requires naming the failure modes:

- **The driver will not install or will not survive a reboot** on some
  machines. Then the product has a hard prerequisite it cannot satisfy
  without an EV certificate.
- **The virtual monitor's aspect ratio will not match the panel's.**
  The Rokid Max is 16:9 per eye, so 1920x1080 matches exactly — but a
  different mode (21:9) would need letterboxing, and getting the
  letterbox wrong means the cursor and the content disagree.
- **Windows will relocate windows** when the display topology changes
  (sleep, hot-plug, driver restart). The virtual monitor's contents may
  get shuffled onto the physical screen.
- **The 3D cursor will lag** the OS cursor by one frame, if the cursor is
  only updated on the render tick. The old differential model had the
  same property, and it was acceptable.
