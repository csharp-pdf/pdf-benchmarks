using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfBenchmarks;

/// <summary>A running library adapter: one JSON command per line in, one JSON reply per line out.</summary>
sealed class AdapterProcess : IDisposable
{
    readonly Process process;
    public string Library { get; }
    public int Pid => process.Id;
    public TimeSpan StartedAt { get; }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    AdapterProcess(string library, Process p, TimeSpan startedAt) { Library = library; process = p; StartedAt = startedAt; }

    public static AdapterProcess Start(string library, string executable, string logDir, Stopwatch clock)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        var startedAt = clock.Elapsed;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + executable);
        Directory.CreateDirectory(logDir);
        var log = new StreamWriter(new FileStream(Path.Combine(logDir, library + ".stderr.log"), FileMode.Append,
                                                  FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        p.Exited += (_, _) => { lock (log) log.Dispose(); };
        p.EnableRaisingEvents = true;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) { try { log.WriteLine(e.Data); } catch (ObjectDisposedException) { } } };
        p.BeginErrorReadLine();
        return new AdapterProcess(library, p, startedAt);
    }

    public JsonObject Send(object command, TimeSpan timeout)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(command, Json));
        process.StandardInput.Flush();
        var read = process.StandardOutput.ReadLineAsync();
        if (!read.Wait(timeout))
            throw new TimeoutException($"{Library}: no reply within {timeout.TotalSeconds:0} s");
        var line = read.Result ?? throw new InvalidOperationException(
            $"{Library}: adapter exited (code {(process.HasExited ? process.ExitCode : -1)}); see its stderr log");
        return JsonNode.Parse(line)!.AsObject();
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("{\"op\":\"quit\"}");
                process.StandardInput.Flush();
                if (!process.WaitForExit(15000)) process.Kill(entireProcessTree: true);
            }
        }
        catch { try { process.Kill(entireProcessTree: true); } catch { } }
        process.Dispose();
    }
}
