# Fixtures

Test pages are served from a local static web server during a run, so network
variance does not affect results and every run uses identical input.

Each fixture lives in its own folder with an `index.html` and every asset it
needs (fonts, images, scripts) — no external requests.

| Fixture | Folder | Source | Licence |
|---|---|---|---|
| trivial | `trivial/` | written for this repository | MIT |
| bootstrap | `bootstrap/` | Bootstrap example page | MIT (Bootstrap) |
| invoice | `invoice/` | written for this repository | MIT |
| long-document | `long-document/` | generated text | MIT |
| web-fonts | `web-fonts/` | written for this repository + open-licence fonts | MIT + font licences (OFL) |
| grid-flex | `grid-flex/` | written for this repository | MIT |
| js-chart | `js-chart/` | written for this repository + an open-source chart library | MIT + library licence |
| lazy-images | `lazy-images/` | written for this repository | MIT |
| paged-media | `paged-media/` | written for this repository | MIT |
| rtl | `rtl/` | Wikipedia article (Arabic), frozen copy | CC BY-SA 4.0, attribution in the folder |
| cjk | `cjk/` | Wikipedia articles (Chinese, Japanese, Korean), frozen copies | CC BY-SA 4.0, attribution in the folder |
| large-images | `large-images/` | written for this repository + openly licensed photos | MIT + photo licences |
| selectpdf-home | `selectpdf-home/` | selectpdf.com home page, frozen copy | used with permission of the owner (SelectPdf) |
| wikipedia-en | `wikipedia-en/` | English Wikipedia article, frozen copy | CC BY-SA 4.0, attribution in the folder |

## Rules

- No live URLs in fixtures. A frozen copy is saved once and versioned.
- Every third-party asset records its source and licence in a `SOURCE.md` file in
  the fixture folder.
- A fixture is never changed in place after results have been published with it;
  a changed fixture gets a new name.
