# M7 — Why a "pinned monitor" is not a real monitor: the honest technical report

**Date:** November 2026
**Research inputs:** `.research/i1/findings.md` (architecture & alternatives),
`.research/i2/findings.md` (content-acquisition mechanisms).
**Verification:** the two load-bearing Microsoft Learn quotes were opened and
checked verbatim during this session; the Rokid hardware facts were checked
against Rokid's own product pages.

---

## 0. The one-paragraph answer

Windows has no 3D desktop compositor. DWM composites every top-level window
into an axis-aligned 2D rectangle, and the OS deliberately refuses to let one
process read another process's pixels — that refusal is enforced by the
kernel (`dxgkrnl.sys`), by DXGI, and by DirectComposition, each with a
documented error code. `Windows.Graphics.Capture` is not a convenience we
chose; it is the only sanctioned bridge across that boundary, and it hands
back a texture precisely so that *we* can apply the 3D transform that the OS
refuses to apply. So the pipeline is not "capture a tab instead of anchoring a
monitor" — it is "the monitor cannot exist as a monitor, so the pixels must
come to us and we must place them ourselves."

There is a second, independent reason that is easier to miss: the Rokid Max
in the mode we use is **monoscopic**, and its birdbath optics are collimated
to roughly 4 metres. There is no hardware depth cue whatsoever. A "pinned
monitor at 1.5 m" on this device is a *perspective illusion* produced by our
projection matrix, not a physical screen at a physical distance. Sizing that
illusion up to 100 % of the field of view does not give you more screen — it
gives you a worse one, because the illusion depends on the panel's edges
being visible inside the frame.

---

## 1. What we are actually running today

| Stage | Mechanism | Where the constraint comes from |
|---|---|---|
| Content | WGC `IGraphicsCaptureItemInterop::CreateForWindow` → `Direct3D11CaptureFramePool` | OS process isolation |
| GPU copy | `ID3D11DeviceContext::CopyResource` into a texture we own | Frame pool recycles its surface |
| Placement | Textured quad, `v_clip = v_world × view × projection` | Our code |
| Anchor | 3DoF orientation + kinematic neck model for parallax | IMU has no translation |
| Output | Borderless `WS_POPUP` on the Rokid monitor, `FLIP_DISCARD` swapchain, waitable object | WDDM / DWM |

Two facts about this pipeline are worth stating plainly, because they answer
the question directly:

1. **We already render to the glasses' full 1920×1080 monitor.** The app owns
   a full-screen window on that display. The only thing that is "small" is the
   quad inside it — and it is small for a geometric reason (§3), not because
   of any capture limitation.
2. **The capture step is not what makes the content flat.** A WGC texture is a
   normal `ID3D11Texture2D`. It can be transformed by any matrix. The reason
   the result reads as a small floating panel is that the panel is drawn as a
   *quad* and the rest of the display is cleared to black — and black is
   optically transparent on Micro-OLED.

---

## 2. The obstacle table — hard limit vs. our design choice

This is the distinction the user asked for, so it is stated as a table
rather than prose.

| # | Obstacle | Verdict | Why |
|---|---|---|---|
| 1 | Windows DWM has no 3D window composition | **HARD OS LIMIT** | DWM composites into 2D screen-space `RECT`s. There is no hook for a third-party perspective matrix. |
| 2 | Cross-process pixel access is refused | **HARD OS LIMIT** | `CreateTargetForHwnd` → `DCOMPOSITION_ERROR_ACCESS_DENIED`; DXGI allows one flip swapchain per HWND → `DXGI_ERROR_INVALID_CALL`. |
| 3 | `DwmRegisterThumbnail` cannot be textured | **HARD OS API LIMIT** | It renders into a destination `HWND` in 2D screen space and exposes no `ID3D11Texture2D`. |
| 4 | The display is one flat plane | **HARD HARDWARE LIMIT** | Depth on a see-through 2D panel is an illusion, by construction. |
| 5 | Monoscopic 2D mode, zero disparity | **HARD HARDWARE LIMIT** (in 2D mode) | Identical frames to both eyes; vergence locked by optics, not by content. |
| 6 | Optics collimated to ~4 m | **HARD HARDWARE LIMIT** | The birdbath focus plane is fixed; the diopter dial moves it, the software cannot. |
| 7 | 3DoF — no absolute position | **HARD HARDWARE LIMIT** | The panel can be anchored in *direction*, never to a wall. |
| 8 | 44.24° horizontal FOV | **HARD OPTICAL LIMIT** | A full-screen pinned plane cannot be rotated without being cut by the bezel. |
| 9 | ~1 frame of capture latency + ~1 frame of present | **HARD PIPELINE LIMIT** | Reps, no timewarp in the glasses' firmware. |
| 10 | 120 Hz forces Limited Range 16–235 | **HARD HW LIMIT** (mitigated) | Bridge EDID; we override it with NVAPI. |
| 11 | **50 % FOV panel width** | **OUR DESIGN CHOICE** | Chosen so the panel survives natural head rotation without bezel clipping. |
| 12 | **Per-window capture instead of a virtual monitor** | **OUR DESIGN CHOICE** | Chosen to avoid shipping a signed UMDF driver. |
| 13 | **Input goes to the original window** | **OUR DESIGN CHOICE** | Simplicity; a gaze-ray cursor is possible later. |

So: of the thirteen obstacles, **ten are hardware or OS limits we cannot
design our way around**, and **three are choices we made** — and each of the
three is reversible.

---

## 3. Why "just make the pinned thing full-screen" is the wrong conclusion

This is the part where an honest answer has to be unflattering to the
question, so here it is.

With a 44.24° horizontal FOV and a panel that is currently 50 % of it
(22.12° wide, half-width 11.06°), the geometry is:

```
half-FOV                 = 22.12°
panel half-width         = 11.06°
max unrotated head turn  = 22.12 − 11.06 = 11.06°
```

That 11.06° is `SpatialAnchorOptions.MaxUnclippedConeDegrees()`, and it is
why the comfort cone is clamped: a wider cone breaks its own promise before
it starts moving.

Now scale the panel to 100 % of FOV. Its half-width becomes 22.12°, so the
maximum turn before clipping is **0°**. Concretely, a **5° head turn
severs 5/44.24 = 11.3 %** of the virtual monitor against the physical bezel,
and the corresponding 11.3 % of the Micro-OLED goes transparent.

And this is not the only cost:

- **Stationary, it is a plain monitor.** Looking straight ahead, a full-FOV
  world-locked quad is visually indistinguishable from a head-locked 0DoF
  display. The user gains no screen real estate, because the physical FOV is
  the ceiling on how much screen can exist at once.
- **It loses sharpness.** Native presentation is 1:1 with the Micro-OLED
  subpixels. A textured quad is resampled by the GPU, so ClearType subpixel
  rendering is destroyed the moment the head is not perfectly still.
- **It removes the depth cue that makes the illusion work.** The panel's
  edges are what tell the visual system "this is an object 1.5 m away". Fill
  the frame with the panel and there is no edge, no parallax reference, and
  nothing but perspective convergence — the illusion collapses back to a
  monitor.
- **It costs peripheral awareness.** The 50 % design leaves half the
  horizontal field optically clear, so the user still sees their keyboard,
  their mouse, and the real room.

The honest summary: **full-screen world-lock is not "the same thing, but
bigger". It is the same thing, made worse.** The panel is small *because* a
44° FOV is a peephole, and a peephole is only legible if it has a visible
frame.

---

## 4. The content question: can we skip WGC?

No — for an arbitrary third-party window. Every alternative was checked:

| Mechanism | Verdict | Blocker |
|---|---|---|
| **WGC** (current) | **Works** | 1 frame latency |
| `CreateSwapChainForHwnd` on a foreign HWND | Impossible | Swapchains are *sinks*, not capture taps; the foreign app owns its own device. Doc: "you can associate only one flip presentation model swap chain at a time with an HWND" → `DXGI_ERROR_INVALID_CALL`. |
| DirectComposition on a foreign HWND | Impossible | Doc: "the window must belong to the calling process... returns `DCOMPOSITION_ERROR_ACCESS_DENIED`." |
| `DwmRegisterThumbnail` | Dead | DWM composites it into a destination `HWND` in 2D; no texture handle. |
| `SetParent` reparenting | Dead | Axis-aligned 2D child hierarchy; breaks DPI awareness and modern HW-accelerated apps. |
| WebView2 `CompositionController` | Dead | Connects to an `IDCompositionVisual`; no D3D11 texture export path. |
| GDI `BitBlt` / `PrintWindow` | Dead | Blank on hardware-accelerated surfaces; CPU readback. |
| `Windows.UI.Composition` interop | Dead | Presentation sink, not a texture source. |
| DXGI Desktop Duplication | Works, wrong shape | Captures a whole *output*, not a window; feedback loop if aimed at the glasses' own display. |
| **Virtual display driver (IddCx)** | Works, heavy | Signed UMDF driver, admin install. |
| **CEF accelerated OSR** | Works, narrow | Shared D3D11 texture — but only for web content we host ourselves, not the user's Chrome. |

The reason is architectural and worth stating in one sentence: **the OS
routes other processes' pixels through a broker, on purpose, and WGC is that
broker.** We are not working around a limitation; we are using the intended
path.

Note what this means for the *product*, though: if the scope ever narrows
from "pin any app" to "provide a pinned browser", CEF accelerated OSR would
let us own the texture outright and drop the capture step entirely — at the
cost of no longer being able to pin VS Code, a terminal, or anything else.

---

## 5. The alternatives, with honest costs

| | What it delivers | Real cost | Worth it? |
|---|---|---|---|
| **A. Keep today's design** | A floating 3DoF panel, 50 % FOV, any app | +1 frame, off-axis text blur | **Yes** — this is the working product |
| **B. Full-screen world-locked** | Same thing, larger | Keyhole clipping at 5°, worse text, no depth cue, no peripheral view | **No** — strictly worse |
| **C. Composite a whole desktop** | Taskbar + all windows, spatially | Needs a second physical monitor or an HDMI dummy plug (feedback loop otherwise); 2–3 weeks | Maybe, if "a whole desktop" is the actual product |
| **D. Virtual display driver (IddCx)** | A real virtual monitor: Windows Snap Layouts, any app, full desktop | Signed UMDF driver, admin install, 3–4 weeks | **The correct path if the goal is a real multi-window workspace** |
| **E. Run the app on the glasses** | — | **Impossible.** The Rokid Max has no CPU, GPU, or OS. The Rokid Station is a separate Android TV 12 puck; it cannot run x86-64 Windows apps. | **No** |
| **F. SBS 3D mode (3840×1080)** | Real binocular disparity, real vergence at the virtual distance, genuine 3D | Drops to 60 Hz per research (verify on hardware), doubles fill rate, vergence–accommodation conflict against the 4 m focal plane, needs EDID/button mode switching | **The only route to true 3D** |
| **G. Drop spatial pinning** | A perfect 1080p @ 120 Hz secondary monitor | Zero latency, 1:1 pixels, perfect ClearType — but the image moves with your head | **The honest baseline for reading code** |

---

## 6. Recommendation

**Do not build B.** It is the most likely-sounding idea and the least useful.

Two things are worth doing, and they are different from each other:

1. **If the product is "pin a reference window while you work"** — keep the
   current design. The 50 % panel, the 11.06° comfort-cone clamp, and the
   strict world-lock are all correct for the optics we have. The one
   measurable improvement left is latency, not geometry: the render loop
   should tick at 120 Hz independently of WGC's `FrameArrived` rate, so a
   slow target app cannot stutter the projection.

2. **If the product is "a real virtual monitor with a real desktop"** — then
   D (a virtual display driver) is the correct architecture, and the honest
   reason is not "3D-ness". It is that a virtual monitor gives you Windows'
   own window management: snapping, multiple windows, the taskbar, the real
   cursor. Capturing one window at a time will never give you that, no
   matter how the projection is done. That is a product difference, not a
   rendering difference.

3. **If the product must be genuinely 3D** — F is the only path, and it is a
   firmware/EDID change with a 60 Hz cost. Worth a spike, not worth
   committing to before measuring the SBS mode on the actual hardware.

The honest bottom line: **the reason we project a captured window is that
Windows cannot give us a monitor we can put in space, and the reason we
cannot make that monitor bigger is that the glasses have a 44° window into
the world.** Neither is fixable in software. The one thing that would change
the picture materially is not a code change at all — it is stereo.

---

## 7. Sources

**Microsoft Learn (opened and verified during this session):**
- [`IDXGIFactory2::CreateSwapChainForHwnd`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd) — "you can associate only one flip presentation model swap chain at a time with an HWND"
- [`IDCompositionDevice::CreateTargetForHwnd`](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-createtargetforhwnd) — "the window must belong to the calling process... returns `DCOMPOSITION_ERROR_ACCESS_DENIED`"
- [`DwmRegisterThumbnail`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail)
- [`Windows.Graphics.Capture`](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)
- [`Indirect Display Driver Model Overview`](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)
- [`IDXGIOutput1::DuplicateOutput`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput)

**Vendor:**
- [Rokid Max — official product page](https://max.rokid.com/product/index.html) — 3840×1080 Micro-OLED, 50° diagonal FOV, 120 Hz, 2D/3D switch, 3DoF, no onboard compute
- [Rokid Max — rokid.store](https://rokid.store/products/rokid-max) — 50° FoV, 3DoF, 9-axis IMU, 2D/3D

**Prior art / landscape:** `.research/i1/findings.md`, `.research/i2/findings.md`

**Our own code:** `src/RokidDiyStation.Core/Spatial/SpatialAnchorService.cs`
(`MaxUnclippedConeDegrees`), `src/RokidDiyStation.Core/Spatial/RokidDisplayProfile.cs`
(FOV derivation), `src/RokidDiyStation.App/Spatial/WindowCapture.cs` (WGC
pipeline), `src/RokidDiyStation.App/Spatial/PanelRenderer.cs` (D3D11 quad +
swapchain).
