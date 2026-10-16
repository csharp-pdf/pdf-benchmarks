// Docotic.Pdf adapter: the Docotic.Pdf core library, its free HtmlToPdf add-on (HTML to PDF through Chromium) and its
// free Conformance add-on (PDF/A). Docotic.Pdf needs a licence key - a free 31-day key works - in DOCOTIC_LICENSE_KEY:
// "Without a license, the library will not create or read PDF documents" (package README). Its trial adds no watermark.
//
// Browser: HtmlConverter.CreateAsync looks for its pinned Chromium revision in HtmlEngineOptions.Path and downloads it
// from storage.googleapis.com when missing. The default path is ".local-chromium" under the current directory; the
// runner starts adapters in their build output folder, so every build would hold its own copy (as with PuppeteerSharp).
// The adapter therefore sets the documented Path to one shared folder: DOCOTIC_CHROMIUM_DIR, default
// %LOCALAPPDATA%/docotic-chromium (~/.local/share/docotic-chromium on Linux).
//
// Lifetime: one HtmlConverter per adapter process, created on the first conversion (so a cold conversion includes the
// Chromium start, as for the other browser-based libraries) and used for every conversion, also concurrently. The
// converter is an IDisposable object with conversion methods, the documented pattern being "create a converter and use
// it to generate PDFs"; Bit Miracle's pages do not say whether to create one per conversion (their samples convert once).
using System.Text.RegularExpressions;
using BitMiracle.Docotic;
using BitMiracle.Docotic.Pdf;
using BitMiracle.Docotic.Pdf.Conformance;
using BitMiracle.Docotic.Pdf.HtmlToPdf;
using PdfBenchmarks;

var key = Environment.GetEnvironmentVariable("DOCOTIC_LICENSE_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Set DOCOTIC_LICENSE_KEY to a Docotic.Pdf licence key (a free time-limited key works).");
    return 2;
}
LicenseManager.AddLicenseData(key.Trim());
if (!LicenseManager.HasValidLicense)   // the check Bit Miracle recommends right after AddLicenseData
{
    Console.Error.WriteLine("DOCOTIC_LICENSE_KEY is not a valid Docotic.Pdf licence (expired trial key?).");
    return 2;
}
return Adapter.Run(new DocoticConverter());

sealed class DocoticConverter : IConverter, IPdfTools, IDisposable
{
    public string Library => "Docotic.Pdf";

    readonly Dictionary<string, string> packages = new(Adapter.PackageVersions())
    {
        // not a NuGet package: the Chromium revision the HtmlToPdf add-on downloads and runs
        ["chromium:revision"] = HtmlEngineOptions.ChromiumRevision.ToString(),
    };
    public IReadOnlyDictionary<string, string> Packages => packages;

    static readonly HttpClient Http = new();

    // ---- the converter: one per process ------------------------------------------------------------------------

    Task<HtmlConverter>? converter;
    readonly object gate = new();
    Task<HtmlConverter> Converter()
    {
        lock (gate)
        {
            if (converter == null || converter.IsFaulted || converter.IsCanceled)
            {
                converter = HtmlConverter.CreateAsync(EngineOptions());   // a failed start is retried by the next conversion
                // The reply carries only the outer "Failed to create a converter."; the cause goes to stderr (the runner's log).
                converter.ContinueWith(t =>
                {
                    for (Exception? e = t.Exception?.InnerException; e != null; e = e.InnerException)
                        Console.Error.WriteLine("docotic: " + e.GetType().FullName + ": " + e.Message);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            return converter;
        }
    }

    public void Dispose()
    {
        Task<HtmlConverter>? c;
        lock (gate) c = converter;
        if (c is { IsCompletedSuccessfully: true })
            c.Result.Dispose();   // closes the Chromium it started
    }

    static HtmlEngineOptions EngineOptions()
    {
        var path = Environment.GetEnvironmentVariable("DOCOTIC_CHROMIUM_DIR") is { Length: > 0 } dir ? dir
                 : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                              // without Create .NET returns "" when the folder does not exist yet (a fresh
                                              // container user), and the relative path would point into the read-only app folder
                                              Environment.SpecialFolderOption.Create), "docotic-chromium");
        var o = new HtmlEngineOptions { Path = path };
        // Bit Miracle's Docker / Azure-on-Linux guide: containers need the Chromium sandbox off (UseSandbox = false).
        // Set only for the documented Docker image (deployment/docotic/documented.Dockerfile); default (true) elsewhere.
        if (Environment.GetEnvironmentVariable("DOCOTIC_DOCKER_DOCUMENTED") == "1")
            o.UseSandbox = false;
        return o;
    }

    // ---- HTML to PDF -------------------------------------------------------------------------------------------

    // The add-on is asynchronous only. Its documented way to call it from synchronous code is
    // Task.Run(async () => ...).GetAwaiter().GetResult() ("Using the async API in synchronous code",
    // https://bitmiracle.com/pdf-library/html-pdf/convert), which is what the sync path does.
    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) =>
        Task.Run(() => ConvertAsync(url, s)).GetAwaiter().GetResult();

    public async Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s)
    {
        var options = await Options(s);
        var c = await Converter();
        using var pdf = await c.CreatePdfAsync(new Uri(url), options);
        return Finish(pdf, s);
    }

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) =>
        Task.Run(async () =>
        {
            var options = await Options(s);
            if (!string.IsNullOrEmpty(baseUrl))
                options.Load.BaseUri = new Uri(baseUrl);   // the documented base URL for an HTML string
            var c = await Converter();
            using var pdf = await c.CreatePdfFromStringAsync(html, options);
            return Finish(pdf, s);
        }).GetAwaiter().GetResult();

    static async Task<HtmlConversionOptions> Options(RenderSettings s)
    {
        Check(s);
        var o = new HtmlConversionOptions();
        o.Page.SetSize(PdfPaperSize.A4, false);   // the default (A4, portrait), set explicitly
        // Margins default to 0 ("There are no margins and no magnification"); KeepBackground defaults to true.
        if (s.Print)
        {
            o.MediaType = CssMediaType.Print;   // paged-media: the page's @page margins at 100 %
        }
        else
        {
            o.MediaType = CssMediaType.Screen;
            // No viewport option: the documented way to fit a wide layout is Page.Scale ("scale the content down to fit
            // the PDF page"). A4 at zero margins is 210 / 25.4 * 96 = 793.70 CSS px, so the page is laid out at
            // ViewportWidth px with scale 793.70 / 1024 = 0.7751 - the same as the Chromium-automation adapters.
            o.Page.Scale = Scale(s.ViewportWidth);
        }
        if (s.WaitMs > 0)
            o.Start.SetStartAfterDelay(s.WaitMs);   // "Delay conversion start"

        if (s.HeaderFooter || s.HeaderHtml != null || s.FooterHtml != null)
        {
            // The templates are drawn inside the top/bottom margins ("You might want to specify a top margin ... Without
            // the margin, the header may be obscured"); left/right stay 0, so the scale above is unchanged. Points.
            var margin = (int)Math.Round((s.HeaderFooter ? 15 : 20) * 72 / 25.4);   // whole points: the add-on takes int
            o.Page.MarginTop = o.Page.MarginBottom = margin;
            o.Page.HeaderTemplate = s.HeaderHtml != null ? await Inline(s.HeaderHtml, s.TemplateBaseUrl)
                                  : s.HeaderFooter ? TextHeader : "<span></span>";
            o.Page.FooterTemplate = s.FooterHtml != null ? Tokens(await Inline(s.FooterHtml, s.TemplateBaseUrl))
                                  : s.HeaderFooter ? TextFooter : "<span></span>";
        }

        if (s.PdfA != null && s.NeedsTags)
            // "The PreserveStructureInformation = true option helps produce accessible documents. This is important for
            // PDF/A-1a, PDF/A-2a, or PDF/A-3a conformance levels." (https://bitmiracle.com/pdf-library/conformance/pdf-to-pdfa)
            o.PreserveStructureInformation = true;
        // FillMetadata keeps its default (true): the title comes from the page's <title>.
        return o;
    }

    /// <summary>Options the add-on and the core library have no API for.</summary>
    static void Check(RenderSettings s)
    {
        if (s.Bookmarks != null)
            throw new UnsupportedException("bookmarks: HtmlConversionOptions has no outline option, and the core library has no bookmarks-from-headings API");
        if (s.Forms)
            throw new UnsupportedException("HTML forms to PDF fields: no option in HtmlConversionOptions");
        if (s.PdfUA != null)
            throw new UnsupportedException("PDF/UA: PreserveStructureInformation tags the file, but no API produces or declares PDF/UA");
        if (s.PdfX != null)
            throw new UnsupportedException("PDF/X: the Conformance add-on only reads PDF/X conformance (ReadPdfxConformance); no PDF/X output");
        if (s.PdfA != null && (s.UserPassword != null || s.OwnerPassword != null))
            throw new UnsupportedException("PDF/A with encryption (PDF/A forbids encryption)");
    }

    static (byte[] Pdf, int Pages) Finish(PdfDocument pdf, RenderSettings s)
    {
        var output = new MemoryStream();
        if (s.PdfA != null)
        {
            pdf.Info.Title = s.Title;
            // The Conformance add-on: "detects and fixes PDF/A compliance issues for the specified conformance level"
            pdf.SaveAsPdfa(output, s.PdfA switch
            {
                "1a" => PdfaConformanceLevel.Pdfa1A,
                "1b" => PdfaConformanceLevel.Pdfa1B,
                "2a" => PdfaConformanceLevel.Pdfa2A,
                "2b" => PdfaConformanceLevel.Pdfa2B,
                "2u" => PdfaConformanceLevel.Pdfa2U,
                "3a" => PdfaConformanceLevel.Pdfa3A,
                "3b" => PdfaConformanceLevel.Pdfa3B,
                "3u" => PdfaConformanceLevel.Pdfa3U,
                "4" => PdfaConformanceLevel.Pdfa4,
                "4e" => PdfaConformanceLevel.Pdfa4E,
                "4f" => PdfaConformanceLevel.Pdfa4F,
                _ => throw new UnsupportedException("PDF/A-" + s.PdfA),
            });
        }
        else if (s.UserPassword != null || s.OwnerPassword != null)
        {
            // Standard security handler; the algorithm keeps its default (AES 128-bit). No user permissions: printing
            // and copying (and everything else) are denied to a user who opens the file with the user password.
            var handler = new PdfStandardEncryptionHandler(s.OwnerPassword ?? "", s.UserPassword ?? "");
            handler.UserPermissions.Flags = PdfPermissionFlags.None;
            pdf.Save(output, new PdfSaveOptions { EncryptionHandler = handler });
        }
        else
        {
            pdf.Save(output);
        }
        return (output.ToArray(), pdf.PageCount);
    }

    static double Scale(int viewportWidth) => Math.Round(210.0 / 25.4 * 96.0 / viewportWidth, 4);

    // Template classes the add-on fills in: pageNumber, totalPages (PdfPageOptions.HeaderTemplate). Templates do not
    // see the page's styles, so they carry their own ("We recommend using inline styles").
    const string TextHeader =
        "<div style=\"font-family:Helvetica,Arial,sans-serif;font-size:10px;width:100%;text-align:center\">" + RenderSettings.HeaderText + "</div>";
    const string TextFooter =
        "<div style=\"font-family:Helvetica,Arial,sans-serif;font-size:10px;width:100%;text-align:center\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>";

    /// <summary>The benchmark's {page}/{pages} tokens as the add-on's placeholder elements.</summary>
    static string Tokens(string html) =>
        html.Replace("{pages}", "<span class=\"totalPages\"></span>").Replace("{page}", "<span class=\"pageNumber\"></span>");

    /// <summary>
    /// The templates have no base URL, and "The conversion engine won't wait for external resources to load ... Data
    /// URIs are recommended for images" (PdfPageOptions.HeaderTemplate). As documented, images referenced by a relative
    /// URL are fetched from the template's base URL and inlined as data URIs (inside the timed conversion).
    /// </summary>
    static async Task<string> Inline(string html, string? baseUrl)
    {
        if (baseUrl == null) return html;
        var result = html;
        foreach (Match m in Regex.Matches(html, "<img\\b[^>]*\\bsrc=\"(?![a-z]+:)([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var src = m.Groups[1].Value;
            using var response = await Http.GetAsync(new Uri(new Uri(baseUrl), src));
            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var data = System.Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync());
            result = result.Replace($"src=\"{src}\"", $"src=\"data:{type};base64,{data}\"");
        }
        return result;
    }

    // ---- IPdfTools ---------------------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile)
    {
        using var pdf = new PdfDocument(pdfFile);
        return pdf.Pages.Select(p => p.GetText()).ToArray();   // plain text, the default extraction
    }

    // Docotic.Pdf has no text-search API. Its "Find and highlight text" sample implements phrase search itself
    // (~200 lines over PdfPage.GetWords: grouping, sorting and matching words), so - as for IronPDF - this is
    // recorded as not supported rather than reimplemented here.
    public int[] Search(string pdfFile, string term) =>
        throw new UnsupportedException("no text search API (the 'Find and highlight text' sample builds one on PdfPage.GetWords)");

    public byte[][] Rasterize(string pdfFile, int dpi)
    {
        using var pdf = new PdfDocument(pdfFile);
        var options = PdfDrawOptions.Create();   // PNG
        options.HorizontalResolution = options.VerticalResolution = dpi;
        options.BackgroundColor = new PdfRgbColor(255, 255, 255);   // as Bit Miracle's samples; the default is transparent
        return pdf.Pages.Select(p =>
        {
            using var ms = new MemoryStream();
            p.Save(ms, options);
            return ms.ToArray();
        }).ToArray();
    }

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) =>
        throw new UnsupportedException("no Word (DOCX) input in Docotic.Pdf or its add-ons");

    // Docotic.Pdf has no redaction API. AddRedactionAnnotation only marks an area ("identifies content that is intended
    // to be removed"); nothing applies it. The "Redact PDF" guide (https://bitmiracle.com/pdf-library/edit/#redact-text)
    // draws rectangles over words and notes that "the text behind the rectangles remains in the document"; to remove
    // text it points to combining two sample programs (find words, then rebuild the page content without them).
    public byte[] Redact(string pdfFile, string[] terms) =>
        throw new UnsupportedException("no redaction API (redaction annotations are not applied; the guide combines two sample programs)");

    public byte[] Merge(string[] pdfFiles)
    {
        // the "Merge documents" sample: open the first, Append the others
        using var pdf = new PdfDocument(pdfFiles[0]);
        foreach (var f in pdfFiles.Skip(1))
            pdf.Append(f);
        return Save(pdf);
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        using var pdf = new PdfDocument(pdfFile);
        using var part = pdf.CopyPages(from - 1, to - from + 1);   // (index, count); the source stays unchanged
        part.RemoveUnusedResources();   // as the "Extract pages" sample: drop resources the copied pages do not use
        return Save(part);
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        // The "Watermarks" sample: the text drawn on an XObject with TextAngle, the XObject drawn over every page.
        using var pdf = new PdfDocument(pdfFile);
        var font = pdf.CreateFont(PdfBuiltInFont.HelveticaBold);
        foreach (var page in pdf.Pages)
        {
            var watermark = pdf.CreateXObject();
            watermark.Width = page.Width;
            watermark.Height = page.Height;
            var c = watermark.Canvas;
            c.Font = font;
            c.FontSize = 72;
            c.Brush.Color = new PdfRgbColor(200, 0, 0);
            c.Brush.Opacity = 35;
            c.TextAngle = 45;   // counter-clockwise: a rising diagonal
            // centre the rotated line on the page (y grows downwards in Docotic's page coordinates)
            double w = c.GetTextWidth(text), h = c.GetTextHeight(), cos = Math.Cos(Math.PI / 4), sin = Math.Sin(Math.PI / 4);
            double x = page.Width / 2 - w / 2 * cos - h / 2 * sin, y = page.Height / 2 + w / 2 * sin - h / 2 * cos;
            c.DrawString(x, y, text);
            page.Canvas.DrawXObject(watermark, 0, 0);
        }
        return Save(pdf);
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        // The "Sign document" sample: PKCS#7 detached, SHA-256 (the sample's choice; the property default is SHA-1).
        // No field given: the library creates an invisible signature field on the first page.
        using var pdf = new PdfDocument(pdfFile);
        var options = new PdfSigningOptions(pfxFile, password)
        {
            DigestAlgorithm = PdfDigestAlgorithm.Sha256,
            Format = PdfSignatureFormat.Pkcs7Detached,
            Reason = "pdf-benchmarks",
        };
        var output = new MemoryStream();
        pdf.SignAndSave(options, output);
        return output.ToArray();
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        // The "Fill form" sample: enumerate GetControls and set each typed control
        using var pdf = new PdfDocument(pdfFile);
        var found = new HashSet<string>();
        foreach (var control in pdf.GetControls())
        {
            var name = control.FullName ?? control.Name;
            if (name == null || !values.TryGetValue(name, out var value)) continue;
            found.Add(name);
            switch (control)
            {
                case PdfTextBox t: t.Text = value; break;
                case PdfCheckBox cb: cb.Checked = value == "true"; break;
                case PdfComboBox combo: combo.Text = value; break;
                case PdfRadioButton r: r.Checked = r.ExportValue == value; break;   // one control per button of the group
                default: throw new InvalidOperationException($"field {name}: unexpected control {control.GetType().Name}");
            }
        }
        var missing = values.Keys.Except(found).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("fields not found: " + string.Join(", ", missing));
        return Save(pdf);
    }

    public byte[] EInvoice(string url, string xmlFile)
    {
        // Bit Miracle's "Create ZUGFeRD / Factur-X files in C#" (https://bitmiracle.com/pdf-library/conformance/pdf-to-pdfa#zugferd):
        // the XML as a shared attachment with Relationship "Alternative", the fx: XMP schema, then SaveAsPdfa - here
        // PDF/A-3b and the Factur-X EN 16931 values instead of the guide's XRechnung ones. The page is rendered with the
        // add-on's defaults (A4, no margins).
        return Task.Run(async () =>
        {
            var c = await Converter();
            using var pdf = await c.CreatePdfAsync(new Uri(url));
            var xml = pdf.CreateFileAttachment(xmlFile);
            xml.Relationship = "Alternative";
            pdf.SharedAttachments.Add(xml);
            var fx = new XmpSchema("fx", "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#");
            fx.Properties.Add(new XmpProperty("DocumentType", new XmpString("INVOICE")));
            fx.Properties.Add(new XmpProperty("DocumentFileName", new XmpString(Path.GetFileName(xmlFile))));
            fx.Properties.Add(new XmpProperty("Version", new XmpString("1.0")));
            fx.Properties.Add(new XmpProperty("ConformanceLevel", new XmpString("EN 16931")));
            pdf.Metadata.Schemas.Add(fx);
            var output = new MemoryStream();
            pdf.SaveAsPdfa(output, PdfaConformanceLevel.Pdfa3B);
            return output.ToArray();
        }).GetAwaiter().GetResult();
    }

    static byte[] Save(PdfDocument pdf)
    {
        var output = new MemoryStream();
        pdf.Save(output);
        return output.ToArray();
    }
}
