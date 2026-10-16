// PDFsharp / MigraDoc adapter (AGPL-3.0 or commercial since 6.5.0; earlier versions MIT; no trial limits). PDFsharp creates and modifies PDFs; it has no
// HTML route (MigraDoc lays out its own document model, not HTML), no text extraction, no rendering to images and no
// Word import - those operations report "unsupported".
using System.Security.Cryptography.X509Certificates;
using PdfBenchmarks;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

// The Core build of PDFsharp resolves no fonts by itself. On Windows it can use the installed fonts (the documented
// switch below); elsewhere it needs a custom font resolver (docs.pdfsharp.net, "Font resolving"): here, one that
// serves the TrueType file named by PDFSHARP_FONT_FILE for every face (the watermark is its only text).
// Since 6.5.0 "your code must select the type of license to be used" (docs.pdfsharp.net, History, 6.5.0); without it
// every operation throws PdfSharpLicenseException. The free AGPL licence is selected with SetAgplLicense().
PdfSharp.PdfSharpLicense.SetAgplLicense();
GlobalFontSettings.UseWindowsFontsUnderWindows = true;
if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PDFSHARP_FONT_FILE") is { Length: > 0 } fontFile)
    GlobalFontSettings.FontResolver = new OneFileFontResolver(fontFile);
return Adapter.Run(new PdfSharpConverter());

sealed class PdfSharpConverter : IConverter, IPdfTools
{
    public string Library => "PDFsharp";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    const string NoHtml = "no HTML to PDF conversion (PDFsharp/MigraDoc do not read HTML)";

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) => throw new UnsupportedException(NoHtml);

    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) => throw new UnsupportedException(NoHtml);

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) => throw new UnsupportedException(NoHtml);

    // ---- IPdfTools ---------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile) =>
        throw new UnsupportedException("no text extraction API (ContentReader only parses content-stream operators)");

    public int[] Search(string pdfFile, string term) => throw new UnsupportedException("no text search API");

    public byte[][] Rasterize(string pdfFile, int dpi) => throw new UnsupportedException("no PDF rendering to images");

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) => throw new UnsupportedException("no Word (.docx) import");

    public byte[] Redact(string pdfFile, string[] terms) => throw new UnsupportedException("no redaction API");

    public byte[] Merge(string[] pdfFiles)
    {
        // PDFsharp's "Concatenate documents" sample: open each source in Import mode and add its pages
        using var doc = new PdfDocument();
        foreach (var f in pdfFiles)
        {
            using var src = PdfReader.Open(f, PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) doc.AddPage(page);
        }
        return Save(doc);
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        using var src = PdfReader.Open(pdfFile, PdfDocumentOpenMode.Import);
        using var doc = new PdfDocument();
        for (int i = from - 1; i < to; i++) doc.AddPage(src.Pages[i]);
        return Save(doc);
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        // PDFsharp's "Watermark" sample: on every page, an XGraphics appended to the content, rotated about the centre
        using var doc = PdfReader.Open(pdfFile, PdfDocumentOpenMode.Modify);
        var font = new XFont("Arial", 72, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(90, 200, 0, 0));   // alpha 90/255, about 35% opaque
        foreach (var page in doc.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.TranslateTransform(page.Width.Point / 2, page.Height.Point / 2);
            gfx.RotateTransform(-45);   // XGraphics has y pointing down: -45 runs up from left to right
            gfx.DrawString(text, font, brush, new XPoint(0, 0), XStringFormats.Center);
        }
        return Save(doc);
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        // PDFsharp 6's documented signer: PdfSharpDefaultSigner (PdfSharp.Cryptography) with an X509Certificate2,
        // attached through DigitalSignatureHandler; the signature is written when the document is saved
        using var cert = new X509Certificate2(pfxFile, password, X509KeyStorageFlags.Exportable);
        using var doc = PdfReader.Open(pdfFile, PdfDocumentOpenMode.Modify);
        var options = new DigitalSignatureOptions { Reason = "pdf-benchmarks", AppName = "pdf-benchmarks" };
        DigitalSignatureHandler.ForDocument(doc, new PdfSharpDefaultSigner(cert, PdfMessageDigestType.SHA256), options);
        return Save(doc);
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        using var doc = PdfReader.Open(pdfFile, PdfDocumentOpenMode.Modify);
        var fields = doc.AcroForm?.Fields ?? throw new InvalidOperationException("no AcroForm");
        foreach (var (name, value) in values)
        {
            switch (fields[name])
            {
                case PdfTextField t: t.Text = value; break;
                case PdfCheckBoxField c: c.Checked = value == "true"; break;
                case PdfComboBoxField c: c.Value = new PdfString(value); break;
                case PdfRadioButtonField r:
                    // The field value is the chosen button's state name (PdfAcroField.Value). PdfRadioButtonField.SelectedIndex
                    // is not used: in 6.2.4 it stores a reference to the button widget as /V instead of its state name, and
                    // reads back -1 on this form. Setting Value does not switch the buttons' /AS, so no button is drawn on.
                    r.Value = new PdfName("/" + value);
                    break;
                case var other: throw new InvalidOperationException($"field {name}: {other?.GetType().Name ?? "not found"}");
            }
        }
        return Save(doc);
    }

    public byte[] EInvoice(string url, string xmlFile) =>
        throw new UnsupportedException(NoHtml + ": the invoice page cannot be rendered");

    static byte[] Save(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}

/// <summary>A custom font resolver (PDFsharp's IFontResolver) that resolves every face to one TrueType file.</summary>
sealed class OneFileFontResolver(string path) : IFontResolver
{
    readonly byte[] font = File.ReadAllBytes(path);
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new("face", bold, italic);
    public byte[] GetFont(string faceName) => font;
}
