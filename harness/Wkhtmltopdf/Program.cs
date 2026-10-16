// wkhtmltopdf adapter, through Haukcode.WkHtmlToPdfDotNet (a maintained fork of DinkToPdf that ships the native
// libwkhtmltox in its NuGet package; NOTES.md explains the choice). wkhtmltopdf itself is archived upstream
// (https://github.com/wkhtmltopdf/wkhtmltopdf) and renders with Qt WebKit.
//
// Threads: libwkhtmltox is not thread-safe. The package documents SynchronizedConverter, registered as a singleton,
// for multi-threaded applications and web servers: every conversion is queued and run on one dedicated thread
// (https://github.com/HakanL/WkHtmlToPdf-DotNet#synchronized-converter). Parallel conversions therefore wait for
// each other - that is the library's behaviour, measured as it is.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using PdfBenchmarks;
using WkHtmlToPdfDotNet;
using IDocument = WkHtmlToPdfDotNet.Contracts.IDocument;

return Adapter.Run(new WkHtmlToPdfConverter());

sealed class WkHtmlToPdfConverter : PdfBenchmarks.IConverter
{
    // The one converter for the process, as the package documents (AddSingleton(new SynchronizedConverter(new PdfTools()))).
    static readonly SynchronizedConverter Converter = CreateConverter();
    // Error messages wkhtmltopdf reported for each document being converted (the Error event names the document).
    static readonly ConcurrentDictionary<IDocument, ConcurrentQueue<string>> Errors = new();

    static SynchronizedConverter CreateConverter()
    {
        var c = new SynchronizedConverter(new PdfTools());
        c.Error += (_, e) => Errors.GetOrAdd(e.Document, _ => new()).Enqueue(e.Message);
        return c;
    }

    public string Library => "wkhtmltopdf";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    /// <summary>A4 width in CSS pixels at wkhtmltopdf's 96 pixels per inch with smart shrinking off (210 mm / 25.4 * 96).</summary>
    const double A4WidthPx = 210 / 25.4 * 96;

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) =>
        Run(s, new ObjectSettings { Page = url });

    // The package has no asynchronous API (IConverter.Convert is synchronous; SynchronizedConverter queues work
    // on its own thread and blocks the caller). The async path runs the synchronous call on the thread pool.
    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) =>
        Task.Run(() => Convert(url, s));

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) =>
        // ObjectSettings.HtmlContent passes the HTML to wkhtmltopdf directly. wkhtmltopdf has no base-URL option,
        // so relative URLs resolve through a <base> element added to the page.
        Run(s, new ObjectSettings { HtmlContent = WithBase(html, baseUrl) });

    static (byte[] Pdf, int Pages) Run(RenderSettings s, ObjectSettings page)
    {
        if (s.Standard != null)
            throw new UnsupportedException("no PDF/A, PDF/UA or PDF/X output");
        if (s.UserPassword != null || s.OwnerPassword != null)
            throw new UnsupportedException("no PDF encryption");

        var temp = new List<string>();
        var doc = new HtmlToPdfDocument
        {
            GlobalSettings =
            {
                PaperSize = PaperKind.A4,
                Orientation = Orientation.Portrait,
            },
        };
        page.WebSettings.PrintMediaType = s.Print;   // screen media is wkhtmltopdf's default (--no-print-media-type)
        if (!s.Print)
        {
            // wkhtmltopdf has no layout-width option: --viewport-size only sets the window size scripts see, and the
            // package documents ViewportSize as "not supported in wkhtmltopdf API since v0.12.2.4". The width a page
            // is laid out at is the paper width divided by the zoom, once smart shrinking (which "makes the
            // pixel/dpi ratio non-constant") is disabled. The package formats ZoomFactor with two decimals
            // ("0.##"), so the zoom is rounded down: 0.77 lays A4 out at 1030 px for a 1024 px viewport (measured), so
            // nothing 1024 px wide is cut off.
            page.WebSettings.EnableIntelligentShrinking = false;
            page.LoadSettings.ZoomFactor = Math.Floor(A4WidthPx / s.ViewportWidth * 100) / 100;
            doc.GlobalSettings.Margins = new MarginSettings(0, 0, 0, 0) { Unit = Unit.Millimeters };
        }
        // Print media: margins and layout are left to wkhtmltopdf's defaults, as the other adapters leave them to
        // the page's @page rule.
        if (s.WaitMs > 0)
            page.LoadSettings.JSDelay = s.WaitMs;   // --javascript-delay (default 200 ms)

        if (s.HeaderFooter)
        {
            // Text headers and footers are drawn in the page margins; [page] and [topage] are wkhtmltopdf's variables.
            doc.GlobalSettings.Margins = new MarginSettings { Top = 15, Bottom = 15, Left = 0, Right = 0, Unit = Unit.Millimeters };
            page.HeaderSettings.Center = RenderSettings.HeaderText;
            page.HeaderSettings.FontSize = 10;
            page.FooterSettings.Center = "Page [page] of [topage]";
            page.FooterSettings.FontSize = 10;
        }

        if (s.HeaderHtml != null || s.FooterHtml != null)
        {
            // HTML headers and footers are separate HTML documents (--header-html / --footer-html, a URL or file).
            // wkhtmltopdf passes the page variables in the query string; the manual's script copies them into
            // elements with the matching class, so {page} and {pages} become <span class="page"> / <span class="topage">.
            doc.GlobalSettings.Margins = new MarginSettings { Top = 20, Bottom = 20, Left = 0, Right = 0, Unit = Unit.Millimeters };
            if (s.HeaderHtml != null)
                page.HeaderSettings.HtmlUrl = TemplateFile(s.HeaderHtml, s.TemplateBaseUrl, temp);
            if (s.FooterHtml != null)
                page.FooterSettings.HtmlUrl = TemplateFile(s.FooterHtml, s.TemplateBaseUrl, temp);
        }

        if (s.Bookmarks != null)
        {
            // wkhtmltopdf's outline is built from the h1-h6 headings; it cannot take CSS selectors. The depth is
            // limited to the deepest heading asked for.
            var levels = s.Bookmarks.Select(b => Regex.Match(b.Trim(), "^[hH]([1-6])$")).ToArray();
            if (levels.Any(m => !m.Success))
                throw new UnsupportedException("outline from CSS selectors (only h1-h6 headings)");
            doc.GlobalSettings.Outline = true;
            doc.GlobalSettings.OutlineDepth = levels.Max(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            page.IncludeInOutline = true;
        }

        if (s.Forms)
            page.ProduceForms = true;   // --enable-forms

        doc.Objects.Add(page);
        try
        {
            var pdf = Converter.Convert(doc);
            Errors.TryRemove(doc, out var errors);
            if (pdf.Length == 0)
                throw new InvalidOperationException("wkhtmltopdf produced no PDF" +
                    (errors is { IsEmpty: false } ? ": " + string.Join("; ", errors) : ""));
            return (pdf, CountPages(pdf));
        }
        finally
        {
            Errors.TryRemove(doc, out _);
            temp.ForEach(File.Delete);
        }
    }

    // The HTML template wrapped in the substitution page from the wkhtmltopdf manual ("Footers And Headers",
    // https://wkhtmltopdf.org/usage/wkhtmltopdf.txt), written to a temporary file.
    static string TemplateFile(string template, string? baseUrl, List<string> temp)
    {
        var body = template.Replace("{pages}", "<span class=\"topage\"></span>").Replace("{page}", "<span class=\"page\"></span>");
        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">{{(baseUrl != null ? $"<base href=\"{baseUrl}\">" : "")}}<script>
            function subst() {
                var vars = {};
                var query_strings_from_url = document.location.search.substring(1).split('&');
                for (var query_string in query_strings_from_url) {
                    if (query_strings_from_url.hasOwnProperty(query_string)) {
                        var temp_var = query_strings_from_url[query_string].split('=', 2);
                        vars[temp_var[0]] = decodeURI(temp_var[1]);
                    }
                }
                var css_selector_classes = ['page', 'frompage', 'topage', 'webpage', 'section', 'subsection', 'date', 'isodate', 'time', 'title', 'doctitle', 'sitepage', 'sitepages'];
                for (var css_class in css_selector_classes) {
                    if (css_selector_classes.hasOwnProperty(css_class)) {
                        var element = document.getElementsByClassName(css_selector_classes[css_class]);
                        for (var j = 0; j < element.length; ++j) {
                            element[j].textContent = vars[css_selector_classes[css_class]];
                        }
                    }
                }
            }
            </script></head><body style="border:0; margin: 0;" onload="subst()">
            {{body}}
            </body></html>
            """;
        var path = Path.Combine(Path.GetTempPath(), "wkhtmltopdf-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(path, html);
        temp.Add(path);
        return path;
    }

    static string WithBase(string html, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return html;
        var tag = $"<base href=\"{baseUrl}\">";
        var head = Regex.Match(html, "<head[^>]*>", RegexOptions.IgnoreCase);
        return head.Success ? html.Insert(head.Index + head.Length, tag) : tag + html;
    }

    /// <summary>
    /// The package returns only the bytes. wkhtmltopdf (Qt's PDF writer) writes every page as a plain
    /// "/Type /Page" object - no object streams - so the page objects are counted in the bytes.
    /// </summary>
    static int CountPages(byte[] pdf) =>
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![a-zA-Z])").Count;
}
