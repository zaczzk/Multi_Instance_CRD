<p align="center">
  <img src="assets/app-icon.png" width="100" alt="Multi_Instance_CRD Icon" />
</p>

# Multi_Instance_CRD

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20(ARM64%20%7C%20x64)-blue.svg)](https://github.com/zaczzk/Multi_Instance_CRD)
[![Build Status](https://github.com/zaczzk/Multi_Instance_CRD/actions/workflows/build.yml/badge.svg)](https://github.com/zaczzk/Multi_Instance_CRD/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A fast, lightweight Windows desktop app for **Chrome Remote Desktop**. Control multiple remote computers in clean, borderless windows without browser tabs getting in the way.

Native support for **Qualcomm Snapdragon X (ARM64)** and **Intel/AMD (x64)**.

---

## ✨ Features

- **True Edge-to-Edge**: Removes all browser address bars and window borders for 100% full-screen remote desktop.
- **Multi-Host Switching**: Switch between up to 3 remote PCs instantly.
- **Split-Screen Mode**: View two remote PCs side-by-side on one monitor.
- **Auto-Hiding Top Bar**: Hover at the top edge of the screen to switch PCs or change settings; it stays hidden while you work.
- **Battery & RAM Friendly**: Native ARM64 and x64 builds pause background sessions to save RAM and laptop battery.
- **OLED Pure Black**: Black borders instead of browser grey bars to prevent burn-in and save power on OLED screens.

---

## 📥 Download & Install

Grab the latest version from [**GitHub Releases**](https://github.com/zaczzk/Multi_Instance_CRD/releases):

| Package | Size | Description |
| :--- | :--- | :--- |
| **Setup (Recommended)** | ~77 MB | Standalone installer with everything included (no .NET required). |
| **Setup (Lightweight)** | ~3.4 MB | Tiny installer if you already have .NET 9 Desktop Runtime installed. |
| **Portable ZIP** | ~50 MB | No installation needed. Extract and run `Run-CRD.cmd`. |

*Note: The installer automatically updates existing versions and warns you before downgrading.*

---

## ⌨️ Essential Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + Alt + [1..3]` | Switch directly to Session 1, 2, or 3 |
| `Ctrl + Alt + C` | Toggle side-by-side split screen |
| `Ctrl + Alt + M` | Lock mouse for games/3D apps *(double-tap `Esc` to unlock)* |
| `Ctrl + Alt + O` | Switch remote monitor (Display 1 → Display 2 → All) |
| `Ctrl + Alt + R` | Hide to system tray / bring it back |
| `F11` | Toggle borderless fullscreen |

---

## ⚙️ Configuration

Remote machines are saved in `%LOCALAPPDATA%\CRDLightweightARM\config.json`. 

You can set your session names and access URLs directly:

```json
{
  "Sessions": [
    { "Index": 1, "Name": "Work PC", "Url": "https://remotedesktop.google.com/access" },
    { "Index": 2, "Name": "Home Lab", "Url": "https://remotedesktop.google.com/access" },
    { "Index": 3, "Name": "Cloud VM", "Url": "https://remotedesktop.google.com/access" }
  ]
}
```

---

## 🛠️ Building from Source

Requirements: Windows 11, [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```cmd
:: Build and run
build.cmd -Run

:: Build installers and release packages
powershell .\build.ps1 -BuildInstallers
```

---

## License

MIT License. Copyright (c) 2026 zaczzk.
