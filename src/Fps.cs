// Frames per second of the app in front, from Windows' event tracing: each Present a DirectX app
// makes (DXGI for DirectX 10-12, D3D9 for 9) is an event. Counted as PresentMon and RivaTuner do:
// per swap chain (the busiest one, so a second one, a launcher overlay or a video, doesn't add to it),
// by each event's own timestamp (Windows hands them over in batches, so the time one arrives says
// nothing), as frames over the time they span in the last second.
// Frame generation (DLSS Frame Generation, FSR's in games with Reflex): a game with NVIDIA Reflex also
// reports each frame it renders itself through Reflex's latency markers (PCLStats, an open provider);
// rendered against presented gives the share generated. AMD's driver-level AFMF adds frames outside
// the game's presents, so it can't be seen this way.
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
    static readonly Guid Reflex = new("0D216F06-82A6-4D49-BC4F-8F38AE56EFAB");  // PCLStats
    const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;
    const int ReflexPresentStart = 4;  // PCLStats' PRESENT_START marker: one per frame the game renders
    const double Window = 1000;  // ms, RivaTuner's default averaging interval

    // Present times (ms, from the trace) per process and swap chain.
    readonly Dictionary<(int Pid, ulong Chain), Queue<double>> presents = new();
    // Frames each process rendered itself, by its Reflex markers.
    readonly Dictionary<int, Queue<double>> rendered = new();
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
            session.EnableProvider(Reflex, TraceEventLevel.Verbose);
            // Reflex's markers are self-describing events: PCLStatsEvent { Marker, FrameID }.
            session.Source.Dynamic.All += e =>
            {
                if (e.ProviderGuid != Reflex || e.PayloadIndex("Marker") < 0) return;
                if (Convert.ToInt32(e.PayloadByName("Marker")) != ReflexPresentStart) return;
                double t = e.TimeStampRelativeMSec;
                lock (presents)
                {
                    if (!rendered.TryGetValue(e.ProcessID, out var q)) rendered[e.ProcessID] = q = new();
                    q.Enqueue(t);
                    while (q.Count > 0 && t - q.Peek() > Window * 2) q.Dequeue();
                }
            };
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

    // The frame rate of the foreground app, the share of its frames generated (null when it doesn't
    // report its own frames), and the window it's in; null when it isn't presenting.
    public (int Pid, double Fps, double? Generated, IntPtr Window)? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return Of((int)pid) is var (fps, generated) ? ((int)pid, fps, generated, hwnd) : null;
    }

    // A process's frame rate and share generated, or null when it isn't presenting.
    public (double Fps, double? Generated)? Of(int pid)
    {
        lock (presents)
        {
            // Its busiest swap chain.
            double best = presents.Where(kv => kv.Key.Pid == pid).Select(kv => Rate(kv.Value)).DefaultIfEmpty(0).Max();
            // Stale: the app stopped presenting (minimised, paused on a loading screen that doesn't draw).
            if (best <= 0 || !Fresh(pid)) return null;
            double? generated = null;
            if (rendered.TryGetValue(pid, out var own) && Rate(own) is double real and > 0)
                generated = Math.Clamp(1 - real / best, 0, 1) is var g && g < 0.05 ? 0 : g;  // within 5%: none
            return (best, generated);
        }
    }

    // Frames per second over the last second of a queue's own timestamps.
    static double Rate(Queue<double> q)
    {
        if (q.Count < 2) return 0;
        double last = q.Last();
        var recent = q.Where(t => last - t <= Window).ToList();
        if (recent.Count < 2) return 0;
        double span = recent[^1] - recent[0];
        return span > 0 ? (recent.Count - 1) * 1000 / span : 0;
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
