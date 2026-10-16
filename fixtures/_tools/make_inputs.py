"""Builds fixtures/inputs/: the files the PDF tools are tested on (METHODOLOGY.md, 4.14-4.17).

  inputs/pdf/<name>.pdf     printed by headless Chromium from the fixture pages - a neutral producer,
                            so no library reads its own output
  inputs/pdf/<name>.txt     the page's text as the browser has it (document.body.innerText): the
                            ground truth for text extraction
  inputs/word/<name>.docx   Word documents written with python-docx
  inputs/word/<name>.word.pdf  Microsoft Word's own PDF export of each document: the reference for
                            Word to PDF (Windows with Word installed; skipped elsewhere)

The output is committed; re-run only to change the inputs, and record it in inputs/SOURCE.md.

    python fixtures/_tools/make_inputs.py --base http://127.0.0.1:8777    (with server/serve.py running)
"""
import argparse
import io
import pathlib
import subprocess
import sys

from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor
from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "inputs"
PDF_PAGES = ["invoice", "rtl", "cjk", "scripts", "accessible", "long-document", "selectpdf-home"]


def pdf_inputs(base: str) -> None:
    (OUT / "pdf").mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        for name in PDF_PAGES:
            page = browser.new_page(viewport={"width": 1024, "height": 800})
            page.goto(f"{base}/{name}/index.html", wait_until="load")
            page.evaluate("document.fonts.ready")
            (OUT / "pdf" / f"{name}.txt").write_text(page.evaluate("document.body.innerText"), encoding="utf-8")
            page.pdf(path=str(OUT / "pdf" / f"{name}.pdf"), format="A4", print_background=True,
                     margin={"top": "15mm", "bottom": "15mm", "left": "15mm", "right": "15mm"})
            page.close()
            print("pdf", name)
        version = browser.version
        browser.close()
    return version


def field(paragraph, code: str) -> None:
    """Inserts a Word field (PAGE, NUMPAGES) into a paragraph."""
    run = paragraph.add_run()
    for kind, text in (("begin", None), (None, code), ("separate", None), (None, "1"), ("end", None)):
        if kind:
            el = OxmlElement("w:fldChar")
            el.set(qn("w:fldCharType"), kind)
        elif text == code:
            el = OxmlElement("w:instrText")
            el.set(qn("xml:space"), "preserve")
            el.text = f" {code} "
        else:
            el = OxmlElement("w:t")
            el.text = text
        run._r.append(el)


def hyperlink(paragraph, url: str, text: str) -> None:
    rid = paragraph.part.relate_to(url, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), rid)
    run = OxmlElement("w:r")
    props = OxmlElement("w:rPr")
    style = OxmlElement("w:rStyle")
    style.set(qn("w:val"), "Hyperlink")
    props.append(style)
    run.append(props)
    t = OxmlElement("w:t")
    t.text = text
    run.append(t)
    link.append(run)
    paragraph._p.append(link)


def report_docx(path: pathlib.Path) -> None:
    doc = Document()
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21), Cm(29.7)
    header = section.header.paragraphs[0]
    header.text = "Northwind Supplies — Annual report 2026"
    footer = section.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    footer.add_run("Page ")
    field(footer, "PAGE")
    footer.add_run(" of ")
    field(footer, "NUMPAGES")

    doc.add_heading("Annual report 2026", 0)
    p = doc.add_paragraph("This document tests Word to PDF conversion: styles, a table, lists, an image, a hyperlink, "
                          "a header and a footer with page numbers, and text that runs over several pages. ")
    p.add_run("Bold, ").bold = True
    p.add_run("italic, ").italic = True
    r = p.add_run("coloured")
    r.font.color.rgb = RGBColor(0xC0, 0x30, 0x30)
    p.add_run(" and ")
    r = p.add_run("larger")
    r.font.size = Pt(16)
    p.add_run(" text.")
    p = doc.add_paragraph("More information: ")
    hyperlink(p, "https://example.com/report", "example.com/report")

    doc.add_heading("1. Results by quarter", 1)
    table = doc.add_table(rows=1, cols=4)
    table.style = "Light Grid Accent 1"
    for cell, text in zip(table.rows[0].cells, ["Quarter", "Invoices", "Reports", "Total"]):
        cell.text = text
    for row in [("Q1", "9,120", "1,840", "10,960"), ("Q2", "10,480", "2,105", "12,585"),
                ("Q3", "10,930", "1,995", "12,925"), ("Q4", "9,710", "2,030", "11,740")]:
        for cell, text in zip(table.add_row().cells, row):
            cell.text = text

    doc.add_heading("2. Highlights", 1)
    for item in ["Tagged output for every customer document", "Archival copies for records kept ten years",
                 "Faster conversions on long documents"]:
        doc.add_paragraph(item, style="List Bullet")
    for item in ["Validate archived files", "Publish the results", "Review in Q1 2027"]:
        doc.add_paragraph(item, style="List Number")

    doc.add_heading("3. Cover image", 1)
    doc.add_picture(str(ROOT / "accessible" / "img" / "figure-1.jpg"), width=Cm(14))
    doc.add_paragraph("Figure 1. Synthetic landscape photograph.").italic = True

    doc.add_heading("4. Notes", 1)
    words = ("report section result value page margin chapter quarter document review summary figure table "
             "client order archive standard engine layout").split()
    for i in range(40):
        doc.add_paragraph(f"Note {i + 1}. " + " ".join(words[(i * 7 + k) % len(words)] for k in range(60)) + ".")
    doc.add_paragraph("End of report.")
    doc.save(path)


def international_docx(path: pathlib.Path) -> None:
    doc = Document()
    doc.sections[0].page_width, doc.sections[0].page_height = Cm(21), Cm(29.7)
    doc.add_heading("International text", 1)
    texts = [("English", "All human beings are born free and equal in dignity and rights.", False),
             ("Arabic", "يولد جميع الناس أحرارًا متساوين في الكرامة والحقوق.", True),
             ("Chinese", "人人生而自由,在尊严和权利上一律平等。", False),
             ("Japanese", "すべての人間は、生まれながらにして自由であり、かつ、尊厳と権利とについて平等である。", False),
             ("Korean", "모든 인간은 태어날 때부터 자유로우며 그 존엄과 권리에 있어 동등하다.", False),
             ("Hindi", "सभी मनुष्यों को गौरव और अधिकारों के मामले में जन्मजात स्वतन्त्रता और समानता प्राप्त है।", False)]
    for label, text, rtl in texts:
        doc.add_paragraph(label).runs[0].bold = True
        p = doc.add_paragraph(text)
        if rtl:
            p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
            bidi = OxmlElement("w:bidi")
            p._p.get_or_add_pPr().append(bidi)
            for run in p.runs:
                rtl_el = OxmlElement("w:rtl")
                run._r.get_or_add_rPr().append(rtl_el)
    doc.save(path)


def form_pdf(path: pathlib.Path) -> None:
    """An AcroForm made with PyMuPDF - neither library produced it: two text fields, a combo box, a check box
    and a radio group of two buttons."""
    import fitz
    doc = fitz.open()
    page = doc.new_page(width=595, height=842)
    page.insert_text((50, 60), "Order form", fontsize=20)
    labels = [("Full name", 110), ("Email", 150), ("Plan", 190), ("Newsletter", 230), ("Contact by", 270)]
    for text, y in labels:
        page.insert_text((50, y + 14), text, fontsize=11)

    def add(kind, name, rect, **kw):
        w = fitz.Widget()
        w.field_type, w.field_name, w.rect = kind, name, fitz.Rect(*rect)
        w.text_fontsize = 11
        for k, v in kw.items():
            setattr(w, k, v)
        page.add_widget(w)

    add(fitz.PDF_WIDGET_TYPE_TEXT, "name", (170, 100, 450, 120), field_value="")
    add(fitz.PDF_WIDGET_TYPE_TEXT, "email", (170, 140, 450, 160), field_value="")
    add(fitz.PDF_WIDGET_TYPE_COMBOBOX, "plan", (170, 180, 300, 200), choice_values=["Free", "Pro", "Enterprise"], field_value="Free")
    add(fitz.PDF_WIDGET_TYPE_CHECKBOX, "news", (170, 222, 186, 238), field_value=False)
    add(fitz.PDF_WIDGET_TYPE_RADIOBUTTON, "contact", (170, 262, 186, 278), field_value=False, button_caption="email")
    add(fitz.PDF_WIDGET_TYPE_RADIOBUTTON, "contact", (250, 262, 266, 278), field_value=False, button_caption="phone")
    page.insert_text((190, 275), "email", fontsize=10)
    page.insert_text((270, 275), "phone", fontsize=10)
    # PyMuPDF names every radio button's on-state "Yes"; a radio group needs one export name per button
    radios = [w.xref for w in page.widgets() if w.field_type == fitz.PDF_WIDGET_TYPE_RADIOBUTTON]
    for xref, export in zip(radios, ["email", "phone"]):
        for state in ("AP/N", "AP/D"):
            kind, value = doc.xref_get_key(xref, state)
            if kind == "dict" and "/Yes" in value:
                doc.xref_set_key(xref, state, value.replace("/Yes", "/" + export))
        doc.xref_set_key(xref, "AS", "/Off")
    # ...and PyMuPDF makes each radio button a field of its own with the same name. A radio group is ONE
    # field (the parent, holding the name and value) whose kids are the buttons.
    parent = doc.get_new_xref()
    doc.update_object(parent, f"<< /FT /Btn /Ff 49152 /T (contact) /V /Off /Kids [{' '.join(f'{x} 0 R' for x in radios)}] >>")
    import re
    for xref in radios:   # the kids lose the field keys - name, type, flags and value belong to the parent
        obj = doc.xref_object(xref, compressed=True)
        obj = re.sub(r"/(T|FT|Ff|V)(?=[\s/(\[<]|\d)\s*(\([^)]*\)|/\w+|\d+)", "", obj)
        doc.update_object(xref, obj.replace("<<", f"<</Parent {parent} 0 R", 1))
    catalog = doc.pdf_catalog()
    kind, fields = doc.xref_get_key(catalog, "AcroForm/Fields")
    for xref in radios:
        fields = fields.replace(f"{xref} 0 R", "")
    doc.xref_set_key(catalog, "AcroForm/Fields", fields.rstrip("]").rstrip() + f" {parent} 0 R]")
    doc.save(path, garbage=1)


def word_reference(docx: pathlib.Path) -> str | None:
    """Exports a .docx to PDF with Microsoft Word (Windows only); returns Word's version."""
    if sys.platform != "win32":
        return None
    out = docx.with_suffix(".word.pdf")
    ps = (f"$w = New-Object -ComObject Word.Application; $w.Visible = $false; "
          f"$d = $w.Documents.Open('{docx}', $false, $true); $d.ExportAsFixedFormat('{out}', 17); "
          f"$d.Close($false); $v = $w.Version; $w.Quit(); $v")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=300)
    return r.stdout.strip() or None


def einvoice(dest: pathlib.Path) -> None:
    """The e-invoice input: an invoice page and its EN 16931 Factur-X XML describing the same invoice,
    taken from SelectPdf's e-invoice sample (both written by SelectPdf; MIT here)."""
    import shutil
    src = pathlib.Path(__file__).resolve().parents[1] / "_tools" / "einvoice-source"
    dest.mkdir(parents=True, exist_ok=True)
    for f in ("invoice.html", "factur-x.xml"):
        shutil.copy(src / f, dest / f)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8777")
    ap.add_argument("--only", default="", help="comma list: pdf,word,form,einvoice (default: all)")
    a = ap.parse_args()
    only = set(filter(None, a.only.split(",")))
    if not only or "form" in only:
        (OUT / "forms").mkdir(parents=True, exist_ok=True)
        form_pdf(OUT / "forms" / "order-form.pdf")
        print("form")
    if not only or "einvoice" in only:
        einvoice(OUT / "einvoice")
        print("einvoice")
    if only and not ({"pdf", "word"} & only):
        raise SystemExit(0)
    chromium = pdf_inputs(a.base)
    (OUT / "word").mkdir(parents=True, exist_ok=True)
    report_docx(OUT / "word" / "report.docx")
    international_docx(OUT / "word" / "international.docx")
    word = None
    for name in ("report", "international"):
        word = word_reference(OUT / "word" / f"{name}.docx") or word
        print("word", name)
    (OUT / "SOURCE.md").write_text(
        "Generated by `_tools/make_inputs.py`.\n\n"
        f"- `pdf/*.pdf`: the fixture pages printed by headless Chromium {chromium} (A4, 15 mm margins); "
        "`pdf/*.txt`: each page's `document.body.innerText`, the ground truth for text extraction.\n"
        "- `word/*.docx`: written with python-docx; the image is the synthetic photo from `accessible/`.\n"
        "- `forms/order-form.pdf`: an AcroForm made with PyMuPDF (text, combo box, check box, radio group).\n"
        "- `einvoice/`: an invoice page and its EN 16931 Factur-X XML, from SelectPdf's e-invoice sample.\n"
        f"- `word/*.word.pdf`: exported by Microsoft Word {word or '(not available)'} — the Word to PDF reference.\n"
        "Text: UDHR (United Nations, freely reproducible) and text written for this repository (MIT).\n",
        encoding="utf-8")
