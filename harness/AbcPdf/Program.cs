// ABCpdf 14 adapter (WebSupergoo), HTML through the ABCChrome146 engine.
// Licence: ABCpdf runs a 30-day trial without a key on Windows (the trial is recorded in the registry on first use).
// Linux has no automatic trial: a key is needed there. A key in ABCPDF_LICENSE_KEY is installed when set.
using System.Text.RegularExpressions;
using WebSupergoo.ABCpdf14;
using WebSupergoo.ABCpdf14.Objects;
using WebSupergoo.ABCpdf14.Operations;
using PdfBenchmarks;

var key = Environment.GetEnvironmentVariable("ABCPDF_LICENSE_KEY");
if (!string.IsNullOrWhiteSpace(key) && !XSettings.InstallLicense(key, false))
{
    // the key itself is never printed
    Console.Error.WriteLine("ABCPDF_LICENSE_KEY was not accepted: " + XSettings.LicenseDescription);
    return 2;
}
return Adapter.Run(new AbcPdfConverter());

sealed class AbcPdfConverter : IConverter, IPdfTools
{
    public string Library => "ABCpdf";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    const double Mm = 72 / 25.4;

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) => Render(s, url, null);

    // ABCpdf has no asynchronous API (no *Async member in the ABCpdf 14 assembly), so the sync call runs on the thread pool.
    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) => Task.Run(() => Convert(url, s));

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) => Render(s, null, (html, baseUrl));

    /// <summary>
    /// Tags, a document outline and HTML headers/footers are features of WebPageOperation (ABCChrome only), which
    /// pages the whole document itself; every other conversion uses the documented paged-HTML route
    /// (AddImageUrl / AddImageHtml, then AddImageToChain while the object is chainable).
    /// </summary>
    static (byte[] Pdf, int Pages) Render(RenderSettings s, string? url, (string Html, string BaseUrl)? html)
    {
        if (s.PdfX != null)
            throw new UnsupportedException("PDF/X (no PDF/X conformance in ABCpdf)");
        // PdfConformityOperation documents PDF/A-1a, 1b, 2a, 2b and 2u. The enum also lists PdfA3a/3b/3u, PdfA4, PdfUA1
        // and PdfUA2, but ABCpdf 14.0.6 rejects them with "PDFException: Invalid PdfConformance" (observed).
        if (s.PdfA != null && s.PdfA is not ("1a" or "1b" or "2a" or "2b" or "2u"))
            throw new UnsupportedException("PDF/A-" + s.PdfA + " (PdfConformityOperation supports PDF/A-1a, 1b, 2a, 2b, 2u)");
        // PDF/UA-1 is a WebPageOperation-tagged document with XTagging.MakePdfUAConformant (which, in 14.0.6, writes no
        // pdfuaid identifier - observed); there is no PDF/UA-2 route.
        if (s.PdfUA != null && s.PdfUA != "1")
            throw new UnsupportedException("PDF/UA-" + s.PdfUA + " (XTagging.MakePdfUAConformant makes PDF/UA-1 only)");
        if (s.Bookmarks != null && s.Bookmarks.Any(b => !Regex.IsMatch(b, "^[hH][1-6]$")))
            throw new UnsupportedException("bookmarks from CSS selectors (WebPageOperation.Outline builds the outline from headings)");

        bool operation = s.NeedsTags || s.Bookmarks != null || s.HeaderHtml != null || s.FooterHtml != null;
        WebPageOperation? op = operation ? new WebPageOperation() : null;
        var doc = op?.Doc ?? new Doc();
        try
        {
            doc.MediaBox.String = "A4";
            doc.Rect.String = doc.MediaBox.String;   // zero margins: the page fills the paper
            var o = doc.HtmlOptions;
            o.Engine = EngineType.Chrome146;         // the default engine, set explicitly as ABCpdf's examples do
            o.Media = s.Print ? MediaType.Print : MediaType.Screen;   // ABCpdf's default is Print
            // ABCpdf's Paged HTML example: InitialWidth with BrowserWidth left at 0 (auto). The page is laid out at least
            // this wide and widens instead of clipping content that is wider; the result is scaled to the Rect width.
            // (A fixed BrowserWidth of 1024 cuts off the wide-content fixture's last columns.)
            o.InitialWidth = s.ViewportWidth;
            if (html != null) o.BaseURI = html.Value.BaseUrl;   // relative URLs in an HTML string
            o.AddLinks = true;                       // off by default; the correctness checks look for link annotations
            if (s.Forms) o.AddForms = true;
            if (s.WaitMs > 0) o.RepaintDelay = s.WaitMs;   // the documented wait for content drawn by JavaScript

            if (s.HeaderFooter)
                doc.Rect.Inset(0, 15 * Mm);          // room for the text header and footer drawn after rendering
            if (op != null)
            {
                if (s.HeaderHtml != null || s.FooterHtml != null)
                {
                    doc.Rect.Inset(0, 20 * Mm);      // HTML headers/footers go above and below the Rect
                    if (s.HeaderHtml != null) op.HeaderHtml = Template(s.HeaderHtml, s.TemplateBaseUrl);
                    if (s.FooterHtml != null) op.FooterHtml = Template(s.FooterHtml, s.TemplateBaseUrl);
                }
                // The outline is built from the tag structure: with Tagged off, Outline = true produces no bookmarks
                // (observed), so bookmarks need tagging too. It covers every heading level (h1-h6), not only the selectors.
                op.Tagged = s.NeedsTags || s.Bookmarks != null;
                op.Outline = s.Bookmarks != null;
                if (url != null) op.ReadUrl(url);
                else op.ReadHtml(html!.Value.Html);
                doc = op.Doc;
            }
            else
            {
                doc.Page = doc.AddPage();
                int id = url != null ? doc.AddImageUrl(url) : doc.AddImageHtml(html!.Value.Html);
                while (doc.Chainable(id))
                {
                    doc.Page = doc.AddPage();
                    id = doc.AddImageToChain(id);
                }
            }
            if (o.AddLinks) doc.HtmlOptions.LinkPages();   // links to anchors in the same page become internal links

            if (s.HeaderFooter)
                TextHeaderFooter(doc);
            return Finish(doc, s);
        }
        finally
        {
            doc.Dispose();
            if (op != null && !ReferenceEquals(op.Doc, doc)) op.Doc.Dispose();
        }
    }

    // WebPageOperation header/footer templates: {page}/{pages} map to its pageNumber/totalPages spans; relative URLs
    // are made absolute against the template's own base URL.
    static string Template(string html, string? baseUrl)
    {
        if (baseUrl != null)
            html = Regex.Replace(html, "(src|href)=\"(?![a-z]+:|/)([^\"]*)\"",
                m => $"{m.Groups[1].Value}=\"{new Uri(new Uri(baseUrl), m.Groups[2].Value)}\"");
        return html.Replace("{pages}", "<span class=totalPages></span>").Replace("{page}", "<span class=pageNumber></span>");
    }

    /// <summary>ABCpdf's Headers and Footers example: after the content, each page gets its header and footer text.</summary>
    static void TextHeaderFooter(Doc doc)
    {
        int count = doc.PageCount;
        doc.FontSize = 10;
        doc.TextStyle.HPos = 0.5;
        doc.TextStyle.VPos = 0.5;
        var media = doc.MediaBox;
        for (int i = 1; i <= count; i++)
        {
            doc.PageNumber = i;
            doc.Rect.SetRect(0, media.Height - 15 * Mm, media.Width, 15 * Mm);
            doc.AddText(RenderSettings.HeaderText);
            doc.Rect.SetRect(0, 0, media.Width, 15 * Mm);
            doc.AddText($"Page {i} of {count}");
        }
    }

    static (byte[] Pdf, int Pages) Finish(Doc doc, RenderSettings s)
    {
        int pages = doc.PageCount;
        if (s.Standard != null)
        {
            var md = doc.ObjectSoup.Catalog.Metadata;
            if (md == null) { md = new Metadata(doc.ObjectSoup); doc.ObjectSoup.Catalog.Metadata = md; }
            md.InfoTitle = s.Title;
            // the old Info dictionary too (ABCpdf's metadata page shows both stores): PDF readers show its Title
            int info = doc.GetInfoInt(-1, "/Info:Ref");
            if (info == 0) { info = doc.AddObject("<< >>"); doc.SetInfo(-1, "/Info:Ref", info.ToString()); }
            doc.SetInfo(info, "/Title:Text", s.Title);
            doc.SetInfo(doc.Root, "/Lang:Text", s.Language);
            doc.SetInfo(doc.Root, "/ViewerPreferences/DisplayDocTitle:Bool", "true");
            if (s.PdfUA == "1")
                doc.Tag.MakePdfUAConformant = true;   // applied when the document is saved
            if (s.PdfA == null)
                return (doc.GetData(), pages);
            using var conformity = new PdfConformityOperation
            {
                Conformance = s.PdfA switch
                {
                    "1a" => PdfConformance.PdfA1a, "1b" => PdfConformance.PdfA1b,
                    "2a" => PdfConformance.PdfA2a, "2b" => PdfConformance.PdfA2b, _ => PdfConformance.PdfA2u,
                },
            };
            var data = conformity.GetData(doc);
            if (conformity.Errors.Count > 0)
                throw new InvalidOperationException("PdfConformityOperation: " + string.Join("; ", conformity.Errors.Take(3)));
            return (data, pages);
        }
        if (s.UserPassword != null || s.OwnerPassword != null)
        {
            doc.Encryption.Type = 5;                          // AES-256, as the docs recommend
            doc.Encryption.SetCryptMethods(CryptMethodType.AESV3);
            if (s.UserPassword != null) doc.Encryption.Password = s.UserPassword;
            if (s.OwnerPassword != null) doc.Encryption.OwnerPassword = s.OwnerPassword;
            doc.Encryption.CanPrint = false;
            doc.Encryption.CanCopy = false;
        }
        return (doc.GetData(), pages);
    }

    // ---- IPdfTools ---------------------------------------------------------------------------------

    static Doc Open(string pdfFile)
    {
        var doc = new Doc();
        doc.Read(pdfFile);
        return doc;
    }

    public string[] ExtractText(string pdfFile)
    {
        using var doc = Open(pdfFile);
        var pages = new string[doc.PageCount];
        for (int i = 1; i <= pages.Length; i++)
        {
            var op = new TextOperation(doc);
            op.PageContents.AddPages(i);
            pages[i - 1] = op.GetText();
        }
        return pages;
    }

    public int[] Search(string pdfFile, string term)
    {
        // ABCpdf's TextOperation.Group example: GetText over the pages, find the term, Select maps it back to fragments
        using var doc = Open(pdfFile);
        var op = new TextOperation(doc);
        op.PageContents.AddPages();
        string text = op.GetText();
        var pageNumbers = Enumerable.Range(1, doc.PageCount).ToDictionary(i => { doc.PageNumber = i; return doc.Page; });
        var hits = new List<int>();
        for (int pos = text.IndexOf(term, StringComparison.Ordinal); pos >= 0; pos = text.IndexOf(term, pos + term.Length, StringComparison.Ordinal))
        {
            var fragments = op.Select(pos, term.Length);
            if (fragments.Count > 0) hits.Add(pageNumbers[fragments[0].PageID]);
        }
        return hits.ToArray();
    }

    public byte[][] Rasterize(string pdfFile, int dpi)
    {
        // the PDF Rendering example: DotsPerInch, Rect = CropBox, Rendering.GetData per page
        using var doc = Open(pdfFile);
        doc.Rendering.DotsPerInch = dpi;
        var images = new byte[doc.PageCount][];
        for (int i = 1; i <= images.Length; i++)
        {
            doc.PageNumber = i;
            doc.Rect.String = doc.CropBox.String;
            images[i - 1] = doc.Rendering.GetData("page.png");
        }
        return images;
    }

    // Doc.Read imports DOC/DOCX only through WordGlue .NET (a separate WebSupergoo product), Microsoft Office or
    // OpenOffice installed on the machine - none of which is part of the ABCpdf NuGet package.
    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) =>
        throw new UnsupportedException("no built-in DOCX import (Doc.Read needs WordGlue .NET, MS Office or OpenOffice installed)");

    public byte[] Redact(string pdfFile, string[] terms) =>
        throw new UnsupportedException("no redaction API (TextOperation finds text; removing it would mean editing content streams by hand)");

    public byte[] Merge(string[] pdfFiles)
    {
        using var doc = Open(pdfFiles[0]);
        foreach (var f in pdfFiles.Skip(1))
        {
            using var next = Open(f);
            doc.Append(next);
        }
        return doc.GetData();
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        using var doc = Open(pdfFile);
        doc.RemapPages(Enumerable.Range(from, to - from + 1).ToArray());   // pages left out are deleted
        return doc.GetData();
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        // as the Doc.Read example (text added to every page), rotated 45 degrees about the page centre, semi-transparent
        using var doc = Open(pdfFile);
        doc.FontSize = 72;
        doc.Font = doc.AddFont("Helvetica-Bold");
        doc.Color.String = "200 0 0 a90";   // alpha 90 of 255
        doc.TextStyle.HPos = 0.5;
        doc.TextStyle.VPos = 0.5;
        for (int i = 1; i <= doc.PageCount; i++)
        {
            doc.PageNumber = i;
            doc.Rect.String = doc.CropBox.String;
            doc.Transform.Reset();
            doc.Transform.Rotate(45, doc.Rect.Left + doc.Rect.Width / 2, doc.Rect.Bottom + doc.Rect.Height / 2);
            doc.AddText(text);
        }
        doc.Transform.Reset();
        return doc.GetData();
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        // XForm.AddSignature (an invisible field: empty rect), then Signature.Sign with the PKCS#12 file (SHA-256 default)
        using var doc = Open(pdfFile);
        doc.PageNumber = 1;
        var sig = doc.Form.AddSignature(new XRect(), "Signature1");
        sig.Reason = "pdf-benchmarks";
        sig.Sign(pfxFile, password);
        return doc.GetData();
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        using var doc = Open(pdfFile);
        foreach (var (name, value) in values)
        {
            var field = doc.Form[name] ?? throw new InvalidOperationException($"field {name} not found");
            // a check box takes its on-state name or Off
            field.Value = value switch
            {
                "true" => field.Options?.FirstOrDefault(v => v != "Off") ?? "Yes",
                "false" => "Off",
                _ => value,
            };
        }
        return doc.GetData();
    }

    public byte[] EInvoice(string url, string xmlFile) =>
        throw new UnsupportedException("no ZUGFeRD / Factur-X API (PDF/A-3 conformance and embedded files exist, the Factur-X metadata does not)");
}
