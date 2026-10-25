// Restarting an app so a new GPU choice takes effect now: an app reads its GPU only as it starts.
//
// The app restarted is the one that launched the process: a helper (Steam's web helper, Discord's
// clip recorder) is restarted by restarting its app. Its windows are asked to close first, as
// clicking X would; whatever is still running after a few seconds is ended (with the same checks as
// End). Then it's opened again as it was: its .exe with its command line, or a Store app through
// Windows (shell:AppsFolder), which is the only way a Store app may be started.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VramMeter;

public sealed class RestartPlan
{
    public required int Pid { get; init; }
    public required long Start { get; init; }
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string Args { get; init; } = "";
    public string? AppId { get; init; }
    public bool IsHelper { get; init; }  // restarting another app to restart this one
}

public static class Restarter
{
    const uint QueryLimited = 0x1000, Terminate = 0x0001, Synchronize = 0x00100000;

    // The app to restart for this one, or why it can't be.
    public static (RestartPlan? Plan, string? Why) PlanFor(AppGroup app)
    {
        if (app.ProtectedWhy != null) return (null, app.ProtectedWhy);
        if (app.Path == null || app.Procs.Count == 0) return (null, "Windows won't say where it's installed.");
        var tree = Snapshot();

        // Walk up from its first process to the oldest ancestor that is still an app of yours
        // (not Explorer, a service, or a process started after its child: a reused ID).
        int pid = app.Procs.OrderBy(p => p.Start).First().Pid;
        while (tree.TryGetValue(pid, out var me) && tree.TryGetValue(me.Parent, out var parent))
        {
            var info = Processes.Get(me.Parent);
            if (info == null || info.ProtectedWhy != null || info.Path == null || !Processes.IsMine(me.Parent)) break;
            var child = Processes.Get(pid);
            if (child == null || info.Start == 0 || info.Start > child.Start) break;
            pid = me.Parent;
        }

        var top = Processes.Get(pid);
        if (top?.Path == null) return (null, "Couldn't find the app that runs it.");
        if (top.ProtectedWhy != null) return (null, top.ProtectedWhy);
        IntPtr h = Processes.OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return (null, "Windows won't let VRAM Meter look at it (it may run as administrator).");
        try
        {
            return (new RestartPlan
            {
                Pid = pid, Start = top.Start, Path = top.Path, Title = top.Title,
                Args = ArgsOf(CommandLineOf(h)), AppId = AppIdOf(h),
                IsHelper = !string.Equals(top.Path, app.Path, StringComparison.OrdinalIgnoreCase),
            }, null);
        }
        finally { Processes.CloseHandle(h); }
    }

    // Closes the app (and everything it started), then opens it again. Runs off the UI thread.
    public static string? Restart(RestartPlan plan)
    {
        var tree = Snapshot();
        var family = Descendants(tree, plan.Pid).Prepend(plan.Pid).ToList();

        // The app it is, still: same .exe, started at the same moment.
        var top = Processes.Get(plan.Pid);
        if (top == null || top.Start != plan.Start || !string.Equals(top.Path, plan.Path, StringComparison.OrdinalIgnoreCase))
            return $"{plan.Title} already closed.";

        // 1. Ask its windows to close.
        var pids = family.ToHashSet();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (pids.Contains((int)owner) && IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero)
                PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        WaitGone(family, TimeSpan.FromSeconds(4));

        // 2. End what's left: apps that hide in the tray on close (Discord, Steam) keep running.
        foreach (int pid in family)
        {
            var p = Processes.Get(pid);
            if (p == null || p.ProtectedWhy != null) continue;
            IntPtr h = Processes.OpenProcess(Terminate | QueryLimited, false, pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (Processes.StartOf(h) == p.Start && !(Processes.IsProcessCritical(h, out var c) && c))
                    Processes.TerminateProcess(h, 1);
            }
            finally { Processes.CloseHandle(h); }
        }
        if (!WaitGone(family, TimeSpan.FromSeconds(5)))
            return $"{plan.Title} wouldn't close (it may run as administrator). Close and reopen it yourself.";

        // 3. Open it again, as it was opened.
        Thread.Sleep(500);
        try
        {
            if (plan.AppId != null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{plan.AppId}") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(plan.Path, plan.Args)
                {
                    UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(plan.Path) ?? "",
                });
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return $"{plan.Title} closed but didn't reopen: {e.Message}. Open it yourself.";
        }
    }

    static bool WaitGone(List<int> pids, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (pids.All(pid => Process.GetProcesses().All(p => p.Id != pid) || Processes.Get(pid) == null)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static IEnumerable<int> Descendants(Dictionary<int, (int Parent, string Exe)> tree, int root)
    {
        var all = new List<int>();
        var queue = new Queue<int>(new[] { root });
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (var kv in tree.Where(kv => kv.Value.Parent == p && kv.Key != p && !all.Contains(kv.Key)))
            {
                all.Add(kv.Key);
                queue.Enqueue(kv.Key);
            }
        }
        return all;
    }

    // Every process and its parent, from one Toolhelp snapshot.
    static Dictionary<int, (int Parent, string Exe)> Snapshot()
    {
        var tree = new Dictionary<int, (int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return tree;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                tree[(int)e.ProcessId] = ((int)e.ParentProcessId, e.ExeFile);
        }
        finally { Processes.CloseHandle(snap); }
        return tree;
    }

    // The command line a process was started with (ProcessCommandLineInformation, Windows 8.1+).
    static string CommandLineOf(IntPtr h)
    {
        int size = 4096;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(h, 60, buf, size, out int needed) != 0)
            {
                if (needed <= size) return "";
                Marshal.FreeHGlobal(buf);
                size = needed;
                buf = Marshal.AllocHGlobal(size);
                if (NtQueryInformationProcess(h, 60, buf, size, out _) != 0) return "";
            }
            ushort length = (ushort)Marshal.ReadInt16(buf);
            IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return Marshal.PtrToStringUni(text, length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Everything after the .exe: "C:\x\app.exe" --a b -> --a b
    static string ArgsOf(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return "";
        int end = s[0] == '"' ? s.IndexOf('"', 1) : s.IndexOf(' ');
        return end < 0 ? "" : s[(end + 1)..].Trim();
    }

    // A Store app's ID (Claude, Spotify, ...), or null for an ordinary app.
    static string? AppIdOf(IntPtr h)
    {
        uint length = 512;
        var sb = new StringBuilder((int)length);
        return GetApplicationUserModelId(h, ref length, sb) == 0 ? sb.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 entry);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr h, ref uint length, StringBuilder id);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
