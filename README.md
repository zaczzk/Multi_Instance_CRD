# Chrome Remote Desktop - Lightweight ARM (CRD-ARM)

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20ARM64%20%7C%20x64-blue.svg)](https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm)
[![Hardware](https://img.shields.io/badge/Optimized%20For-Qualcomm%20Snapdragon%20X%20Plus%20%26%20Elite-orange.svg)](https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm)
[![Target](https://img.shields.io/badge/.NET-9.0--windows%20(Native%20ARM64)-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A minimalist, high-performance **Chrome Remote Desktop client** purpose-built for **Windows 11 on ARM64** (Qualcomm Snapdragon X Plus `X1P42100` and Snapdragon X Elite) and modern x64 laptops.

Engineered for power users who juggle **1 to 3 remote desktop hosts** simultaneously and demand **0px wasted screen space**, **native 16:10 (`1920×1200`) virtual EDID negotiation**, **zero-copy Adreno GPU presentation**, sub-35ms session switching, and near-zero standby battery drain.

---

## ⚡ Key Highlights & What Sets CRD-ARM Apart

* **0px Wasted Pixels**: Strips 70–110px of standard browser tab bars, URL omniboxes, bookmarks, and window borders for edge-to-edge full-bleed remote rendering.
* **Smart Ultrawide & High-DPI Fit**: Defaults to **Fit to Screen** with native CRD matrix scaling preserved—remote ultrawide (`21:9` / `32:9`) and 4K displays render proportionally with crystal-clear text and zero oversized window distortion.
* **OLED Pitch-Black Zero-Waste Letterboxing**: Strips Google's `#202124` / `#3c4043` grey margins across all container tiers, enforcing true `#000000` pitch black for maximum OLED battery savings.
* **Native Chromoting Protobuf Resolution Sync (`Ctrl+Alt+N`)**: Direct wire-format injection of `ClientResolution`, `VideoLayout`, and `VideoControl` protobufs into the WebRTC SCTP `"control"` channel to request native `1920×1200 @ 96 DPI` (or `960×1200` per pane) directly from the host.
* **Dynamic Island HUD (Elastic Top Notch)**: Fluid, auto-measuring top bar that adapts its width to session names and controls without clipping, hosted in a dedicated owned transparent layered window to completely eliminate the WPF `WebView2` Win32 Airspace bug.
* **Instantaneous 0ms Visual Swap**: Hotkey or click switching between Sessions 1, 2, and 3 is immediate on the UI thread, delegating RAM trimming and thumbnail capture to non-blocking background workers.
* **Remote Monitor Switcher (`🖥 Mon` / `Ctrl+Alt+O`)**: Easily toggle between Monitor 1, Monitor 2, and All Displays on multi-monitor remote hosts via Chromoting `SelectDesktopDisplayRequest` protobufs.
* **Snapdragon Oryon MMCSS & Windows 11 EcoQoS**: Main UI and swapchain thread is promoted via `AvSetMmThreadCharacteristicsW("Games")`, while inactive sessions are power-gated via `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` (`EcoQoS`) and `EmptyWorkingSet` memory compaction.
* **Win32 `WM_INPUT` Raw Hardware Mouse Engine (`🔒` / `Ctrl+Alt+M`)**: Unaccelerated relative mouse delta capture with automatic cursor recentering and SCTP `"event"` DataChannel `MouseEvent` injection for 3D CAD, Blender, and games.
* **USB HID Secure Attention Sequence Flyout (`⌨`)**: Dispatches `Ctrl+Alt+Del` (SAS), `Win+L`, `Win+D`, and `Ctrl+Shift+Esc` via USB HID Usage Page `0x07` protobufs without locking your local laptop.
* **Smart App Control Bypass (`Run-CRD.cmd`)**: Includes an automated launcher that executes through Microsoft-signed `dotnet.exe` to guarantee zero Windows Defender / Smart App Control execution blocks.

---

## 🖥️ Top Notch Dynamic HUD (Elastic Island)

```text
       Top Edge of Display (0px Wasted Baseline)
────────────────────────────────────────────────────────────────────────────────────
[ Remote Tabs ]               [ Top Notch HUD (Dynamic Width) ]       [ Remote Min/Close ]
  ┌──────────────────────────────────────────────────────────────────────────────────────┐
  │ ● 16ms 60f │ [● Workstation] [◐ Lab Server] [○ Cloud] │ 🖥 Mon ◫ 🔒 ⛶ Fit 16:10 ⌨ 📌 ⚙ ✕ │
  └──────────────────────────────────────────────────────────────────────────────────────┘
```

The Top Notch floats at the top-center of your screen. It automatically auto-hides when mouse cursor leaves the area (or remains pinned with `📌` / `Ctrl+Alt+S`):

1. **Live WebRTC Telemetry Badge**:
   * Displays real-time round-trip latency (RTT) and decoded frames-per-second (FPS) sampled every 1500ms without waking Oryon CPU performance cores.
   * 🟢 **Green**: Latency < 35ms & $\ge$ 30 FPS (Fluid connection).
   * 🟡 **Amber**: Latency 35–80ms (Moderate latency).
   * 🔴 **Red**: Latency > 80ms (High latency / Jitter).
2. **Session Slots (1, 2, and 3)**:
   * **Click-to-Switch**: Pressing any session button instantly swaps to that session.
   * **Hover Thumbnail Peek**: Hovering over an inactive session opens a floating JPEG preview window. Clicking the preview window directly switches straight to that session.
3. **Remote Monitor Switcher (`🖥 Mon` / `Ctrl+Alt+O`)**:
   * Cycles multi-monitor remote hosts between **Display 1**, **Display 2**, and **All Displays** using Chromoting `SelectDesktopDisplayRequest` protobufs.
4. **Cockpit 50/50 Dual Split (`◫` / `Ctrl+Alt+C`)**:
   * Displays two remote machines side-by-side in native vertical 8:10 format (`960×1200` per pane) with asymmetric audio and power pacing.
5. **Relative Mouse Lock (`🔒` / `Ctrl+Alt+M`)**:
   * Captures raw hardware `WM_INPUT` mouse deltas for 3D viewport navigation in Blender, Maya, Unreal Engine, and CAD. Double-tap `Esc` or press `Ctrl+Alt+Home` to release cursor confinement.
6. **Display Fit Selector (`⛶ Fit to Screen` / `Ctrl+Alt+D`)**:
   * **`⛶ Fit to Screen`** *(Default)*: Preserves native aspect ratio with CRD's native scaling matrix intact against pitch-black OLED bars. Perfect for ultrawide (`21:9`) and 4K hosts.
   * **`↔ Stretch`**: Expands 100% full-bleed across 16:10, eliminating all top/bottom bars.
   * **`🗖 Zoom`**: Centers and crops viewport to fill the screen without aspect distortion.
7. **Native 16:10 Host Sync (`16:10` / `Ctrl+Alt+N`)**:
   * Injects macroblock-aligned `ClientResolution` and `VideoLayout` protobufs into Google's `remoting_me2me_host` service to lock the remote display to exact `1920×1200` (16:10).
8. **Remote System Keys Flyout (`⌨`)**:
   * Dispatches `Ctrl+Alt+Del`, `Win+L`, `Win+D`, and `Ctrl+Shift+Esc` via USB HID Usage Page `0x07` keyboard event protobufs.
9. **Pin / Auto-Hide (`📌` / `Ctrl+Alt+S`)**:
   * Locks the top notch open or allows it to smoothly slide up into a 10px hover trigger.

---

## ⌨️ Global & In-App Keyboard Shortcuts

All `Ctrl+Alt+*` shortcuts are intercepted at the OS level via a scoped Win32 Low-Level Keyboard Hook (`WH_KEYBOARD_LL`), ensuring Chrome Remote Desktop's `navigator.keyboard.lock()` cannot swallow them:

| Hotkey | Action | Scope |
| :--- | :--- | :--- |
| **`Ctrl + Alt + R`** | **Global Summon**: Instant hide to / restore from System Tray | Global (Anywhere in Windows) |
| **`Ctrl + Alt + [1..3]`** | Instant jump to **Session 1, 2, or 3** | In-App / Focused |
| **`Ctrl + Alt + Tab`** | Cycle to the next active session | In-App / Focused |
| **`Ctrl + Alt + O`** | **Cycle Remote Monitor**: Switch Display 1 $\rightarrow$ 2 $\rightarrow$ All | In-App / Focused |
| **`Ctrl + Alt + D`** | **Cycle Display Mode**: `Fit to Screen` $\rightarrow$ `Stretch` $\rightarrow$ `Zoom` | In-App / Focused |
| **`Ctrl + Alt + N`** | **Native 16:10 Resolution Sync**: Negotiate `1920×1200` via VideoLayout Proto | In-App / Focused |
| **`Ctrl + Alt + C`** | Toggle **Cockpit 50/50 Dual Split** (`960×1200` per pane) | In-App / Focused |
| **`Ctrl + Alt + M`** | Toggle **RawInput Relative Mouse Mode** (3D CAD / Blender / Gaming) | In-App / Focused |
| **`Ctrl + Alt + S`** | Pin / Auto-hide the Top Notch Dynamic Dock | In-App / Focused |
| **`Ctrl + Alt + Home`** | **Escape Hatch**: Release mouse cursor confinement and pointer lock | In-App / Focused |
| **`Esc Esc`** (Double-Tap) | Alternative escape hatch to release relative mouse capture | In-App / Focused |
| **`F11`** | Toggle borderless fullscreen mode | In-App / Focused |

---

## 🏛️ Architecture & Internal Subsystems

```text
src/CRDLightweightARM/
├── Hardware/
│   ├── SnapdragonOryonGovernor.cs     # MMCSS thread promotion ("Games") & EcoQoS process throttling
│   ├── RawInputMouseEngine.cs         # Win32 WM_INPUT hardware mouse deltas & midpoint cursor confinement
│   └── DisplayTopologyDetector.cs     # GetDeviceCaps raster probe (1920x1200 16:10 @ 96 DPI) & profiles
├── NativeProtocol/
│   ├── ChromotingProtobuf.cs          # Zero-dependency protobuf encoder/decoder (VideoLayout, ClientResolution, HID)
│   ├── Spake2Authenticator.cs         # Chromium SPAKE2 Curve25519 authenticator & verification hash engine
│   └── D3D11AdrenoPresenter.cs        # Direct3D 11.1 / DXGI 1.6 zero-copy NV12 presentation on Adreno X1-45
├── crd-mediator.js                    # Injected WebRTC SCTP DataChannel interceptor, fit engine & watchdog
├── MainWindow.xaml / .cs              # Owned layered HUD window, low-level keyboard hook & session orchestrator
├── SessionModel.cs                    # Session configuration model & JSON persistence
├── PowerGovernor.cs                   # Windows AC/Battery power source listener
└── TrayIconHelper.cs                  # Windows 11 Fluent System Tray integration with EcoQoS standby
```

### 1. Zero-DOM Chromoting Protocol Stack ([`NativeProtocol/`](src/CRDLightweightARM/NativeProtocol/))
* **[`ChromotingProtobuf.cs`](src/CRDLightweightARM/NativeProtocol/ChromotingProtobuf.cs)**: Clean, zero-dependency binary serializer for Chromoting wire-format messages:
  * `ClientResolution` (Tag 2 in `ControlMessage`) with explicit DIPs calculation.
  * `VideoLayout` & `VideoTrackLayout` (Tag 10 in `ControlMessage`) for host virtual EDID negotiation.
  * `VideoControl` (Tag 4) for requesting 60 FPS framerate boost and lossless YUV444 encoding.
  * `SelectDesktopDisplayRequest` (Tag 11) for remote multi-monitor selection.
  * `EventMessage` & `KeyEvent` / `MouseEvent` for USB HID Usage Page `0x07` keyboard chords and unaccelerated relative mouse movement.
* **[`Spake2Authenticator.cs`](src/CRDLightweightARM/NativeProtocol/Spake2Authenticator.cs)**: Implements SPAKE2 over Ed25519/Curve25519 compliant with BoringSSL. Derives shared key material via `HMAC-SHA256(host_id, pin)` with 64-bit LE length transcript hashing and DTLS certificate-bound `CalculateVerificationHash`.
* **[`D3D11AdrenoPresenter.cs`](src/CRDLightweightARM/NativeProtocol/D3D11AdrenoPresenter.cs)**: Direct3D 11.1 + DXGI 1.6 Flip-Model presentation engine (`DXGI_SWAP_EFFECT_FLIP_DISCARD`, 1-frame waitable latency, `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`, and `ID3D11VideoContext::VideoProcessorBlt` zero-copy `DXGI_FORMAT_NV12` hardware composition tuned for Qualcomm Adreno X1-45 GPUs).

### 2. Snapdragon X Hardware Governor ([`Hardware/`](src/CRDLightweightARM/Hardware/))
* **[`SnapdragonOryonGovernor.cs`](src/CRDLightweightARM/Hardware/SnapdragonOryonGovernor.cs)**: Identifies Snapdragon X Plus (`X1P42100` 8-core Oryon CPU) and Adreno GPU:
  * Promotes the UI and rendering thread to MMCSS real-time schedule (`AvSetMmThreadCharacteristicsW("Games")` + `AVRT_PRIORITY_HIGH`) and disables `THREAD_POWER_THROTTLING_EXECUTION_SPEED`.
  * Applies Windows 11 `EcoQoS` (`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) and calls `EmptyWorkingSet` on standby session process trees to reclaim up to 400MB of RAM per background host.
* **[`RawInputMouseEngine.cs`](src/CRDLightweightARM/Hardware/RawInputMouseEngine.cs)**: Captures unaccelerated raw hardware mouse movement directly from Win32 `WM_INPUT` (`RegisterRawInputDevices` with `RIDEV_INPUTSINK`). Automatically recenters cursor to the window midpoint and streams `delta_x` / `delta_y` protobufs over WebRTC SCTP.
* **[`DisplayTopologyDetector.cs`](src/CRDLightweightARM/Hardware/DisplayTopologyDetector.cs)**: Queries true physical panel raster metrics (`DESKTOPHORZRES` = 1920, `DESKTOPVERTRES` = 1200, 60 Hz) bypassing OS display scaling virtualization, and computes macroblock-aligned target profiles for Full-Bleed 16:10 and Cockpit 50/50 split (8:10).

### 3. Owned Transparent Layered HUD Window Architecture
* **The Problem**: In WPF, `<wv2:WebView2>` is a Win32 child `HwndHost` (`msedgewebview2.exe`). Win32 rules dictate that child `HWND`s always paint on top of WPF vector controls in the same window (the classic "Airspace Bug"), hiding custom HUD elements.
* **The Solution**: The Top Notch dock, popups, and hover triggers are hosted inside a separate, owned transparent layered `Window` (`_hudWindow`).
  * `AllowsTransparency = true` with `Background = null`: Alpha=0 pixels are click-through to `WebView2`.
  * Dock elements (`#F218181B`) capture mouse clicks and hover events seamlessly above `WebView2`.
  * Automatically synchronizes position, size, and minimization state with the main window.

---

## 🚀 Getting Started

### Prerequisites
* **Windows 11 on ARM64** (Snapdragon X Plus / Elite) or **Windows 10/11 x64**.
* **.NET 9.0 SDK**: Installed at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` or standard `Program Files`.
* **Microsoft Edge WebView2 Runtime** (Preinstalled on Windows 11).

### One-Click Launch (Bypasses Smart App Control)
Windows 11 Smart App Control may block freshly compiled, unsigned `.exe` files in the `dist\` folder. To launch the client without security warnings, run the provided batch runner:

```cmd
Run-CRD.cmd
```

*This launches the compiled assembly through the Microsoft-signed `dotnet.exe` host, guaranteeing full compatibility with Smart App Control and Windows Defender.*

### Building from Source

```powershell
# 1. Build and launch native ARM64 release
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run

# 2. Run the automated 5-Stage Protocol & Hardware Self-Test
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\src\CRDLightweightARM\bin\Release\net9.0-windows\win-arm64\CRDLightweightARM.exe --self-test

# 3. Publish standalone single-file binary for ARM64
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Arch arm64 -PublishStandalone

# 4. Publish Dual-Architecture distribution (ARM64 + x64) into ./dist/
powershell -ExecutionPolicy Bypass -File .\build.ps1 -PublishAll
```

---

## 🧪 Automated 5-Stage Hardware & Protocol Self-Test

Verify hardware acceleration and reverse-engineered protocol compatibility on your local machine:

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

## ⚙️ Configuration (`%LOCALAPPDATA%\CRDLightweightARM\config.json`)

The client automatically creates and maintains a persistent configuration file:

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

* **Efficiency Modes**:
  * `UltraWarm`: Instant wake (<35ms), 0% background GPU decode, normal memory pacing (Recommended).
  * `EcoWarm`: Low RAM standby (~200ms wake), Windows 11 `EcoQoS` + `EmptyWorkingSet` memory compaction.
  * `LiveHot`: 0ms wake, all sessions continuously decode 60 FPS in background (Mains power).
* **AutoSyncNativeResolution**:
  * Set to `false` by default to ensure remote ultrawide and 4K displays maintain their native resolution and scale smoothly. Press `Ctrl+Alt+N` or click `16:10` to sync 1920×1200 on demand.

---

## 📄 License
MIT License. Copyright (c) 2026 Zac and Contributors.
