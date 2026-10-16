# .NET PDF Library Benchmarks

A reproducible benchmark harness for C# / .NET PDF libraries and HTML to PDF
APIs: the code, the test pages, the environment description and the raw results.
The library pages that use these numbers live in
[csharp-pdf/pdf-libraries-compared](https://github.com/csharp-pdf/pdf-libraries-compared).

> **Disclosure:** this repository is maintained by [SelectPdf](https://selectpdf.com/),
> which makes a .NET PDF library and an HTML to PDF API that are measured here.
> That is why everything needed to check the numbers is public: run the harness on
> your own hardware and compare. Results are published in full, including the axes
> where SelectPdf is behind.

## What is measured

Speed (warm and cold), throughput under parallel load, memory, output size,
rendering fidelity against a browser screenshot of the same page, correctness checks
(page numbers, bookmarks, links, form fields, vector output, complex scripts), PDF/A and
PDF/UA validity checked with veraPDF plus the content of the tags, isolation between conversions,
async and parallel conversion (and whether parallel output is identical), errors and timeouts,
stability over 1,000 conversions, HTML headers/footers and encryption, text extraction and search,
PDF to image, Word to PDF, redaction, merge and split, watermarks, digital signatures, form
filling, ZUGFeRD/Factur-X e-invoices, PDF/X-1a, Linux/Docker deployment
requirements, publish size and cost. The full protocol is in
[METHODOLOGY.md](METHODOLOGY.md).

There is **no overall score**. Each axis is reported on its own, with the run
date, environment and exact package versions.

## Repository layout

```
METHODOLOGY.md      the protocol — read this first
fixtures/           the test pages and fixtures.json (wait times, media, content checks)
server/             the static server the fixtures are loaded from during a run
harness/            one adapter program per library + the runner that drives them
reference/          captures the browser screenshots the PDFs are compared with
analysis/           content checks and the image comparison
deployment/         Dockerfiles and the script that builds and tests them
pricing/            every published licence, sourced and dated, and common situations priced
results/            raw results per run (CSV/JSON) + environment description
run.ps1, run.sh     a complete run on Windows (and Linux via WSL) / on Linux
```

## Running it

Needs the .NET 8 SDK, Python 3 with `playwright` (Chromium), `pymupdf`, `pypdf`,
`opencv-python` and `numpy`, and [veraPDF](https://verapdf.org/) — set `VERAPDF`
to its launcher, and `VERAPDF_JAVA_HOME` if Java is not on the PATH. Libraries that need a key for their trial read it
from an environment variable (IronPDF: `IRONPDF_LICENSE_KEY`); a library without
its key is skipped.

```powershell
./run.ps1            # Windows
./run.ps1 -Linux     # Windows, then the same run inside WSL
```

```bash
./run.sh             # Linux
```

A full run takes about half an hour per operating system. Close other
applications first: timings are only comparable on an otherwise idle machine.

## Contributing

If a measurement looks unfair or something important is missing, open an issue.
Vendors are invited to check that their library is configured the way they
recommend; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Code: [MIT](LICENSE). Fixture pages carry their own licences, listed in
[fixtures/README.md](fixtures/README.md). Product names are trademarks of their
respective owners.
