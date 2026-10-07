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
rendering fidelity against a browser screenshot of the same page, correctness checks, PDF/A
and PDF/UA validity, isolation between conversions, Linux/Docker deployment
requirements, publish size and cost. The full protocol is in
[METHODOLOGY.md](METHODOLOGY.md).

There is **no overall score**. Each axis is reported on its own, with the run
date, environment and exact package versions.

## Repository layout

```
METHODOLOGY.md      the protocol — read this first
fixtures/           the test pages, served from a local static server
results/            raw results per run (CSV/JSON) + environment description
```

## Contributing

If a measurement looks unfair or something important is missing, open an issue.
Vendors are invited to check that their library is configured the way they
recommend; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Code: [MIT](LICENSE). Fixture pages carry their own licences, listed in
[fixtures/README.md](fixtures/README.md). Product names are trademarks of their
respective owners.
