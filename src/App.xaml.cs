// VRAM Meter: which apps use the GPU's memory, ending them, and choosing the GPU each one runs on.
//   VramMeter.exe          open the window (or bring up the one already running)
//   VramMeter.exe --tray   start in the tray (at logon)
//   VramMeter.exe --displays-normal   save a layout of the fast GPU's displays alone, and stop
//                                     starting on the power saving one (uninstall runs this)
using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace VramMeter;

public partial class App : Application
{
    // One VRAM Meter per sign-in; starting another shows this one's window.
    const string InstanceName = @"Local\VramMeter";
    const string ShowEventName = @"Local\VramMeterShow";

    Mutex? instance;
    EventWaitHandle? showEvent;
    MainWindow? window;
    Tray? tray;
    Overlay? overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool trayOnly = e.Args.Contains("--tray");
        if (e.Args.Contains("--displays-normal"))
        {
            DisplaysNormal();
            Shutdown();
            return;
        }

        instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            if (!trayOnly && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) ev.Set();
            Shutdown();
            return;
        }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        window = new MainWindow();
        tray = new Tray();
        tray.Open += ShowWindow;
        tray.Exit += ExitApp;
        tray.SwitchDisplays += window.SwitchDisplays;
        tray.DisplaysLabel = () => window.SwitchDisplaysLabel;
        overlay = new Overlay(window.Settings);
        window.Read += overlay.Update;
        if (window.Settings.Overlay) overlay.Show();
        window.OverlayOn = overlay.Shown;
        tray.OverlayShown = () => overlay.Shown;
        tray.ToggleOverlay += () =>
        {
            if (overlay.Shown) overlay.Hide();
            else overlay.Show();
            window.OverlayOn = overlay.Shown;
            window.Settings.Overlay = overlay.Shown;
            window.Settings.Save();
        };
        window.TrayReading += tray.Update;
        window.AutoSorted += tray.Notify;
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible || window.Exiting || window.Settings.TrayHintShown) return;
            window.Settings.TrayHintShown = true;
            window.Settings.Save();
            tray.Hint();
        };
        if (!trayOnly) ShowWindow();

        // Signed in on the power saving GPU's display: switch to the fast one once startup apps are up.
        if (window.Settings.SignInOnPowerSaving && Environment.TickCount64 < 10 * 60 * 1000)
        {
            SignInSwitch.Start(message => Dispatcher.BeginInvoke(() =>
            {
                if (message == null) return;
                window.DisplaysChanged();
                tray.Notify(message);
            }));
        }
    }

    static void DisplaysNormal()
    {
        var settings = Settings.Load();
        settings.SignInOnPowerSaving = false;
        settings.Save();
        var gpus = Dxgi.Gpus();
        var low = gpus.FirstOrDefault(g => g.PowerSaving);
        var high = gpus.FirstOrDefault(g => g.HighPerformance);
        if (low == null || high == null || !Displays.Available(low, high)) return;
        try { Displays.FastOnly(low, high, save: true); }
        catch (InvalidOperationException) { }
    }

    void ShowWindow()
    {
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void ExitApp()
    {
        if (window != null)
        {
            window.Exiting = true;
            window.Close();
        }
        overlay?.Dispose();
        tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
