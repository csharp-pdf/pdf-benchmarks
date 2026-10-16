"""Analysis of the suites beyond HTML to PDF (METHODOLOGY.md, 4.2 and 4.12-4.17). Writes, per run:

  parallel.csv     every PDF converted with 8 conversions in flight, compared with the sequential one
  errors_checks.csv  what each error case produced, against what it should produce
  extraction.csv   text extraction accuracy against each page's real text (and PyMuPDF as a reference extractor)
  search.csv       search hits against the pages an independent search finds
  raster.csv       page images: size at the asked resolution and similarity to PDFium's rendering
  word.csv         Word to PDF against Microsoft Word's own PDF of the same document
  redaction.csv    redacted terms gone (two independent extractors), covered, and the rest of the text kept
"""
import csv
import io
import json
import pathlib
import re
import unicodedata
from collections import Counter

import cv2
import fitz
import numpy as np
import pypdfium2 as pdfium
from pypdf import PdfReader

ROOT = pathlib.Path(__file__).resolve().parents[1]
INPUTS = ROOT / "fixtures" / "inputs"
# Evaluation stamps of trial editions; never part of the content compared (METHODOLOGY.md, section 2)
BANNERS = ["Demo Version - Select.Pdf SDK"]


def unsupported_tools(results: pathlib.Path) -> dict:
    """(library, tool) -> reason, for tools the library has no API for (from the runner's tools_runs.csv)."""
    out = {}
    if (results / "tools_runs.csv").exists():
        for r in csv.DictReader(open(results / "tools_runs.csv", encoding="utf-8")):
            if r["unsupported"].lower() == "true":
                out[(r["library"], r["tool"])] = r["error"]
    return out


def write(results: pathlib.Path, name: str, header: list, rows: list) -> None:
    with open(results / name, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)


def norm(s: str) -> str:
    s = unicodedata.normalize("NFKC", s)
    for banner in BANNERS:
        s = s.replace(banner, "")
    return s


def bigram_f1(truth: str, got: str) -> float:
    """Overlap of character pairs, whitespace removed: insensitive to line breaks and spacing, sensitive to
    missing, wrong or reordered characters (a right-to-left line extracted backwards scores low)."""
    a, b = re.sub(r"\s+", "", norm(truth)), re.sub(r"\s+", "", norm(got))
    ca, cb = Counter(a[i:i + 2] for i in range(len(a) - 1)), Counter(b[i:i + 2] for i in range(len(b) - 1))
    if not ca or not cb:
        return 0.0
    common = sum((ca & cb).values())
    return 2 * common / (sum(ca.values()) + sum(cb.values()))


def ssim(a: np.ndarray, b: np.ndarray) -> float:
    a, b = a.astype(np.float64), b.astype(np.float64)
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    blur = lambda x: cv2.GaussianBlur(x, (11, 11), 1.5)
    ma, mb = blur(a), blur(b)
    va, vb, cov = blur(a * a) - ma ** 2, blur(b * b) - mb ** 2, blur(a * b) - ma * mb
    return float((((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma ** 2 + mb ** 2 + c1) * (va + vb + c2))).mean())


def gray(page: fitz.Page, dpi: int, mask_banner: bool = True) -> np.ndarray:
    pix = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY, alpha=False)
    img = np.frombuffer(pix.samples, np.uint8).reshape(pix.height, pix.width).copy()
    if mask_banner:
        z = dpi / 72
        for r in [r for banner in BANNERS for r in page.search_for(banner)]:
            img[int(r.y0 * z) - 3:int(r.y1 * z) + 3, int(r.x0 * z) - 3:int(r.x1 * z) + 3] = 255
    return img


def fit(img: np.ndarray, shape: tuple) -> np.ndarray:
    """Crops or pads (white) an image to the reference's height and width - for a pixel of rounding difference,
    where resampling would blur the whole image."""
    h, w = shape[:2]
    out = np.full((h, w) + img.shape[2:], 255, img.dtype)
    out[:min(h, img.shape[0]), :min(w, img.shape[1])] = img[:h, :w]
    return out


def same_size(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    return b if b.shape == a.shape else cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA)


# ---- parallel ---------------------------------------------------------------------------------------------

def parallel(run: pathlib.Path, results: pathlib.Path, libs: list[str]) -> None:
    rows = []
    for lib in libs:
        for mode_dir in sorted((run / "pdf" / lib / "parallel").glob("*")):
            for f in sorted(mode_dir.glob("*.pdf")):
                fixture, copy = f.stem.rsplit("-", 1)
                seq = run / "pdf" / lib / f"{fixture}.pdf"
                if not seq.exists():
                    continue
                a, b = fitz.open(seq), fitz.open(f)
                text_same = norm("".join(p.get_text() for p in a)).split() == norm("".join(p.get_text() for p in b)).split()
                diff = 0.0
                for pa, pb in zip(a, b):
                    ga, gb = gray(pa, 72), gray(pb, 72)
                    gb = same_size(ga, gb)
                    diff = max(diff, 100.0 * float((np.abs(ga.astype(int) - gb.astype(int)) > 32).mean()))
                ok = a.page_count == b.page_count and text_same and diff < 1.0
                rows.append([lib, mode_dir.name, fixture, copy, a.page_count, b.page_count, text_same, f"{diff:.2f}", ok])
    write(results, "parallel.csv", ["library", "mode", "fixture", "copy", "pages_sequential", "pages_parallel",
                                    "text_same", "max_pixel_diff_pct", "same"], rows)


# ---- errors -----------------------------------------------------------------------------------------------

def errors(run: pathlib.Path, results: pathlib.Path, manifest: dict) -> None:
    src = results / "errors.csv"
    if not src.exists():
        return
    cases = {c["case"]: c for c in manifest["errors"]}
    rows = []
    for r in csv.DictReader(open(src, encoding="utf-8")):
        if r["outcome"] == "unsupported":   # no HTML conversion: no error cases to run
            rows.append([r["library"], "all", "", "unsupported", "", "", "n/a", r["error"]])
            continue
        c = cases[r["case"]]
        expect, outcome = c["expect"], r["outcome"]
        detail, ok = "", outcome != "hung" and r["recovered"] == "True"
        if expect == "pdf":
            ok = ok and outcome == "pdf"
            pdf_path = run / "pdf" / r["library"] / "errors" / f"{r['case']}.pdf"
            if outcome == "pdf" and pdf_path.exists():
                d = fitz.open(pdf_path)
                text = norm("".join(p.get_text() for p in d))
                if c.get("text") and c["text"] not in text:
                    ok, detail = False, "page text missing"
                if c.get("images") and len({i[0] for p in d for i in p.get_images()}) < c["images"]:
                    ok, detail = False, "image missing"
        elif expect == "error":
            ok = ok and outcome == "error"
        rows.append([r["library"], r["case"], expect, outcome, r["ms"], r["recovered"], ok, detail or r["error"]])
    write(results, "errors_checks.csv", ["library", "case", "expected", "outcome", "ms", "recovered", "pass", "detail"], rows)


# ---- tools ------------------------------------------------------------------------------------------------

def extraction(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> None:
    rows = []
    for name in spec["extract"]:
        truth = (INPUTS / "pdf" / f"{name}.txt").read_text(encoding="utf-8")
        ref = fitz.open(INPUTS / "pdf" / f"{name}.pdf")
        rows.append(["pymupdf (reference)", name, ref.page_count, ref.page_count,
                     f"{bigram_f1(truth, ''.join(p.get_text() for p in ref)):.4f}"])
        for lib in libs:
            out = run / "pdf" / lib / "tools" / "extract" / f"{name}.txt"
            if out.exists():
                got = out.read_text(encoding="utf-8")
                rows.append([lib, name, ref.page_count, got.count("\f") + 1, f"{bigram_f1(truth, got):.4f}"])
    write(results, "extraction.csv", ["library", "input", "pages_expected", "pages", "text_f1"], rows)


def _unspaced(text: str) -> str:
    # The text layer of Chromium-printed Arabic breaks words into pieces with spaces between them
    # ("الح قو ق" for "الحقوق"); compare without whitespace or zero-width characters.
    return "".join(ch for ch in text if not ch.isspace() and ch not in "​‌‍‎‏﻿")


def pdfium_pages(pdf: pathlib.Path, term: str) -> list[int]:
    """Pages where the term occurs - the independent reference for search. A page counts when PDFium's
    own text search finds the term, or when the page's text (PDFium) holds it - written either way
    round - once the spaces the text layer inserted are removed. On rtl.pdf PDFium's search found page 1
    only, while the input's ground truth (rtl.txt) has the term twice on page 2 as well ("والحقوق",
    "الحقوق"): PDFium returns that page's Arabic in visual order ("قوقحلاو"). PyMuPDF's search misses
    right-to-left text in these inputs altogether."""
    doc, pages, needle = pdfium.PdfDocument(str(pdf)), [], _unspaced(term)
    for i in range(len(doc)):
        textpage = doc[i].get_textpage()
        text = _unspaced(textpage.get_text_range())
        if textpage.search(term, match_case=True).get_next() is not None or needle in text or needle[::-1] in text:
            pages.append(i + 1)
    return pages


def search(results: pathlib.Path, spec: dict) -> None:
    src = results / "tools_runs.csv"
    rows = []
    for r in csv.DictReader(open(src, encoding="utf-8")):
        if r["tool"] != "search":
            continue
        name, term = r["input"].split(": ", 1)
        expected = pdfium_pages(INPUTS / "pdf" / f"{name}.pdf", term)
        if r["unsupported"].lower() == "true":
            rows.append([r["library"], name, term, expected, "", "", "not supported"])
            continue
        hits = json.loads(r["result"]) if r["result"] else []
        found = sorted(set(hits))
        rows.append([r["library"], name, term, expected, found, len(hits), found == expected])
    write(results, "search.csv", ["library", "input", "term", "expected_pages", "found_pages", "hits", "pass"], rows)


def raster(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> None:
    dpi = spec["rasterize"]["dpi"]
    rows = []
    for name in spec["rasterize"]["inputs"]:
        src = INPUTS / "pdf" / f"{name}.pdf"
        ref_doc = pdfium.PdfDocument(str(src))
        refs = [np.array(ref_doc[i].render(scale=dpi / 72).to_pil().convert("L")) for i in range(len(ref_doc))]
        mu = fitz.open(src)
        refs_mu = [np.frombuffer((pix := mu[i].get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)).samples, np.uint8)
                   .reshape(pix.height, pix.width) for i in range(mu.page_count)]
        for lib in libs:
            folder = run / "pdf" / lib / "tools" / "raster" / name
            images = sorted(folder.glob("page-*.png")) if folder.exists() else []
            for i, path in enumerate(images):
                color = cv2.imread(str(path))
                ref = refs[i].copy() if i < len(refs) else None
                ref_mu = fit(refs_mu[i], ref.shape) if ref is not None else None
                size_ok = ref is not None and abs(color.shape[0] - ref.shape[0]) <= 2 and abs(color.shape[1] - ref.shape[1]) <= 2
                if ref is not None:
                    color = fit(color, ref.shape) if size_ok else cv2.resize(color, (ref.shape[1], ref.shape[0]), interpolation=cv2.INTER_AREA)
                    red = (color[:, :, 2] > 180) & (color[:, :, 1] < 90) & (color[:, :, 0] < 130)   # trial banners: red / #FF0066
                    red = cv2.dilate(red.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
                img = cv2.cvtColor(color, cv2.COLOR_BGR2GRAY)
                if ref is not None and red.any():
                    img[red] = 255
                    ref[red] = 255
                    ref_mu[red] = 255
                # scored at half resolution, both images downscaled alike: glyph anti-aliasing differs between
                # renderers at the pixel level, and at full resolution it would outweigh what is on the page
                half = lambda x: cv2.resize(x, (x.shape[1] // 2, x.shape[0] // 2), interpolation=cv2.INTER_AREA)
                score = ssim(half(ref), half(img)) if ref is not None else 0.0
                score_mu = ssim(half(ref_mu), half(img)) if ref is not None else 0.0
                h0, w0 = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE).shape
                rows.append([lib, name, i + 1, w0, h0, ref.shape[1] if ref is not None else "",
                             ref.shape[0] if ref is not None else "", size_ok, f"{score:.4f}", f"{score_mu:.4f}"])
            if len(images) != len(refs):
                rows.append([lib, name, "pages", len(images), "", len(refs), "", False, ""])
    write(results, "raster.csv", ["library", "input", "page", "width", "height", "expected_width", "expected_height",
                                  "size_ok", "ssim_vs_pdfium", "ssim_vs_mupdf"], rows)


def word(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> None:
    rows = []
    for name in spec["word"]:
        ref_path = INPUTS / "word" / f"{name}.word.pdf"
        if not ref_path.exists():
            continue
        ref = fitz.open(ref_path)
        truth = "".join(p.get_text() for p in ref)
        for lib in libs:
            path = run / "pdf" / lib / "tools" / "word" / f"{name}.pdf"
            if not path.exists():
                continue
            d = fitz.open(path)
            scores = [ssim(gray(ref[i], 50, False), same_size(gray(ref[i], 50, False), gray(d[i], 50)))
                      for i in range(min(ref.page_count, d.page_count))]
            fonts = [f for p in d for f in p.get_fonts(full=True)]
            rows.append([lib, name, ref.page_count, d.page_count, f"{bigram_f1(truth, ''.join(p.get_text() for p in d)):.4f}",
                         f"{np.mean(scores):.4f}" if scores else "", f"{min(scores):.4f}" if scores else "",
                         all(f[1] != "n/a" for f in fonts), len({i[0] for p in d for i in p.get_images()})])
    write(results, "word.csv", ["library", "input", "pages_word", "pages", "text_f1_vs_word", "ssim_mean", "ssim_worst",
                                "fonts_embedded", "images"], rows)


def redaction(run: pathlib.Path, results: pathlib.Path, libs: list[str], spec: dict) -> None:
    red = spec["redact"]
    src = fitz.open(INPUTS / "pdf" / f"{red['input']}.pdf")
    rows, unsupported = [], unsupported_tools(results)
    for lib in libs:
        path = run / "pdf" / lib / "tools" / "redact" / f"{red['input']}.pdf"
        if not path.exists():
            if (lib, "redact") in unsupported:
                rows.append([lib, "redacted file", "", "n/a", "not supported: " + unsupported[(lib, "redact")]])
            else:
                rows.append([lib, "redacted file", "", False, "no output"])
            continue
        d = fitz.open(path)
        t_mupdf = norm("".join(p.get_text() for p in d))
        t_pypdf = norm("".join((p.extract_text() or "") for p in PdfReader(str(path)).pages))
        for term in red["terms"]:
            gone = term not in t_mupdf and term not in t_pypdf and term.replace(" ", "") not in t_pypdf.replace(" ", "")
            rows.append([lib, "removed", term, gone, "" if gone else "still extractable"])
            covered, total = 0, 0
            for i, page in enumerate(src):
                for r in page.search_for(term):
                    total += 1
                    band = fitz.Rect(r.x0 + r.width * 0.1, r.y0 + r.height * 0.35, r.x1 - r.width * 0.1, r.y1 - r.height * 0.35)
                    clip = d[i].get_pixmap(dpi=144, clip=band, colorspace=fitz.csGRAY)
                    px = np.frombuffer(clip.samples, np.uint8)
                    covered += int(px.size > 0 and (px < 80).mean() > 0.9)   # the middle of where the text was is filled
            rows.append([lib, "covered", term, total > 0 and covered == total, f"{covered}/{total} occurrences covered"])
        for term in red["keep"]:
            kept = term in t_mupdf
            rows.append([lib, "kept", term, kept, "" if kept else "unrelated text was removed too"])
        rows.append([lib, "pages", src.page_count, d.page_count == src.page_count, f"{d.page_count} pages"])
    write(results, "redaction.csv", ["library", "check", "term", "pass", "detail"], rows)


def run_all(run: pathlib.Path, results: pathlib.Path) -> None:
    manifest = json.loads((ROOT / "fixtures" / "fixtures.json").read_text(encoding="utf-8"))
    libs = sorted(d.name for d in (run / "pdf").iterdir() if d.is_dir())
    if any((run / "pdf" / lib / "parallel").exists() for lib in libs):
        parallel(run, results, libs)
    errors(run, results, manifest)
    if (results / "tools_runs.csv").exists():
        spec = manifest["tools"]
        extraction(run, results, libs, spec)
        search(results, spec)
        raster(run, results, libs, spec)
        word(run, results, libs, spec)
        redaction(run, results, libs, spec)


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--results", required=True)
    a = ap.parse_args()
    run_all(pathlib.Path(a.run), pathlib.Path(a.results))
