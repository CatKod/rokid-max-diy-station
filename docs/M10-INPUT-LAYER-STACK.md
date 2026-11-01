# M10 — Driving any application from the glasses: the input layer stack

**Date:** November 2026
**Research input:** `.research/i5/findings.md`
**Companion docs:** `M9-SPATIAL-POINTER-BRIDGE.md` (the pointer bridge itself), `M8-INTERACTIVE-PANEL.md`

---

## 0. The short answer

CDP is **not** a browser-only trick that we are stuck with. It is *one instance*
of a general principle:

> **The cursor-jump-free input problem is only solvable when the target
> application cooperates.** Windows has exactly one system cursor, and every
> "inject input" API that reaches an arbitrary app either moves it or is
> rejected.

So the architecture is not "CDP for browsers". It is a **four-layer stack**
where each layer is a different *kind* of cooperation:

| Layer | Cooperation | Covers | Cursor moves? |
|---|---|---|---|
| **1. App-native channel** | The app exposes a control API | VS Code, JetBrains, terminal, media, Office | No |
| **2. Browser protocol** | The app exposes a debug protocol | Chrome/Edge/Brave/Electron | No |
| **3. Win32 message injection** | The app reads Win32 mouse messages | Classic Win32, WinForms | No |
| **4. Hardware warp + `SendInput`** | None — raw OS input | **Everything** | **Yes** |

**And there is no single mechanism that covers all apps.** That is an
architectural fact of Windows, not a limitation of our implementation.

---

## 1. A correction to M9

M9 claimed that the cursor-warp fallback is invisible because the cursor is
already hidden on the Rokid display. **That is wrong**, and it matters.

Windows has **one** cursor position for the whole virtual desktop. Warping it
to `\\.\DISPLAY5` *removes it* from `\\.\DISPLAY1` and `\\.\DISPLAY2` — the
arrow disappears from under the user's hand on the XP-Pen, any hover state
there collapses with `WM_MOUSELEAVE`, and it pops back on restore.

The warp is invisible **on the glasses**. It is not invisible **on the desk**.
M9's fallback ranking was too optimistic; the warp is a real cost to the user,
not a free universal fallback.

---

## 2. Layer-by-layer

### Layer 1 — App-native control channels (highest fidelity, zero cursor movement)

These bypass pixel injection entirely and act on *meaning* rather than
*coordinates*. This is where the real wins are for the "code, not just
browser" case the user asked about.

| Target | Channel | Mechanism |
|---|---|---|
| **VS Code / Cursor** | Named-pipe CLI IPC | `\\.\pipe\<hash>-main-sock`, via `code.exe -r --goto file:line:col` |
| **JetBrains IDEs** | Built-in localhost HTTP server | `http://localhost:63342/api/file/<path>:<line>` |
| **Terminals** | Console input buffer | `AttachConsole(pid)` + `WriteConsoleInput` |
| **Media players** (YouTube/Spotify/VLC) | WinRT **GSMTC** | `GlobalSystemMediaTransportControlsSessionManager` → `TryTogglePlayPause()` |
| **Office** | COM automation | `Marshal.GetActiveObject("Excel.Application")` |

**The GSMTC one is worth calling out.** The user's original concrete complaint
was *"I want to pause the YouTube video."* That is exactly what
`TryTogglePlayPause()` does — background play/pause with no window activation,
no cursor movement, no protocol, no relaunch. It is a *first-party Windows
media API* and it works today.

**What Layer 1 cannot do:** arbitrary coordinate clicks, canvas drawing,
anything that is genuinely a pointer gesture. It is a *command* channel, not a
*pointer*.

### Layer 2 — Browser protocol

CDP `Input.dispatchMouseEvent` remains the best option for an *external*
browser, and the research confirmed there is no better one:

* `--remote-debugging-pipe` — cannot attach to an already-running browser
  (it needs inherited pipe handles from a parent process).
* `chrome.debugger` extension API — works, but Chromium shows a **permanent
  infobar** ("Google Chrome is being debugged by …") that narrows the
  viewport. Unusable here.
* WebDriver / WebDriver BiDi — wraps CDP, adds overhead, same launch-flag
  requirement.
* `CoreWebView2CompositionController.SendMouseInput` — fully supported
  Microsoft API, perfect fidelity, **but only for a browser we host**.

**And the research confirmed, from Chromium's own source, exactly why
`PostMessage` fails:**

* `LegacyRenderWidgetHostHWND::OnMouseRange` → `HWNDMessageHandler::OnMouseRange`
  → `DesktopWindowTreeHostWin::HandleMouseEvent` →
  `RenderWidgetHostImpl::ForwardMouseEvent`.
* Chromium calls the **real** `::GetKeyState(VK_LBUTTON)` and
  `::GetCursorPos()` rather than trusting `wParam`/`lParam`. A posted message
  never updates the thread's physical key state, so the event is inconsistent
  and gets dropped.
* `HWNDMessageHandler::IsSynthesizedMouseMessage` compares the message point
  against the real cursor position.
* A posted `WM_LBUTTONDOWN` never generates `WM_MOUSEACTIVATE`, so the
  unactivated Aura window drops the click for lack of focus.
* **There is no Chromium switch that makes posted Win32 mouse messages
  trusted.** The research looked specifically for one and did not find it.
  `--enable-automation` does not bypass the `GetKeyState` checks.

### Layer 3 — Win32 message injection

`PostMessage(WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP)` with
`wParam = MK_LBUTTON` and `lParam = MAKELPARAM(x, y)`.

| Framework | Result |
|---|---|
| Classic Win32 (Notepad, standard controls) | **Works** |
| WinForms | Works for standard controls; breaks on anything reading `Control.MousePosition` (which queries `GetCursorPos`, not the message) |
| Qt 5/6 | Partial — global-coordinate mapping desyncs when the window is inactive; mouse-release fails without internal grab |
| Java Swing | Buttons work; `JTable`/`JTree` fail (lightweight hierarchy needs continuous hover) |
| **WPF** | **Breaks** — `HwndMouseInputProvider` / `InputManager` hit-tests against physical coordinates |
| **WinUI 3 / Windows App SDK** | **Fails entirely** — pointer input goes through the Windows Pointer subsystem (`WM_POINTERDOWN`), not `WM_LBUTTONDOWN`. `Button.Click` never fires |
| Chromium | Fails (Layer 2 above) |

Also: **cross-process `SetCapture` is impossible.** `SetCapture` is
thread-affine; calling it on another process's window requires
`AttachThreadInput`, and Microsoft explicitly warns against that in modern
apps (deadlock risk if the target enters a modal loop).

### Layer 4 — Hardware warp + `SendInput`

The universal fallback. `SetCursorPos` to the target's screen coords, then
`SendInput` with `MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`, normalised
against the **virtual desktop** bounding box (`SM_XVIRTUALSCREEN` etc.), not
against the primary monitor.

It works for *everything* — WPF, WinUI 3, games, design tools — because it is
the same path as a physical mouse.

**The warp-and-restore trap, precisely:** Win32's input queue is
**asynchronous**. `SendInput` returns before the target thread has processed
the click. If you restore the cursor immediately, the target sees the click
*after* the cursor is already back on `DISPLAY1`, calls `GetCursorPos()`, sees
the wrong monitor, and discards it. And during a **drag**, restoring the
cursor terminates the drag or drags the content back to the origin.

The correct pattern is to restore **after a delay** (≥1 frame), and never
restore during an active drag.

### What is genuinely impossible

**Real-time 3D games with mouse-look.** Not a limitation of our code — a
limitation of Windows:

* Mouse-look engines read **relative** deltas `(dx, dy)` and re-centre the OS
  cursor every frame via `SetCursorPos(screenCenter)`.
* `WM_INPUT` cannot be forged from outside: `lParam` is an opaque kernel
  `HRAWINPUT` handle, and `GetRawInputData` on a fake one fails with
  `ERROR_INVALID_HANDLE`.
* `SendInput` *does* generate raw input for other processes, but with
  `hDevice == NULL` (software injection) — and games plus anti-cheat discard
  those.
* Streaming products (Parsec, Sunshine/Moonlight, Steam Link) all **take over
  the foreground**. They do not drive a game in the background.

So: a 3D game *cannot* be driven from the glasses while the user keeps working
on the desktop. Not with a driver either.

---

## 3. Two mechanisms we should reject outright

### Virtual HID mouse driver — no

The research verified the mechanism: `mouclass.sys` multiplexes **all** mouse
devices — physical USB, Bluetooth, VHF virtual — into the single system
cursor. **A virtual mouse cannot create a second cursor.** It buys nothing
over `SendInput`, and costs an EV certificate ($500+/yr), Microsoft Hardware
Dev Center attestation, Secure Boot friction, and anti-cheat bans.

### `ClipCursor` confinement — dangerous with this hardware

Confining the OS cursor to the Rokid display's bounds is tempting: it would
keep the arrow off the physical screens and make the warp invisible. But:

* `ClipCursor` is a **hard barrier** — the user cannot push past the edge.
  Returning to the XP-Pen requires a low-level hook watching for a breakout
  gesture. Clunky.
* **It breaks the XP-Pen.** The Artist 13.3 is an *absolute* digitiser mapped
  to `\\.\DISPLAY2`. While the cursor is clipped to `\\.\DISPLAY5`, the kernel
  **clamps the pen's absolute coordinates to the nearest edge** of the clip
  rect. The user literally cannot draw.
* It resets automatically on desktop switches (UAC prompt, `Win+L`), so any
  release logic must handle that.

Verdict: **do not clip the cursor.**

---

## 4. The recommended architecture

```
              [Click at panel UV (u,v)]
                         │
                         ▼
            [UV → target window client px]
                         │
                         ▼
          [Identify target process + window class]
                         │
     ┌───────────────────┼───────────────────────┬────────────────────┐
     ▼                   ▼                       ▼                    ▼
 [Media?]           [IDE/Editor?]          [Chromium?]          [Legacy Win32
  → GSMTC          → CLI/HTTP IPC             → CDP             or WinForms?]
  TryToggle        code -r -g                 dispatch           → PostMessage
  PlayPause        :63342                     MouseEvent
     │                   │                       │                    │
     └────────► FAIL ────┴───────► FAIL ───────┴──── FAIL ──────────┘
                                     │
                                     ▼
                   [Layer 4: warp + SendInput]
                   save → warp → click → (delay) → restore
```

Each layer is tried in order; the first that acknowledges the action wins.
"Acknowledges" matters: if `PostMessage` produces no UI change, escalate to
the warp rather than leaving the user with a dead click.

### Sizing the work

| Layer | Effort | Value |
|---|---|---|
| 1a. GSMTC media control | ~0.5 day | **Solves the user's original "pause the video" complaint directly** |
| 1b. VS Code CLI IPC | ~1 day | Makes code editing genuinely usable |
| 2. CDP | ~3–4 days | Full browser interactivity |
| 3. Win32 `PostMessage` | ~1 day | Legacy app support |
| 4. Warp fallback | ~1 day | Universal, but costs the user their cursor |

---

## 5. Honest supportability matrix

| Target | Tier | Mechanism | Cursor-free? |
|---|---|---|---|
| Chrome / Edge / Brave (we launch, CDP) | **First-class** | CDP `Input.dispatchMouseEvent` | Yes |
| VS Code / Cursor | **First-class** | Named-pipe CLI IPC | Yes |
| JetBrains IDEs | **First-class** | HTTP `:63342` | Yes |
| Terminals (PS/CMD/WSL) | **First-class** | `AttachConsole` + `WriteConsoleInput` | Yes |
| Media (YouTube/Spotify/VLC) | **First-class** | WinRT GSMTC | Yes |
| Office (Excel/Word) | **First-class** | COM automation | Yes |
| Classic Win32 / WinForms | First-class | `PostMessage` | Yes |
| WPF / WinUI 3 / Qt / Electron (no CDP) | Best-effort | Warp + `SendInput` | No |
| Design tools (Photoshop, Blender) | Best-effort | Warp + `SendInput` | No |
| **Real-time 3D games (mouse-look)** | **Unsupported** | — | **Impossible** |

### The honest product promise

> **"Cursor-free AR interaction for the web, for code, for media and for
> terminals. Best-effort for arbitrary desktop windows. Real-time 3D games are
> out of scope — they need the glasses to themselves."**

For games the answer is a **dedicated mode**, not input injection: when the
user wants to game, suspend the pinning and hand the Rokid display to the
game exclusively. Alternatively map head motion to a **virtual gamepad**
(XInput), which games accept in the background without touching the cursor —
but that is a different feature, not a fix.

---

## 6. What this changes about the roadmap

M9's Stage 3 assumed CDP was *the* answer for "click the pinned content".
It is the answer *for browsers*, and it should be built — but the sequencing
changes:

1. **GSMTC first** (half a day). It is a first-party Windows API, it needs no
   browser relaunch, and it delivers the exact action the user named. If we
   ship only this, the "pause the video" case already works.
2. **VS Code IPC second** (~1 day). Directly answers "what about code?".
3. **CDP third** (~3–4 days). The heavy one, and browser-specific.
4. **`PostMessage` + warp fallback** (one day each) as the safety net.
5. **IddCx / virtual display** only if we must relocate apps we cannot
   cooperate with — and note the pen-display constraint from M8 still
   applies.

---

## 7. Sources

**Chromium source (primary):**
- [`legacy_render_widget_host_win.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/browser/renderer_host/legacy_render_widget_host_win.cc)
- [`ui/views/win/hwnd_message_handler.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/views/win/hwnd_message_handler.cc)
- [`ui/views/widget/desktop_aura/desktop_window_tree_host_win.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/views/widget/desktop_aura/desktop_window_tree_host_win.cc)
- [`content/browser/renderer_host/render_widget_host_impl.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/browser/renderer_host/render_widget_host_impl.cc)
- [`content/browser/devtools/devtools_pipe_handler.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/browser/devtools/devtools_pipe_handler.cc)
- [`remoting/host/input_injector_win.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/remoting/host/input_injector_win.cc) — Chrome Remote Desktop's own injector

**Microsoft Learn:**
- [`SendInput`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) · [`MOUSEINPUT`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)
- [`ClipCursor`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clipcursor)
- [About Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input)
- [`InputInjector`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.preview.injection.inputinjector)
- [`CoreWebView2CompositionController.SendMouseInput`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller.sendmouseinput)
- [`GlobalSystemMediaTransportControlsSessionManager`](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [`AttachConsole`](https://learn.microsoft.com/en-us/windows/console/attachconsole) · [`WriteConsoleInput`](https://learn.microsoft.com/en-us/windows/console/writeconsoleinput)
- [`RegisterDragDrop`](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop) · [`IDropTarget`](https://learn.microsoft.com/en-us/windows/win32/api/oleidl/nn-oleidl-idroptarget)
- [Mouse and Keyboard Class Drivers](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/keyboard-and-mouse-hid-client-drivers)
- [CDP Input domain](https://chromedevtools.github.io/devtools-protocol/tot/Input/)

**Repositories:**
- [microsoft/vscode](https://github.com/microsoft/vscode)
- [LizardByte/Sunshine](https://github.com/LizardByte/Sunshine)
- [nefarius/ViGEmBus](https://github.com/nefarius/ViGEmBus) — end-of-life, gamepad-only
