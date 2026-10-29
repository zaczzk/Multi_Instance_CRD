# Chrome Remote Desktop - Lightweight ARM (CRD-ARM)

A minimalist, high-performance **Chrome Remote Desktop client** built natively for **Windows 11 on ARM64 (Qualcomm Snapdragon X Plus & Elite)**.

Designed for users who actively juggle **1 to 3 remote desktop sessions** and want **zero wasted screen space**, **native 16:10 (`1920×1200`) / 3:2 host resolution negotiation via Chromoting `VideoLayout` Protobufs**, instantaneous session jumping, and near-zero background battery drain.

---

## Hardware-Specific Optimizations (Snapdragon X Plus `X1P42100` & Adreno `X1-45`)

| Standard Browser Behavior | CRD-ARM Solution |
| :--- | :--- |
| **70–110px Wasted Space** (Tabs, URL bar, bookmarks bar, window chrome) | **0px Wasted**: 100% full-bleed remote display canvas from edge to edge. |
| **16:9 1080p Letterbox Bars & Scaling Blur** on 16:10 (`1920×1200`) panels | **Native `VideoLayout` Protobuf Sync (`Ctrl+Alt+N` / `16:10` Pill)**: Injects binary `ClientResolution`, `VideoLayout`, and `VideoControl` protobufs directly into the WebRTC `"control"` SCTP DataChannel to lock the remote host to exact **`1920×1200 @ 96 DPI`** (or **`960×1200`** per pane in 50/50 Split). |
| **Heavy ARM64 Battery Drain** (Multiple tabs decoding 60fps in background) | **0% Background GPU Decode + Windows 11 EcoQoS**: Detaches video sinks, applies `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` (`EcoQoS`), and compacts RAM via `EmptyWorkingSet` on standby sessions. |
| **UI / Input Thread Migration Jitter** on 8-core Oryon CPUs | **MMCSS Real-Time Thread Pinning**: Pins the main UI/Input/SwapChain thread via `AvSetMmThreadCharacteristicsW("Games")` (`AVRT_PRIORITY_HIGH`) and disables thread execution speed throttling. |
| **Friction Switching Between 1–3 Hosts** | **Sub-35ms Warm Swap**: Instant hotkeys (`Ctrl+Alt+1..3`) and auto-hiding/pinned top notch. |
| **Juggling Two Machines at Once** | **Cockpit 50/50 Dual Mode**: View two machines side-by-side (`960×1200` 8:10 native split per pane) with asymmetric power pacing. |
| **3D CAD & Blender Viewports Colliding with Screen Edge** | **Win32 `WM_INPUT` Raw Mouse Engine**: Captures unaccelerated hardware mouse deltas (`RegisterRawInputDevices`) with automatic window-midpoint re-centering and direct SCTP `"event"` DataChannel `MouseEvent` protobuf injection. |
| **Accidental Local Key Triggers (`Ctrl+Alt+Del`, `Win+L`)** | **USB HID Usage Page `0x07` Protobuf + CDP Flyout**: Injects binary `KeyEvent` (`0x070000 | usage`) protobufs directly over the `"event"` SCTP DataChannel without locking your local laptop. |
| **Direct3D 11 / DXGI Adreno X1-45 Pipeline** | **Zero-Copy NV12 & MPO Overlay Ready**: Includes native `D3D11AdrenoPresenter` (`DXGI_SWAP_EFFECT_FLIP_DISCARD` + 1-frame waitable object + `ID3D11VideoContext::VideoProcessorBlt`) and DirectComposition hardware video overlays. |

---

## The Dynamic Island Top Notch (528px HUD)

```text
       Top Edge of Snapdragon Display (0px Wasted Baseline)
────────────────────────────────────────────────────────────────────────────────────
[ Remote Tabs ]        [ The Top Notch: Center 30% ]              [ Remote Min/Close ]
  ┌────────────────────────────────────────────────────────────────────────────────┐
  │ ● 16ms 60f │ [● Workstation]  [◐ Home Lab]  [○ Cloud] │ ◫ 🔒 ⛶ 16:10 ⌨ 📌 ⚙ ✕ │
  └────────────────────────────────────────────────────────────────────────────────┘
```

* **Live Telemetry Badge**: Real-time WebRTC round-trip time (RTT), frame rate, and decoded stream resolution (`1920×1200`) directly from the active connection.
  * 🟢 **Green**: Latency < 35ms & $\ge$ 30 FPS (Smooth connection).
  * 🟡 **Amber**: Latency 35–80ms (Moderate latency).
  * 🔴 **Red**: Latency > 80ms (High latency / Jitter).
* **Cockpit 50/50 Split (`◫`)**:
  * Toggles dual side-by-side view of Session 1 and Session 2.
  * Automatically negotiates **`960×1200 @ 96 DPI` (8:10)** per pane via `VideoLayout` protobufs so neither machine has horizontal scrollbars or letterbox bars.
* **Win32 Raw Input Relative Mouse Mode (`🔒`)**:
  * Captures hardware `WM_INPUT` mouse deltas (`RawInputMouseEngine.cs`) and streams binary `MouseEvent` (`delta_x`, `delta_y`) protobufs over the `"event"` DataChannel for 3D modeling (Blender, Maya, CAD) and gaming.
  * Double-tap `Esc` or press `Ctrl + Alt + Home` immediately breaks cursor lock and returns pointer control.
* **Native `16:10` Resolution Negotiator (`16:10` / `Ctrl+Alt+N`)**:
  * Sends macroblock-aligned `ClientResolution`, `VideoLayout`, and `VideoControl` protobuf messages directly over the WebRTC `"control"` SCTP DataChannel.
* **Zero-Bar Display Switcher (`⛶ Fit`)**:
  * `⛶ Fit`: Preserves aspect ratio against pitch-black OLED background.
  * `↔ Stretch`: Fills 100% of vertical and horizontal display, eliminating all bars.
  * `🗖 Zoom`: Centers and scales video to eliminate letterbox margins without distortion.
* **Remote System Keys (`⌨`)**:
  * `Ctrl + Alt + Del`: Dispatches Secure Attention Sequence (SAS) via USB HID `KeyEvent` (`0x0700E0` + `0x0700E2` + `0x07004C`) + CDP.
  * `Win + L`: Locks remote machine (`0x0700E3` + `0x07000F`) without locking your local Snapdragon laptop.
  * `Win + D`: Minimizes all remote windows (`0x0700E3` + `0x070007`) to show remote desktop.
  * `Ctrl + Shift + Esc`: Launches Task Manager (`0x0700E0` + `0x0700E1` + `0x070029`) on remote host.

---

## Architecture Overview (`src/CRDLightweightARM/`)

* **`NativeProtocol/`**:
  * [`ChromotingProtobuf.cs`](src/CRDLightweightARM/NativeProtocol/ChromotingProtobuf.cs): Zero-dependency wire-format encoder/decoder for `ControlMessage`, `ClientResolution`, `VideoLayout`, `VideoTrackLayout`, `VideoControl`, `EventMessage`, `KeyEvent`, `MouseEvent`, and `UsbHidKeycodeMapper`.
  * [`Spake2Authenticator.cs`](src/CRDLightweightARM/NativeProtocol/Spake2Authenticator.cs): Chromium-compatible SPAKE2 over Ed25519/Curve25519 authenticator (`HMAC-SHA256` PIN secret derivation, 64-bit LE transcript formatting, and DTLS certificate-bound `CalculateVerificationHash`).
  * [`D3D11AdrenoPresenter.cs`](src/CRDLightweightARM/NativeProtocol/D3D11AdrenoPresenter.cs): Direct3D 11.1 + DXGI 1.6 Flip-Model swapchain presenter (`SetMaximumFrameLatency(1)`, `DXGI_SWAP_EFFECT_FLIP_DISCARD`, `ID3D11VideoContext::VideoProcessorBlt` zero-copy NV12 presentation on Qualcomm Adreno GPUs).
* **`Hardware/`**:
  * [`SnapdragonOryonGovernor.cs`](src/CRDLightweightARM/Hardware/SnapdragonOryonGovernor.cs): Detects Snapdragon X Oryon CPU & Adreno GPU, promotes the UI/Input thread via MMCSS (`AvSetMmThreadCharacteristicsW("Games")`), and applies Windows 11 `EcoQoS` + `EmptyWorkingSet` to background session process trees.
  * [`RawInputMouseEngine.cs`](src/CRDLightweightARM/Hardware/RawInputMouseEngine.cs): 64-bit ARM64 Win32 `WM_INPUT` hardware mouse delta engine with automatic window-midpoint cursor re-centering.
  * [`DisplayTopologyDetector.cs`](src/CRDLightweightARM/Hardware/DisplayTopologyDetector.cs): Queries real physical raster metrics (`DESKTOPHORZRES` / `DESKTOPVERTRES`) and Per-Monitor V2 DPI to compute Full-Bleed `1920×1200` (16:10) and Cockpit Split `960×1200` (8:10) target profiles.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **`Ctrl + Alt + R`** | **Global Summon**: Instant hide to / restore from System Tray (from anywhere in Windows) |
| **`Ctrl + Alt + [1..3]`** | Instant jump to **Session 1, 2, or 3** |
| **`Ctrl + Alt + Tab`** | Cycle forward to the next session |
| **`Ctrl + Alt + N`** | **Native 16:10 Sync**: Negotiate `1920×1200` (or `960×1200` in split) via `VideoLayout` Protobuf |
| **`Ctrl + Alt + C`** | Toggle **Cockpit 50/50 Dual View** |
| **`Ctrl + Alt + M`** | Toggle **Relative Mouse Mode** (Win32 `WM_INPUT` Raw Hardware Deltas) |
| **`Ctrl + Alt + D`** | Cycle Display Mode (`Fit` $\rightarrow$ `Stretch` $\rightarrow$ `Zoom`) |
| **`Ctrl + Alt + S`** | Summon or dismiss the Top Notch Dock |
| **`Ctrl + Alt + Home`** | Escape hatch: Release mouse cursor trap / pointer lock |
| **`F11`** | Toggle borderless fullscreen mode |

---

## Building & Running Hardware Self-Test

```powershell
# Build and run native ARM64 directly on your Snapdragon laptop
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run

# Run the automated 5-stage Snapdragon Hardware & Chromoting Protocol Self-Test
.\src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.exe --self-test

# Publish standalone single-file ARM64 executable
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Arch arm64 -PublishStandalone

# Publish both ARM64 and x64 releases at once into ./dist/
powershell -ExecutionPolicy Bypass -File .\build.ps1 -PublishAll
```

---

## License
MIT License.
