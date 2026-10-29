# Chrome Remote Desktop — Multi_Instance_CRD

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20ARM64%20%7C%20x64-blue.svg)](https://github.com/zaczzk/Multi_Instance_CRD)
[![Hardware](https://img.shields.io/badge/Hardware-Qualcomm%20Snapdragon%20X%20Series-orange.svg)](https://github.com/zaczzk/Multi_Instance_CRD)
[![Target](https://img.shields.io/badge/.NET-9.0--windows%20(Native%20ARM64)-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A high-performance, minimalist multi-instance Chrome Remote Desktop client compiled natively for **Windows 11 on ARM64** (Qualcomm Snapdragon X Plus `X1P42100` and Snapdragon X Elite) and **x64** systems.

Engineered for concurrent multi-host workflows (1–3 active sessions) with zero-margin edge-to-edge presentation, hardware-accelerated Direct3D 11 NV12 swapchain composition, native 16:10 (`1920×1200`) virtual EDID negotiation, and sub-35ms session switching.

---

## Capabilities

* **Zero-Margin Viewport**: Strips 70–110px of browser chrome, URL omniboxes, and OS borders for edge-to-edge remote rendering.
* **Aspect-Preserving Scaling**: Default Fit-to-Screen matrix scaling supports ultrawide (`21:9`, `32:9`) and 4K displays with proportional scaling and sharp font rendering.
* **OLED Pure Black Letterboxing**: Enforces `#000000` pitch-black margins across all container tiers, eliminating Google `#202124` / `#3c4043` grey letterbox power draw on OLED displays.
* **Native Wire-Level Resolution Sync (`Ctrl+Alt+N`)**: Injects `ClientResolution`, `VideoLayout`, and `VideoControl` protobuf messages over the WebRTC SCTP `"control"` data channel to enforce exact host-side raster dimensions (`1920×1200 @ 96 DPI` or `960×1200` per split pane).
* **Per-Monitor Raster Topology**: Automatically probes physical raster dimensions (`rcMonitor`) and Per-Monitor V2 DPI via `MonitorFromWindow`, adapting to external 4K monitors and laptop panels.
* **Elastic Dynamic HUD**: Auto-measuring top dock hosted in an independent Win32 owned layered window (`WS_EX_LAYERED`), bypassing WPF `WebView2` airspace occlusion.
* **0ms UI Session Switching**: Swaps active session surfaces synchronously on the UI thread; delegates working-set compaction and thumbnail encoding to non-blocking background threads with sequence cancellation protection.
* **Remote Display Routing (`Ctrl+Alt+O`)**: Cycles multi-monitor remote hosts across Display 1, Display 2, or Spanned All via Chromoting `SelectDesktopDisplayRequest` protobufs.
* **Snapdragon Oryon MMCSS & Windows 11 EcoQoS**: Promotes active UI and render threads via `AvSetMmThreadCharacteristicsW("Games")`; throttles background sessions via `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` and `EmptyWorkingSet` memory compaction. Automatically drops priority to idle during tray standby.
* **Win32 `WM_INPUT` Raw Hardware Mouse Engine (`Ctrl+Alt+M`)**: Unaccelerated relative cursor delta capture with automatic midpoint reset and SCTP `"event"` channel `MouseEvent` streaming for 3D viewports (CAD, Blender, Unreal Engine).
* **USB HID SAS Dispatch (`⌨`)**: Dispatches `Ctrl+Alt+Del` (Secure Attention Sequence), `Win+L`, `Win+D`, and `Ctrl+Shift+Esc` via USB HID Usage Page `0x07` keyboard descriptors without triggering local OS handlers.
* **Smart App Control Execution Bypass**: Ships with `Run-CRD.cmd` launcher executing via Microsoft-signed `dotnet.exe`, preventing Smart App Control and Windows Defender untrusted-binary execution blocks.

---

## Top Notch Dynamic HUD

```text
               Top Edge of Display (0px Margin Baseline / Edge-to-Edge)
────────────────────────────────────────────────────────────────────────────────────
 [ Remote Tabs ]            [ Top Notch Dynamic HUD (Auto-Sized) ]    [ Controls ]
┌──────────────┬──────────────────────────────────────────┬────────────────────────┐
│  16ms  60f   │ [1: Workstation*] [2: Home Lab] [3: VM]  │ Mon  Split  Fit  SAS X │
└──────────────┴──────────────────────────────────────────┴────────────────────────┘
```

The Top Notch floats at the top-center of the screen. It auto-hides when the cursor leaves the trigger zone, or remains anchored when pinned (`📌` / `Ctrl+Alt+S`):

* **Telemetry Badge**: Real-time RTT and decoded FPS sampled every 1500ms via WebRTC stats:
  * 🟢 **Optimal**: RTT < 35ms, $\ge$ 30 FPS
  * 🟡 **Moderate**: RTT 35–80ms
  * 🔴 **Degraded**: RTT > 80ms
* **Session Selectors (1–3)**: Click to switch active session surface; hover to inspect live JPEG thumbnail preview.
* **Display Router (`🖥 Mon` / `Ctrl+Alt+O`)**: Cycles remote outputs: `Display 1` $\rightarrow$ `Display 2` $\rightarrow$ `All Displays`.
* **Cockpit Dual Split (`◫` / `Ctrl+Alt+C`)**: Dual-session 50/50 side-by-side split (`960×1200` per pane, 8:10 portrait) with isolated audio and power pacing.
* **Relative Mouse Lock (`🔒` / `Ctrl+Alt+M`)**: Confineless raw hardware delta capture (`WM_INPUT`). Release via double-tap `Esc` or `Ctrl+Alt+Home`.
* **Viewport Scaling (`⛶ Fit` / `Ctrl+Alt+D`)**:
  * `Fit to Screen` *(Default)*: Proportional aspect-preserving scaling with OLED black letterboxing.
  * `Stretch`: Full-bleed stretch across panel aspect ratio.
  * `Zoom`: Center-cropped 1:1 viewport fill.
* **Resolution Sync (`16:10` / `Ctrl+Alt+N`)**: Negotiates macroblock-aligned `1920×1200` raster from host `remoting_me2me_host`.
* **System Keys (`⌨`)**: Injects `Ctrl+Alt+Del`, `Win+L`, `Win+D`, and `Ctrl+Shift+Esc` via USB HID scan codes.
* **Dock Pin (`📌` / `Ctrl+Alt+S`)**: Toggles dock auto-hide behavior (10px hover trigger vs. persistent pinned overlay).

---

## Keyboard Shortcuts

All `Ctrl+Alt+*` hotkeys are intercepted at the OS level via a scoped low-level keyboard hook (`WH_KEYBOARD_LL`), preventing browser `navigator.keyboard.lock()` capture:

| Shortcut | Action | Scope |
| :--- | :--- | :--- |
| `Ctrl + Alt + R` | Global toggle: minimize to / restore from System Tray | Global OS |
| `Ctrl + Alt + [1..3]` | Direct switch to Session 1, 2, or 3 | Focused |
| `Ctrl + Alt + Tab` | Cycle to next active session | Focused |
| `Ctrl + Alt + O` | Cycle remote display: Display 1 $\rightarrow$ Display 2 $\rightarrow$ All | Focused |
| `Ctrl + Alt + D` | Cycle viewport mode: `Fit to Screen` $\rightarrow$ `Stretch` $\rightarrow$ `Zoom` | Focused |
| `Ctrl + Alt + N` | Enforce native 16:10 resolution (`1920×1200 @ 96 DPI`) | Focused |
| `Ctrl + Alt + C` | Toggle Cockpit 50/50 Dual Split (`960×1200` per pane) | Focused |
| `Ctrl + Alt + M` | Toggle raw relative mouse capture (`WM_INPUT`) | Focused |
| `Ctrl + Alt + S` | Toggle HUD dock pin / auto-hide state | Focused |
| `Ctrl + Alt + Home` | Pointer escape: release relative mouse capture and lock | Focused |
| `Esc Esc` | Fast pointer escape (double-tap) | Focused |
| `F11` | Toggle borderless fullscreen | Focused |

---

## Architecture & Subsystems

```text
src/CRDLightweightARM/
├── Hardware/
│   ├── SnapdragonOryonGovernor.cs     # MMCSS thread priority & EcoQoS process management
│   ├── RawInputMouseEngine.cs         # Win32 WM_INPUT mouse delta capture & cursor confinement
│   └── DisplayTopologyDetector.cs     # Panel raster probe (1920×1200 16:10 @ 96 DPI) & profile math
├── NativeProtocol/
│   ├── ChromotingProtobuf.cs          # Zero-dependency protobuf codecs (VideoLayout, ClientResolution, HID)
│   ├── Spake2Authenticator.cs         # SPAKE2 Ed25519 authentication & DTLS transcript hashing
│   └── D3D11AdrenoPresenter.cs        # Direct3D 11.1 / DXGI 1.6 zero-copy NV12 swapchain presenter
├── crd-mediator.js                    # Injected WebRTC SCTP channel interceptor, fit engine & watchdog
├── MainWindow.xaml / .cs              # Owned layered HUD host, low-level keyboard hook, session lifecycle
├── SessionModel.cs                    # Session schema definition & JSON persistence
├── PowerGovernor.cs                   # AC / Battery power source state observer
└── TrayIconHelper.cs                  # Windows 11 System Tray integration with EcoQoS standby
```

### 1. Zero-DOM Chromoting Protocol Stack (`NativeProtocol/`)
* **[`ChromotingProtobuf.cs`](src/CRDLightweightARM/NativeProtocol/ChromotingProtobuf.cs)**: Zero-allocation binary encoder/decoder for Chromoting wire-format structures:
  * `ClientResolution` (Tag 2): Viewport width, height, and explicit DIPs calculation.
  * `VideoLayout` & `VideoTrackLayout` (Tag 10): Host virtual EDID configuration and multi-monitor topologies.
  * `VideoControl` (Tag 4): 60 FPS target framerate negotiation and lossless YUV444 encoding requests.
  * `SelectDesktopDisplayRequest` (Tag 11): Remote display output routing.
  * `EventMessage`, `KeyEvent`, `MouseEvent`: USB HID Usage Page `0x07` key codes and relative cursor deltas.
* **[`Spake2Authenticator.cs`](src/CRDLightweightARM/NativeProtocol/Spake2Authenticator.cs)**: SPAKE2 password-authenticated key exchange over Curve25519/Ed25519 compliant with BoringSSL specifications. Derives key material via `HMAC-SHA256(host_id, pin)` with 64-bit LE length transcript framing and DTLS certificate-bound `CalculateVerificationHash`.
* **[`D3D11AdrenoPresenter.cs`](src/CRDLightweightARM/NativeProtocol/D3D11AdrenoPresenter.cs)**: Direct3D 11.1 and DXGI 1.6 Flip-Model presentation engine (`DXGI_SWAP_EFFECT_FLIP_DISCARD`, 1-frame waitable latency, `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`, and `ID3D11VideoContext::VideoProcessorBlt` zero-copy `DXGI_FORMAT_NV12` composition optimized for Qualcomm Adreno X1-45 GPUs).

### 2. Snapdragon X Hardware Governor (`Hardware/`)
* **[`SnapdragonOryonGovernor.cs`](src/CRDLightweightARM/Hardware/SnapdragonOryonGovernor.cs)**: Hardware topology management for Qualcomm Snapdragon X Series (8-core Oryon CPU and Adreno GPU):
  * Promotes UI and presentation threads to Multimedia Class Scheduler Service (`AvSetMmThreadCharacteristicsW("Games")` + `AVRT_PRIORITY_HIGH`), disabling thread execution speed throttling.
  * Applies Windows 11 `EcoQoS` (`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) and executes `EmptyWorkingSet` sweeps on inactive session process trees, reclaiming up to 400 MB RAM per background host.
* **[`RawInputMouseEngine.cs`](src/CRDLightweightARM/Hardware/RawInputMouseEngine.cs)**: Win32 `WM_INPUT` unaccelerated hardware delta listener (`RegisterRawInputDevices` with `RIDEV_INPUTSINK`). Re-centers cursor to viewport midpoint and streams serialized `delta_x` / `delta_y` protobuf packets over WebRTC SCTP.
* **[`DisplayTopologyDetector.cs`](src/CRDLightweightARM/Hardware/DisplayTopologyDetector.cs)**: Probes native display raster metrics (`DESKTOPHORZRES` = 1920, `DESKTOPVERTRES` = 1200, 60 Hz) bypassing DPI virtualization, calculating macroblock-aligned geometries for Full-Bleed 16:10 and Cockpit 50/50 (8:10) configurations.

### 3. Owned Transparent Layered HUD Window Architecture
* **Win32 Airspace Isolation**: WPF `<wv2:WebView2>` wraps an external Win32 `HwndHost` (`msedgewebview2.exe`). Under Win32 window hierarchies, child `HWND`s paint over WPF vector trees within the same window (Airspace limitation).
* **Layered Host Window**: Dock controls, flyouts, and hover triggers reside in an independent owned transparent layered window (`WS_EX_LAYERED`, `AllowsTransparency = true`, `Background = null`). Transparent regions (`Alpha = 0`) yield hit-testing to the underlying `WebView2` instance, while dock elements retain full input capture. Window bounds and z-order synchronize with `MainWindow` lifecycle events.

---

## Getting Started

### Prerequisites
* **Operating System**: Windows 11 ARM64 (Qualcomm Snapdragon X Series) or Windows 10/11 x64.
* **Runtime**: .NET 9.0 SDK or Desktop Runtime.
* **Browser Subsystem**: Microsoft Edge WebView2 Runtime (inbox on Windows 11).

### One-Click Launch (Smart App Control Bypass)
To launch the client without unsigned executable verification warnings, execute the batch runner:

```cmd
Run-CRD.cmd
```

*Executes the compiled assembly via host-signed `dotnet.exe` to satisfy Windows 11 Smart App Control policies without certificate signing requirements.*

### Build from Source

```cmd
:: Build and launch native ARM64 release via CMD
build.cmd -Run

:: Or via PowerShell with bypass:
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

```powershell
# 1. Run automated 5-stage hardware and protocol verification
.\build.ps1
.\src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.exe --self-test

# 2. Publish standalone single-file binary for ARM64
.\build.ps1 -Arch arm64 -PublishStandalone

# 3. Publish dual-architecture distribution (ARM64 + x64) to ./dist/
.\build.ps1 -PublishAll
```

---

## Hardware & Protocol Self-Test

Validate hardware acceleration and protocol compatibility on the target host:

```powershell
.\src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.exe --self-test
```

```text
[CRD-ARM Self-Test] Stage 1/5: Snapdragon Oryon Governor Probe...
   Detected: Snapdragon X Plus (8C Oryon) • Adreno X1-45
   MMCSS Thread Promotion: OK
[CRD-ARM Self-Test] Stage 2/5: Display Topology Detector...
   Physical Display: 1920x1200 (16:10) @ 60Hz, 96 DPI
[CRD-ARM Self-Test] Stage 3/5: Chromoting Protobuf Wire Formats...
   ControlMessage, VideoLayout, ClientResolution, EventMessage: OK
[CRD-ARM Self-Test] Stage 4/5: SPAKE2 Curve25519 Authenticator...
   Key Derivation, Exchange & DTLS Verification Hash: OK
[CRD-ARM Self-Test] Stage 5/5: Direct3D 11 Adreno SwapChain Presenter...
   Adreno X1-45 D3D11.1 Flip-Discard NV12 SwapChain: OK
[CRD-ARM Self-Test] ALL 5 STAGES PASSED (ExitCode: 0)
```

---

## Configuration (`%LOCALAPPDATA%\CRDLightweightARM\config.json`)

The client initializes and maintains a persistent configuration profile:

```json
{
  "EfficiencyMode": "UltraWarm",
  "NotchDwellMs": 220,
  "EnforceNativeDpi": true,
  "AutoSyncNativeResolution": false,
  "LosslessColorMode": false,
  "StartFullscreen": true,
  "MinimizeToTrayOnClose": true,
  "GlobalHotkeyEnabled": true,
  "Sessions": [
    { "Index": 1, "Name": "Workstation", "Url": "https://remotedesktop.google.com/access" },
    { "Index": 2, "Name": "Home Lab", "Url": "https://remotedesktop.google.com/access" },
    { "Index": 3, "Name": "Cloud VM", "Url": "https://remotedesktop.google.com/access" }
  ]
}
```

### Schema

| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `EfficiencyMode` | string | `"UltraWarm"` | Background power pacing: `UltraWarm` (<35ms resume), `EcoWarm` (EcoQoS + RAM trim), `LiveHot` (60 FPS unthrottled decode). |
| `NotchDwellMs` | int | `220` | Mouse hover dwell threshold (ms) required to deploy the Top Notch HUD. |
| `EnforceNativeDpi` | bool | `true` | Forces 96 DPI (1:1 DIP mapping) in host resolution negotiation. |
| `AutoSyncNativeResolution` | bool | `false` | Automatically pushes client raster (`1920×1200`) on session connect. Keep `false` for ultrawide/4K hosts; trigger manually via `Ctrl+Alt+N`. |
| `LosslessColorMode` | bool | `false` | Requests YUV444 lossless chroma profile over WebRTC video track. |
| `StartFullscreen` | bool | `true` | Launches in borderless fullscreen mode. |
| `MinimizeToTrayOnClose` | bool | `true` | Intercepts window close to minimize process into system tray. |
| `GlobalHotkeyEnabled` | bool | `true` | Registers system-wide `Ctrl+Alt+R` hotkey for tray summon/hide. |

---

## License

MIT License. Copyright (c) 2026 Zac and Contributors.
