# NotOnlyFans

Temperatures, fans, RGB and graphics-card tuning for Windows 11, in one fast app that looks and feels like Windows Settings.

NotOnlyFans is two parts: a small **background service** that reads every sensor once a second and drives the fans and lights you put under its control (also with the window closed), and the **app**, which shows it all and lets you change it. Switching pages is instant: the pages stay in memory and the data is already there.

> **Status:** early, made and tested on one PC (MSI PRO Z690-A, RTX 3060). Nothing in the code is tied to that hardware: groups, sensors and fans come from what the service finds. Reports from other PCs are very welcome.

## Features

- **Home**: CPU, graphics card, fans, motherboard and drives at a glance, with usage and speed history.
- **Cooling**: every fan the PC can control.
  - Modes: **Curve** (drag points on a chart), **Mix** (2–4 curves from different sensors, highest / lowest / average), **Offset** (follow another fan ± %), **Fixed**, or leave it to the **BIOS**.
  - Response: hysteresis, delay before slowing down, smooth changes (% per second), minimum speed, start speed (a short kick for fans that don't start at low duty), stop at 0%.
  - **Calibration** measures where a fan starts and stops.
  - Graphics-card fans can be driven one by one; a true 0% where the card allows it (checked once per fan).
  - Rename fans, change their order, pin them.
- **Sensors**: every sensor LibreHardwareMonitor sees, grouped by device. Click a row for its last 5 minutes; hide what you don't need, rename, pin.
- **Pinned**: chosen readings always in the sidebar and in the tray icon's tooltip.
- **Colors**: motherboard and graphics-card RGB, static or effects, or a color that follows a temperature. Supported now: MSI Mystic Light boards (USB HID) and Gigabyte RGB Fusion 1 graphics cards.
- **OC**: graphics card core and memory clock offsets and the power limit, optionally applied when Windows starts — but never after a crash of Windows or the graphics driver.
  - NVIDIA GeForce (NVAPI and NVML).
  - AMD Radeon RX 6000 and newer (ADLX): highest GPU and memory clock as offsets from the default, power limit in %. *Experimental: not tested on an AMD card yet.*
  - Intel Arc (IGCL): GPU clock offset and power limit; asks you once to accept the risk, as Intel requires. *Experimental: not tested on an Arc card yet.*
  - If a maker's driver library ever stops the service, NotOnlyFans stops using it (the OC tab says so) until it is installed again.
- **Diagnostics**: Settings › Copy diagnostics puts a report for bug reports on the clipboard (hardware, sensors, settings, recent errors; no user or computer names).
- Features this PC can't use stay visible but greyed, with the reason on hover.
- **Game overlay**: your pinned readings over games in borderless or windowed mode. Clicks go through it; nothing is injected into the game. `Ctrl+Shift+O` shows or hides it, `Ctrl+Shift+M` moves it.
- Tray icon, start with Windows, light and dark theme, Back / Forward with the mouse side buttons.

## Safety

- **Emergency temperature** (default 90 °C, 60–100 °C): when any real temperature reaches it, every fan NotOnlyFans drives runs at 100% — whatever its setting — until all temperatures are 5 °C lower.
- When the service stops, fails or Windows shuts down, every fan goes back to the BIOS (or the graphics card).
- If reading the sensors fails, fans go back to the BIOS until it works again; a sensor a curve depends on has no reading → that fan runs at 100%.
- Fans left in BIOS mode are never touched.

## Install

Requirements: Windows 11 x64 (Windows 10 may work, not tested).

1. Install the **PawnIO** driver — LibreHardwareMonitor uses it to read the CPU and the motherboard's sensor chip. Without it CPU and motherboard sensors (and motherboard fans) are missing.
   ```powershell
   winget install namazso.PawnIO
   ```
   or download it from [pawnio.eu](https://pawnio.eu).
2. Download `NotOnlyFans-<version>-x64.zip` from [Releases](../../releases) and unpack it.
3. Run `Install.ps1` (right-click › *Run with PowerShell*, or `powershell -ExecutionPolicy Bypass -File .\Install.ps1`). It asks for administrator rights once.

What the installer does, so you know what you are trusting:

- Adds `NotOnlyFans.cer` to **Local Machine › Trusted People**. The app is an MSIX package signed with the author's own certificate, not one from a company Windows already knows; Windows installs only packages whose signer it trusts.
- Copies the service to `C:\Program Files\NotOnlyFans\Service` and registers the Windows service **NotOnlyFans** (Local System, starts with Windows, restarts itself if it fails). It needs those rights to reach the sensor chips.
- Installs the app for your user.

**Smart App Control** (Windows Security › App & browser control) blocks programs from signers it doesn't know, so the installer stops if it is on.

### Uninstall

1. Windows Settings › Apps › NotOnlyFans › Uninstall.
2. `Uninstall-Service.ps1` from the zip (removes the service; fans go back to the BIOS).
3. Optionally remove the certificate: `certlm.msc` › Trusted People › Certificates › the NotOnlyFans one.

Settings stay in `%ProgramData%\NotOnlyFans` (fans, lights, overclock) until you delete that folder.

## Build from source

Needs the .NET 10 SDK on Windows 11 x64.

```powershell
# Build and install (or update) the app and the service on this PC: one UAC prompt.
powershell -ExecutionPolicy Bypass -File tools\Install.ps1

# Only the app (after the first install).
powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -AppOnly

# Run a development copy next to the installed one ("NotOnlyFans Dev", its own settings).
dotnet run --project NotOnlyFans/NotOnlyFans.csproj -p:Platform=x64

# Make a release zip in artifacts\ (installs nothing).
powershell -ExecutionPolicy Bypass -File tools\Build-Release.ps1 -Version 0.1.0
```

The first run makes a self-signed code-signing certificate in your user certificate store (`tools\Signing.ps1`); its private key never leaves your PC.

## How it works

| Project | What it is |
|---|---|
| `NotOnlyFans.Service` | Windows service (.NET worker, Local System). Reads sensors with [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) once a second, runs fan curves and the safety rule, drives RGB (own drivers) and graphics card overclocking (NVIDIA NVAPI/NVML, AMD ADLX, Intel IGCL — the libraries that come with each driver). Settings in `%ProgramData%\NotOnlyFans`. |
| `NotOnlyFans` | WinUI 3 app (Windows App SDK), packaged as MSIX. Only shows and sends changes; it holds no hardware access. |
| `NotOnlyFans.Contracts` | The messages both sides share (JSON, source-generated). |

They talk over two named pipes on this PC only: `NotOnlyFans.Sensors.v1` (the service streams readings to every open app) and `NotOnlyFans.Control.v1` (the app sends one request per change). Network access to both is denied. Any user signed in at the PC may change fan, light and overclock settings through the app — keep that in mind on a shared PC.

## Disclaimer

NotOnlyFans writes to fan controllers, RGB controllers and the graphics card. It has safety rules, but overclocking and unusual fan settings are at your own risk.

## Credits

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) — sensors and fan control.
- [PawnIO](https://pawnio.eu) — the driver LibreHardwareMonitor uses for low-level hardware access.
- [HidSharp](https://www.zer7.com/software/hidsharp) — comes with LibreHardwareMonitor.
- Windows App SDK / WinUI 3 — the app.

## License

Copyright © 2026 Kacper

NotOnlyFans is free software: you can use it, study it, change it and share it under the terms of the [GNU General Public License v3.0](LICENSE). A changed version you share must be under the same license, with its source code.
