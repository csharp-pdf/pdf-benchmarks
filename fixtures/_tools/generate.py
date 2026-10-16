"""Generates the data-heavy fixtures deterministically (fixed seeds), so every
run uses identical input. Output is committed.

    python fixtures/_tools/generate.py

Writes: invoice line items, long-document/index.html, large-images/ and
lazy-images/ (pages + synthetic photographs). Needs numpy and Pillow.
"""
import html
import pathlib
import random
import re

import numpy as np
from PIL import Image, ImageFilter

ROOT = pathlib.Path(__file__).resolve().parents[1]

WORDS = ("document page render layout engine margin header footer table invoice report customer order "
         "quarter revenue column font glyph vector image paragraph section chapter value result output "
         "browser print screen width height scale archive signature standard compliance template server "
         "request response process memory thread queue stream format version release support license").split()


def sentence(rng: random.Random, lo=8, hi=18) -> str:
    w = [rng.choice(WORDS) for _ in range(rng.randint(lo, hi))]
    return " ".join(w).capitalize() + "."


def invoice() -> None:
    rng = random.Random(2026)
    items = ["Copy paper A4 80gsm (box)", "Toner cartridge, black", "Toner cartridge, colour set", "Archive box",
             "Ring binder 2-ring", "Desk organiser", "Ballpoint pens (50)", "Sticky notes (12 pads)",
             "Label printer tape", "Envelopes C4 (250)", "Whiteboard markers (8)", "Stapler, heavy duty",
             "Laminating pouches (100)", "Shredder bags (50)", "USB-C cable 2 m", "Monitor stand"]
    rows, subtotal = [], 0
    for i in range(1, 61):   # 60 lines: the table runs over three pages, its header repeats
        name = items[rng.randrange(len(items))]
        qty = rng.randint(1, 40)
        cents = rng.randint(199, 18999)
        amount = qty * cents
        subtotal += amount
        rows.append(f'      <tr><td>{i}</td><td>{html.escape(name)}</td><td>NW-{rng.randint(10000, 99999)}</td>'
                    f'<td class="num">{qty}</td><td class="num">£{cents / 100:,.2f}</td><td class="num">£{amount / 100:,.2f}</td></tr>')
    vat = round(subtotal * 0.2)
    page = (ROOT / "invoice" / "index.html").read_text(encoding="utf-8")
    page = re.sub(r'<tbody id="lines">.*?</tbody>', '<tbody id="lines">\n' + "\n".join(rows) + '\n    </tbody>', page, flags=re.S)
    for key, val in (("subtotal", subtotal), ("vat", vat), ("total", subtotal + vat)):
        page = re.sub(rf'<td class="num" id="{key}">.*?</td>', f'<td class="num" id="{key}">£{val / 100:,.2f}</td>', page)
    (ROOT / "invoice" / "index.html").write_text(page, encoding="utf-8")


def long_document() -> None:
    rng = random.Random(4000)
    out = ['<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n<title>long-document</title>',
           '<link rel="stylesheet" href="../_shared/fonts/fonts.css">',
           "<style>body { margin: 40px; font-family: 'Lora', serif; font-size: 15px; line-height: 1.6; color: #222; }"
           " h2 { font-family: 'Inter', sans-serif; margin-top: 32px; }</style>\n</head>\n<body>",
           "<h1>Long document: 4,000 paragraphs</h1>"]
    for i in range(4000):
        if i % 100 == 0:
            out.append(f"<h2>Chapter {i // 100 + 1}</h2>")
        out.append(f"<p>{i + 1}. " + " ".join(sentence(rng) for _ in range(rng.randint(1, 3))) + "</p>")
    out.append("</body>\n</html>\n")
    (ROOT / "long-document").mkdir(exist_ok=True)
    (ROOT / "long-document" / "index.html").write_text("\n".join(out), encoding="utf-8")


def photo(seed: int, w: int, h: int) -> Image.Image:
    """A synthetic 'photograph': sky gradient, hills, soft shapes and sensor noise.
    Real-photo-like JPEG statistics without third-party image licences."""
    rng = np.random.default_rng(seed)
    y = np.linspace(0, 1, h)[:, None]
    x = np.linspace(0, 1, w)[None, :]
    top, bottom = rng.uniform(80, 220, 3), rng.uniform(20, 160, 3)
    img = top[None, None, :] * (1 - y[..., None]) + bottom[None, None, :] * y[..., None]
    img = np.broadcast_to(img, (h, w, 3)).copy()
    for k in range(3):   # three hill layers
        ridge = 0.45 + 0.12 * k + 0.08 * np.sin(x * rng.uniform(4, 12) + rng.uniform(0, 6)) + 0.03 * np.sin(x * 37 + k)
        mask = (y > ridge)[..., None]
        img = np.where(mask, rng.uniform(20, 140, 3)[None, None, :] * (0.7 + 0.3 * y[..., None]), img)
    for _ in range(12):   # soft discs: clouds, sun, trees
        cx, cy, r = rng.uniform(0, w), rng.uniform(0, h), rng.uniform(w * 0.02, w * 0.12)
        yy, xx = np.ogrid[:h, :w]
        d = np.exp(-(((xx - cx) ** 2 + (yy - cy) ** 2) / (2 * r * r)))[..., None]
        img = img * (1 - 0.6 * d) + rng.uniform(0, 255, 3)[None, None, :] * 0.6 * d
    img += rng.normal(0, 9, img.shape)   # sensor noise keeps the JPEG photo-sized
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    return im.filter(ImageFilter.GaussianBlur(0.6))


def image_page(name: str, title: str, count: int, size: tuple[int, int], lazy: bool, intro: str) -> None:
    d = ROOT / name
    (d / "img").mkdir(parents=True, exist_ok=True)
    figs = []
    for i in range(count):
        fn = f"photo-{i + 1:02d}.jpg"
        photo(1000 * (2 if lazy else 1) + i, *size).save(d / "img" / fn, quality=85, optimize=True)
        attr = ' loading="lazy"' if lazy else ""
        figs.append(f'  <figure><img src="img/{fn}" width="{size[0]}" height="{size[1]}"{attr} alt="Synthetic photo {i + 1}">'
                    f'<figcaption>Photo {i + 1}</figcaption></figure>')
    page = ['<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">', f"<title>{name}</title>",
            '<link rel="stylesheet" href="../_shared/fonts/fonts.css">',
            "<style>body { margin: 32px; font-family: 'Inter', sans-serif; color: #1d1d1f; }"
            " figure { margin: 0 0 24px; } img { width: 100%; height: auto; display: block; border-radius: 6px; }"
            " figcaption { font-size: 13px; color: #666; margin-top: 6px; }</style>\n</head>\n<body>",
            f"<h1>{title}</h1>", f"<p>{intro}</p>", *figs, "</body>\n</html>\n"]
    (d / "index.html").write_text("\n".join(page), encoding="utf-8")
    (d / "SOURCE.md").write_text("Synthetic photographs generated by `_tools/generate.py` (fixed seeds). MIT.\n", encoding="utf-8")


if __name__ == "__main__":
    invoice()
    long_document()
    image_page("large-images", "Large images", 8, (2400, 1600), False,
               "Eight 2400 × 1600 JPEG photographs, scaled down to the page width.")
    image_page("lazy-images", "Lazy-loaded images", 12, (1200, 800), True,
               "Twelve images with loading=\"lazy\": all of them must appear in the PDF, including those far below the first screen.")
    print("generated")
