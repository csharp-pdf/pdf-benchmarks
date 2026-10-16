"""Captures the fidelity references (METHODOLOGY.md, section 4.5) with headless Chromium:

- screen fixtures: a full-page screenshot, screen media, 1024 px wide, device scale factor 1,
  after the page has loaded, the fixture's waitMs has passed and the page has been scrolled
  through once (so lazy-loaded content is present, as it is for a reader);
- print fixtures (paged-media): Chromium's own print output, A4, @page rules honoured.

    python reference/reference.py --base http://127.0.0.1:8777 --out out/<run>/reference

Needs Playwright with Chromium (pip install playwright; playwright install chromium).
"""
import argparse
import json
import pathlib

from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[1]

SCROLL = """async () => {
    for (let y = 0; y < document.documentElement.scrollHeight; y += 500) {
        window.scrollTo(0, y); await new Promise(r => setTimeout(r, 100));
    }
    window.scrollTo(0, 0);
    await Promise.all(Array.from(document.images).map(i => i.complete ? null : new Promise(r => { i.onload = i.onerror = r; })));
    await document.fonts.ready;
}"""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8777")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / "fixtures" / "fixtures.json").read_text(encoding="utf-8"))
    width = manifest["viewportWidth"]

    with sync_playwright() as p:
        browser = p.chromium.launch()
        meta = {"browser": "chromium", "version": browser.version, "viewportWidth": width, "fixtures": {}}
        for f in manifest["fixtures"]:
            if f.get("sequence") or f.get("fidelity") is False:
                continue   # isolation and long-document are checked by text and page count, not by image
            name, media = f["name"], f.get("media", "screen")
            page = browser.new_page(viewport={"width": width, "height": 800}, device_scale_factor=1)
            page.emulate_media(media=media)
            page.goto(f"{args.base}/{f.get('source', name)}/index.html", wait_until="load")   # html-string shows the invoice page
            page.wait_for_timeout(f.get("waitMs", 0))
            if media == "print":
                page.pdf(path=str(out / f"{name}.pdf"), format="A4", print_background=True, prefer_css_page_size=True)
                meta["fixtures"][name] = {"kind": "print-pdf"}
            else:
                page.evaluate(SCROLL)
                page.screenshot(path=str(out / f"{name}.png"), full_page=True)
                h = page.evaluate("document.documentElement.scrollHeight")
                meta["fixtures"][name] = {"kind": "screenshot", "height": h}
            page.close()
            print("reference", name, flush=True)
        browser.close()
    (out / "reference.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
