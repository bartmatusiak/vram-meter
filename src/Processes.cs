// What each process is, whether it's safe to end, and ending it.
//
// Ending is guarded so a click can only ever close the app you meant:
// - VRAM Meter runs as you (never elevated), so Windows lets it end only what you could end yourself.
// - Windows' own processes, services in other sessions, anything Windows marks critical, and
//   VRAM Meter itself are refused, here and by greying out the button.
// - Each process is opened by its ID and checked to be the same .exe, started at the same moment,
//   as the one listed, before it's ended through that handle; a reused process ID can't be hit.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VramMeter;

public sealed class ProcInfo
{
    public int Pid { get; init; }
    public long Start { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string Title { get; init; } = "";
    public string? ProtectedWhy { get; init; }
}

public sealed record EndResult(int Ended, int Gone, List<string> Refused);

public static class Processes
{
    const uint QueryLimited = 0x1000, Terminate = 0x0001;

    // Windows' own, in your session: ending these logs you out, blanks the screen or breaks the desktop.
    static readonly HashSet<string> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "dwm",
        "fontdrvhost", "explorer", "sihost", "ctfmon", "audiodg", "LogonUI", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "TextInputHost", "Widgets", "ApplicationFrameHost",
        "RuntimeBroker", "svchost", "taskhostw", "conhost", "dllhost", "SecurityHealthSystray",
    };

    static readonly int Self = Environment.ProcessId;
    static readonly uint MySession = SessionOf(Self) ?? 0;
    static readonly Dictionary<int, ProcInfo> Cache = new();
    static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase);

    public static ProcInfo? Get(int pid)
    {
        long start = 0;
        string? path = null;
        bool critical = false;
        IntPtr h = OpenProcess(QueryLimited, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                start = StartOf(h);
                path = PathOf(h);
                critical = IsProcessCritical(h, out var c) && c;
            }
            finally { CloseHandle(h); }
        }
        lock (Cache)
        {
            if (Cache.TryGetValue(pid, out var cached) && cached.Start == start) return cached;
        }

        string name;
        if (path != null) name = System.IO.Path.GetFileNameWithoutExtension(path);
        else
        {
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch (ArgumentException) { return null; }  // gone
            catch (InvalidOperationException) { return null; }
        }

        var info = new ProcInfo
        {
            Pid = pid, Start = start, Name = name, Path = path, Title = TitleOf(name, path),
            ProtectedWhy = WhyProtected(pid, name, critical),
        };
        lock (Cache) Cache[pid] = info;
        return info;
    }

    static string? WhyProtected(int pid, string name, bool critical)
    {
        if (pid == Self) return "This is VRAM Meter. Exit it from its tray icon.";
        if (pid <= 4 || Windows.Contains(name)) return "Part of Windows: ending it could log you out or blank the screen.";
        if (critical) return "Windows marks it critical: ending it would crash the PC.";
        if (SessionOf(pid) is uint s && s != MySession) return "A Windows service, not one of your apps.";
        return null;
    }

    static string TitleOf(string name, string? path)
    {
        if (path == null) return name;
        lock (Titles)
        {
            if (Titles.TryGetValue(path, out var t)) return t;
            string title = name;
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d)) title = d;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Titles[path] = title;
            return title;
        }
    }

    public static EndResult End(AppGroup app)
    {
        if (app.ProtectedWhy != null) throw new InvalidOperationException(app.ProtectedWhy);
        int ended = 0, gone = 0;
        var refused = new List<string>();
        foreach (var r in app.Procs)
        {
            IntPtr h = OpenProcess(Terminate | QueryLimited, false, r.Pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 87) gone++;  // ERROR_INVALID_PARAMETER: no such process
                else refused.Add($"{r.Pid}: {new Win32Exception(err).Message}");
                continue;
            }
            try
            {
                // The same program, started at the same moment, as the one listed: not a reused ID.
                if (StartOf(h) != r.Start || !string.Equals(PathOf(h), app.Path, StringComparison.OrdinalIgnoreCase))
                {
                    gone++;
                    continue;
                }
                if ((IsProcessCritical(h, out var c) && c) || SessionOf(r.Pid) != MySession)
                {
                    refused.Add($"{r.Pid}: protected by Windows");
                    continue;
                }
                if (TerminateProcess(h, 1)) ended++;
                else refused.Add($"{r.Pid}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
            finally { CloseHandle(h); }
        }
        lock (Cache) foreach (var r in app.Procs) Cache.Remove(r.Pid);
        return new EndResult(ended, gone, refused);
    }

    internal static string? WhyProtectedNow(int pid, string name)
    {
        IntPtr h = OpenProcess(QueryLimited, false, pid);
        bool critical = false;
        if (h != IntPtr.Zero)
        {
            try { critical = IsProcessCritical(h, out var c) && c; }
            finally { CloseHandle(h); }
        }
        return WhyProtected(pid, name, critical);
    }

    internal static bool IsMine(int pid) => SessionOf(pid) == MySession;

    internal static long StartOf(IntPtr h) => GetProcessTimes(h, out var created, out _, out _, out _) ? created : 0;

    internal static string? PathOf(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    static uint? SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out var s) ? s : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr h, uint exitCode);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll")]
    internal static extern bool IsProcessCritical(IntPtr h, out bool critical);
}
