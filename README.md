# Chrome Remote Desktop - Lightweight ARM (CRD-ARM)

A minimalist, high-performance **Chrome Remote Desktop client wrapper** built natively for **Windows 11 on ARM64 (Qualcomm Snapdragon X Plus & Elite)**.

Designed for users who actively juggle **1 to 3 remote desktop sessions** and want **zero wasted screen space**, instantaneous session jumping, and near-zero background battery drain.

---

## What Makes This Different

| Standard Browser Behavior | CRD-ARM Solution |
| :--- | :--- |
| **70–110px Wasted Space** (Tabs, URL bar, bookmarks bar, window chrome) | **0px Wasted**: 100% full-bleed remote display canvas from edge to edge. |
| **Grey Letterbox Bars** on 16:10 / 3:2 laptop displays | **Zero-Bar Display Switcher**: Pitch-black OLED background + 1-click `Fit`, `Stretch`, and `Zoom` modes. |
| **Heavy ARM64 Battery Drain** (Multiple tabs decoding 60fps in background) | **0% Background GPU Decode**: Power-gates the Qualcomm Adreno VPU when inactive. |
| **Friction Switching Between 1–3 Hosts** | **Sub-35ms Warm Swap**: Instant hotkeys (`Ctrl+Alt+1..3`) and auto-hiding/pinned top notch. |
| **Juggling Two Machines at Once** | **Cockpit 50/50 Dual Mode**: View two machines side-by-side with asymmetric power pacing. |
| **3D CAD & Blender Viewports Colliding with Screen Edge** | **Relative Mouse Mode**: Infinite cursor rotation with fail-safe double-tap `Esc` breakout. |
| **Annoying Clipboard Permission Prompts** | **Silent Scoped Auto-Grant**: Seamless text/code/image copy-paste strictly scoped to CRD. |
| **Accidental Local Key Triggers (`Ctrl+Alt+Del`, `Win+L`)** | **Remote System Keys Flyout**: Direct CDP dispatch to remote host without locking your laptop. |
| **Guessing Network Quality** | **Live Telemetry HUD**: Real-time RTT latency and FPS (`16ms · 60f`) with color-coded health dot. |

---

## The Dynamic Island Top Notch (470px HUD)

```text
       Top Edge of Snapdragon Display (0px Wasted Baseline)
────────────────────────────────────────────────────────────────────────────────
[ Remote Tabs ]        [ The Top Notch: Center 30% ]          [ Remote Min/Close ]
  ┌────────────────────────────────────────────────────────────────────────┐
  │ ● 16ms 60f │ [● Workstation]  [◐ Home Lab]  [○ Cloud] │ ◫ 🔒 ⛶ ⌨ 📌 ⚙ ✕ │
  └────────────────────────────────────────────────────────────────────────┘
```

* **Live Telemetry Badge**: Real-time WebRTC round-trip time (RTT) and frame rate directly from the active connection.
  * 🟢 **Green**: Latency < 35ms & $\ge$ 30 FPS (Smooth connection).
  * 🟡 **Amber**: Latency 35–80ms (Moderate latency).
  * 🔴 **Red**: Latency > 80ms (High latency / Jitter).
* **Cockpit 50/50 Split (`◫`)**:
  * Toggles dual side-by-side view of Session 1 and Session 2.
  * Separated by a 1px high-DPI subtle divider.
  * Secondary pane runs with low-power memory target and audio solo on the focused pane.
* **Relative Mouse Mode (`🔒`)**:
  * Locks cursor and delivers raw delta coordinates for 3D modeling (Blender, Maya, CAD) and gaming.
  * Double-tap `Esc` or press `Ctrl + Alt + Home` immediately breaks cursor lock and returns pointer control.
* **Zero-Bar Display Switcher (`⛶ Fit`)**:
  * `⛶ Fit`: Preserves aspect ratio against pitch-black OLED background.
  * `↔ Stretch`: Fills 100% of vertical and horizontal display, eliminating all bars.
  * `🗖 Zoom`: Centers and scales video to eliminate letterbox margins without distortion.
* **Remote System Keys (`⌨`)**:
  * `Ctrl + Alt + Del`: Dispatches Secure Attention Sequence (SAS) to remote host.
  * `Win + L`: Locks remote machine without locking your local Snapdragon laptop.
  * `Win + D`: Minimizes all remote windows to show remote desktop.
  * `Ctrl + Shift + Esc`: Launches Task Manager on remote host.
  * `Release Cursor`: Exits pointer lock and returns mouse focus to local PC.
* **Thumbnail Peek on Hover**: Resting the cursor over an inactive session pill displays a floating 192×128 thumbnail preview of that machine's screen.
* **Pin Toggle (`📌`)**: Toggle between leaving the dock pinned at the top center or auto-collapsing.

---

## Snapdragon Smart Battery Governor

When running on your Snapdragon laptop:
* **On AC Power**: Unlocks full telemetry frequency (1.5s) and keeps swapchains hot.
* **On Battery**: Event-driven via Win32 `RegisterPowerSettingNotification`. Automatically lowers inactive session memory targets to `Low` and throttles background checks, saving power on Qualcomm Oryon CPU cores.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **`Ctrl + Alt + R`** | **Global Summon**: Instant hide to / restore from System Tray (from anywhere in Windows) |
| **`Ctrl + Alt + [1..3]`** | Instant jump to **Session 1, 2, or 3** |
| **`Ctrl + Alt + Tab`** | Cycle forward to the next session |
| **`Ctrl + Alt + C`** | Toggle **Cockpit 50/50 Dual View** |
| **`Ctrl + Alt + M`** | Toggle **Relative Mouse Mode** (3D CAD/Blender/Gaming) |
| **`Ctrl + Alt + D`** | Cycle Display Mode (`Fit` $\rightarrow$ `Stretch` $\rightarrow$ `Zoom`) |
| **`Ctrl + Alt + S`** | Summon or dismiss the Top Notch Dock |
| **`Ctrl + Alt + Home`** | Escape hatch: Release mouse cursor trap / pointer lock |
| **`F11`** | Toggle borderless fullscreen mode |

---

## Universal Architecture & Building

The client is built for **Windows on ARM64** (Snapdragon X Elite / Plus) and natively cross-compiles for standard **x64** (Intel Core / AMD Ryzen):

```powershell
# Build and run native ARM64 directly on your Snapdragon laptop
.\build.ps1 -Run

# Publish standalone single-file ARM64 executable
.\build.ps1 -Arch arm64 -PublishStandalone

# Publish standalone single-file x64 executable (for Intel / AMD PCs)
.\build.ps1 -Arch x64 -PublishStandalone

# Publish both ARM64 and x64 releases at once into ./dist/
.\build.ps1 -PublishAll
```

---

## System Tray & Process Lifecycle

* **Zero-Drain Standby**: Closing the window (`[X]`) tucks the application into the Windows 11 System Tray. Video decoders and background rendering are completely paused (0% GPU, <60MB RAM) while maintaining the WebRTC connection.
* **Single-Click Summon**: Left-click the tray icon or press `Ctrl + Alt + R` from anywhere to wake the client with a sub-45ms keyframe refresh.
* **Kernel Job Object**: All `msedgewebview2.exe` sub-processes are locked into a Win32 Job Object with `KILL_ON_JOB_CLOSE`, ensuring zero lingering background processes or lockfile collisions on exit.

---

## Repository
GitHub: [https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm](https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm)

## License
MIT License.
