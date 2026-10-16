// Playwright for .NET adapter. Free (the .NET package is MIT; the Node driver it bundles is Apache-2.0).
// HTML to PDF only: Chromium's page.pdf ("Generating a pdf is currently only supported in Chromium headless").
//
// Browser: Playwright does not ship browsers in the NuGet package; they are installed with `playwright install`
// (https://playwright.dev/dotnet/docs/browsers). At startup the adapter runs the documented install through the
// .NET API - Program.RunWithResult(["install", "chromium"]), the output-capturing twin of Program.Main, so the
// installer's console output cannot corrupt the protocol on stdout. It is a no-op when the pinned build is already
// in %LOCALAPPDATA%\ms-playwright (~/.cache/ms-playwright on Linux). `install chromium` fetches Chrome for Testing
// and Chrome Headless Shell; a headless launch uses the headless shell (Playwright's default). The running
// browser's version is added to Packages so it lands in results/<run>/packages.json.
//
// Lifetime: one IPlaywright + one browser per adapter process, launched on the first conversion; every conversion
// gets a new browser context (IBrowser.NewContextAsync - "Create a new incognito browser context"; "It won't share
// cookies/cache with other browser contexts"), closed afterwards, as Playwright's docs recommend.
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PdfBenchmarks;

var install = new Microsoft.Playwright.Program().RunWithResult(["install", "chromium"]);
Console.Error.Write(install.StandardOutput);
Console.Error.Write(install.StandardError);
if (install.ExitCode != 0)
{
    Console.Error.WriteLine($"playwright install chromium failed with exit code {install.ExitCode}");
    return 2;
}
return Adapter.Run(new PlaywrightConverter());

sealed class PlaywrightConverter : IConverter, IPdfTools, IDisposable
{
    public string Library => "Playwright";

    readonly Dictionary<string, string> packages = new(Adapter.PackageVersions());
    public IReadOnlyDictionary<string, string> Packages => packages;

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
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var b = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        lock (packages) packages["browser:chromium"] = b.Version;   // not a NuGet package: the browser that ran
        Console.Error.WriteLine("playwright: launched chromium " + b.Version);
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
        Render(s, page => page.GotoAsync(url));   // waits for the load event (the default)

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
                await page.GotoAsync(baseUrl);
            await page.SetContentAsync(html);
        }).GetAwaiter().GetResult();

    async Task<(byte[] Pdf, int Pages)> Render(RenderSettings s, Func<IPage, Task> load)
    {
        if (s.Standard != null)
            throw new UnsupportedException(Standards.Reason(s));
        if (s.UserPassword != null || s.OwnerPassword != null)
            throw new UnsupportedException("no encryption option in page.pdf");

        var b = await Browser();
        var context = await b.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = s.ViewportWidth, Height = 768 },
            DeviceScaleFactor = 1,
        });
        try
        {
            var page = await context.NewPageAsync();
            // "By default, page.pdf() generates a pdf with print css media. To generate a pdf with screen media,
            // call Page.EmulateMediaAsync() first."
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = s.Print ? Media.Print : Media.Screen });
            await load(page);
            if (s.WaitMs > 0)
                await Task.Delay(s.WaitMs);   // WaitForTimeoutAsync exists but is documented as "for debugging" only
            var pdf = await page.PdfAsync(Options(s));
            return (pdf, PdfPages.Count(pdf));
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    static PagePdfOptions Options(RenderSettings s)
    {
        var o = new PagePdfOptions
        {
            Format = "A4",
            PrintBackground = true,
            // Tagged keeps its default (false in Playwright).
        };
        if (s.Print)
        {
            o.PreferCSSPageSize = true;   // paged-media: the page's own @page size and margins, at 100 %
        }
        else
        {
            // Zero margins; laid out at ViewportWidth CSS px and scaled to the paper width:
            // A4 width 210 mm = 210 / 25.4 * 96 = 793.70 CSS px, so scale = 793.70 / 1024 = 0.7751.
            o.Margin = new Margin { Top = "0", Bottom = "0", Left = "0", Right = "0" };
            o.Scale = Layout.Scale(s.ViewportWidth);
        }

        if (s.HeaderFooter || s.HeaderHtml != null || s.FooterHtml != null)
        {
            // Chromium draws the templates inside the top/bottom margins, so the margins make room (left/right stay 0,
            // so the scale is unchanged).
            o.DisplayHeaderFooter = true;
            var m = s.HeaderFooter ? "15mm" : "20mm";
            o.Margin = new Margin { Top = m, Bottom = m, Left = "0", Right = "0" };
            o.HeaderTemplate = s.HeaderHtml ?? (s.HeaderFooter ? Layout.TextHeader : "<span></span>");
            o.FooterTemplate = s.FooterHtml != null ? Layout.Tokens(s.FooterHtml)
                             : s.HeaderFooter ? Layout.TextFooter : "<span></span>";
            // s.TemplateBaseUrl: page.pdf has no base URL for templates (see NOTES.md - the header's logo.svg).
        }

        if (s.Bookmarks != null)
        {
            // Built from the page's headings (h1-h6); Chromium takes no CSS selectors. Chromium builds the outline from
            // the tagged structure, so Outline needs Tagged (Playwright's Tagged defaults to false).
            o.Outline = true;
            o.Tagged = true;
        }

        // s.Forms: page.pdf has no option that turns HTML form controls into PDF form fields; converted as is.
        return o;
    }

    // ---- IPdfTools: Playwright renders pages; it has no API for existing PDFs or Word files ---------------------------

    const string NoPdfApi = "Playwright only creates PDFs from pages (page.pdf); no API for existing PDFs";
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
    public static float Scale(int viewportWidth) => (float)Math.Round(210.0 / 25.4 * 96.0 / viewportWidth, 4);

    // Chromium's template classes: pageNumber, totalPages (documented on PagePdfOptions.HeaderTemplate). Templates do
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
    /// page.pdf returns bytes only, no page count. Chromium (Skia) writes the page tree uncompressed, so the count is
    /// the page tree root's /Count. Measurement only - not part of the library's output.
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
