// SelectPdf.Universal adapter. Runs unlicensed (the free trial): every page carries an evaluation
// banner, which the analysis masks out before comparing images (METHODOLOGY.md, section 2).
using PdfBenchmarks;
using SelectPdf.Universal;

return Adapter.Run(new SelectPdfConverter());

sealed class SelectPdfConverter : IConverter, IPdfTools
{
    public string Library => "SelectPdf";

    public IReadOnlyDictionary<string, string> Packages { get; } = Adapter.PackageVersions();

    public (byte[] Pdf, int Pages) Convert(string url, RenderSettings s) => Finish(New(s).ConvertUrl(url));

    public async Task<(byte[] Pdf, int Pages)> ConvertAsync(string url, RenderSettings s) =>
        Finish(await New(s).ConvertUrlAsync(url));

    public (byte[] Pdf, int Pages) ConvertHtml(string html, string baseUrl, RenderSettings s) =>
        Finish(New(s).ConvertHtmlString(html, baseUrl));

    static (byte[] Pdf, int Pages) Finish(PdfDocument doc)
    {
        try
        {
            return (doc.Save(), doc.Pages.Count);
        }
        finally
        {
            doc.Close();
        }
    }

    static HtmlToPdf New(RenderSettings s)
    {
        var converter = new HtmlToPdf();
        var o = converter.Options;
        o.PdfPageSize = PdfPageSize.A4;
        o.PdfPageOrientation = PdfPageOrientation.Portrait;
        if (s.Print)
        {
            o.CssMediaType = HtmlToPdfCssMediaType.Print;
        }
        else
        {
            // WebPageWidth is laid out like a browser window this wide and scaled to fill the page width.
            o.CssMediaType = HtmlToPdfCssMediaType.Screen;
            o.WebPageWidth = s.ViewportWidth;
            o.MarginTop = o.MarginBottom = o.MarginLeft = o.MarginRight = 0;
        }
        if (s.WaitMs > 0)
            o.ConversionDelay = (int)Math.Ceiling(s.WaitMs / 1000.0);   // whole seconds

        if (s.Standard != null)
        {
            if (s.PdfX != null)
                o.PdfStandard = s.PdfX == "1a" ? PdfStandard.PdfX : throw new UnsupportedException("PDF/X-" + s.PdfX);
            if (s.PdfA != null)
            {
                o.PdfStandard = s.PdfA switch
                {
#if SELECTPDF_26_5
                    "1a" => PdfStandard.PdfA1A,
#endif
                    "1b" => PdfStandard.PdfA,
#if SELECTPDF_26_5
                    "2a" => PdfStandard.PdfA2A,
#endif
                    "2b" => PdfStandard.PdfA2B,
#if SELECTPDF_26_5
                    "2u" => PdfStandard.PdfA2U,
#endif
                    "3b" => PdfStandard.PdfA3B,
                    "3u" => PdfStandard.PdfA3U,
                    "3a" => PdfStandard.PdfA3A,
                    "4" => PdfStandard.PdfA4,
                    "4e" => PdfStandard.PdfA4E,
                    "4f" => PdfStandard.PdfA4F,
                    _ => throw new UnsupportedException("PDF/A-" + s.PdfA),
                };
            }
            if (s.PdfUA != null)
            {
                o.AccessibilityStandard = s.PdfUA switch
                {
                    "1" => PdfUAStandard.UA1,
                    "2" => PdfUAStandard.UA2,
                    _ => throw new UnsupportedException("PDF/UA-" + s.PdfUA),
                };
            }
            o.Title = s.Title;
            o.Language = s.Language;
        }

        if (s.HeaderFooter)
        {
            var font = PdfFont.CreateStandardFont(PdfStandardFont.Helvetica, 10);
            o.DisplayHeader = o.DisplayFooter = true;
            converter.Header.Height = converter.Footer.Height = 30;
            converter.Header.Add(new PdfTextSection(20, 10, RenderSettings.HeaderText, font));
            converter.Footer.Add(new PdfTextSection(20, 10, "Page {page_number} of {total_pages}", font));
        }

        if (s.HeaderHtml != null || s.FooterHtml != null)
        {
            // HTML sections render a template; page numbers are text sections ({page_number} / {total_pages}),
            // which is how SelectPdf documents numbering - HTML sections have no page-number placeholders.
            o.DisplayHeader = o.DisplayFooter = true;
            converter.Header.Height = converter.Footer.Height = 50;
            if (s.HeaderHtml != null)
                converter.Header.Add(new PdfHtmlSection(s.HeaderHtml, s.TemplateBaseUrl ?? ""));
            if (s.FooterHtml != null)
            {
                int split = s.FooterHtml.IndexOf("{page}", StringComparison.Ordinal);
                string html = split >= 0 ? s.FooterHtml.Replace("{page}", "").Replace("{pages}", "").Replace("Page  of", "") : s.FooterHtml;
                converter.Footer.Add(new PdfHtmlSection(html, s.TemplateBaseUrl ?? ""));
                if (split >= 0)
                    converter.Footer.Add(new PdfTextSection(0, 32, "Page {page_number} of {total_pages}",
                        PdfFont.CreateStandardFont(PdfStandardFont.Helvetica, 8)) { HorizontalAlign = PdfTextHorizontalAlign.Center });
            }
        }

        if (s.UserPassword != null || s.OwnerPassword != null)
        {
            o.SecurityOptions.UserPassword = s.UserPassword;
            o.SecurityOptions.OwnerPassword = s.OwnerPassword;
            o.SecurityOptions.CanPrint = false;
            o.SecurityOptions.CanCopyContent = false;
        }

        if (s.Bookmarks != null)
            o.PdfBookmarkOptions.CssSelectors = s.Bookmarks;

        // s.Forms: turn the page's HTML form controls into fillable PDF form fields (SelectPdf.Universal 26.5+).
        if (s.Forms)
#if SELECTPDF_26_5
            o.CreateFormFields = true;
#else
            throw new UnsupportedException("HTML forms to PDF fields: CreateFormFields is new in 26.5");
#endif

        return converter;
    }

    // ---- IPdfTools ---------------------------------------------------------------------------------

    public string[] ExtractText(string pdfFile)
    {
        var tool = new PdfToText { MarkPageBreaks = true };
        tool.Load(pdfFile);
        try
        {
            var pages = tool.GetText().Split(tool.PageBreakMark);
            // the mark follows every page, the last one included: drop the empty piece after it
            return pages.Length > 1 && pages[^1].Trim().Length == 0 ? pages[..^1] : pages;
        }
        finally { tool.Close(); }
    }

    public int[] Search(string pdfFile, string term)
    {
        var tool = new PdfToText();
        tool.Load(pdfFile);
        try
        {
            return tool.Search(term, false, false).Select(p => p.PageNumber).ToArray();
        }
        finally { tool.Close(); }
    }

    public byte[][] Rasterize(string pdfFile, int dpi)
    {
        var tool = new PdfRasterizer { Resolution = dpi, ImagesFormat = PdfImageFormat.Png };
        tool.Load(pdfFile);
        return tool.ConvertToImages();
    }

    public (byte[] Pdf, int Pages) WordToPdf(string docxFile) => Finish(new WordToPdf().ConvertFile(docxFile));

    public byte[] Merge(string[] pdfFiles)
    {
        var sources = pdfFiles.Select(f => new PdfDocument(f)).ToList();
        var doc = new PdfDocument();
        try
        {
            foreach (var src in sources) doc.Append(src);
            return doc.Save();
        }
        finally
        {
            doc.Close();
            sources.ForEach(d => d.Close());
        }
    }

    public byte[] Split(string pdfFile, int from, int to)
    {
        var src = new PdfDocument(pdfFile);
        var doc = new PdfDocument();
        try
        {
            for (int i = from - 1; i < to; i++) doc.AddPage(src.Pages[i]);
            return doc.Save();
        }
        finally
        {
            doc.Close();
            src.Close();
        }
    }

    public byte[] Watermark(string pdfFile, string text)
    {
        // a template covers every page of the document (the documented way to stamp all pages)
        var doc = new PdfDocument(pdfFile);
        try
        {
            var page = doc.Pages[0].ClientRectangle;
            var font = doc.Fonts.Add(PdfStandardFont.HelveticaBold);
            font.Size = 72;
            var template = doc.Templates.Add(page);
            template.Add(new PdfTextElement(page.Width * 0.2f, page.Height * 0.65f, text, font)
            {
                Direction = 45,
                ForeColor = new PdfColor(200, 0, 0),
                Transparency = 35,
            });
            return doc.Save();
        }
        finally { doc.Close(); }
    }

    public byte[] Sign(string pdfFile, string pfxFile, string password)
    {
        var manager = new PdfSecurityManager();
        manager.Load(pdfFile);
        try
        {
            manager.Sign(pfxFile, password);
            return manager.Save();
        }
        finally { manager.Close(); }
    }

    public byte[] FillForm(string pdfFile, IReadOnlyDictionary<string, string> values)
    {
        var form = new PdfFormManager();
        form.Load(pdfFile);
        try
        {
            foreach (var (name, value) in values)
            {
                switch (form.Fields[name])
                {
                    case PdfFormFieldTextBox t: t.Text = value; break;
                    case PdfFormFieldCheckBox c: c.Checked = value == "true"; break;
                    case PdfFormFieldComboBox c: c.SelectedValue = value; break;
                    case PdfFormFieldRadioButtonList r: r.SelectedValue = value; break;
                    case var other: throw new InvalidOperationException($"field {name}: {other?.GetType().Name ?? "not found"}");
                }
            }
            return form.Save();
        }
        finally { form.Close(); }
    }

    public byte[] EInvoice(string url, string xmlFile)
    {
        // as the e-invoice sample does: the invoice page as PDF/A-3b, then the XML embedded as an EN 16931
        // Factur-X invoice whose content is the same as the page ("Alternative")
        var converter = new HtmlToPdf();
        converter.Options.PdfPageSize = PdfPageSize.A4;
        converter.Options.PdfStandard = PdfStandard.PdfA3B;
        var doc = converter.ConvertUrl(url);
        try
        {
            doc.AddZugferdInvoice(xmlFile, ZugferdProfile.En16931, PdfAttachmentRelationship.Alternative);
            return doc.Save();
        }
        finally { doc.Close(); }
    }

    public byte[] Redact(string pdfFile, string[] terms)
    {
        var manager = new PdfRedactionManager();
        manager.Load(pdfFile);
        try
        {
            foreach (var t in terms) manager.AddRedaction(t, true, false);
            manager.Redact();
            return manager.Save();
        }
        finally { manager.Close(); }
    }
}
