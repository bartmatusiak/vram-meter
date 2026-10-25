# VRAM Meter

Shows how much of each GPU's memory is in use and which apps hold it, in the look of Call of Duty's
VRAM meter; ends apps; and picks the GPU each app runs on.

## Installing

Windows 10/11, no admin needed. Needs the .NET 10 SDK to build (`winget install Microsoft.DotNet.SDK.10`);
the installed app needs nothing.

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

It builds one self-contained `VramMeter.exe` into `%LOCALAPPDATA%\Programs\VRAM Meter`, adds it to the
Start menu and the desktop, starts it with Windows in the tray, and lists it in Settings > Apps > Installed apps
(uninstall there, or `install.ps1 -Remove`). Rerun it after changing the source.

## Using it

- **The meter**: one bar per GPU. Green is the app you're measuring (the biggest game running, or
  the app you click, until the window closes or you click it again), white is everything else, from
  the card's own total.
- **Apps**: every app holding GPU memory, by what sits in the GPU's own memory (Windows' Local Usage;
  its Dedicated Usage also charges recorders, overlays and the compositor for every screen they
  borrow, reaching dozens of GB on a 16 GB card). An app drawn on one GPU and shown on a screen
  plugged into the other keeps a little on that one for the frames it hands over: ON RADEON +0.04 GB
  RTX 5080.
- **GPU**: a GPU by name, or Auto. This is Windows' own per-app setting (Settings > System >
  Display > Graphics, where the GPUs are called High performance and Power saving), so it lasts, and an app takes it up the next time
  it starts: restart the PC to switch everything cleanly. Power saving is a second GPU only when one
  is on (a CPU's graphics, enabled in the BIOS). Where an app runs turns amber when it's set to one
  GPU but running on the other: it started before the setting, or, opened at sign-in, before a
  built-in GPU's driver was ready; closing and reopening it moves it.
- **Which GPU runs what**: a column per GPU with every app that can move in the one it will run on:
  games on the fast card, everything else on the other GPU, capture and overlay
  tools (OBS, NVIDIA's overlay, Discord clips) with the games. Click an app to move it across; Apply
  sets them, and a restart of the PC switches them. A game is an .exe Windows' Game Bar has
  detected as one, or one in a game library (Steam, EA, Epic, GOG, Ubisoft, Riot, Xbox). Its option
  to keep sorting sorts each new app as it first uses a GPU; apps you've set yourself are left alone.
  On a built-in GPU the meter counts the system RAM it borrows, since that is its memory.
- **End**: closes every process of the app, after you confirm.
- **Tray**: the icon is a small live meter; hover for the figures, click for the window, right-click
  for Start with Windows and Exit. Closing the window leaves it in the tray.

## Safety

- No network and no server: nothing outside the app can ask it to do anything.
- Runs as you, never as administrator, so it can only end what you could end in Task Manager.
- Won't end Windows' own processes, services, anything Windows marks critical, or itself.
- Ends a process only after checking, through the same handle it ends it with, that it is the same
  .exe started at the same moment as the one listed, so a reused process ID can't be hit.
- Confirming needs a click: Cancel has the focus and Enter doesn't confirm.
- Writes only to your own registry: its GPU choices (just the `GpuPreference` entry, keeping Auto HDR
  and the rest), its startup entry and its uninstall entry.

## Files

- `src/`: the app (WPF on .NET 10). `Gpus.cs` reads the GPUs (DirectX) and the GPU memory counters;
  `Processes.cs` names and ends processes; `GpuPreferences.cs` sets an app's GPU; `Tray.cs` the tray
  icon; `MainWindow.xaml` the window.
- `src/make-icon.ps1`: draws `VramMeter.ico`.
- `install.ps1`: builds and installs; `-Remove` uninstalls.
