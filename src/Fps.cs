// Frames per second of the app in front, from Windows' event tracing: each Present a DirectX app
// makes (DXGI for DirectX 10-12, D3D9 for 9) is an event. Counted as PresentMon and RivaTuner do:
// per swap chain (the busiest one, so a second one, a launcher overlay or a video, doesn't add to it),
// by each event's own timestamp (Windows hands them over in batches, so the time one arrives says
// nothing), as frames over the time they span in the last second.
// Reading these events takes administrator rights or membership of the Performance Log Users group
// (Game Bar's FPS "Request access" adds you to it); without either, there's no FPS.
using System;
using System.Collections.Generic;
using System.Linq;
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
    const double Window = 1000;  // ms, RivaTuner's default averaging interval

    // Present times (ms, from the trace) per process and swap chain.
    readonly Dictionary<(int Pid, ulong Chain), Queue<double>> presents = new();
    // When each process's frames last arrived, by the clock (batches come about once a second).
    readonly Dictionary<int, long> arrived = new();
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
                // The swap chain (DXGI) or device (D3D9) is the event's first field, a pointer.
                var data = e.EventData();
                ulong chain = data.Length >= 8 ? BitConverter.ToUInt64(data, 0) : 0;
                double t = e.TimeStampRelativeMSec;
                lock (presents)
                {
                    var key = (e.ProcessID, chain);
                    if (!presents.TryGetValue(key, out var q)) presents[key] = q = new();
                    q.Enqueue(t);
                    while (q.Count > 0 && t - q.Peek() > Window * 2) q.Dequeue();
                    arrived[e.ProcessID] = Environment.TickCount64;
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
        lock (presents)
        {
            // Its busiest swap chain over the last second of its own frames.
            double best = 0;
            foreach (var (key, q) in presents)
            {
                if (key.Pid != (int)pid || q.Count < 2) continue;
                double last = q.Last();
                var recent = q.Where(t => last - t <= Window).ToList();
                if (recent.Count < 2) continue;
                double span = recent[^1] - recent[0];
                if (span > 0) best = Math.Max(best, (recent.Count - 1) * 1000 / span);
            }
            // Stale: the app stopped presenting (minimised, paused on a loading screen that doesn't draw).
            return best > 0 && Fresh((int)pid) ? ((int)pid, best, hwnd) : null;
        }
    }

    // Whether the app's frames have kept arriving: none for 3 s means it stopped presenting.
    bool Fresh(int pid) => arrived.TryGetValue(pid, out var at) && Environment.TickCount64 - at < 3000;

    public void Dispose()
    {
        try { session?.Dispose(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
