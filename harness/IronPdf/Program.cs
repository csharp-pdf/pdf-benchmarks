// IronPDF adapter. IronPDF renders without a key but will not return the PDF bytes, so it needs a
// licence key - a free trial key works - in the IRONPDF_LICENSE_KEY environment variable.
using IronPdf;
using IronPdf.Rendering;
using PdfBenchmarks;

var key = Environment.GetEnvironmentVariable("IRONPDF_LICENSE_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Set IRONPDF_LICENSE_KEY to an IronPDF licence key (a free trial key works).");
    return 2;
}
License.LicenseKey = key;
if (Environment.GetEnvironmentVariable("IRONPDF_DOCKER_DOCUMENTED") == "1")
{
    // The settings IronPDF's Docker guide prescribes next to its apt package list (deployment/ironpdf/documented.Dockerfile).
    Installation.LinuxAndDockerDependenciesAutoConfig = false;
    Installation.ChromeGpuMode = IronPdf.Engines.Chrome.ChromeGpuModes.Disabled;
}
return Adapter.Run(new IronPdfConverter());

sealed class IronPdfConverter : IConverter, IPdfTools
{
    public string Library => "IronPDF";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s)
    {
        // A tagged file must be tagged from the HTML: IronPDF's documented route for that is RenderHtmlFileAsPdfUA,
        // which reads a local file. (ConvertToPdfUA on an already rendered PDF produces no headings, tables or lists.)
        if (s.NeedsTags && s.SourceFile != null)
            return Finish(New(s).RenderHtmlFileAsPdfUA(s.SourceFile, NaturalLanguages.English), s, tagged: true);
        return Finish(New(s).RenderUrlAsPdf(url), s);
    }

    public async Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) =>
        Finish(await New(s).RenderUrlAsPdfAsync(url), s);

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) =>
        Finish(New(s).RenderHtmlAsPdf(html, baseUrl, null), s);

    static ChromePdfRenderer New(RenderSettings s)
    {
        var renderer = new ChromePdfRenderer();
        var o = renderer.RenderingOptions;
        o.PaperSize = PdfPaperSize.A4;
        o.PaperOrientation = PdfPaperOrientation.Portrait;
        o.PrintHtmlBackgrounds = true;
        if (s.Print)
        {
            o.CssMediaType = PdfCssMediaType.Print;
        }
        else
        {
            // IronPDF's documented mode for matching a browser window of a given width.
            o.CssMediaType = PdfCssMediaType.Screen;
            o.PaperFit.UseResponsiveCssRendering(s.ViewportWidth);
            o.MarginTop = o.MarginBottom = o.MarginLeft = o.MarginRight = 0;
        }
        if (s.WaitMs > 0)
            o.WaitFor.RenderDelay(s.WaitMs);

        if (s.HeaderFooter)
        {
            // IronPDF draws text headers and footers inside the page margins, so the margins make room for them.
            o.MarginTop = o.MarginBottom = 15;
            o.TextHeader = new TextHeaderFooter { CenterText = RenderSettings.HeaderText, FontSize = 10 };
            o.TextFooter = new TextHeaderFooter { CenterText = "Page {page} of {total-pages}", FontSize = 10 };
        }

        if (s.HeaderHtml != null || s.FooterHtml != null)
        {
            o.MarginTop = o.MarginBottom = 20;   // HTML headers and footers are drawn inside the margins too
            if (s.HeaderHtml != null)
                o.HtmlHeader = new HtmlHeaderFooter { HtmlFragment = s.HeaderHtml, BaseUrl = s.TemplateBaseUrl, MaxHeight = 15 };
            if (s.FooterHtml != null)
                o.HtmlFooter = new HtmlHeaderFooter
                {
                    HtmlFragment = s.FooterHtml.Replace("{pages}", "{total-pages}"),   // {page} is IronPDF's own token
                    BaseUrl = s.TemplateBaseUrl,
                    MaxHeight = 15,
                };
        }

        if (s.Bookmarks != null)
        {
            o.AutoBookmarksFromHeadings = true;
            o.AutoBookmarkCssSelectors = s.Bookmarks;
        }

        if (s.Forms)
            o.CreatePdfFormsFromHtml = true;

        return renderer;
    }

    static (byte[] Pdf, int Pages) Finish(PdfDocument rendered, RenderSettings s, bool tagged = false)
    {
        var doc = rendered;
        try
        {
            if (s.Standard != null)
            {
                if (s.PdfX != null)
                    throw new UnsupportedException("PDF/X");
                doc.MetaData.Title = s.Title;
                if (s.PdfUA != null && !(tagged && s.PdfUA == "1"))   // the tagged render already declares PDF/UA-1
                {
                    var ua = s.PdfUA switch
                    {
                        "1" => PdfUAVersions.PdfUA1,
                        "2" => PdfUAVersions.PdfUA2,
                        _ => throw new UnsupportedException("PDF/UA-" + s.PdfUA),
                    };
                    doc = doc.ConvertToPdfUA(ua, NaturalLanguages.English);
                }
                if (s.PdfA != null)
                {
                    var a = s.PdfA switch
                    {
                        "1a" => PdfAVersions.PdfA1a,
                        "1b" => PdfAVersions.PdfA1b,
                        "2a" => PdfAVersions.PdfA2a,
                        "2b" => PdfAVersions.PdfA2b,
                        "3a" => PdfAVersions.PdfA3a,
                        "3b" => PdfAVersions.PdfA3b,
                        "4" => PdfAVersions.PdfA4,
                        "4e" => PdfAVersions.PdfA4e,
                        "4f" => PdfAVersions.PdfA4f,
                        _ => throw new UnsupportedException("PDF/A-" + s.PdfA),
                    };
                    doc = doc.ConvertToPdfA(a, null);
                }
            }
            if (s.UserPassword != null || s.OwnerPassword != null)
            {
                doc.SecuritySettings.UserPassword = s.UserPassword;
                doc.SecuritySettings.OwnerPassword = s.OwnerPassword;
                doc.SecuritySettings.AllowUserPrinting = IronPdf.Security.PdfPrintSecurity.NoPrint;
                doc.SecuritySettings.AllowUserCopyPasteContent = false;
            }
            return (doc.BinaryData, doc.PageCount);
        }
        finally
        {
            if (!ReferenceEquals(doc, rendered)) doc.Dispose();
            rendered.Dispose();
        }
    }

    // ---- IPdfTools ---------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        return Enumerable.Range(0, doc.PageCount).Select(i => doc.ExtractTextFromPage(i)).ToArray();
    }

    public int[] Search(string pdfFile, string term) =>
        throw new UnsupportedException("no text search API (ExtractText then search the string yourself)");

    public byte[][] Rasterize(string pdfFile, int dpi)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        var dir = Directory.CreateTempSubdirectory("ironpdf-raster-");
        try
        {
            var files = doc.RasterizeToImageFiles(Path.Combine(dir.FullName, "page-*.png"), IronPdf.Imaging.ImageType.Png, dpi);
            return files.Select(File.ReadAllBytes).ToArray();
        }
        finally { dir.Delete(true); }
    }

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile)
    {
        using var doc = new DocxToPdfRenderer().RenderDocxAsPdf(docxFile);
        return (doc.BinaryData, doc.PageCount);
    }

    public byte[] Merge(string[] pdfFiles)
    {
        var sources = pdfFiles.Select(f => PdfDocument.FromFile(f)).ToList();
        try
        {
            using var merged = PdfDocument.Merge(sources);
            return merged.BinaryData;
        }
        finally { sources.ForEach(d => d.Dispose()); }
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        using var part = doc.CopyPages(from - 1, to - 1);
        return part.BinaryData;
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        doc.ApplyWatermark($"<div style='color:#c80000;font:bold 72px Helvetica,Arial,sans-serif'>{text}</div>", 45, 35);
        return doc.BinaryData;
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        doc.Sign(new IronPdf.Signing.PdfSignature(pfxFile, password));
        return doc.BinaryData;
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        foreach (var (name, value) in values)
        {
            var field = doc.Form.FindFormField(name) ?? throw new InvalidOperationException($"field {name} not found");
            // check boxes take their on-state name ("Yes") or "Off", as IronPDF documents
            field.Value = value switch { "true" => "Yes", "false" => "Off", _ => value };
        }
        return doc.BinaryData;
    }

    public byte[] EInvoice(string url, string xmlFile)
    {
        // IronPDF's route: render the page, then convert to PDF/A-3b embedding the XML with a Factur-X configuration
        var renderer = new ChromePdfRenderer();
        renderer.RenderingOptions.PaperSize = PdfPaperSize.A4;
        using var doc = renderer.RenderUrlAsPdf(url);
        var config = new EmbedFileConfiguration(EmbedFileType.xml)
        {
            EmbedFileName = "factur-x.xml",
            ConformanceLevel = ConformanceLevel.EN16931,
            SchemaNamespace = SchemaNamespace.facturX,
            AFRelationship = AFRelationship.Alternative,
        };
        using var invoice = doc.ConvertToPdfA(new[] { new EmbedFilePath(xmlFile, config) }, PdfAVersions.PdfA3b);
        return invoice.BinaryData;
    }

    public byte[] Redact(string pdfFile, string[] terms)
    {
        using var doc = PdfDocument.FromFile(pdfFile);
        foreach (var t in terms)
            doc.RedactTextOnAllPages(t, CaseSensitive: true, OnlyMatchWholeWords: false, DrawRectangles: true);
        return doc.BinaryData;
    }
}
