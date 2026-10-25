# VRAM Meter

A Windows app that shows how much of each GPU's memory is in use and which apps hold it, in the
look of Call of Duty's VRAM meter. It can end apps, choose the GPU each app runs on, sort games and
everything else between two GPUs, and show a small overlay on each display.

Made for PCs with two GPUs, a graphics card plus the graphics built into the CPU, where a game
wants every last GB of the card and Discord, browsers and launchers don't need to be on it.

## Download

Get `VramMeter.exe` from the [latest release](https://github.com/bartmatusiak/vram-meter/releases/latest).
It's one self-contained file for Windows 10/11 (x64): nothing else to install, no admin needed.
Run it from anywhere, or install it properly with `install.ps1` (below).

## What it does

- **The meter**: one bar per GPU. Green is the app you're measuring (the biggest game running, or
  the app you click), white is everything else, out of the card's total.
- **Apps**: every app holding GPU memory, by what actually sits in the GPU's own memory (Windows'
  *Local Usage*; its *Dedicated Usage* also charges recorders, overlays and the desktop compositor
  for every screen they borrow, reaching dozens of GB on a 16 GB card). An app drawn on one GPU
  and shown on a screen plugged into the other keeps a little on that one for the frames it hands
  over: `ON RADEON +0.04 GB RTX 5080`. On a built-in GPU the meter counts the system RAM it borrows.
- **GPU**: the GPU an app runs on, by name, or Auto. This is Windows' own per-app setting
  (Settings › System › Display › Graphics, which calls them High performance and Power saving), so
  it lasts; an app takes it up when it next starts. The GPU column turns amber when an app is set to
  one GPU but running on the other (see the Windows issue below).
- **Which GPU runs what**: a column per GPU; games on the fast card, capture and overlay tools (OBS,
  NVIDIA's overlay, Discord clips) with them, everything else on the other GPU. Click an app to move
  it across. A game is an .exe Windows' Game Bar has detected as one, or one in a game library
  (Steam, EA, Epic, GOG, Ubisoft, Riot, Xbox). It can also sort each new app as it first uses a GPU.
- **Displays**: with a monitor cabled to both GPUs, switch which GPU's display is main (apps start on
  that GPU), and optionally start Windows on the built-in GPU's display, so the apps that start with
  Windows land there, then show only the card's display once they're up (not saved, so every boot
  starts the same way). See the Windows issue below for why.
- **Overlay**: a badge in the corner of each display: its GPU and cable (with one monitor on two
  inputs, which one you're looking at), resolution, refresh rate, and GPU and CPU use. Click-through.
- **End**: closes every process of an app, after you confirm.
- **Tray**: the icon is a small live meter; hover for the figures, click for the window, right-click
  for the display switch, the overlay, Start with Windows and Exit.

## The Windows issue

On some Windows builds (seen on Insider build 26300 with an RTX 5080 and a Ryzen's Radeon graphics),
Windows ignores the per-app GPU setting for apps opened from Start, the taskbar, a shortcut or at
sign-in: they all start on the GPU the main display is plugged into. Windows' own Graphics settings
page writes the very same registry entry VRAM Meter does, and it's ignored just the same; started some
other ways, the same app lands on the GPU it's set to. Until Windows fixes it, the display switch is
the workaround: apps start on the GPU whose display is main, and stay there when you switch.

## Safety

- No network and no server: nothing outside the app can ask it to do anything.
- Runs as you, never as administrator, so it can only end what you could end in Task Manager.
- Won't end Windows' own processes, services, anything Windows marks critical, or itself.
- Ends a process only after checking, through the same handle it ends it with, that it is the same
  .exe started at the same moment as the one listed, so a reused process ID can't be hit.
- Confirming needs a click: Cancel has the focus and Enter doesn't confirm.
- Writes only your own settings: its GPU choices (just the `GpuPreference` entry, keeping Auto HDR
  and the rest), its startup and uninstall entries, and, if you turn on starting on the built-in
  GPU's display, your saved display layout (turning it off or uninstalling puts the card's display
  back alone).

## Installing from source

Windows 10/11, no admin needed. Building needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

It builds one self-contained `VramMeter.exe` into `%LOCALAPPDATA%\Programs\VRAM Meter`, adds it to the
Start menu and the desktop, starts it with Windows in the tray, and lists it in Settings › Apps ›
Installed apps (uninstall there, or `install.ps1 -Remove`). Rerun it after changing the source.

## Files

- `src/`: the app (WPF on .NET 10). `Gpus.cs` reads the GPUs (DirectX) and the GPU memory counters;
  `Processes.cs` names and ends processes; `GpuPreferences.cs` sets an app's GPU; `Sorter.cs` tells
  games from the rest; `Displays.cs` switches displays; `Overlay.cs` and `Load.cs` the overlay;
  `Tray.cs` the tray icon; `MainWindow.xaml` the window.
- `src/make-icon.ps1`: draws `VramMeter.ico`.
- `install.ps1`: builds and installs; `-Remove` uninstalls.
