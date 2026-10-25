// VRAM Meter: which apps use the GPU's memory, ending them, and choosing the GPU each one runs on.
//   VramMeter.exe          open the window (or bring up the one already running)
//   VramMeter.exe --tray   start in the tray (at logon)
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool trayOnly = e.Args.Contains("--tray");
        // Started in the first minutes after Windows: at sign-in, or opened by hand before Windows
        // got to its startup entry.
        bool signIn = trayOnly || Environment.TickCount64 < 5 * 60 * 1000;

        instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            if (!trayOnly && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) ev.Set();
            Shutdown();
            return;
        }
        // Started at sign-in: log the first minutes of the GPUs (see SignInLog).
        if (signIn || e.Args.Contains("--sign-in-log")) SignInLog.Start();

        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        window = new MainWindow();
        tray = new Tray();
        tray.Open += ShowWindow;
        tray.Exit += ExitApp;
        window.Reading += tray.Update;
        window.AutoSorted += tray.Notify;
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible || window.Exiting || window.Settings.TrayHintShown) return;
            window.Settings.TrayHintShown = true;
            window.Settings.Save();
            tray.Hint();
        };
        if (!trayOnly) ShowWindow();
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
