// Frames per second of the app in front, from Windows' event tracing: each Present a DirectX app
// makes (DXGI for DirectX 10-12, D3D9 for 9) is an event; counting them over the last second gives
// its frame rate, as PresentMon and Game Bar do. Reading these events takes administrator rights or
// membership of the Performance Log Users group (Game Bar's FPS "Request access" adds you to it);
// without either, there's no FPS and nothing else changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace VramMeter;

public sealed class Fps : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    readonly Dictionary<int, Queue<long>> presents = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    TraceEventSession? session;

    // Why there's no FPS, when there isn't.
    public string? Unavailable { get; private set; }

    public Fps()
    {
        new Thread(Run) { IsBackground = true, Name = "FPS" }.Start();
    }

    void Run()
    {
        try
        {
            session = new TraceEventSession("VramMeterFps") { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            session.EnableProvider(D3D9, TraceEventLevel.Informational);
            session.Source.AllEvents += e =>
            {
                int id = (int)e.ID;
                bool present = e.ProviderGuid == Dxgi ? id is DxgiPresentStart or DxgiPresentMpoStart
                             : e.ProviderGuid == D3D9 && id == D3D9PresentStart;
                if (!present) return;
                lock (presents)
                {
                    if (!presents.TryGetValue(e.ProcessID, out var q)) presents[e.ProcessID] = q = new();
                    q.Enqueue(clock.ElapsedMilliseconds);
                }
            };
            session.Source.Process();  // blocks until disposed
        }
        catch (UnauthorizedAccessException)
        {
            Unavailable = "FPS needs your account in Windows' Performance Log Users group (then sign out and in).";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            Unavailable = "FPS unavailable: " + e.Message;
        }
    }

    // The frame rate of the foreground app, and the window it's in; null when it isn't presenting.
    public (int Pid, double Fps, IntPtr Window)? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        long now = clock.ElapsedMilliseconds;
        lock (presents)
        {
            // Drop what's older than a second, everywhere, so the queues stay small.
            foreach (var q in presents.Values)
                while (q.Count > 0 && now - q.Peek() > 1000) q.Dequeue();
            if (!presents.TryGetValue((int)pid, out var mine) || mine.Count < 2) return null;
            return ((int)pid, mine.Count, hwnd);
        }
    }

    public void Dispose()
    {
        try { session?.Dispose(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
