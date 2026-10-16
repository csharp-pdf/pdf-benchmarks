// iText adapter: iText 9 core + the pdfHTML add-on (HTML to PDF) + pdfSweep (redaction), all used under the
// AGPL (no licence key: iText's open-source mode). AGPL mode adds no watermark and no page limit.
// pdfHTML is not a browser: it has no JavaScript engine and no viewport - it lays the page out at the page width.
using System.Text.RegularExpressions;
using iText.Bouncycastle.X509;
using iText.Bouncycastle.Crypto;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms;
using iText.Forms.Fields;
using iText.Html2pdf;
using iText.Html2pdf.Attach.Impl;
using iText.Kernel.Colors;
using iText.Kernel.Crypto;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Utils;
using iText.Kernel.XMP;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Pdfa;
using iText.PdfCleanup;
using iText.PdfCleanup.Autosweep;
using iText.Signatures;
using iText.StyledXmlParser.Css.Media;
using Org.BouncyCastle.Pkcs;
using PdfBenchmarks;

return Adapter.Run(new ITextConverter());

sealed class ITextConverter : IConverter, IPdfTools
{
    public string Library => "iText";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    static readonly HttpClient Http = new();

    // pdfHTML converts HTML text, a stream or a local file - it has no URL input. So the page is fetched here and
    // converted with the URL as the base URI; pdfHTML's own resource retriever then loads the CSS, fonts and images.
    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) =>
        ConvertHtml(Http.GetStringAsync(url).GetAwaiter().GetResult(), url, s);

    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) =>
        throw new UnsupportedException("no asynchronous API (HtmlConverter is synchronous only)");

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s)
    {
        var props = Properties(s, baseUrl);
        var output = new MemoryStream();
        var writerProps = new WriterProperties();
        if (s.UserPassword != null || s.OwnerPassword != null)
            // permissions 0: none of ALLOW_PRINTING / ALLOW_COPY ... is granted
            writerProps.SetStandardEncryption(Bytes(s.UserPassword), Bytes(s.OwnerPassword), 0, EncryptionConstants.ENCRYPTION_AES_256);
        if (s.PdfUA == "2" || s.PdfA == "4" || s.PdfA == "4e" || s.PdfA == "4f")
            writerProps.SetPdfVersion(PdfVersion.PDF_2_0);   // PDF/UA-2 and PDF/A-4 are PDF 2.0 files
        using (var writer = new PdfWriter(output, writerProps))
            HtmlConverter.ConvertToPdf(Prepare(html, s), writer, props);
        var pdf = output.ToArray();
        return (pdf, PageCount(pdf, s.UserPassword ?? s.OwnerPassword));
    }

    static ConverterProperties Properties(RenderSettings s, string baseUrl)
    {
        var props = new ConverterProperties().SetBaseUri(baseUrl);
        // CSS media type; the width is what media queries see (points: 1 CSS px = 0.75 pt). pdfHTML has no option to lay
        // the page out at a wider viewport and scale it down to the paper - content is laid out at the A4 width.
        var media = new MediaDeviceDescription(s.Print ? MediaType.PRINT : MediaType.SCREEN);
        if (!s.Print) media.SetWidth(s.ViewportWidth * 0.75f);
        props.SetMediaDeviceDescription(media);
        // s.WaitMs: pdfHTML runs no JavaScript, so there is nothing to wait for.

        if (s.Forms)
            props.SetCreateAcroForm(true);   // pdfHTML's option: HTML form controls become AcroForm fields

        if (s.Bookmarks != null)
        {
            // pdfHTML builds outlines from tag names with a priority (level) each; only plain tag selectors map onto that.
            var outlines = new OutlineHandler();
            for (int i = 0; i < s.Bookmarks.Length; i++)
            {
                if (!Regex.IsMatch(s.Bookmarks[i], "^[a-zA-Z][a-zA-Z0-9]*$"))
                    throw new UnsupportedException("bookmarks from CSS selector " + s.Bookmarks[i] + " (tag names only)");
                outlines.PutMarkPriorityMapping(s.Bookmarks[i].ToLowerInvariant(), i + 1);
            }
            props.SetOutlineHandler(outlines);
        }

        if (s.Standard != null)
        {
            if (s.PdfX != null)
                throw new UnsupportedException("PDF/X");
            if (s.PdfA != null && s.PdfUA != null)   // pdfHTML 6.3 refuses: "Targeting multiple conformance levels ... is currently not supported"
                throw new UnsupportedException("PDF/A and PDF/UA together");
            if (s.PdfA != null)
            {
                props.SetPdfAConformance(s.PdfA switch
                {
                    "1a" => PdfAConformance.PDF_A_1A,
                    "1b" => PdfAConformance.PDF_A_1B,
                    "2a" => PdfAConformance.PDF_A_2A,
                    "2b" => PdfAConformance.PDF_A_2B,
                    "2u" => PdfAConformance.PDF_A_2U,
                    "3a" => PdfAConformance.PDF_A_3A,
                    "3b" => PdfAConformance.PDF_A_3B,
                    "3u" => PdfAConformance.PDF_A_3U,
                    "4" => PdfAConformance.PDF_A_4,
                    "4e" => PdfAConformance.PDF_A_4E,
                    "4f" => PdfAConformance.PDF_A_4F,
                    _ => throw new UnsupportedException("PDF/A-" + s.PdfA),
                });
                props.SetDocumentOutputIntent(SrgbIntent());   // pdfHTML requires an explicit output intent for PDF/A
            }
            if (s.PdfUA != null)
            {
                props.SetPdfUAConformance(s.PdfUA switch
                {
                    "1" => PdfUAConformance.PDF_UA_1,
                    "2" => PdfUAConformance.PDF_UA_2,
                    _ => throw new UnsupportedException("PDF/UA-" + s.PdfUA),
                });
            }
            // Title and language come from the page's <title> and <html lang>, as pdfHTML documents.
        }
        return props;
    }

    /// <summary>
    /// Page set-up in pdfHTML is CSS: page size, margins, headers and footers are @page rules (margin boxes and
    /// running elements), which is how pdfHTML documents them. They are added as a style sheet at the top of the
    /// head, so a page's own @page rules still win for print media.
    /// </summary>
    static string Prepare(string html, RenderSettings s)
    {
        var css = new List<string>();
        var body = "";
        if (!s.Print)
            css.Add("@page { size: A4; margin: 0; }");
        if (s.HeaderFooter)
            css.Add($"@page {{ margin-top: 15mm; margin-bottom: 15mm; " +
                    $"@top-center {{ content: \"{RenderSettings.HeaderText}\"; font: 10pt Helvetica; }} " +
                    "@bottom-center { content: \"Page \" counter(page) \" of \" counter(pages); font: 10pt Helvetica; } }");
        if (s.HeaderHtml != null || s.FooterHtml != null)
        {
            // running elements: the template is part of the document and moved into the page margin box
            css.Add("@page { margin-top: 20mm; margin-bottom: 20mm; @top-center { content: element(pdfbHeader); } " +
                    "@bottom-center { content: element(pdfbFooter); } }");
            css.Add("#pdfb-header { position: running(pdfbHeader); } #pdfb-footer { position: running(pdfbFooter); }");
            css.Add(".pdfb-page::after { content: counter(page); } .pdfb-pages::after { content: counter(pages); }");
            if (s.HeaderHtml != null)
                body += $"<div id=\"pdfb-header\">{Rebase(s.HeaderHtml, s.TemplateBaseUrl)}</div>";
            if (s.FooterHtml != null)
                body += "<div id=\"pdfb-footer\">" + Rebase(s.FooterHtml, s.TemplateBaseUrl)
                    .Replace("{pages}", "<span class=\"pdfb-pages\"></span>")
                    .Replace("{page}", "<span class=\"pdfb-page\"></span>") + "</div>";
        }
        if (css.Count > 0)
            html = Insert(html, "<head", "<style>" + string.Join("\n", css) + "</style>");
        if (body.Length > 0)
            html = Insert(html, "<body", body);
        return html;
    }

    // the templates resolve relative URLs against their own base URL, not the page's
    static string Rebase(string html, string? baseUrl) => baseUrl == null ? html :
        Regex.Replace(html, "(src|href)=\"(?![a-z]+:|/)([^\"]*)\"", m => $"{m.Groups[1].Value}=\"{new Uri(new Uri(baseUrl), m.Groups[2].Value)}\"");

    // insert right after the opening tag (e.g. "<head ...>"), or at the start when the page has none
    static string Insert(string html, string tag, string content)
    {
        var m = Regex.Match(html, tag + "\\b[^>]*>", RegexOptions.IgnoreCase);
        return m.Success ? html.Insert(m.Index + m.Length, content) : content + html;
    }

    static byte[]? Bytes(string? s) => s == null ? null : System.Text.Encoding.UTF8.GetBytes(s);

    static int PageCount(byte[] pdf, string? password)
    {
        var rp = new ReaderProperties();
        if (password != null) rp.SetPassword(Bytes(password));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf), rp));
        return doc.GetNumberOfPages();
    }

    /// <summary>
    /// The sRGB output intent PDF/A needs. iText ships no ICC profile (its samples load one from disk), so the adapter
    /// carries a standard sRGB profile generated with LittleCMS (fixtures/_shared/icc/sRGB.icc, copied next to the
    /// executable) - the same file on every OS. ITEXT_ICC_PROFILE overrides it.
    /// </summary>
    static PdfOutputIntent SrgbIntent()
    {
        var path = Environment.GetEnvironmentVariable("ITEXT_ICC_PROFILE") ?? Path.Combine(AppContext.BaseDirectory, "sRGB.icc");
        if (!File.Exists(path))
            throw new InvalidOperationException("PDF/A needs an sRGB ICC profile: set ITEXT_ICC_PROFILE to its path");
        return new PdfOutputIntent("Custom", "", "http://www.color.org", "sRGB IEC61966-2.1", new MemoryStream(File.ReadAllBytes(path)));
    }

    // ---- IPdfTools ---------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile)
    {
        using var doc = new PdfDocument(new PdfReader(pdfFile));
        return Enumerable.Range(1, doc.GetNumberOfPages())
            .Select(i => PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).ToArray();   // LocationTextExtractionStrategy
    }

    public int[] Search(string pdfFile, string term)
    {
        // iText's text-location API: RegexBasedLocationExtractionStrategy returns every match on a page
        using var doc = new PdfDocument(new PdfReader(pdfFile));
        var hits = new List<int>();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var strategy = new RegexBasedLocationExtractionStrategy(Regex.Escape(term));
            new PdfCanvasProcessor(strategy).ProcessPageContent(doc.GetPage(i));
            hits.AddRange(Enumerable.Repeat(i, strategy.GetResultantLocations().Count));
        }
        return hits.ToArray();
    }

    public byte[][] Rasterize(string pdfFile, int dpi) =>
        throw new UnsupportedException("no PDF-to-image API in iText for .NET");

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) =>
        throw new UnsupportedException("no Word-to-PDF API in iText for .NET");

    public byte[] Redact(string pdfFile, string[] terms)
    {
        // pdfSweep: a regex strategy per term finds the text, removes it and fills the area with the redaction colour
        var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(pdfFile), new PdfWriter(output)))
        {
            var strategy = new CompositeCleanupStrategy();
            foreach (var t in terms)
                strategy.Add(new RegexBasedCleanupStrategy(Regex.Escape(t)).SetRedactionColor(ColorConstants.BLACK));
            PdfCleaner.AutoSweepCleanUp(doc, strategy);
        }
        return output.ToArray();
    }

    public byte[] Merge(string[] pdfFiles)
    {
        var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var merger = new PdfMerger(doc);
            foreach (var f in pdfFiles)
            {
                using var src = new PdfDocument(new PdfReader(f));
                merger.Merge(src, 1, src.GetNumberOfPages());
            }
        }
        return output.ToArray();
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        var output = new MemoryStream();
        using (var src = new PdfDocument(new PdfReader(pdfFile)))
        using (var doc = new PdfDocument(new PdfWriter(output)))
            src.CopyPagesTo(from, to, doc);
        return output.ToArray();
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        // iText's watermark sample: on every page, text drawn over the content, rotated 45 degrees, semi-transparent
        var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(pdfFile), new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD);
            var gs = new PdfExtGState().SetFillOpacity(0.35f);
            for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                var page = doc.GetPage(i);
                var size = page.GetPageSizeWithRotation();
                // wrapOldContent: the page's own content is put in q/Q, so a transformation it leaves behind cannot move ours
                var canvas = new PdfCanvas(page, true);
                canvas.SaveState().SetExtGState(gs);
                using (var layout = new iText.Layout.Canvas(canvas, size))
                    layout.ShowTextAligned(new Paragraph(text).SetFont(font).SetFontSize(72).SetFontColor(new DeviceRgb(200, 0, 0)),
                        size.GetWidth() / 2, size.GetHeight() / 2, i, TextAlignment.CENTER, VerticalAlignment.MIDDLE, (float)(Math.PI / 4));
                canvas.RestoreState();
            }
        }
        return output.ToArray();
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        // as iText's signing samples: the key and chain read from the PKCS#12 file with BouncyCastle, then a detached
        // CMS signature (adbe.pkcs7.detached) with SHA-256 through PdfSigner
        var store = new Pkcs12StoreBuilder().Build();
        using (var fs = File.OpenRead(pfxFile)) store.Load(fs, password.ToCharArray());
        var alias = store.Aliases.Cast<string>().First(store.IsKeyEntry);
        var chain = store.GetCertificateChain(alias).Select(c => (IX509Certificate)new X509CertificateBC(c.Certificate)).ToArray();
        var key = new PrivateKeyBC(store.GetKey(alias).Key);

        var output = new MemoryStream();
        var signer = new PdfSigner(new PdfReader(pdfFile), output, new StampingProperties());
        signer.SetSignerProperties(new SignerProperties().SetFieldName("Signature1").SetReason("pdf-benchmarks"));
        signer.SignDetached(new PrivateKeySignature(key, DigestAlgorithms.SHA256), chain, null, null, null, 0, PdfSigner.CryptoStandard.CMS);
        return output.ToArray();
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(pdfFile), new PdfWriter(output)))
        {
            var form = PdfAcroForm.GetAcroForm(doc, false) ?? throw new InvalidOperationException("no AcroForm");
            foreach (var (name, value) in values)
            {
                var field = form.GetField(name) ?? throw new InvalidOperationException($"field {name} not found");
                if (field is PdfButtonFormField { } b && !b.IsRadio() && !b.IsPushButton())
                    // a check box takes its "on" appearance state (the name that is not Off) or Off
                    field.SetValue(value == "true" ? field.GetAppearanceStates().First(a => a != "Off") : "Off");
                else
                    field.SetValue(value);   // text, combo box (export value) and radio group (the button's state name)
            }
        }
        return output.ToArray();
    }

    // iText 9 has no ZUGFeRD / Factur-X API: a PDF/A-3 file with an attachment is possible, but the Factur-X
    // metadata would have to be written by hand, which is not the library's own feature.
    public byte[] EInvoice(string url, string xmlFile) =>
        throw new UnsupportedException("no ZUGFeRD / Factur-X API (the Factur-X XMP would have to be hand-written)");

    // The Factur-X 1.0 XMP: the fx: properties for an EN 16931 invoice and the PDF/A extension schema that declares them.
    const string FacturXXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" xmlns:fx="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#">
           <fx:DocumentType>INVOICE</fx:DocumentType>
           <fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>
           <fx:Version>1.0</fx:Version>
           <fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>
          </rdf:Description>
          <rdf:Description rdf:about="" xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/"
              xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
           <pdfaExtension:schemas>
            <rdf:Bag>
             <rdf:li rdf:parseType="Resource">
              <pdfaSchema:schema>Factur-X PDFA Extension Schema</pdfaSchema:schema>
              <pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>
              <pdfaSchema:prefix>fx</pdfaSchema:prefix>
              <pdfaSchema:property>
               <rdf:Seq>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentFileName</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The name of the embedded XML document</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentType</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The type of the hybrid document in capital letters, e.g. INVOICE or ORDER</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>Version</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The actual version of the standard applying to the embedded XML document</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>ConformanceLevel</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The conformance level of the embedded XML document</pdfaProperty:description></rdf:li>
               </rdf:Seq>
              </pdfaSchema:property>
             </rdf:li>
            </rdf:Bag>
           </pdfaExtension:schemas>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;
}
