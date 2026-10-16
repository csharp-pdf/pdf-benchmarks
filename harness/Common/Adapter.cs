// Shared by every library adapter. An adapter is a console program that converts on request:
// the runner writes one JSON command per line to stdin and reads one JSON reply per line from stdout.
// One library per process: several libraries embed Chromium, and two copies cannot share a process.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfBenchmarks;

/// <summary>The output conditions every library is asked for (METHODOLOGY.md, sections 4.1 and 4.5).</summary>
public sealed record RenderSettings(
    string Media,         // "screen" (all fixtures but paged-media) or "print" (paged-media)
    int WaitMs,           // extra wait after the page has loaded, for content drawn by JavaScript
    int ViewportWidth)    // browser width the page is laid out at; scaled to fit the paper width
{
    public bool Print => Media == "print";
    public const string Paper = "A4";   // margins: 0 for screen media; print media leaves margins to the page's @page rule

    /// <summary>"pdfa-2b", "pdfua-1", "pdfa-4+pdfua-2" ... or null (METHODOLOGY.md, 4.7).</summary>
    public string? Standard { get; init; }
    public string Title { get; init; } = "";
    public string Language { get; init; } = "en";
    /// <summary>Add a header line and a "Page N of M" footer with the library's own text header/footer API.</summary>
    public bool HeaderFooter { get; init; }
    /// <summary>HTML header/footer templates. The footer contains the tokens {page} and {pages}, which the
    /// adapter maps to the library's own page-number placeholders.</summary>
    public string? HeaderHtml { get; init; }
    public string? FooterHtml { get; init; }
    public string? TemplateBaseUrl { get; init; }
    /// <summary>Create PDF bookmarks from these CSS selectors (e.g. h2, h3).</summary>
    public string[]? Bookmarks { get; init; }
    /// <summary>Turn HTML form controls into PDF form fields, where the library has that option.</summary>
    public bool Forms { get; init; }
    /// <summary>Encrypt with these passwords and deny printing and copying (METHODOLOGY.md, 4.13).</summary>
    public string? UserPassword { get; init; }
    public string? OwnerPassword { get; init; }
    /// <summary>The page as a local file, for libraries whose tagging route reads files (sent with standards).</summary>
    public string? SourceFile { get; init; }
    /// <summary>True when the standard needs a tagged (accessible) file: PDF/UA, or PDF/A level "a".</summary>
    public bool NeedsTags => PdfUA != null || (PdfA?.EndsWith('a') ?? false);

    public const string HeaderText = "pdf-benchmarks";
    /// <summary>The PDF/A part ("1b", "2b", "4e" ...) of <see cref="Standard"/>, or null.</summary>
    public string? PdfA => Part("pdfa-");
    /// <summary>The PDF/X part ("1a") of <see cref="Standard"/>, or null.</summary>
    public string? PdfX => Part("pdfx-");
    /// <summary>The PDF/UA part ("1" or "2") of <see cref="Standard"/>, or null.</summary>
    public string? PdfUA => Part("pdfua-");
    string? Part(string prefix) =>
        Standard?.Split('+').FirstOrDefault(p => p.StartsWith(prefix))?[prefix.Length..];
}

/// <summary>Thrown by an adapter when the library has no option for what was asked; reported as "unsupported".</summary>
public sealed class UnsupportedException(string what) : Exception(what);

/// <summary>One library.</summary>
public interface IConverter
{
    string Library { get; }
    /// <summary>NuGet package id(s) and version(s) actually loaded, for results/packages.json.</summary>
    IReadOnlyDictionary<string, string> Packages { get; }
    /// <summary>Converts a URL to PDF and returns the bytes - the bytes are part of the measured work.</summary>
    (byte[] Pdf, int Pages) Convert(string url, RenderSettings settings);
    /// <summary>The same through the library's asynchronous API.</summary>
    Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings settings);
    /// <summary>Converts an HTML string; relative URLs in it resolve against <paramref name="baseUrl"/>.</summary>
    (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings settings);
}

/// <summary>
/// Work on existing PDFs and Word files (METHODOLOGY.md, 4.14-4.17). Every method gets a local file and
/// returns its result; a library without the feature throws <see cref="UnsupportedException"/>.
/// </summary>
public interface IPdfTools
{
    /// <summary>The text of every page, in page order.</summary>
    string[] ExtractText(string pdfFile);
    /// <summary>1-based page numbers of every match of <paramref name="term"/> (a page once per match).</summary>
    int[] Search(string pdfFile, string term);
    /// <summary>Every page as a PNG at <paramref name="dpi"/>.</summary>
    byte[][] Rasterize(string pdfFile, int dpi);
    /// <summary>A .docx file converted to PDF.</summary>
    (byte[] Pdf, int Pages) WordToPdf(string docxFile);
    /// <summary>The PDF with every occurrence of each term redacted (removed and covered).</summary>
    byte[] Redact(string pdfFile, string[] terms);
    /// <summary>The files joined in order.</summary>
    byte[] Merge(string[] pdfFiles);
    /// <summary>A new PDF with pages <paramref name="from"/>..<paramref name="to"/> (1-based, inclusive).</summary>
    byte[] Split(string pdfFile, int from, int to);
    /// <summary>The PDF with a diagonal text watermark on every page.</summary>
    byte[] Watermark(string pdfFile, string text);
    /// <summary>The PDF signed with the certificate in a PKCS#12 file.</summary>
    byte[] Sign(string pdfFile, string pfxFile, string password);
    /// <summary>The form filled: text fields get text, check boxes "true"/"false", choices and radio groups a value.</summary>
    byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values);
    /// <summary>A ZUGFeRD / Factur-X invoice: the page at <paramref name="url"/> as PDF/A-3b with the EN 16931 XML embedded.</summary>
    byte[] EInvoice(string url, string xmlFile);
}

public static class Adapter
{
    /// <summary>The NuGet packages (id, version) this adapter was built with, stamped in by Directory.Build.targets.</summary>
    public static IReadOnlyDictionary<string, string> PackageVersions() =>
        System.Reflection.Assembly.GetEntryAssembly()!.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith("package:"))
            .ToDictionary(a => a.Key["package:".Length..], a => a.Value ?? "");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed record Job(string Url, string Out);

    sealed record Command(string Op, string? Url, string? Out, string? Media, int WaitMs, int ViewportWidth,
                          int Concurrency, double Seconds, string? Mode, string? Html, string? BaseUrl,
                          string? Standard, string? Title, string? Language, bool HeaderFooter,
                          string? HeaderHtml, string? FooterHtml, string? TemplateBaseUrl, string[]? Bookmarks,
                          bool Forms, string? UserPassword, string? OwnerPassword, string? File, Job[]? Jobs,
                          string? Term, string[]? Terms, int Dpi, string[]? Files, int From, int To, string? Text,
                          string? Pfx, string? Password, Dictionary<string, string>? Values, string? Xml);

    public static int Run(IConverter converter)
    {
        var stdout = Console.Out;
        Console.SetOut(TextWriter.Null);   // libraries that log to the console must not corrupt the protocol
        void Reply(object o) { stdout.WriteLine(JsonSerializer.Serialize(o, Json)); stdout.Flush(); }

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            var cmd = JsonSerializer.Deserialize<Command>(line, Json)!;
            var settings = new RenderSettings(cmd.Media ?? "screen", cmd.WaitMs, cmd.ViewportWidth > 0 ? cmd.ViewportWidth : 1024)
            {
                Standard = cmd.Standard, Title = cmd.Title ?? "", Language = cmd.Language ?? "en",
                HeaderFooter = cmd.HeaderFooter, HeaderHtml = cmd.HeaderHtml, FooterHtml = cmd.FooterHtml,
                TemplateBaseUrl = cmd.TemplateBaseUrl, Bookmarks = cmd.Bookmarks, Forms = cmd.Forms,
                UserPassword = cmd.UserPassword, OwnerPassword = cmd.OwnerPassword, SourceFile = cmd.File,
            };
            bool async = cmd.Mode == "async";
            try
            {
                switch (cmd.Op)
                {
                    case "info":
                        Reply(new { ok = true, library = converter.Library, packages = converter.Packages,
                                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });
                        break;
                    case "convert":
                        var sw = Stopwatch.StartNew();
                        var (pdf, pages) = cmd.Html != null ? converter.ConvertHtml(cmd.Html, cmd.BaseUrl ?? "", settings)
                                         : async ? converter.ConvertAsync(cmd.Url!, settings).GetAwaiter().GetResult()
                                         : converter.Convert(cmd.Url!, settings);
                        sw.Stop();
                        Save(cmd.Out, pdf);
                        Reply(new { ok = true, ms = sw.Elapsed.TotalMilliseconds, pages, bytes = pdf.Length });
                        break;
                    case "throughput":
                        Reply(Throughput(converter, cmd.Url!, settings, cmd.Concurrency, cmd.Seconds, async));
                        break;
                    case "batch":
                        Reply(Batch(converter, cmd.Jobs!, settings, cmd.Concurrency, async));
                        break;
                    case "extract" or "search" or "rasterize" or "word" or "redact" or "merge" or "split" or "watermark"
                         or "sign" or "fill" or "einvoice":
                        Reply(Tool(converter as IPdfTools ?? throw new UnsupportedException("no PDF tools"), cmd));
                        break;
                    case "quit":
                        Close(converter);
                        Reply(new { ok = true });
                        return 0;
                    default:
                        Reply(new { ok = false, error = "unknown op " + cmd.Op });
                        break;
                }
            }
            catch (UnsupportedException e)
            {
                Reply(new { ok = false, unsupported = true, error = e.Message });
            }
            catch (Exception e)
            {
                Reply(new { ok = false, error = Describe(e) });
            }
        }
        Close(converter);
        return 0;
    }

    /// <summary>
    /// Shuts down what the library started (a browser, for the automation libraries). On Windows a child process
    /// outlives its parent, so a browser left running would keep using memory and CPU while the next library is measured.
    /// </summary>
    static void Close(IConverter converter)
    {
        try { (converter as IDisposable)?.Dispose(); }
        catch (Exception e) { Console.Error.WriteLine("close: " + Describe(e)); }
    }

    static object Tool(IPdfTools tools, Command cmd)
    {
        var sw = Stopwatch.StartNew();
        switch (cmd.Op)
        {
            case "extract":
                var pages = tools.ExtractText(cmd.File!);
                sw.Stop();
                if (cmd.Out != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cmd.Out))!); File.WriteAllText(cmd.Out, string.Join((char)12, pages)); }
                return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, pages = pages.Length };
            case "search":
                var hits = tools.Search(cmd.File!, cmd.Term!);
                return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, hits };
            case "rasterize":
                var images = tools.Rasterize(cmd.File!, cmd.Dpi > 0 ? cmd.Dpi : 150);
                sw.Stop();
                if (cmd.Out != null)
                    for (int i = 0; i < images.Length; i++) Save(Path.Combine(cmd.Out, $"page-{i + 1:000}.png"), images[i]);
                return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, pages = images.Length };
            case "word":
                var (pdf, n) = tools.WordToPdf(cmd.File!);
                sw.Stop();
                Save(cmd.Out, pdf);
                return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, pages = n, bytes = pdf.Length };
            default:
                byte[] result = cmd.Op switch
                {
                    "redact" => tools.Redact(cmd.File!, cmd.Terms!),
                    "merge" => tools.Merge(cmd.Files!),
                    "split" => tools.Split(cmd.File!, cmd.From, cmd.To),
                    "watermark" => tools.Watermark(cmd.File!, cmd.Text!),
                    "sign" => tools.Sign(cmd.File!, cmd.Pfx!, cmd.Password!),
                    "fill" => tools.FillForm(cmd.File!, cmd.Values!),
                    _ => tools.EInvoice(cmd.Url!, cmd.Xml!),
                };
                sw.Stop();
                Save(cmd.Out, result);
                return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, bytes = result.Length };
        }
    }

    static string Describe(Exception e)
    {
        while (e is AggregateException { InnerException: { } inner }) e = inner;
        return e.GetType().Name + ": " + e.Message.Split('\n')[0];
    }

    static void Save(string? path, byte[] pdf)
    {
        if (path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, pdf);
    }

    /// <summary>
    /// <paramref name="concurrency"/> workers convert back to back until the time is up. Conversions still running
    /// at the deadline are allowed to finish and are not counted. Async mode keeps <paramref name="concurrency"/>
    /// calls of the library's async API in flight instead of blocking threads.
    /// </summary>
    static object Throughput(IConverter converter, string url, RenderSettings s, int concurrency, double seconds, bool async)
    {
        int done = 0, failed = 0;
        var deadline = Stopwatch.StartNew();
        async Task AsyncWorker()
        {
            while (deadline.Elapsed.TotalSeconds < seconds)
            {
                try { await converter.ConvertAsync(url, s); if (deadline.Elapsed.TotalSeconds <= seconds) Interlocked.Increment(ref done); }
                catch { Interlocked.Increment(ref failed); }
            }
        }
        void SyncWorker()
        {
            while (deadline.Elapsed.TotalSeconds < seconds)
            {
                try { converter.Convert(url, s); if (deadline.Elapsed.TotalSeconds <= seconds) Interlocked.Increment(ref done); }
                catch { Interlocked.Increment(ref failed); }
            }
        }
        var workers = Enumerable.Range(0, concurrency).Select(_ => async ? AsyncWorker() : Task.Run(SyncWorker)).ToArray();
        Task.WaitAll(workers);
        return new { ok = true, completed = done, failed, seconds, perMinute = done * 60.0 / seconds };
    }

    /// <summary>Converts every job with <paramref name="concurrency"/> conversions in flight; each PDF is saved.</summary>
    static object Batch(IConverter converter, Job[] jobs, RenderSettings s, int concurrency, bool async)
    {
        var results = new object[jobs.Length];
        var gate = new SemaphoreSlim(concurrency);
        var sw = Stopwatch.StartNew();
        var tasks = jobs.Select(async (job, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var (pdf, pages) = async ? await converter.ConvertAsync(job.Url, s) : await Task.Run(() => converter.Convert(job.Url, s));
                Save(job.Out, pdf);
                results[i] = new { ok = true, pages, bytes = pdf.Length };
            }
            catch (Exception e) { results[i] = new { ok = false, error = Describe(e) }; }
            finally { gate.Release(); }
        }).ToArray();
        Task.WaitAll(tasks);
        return new { ok = true, ms = sw.Elapsed.TotalMilliseconds, results };
    }
}
