"""Builds fixtures/_shared/: third-party CSS/JS and subsetted static fonts.

The output is committed, so a benchmark run never touches the network. Re-run
this only to update a dependency; record the change in _shared/SOURCE.md.

    python fixtures/_tools/udhr.py          # first: the rtl/cjk pages decide the CJK/Arabic glyph sets
    python fixtures/_tools/build_shared.py

Needs: fontTools, brotli (pip install fonttools brotli).
"""
import io
import pathlib
import urllib.request

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

ROOT = pathlib.Path(__file__).resolve().parents[1]
SHARED = ROOT / "_shared"

JSDELIVR = "https://cdn.jsdelivr.net/npm/"
VENDOR = {
    # folder: (package@version, [files to copy], licence file)
    "bootstrap": ("bootstrap@5.3.8", ["dist/css/bootstrap.min.css"], "LICENSE"),
    "bootstrap-icons": ("bootstrap-icons@1.13.1",
                        ["font/bootstrap-icons.min.css", "font/fonts/bootstrap-icons.woff2"], "LICENSE"),
    "chartjs": ("chart.js@4.5.1", ["dist/chart.umd.min.js"], "LICENSE.md"),
}

GOOGLE_FONTS = "https://raw.githubusercontent.com/google/fonts/main/ofl/"
# family: (google/fonts folder, variable font file, weights, glyph set)
FONTS = {
    "Inter": ("inter", "Inter[opsz,wght].ttf", [400, 700], "latin"),
    "Lora": ("lora", "Lora[wght].ttf", [400, 700], "latin"),
    "JetBrains Mono": ("jetbrainsmono", "JetBrainsMono[wght].ttf", [400], "latin"),
    "Noto Sans Arabic": ("notosansarabic", "NotoSansArabic[wdth,wght].ttf", [400, 700], "rtl"),
    "Noto Sans JP": ("notosansjp", "NotoSansJP[wght].ttf", [400, 700], "cjk"),
    "Noto Sans SC": ("notosanssc", "NotoSansSC[wght].ttf", [400, 700], "cjk"),
    "Noto Sans KR": ("notosanskr", "NotoSansKR[wght].ttf", [400, 700], "cjk"),
    "Noto Sans Devanagari": ("notosansdevanagari", "NotoSansDevanagari[wdth,wght].ttf", [400], "scripts"),
    "Noto Sans Thai": ("notosansthai", "NotoSansThai[wdth,wght].ttf", [400], "scripts"),
    "Noto Color Emoji": ("notocoloremoji", "NotoColorEmoji-Regular.ttf", [400], "scripts"),   # not variable: bitmap colour font
}
LATIN = (list(range(0x20, 0x7F)) + list(range(0xA0, 0x100)) + list(range(0x2010, 0x2028))
         + list(range(0x2030, 0x203B)) + [0x20AC, 0x2122, 0x2212])


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url) as r:
        return r.read()


def page_chars(*folders: str) -> set[int]:
    chars: set[int] = set(LATIN)
    for f in folders:
        chars.update(ord(c) for c in (ROOT / f / "index.html").read_text(encoding="utf-8"))
    return chars


def build_vendor() -> None:
    for folder, (pkg, files, lic) in VENDOR.items():
        dest = SHARED / "vendor" / folder
        for f in files:
            out = dest / pathlib.Path(f).relative_to(pathlib.Path(f).parts[0])
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(fetch(JSDELIVR + pkg + "/" + f))
        (dest / "LICENSE").write_bytes(fetch(JSDELIVR + pkg + "/" + lic))
        print("vendor", pkg)


def build_fonts() -> None:
    dest = SHARED / "fonts"
    dest.mkdir(parents=True, exist_ok=True)
    glyphs = {"latin": set(LATIN), "rtl": page_chars("rtl"), "cjk": page_chars("cjk"), "scripts": page_chars("scripts")}
    css = []
    for family, (folder, file, weights, glyph_set) in FONTS.items():
        src = fetch(GOOGLE_FONTS + folder + "/" + file.replace("[", "%5B").replace("]", "%5D"))
        (dest / f"OFL-{folder}.txt").write_bytes(fetch(GOOGLE_FONTS + folder + "/OFL.txt"))
        slug = family.lower().replace(" ", "-")
        for w in weights:
            font = TTFont(io.BytesIO(src))
            if "fvar" in font:
                axes = {a.axisTag: a.defaultValue for a in font["fvar"].axes}
                axes["wght"] = w
                static = instancer.instantiateVariableFont(font, axes)   # static instance: no variable-font support needed
            else:
                static = font
            opts = subset.Options()
            opts.flavor = "woff2"
            opts.layout_features = ["*"]                              # keep shaping (Arabic joining, kerning)
            opts.name_IDs = ["*"]
            sub = subset.Subsetter(opts)
            sub.populate(unicodes=glyphs[glyph_set])
            sub.subset(static)
            name = f"{slug}-{w}.woff2"
            subset.save_font(static, str(dest / name), opts)
            css.append(f"@font-face {{ font-family: '{family}'; font-weight: {w}; font-style: normal; "
                       f"font-display: block; src: url('{name}') format('woff2'); }}")
            print("font", name)
    (dest / "fonts.css").write_text("\n".join(css) + "\n", encoding="utf-8")


if __name__ == "__main__":
    build_vendor()
    build_fonts()
