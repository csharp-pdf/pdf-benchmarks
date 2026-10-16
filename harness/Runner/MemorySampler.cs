using System.Collections.Concurrent;

namespace PdfBenchmarks;

/// <summary>
/// Samples the memory (PSS) of each adapter's process tree every 100 ms and keeps the peak per label
/// (a label is "library|what is running", set by the runner around each measured step).
/// </summary>
sealed class MemorySampler : IDisposable
{
    readonly ConcurrentDictionary<string, (long Bytes, int Processes)> peaks = new();
    readonly ConcurrentDictionary<int, string> active = new();   // pid -> current label
    readonly Thread thread;
    volatile bool stop;

    public MemorySampler()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "memory-sampler" };
        thread.Start();
    }

    public void Begin(AdapterProcess p, string label) => active[p.Pid] = p.Library + "|" + label;
    public void End(AdapterProcess p) => active.TryRemove(p.Pid, out _);

    public long PeakBytes(string library, string label) => Peak(library, label).Bytes;
    /// <summary>Peak memory (PSS) and the number of processes in the tree at that moment.</summary>
    public (long Bytes, int Processes) Peak(string library, string label) =>
        peaks.TryGetValue(library + "|" + label, out var v) ? v : (0, 0);

    void Loop()
    {
        while (!stop)
        {
            foreach (var (pid, label) in active.ToArray())
            {
                var ws = ProcessTree.WorkingSet(pid);
                peaks.AddOrUpdate(label, ws, (_, old) => ws.Bytes > old.Bytes ? ws : old);
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose() { stop = true; thread.Join(); }
}
