"""Freezes a live web page into a fixture folder: the rendered DOM with scripts
removed, and every stylesheet, font and image saved locally, so the page renders
offline and identically on every run.

    python fixtures/_tools/snapshot.py https://selectpdf.com/ selectpdf-home

Only snapshot pages whose licence allows redistribution, and record it in the
folder's SOURCE.md. Needs Playwright (pip install playwright; playwright install chromium)
and beautifulsoup4.
"""
import hashlib
import pathlib
import re
import sys
import urllib.parse
import urllib.request
from datetime import date

from bs4 import BeautifulSoup
from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[1]
CSS_URL = re.compile(r"url\(\s*(['\"]?)([^'\")]+)\1\s*\)")
CSS_IMPORT = re.compile(r"@import\s+(?:url\()?\s*['\"]?([^'\")\s;]+)['\"]?\s*\)?[^;]*;")


class Snapshot:
    def __init__(self, url: str, name: str):
        self.url, self.dir = url, ROOT / name
        self.assets = self.dir / "assets"
        self.captured: dict[str, bytes] = {}
        self.saved: dict[str, str] = {}

    def body(self, url: str) -> bytes | None:
        if url in self.captured:
            return self.captured[url]
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 fixture-snapshot"})
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.read()
        except Exception:
            return None

    def save(self, url: str, css_base: str | None = None) -> str | None:
        """Saves an asset under assets/, returns its path relative to the page; None if unavailable."""
        if url.startswith("data:"):
            return url
        if url in self.saved:
            return self.saved[url]
        data = self.body(url)
        if data is None:
            return None
        ext = pathlib.PurePosixPath(urllib.parse.urlparse(url).path).suffix.lower()[:6] or ".bin"
        name = hashlib.sha1(url.encode()).hexdigest()[:12] + ext
        self.saved[url] = "assets/" + name   # before recursing: stylesheets can import each other
        if ext == ".css":
            data = self.rewrite_css(data.decode("utf-8", "replace"), url, in_assets=True).encode("utf-8")
        (self.assets / name).write_bytes(data)
        return self.saved[url]

    def rewrite_css(self, css: str, base: str, in_assets: bool) -> str:
        def local(ref: str) -> str:
            path = self.save(urllib.parse.urljoin(base, ref))
            if path is None:
                return ""
            return path.removeprefix("assets/") if in_assets and not path.startswith("data:") else path

        css = CSS_IMPORT.sub(lambda m: f'@import url("{local(m.group(1))}");', css)
        return CSS_URL.sub(lambda m: f'url("{local(m.group(2))}")', css)

    def run(self) -> None:
        self.assets.mkdir(parents=True, exist_ok=True)
        with sync_playwright() as p:
            browser = p.chromium.launch()
            page = browser.new_page(viewport={"width": 1024, "height": 900})

            def keep(resp):
                try:
                    if resp.ok and resp.request.resource_type in ("stylesheet", "font", "image"):
                        self.captured[resp.url] = resp.body()
                except Exception:
                    pass

            page.on("response", keep)
            page.goto(self.url, wait_until="networkidle")
            page.evaluate("""async () => {   // scroll through so lazy content loads, then freeze image choices
                for (let y = 0; y < document.body.scrollHeight; y += 600) { window.scrollTo(0, y); await new Promise(r => setTimeout(r, 120)); }
                window.scrollTo(0, 0);
                document.querySelectorAll('img').forEach(i => { if (i.currentSrc) i.setAttribute('src', i.currentSrc);
                    i.removeAttribute('srcset'); i.removeAttribute('sizes'); i.removeAttribute('loading'); });
                document.querySelectorAll('picture source').forEach(s => s.remove());
            }""")
            page.wait_for_load_state("networkidle")
            html, final_url = page.content(), page.url
            browser.close()

        soup = BeautifulSoup(html, "html.parser")
        for tag in soup.find_all(["script", "noscript", "iframe", "base"]):
            tag.decompose()
        for link in soup.find_all("link"):
            rel = " ".join(link.get("rel", [])).lower()
            href = link.get("href")
            if "stylesheet" in rel and href:
                path = self.save(urllib.parse.urljoin(final_url, href))
                if path:
                    link["href"] = path
                    continue
            if "icon" in rel and href:
                path = self.save(urllib.parse.urljoin(final_url, href))
                if path:
                    link["href"] = path
                    continue
            link.decompose()   # preload, prefetch, canonical, alternate, manifest: nothing to fetch offline
        for img in soup.find_all("img"):   # tracking pixels: zero-size images carry nothing to render
            if img.get("width") == "0" or img.get("height") == "0" or img.get("src", "").startswith(("https://bat.bing", "https://www.facebook.com/tr")):
                img.decompose()
        for img in soup.find_all(["img", "source"]):
            for attr in ("src", "data-src", "poster"):
                if img.get(attr):
                    path = self.save(urllib.parse.urljoin(final_url, img[attr]))
                    if path:
                        img[attr] = path
                    else:
                        del img[attr]
        for el in soup.find_all(style=True):
            el["style"] = self.rewrite_css(el["style"], final_url, in_assets=False)
        for style in soup.find_all("style"):
            style.string = self.rewrite_css(style.get_text(), final_url, in_assets=False)
        for a in soup.find_all("a", href=True):   # keep links absolute so link annotations stay meaningful
            a["href"] = urllib.parse.urljoin(final_url, a["href"])
        for el in soup.find_all(True):   # inline event handlers would run against a page with no scripts
            for attr in [k for k in el.attrs if k.lower().startswith("on")]:
                del el[attr]

        (self.dir / "index.html").write_text(str(soup), encoding="utf-8")
        print(f"{self.dir.name}: {len(self.saved)} assets, {sum(f.stat().st_size for f in self.assets.iterdir()) // 1024} KB")
        source = self.dir / "SOURCE.md"
        if not source.exists():
            source.write_text(f"Frozen copy of {self.url}, taken {date.today().isoformat()} with `_tools/snapshot.py` "
                              "(scripts removed, assets local).\n", encoding="utf-8")


if __name__ == "__main__":
    Snapshot(sys.argv[1], sys.argv[2]).run()
