# Benchmark methodology

Version 1.0. This document is versioned; every published result names the
methodology version it was produced with.

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

- One dedicated machine. Windows runs natively; Linux runs in WSL2 on the same
  hardware, so the two operating systems are measured on identical CPUs and
  memory. Exact hardware, OS build and .NET runtime are recorded in
  `results/<run>/env.json`.
- Packages are restored from nuget.org only, into a package folder private to
  this repository (`nuget.config`), so a run never picks up a locally built copy.
- All libraries run on the same .NET runtime (`net8.0`; `net10.0` reported
  separately).
- Package versions are pinned per run and recorded.
- Commercial libraries run on their free trial (a library that needs a key for
  its trial gets one through an environment variable; keys are never committed). Evaluation watermarks are masked
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
| long-document | 4,000 paragraphs: pagination, speed and memory (no image comparison — too tall to screenshot) |
| web-fonts | web fonts and an icon font |
| grid-flex | CSS grid and flexbox layout |
| js-chart | SVG chart drawn by JavaScript (needs a wait) |
| lazy-images | lazy-loaded images |
| paged-media | `@page` rules and page-break properties |
| rtl | right-to-left text (Arabic) |
| cjk | Chinese / Japanese / Korean text |
| large-images | photo-heavy page |
| html-string | the invoice page passed as an HTML string with a base URL, not as a URL |
| effects | backgrounds, shadows, opacity, transforms, clip-path, sticky header |
| vector | text, SVG and gradients — must stay vector, not become a bitmap |
| wide-content | a table wider than the viewport — nothing may be cut off (checked by text, not image) |
| scripts | Devanagari, Thai and colour emoji |
| accessible | headings, lists, a data table, figures, links — the input for the standards tests |
| header-footer | a header and a "Page N of M" footer added with each library's own API |
| bookmarks | PDF bookmarks from headings, and links to anchors in the same page |
| forms | HTML form controls — are they turned into fillable PDF fields? |
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
on a mid-size fixture - once through the library's synchronous API (blocking calls
on worker threads) and once through its asynchronous API (that many calls in
flight). Memory is not sampled during this step (see 4.3).

**Parallel correctness:** a set of fixtures is converted twice each with 8
conversions in flight, sync and async, and every PDF is compared with the one
the same library produced sequentially: page count, extracted text, and page
images (more than 1% of pixels differing counts as different).

### 4.3 Memory

Peak memory during a run, **including child processes** (browser-based engines
run in separate processes), measured as the proportional set size (PSS): each
process's private memory plus its share of memory it shares with other
processes, sampled every 100 ms. Adding up plain working sets would count the
libraries those processes share once per process and overstate an engine that
uses more processes. Linux reports PSS directly (`/proc/<pid>/smaps_rollup`); on
Windows it is computed from each process's working-set pages, dividing a shared
page by the number of processes sharing it. The number of processes at the peak
is recorded too. Sampling briefly pauses the process it reads, so memory has
its own pass - three conversions of each fixture in a warm process, then the
throughput fixture at 8 parallel conversions - and never runs during a timed step.

**Stability:** 1,000 conversions in one process, cycling through the fixtures.
Every 100 conversions: memory (one snapshot between conversions), the number of
processes, the median conversion time of those 100, and failures so far. Growth
in memory or time across the run is what a long-running server would see.

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
  libraries get, scrolled through once so lazy-loaded content is present. One
  reference per run, captured on Windows, is used for both operating systems:
  every fixture embeds its own fonts, so the page renders the same on both.
- **Library output:** each library renders with `screen` media, the same
  viewport width, zero margins, and the content scaled to fit the page width,
  using the library's documented settings. A library that cannot render
  `screen` media is reported as such and compared as-is.
- **Normalising:** every PDF page is rasterised (PyMuPDF) at the scale that
  maps the page width to the reference width, and the pages are stacked into one
  tall image. A library may move an element to the next page rather than split
  it, leaving blank space at the bottom of a page; that blank space is removed
  before stacking, since the screenshot has no page breaks. The page count still
  records how the content was paginated.
- **Alignment:** the stacked image is compared with the reference in tiles of
  256 rows. Each tile is aligned to the reference first — the best match within
  200 rows of the previous tile's position — so that content a page break moved
  (or a table header repeated on a new page) does not misalign everything after it.
- **Comparison:** SSIM and pixel difference computed **per tile** (a single
  whole-page score lets one large blank area hide local errors), plus the worst
  tile. Side-by-side images are published for every fixture.
- **Also reported, not hidden:** page count, rendered content height versus the
  reference height (shows content that was cut or scaled), and elements that
  were split across a page break.

The `long-document` fixture is not compared as an image: a 4,000-paragraph page
is far taller than a screenshot can sensibly be. It is checked by text and page
count.

The `paged-media` fixture is the other exception: it tests `@page` rules and
page-break properties, which only apply with `print` media. Its reference is
Chrome's print output, and libraries render it with `print` media where they
support it.

### 4.6 Correctness

Per-fixture assertions: links preserved as link annotations, page-break rules
honoured, JavaScript output present, right-to-left and CJK text extractable.

### 4.7 Standards

The `accessible` fixture is converted once per level: PDF/A-1a, 1b, 2a, 2b, 2u,
3a, 3b, 3u, 4, 4e, 4f, PDF/UA-1, PDF/UA-2, and the combinations PDF/A-2a +
PDF/UA-1, PDF/A-3a + PDF/UA-1 and PDF/A-4 + PDF/UA-2, and PDF/X-1a:2001, using
each library's documented API for that level. A level a library does not offer is
recorded as "not supported".

- **Validation:** every file is validated with veraPDF twice — against each
  profile it was asked for, and with veraPDF's automatic profile, which reads the
  standard the file *claims* in its metadata. The result is veraPDF's verdict and
  the rules that failed, not the library's claim.
- **Tag content:** a validator checks the rules a tagged file must follow, not
  whether the tags describe the page. For every file asked to be accessible, the
  structure tree is compared with the HTML: the language, the title (and that
  viewers show it), and the number of H1/H2/H3 headings, lists and list items,
  tables and their TH/TD cells, links, and figures — the content image must be a
  Figure with its alternative text; the decorative image (`alt=""`) must not be.
- **PDF/X-1a:** no free validator checks PDF/X, so the standard's machine-checkable
  rules are checked directly: a GTS_PDFX output intent with an output condition,
  the version and conformance keys, the trapping key, every font embedded, no
  transparency, and no RGB colour anywhere - not in drawing operators, colour
  spaces or images (PDF/X-1a allows only CMYK, grey and spot colour).

### 4.8 Isolation

A pair of fixtures checks whether state from one conversion (for example
`localStorage` or `sessionStorage`) is visible to the next.

### 4.9 Deployment

For Linux (`deployment/`): each library's adapter is published for linux-x64 and
built into a Docker image from `mcr.microsoft.com/dotnet/runtime:8.0` - first
the minimal image (runtime + application, nothing else), then the vendor's
documented setup where the vendor documents one (its package list and settings,
with the source linked in the Dockerfile). In each image one HTML string is
converted, as root and as the image's non-root user, with no extra flags (no
`--no-sandbox`, privileged mode or permission changes). Recorded: image size,
extra OS packages, whether each run works, and the time to the first PDF. Also
the size of `dotnet publish` output per runtime identifier.

### 4.10 Cost

Every published licence from each vendor's own pricing page, with the date it was
read, in [pricing/](pricing/) - plus common situations (one developer on one
server; a SaaS product; a team; unlimited developers) priced with the cheapest
published licence that covers each, as the vendor's published terms read. Prices
are a dated copy: the vendor's page is the authority. Not a measurement, so not
part of a run.

### 4.11 REST APIs (not covered yet)

APIs use the same fixtures, uploaded or exposed at a public URL as the API
requires. Network time is measured and reported separately from conversion time.

### 4.12 Errors and timeouts

What a library does when a page goes wrong, with its default timeouts: the main
document never answers; it is 404; the host is unreachable; an image is
missing; a script throws; an image takes 5 seconds; an image never arrives. For
each: the outcome (a PDF, an error, or no answer within 5 minutes), the time it
took, and whether the same process still converts a normal page afterwards.
Where a broken resource should not stop the page, the PDF must contain the page.

### 4.13 Headers, footers and encryption

- **HTML templates:** a header (logo image and styled text) and a footer with
  "Page N of M", each from an HTML template, through the library's documented
  API. Checked: the header text and logo on every page, the right page numbers on
  every page.
- **Encryption:** user and owner passwords, printing and copying denied. Checked:
  the file needs a password, rejects a wrong one, opens with the user password,
  denies printing and copying, and which encryption method it uses.

### 4.14 Text extraction and search

The inputs are PDFs that neither library produced: the fixture pages printed by
headless Chromium (`fixtures/inputs/pdf/`), in Latin, Arabic, Chinese, Japanese,
Korean, Hindi and Thai, plus a 344-page document.

- **Extraction:** the text each library extracts is compared with the page's
  real text (the browser's `innerText`) as the overlap of character pairs with
  whitespace removed (F1): insensitive to line breaks and spacing, sensitive to
  missing, wrong or reordered characters - right-to-left text extracted
  backwards scores low. PyMuPDF's extraction is reported alongside as a reference.
- **Search:** each term must be found on the pages where PDFium's own text
  search finds it. A library without a search API is recorded as not supported.

### 4.15 PDF to image

The same inputs rendered to PNG at 150 DPI: time, image size (must match the
page size at that resolution), and similarity (SSIM) to PDFium's rendering, scored at half resolution (both images downscaled alike) so glyph anti-aliasing does not outweigh content; the SelectPdf trial banner is masked.

### 4.16 Word to PDF

DOCX files written for this repository (`fixtures/inputs/word/`) - styles, a
table, lists, an image, a hyperlink, a header and a footer with page fields,
several pages, and Arabic, CJK and Hindi text - converted by each library and
compared with Microsoft Word's own PDF export of the same file: page count,
text (character-pair F1), page images (SSIM), fonts embedded.

### 4.17 Redaction

Two terms are redacted from the Chromium-printed invoice. Checked: neither term
can be extracted any more (by two independent extractors), every place it was is
covered, other text is untouched, and the page count is unchanged.

### 4.18 Merge and split

Three Chromium-printed PDFs merged into one, and pages 10-12 of the 344-page
document split into a new file. Checked against the source pages: page count,
each page's text in order (character-pair F1 at least 0.98), fonts embedded.

### 4.19 Watermark

A diagonal "CONFIDENTIAL" text watermark on every page of the invoice, through
the library's documented way to stamp all pages. Checked: the text on every page,
visible on every page (rendered pixels change), the original text kept, the page
count unchanged.

### 4.20 Digital signatures

The invoice signed with a self-signed test certificate made for each run (never
committed). Validated with pyHanko, with that certificate as the only trust
root: one signature, intact, valid, covering the whole file; the signature type,
digest and signing time are recorded.

### 4.21 Form filling and e-invoices

- **Form filling:** an AcroForm made with PyMuPDF (`fixtures/inputs/forms/`) -
  two text fields, a combo box, a check box and a radio group - filled through
  the library's form API. Each value is read back with pypdf, and text values
  must also be drawn in the field's appearance.
- **ZUGFeRD / Factur-X:** an invoice page converted to PDF/A-3b with its EN 16931
  Factur-X XML embedded (`fixtures/inputs/einvoice/`), through each library's
  documented route. Checked: veraPDF PDF/A-3b, the XML embedded byte for byte as
  `factur-x.xml` with relationship Alternative, and the Factur-X metadata
  (namespace, document type, file name, conformance level, version). The XML's
  own business rules (EN 16931 schematron) are not validated - the XML is the
  same input for every library.

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
- Docker images are built on Windows: Docker marks every file in a Windows build
  context as executable and readable by all users, so file permissions are not
  tested - a Linux build host may need what a Windows one hides.
