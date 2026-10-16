// Benchmark runner (METHODOLOGY.md). Starts the library adapters, drives them, and writes raw results.
//
//   runner --adapter selectpdf=<exe> --adapter ironpdf=<exe> --base http://127.0.0.1:8777
//          --results results/<run> --pdfs out/<run>/pdf [--rounds 9] [--cold 5] [--seconds 60]
//          [--only outputs,speed,cold,throughput] [--fixtures name,name]
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfBenchmarks;

const string MethodologyVersion = "1.0";
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var opt = Options.Parse(args);
var root = Options.RepoRoot();
var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "fixtures.json")))!.AsObject();
int viewport = manifest["viewportWidth"]!.GetValue<int>();
var fixtures = manifest["fixtures"]!.AsArray().Select(f => f!.AsObject())
    .Where(f => opt.Fixtures.Count == 0 || opt.Fixtures.Contains(f["name"]!.GetValue<string>())).ToList();
Directory.CreateDirectory(opt.Results);
Directory.CreateDirectory(opt.Pdfs);

var clock = Stopwatch.StartNew();
var convertTimeout = TimeSpan.FromMinutes(5);
using var sampler = new MemorySampler();
var logs = Path.Combine(opt.Pdfs, "..", "logs");
void Log(string s) => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {s}");

// A fixture's page: its own folder, or another fixture's page ("source") - e.g. html-string converts the
// invoice page passed as an HTML string instead of a URL.
string Page(JsonObject f) => f["source"]?.GetValue<string>() ?? f["name"]!.GetValue<string>();
object ConvertCmd(JsonObject f, string? outPath, string? standard = null)
{
    bool html = f["input"]?.GetValue<string>() == "html";
    string page = Page(f);
    return new
    {
        op = "convert",
        url = html ? null : $"{opt.Base}/{page}/index.html",
        html = html ? File.ReadAllText(Path.Combine(root, "fixtures", page, "index.html")) : null,
        baseUrl = html ? $"{opt.Base}/{page}/" : null,
        @out = outPath,
        media = f["media"]?.GetValue<string>() ?? "screen",
        waitMs = f["waitMs"]?.GetValue<int>() ?? 0,
        viewportWidth = viewport,
        headerFooter = f["headerFooter"]?.GetValue<bool>() ?? false,
        bookmarks = f["bookmarks"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
        forms = f["forms"]?.GetValue<bool>() ?? false,
        headerHtml = f["htmlHeaderFooter"] != null ? File.ReadAllText(Path.Combine(root, "fixtures", "html-header-footer", "header.html")) : null,
        footerHtml = f["htmlHeaderFooter"] != null ? File.ReadAllText(Path.Combine(root, "fixtures", "html-header-footer", "footer.html")) : null,
        templateBaseUrl = f["htmlHeaderFooter"] != null ? $"{opt.Base}/html-header-footer/" : null,
        userPassword = f["encrypt"]?["userPassword"]?.GetValue<string>(),
        ownerPassword = f["encrypt"]?["ownerPassword"]?.GetValue<string>(),
        standard,
        file = standard != null ? Path.Combine(root, "fixtures", page, "index.html") : null,
        title = standard != null ? manifest["standards"]!["title"]!.GetValue<string>() : null,
        language = standard != null ? manifest["standards"]!["language"]!.GetValue<string>() : null,
    };
}
string Name(JsonObject f) => f["name"]!.GetValue<string>();
// One conversion that doubles as a warm-up; false when the library answers "unsupported" (it has no HTML
// converter, or no async API) - the step then records that once instead of timing failures.
bool Supports(AdapterProcess a, JsonObject f, string mode = "sync")
{
    var cmd = JsonNode.Parse(JsonSerializer.Serialize(ConvertCmd(f, null), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
    cmd["mode"] = mode;
    return a.Send(cmd, convertTimeout)["unsupported"] == null;
}
string PdfPath(string lib, JsonObject f) => Path.GetFullPath(Path.Combine(opt.Pdfs, lib, Name(f) + ".pdf"));

// ---- environment and package versions
var packages = new JsonObject();
foreach (var (lib, exe) in opt.Adapters)
{
    using var a = AdapterProcess.Start(lib, exe, logs, clock);
    var info = a.Send(new { op = "info" }, TimeSpan.FromMinutes(2));
    packages[lib] = JsonNode.Parse(info.ToJsonString());
    Log($"{lib}: {info["packages"]!.ToJsonString()} on {info["runtime"]}");
}
File.WriteAllText(Path.Combine(opt.Results, "packages.json"), packages.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(opt.Results, "env.json"), Env.Describe(MethodologyVersion, opt).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// ---- outputs: every fixture once per library, PDFs kept for analysis (isolation pair in sequence)
if (opt.Steps.Contains("outputs"))
{
    var csv = new Csv(Path.Combine(opt.Results, "outputs.csv"), "library,fixture,ok,unsupported,ms,pages,bytes,error");
    foreach (var (lib, exe) in opt.Adapters)
    {
        using var a = AdapterProcess.Start(lib, exe, logs, clock);
        foreach (var f in fixtures)
        {
            var r = a.Send(ConvertCmd(f, PdfPath(lib, f)), convertTimeout);
            csv.Row(lib, Name(f), r["ok"], r["unsupported"] ?? false, r["ms"], r["pages"], r["bytes"], r["error"]);
            Log($"outputs {lib} {Name(f)}: {(r["ok"]!.GetValue<bool>() ? $"{r["pages"]} pages" : r["error"])}");
        }
    }
}

// ---- standards: the standards fixture once per PDF/A and PDF/UA level, PDFs kept for veraPDF (METHODOLOGY.md, 4.7)
if (opt.Steps.Contains("standards"))
{
    var std = manifest["standards"]!.AsObject();
    var source = manifest["fixtures"]!.AsArray().Select(f => f!.AsObject()).First(f => Name(f) == std["fixture"]!.GetValue<string>());
    var csv = new Csv(Path.Combine(opt.Results, "standards_runs.csv"), "library,standard,ok,unsupported,ms,pages,bytes,error");
    foreach (var (lib, exe) in opt.Adapters)
    {
        using var a = AdapterProcess.Start(lib, exe, logs, clock);
        a.Send(ConvertCmd(source, null), convertTimeout);   // warm-up, so the times compare like for like
        foreach (var level in std["levels"]!.AsArray().Select(x => x!.GetValue<string>()))
        {
            var path = Path.GetFullPath(Path.Combine(opt.Pdfs, lib, "standards", level + ".pdf"));
            var r = a.Send(ConvertCmd(source, path, level), convertTimeout);
            csv.Row(lib, level, r["ok"], r["unsupported"] ?? false, r["ms"], r["pages"], r["bytes"], r["error"]);
            Log($"standards {lib} {level}: {(r["ok"]!.GetValue<bool>() ? "ok" : (r["unsupported"] != null ? "unsupported" : r["error"]))}");
        }
    }
}

// ---- speed + memory: all adapters alive, libraries interleaved, order rotated every round
if (opt.Steps.Contains("speed"))
{
    var speedFixtures = fixtures.Where(f => f["speed"]?.GetValue<bool>() == true).ToList();
    var speed = new Csv(Path.Combine(opt.Results, "speed.csv"), "library,fixture,round,ok,ms,pages,bytes,error");
    var procs = opt.Adapters.Select(x => AdapterProcess.Start(x.Key, x.Value, logs, clock)).ToList();
    try
    {
        var unsupported = new HashSet<(string, string)>();
        foreach (var a in procs)   // warm-up: one discarded conversion of every fixture
            foreach (var f in speedFixtures)
                if (!Supports(a, f))
                {
                    unsupported.Add((a.Library, Name(f)));
                    speed.Row(a.Library, Name(f), 0, false, "", "", "", "unsupported");
                }
        Log("speed: warm-up done");
        for (int round = 1; round <= opt.Rounds; round++)
        {
            foreach (var f in speedFixtures)
            {
                for (int i = 0; i < procs.Count; i++)
                {
                    var a = procs[(i + round) % procs.Count];
                    if (unsupported.Contains((a.Library, Name(f)))) continue;
                    var r = a.Send(ConvertCmd(f, null), convertTimeout);
                    speed.Row(a.Library, Name(f), round, r["ok"], r["ms"], r["pages"], r["bytes"], r["error"]);
                }
            }
            Log($"speed: round {round}/{opt.Rounds}");
        }
    }
    finally { procs.ForEach(p => p.Dispose()); }
}

// ---- cold start: a fresh process per conversion, libraries alternating
if (opt.Steps.Contains("cold"))
{
    var csv = new Csv(Path.Combine(opt.Results, "cold.csv"), "library,run,process_start_to_pdf_ms,conversion_ms,ok,error");
    var trivial = fixtures.FirstOrDefault(f => Name(f) == "trivial") ?? fixtures[0];
    for (int run = 1; run <= opt.Cold; run++)
    {
        foreach (var (lib, exe) in opt.Adapters)
        {
            using var a = AdapterProcess.Start(lib, exe, logs, clock);
            var r = a.Send(ConvertCmd(trivial, null), convertTimeout);
            var total = (clock.Elapsed - a.StartedAt).TotalMilliseconds;
            csv.Row(lib, run, total.ToString("0"), r["ms"], r["ok"], r["error"]);
        }
        Log($"cold: run {run}/{opt.Cold}");
    }
}

// ---- throughput: a fresh process per library, 1 / 4 / 8 parallel conversions for a fixed time
if (opt.Steps.Contains("throughput"))
{
    var csv = new Csv(Path.Combine(opt.Results, "throughput.csv"),
                      "library,fixture,mode,concurrency,seconds,completed,failed,per_minute");
    foreach (var f in fixtures.Where(f => f["throughput"]?.GetValue<bool>() == true))
    {
        foreach (var (lib, exe) in opt.Adapters)
        {
            using var a = AdapterProcess.Start(lib, exe, logs, clock);
            foreach (var mode in new[] { "sync", "async" })   // async: the library's own async API (METHODOLOGY.md, 4.2)
            {
                if (!Supports(a, f, mode))   // also the warm-up
                {
                    csv.Row(lib, Name(f), mode, "", "", "", "", "unsupported");
                    Log($"throughput {lib} {mode}: unsupported");
                    continue;
                }
                foreach (int c in new[] { 1, 4, 8 })
                {
                    var r = a.Send(new { op = "throughput", url = $"{opt.Base}/{Name(f)}/index.html", media = "screen", mode,
                                         waitMs = 0, viewportWidth = viewport, concurrency = c, seconds = opt.Seconds },
                                   TimeSpan.FromSeconds(opt.Seconds) + convertTimeout);
                    csv.Row(lib, Name(f), mode, c, opt.Seconds, r["completed"], r["failed"], r["perMinute"]?.GetValue<double>().ToString("0.0"));
                    Log($"throughput {lib} {mode} x{c}: {r["perMinute"]?.GetValue<double>():0.0}/min, {r["failed"]} failed");
                }
            }
        }
    }
}
// ---- parallel correctness: the same fixtures converted with 8 conversions in flight, sync and async; the
// analysis compares each PDF with the sequential one from the outputs step (METHODOLOGY.md, 4.2)
if (opt.Steps.Contains("parallel"))
{
    var spec = manifest["parallel"]!;
    var names = spec["fixtures"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
    int copies = spec["copies"]!.GetValue<int>(), concurrency = spec["concurrency"]!.GetValue<int>();
    var csv = new Csv(Path.Combine(opt.Results, "parallel_runs.csv"), "library,mode,jobs,ms,failed,errors");
    foreach (var (lib, exe) in opt.Adapters)
        foreach (var mode in new[] { "sync", "async" })
        {
            using var a = AdapterProcess.Start(lib, exe, logs, clock);
            if (!Supports(a, fixtures.First(f => Name(f) == names[0]), mode))   // also the warm-up
            {
                csv.Row(lib, mode, "", "", "", "unsupported");
                continue;
            }
            // A batch uses one setting for every job: a 1 s wait, which js-chart needs and the other fixtures ignore.
            var jobs = names.SelectMany(n => Enumerable.Range(1, copies).Select(k => new
            {
                url = $"{opt.Base}/{n}/index.html",
                @out = Path.GetFullPath(Path.Combine(opt.Pdfs, lib, "parallel", mode, $"{n}-{k}.pdf")),
            })).ToArray();
            var r = a.Send(new { op = "batch", jobs, concurrency, mode, media = "screen", viewportWidth = viewport, waitMs = 1000 },
                           convertTimeout * 3);
            var results = r["results"]!.AsArray();
            var errors = results.Where(x => x!["ok"]!.GetValue<bool>() == false).Select(x => x!["error"]!.GetValue<string>()).Distinct();
            csv.Row(lib, mode, jobs.Length, r["ms"], results.Count(x => x!["ok"]!.GetValue<bool>() == false), string.Join(" | ", errors));
            Log($"parallel {lib} {mode}: {jobs.Length} jobs in {r["ms"]?.GetValue<double>():0} ms");
        }
}

// ---- errors and timeouts: what each library does with a page that hangs, is missing, is unreachable or is
// broken, and whether the same process still converts afterwards (METHODOLOGY.md, 4.12)
if (opt.Steps.Contains("errors"))
{
    var csv = new Csv(Path.Combine(opt.Results, "errors.csv"), "library,case,outcome,ms,pages,error,recovered");
    var trivial = fixtures.First(f => Name(f) == "trivial");
    var limit = TimeSpan.FromMinutes(5);
    foreach (var (lib, exe) in opt.Adapters)
    {
        var a = AdapterProcess.Start(lib, exe, logs, clock);
        if (!Supports(a, trivial))   // also the warm-up
        {
            csv.Row(lib, "", "unsupported", "", "", "no HTML conversion", "");
            a.Dispose();
            continue;
        }
        foreach (var c in manifest["errors"]!.AsArray().Select(x => x!.AsObject()))
        {
            string name = c["case"]!.GetValue<string>(), url = c["url"]!.GetValue<string>();
            if (url.StartsWith('/')) url = opt.Base + url;
            var started = clock.Elapsed;
            string outcome, error = "";
            JsonNode? pages = null;
            try
            {
                var r = a.Send(new { op = "convert", url, @out = Path.GetFullPath(Path.Combine(opt.Pdfs, lib, "errors", name + ".pdf")),
                                     media = "screen", viewportWidth = viewport }, limit);
                outcome = r["ok"]!.GetValue<bool>() ? "pdf" : "error";
                error = r["error"]?.GetValue<string>() ?? "";
                pages = r["pages"];
            }
            catch (TimeoutException)
            {
                outcome = "hung";   // no answer within 5 minutes: the process is killed and restarted
                a.Dispose();
                a = AdapterProcess.Start(lib, exe, logs, clock);
            }
            var ms = (clock.Elapsed - started).TotalMilliseconds;
            bool recovered;
            try { recovered = a.Send(ConvertCmd(trivial, null), convertTimeout)["ok"]!.GetValue<bool>(); }
            catch { recovered = false; a.Dispose(); a = AdapterProcess.Start(lib, exe, logs, clock); }
            csv.Row(lib, name, outcome, ms.ToString("0"), pages, error, recovered);
            Log($"errors {lib} {name}: {outcome} in {ms / 1000:0.0} s{(recovered ? "" : ", NOT recovered")}");
        }
        a.Dispose();
    }
}

// ---- stability: many conversions in one process - failures, memory growth, slow-down (METHODOLOGY.md, 4.3)
if (opt.Steps.Contains("stability"))
{
    var spec = manifest["stability"]!;
    int total = opt.StabilityConversions ?? spec["conversions"]!.GetValue<int>(), every = spec["sampleEvery"]!.GetValue<int>();
    var cycle = spec["fixtures"]!.AsArray().Select(x => fixtures.First(f => Name(f) == x!.GetValue<string>())).ToList();
    var csv = new Csv(Path.Combine(opt.Results, "stability.csv"), "library,conversions,pss_mb,processes,median_ms,failures");
    foreach (var (lib, exe) in opt.Adapters)
    {
        using var a = AdapterProcess.Start(lib, exe, logs, clock);
        if (!Supports(a, cycle[0]))
        {
            csv.Row(lib, "", "", "", "", "unsupported");
            continue;
        }
        foreach (var f in cycle) a.Send(ConvertCmd(f, null), convertTimeout);   // warm-up
        var (b0, p0) = ProcessTree.WorkingSet(a.Pid);
        csv.Row(lib, 0, (b0 / 1048576.0).ToString("0"), p0, "", 0);
        int failures = 0;
        var window = new List<double>();
        for (int i = 1; i <= total; i++)
        {
            var r = a.Send(ConvertCmd(cycle[(i - 1) % cycle.Count], null), convertTimeout);
            if (r["ok"]!.GetValue<bool>()) window.Add(r["ms"]!.GetValue<double>()); else failures++;
            if (i % every == 0 || i == total)
            {
                var (b, n) = ProcessTree.WorkingSet(a.Pid);   // one snapshot between conversions: not timed
                window.Sort();
                csv.Row(lib, i, (b / 1048576.0).ToString("0"), n, window.Count > 0 ? window[window.Count / 2].ToString("0") : "", failures);
                Log($"stability {lib}: {i}/{total}, {b / 1048576.0:0} MB, {failures} failed");
                window.Clear();
            }
        }
    }
}

// ---- PDF tools on the neutral inputs in fixtures/inputs: text extraction, search, rasterising, Word to PDF,
// redaction (METHODOLOGY.md, 4.14-4.17). One warm-up call per tool, then one timed call per input.
if (opt.Steps.Contains("tools"))
{
    var spec = manifest["tools"]!;
    string Input(string kind, string name, string ext) => Path.Combine(root, "fixtures", "inputs", kind, name + ext);
    string Out(string lib, params string[] parts) => Path.GetFullPath(Path.Combine(new[] { opt.Pdfs, lib, "tools" }.Concat(parts).ToArray()));
    var csv = new Csv(Path.Combine(opt.Results, "tools_runs.csv"), "library,tool,input,ok,unsupported,ms,result,error");
    TestCertificate.Folder = Path.GetFullPath(Path.Combine(opt.Pdfs, "..", "cert"));
    foreach (var (lib, exe) in opt.Adapters)
    {
        using var a = AdapterProcess.Start(lib, exe, logs, clock);
        void Run(string tool, string input, object cmd, Func<JsonObject, string> result)
        {
            var r = a.Send(cmd, convertTimeout);
            bool ok = r["ok"]!.GetValue<bool>();
            csv.Row(lib, tool, input, ok, r["unsupported"] ?? false, r["ms"], ok ? result(r) : "", r["error"]);
            Log($"tools {lib} {tool} {input}: {(ok ? result(r) : r["unsupported"] != null ? "unsupported" : r["error"])}");
        }
        var extract = spec["extract"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        a.Send(new { op = "extract", file = Input("pdf", "invoice", ".pdf") }, convertTimeout);   // warm-up
        foreach (var n in extract)
            Run("extract", n, new { op = "extract", file = Input("pdf", n, ".pdf"), @out = Out(lib, "extract", n + ".txt") }, r => $"{r["pages"]} pages");
        foreach (var (n, terms) in spec["search"]!.AsObject())
            foreach (var t in terms!.AsArray().Select(x => x!.GetValue<string>()))
                Run("search", $"{n}: {t}", new { op = "search", file = Input("pdf", n, ".pdf"), term = t }, r => r["hits"]!.ToJsonString());
        var raster = spec["rasterize"]!;
        int dpi = raster["dpi"]!.GetValue<int>();
        a.Send(new { op = "rasterize", file = Input("pdf", "invoice", ".pdf"), dpi }, convertTimeout);   // warm-up
        foreach (var n in raster["inputs"]!.AsArray().Select(x => x!.GetValue<string>()))
            Run("rasterize", n, new { op = "rasterize", file = Input("pdf", n, ".pdf"), dpi, @out = Out(lib, "raster", n) }, r => $"{r["pages"]} pages");
        var words = spec["word"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        a.Send(new { op = "word", file = Input("word", words[0], ".docx") }, convertTimeout);   // warm-up
        foreach (var n in words)
            Run("word", n, new { op = "word", file = Input("word", n, ".docx"), @out = Out(lib, "word", n + ".pdf") }, r => $"{r["pages"]} pages");
        var red = spec["redact"]!;
        var redIn = red["input"]!.GetValue<string>();
        var redTerms = red["terms"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Run("redact", redIn, new { op = "redact", file = Input("pdf", redIn, ".pdf"), terms = redTerms, @out = Out(lib, "redact", redIn + ".pdf") },
            r => $"{r["bytes"]} bytes");

        // merge, split, watermark, sign, fill a form, e-invoice (METHODOLOGY.md, 4.18-4.21)
        var mergeIn = spec["merge"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Run("merge", string.Join("+", mergeIn), new { op = "merge", files = mergeIn.Select(n => Input("pdf", n, ".pdf")).ToArray(),
            @out = Out(lib, "merge", "merged.pdf") }, r => $"{r["bytes"]} bytes");
        var split = spec["split"]!;
        var splitIn = split["input"]!.GetValue<string>();
        Run("split", $"{splitIn} {split["from"]}-{split["to"]}", new { op = "split", file = Input("pdf", splitIn, ".pdf"),
            from = split["from"]!.GetValue<int>(), to = split["to"]!.GetValue<int>(), @out = Out(lib, "split", splitIn + ".pdf") }, r => $"{r["bytes"]} bytes");
        var wm = spec["watermark"]!;
        var wmIn = wm["input"]!.GetValue<string>();
        Run("watermark", wmIn, new { op = "watermark", file = Input("pdf", wmIn, ".pdf"), text = wm["text"]!.GetValue<string>(),
            @out = Out(lib, "watermark", wmIn + ".pdf") }, r => $"{r["bytes"]} bytes");
        var signIn = spec["sign"]!["input"]!.GetValue<string>();
        Run("sign", signIn, new { op = "sign", file = Input("pdf", signIn, ".pdf"), pfx = TestCertificate.Pfx, password = TestCertificate.Password,
            @out = Out(lib, "sign", signIn + ".pdf") }, r => $"{r["bytes"]} bytes");
        var fill = spec["fill"]!;
        var fillIn = fill["input"]!.GetValue<string>();
        Run("fill", fillIn, new { op = "fill", file = Input("forms", fillIn, ".pdf"),
            values = fill["values"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
            @out = Out(lib, "fill", fillIn + ".pdf") }, r => $"{r["bytes"]} bytes");
        var inv = spec["einvoice"]!;
        Run("einvoice", "factur-x", new { op = "einvoice", url = $"{opt.Base}/{inv["page"]!.GetValue<string>()}",
            xml = Path.Combine(root, "fixtures", "inputs", inv["xml"]!.GetValue<string>()), @out = Out(lib, "einvoice", "invoice.pdf") },
            r => $"{r["bytes"]} bytes");
    }
}

// ---- memory: its own pass, because sampling a process's memory pauses it briefly and would slow the
// timed steps above (METHODOLOGY.md, 4.3). Per fixture: 3 conversions in a warm process; then the
// throughput fixture at 8 parallel conversions for a fixed time.
if (opt.Steps.Contains("memory"))
{
    var csv = new Csv(Path.Combine(opt.Results, "memory.csv"), "library,fixture,parallel,peak_pss_mb,processes_at_peak");
    var memFixtures = fixtures.Where(f => f["speed"]?.GetValue<bool>() == true).ToList();
    foreach (var (lib, exe) in opt.Adapters)
    {
        using var a = AdapterProcess.Start(lib, exe, logs, clock);
        if (!Supports(a, memFixtures[0]))
        {
            csv.Row(lib, "", "", "", "unsupported");
            continue;
        }
        foreach (var f in memFixtures) a.Send(ConvertCmd(f, null), convertTimeout);   // warm-up
        foreach (var f in memFixtures)
        {
            sampler.Begin(a, Name(f));
            for (int i = 0; i < 3; i++) a.Send(ConvertCmd(f, null), convertTimeout);
            sampler.End(a);
            var (bytes, n) = sampler.Peak(lib, Name(f));
            csv.Row(lib, Name(f), 1, (bytes / 1048576.0).ToString("0"), n);
        }
        foreach (var f in fixtures.Where(f => f["throughput"]?.GetValue<bool>() == true))
        {
            var label = Name(f) + "-x8";
            sampler.Begin(a, label);
            a.Send(new { op = "throughput", url = $"{opt.Base}/{Name(f)}/index.html", media = "screen", waitMs = 0,
                         viewportWidth = viewport, concurrency = 8, seconds = 15.0 }, TimeSpan.FromSeconds(15) + convertTimeout);
            sampler.End(a);
            var (bytes, n) = sampler.Peak(lib, label);
            csv.Row(lib, Name(f), 8, (bytes / 1048576.0).ToString("0"), n);
        }
        Log($"memory {lib}: done");
    }
}
Log("done");

// ---------------------------------------------------------------------------------------------
/// <summary>A throwaway self-signed certificate for the signing test, made once per run and never committed.</summary>
static class TestCertificate
{
    public const string Password = "benchmark";
    /// <summary>Where the certificate is written: next to the run's PDFs (out/ is never committed).</summary>
    public static string? Folder { get; set; }
    static string? pfx;
    public static string Pfx => pfx ??= Create();

    static string Create()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=pdf-benchmarks test signer", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature |
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.NonRepudiation, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var dir = Directory.CreateDirectory(Folder ?? Path.GetTempPath()).FullName;
        var path = Path.Combine(dir, "test-signer.pfx");
        File.WriteAllBytes(path, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, Password));
        File.WriteAllText(Path.Combine(dir, "test-signer.cer.pem"), cert.ExportCertificatePem());
        return path;
    }
}

sealed class Options
{
    public Dictionary<string, string> Adapters { get; } = new();
    public string Base { get; private set; } = "http://127.0.0.1:8777";
    public string Results { get; private set; } = "";
    public string Pdfs { get; private set; } = "";
    public int Rounds { get; private set; } = 9;
    public int Cold { get; private set; } = 5;
    public double Seconds { get; private set; } = 60;
    public HashSet<string> Steps { get; private set; } = new() { "outputs", "standards", "speed", "cold", "throughput", "memory", "parallel", "errors", "stability", "tools" };
    public int? StabilityConversions { get; private set; }
    public HashSet<string> Fixtures { get; private set; } = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string v = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--adapter": var kv = v.Split('=', 2); o.Adapters[kv[0]] = Path.GetFullPath(kv[1]); i++; break;
                case "--base": o.Base = v.TrimEnd('/'); i++; break;
                case "--results": o.Results = v; i++; break;
                case "--pdfs": o.Pdfs = v; i++; break;
                case "--rounds": o.Rounds = int.Parse(v); i++; break;
                case "--cold": o.Cold = int.Parse(v); i++; break;
                case "--seconds": o.Seconds = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
                case "--only": o.Steps = v.Split(',').ToHashSet(); i++; break;
                case "--fixtures": o.Fixtures = v.Split(',').ToHashSet(); i++; break;
                case "--stability": o.StabilityConversions = int.Parse(v); i++; break;
                default: throw new ArgumentException("unknown option " + args[i]);
            }
        }
        if (o.Adapters.Count == 0 || o.Results == "" || o.Pdfs == "")
            throw new ArgumentException("needs --adapter name=exe (one or more), --results and --pdfs");
        return o;
    }

    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "METHODOLOGY.md"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }
}

sealed class Csv
{
    readonly string path;
    public Csv(string path, string header) { this.path = path; File.WriteAllText(path, header + "\n"); }
    public void Row(params object?[] values) =>
        File.AppendAllText(path, string.Join(",", values.Select(Cell)) + "\n");
    static string Cell(object? v)
    {
        string s = v switch
        {
            null => "",
            JsonNode n when n.GetValueKind() == JsonValueKind.Number => n.GetValue<double>().ToString("0.###", CultureInfo.InvariantCulture),
            JsonNode n when n.GetValueKind() == JsonValueKind.String => n.GetValue<string>(),
            JsonNode n => n.ToJsonString(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

static class Env
{
    public static JsonObject Describe(string methodology, Options o) => new()
    {
        ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["methodologyVersion"] = methodology,
        ["os"] = RuntimeInformation.OSDescription,
        ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
        ["cpu"] = Cpu(),
        ["logicalCores"] = Environment.ProcessorCount,
        ["memoryGb"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["rounds"] = o.Rounds,
        ["coldRuns"] = o.Cold,
        ["throughputSeconds"] = o.Seconds,
    };

    static string Cpu()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return File.ReadLines("/proc/cpuinfo").First(l => l.StartsWith("model name")).Split(':', 2)[1].Trim();
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                                                        "ProcessorNameString", "")?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }
}
