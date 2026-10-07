# Benchmark methodology

This document is versioned; every published result names the methodology
version it was produced with.

## 1. Principles

1. **Reproducible.** Code, fixtures, package versions and environment are all in
   this repository. Anyone can re-run a result.
2. **Same input, same output conditions.** Every library converts the same local
   fixtures to the same paper size, margins and media type.
3. **Recommended configuration.** Each library is used the way its vendor
   documents. Vendors may correct the configuration through a pull request.
4. **Per-axis results, no composite score.** A single score hides trade-offs.
5. **Everything published.** Results are not selected after the fact; a run is
   published whole or not at all.

## 2. Environment

- Dedicated machines, same size, one per OS: Windows and Linux; macOS on Apple
  Silicon for libraries that support it. Exact hardware, OS build, .NET SDK and
  runtime are recorded in `results/<run>/env.json`.
- All libraries run on the same .NET runtime (`net8.0`; `net10.0` reported
  separately).
- Package versions are pinned per run and recorded.
- Commercial libraries run on their free trial. Evaluation watermarks are masked
  identically in every library's output before any image comparison.
- No other workload runs on the machine during a run.
- **Licence terms are checked before a library is included.** If a licence
  restricts publishing benchmark results, the library is covered by samples and
  features only, and the reason is stated.

## 3. Fixtures

Served from a local static web server so network variance is excluded and runs
repeat exactly. See [fixtures/README.md](fixtures/README.md) for the list and
the licence of each page.

| Fixture | What it exercises |
|---|---|
| trivial | baseline cost of one conversion |
| bootstrap | a typical CSS framework page |
| invoice | tables, totals, page breaks inside tables |
| long-document | ~4,000 paragraphs: pagination and memory |
| web-fonts | web fonts and an icon font |
| grid-flex | CSS grid and flexbox layout |
| js-chart | SVG chart drawn by JavaScript (needs a wait) |
| lazy-images | lazy-loaded images |
| paged-media | `@page` rules and page-break properties |
| rtl | right-to-left text (Arabic) |
| cjk | Chinese / Japanese / Korean text |
| large-images | photo-heavy page |
| real-site snapshots | frozen copies of real pages with a compatible licence |

## 4. Measurements

### 4.1 Speed

- **Warm:** after a warm-up conversion that is discarded, 9 conversions per
  fixture, libraries **interleaved** (A, B, C, A, B, C, …) to spread out machine
  drift. Report median and p95, in milliseconds, including writing the PDF bytes.
- **Cold:** the first conversion in a fresh process, measured separately over
  repeated fresh processes.

### 4.2 Throughput

Conversions per minute at 1, 4 and 8 parallel conversions, over a fixed duration,
on a mid-size fixture.

### 4.3 Memory

Peak working set during a run, **including child processes** (browser-based
engines run in separate processes).

### 4.4 Output

File size, page count, whether fonts are embedded, and whether the text can be
extracted and searched (checked with PyMuPDF / pypdf).

### 4.5 Fidelity

The question is: **does the PDF look like the page does in a browser?** So the
reference is what users see — a full-page **screenshot** of the fixture in
headless Chrome — not Chrome's print output, which uses `print` media and a
different layout.

- **Reference:** full-page screenshot, `screen` media, fixed viewport width
  (1024 px), device scale factor 1, after the same load/JavaScript wait the
  libraries get.
- **Library output:** each library renders with `screen` media, the same
  viewport width, zero margins, and the content scaled to fit the page width,
  using the library's documented settings. A library that cannot render
  `screen` media is reported as such and compared as-is.
- **Normalising:** every PDF page is rasterised with the same rasteriser, the
  pages are stacked into one tall image, and the image is scaled to the
  reference width. Rows within a small band around each page boundary are masked
  out of the comparison, because a page break legitimately moves content.
- **Comparison:** SSIM and pixel difference computed **per tile** along the page
  (a single whole-page score lets one large blank area hide local errors), plus
  the worst tile. Side-by-side images are published for every fixture.
- **Also reported, not hidden:** page count, rendered content height versus the
  reference height (shows content that was cut or scaled), and elements that
  were split across a page break.

The `paged-media` fixture is the exception: it tests `@page` rules and
page-break properties, which only apply with `print` media. Its reference is
Chrome's print output, and libraries render it with `print` media where they
support it.

### 4.6 Correctness

Per-fixture assertions: links preserved as link annotations, page-break rules
honoured, JavaScript output present, right-to-left and CJK text extractable.

### 4.7 Standards

PDF/A and PDF/UA output validated with veraPDF; the result is the validator's
verdict, not the library's claim.

### 4.8 Isolation

A pair of fixtures checks whether state from one conversion (for example
`localStorage` or `sessionStorage`) is visible to the next.

### 4.9 Deployment

For Linux: the smallest working Dockerfile, the resulting image size, extra OS
packages required, and whether the container runs as a non-root user without
extra flags (no `--no-sandbox`, privileged mode or manual permission changes).
For all OSes: `dotnet publish` output size per runtime identifier.

### 4.10 Cost

Entry price, licensing model and free-tier or trial limits, from the vendor's
pricing page, with the date checked.

### 4.11 REST APIs

APIs use the same fixtures, uploaded or exposed at a public URL as the API
requires. Network time is measured and reported separately from conversion time.

## 5. Reporting

- Each run is a folder `results/<yyyy-mm-dd>-<os>/` with raw CSV/JSON,
  `env.json` and the methodology version.
- Tables in the library pages are generated from these files by script, never
  edited by hand.
- Older runs are kept.
- Runs repeat on a schedule and when a covered library releases a new version.

## 6. Known limitations

- Timings depend on hardware; compare libraries within one run, not across runs
  on different machines.
- Fidelity is measured against a Chrome screenshot (and, for `paged-media`,
  Chrome's print output). That is a reference, not a definition of "correct":
  a library using a different engine can be right where Chrome differs.
- Splitting a long page into PDF pages always changes the layout near the
  breaks; the masked band reduces but cannot remove that effect.
- Trial editions may differ from licensed editions; where a vendor documents a
  difference, it is noted.
