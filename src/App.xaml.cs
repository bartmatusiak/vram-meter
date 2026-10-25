// VRAM Meter: which apps use the GPU's memory, ending them, and choosing the GPU each one runs on.
//   VramMeter.exe          open the window (or bring up the one already running)
//   VramMeter.exe --tray   start in the tray (at logon)
//   VramMeter.exe --displays-normal   save a layout of the fast GPU's displays alone, and stop
//                                     starting on the power saving one (uninstall runs this)
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

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
        tray.ToggleOverlay += ToggleOverlay;

        // Alt+O anywhere, games included, shows or hides the overlay.
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == OverlayHotkey) { ToggleOverlay(); handled = true; }
            return IntPtr.Zero;
        });
        if (!RegisterHotKey(hwnd, OverlayHotkey, 0x1 | 0x4000 /* Alt, no repeat */, 0x4F /* O */))
            tray.Notify("Alt+O is taken by another app, so it can't show and hide the overlay. Use the tray menu.");
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

    const int OverlayHotkey = 1;

    void ToggleOverlay()
    {
        if (overlay == null || window == null) return;
        if (overlay.Shown) overlay.Hide();
        else overlay.Show();
        window.OverlayOn = overlay.Shown;
        window.Settings.Overlay = overlay.Shown;
        window.Settings.Save();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

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
