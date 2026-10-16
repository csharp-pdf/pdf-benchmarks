// QuestPDF adapter. QuestPDF builds documents from C# code (its fluent API); it has no HTML engine, so every
// HTML conversion is unsupported. Work on existing PDFs goes through its DocumentOperation API (built on qpdf):
// merge, page selection (split) and overlay (watermark). See NOTES.md for the operation table and sources.
//
// Licence: QuestPDF requires Settings.License to be set before the first document. The Community licence covers
// open-source projects, charities, academic institutions, and individuals and businesses under USD 1M annual gross
// revenue (https://www.questpdf.com/license/community.html); the Evaluation tier is for internal testing and
// technical review by organisations not eligible for Community. QUESTPDF_LICENSE (Community, Evaluation,
// Professional, Enterprise) overrides the default, so whoever re-runs the benchmark can declare the tier that
// applies to them.
using PdfBenchmarks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

var tier = Environment.GetEnvironmentVariable("QUESTPDF_LICENSE");
QuestPDF.Settings.License = string.IsNullOrWhiteSpace(tier) ? LicenseType.Community : Enum.Parse<LicenseType>(tier, ignoreCase: true);
return Adapter.Run(new QuestPdfConverter());

sealed class QuestPdfConverter : IConverter, IPdfTools
{
    const string NoHtml = "no HTML rendering (QuestPDF builds documents from C# code)";

    public string Library => "QuestPDF";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings settings) => throw new UnsupportedException(NoHtml);

    public Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings settings) => throw new UnsupportedException(NoHtml);

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings settings) => throw new UnsupportedException(NoHtml);

    // ---- IPdfTools ---------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile) => throw new UnsupportedException("no text extraction from existing PDFs");

    public int[] Search(string pdfFile, string term) => throw new UnsupportedException("no text search in existing PDFs");

    public byte[][] Rasterize(string pdfFile, int dpi) =>
        throw new UnsupportedException("GenerateImages renders QuestPDF documents only, not existing PDFs");

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) => throw new UnsupportedException("no Word (DOCX) import");

    public byte[] Redact(string pdfFile, string[] terms) => throw new UnsupportedException("no redaction");

    public byte[] Sign(string pdfFile, string pfxFile, string password) => throw new UnsupportedException("no digital signatures");

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values) => throw new UnsupportedException("no AcroForm filling");

    public byte[] EInvoice(string url, string xmlFile) =>
        // QuestPDF documents a ZUGFeRD route (PDF/A-3b + AddAttachment + ExtendMetadata), but the harness asks for
        // the invoice page at a URL, which QuestPDF cannot render.
        throw new UnsupportedException(NoHtml);

    public byte[] Merge(string[] pdfFiles)
    {
        // DocumentOperation.LoadFile + MergeFile (https://www.questpdf.com/concepts/document-operations.html)
        var op = DocumentOperation.LoadFile(pdfFiles[0]);
        foreach (var file in pdfFiles.Skip(1))
            op = op.MergeFile(file);
        return Save(op);
    }

    public byte[] Split(string pdfFile, int from, int to) =>
        // TakePages with qpdf's page-range syntax: "10-12" is pages 10 to 12 inclusive
        Save(DocumentOperation.LoadFile(pdfFile).TakePages($"{from}-{to}"));

    public byte[] Watermark(string pdfFile, string text)
    {
        // QuestPDF's documented watermark route is an overlay: a one-page PDF with the watermark, made here with
        // QuestPDF itself, drawn over every page (RepeatSourcePages repeats it after the first target page).
        var stamp = TempFile();
        try
        {
            Document.Create(c => c.Page(page =>
            {
                page.Size(PageSizes.A4);   // the inputs are A4 (fixtures/inputs/pdf)
                page.PageColor(Colors.Transparent);
                page.Content().AlignCenter().AlignMiddle().Rotate(-45).Text(text)
                    .FontSize(72).Bold().FontColor(Color.FromARGB(166, 200, 0, 0));   // 35 % transparent red
            })).GeneratePdf(stamp);
            return Save(DocumentOperation.LoadFile(pdfFile).OverlayFile(new DocumentOperation.LayerConfiguration
            {
                FilePath = stamp,
                RepeatSourcePages = "1",
            }));
        }
        finally { File.Delete(stamp); }
    }

    /// <summary>DocumentOperation only saves to a file path: save to a temporary file and return its bytes.</summary>
    static byte[] Save(DocumentOperation op)
    {
        var path = TempFile();
        try
        {
            op.Save(path);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    static string TempFile() => Path.Combine(Path.GetTempPath(), "questpdf-" + Guid.NewGuid().ToString("N") + ".pdf");
}
