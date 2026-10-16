"""Merge, split, watermark, signatures, form filling, e-invoices and PDF/X (METHODOLOGY.md, 4.18-4.21 and 4.7).
Writes, per run:

  merge_split.csv  merged and split documents against their sources, page by page
  watermark.csv    the watermark on every page, visible, and the original text kept
  signature.csv    pyHanko's verdict on each signature: intact, valid with the test certificate as trust root,
                   covering the whole file, and the signature type
  fill.csv         every filled field read back with two readers, and drawn on the page
  einvoice.csv     the e-invoice: PDF/A-3b (veraPDF), the embedded XML byte for byte, its relationship,
                   and the Factur-X metadata
  pdfx.csv         PDF/X-1a structure: output intent, version keys, trapping, fonts, transparency, colour
"""
import csv
import json
import pathlib
import re
import subprocess

import fitz
import numpy as np
from pypdf import PdfReader

import standards
import suites

ROOT = pathlib.Path(__file__).resolve().parents[1]
INPUTS = ROOT / "fixtures" / "inputs"


def text_pages(path: pathlib.Path) -> list[str]:
    return [suites.norm(p.get_text()) for p in fitz.open(path)]


def absent(results: pathlib.Path, lib: str, tool: str) -> tuple:
    """(pass, detail) for an output that is not there: "n/a" when the library has no API for the tool."""
    reason = suites.unsupported_tools(results).get((lib, tool))
    return ("n/a", "not supported: " + reason) if reason is not None else (False, "no output")


def merge_split(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> list:
    rows = []
    sources = [text_pages(INPUTS / "pdf" / f"{n}.pdf") for n in spec["merge"]]
    expected = [t for src in sources for t in src]
    sp = spec["split"]
    split_src = text_pages(INPUTS / "pdf" / f"{sp['input']}.pdf")[sp["from"] - 1:sp["to"]]
    for lib in libs:
        for kind, path, want in (("merge", run / "pdf" / lib / "tools" / "merge" / "merged.pdf", expected),
                                 ("split", run / "pdf" / lib / "tools" / "split" / f"{sp['input']}.pdf", split_src)):
            if not path.exists():
                rows.append([lib, kind, "output", *absent(results, lib, kind)])
                continue
            got = text_pages(path)
            rows.append([lib, kind, "pages", len(got) == len(want), f"{len(got)} of {len(want)}"])
            scores = [suites.bigram_f1(w, g) for w, g in zip(want, got)]
            rows.append([lib, kind, "every page's text in order", bool(scores) and min(scores) >= 0.98,
                         f"lowest page F1 {min(scores):.3f}" if scores else ""])
            fonts = [f for p in fitz.open(path) for f in p.get_fonts(full=True)]
            rows.append([lib, kind, "fonts embedded", all(f[1] != "n/a" for f in fonts), f"{len(fonts)} fonts"])
    return rows


def watermark(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> list:
    rows, wm = [], spec["watermark"]
    src_path = INPUTS / "pdf" / f"{wm['input']}.pdf"
    src = fitz.open(src_path)
    for lib in libs:
        path = run / "pdf" / lib / "tools" / "watermark" / f"{wm['input']}.pdf"
        if not path.exists():
            rows.append([lib, "output", *absent(results, lib, "watermark")])
            continue
        d = fitz.open(path)
        rows.append([lib, "pages", d.page_count == src.page_count, f"{d.page_count} pages"])
        missing = [i + 1 for i, p in enumerate(d) if wm["text"] not in suites.norm(p.get_text()).replace("\n", "")
                   and wm["text"] not in "".join(suites.norm(p.get_text()).split())]
        rows.append([lib, "text on every page", not missing, f"not found on pages {missing}" if missing else "",])
        changed = []
        for i in range(min(d.page_count, src.page_count)):
            a, b = suites.gray(src[i], 50, False), suites.gray(d[i], 50)
            b = suites.fit(b, a.shape)
            changed.append(float((np.abs(a.astype(int) - b.astype(int)) > 24).mean()))
        rows.append([lib, "visible on every page", min(changed) > 0.01, f"min {min(changed) * 100:.1f}% of pixels changed"])
        kept = min(suites.bigram_f1(s.get_text(), suites.norm(p.get_text()).replace(wm["text"], ""))
                   for s, p in zip(src, d))
        rows.append([lib, "original text kept", kept >= 0.97, f"lowest page F1 {kept:.3f}"])
    return rows


def signature(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> list:
    from pyhanko.keys import load_cert_from_pemder
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign.validation import validate_pdf_signature
    from pyhanko_certvalidator import ValidationContext

    rows = []
    cert_pem = run / "cert" / "test-signer.cer.pem"
    root = load_cert_from_pemder(str(cert_pem)) if cert_pem.exists() else None
    for lib in libs:
        path = run / "pdf" / lib / "tools" / "sign" / f"{spec['sign']['input']}.pdf"
        if not path.exists():
            rows.append([lib, "output", *absent(results, lib, "sign")])
            continue
        with open(path, "rb") as fh:
            sigs = PdfFileReader(fh).embedded_signatures
            rows.append([lib, "one signature", len(sigs) == 1, f"{len(sigs)} signatures"])
            if not sigs:
                continue
            sig = sigs[0]
            ctx = ValidationContext(trust_roots=[root]) if root else None
            status = validate_pdf_signature(sig, ctx)
            rows.append([lib, "intact (digest matches)", bool(status.intact), ""])
            rows.append([lib, "valid with the test certificate", bool(status.valid and status.trusted),
                         "" if status.trusted else "not trusted"])
            rows.append([lib, "covers the whole file", str(status.coverage).endswith("ENTIRE_FILE"), str(status.coverage)])
            rows.append([lib, "signature type", True, f"{sig.sig_object.get('/SubFilter')}; digest {status.md_algorithm}"])
            rows.append([lib, "signing time recorded", status.signer_reported_dt is not None, str(status.signer_reported_dt or "")])
    return rows


def fill(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> list:
    rows, f = [], spec["fill"]
    for lib in libs:
        path = run / "pdf" / lib / "tools" / "fill" / f"{f['input']}.pdf"
        if not path.exists():
            rows.append([lib, "output", "", *absent(results, lib, "fill")])
            continue
        fields = PdfReader(str(path)).get_fields() or {}
        widgets = [w for p in fitz.open(path) for w in p.widgets()]
        for name, want in f["values"].items():
            v = fields.get(name, {}).get("/V")
            v = str(v).lstrip("/") if v is not None else ""
            ok = v == want or (want == "true" and v not in ("", "Off")) or (want == "false" and v in ("", "Off"))
            rows.append([lib, name, want, ok, f"pypdf reads {v!r}"])
            # the value must also be drawn: the widget's appearance stream must show it
            ws = [w for w in widgets if w.field_name == name]
            if want not in ("true", "false") and ws and ws[0].field_type in (fitz.PDF_WIDGET_TYPE_TEXT, fitz.PDF_WIDGET_TYPE_COMBOBOX):
                doc = fitz.open(path)
                ap = doc.xref_get_key(ws[0].xref, "AP/N")
                stream = doc.xref_stream(int(ap[1].split()[0])) if ap[0] == "xref" else b""
                drawn = want.encode("latin-1", "ignore") in stream or any(
                    want in suites.norm(p.get_text()) for p in fitz.open(path))
                rows.append([lib, name + " drawn", want, drawn, "" if drawn else "value not in the appearance stream"])
    return rows


def einvoice(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> list:
    rows, inv = [], spec["einvoice"]
    xml_in = (INPUTS / inv["xml"]).read_bytes()
    exe, env = standards.verapdf()
    for lib in libs:
        path = run / "pdf" / lib / "tools" / "einvoice" / "invoice.pdf"
        if not path.exists():
            rows.append([lib, "output", *absent(results, lib, "einvoice")])
            continue
        v = standards.validate(exe, env, path, "3b")
        rows.append([lib, "PDF/A-3b (veraPDF)", v["compliant"] is True, v["rules"][:200]])
        doc = fitz.open(path)
        names = doc.embfile_names()
        rows.append([lib, "factur-x.xml embedded", "factur-x.xml" in names, ", ".join(names)])
        if "factur-x.xml" in names:
            rows.append([lib, "XML byte for byte", doc.embfile_get("factur-x.xml") == xml_in, ""])
        reader = PdfReader(str(path))
        af = reader.trailer["/Root"].get("/AF")
        rel = ""
        if af is not None:
            for spec_ref in af.get_object():
                fs = spec_ref.get_object()
                if str(fs.get("/F", "")) == "factur-x.xml" or str(fs.get("/UF", "")) == "factur-x.xml":
                    rel = str(fs.get("/AFRelationship", ""))
        rows.append([lib, "AFRelationship Alternative", rel == "/Alternative", rel or "no /AF entry"])
        meta = reader.trailer["/Root"].get("/Metadata")
        xmp = meta.get_object().get_data().decode("utf-8", "replace") if meta is not None else ""
        ns = re.search(r'xmlns:(\w+)="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#"', xmp)
        rows.append([lib, "Factur-X XMP namespace", ns is not None, ""])
        for key, want in (("DocumentType", "INVOICE"), ("DocumentFileName", "factur-x.xml"),
                          ("ConformanceLevel", inv["profile"]), ("Version", "1.0")):
            m = re.search(rf"{ns.group(1) if ns else 'fx'}:{key}(?:>|=\")([^<\"]*)", xmp)
            got = m.group(1).strip() if m else ""
            rows.append([lib, f"XMP {key}", got == want, got])
    return rows


def pdfx(run: pathlib.Path, libs: list[str]) -> list:
    """PDF/X-1a:2001 structure. No free validator checks PDF/X; these are the standard's machine-checkable rules."""
    rows = []
    for lib in libs:
        path = run / "pdf" / lib / "standards" / "pdfx-1a.pdf"
        if not path.exists():
            continue
        reader = PdfReader(str(path))
        root, info = reader.trailer["/Root"], reader.trailer.get("/Info")
        info = info.get_object() if info is not None else {}
        intents = [o.get_object() for o in root.get("/OutputIntents", [])]
        gts = [o for o in intents if o.get("/S") == "/GTS_PDFX"]
        rows.append([lib, "GTS_PDFX output intent", bool(gts), f"{len(intents)} output intents"])
        rows.append([lib, "output condition identifier", bool(gts and gts[0].get("/OutputConditionIdentifier")),
                     str(gts[0].get("/OutputConditionIdentifier", "")) if gts else ""])
        rows.append([lib, "GTS_PDFXVersion", str(info.get("/GTS_PDFXVersion", "")).startswith("PDF/X-1"), str(info.get("/GTS_PDFXVersion", ""))])
        rows.append([lib, "GTS_PDFXConformance", str(info.get("/GTS_PDFXConformance", "")) == "PDF/X-1a:2001",
                     str(info.get("/GTS_PDFXConformance", ""))])
        rows.append([lib, "Trapped set", str(info.get("/Trapped", "")) in ("/True", "/False"), str(info.get("/Trapped", ""))])
        doc = fitz.open(path)
        fonts = [f for p in doc for f in p.get_fonts(full=True)]
        rows.append([lib, "all fonts embedded", all(f[1] != "n/a" for f in fonts), f"{len(fonts)} fonts"])
        objects = {x: doc.xref_object(x) for x in range(1, doc.xref_length())}
        transparency = any("/SMask" in o and "/SMask /None" not in o or re.search(r"/(CA|ca) 0?\.\d", o) for o in objects.values())
        rows.append([lib, "no transparency", not transparency, ""])
        # RGB anywhere it can be used: drawing operators in page and form content, colour spaces, images
        content = [x for p in doc for x in p.get_contents()] + [x for x, o in objects.items() if "/Subtype /Form" in o]
        rgb_ops = sum(len(re.findall(rb"(?:[\d.]+\s+){3}(?:rg|RG)\s", doc.xref_stream(x) or b"")) for x in content)
        rgb_spaces = [x for x, o in objects.items() if "/DeviceRGB" in o]
        rgb_icc = [x for x, o in objects.items() if re.search(r"/N 3(?!\d)", o) and (doc.xref_stream(x) or b"")[16:20] == b"RGB "]
        rgb_images = [x for x, o in objects.items() if "/Subtype /Image" in o and any(f"{i} 0 R" in o for i in rgb_icc)]
        detail = "; ".join(filter(None, [f"{rgb_ops} rg/RG operators" if rgb_ops else "",
                                         f"DeviceRGB in objects {rgb_spaces[:5]}" if rgb_spaces else "",
                                         f"{len(rgb_images)} images with an RGB ICC profile" if rgb_images else ""]))
        rows.append([lib, "no RGB colour (CMYK/grey only)", not detail, detail])
    return rows


def write(results: pathlib.Path, name: str, header: list, rows: list) -> None:
    with open(results / name, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)


def run_all(run: pathlib.Path, results: pathlib.Path) -> None:
    manifest = json.loads((ROOT / "fixtures" / "fixtures.json").read_text(encoding="utf-8"))
    spec = manifest["tools"]
    libs = sorted(d.name for d in (run / "pdf").iterdir() if d.is_dir())
    if (results / "tools_runs.csv").exists():
        write(results, "merge_split.csv", ["library", "operation", "check", "pass", "detail"], merge_split(run, results, libs, spec))
        write(results, "watermark.csv", ["library", "check", "pass", "detail"], watermark(run, results, libs, spec))
        write(results, "signature.csv", ["library", "check", "pass", "detail"], signature(run, results, libs, spec))
        write(results, "fill.csv", ["library", "field", "value", "pass", "detail"], fill(run, results, libs, spec))
        write(results, "einvoice.csv", ["library", "check", "pass", "detail"], einvoice(run, results, libs, spec))
    if any((run / "pdf" / lib / "standards" / "pdfx-1a.pdf").exists() for lib in libs):
        write(results, "pdfx.csv", ["library", "check", "pass", "detail"], pdfx(run, libs))


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--results", required=True)
    a = ap.parse_args()
    run_all(pathlib.Path(a.run), pathlib.Path(a.results))
