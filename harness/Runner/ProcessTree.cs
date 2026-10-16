using System.Runtime.InteropServices;

namespace PdfBenchmarks;

/// <summary>
/// Memory of a process and all of its descendants, as the proportional set size (PSS): each process's
/// private memory plus its share of the memory it shares with other processes. Browser-based engines
/// render in several processes that map the same libraries; adding up plain working sets would count
/// that shared memory once per process and overstate the libraries that use more processes
/// (METHODOLOGY.md, 4.3). Linux reports PSS directly; on Windows it is computed from the working-set
/// pages, dividing each shared page by its share count.
/// </summary>
static class ProcessTree
{
    public static (long Bytes, int Processes) WorkingSet(int rootPid)
    {
        var parents = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        var tree = new HashSet<int> { rootPid };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, ppid) in parents)
                if (tree.Contains(ppid) && tree.Add(pid)) grew = true;
        }
        long total = 0;
        foreach (var pid in tree)
            total += OperatingSystem.IsWindows() ? WindowsPss(pid) : LinuxPss(pid);
        return (total, tree.Count);
    }

    // ---- Linux: /proc
    static List<(int, int)> LinuxParents()
    {
        var list = new List<(int, int)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');   // after "(comm) ": state ppid ...
                list.Add((pid, int.Parse(rest[1])));
            }
            catch { /* process exited */ }
        }
        return list;
    }

    static long LinuxPss(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/smaps_rollup"))
                if (line.StartsWith("Pss:"))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;   // kB
        }
        catch { /* process exited */ }
        return 0;
    }

    // ---- Windows: Toolhelp snapshot + GetProcessMemoryInfo
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool K32QueryWorkingSet(IntPtr h, IntPtr buffer, int size);
    [ThreadStatic] static IntPtr wsBuffer;
    [ThreadStatic] static int wsBufferSize;

    static List<(int, int)> WindowsParents()
    {
        var list = new List<(int, int)>();
        IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    /// <summary>
    /// PSS from QueryWorkingSet: one entry per resident page - bit 8 says it is shared, bits 5-7 hold how
    /// many processes share it (7 means 7 or more). Private pages count in full, shared pages divided.
    /// </summary>
    static long WindowsPss(int pid)
    {
        IntPtr h = OpenProcess(0x0400 /* QUERY_INFORMATION */ | 0x0010 /* VM_READ */, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            if (wsBufferSize == 0) { wsBufferSize = 1 << 20; wsBuffer = Marshal.AllocHGlobal(wsBufferSize); }
            while (!K32QueryWorkingSet(h, wsBuffer, wsBufferSize))
            {
                if (Marshal.GetLastWin32Error() != 24 /* ERROR_BAD_LENGTH */) return 0;
                long needed = (Marshal.ReadIntPtr(wsBuffer).ToInt64() + 4096) * IntPtr.Size + IntPtr.Size;
                Marshal.FreeHGlobal(wsBuffer);
                wsBufferSize = (int)Math.Min(needed, int.MaxValue);
                wsBuffer = Marshal.AllocHGlobal(wsBufferSize);
            }
            int entries = (int)Marshal.ReadIntPtr(wsBuffer).ToInt64();
            var pages = new long[entries];
            Marshal.Copy(wsBuffer + IntPtr.Size, pages, 0, entries);
            double bytes = 0;
            int page = Environment.SystemPageSize;
            foreach (long e in pages)
            {
                bool shared = (e & 0x100) != 0;
                int shareCount = (int)((e >> 5) & 0x7);
                bytes += shared ? (double)page / Math.Max(shareCount, 1) : page;
            }
            return (long)bytes;
        }
        finally { CloseHandle(h); }
    }
}
