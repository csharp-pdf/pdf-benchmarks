"""Analyses the PDFs of a run (METHODOLOGY.md, sections 4.4-4.8) and writes:

  output.csv     library, fixture, bytes, pages, fonts, fonts_embedded, text_chars
  checks.csv     library, fixture, check, expected, pass, detail
  fidelity.csv   library, fixture, pages, height_ratio, ssim_mean, ssim_worst, pixel_diff_pct, max_offset_px, masked_pct
  fidelity_tiles.csv  per-tile values behind fidelity.csv
and side-by-side images under <run>/images/.

    python analysis/analyze.py --run out/<run> --results results/<run>

Fidelity, in short: each PDF page is rasterised at the scale that maps the page width to the
viewport width (1024 px), the pages are stacked into one tall image, and that image is compared
tile by tile with the reference screenshot. A page break can push content down; each tile is
therefore aligned to the reference by searching a small vertical window around the previous
tile's offset before it is scored. Evaluation watermarks are masked in both images.
"""
import argparse
import csv
import json
import pathlib
import unicodedata

import cv2
import fitz  # PyMuPDF
import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[1]
TILE = 256            # tile height in reference pixels
SEARCH = 200          # +/- pixels searched around the previous tile's offset
WHITE = 245           # a row is blank when every pixel is at least this bright
DIFF = 32             # a pixel differs when a channel differs by more than this
# Text a library stamps on trial output; masked before images are compared (METHODOLOGY.md, section 2).
WATERMARKS = {
    "selectpdf": ["Demo Version - Select.Pdf SDK"],
    "ironpdf": [],
}


def rasterise(pdf: fitz.Document, width: int, library: str):
    """Stacks all pages into one RGB image `width` pixels wide; returns it with a watermark mask."""
    pages, masks = [], []
    for page in pdf:
        zoom = width / page.rect.width
        pix = page.get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=False)
        img = np.frombuffer(pix.samples, np.uint8).reshape(pix.height, pix.width, 3)
        img = cv2.resize(img, (width, round(pix.height * width / pix.width)), interpolation=cv2.INTER_AREA) if pix.width != width else img.copy()
        mask = np.zeros(img.shape[:2], bool)
        for text in WATERMARKS.get(library, []):
            for r in page.search_for(text):
                x0, y0, x1, y1 = (int(v * zoom) for v in (r.x0 - 4, r.y0 - 4, r.x1 + 4, r.y1 + 4))
                mask[max(y0, 0):y1, max(x0, 0):x1] = True
        pages.append(img)
        masks.append(mask)
    # A library may move an element (an image, a table row) to the next page rather than split it,
    # leaving blank space at the bottom of the page. The screenshot has no page breaks, so that space
    # is removed before stacking; the page count still records how the content was paginated.
    for i in range(len(pages) - 1):
        keep = content_height(pages[i]) or pages[i].shape[0]
        pages[i], masks[i] = pages[i][:keep], masks[i][:keep]
    return np.vstack(pages), np.vstack(masks)


def content_height(img: np.ndarray) -> int:
    rows = np.where((img.min(axis=2) < WHITE).any(axis=1))[0]
    return int(rows[-1]) + 1 if len(rows) else 0


def ssim(a: np.ndarray, b: np.ndarray) -> float:
    """Mean structural similarity of two grayscale images (Wang et al. 2004, Gaussian window)."""
    a, b = a.astype(np.float64), b.astype(np.float64)
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    blur = lambda x: cv2.GaussianBlur(x, (11, 11), 1.5)
    mu_a, mu_b = blur(a), blur(b)
    va, vb, cov = blur(a * a) - mu_a ** 2, blur(b * b) - mu_b ** 2, blur(a * b) - mu_a * mu_b
    s = ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (va + vb + c2))
    return float(s.mean())


def compare(ref: np.ndarray, lib: np.ndarray, mask: np.ndarray):
    """Tile-by-tile comparison over the reference's height. Returns per-tile rows."""
    h = ref.shape[0]
    pad = max(0, h + 2 * SEARCH + 2 * TILE - lib.shape[0])
    lib = np.vstack([lib, np.full((pad, lib.shape[1], 3), 255, np.uint8)])
    mask = np.vstack([mask, np.zeros((pad, mask.shape[1]), bool)])
    ref_g, lib_g = cv2.cvtColor(ref, cv2.COLOR_RGB2GRAY), cv2.cvtColor(lib, cv2.COLOR_RGB2GRAY)
    tiles, offset = [], 0
    for y in range(0, h, TILE):
        t = min(TILE, h - y)
        r_tile = ref_g[y:y + t]
        lo, hi = max(-y, offset - SEARCH), offset + SEARCH
        if r_tile.std() > 1:   # align on content, at full resolution; a blank tile keeps the previous offset
            region = lib_g[y + lo:y + hi + t]
            if region.shape[0] >= t:
                scores = cv2.matchTemplate(region, r_tile, cv2.TM_CCOEFF_NORMED)[:, 0]
                offset = lo + int(np.argmax(scores))
        l_rgb = lib[y + offset:y + offset + t].copy()
        r_rgb = ref[y:y + t]
        m = mask[y + offset:y + offset + t]
        l_rgb[m] = r_rgb[m]   # masked pixels count as identical in both images
        diff = (np.abs(l_rgb.astype(int) - r_rgb.astype(int)).max(axis=2) > DIFF)
        tiles.append({"y": y, "offset": offset, "ssim": ssim(cv2.cvtColor(r_rgb, cv2.COLOR_RGB2GRAY), cv2.cvtColor(l_rgb, cv2.COLOR_RGB2GRAY)),
                      "diff_pct": 100.0 * diff.mean(), "masked_pct": 100.0 * m.mean()})
    return tiles


def side_by_side(ref: np.ndarray, lib: np.ndarray, path: pathlib.Path, rows: int = 3000) -> None:
    h = min(rows, max(ref.shape[0], lib.shape[0]))
    def top(img):
        img = img[:h]
        return np.vstack([img, np.full((h - img.shape[0], img.shape[1], 3), 255, np.uint8)])
    gap = np.full((h, 16, 3), 200, np.uint8)
    both = np.hstack([top(ref), gap, top(lib)])
    both = cv2.resize(both, (both.shape[1] // 2, both.shape[0] // 2), interpolation=cv2.INTER_AREA)
    path.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(path), cv2.cvtColor(both, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 80])


def norm(s: str) -> str:
    return unicodedata.normalize("NFKC", s).replace(" ", " ")


def logo_on_every_page(pdf: fitz.Document) -> tuple[bool, str]:
    """The header logo (fixtures/html-header-footer/logo.svg: a #2563eb rounded square with a white triangle and
    "ACME") must be drawn in the top 90 pt of every page - as the logo's blue shape, or as a picture of it at
    least 30 pt wide. A browser's broken-image icon with the alt text "ACME" does not count."""
    blue = (0x25 / 255, 0x63 / 255, 0xEB / 255)
    def is_blue(c):
        return c is not None and len(c) == 3 and all(abs(x - y) < 0.04 for x, y in zip(c, blue))
    missing = []
    for i, p in enumerate(pdf):
        shape = any(d["rect"].y1 < 90 and is_blue(d.get("fill")) and d["rect"].height >= 5   # the square, not the
                    and d["rect"].width < p.rect.width / 2 for d in p.get_drawings())          # header's blue rule
        picture = any(info["bbox"][1] < 90 and info["bbox"][2] - info["bbox"][0] >= 30 for info in p.get_image_info())
        if not (shape or picture):
            missing.append(i + 1)
    return not missing, f"missing on pages {missing[:8]}" if missing else ""


def encryption_checks(pdf: fitz.Document, spec: dict):
    """The file must need the user password, open with it, deny printing and copying, and say how it is encrypted."""
    needs = bool(pdf.needs_pass)
    yield "needsPassword", True, needs, ""
    if not needs:
        return
    probe = fitz.open(pdf.name)
    yield "wrongPasswordRejected", True, probe.authenticate("not-the-password") == 0, ""
    yield "userPasswordOpens", True, probe.authenticate(spec["userPassword"]) != 0, ""
    perm = probe.permissions
    yield "printDenied", True, not (perm & fitz.PDF_PERM_PRINT), f"permissions {perm:#x}"
    yield "copyDenied", True, not (perm & fitz.PDF_PERM_COPY), ""
    method = (probe.metadata or {}).get("encryption", "") or ""
    yield "algorithm", "AES 256", "AES" in method and "256" in method, method


def run_checks(fixture: dict, pdf: fitz.Document):
    if fixture.get("encrypt"):
        yield from encryption_checks(pdf, fixture["encrypt"])
        pdf.authenticate(fixture["encrypt"]["userPassword"])
        return
    text = norm("".join(p.get_text() for p in pdf))
    squashed = "".join(text.split())
    fonts = {f[3] for p in pdf for f in p.get_fonts(full=True)}
    for check in fixture.get("checks", []):
        kind, expected = next(iter(check.items()))
        if kind == "text":
            want = norm(expected)
            ok = want in text or "".join(want.split()) in squashed or want[::-1] in text   # RTL text may extract in visual order
            yield kind, expected, ok, ""
        elif kind == "pages":
            yield kind, expected, pdf.page_count == expected, f"{pdf.page_count} pages"
        elif kind == "images":
            n = len({img[0] for p in pdf for img in p.get_images(full=True)})
            yield kind, expected, n >= expected, f"{n} images"
        elif kind == "links":
            n = sum(1 for p in pdf for link in p.get_links() if link.get("uri"))
            yield kind, expected, n >= expected, f"{n} links"
        elif kind == "pageNumbers":   # every page carries its own "Page N of M"
            n = pdf.page_count
            missing = [i + 1 for i, p in enumerate(pdf) if f"Page {i + 1} of {n}" not in norm(p.get_text())]
            yield kind, f"Page N of {n} on every page", not missing, f"missing on pages {missing[:8]}" if missing else f"{n} pages"
        elif kind == "everyPage":   # a header/footer text on every page
            missing = [i + 1 for i, p in enumerate(pdf) if norm(expected) not in norm(p.get_text())]
            yield kind, expected, not missing, f"missing on pages {missing[:8]}" if missing else f"{pdf.page_count} pages"
        elif kind == "imageEveryPage":   # the header logo on every page (an image or a vector drawing)
            yield kind, "logo on every page", *logo_on_every_page(pdf)
        elif kind == "outline":
            n = len(pdf.get_toc())
            yield kind, expected, n >= expected, f"{n} bookmarks"
        elif kind == "internalLinks":   # links that jump to a page of the same document
            n = sum(1 for p in pdf for link in p.get_links() if link.get("kind") in (fitz.LINK_GOTO, fitz.LINK_NAMED))
            yield kind, expected, n >= expected, f"{n} internal links"
        elif kind == "formFields":
            n = sum(1 for p in pdf for _ in p.widgets())
            yield kind, expected, n >= expected, f"{n} form field widgets"
        elif kind == "noImages":   # vector content must not have been turned into a bitmap
            n = len({img[0] for p in pdf for img in p.get_images(full=True)})
            yield kind, "0 images", n == 0, f"{n} images"
        elif kind == "fontsEmbedded":
            missing = [f for f in expected if not any(f.lower() in name.lower().replace(" ", "") for name in fonts)]
            yield kind, "+".join(expected), not missing, "missing " + ", ".join(missing) if missing else ""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True, help="out/<run>: has pdf/<library>/ and reference/")
    ap.add_argument("--results", required=True, help="results/<run>: where the CSV files go")
    ap.add_argument("--checks-only", action="store_true", help="content checks only, no reference images needed")
    args = ap.parse_args()
    run, results = pathlib.Path(args.run), pathlib.Path(args.results)
    results.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / "fixtures" / "fixtures.json").read_text(encoding="utf-8"))
    width = manifest["viewportWidth"]
    libraries = sorted(d.name for d in (run / "pdf").iterdir() if d.is_dir())

    out_rows, check_rows, fid_rows, tile_rows = [], [], [], []
    unsupported = {}   # (library, fixture) -> reason, from the runner's outputs.csv
    if (results / "outputs.csv").exists():
        for r in csv.DictReader(open(results / "outputs.csv", encoding="utf-8")):
            if r.get("unsupported", "").lower() == "true":
                unsupported[(r["library"], r["fixture"])] = r["error"]
    for lib in libraries:
        for fx in manifest["fixtures"]:
            name = fx["name"]
            path = run / "pdf" / lib / f"{name}.pdf"
            if not path.exists():
                if (lib, name) in unsupported:   # the library has no API for this: not a failure
                    check_rows.append([lib, name, "converted", "", "n/a", "not supported: " + unsupported[(lib, name)]])
                else:
                    check_rows.append([lib, name, "converted", "", False, "no PDF"])
                continue
            pdf = fitz.open(path)
            if fx.get("encrypt"):
                for row in run_checks(fx, pdf):   # also leaves the document opened with the user password
                    check_rows.append([lib, name, *row])
                continue
            fonts = [f for p in pdf for f in p.get_fonts(full=True)]
            out_rows.append([lib, name, path.stat().st_size, pdf.page_count, len({f[3] for f in fonts}),
                             all(f[1] != "n/a" for f in fonts), len("".join(p.get_text() for p in pdf).strip())])
            for kind, expected, ok, detail in run_checks(fx, pdf):
                check_rows.append([lib, name, kind, expected, ok, detail])

            if args.checks_only or fx.get("sequence") or fx.get("fidelity") is False:
                continue
            if fx.get("media") == "print":   # paged-media: compare with Chromium's own print output, page by page
                ref_pdf = fitz.open(run / "reference" / f"{name}.pdf")
                ref, _ = rasterise(ref_pdf, width, "reference")
            else:
                ref = cv2.cvtColor(cv2.imread(str(run / "reference" / f"{name}.png")), cv2.COLOR_BGR2RGB)
            if ref.shape[1] != width:   # a reference must be exactly the viewport wide
                raise SystemExit(f"reference for {name} is {ref.shape[1]} px wide, expected {width}")
            img, mask = rasterise(pdf, width, lib)
            tiles = compare(ref, img, mask)
            for t in tiles:
                tile_rows.append([lib, name, t["y"], t["offset"], f"{t['ssim']:.4f}", f"{t['diff_pct']:.2f}"])
            ref_h, lib_h = content_height(ref), content_height(img)
            fid_rows.append([lib, name, pdf.page_count, f"{lib_h / ref_h:.3f}" if ref_h else "",
                             f"{np.mean([t['ssim'] for t in tiles]):.4f}", f"{min(t['ssim'] for t in tiles):.4f}",
                             f"{np.mean([t['diff_pct'] for t in tiles]):.2f}", max(abs(t["offset"]) for t in tiles),
                             f"{np.mean([t['masked_pct'] for t in tiles]):.2f}"])
            side_by_side(ref, img, run / "images" / f"{name}--{lib}.jpg")
            print(f"{lib:10} {name:16} ssim {fid_rows[-1][4]}  worst {fid_rows[-1][5]}  diff {fid_rows[-1][6]}%", flush=True)

    def write(name, header, rows):
        with open(results / name, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(header)
            w.writerows(rows)

    write("output.csv", ["library", "fixture", "bytes", "pages", "fonts", "fonts_embedded", "text_chars"], out_rows)
    write("checks.csv", ["library", "fixture", "check", "expected", "pass", "detail"], check_rows)
    write("fidelity.csv", ["library", "fixture", "pages", "height_ratio", "ssim_mean", "ssim_worst", "pixel_diff_pct",
                           "max_offset_px", "masked_pct"], fid_rows)
    write("fidelity_tiles.csv", ["library", "fixture", "y", "offset", "ssim", "pixel_diff_pct"], tile_rows)
    if not args.checks_only and (run / "pdf").exists() and any((d / "standards").is_dir() for d in (run / "pdf").iterdir()):
        import standards
        standards.run(run, results)

    if not args.checks_only:
        import suites
        suites.run_all(run, results)
        import features
        features.run_all(run, results)

    applicable = [r for r in check_rows if r[4] != "n/a"]
    failed = [r for r in applicable if not r[4]]
    print(f"checks: {len(applicable) - len(failed)}/{len(applicable)} passed ({len(check_rows) - len(applicable)} not supported)")
    for r in failed:
        print("  FAIL", *r)
    return [r for r in failed if r[2] == "converted"]   # a missing PDF (not "not supported") is an error


if __name__ == "__main__":
    failed = main()
    raise SystemExit(1 if failed else 0)
