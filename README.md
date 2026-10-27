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
| **Annoying Clipboard Permission Prompts** | **Silent Scoped Auto-Grant**: Seamless text/code/image copy-paste strictly scoped to CRD. |
| **Accidental Local Key Triggers (`Ctrl+Alt+Del`, `Win+L`)** | **Remote System Keys Flyout**: Direct CDP dispatch to remote host without locking your laptop. |
| **Guessing Network Quality** | **Live Telemetry HUD**: Real-time RTT latency and FPS (`16ms · 60f`) with color-coded health dot. |

---

## The Dynamic Island Top Notch (440px HUD)

```text
       Top Edge of Snapdragon Display (0px Wasted Baseline)
────────────────────────────────────────────────────────────────────────────────
[ Remote Tabs ]        [ The Top Notch: Center 30% ]          [ Remote Min/Close ]
  ┌────────────────────────────────────────────────────────────────────────┐
  │ ● 16ms 60f │ [● Workstation]  [◐ Home Lab]  [○ Cloud] │ ⛶ Fit ⌨ 📌 ⚙ ✕ │
  └────────────────────────────────────────────────────────────────────────┘
```

* **Live Telemetry Badge**: Real-time WebRTC round-trip time (RTT) and frame rate directly from the active connection.
  * 🟢 **Green**: Latency < 35ms & $\ge$ 30 FPS (Excellent)
  * 🟡 **Amber**: Latency 35–80ms (Moderate)
  * 🔴 **Red**: Latency > 80ms (High latency / Jitter)
* **Zero-Bar Display Switcher (`BtnDisplayMode`)**:
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
| **`Ctrl + Alt + 1`** | Instant jump to **Session 1** |
| **`Ctrl + Alt + 2`** | Instant jump to **Session 2** |
| **`Ctrl + Alt + 3`** | Instant jump to **Session 3** |
| **`Ctrl + Alt + Tab`** | Cycle forward to the next session |
| **`Ctrl + Alt + D`** | Cycle Display Mode (`Fit` $\rightarrow$ `Stretch` $\rightarrow$ `Zoom`) |
| **`Ctrl + Alt + S`** | Summon or dismiss the Top Notch Dock |
| **`Ctrl + Alt + Home`** | Release pointer lock / return focus to local PC |
| **`F11`** | Toggle borderless fullscreen mode |

---

## Building & Running

```powershell
# Build and run directly
.\build.ps1 -Run

# Publish single-file standalone ARM64 executable
.\build.ps1 -PublishStandalone
```

---

## Repository
GitHub: [https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm](https://github.com/zaczzk/chrome_remotedesktop_lightweight_arm)

## License
MIT License.
