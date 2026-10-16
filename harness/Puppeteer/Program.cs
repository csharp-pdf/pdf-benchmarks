// PuppeteerSharp adapter. Free (MIT), HTML to PDF only: Chromium's page.pdf.
//
// Browser: PuppeteerSharp drives a browser it downloads itself. As the README's PDF sample does, the adapter
// calls BrowserFetcher.DownloadAsync() at startup (a no-op when the build is already in the cache). It downloads
// the default browser, Chrome for Testing, and launches it headless (Headless = true: Chrome's new headless mode).
// The fetcher's default cache folder is the process's current directory - the runner starts adapters
// in their build output folder, so every build would hold its own 435 MB copy. The adapter therefore sets the
// documented BrowserFetcherOptions.Path to one shared folder (PUPPETEER_CACHE_DIR, default
// %LOCALAPPDATA%/puppeteer-sharp, ~/.local/share/puppeteer-sharp on Linux) and launches the executable found there.
// The build id and the running browser's version are added to Packages so they land in results/<run>/packages.json.
//
// Lifetime: one browser per adapter process, launched on the first conversion (so a cold conversion includes the
// launch, as it includes engine start-up for the other libraries); every conversion gets its own browser context
// (IBrowser.CreateBrowserContextAsync - "won't share cookies/cache with other browser contexts") and a page in it,
// both closed afterwards. That is Puppeteer's documented way to isolate pages that share a browser.
using System.Text.RegularExpressions;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using PuppeteerSharp.Media;
using PdfBenchmarks;

// stdout carries the protocol: anything the download prints goes to stderr
var cache = Environment.GetEnvironmentVariable("PUPPETEER_CACHE_DIR") is { Length: > 0 } dir ? dir
          : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "puppeteer-sharp");
var fetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = cache });
var installed = await fetcher.DownloadAsync();
Console.Error.WriteLine($"puppeteer: {installed.Browser} {installed.BuildId} at {installed.GetExecutablePath()}");
var executable = installed.GetExecutablePath();
return Adapter.Run(new PuppeteerConverter(installed, executable));

sealed class PuppeteerConverter(InstalledBrowser installed, string executable) : IConverter, IPdfTools, IDisposable
{
    public string Library => "PuppeteerSharp";

    readonly Dictionary<string, string> packages = new(Adapter.PackageVersions())
    {
        // not a NuGet package: the browser the package downloaded (see the header comment)
        ["browser:" + installed.Browser] = installed.BuildId,
    };
    public IReadOnlyDictionary<string, string> Packages => packages;

    // One browser for the process (the documented pattern: launch once, open pages from it).
    Task<IBrowser>? browser;
    readonly object gate = new();
    Task<IBrowser> Browser()
    {
        lock (gate)
        {
            if (browser == null || browser.IsFaulted || browser.IsCanceled)
                browser = Launch();   // a failed launch is retried by the next conversion
            return browser;
        }
    }
    async Task<IBrowser> Launch()
    {
        var b = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = executable });
        var version = await b.GetVersionAsync();
        lock (packages) packages["browser:version"] = version;
        Console.Error.WriteLine("puppeteer: launched " + version);
        return b;
    }

    public void Dispose()
    {
        Task<IBrowser>? b;
        lock (gate) b = browser;
        if (b is { IsCompletedSuccessfully: true })
            b.Result.CloseAsync().GetAwaiter().GetResult();
    }

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) => ConvertAsync(url, s).GetAwaiter().GetResult();

    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) =>
        Render(s, page => page.GoToAsync(url));   // waits for the load event (the default)

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) =>
        Render(s, async page =>
        {
            // SetContentAsync has no base-URL option and writes the HTML into the page's current document
            // (document.open/write), keeping that document's URL. A fresh page is about:blank, so relative URLs would
            // not resolve; with a <base href> they resolve, but the page's origin stays opaque, so the fixture's web
            // fonts (cross-origin requests that need CORS) are refused and fallback fonts are used. The page is
            // therefore first navigated to the base URL and the HTML written into that document: relative URLs and
            // the origin are those of the base URL, as for a page loaded from it. See NOTES.md.
            if (!string.IsNullOrEmpty(baseUrl))
                await page.GoToAsync(baseUrl);
            await page.SetContentAsync(html);
        }).GetAwaiter().GetResult();

    async Task<(byte[] Pdf, int Pages)> Render(RenderSettings s, Func<IPage, Task> load)
    {
        if (s.Standard != null)
            throw new UnsupportedException(Standards.Reason(s));
        if (s.UserPassword != null || s.OwnerPassword != null)
            throw new UnsupportedException("no encryption option in page.pdf");

        var b = await Browser();
        var context = await b.CreateBrowserContextAsync();   // isolated: own cookies, storage and cache
        try
        {
            var page = await context.NewPageAsync();
            await page.SetViewportAsync(new ViewPortOptions { Width = s.ViewportWidth, Height = 768, DeviceScaleFactor = 1 });
            await page.EmulateMediaTypeAsync(s.Print ? MediaType.Print : MediaType.Screen);
            await load(page);
            if (s.WaitMs > 0)
                await Task.Delay(s.WaitMs);   // PuppeteerSharp has no fixed-delay API any more (WaitForTimeoutAsync was removed upstream)
            var pdf = await page.PdfDataAsync(Options(s));
            return (pdf, PdfPages.Count(pdf));
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    static PdfOptions Options(RenderSettings s)
    {
        var o = new PdfOptions
        {
            Format = PaperFormat.A4,
            PrintBackground = true,
            // Tagged keeps its default (true in PuppeteerSharp 25, as in Puppeteer): the file is tagged but
            // claims no standard.
        };
        if (s.Print)
        {
            // paged-media: the page's own @page size and margins, at 100 %
            o.PreferCSSPageSize = true;
        }
        else
        {
            // Zero margins; the page is laid out at ViewportWidth CSS px and scaled to the paper width:
            // A4 width 210 mm = 210 / 25.4 * 96 = 793.70 CSS px, so scale = 793.70 / 1024 = 0.7751.
            o.MarginOptions = new MarginOptions { Top = "0", Bottom = "0", Left = "0", Right = "0" };
            o.Scale = Layout.Scale(s.ViewportWidth);
        }

        if (s.HeaderFooter || s.HeaderHtml != null || s.FooterHtml != null)
        {
            // Chromium draws headerTemplate/footerTemplate inside the top/bottom page margins, so the margins make room
            // (left/right stay 0, so the scale above is unchanged).
            o.DisplayHeaderFooter = true;
            var m = s.HeaderFooter ? "15mm" : "20mm";
            o.MarginOptions = new MarginOptions { Top = m, Bottom = m, Left = "0", Right = "0" };
            o.HeaderTemplate = s.HeaderHtml ?? (s.HeaderFooter ? Layout.TextHeader : "<span></span>");
            o.FooterTemplate = s.FooterHtml != null ? Layout.Tokens(s.FooterHtml)
                             : s.HeaderFooter ? Layout.TextFooter : "<span></span>";
            // s.TemplateBaseUrl: page.pdf has no base URL for templates (see NOTES.md - the header's logo.svg).
        }

        if (s.Bookmarks != null)
        {
            // Chromium's outline is built from the page's headings (h1-h6); it takes no CSS selectors.
            o.Outline = true;
        }

        // s.Forms: page.pdf has no option that turns HTML form controls into PDF form fields; converted as is.
        return o;
    }

    // ---- IPdfTools: PuppeteerSharp renders pages; it has no API for existing PDFs or Word files --------------------

    const string NoPdfApi = "PuppeteerSharp only creates PDFs from pages (page.pdf); no API for existing PDFs";
    public string[] ExtractText(string pdfFile) => throw new UnsupportedException(NoPdfApi);
    public int[] Search(string pdfFile, string term) => throw new UnsupportedException(NoPdfApi);
    public byte[][] Rasterize(string pdfFile, int dpi) => throw new UnsupportedException(NoPdfApi);
    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) => throw new UnsupportedException("no Word (DOCX) input");
    public byte[] Redact(string pdfFile, string[] terms) => throw new UnsupportedException(NoPdfApi);
    public byte[] Merge(string[] pdfFiles) => throw new UnsupportedException(NoPdfApi);
    public byte[] Split(string pdfFile, int from, int to) => throw new UnsupportedException(NoPdfApi);
    public byte[] Watermark(string pdfFile, string text) => throw new UnsupportedException(NoPdfApi);
    public byte[] Sign(string pdfFile, string pfxFile, string password) => throw new UnsupportedException(NoPdfApi);
    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values) => throw new UnsupportedException(NoPdfApi);
    public byte[] EInvoice(string url, string xmlFile) => throw new UnsupportedException("no PDF/A-3 or file attachments in page.pdf");
}

static class Standards
{
    /// <summary>page.pdf offers Tagged and Outline only: no PDF/A, PDF/X or PDF/UA conformance (no claim, no profile).</summary>
    public static string Reason(RenderSettings s) =>
        s.PdfUA != null ? "PDF/UA: page.pdf can tag (Tagged) but makes no PDF/UA claim"
        : s.PdfA != null ? "PDF/A: no option in page.pdf"
        : "PDF/X: no option in page.pdf";
}

static class Layout
{
    /// <summary>A4 printable width at zero margins in CSS px (96 px per inch) divided by the layout width.</summary>
    public static decimal Scale(int viewportWidth) => Math.Round(210m / 25.4m * 96m / viewportWidth, 4);

    // Chromium's template classes: pageNumber, totalPages (documented on PdfOptions.HeaderTemplate). Templates do
    // not see the page's styles and default to a very small font, so they carry their own.
    public const string TextHeader =
        "<div style=\"font-family:Helvetica,Arial,sans-serif;font-size:10px;width:100%;text-align:center\">" + RenderSettings.HeaderText + "</div>";
    public const string TextFooter =
        "<div style=\"font-family:Helvetica,Arial,sans-serif;font-size:10px;width:100%;text-align:center\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>";

    /// <summary>The benchmark's {page}/{pages} tokens as Chromium's placeholder elements.</summary>
    public static string Tokens(string html) =>
        html.Replace("{pages}", "<span class=\"totalPages\"></span>").Replace("{page}", "<span class=\"pageNumber\"></span>");
}

static class PdfPages
{
    /// <summary>
    /// page.pdf returns bytes only, no page count. Chromium (Skia) writes every page object uncompressed, so the
    /// count is the page tree root's /Count. Measurement only - not part of the library's output.
    /// </summary>
    public static int Count(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var counts = Regex.Matches(text, @"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)|/Count\s+(\d+)[^>]*?/Type\s*/Pages\b");
        if (counts.Count > 0)
            return counts.Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).Max();
        return Regex.Matches(text, @"/Type\s*/Page\b").Count;
    }
}
