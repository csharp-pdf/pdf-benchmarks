"""Writes the Markdown results page for one benchmark run from the CSV files in results/.

    python analysis/report.py --run 2026-10-15 --out <path>.md
        [--override windows:pdfsharp=2026-10-15-pdfsharp-windows]
        [--override linux:wkhtmltopdf=2026-10-15-wkhtmltopdf-linux]

Every number on the page comes from results/<run>-<os>/*.csv. An --override takes one library's rows for one
operating system from another results folder (a rerun of that library alone), and the page says so.
Nothing is ranked or scored: each table is one axis.
"""
import argparse
import collections
import csv
import json
import pathlib
import statistics

ROOT = pathlib.Path(__file__).resolve().parents[1]
RESULTS = ROOT / "results"
REPO = "https://github.com/csharp-pdf/pdf-benchmarks"
OSES = ("windows", "linux")
OS_NAME = {"windows": "Windows", "linux": "Linux"}


class Run:
    def __init__(self, run: str, overrides: dict):
        self.run = run
        self.overrides = overrides            # (os, library) -> folder name

    def folder(self, os_: str) -> pathlib.Path:
        return RESULTS / f"{self.run}-{os_}"

    def rows(self, os_: str, name: str) -> list[dict]:
        def read(folder: pathlib.Path) -> list[dict]:
            path = folder / f"{name}.csv"
            return list(csv.DictReader(open(path, encoding="utf-8"))) if path.exists() else []
        replaced = {lib for (o, lib) in self.overrides if o == os_}
        rows = [r for r in read(self.folder(os_)) if r.get("library") not in replaced]
        for (o, lib), folder in self.overrides.items():
            if o == os_:
                rows += [r for r in read(RESULTS / folder) if r.get("library") == lib]
        return rows

    def packages(self, os_: str) -> dict:
        data = json.load(open(self.folder(os_) / "packages.json", encoding="utf-8"))
        for (o, lib), folder in self.overrides.items():
            if o == os_:
                data[lib] = json.load(open(RESULTS / folder / "packages.json", encoding="utf-8"))[lib]
        return data

    def env(self, os_: str) -> dict:
        return json.load(open(self.folder(os_) / "env.json", encoding="utf-8"))


def ok(v) -> bool:
    return str(v).lower() == "true"


def num(v, default=None):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def table(header: list, rows: list) -> str:
    out = ["| " + " | ".join(header) + " |", "|" + "|".join("---" for _ in header) + "|"]
    out += ["| " + " | ".join(str(c) for c in r) + " |" for r in rows]
    return "\n".join(out)


def fmt(v, digits=0):
    if v is None:
        return "–"
    return f"{v:,.{digits}f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--override", action="append", default=[])
    a = ap.parse_args()
    overrides = {}
    for o in a.override:
        left, folder = o.split("=")
        os_, lib = left.split(":")
        overrides[(os_, lib)] = folder
    run = Run(a.run, overrides)

    pk = {o: run.packages(o) for o in OSES}
    libs = sorted(pk["windows"], key=lambda l: pk["windows"][l]["library"].lower())
    name = {l: pk["windows"][l]["library"] for l in libs}
    env = run.env("windows")
    envl = run.env("linux")
    md = []
    w = md.append

    w(f"# Benchmark results — {a.run}\n")
    w(f"Produced by [csharp-pdf/pdf-benchmarks]({REPO}) with methodology version {env['methodologyVersion']} "
      f"([METHODOLOGY.md]({REPO}/blob/main/METHODOLOGY.md)). Raw data: "
      f"[`results/{a.run}-windows`]({REPO}/tree/main/results/{a.run}-windows) and "
      f"[`results/{a.run}-linux`]({REPO}/tree/main/results/{a.run}-linux). "
      "Every table below is generated from those files by `analysis/report.py`; nothing is edited by hand.\n")
    w("Results are reported per axis. There is no overall score: weigh the axes that matter for your project.\n")
    w("> **Disclosure:** these benchmarks are maintained by [SelectPdf](https://selectpdf.com/), one of the libraries "
      "measured. The harness, fixtures and raw data are public so that every number can be checked and re-run; "
      "vendors can [request a correction](https://github.com/csharp-pdf/pdf-libraries-compared/issues/new?template=correction.yml).\n")

    # ---- environment and versions
    w("## Environment\n")
    w(table(["", "Windows", "Linux"], [
        ["CPU", f"{env['cpu']} ({env['logicalCores']} logical cores)", f"same machine ({envl['logicalCores']} logical cores)"],
        ["Memory", f"{env['memoryGb']:.0f} GB", f"{envl['memoryGb']:.0f} GB available to WSL2"],
        ["OS", env["os"], envl["os"] + " (WSL2 on the same machine)"],
        [".NET", env["runtime"], envl["runtime"]],
        ["Speed rounds / cold runs / throughput window", f"{env['rounds']} / {env['coldRuns']} / {env['throughputSeconds']} s",
         f"{envl['rounds']} / {envl['coldRuns']} / {envl['throughputSeconds']} s"],
    ]))
    w("")
    rows = []
    for l in libs:
        p = pk["windows"][l].get("packages", {})
        vers = ", ".join(f"{k} {v}" for k, v in p.items() if not k.startswith(("browser:", "chromium:")))
        browser = "; ".join(f"{k.split(':')[1]} {v}" for k, v in p.items() if k.startswith(("browser:", "chromium:")))
        rows.append([name[l], vers + (f" (downloads {browser})" if browser else "")])
    w("All packages are the latest stable versions on nuget.org on the run date:\n")
    w(table(["Library", "Packages"], rows))
    w("")
    if overrides:
        notes = [f"{name[lib]} on {OS_NAME[o]}: [`results/{f}`]({REPO}/tree/main/results/{f})" for (o, lib), f in overrides.items()]
        w("Rerun alone after the main run (the page uses these rows): " + "; ".join(notes) + ".\n")

    # ---- speed
    for o in OSES:
        sp = collections.defaultdict(list)
        for r in run.rows(o, "speed"):
            if r["ok"] == "true":
                sp[(r["fixture"], r["library"])].append(float(r["ms"]))
        hl = [l for l in libs if any(k[1] == l for k in sp)]
        fx = list(dict.fromkeys(r["fixture"] for r in run.rows(o, "speed")))
        med = {k: statistics.median(v) for k, v in sp.items()}
        if o == "windows":
            w("## HTML to PDF: speed\n")
            w(f"Median of {env['rounds']} rounds per page in a warm process, rounds interleaved across libraries; the time "
              "includes producing the PDF bytes. Milliseconds, lower is faster. PDFsharp and QuestPDF do not convert HTML.\n")
        w(f"### {OS_NAME[o]}\n")
        rows = []
        for f in fx:
            best = min((med[(f, l)] for l in hl if (f, l) in med), default=None)
            rows.append([f] + [(f"**{fmt(med[(f, l)])}**" if med[(f, l)] == best else fmt(med[(f, l)])) if (f, l) in med else "–" for l in hl])
        common = [f for f in fx if all((f, l) in med for l in hl)]
        rows.append(["*geometric mean*"] + [f"*{fmt(statistics.geometric_mean([med[(f, l)] for f in common]))}*" for l in hl])
        w(table(["Page"] + [name[l] for l in hl], rows))
        w("")

    # ---- cold start and throughput
    w("## Cold start\n")
    w("A new process converts one page: time from process start to the PDF bytes, median of the cold runs (ms).\n")
    cold = {}
    for o in OSES:
        c = collections.defaultdict(list)
        for r in run.rows(o, "cold"):
            if r["ok"] == "true":
                c[r["library"]].append(float(r["process_start_to_pdf_ms"]))
        cold[o] = {l: statistics.median(v) for l, v in c.items()}
    hl = [l for l in libs if l in cold["windows"] or l in cold["linux"]]
    w(table(["", *[name[l] for l in hl]], [[OS_NAME[o]] + [fmt(cold[o].get(l)) for l in hl] for o in OSES]))
    w("")

    w("## Throughput\n")
    w(f"The `bootstrap` page converted back to back for {env['throughputSeconds']} s by 1, 4 and 8 concurrent workers, with "
      "the library's synchronous API (threads) and its asynchronous API (tasks). Conversions per minute, higher is better; "
      "*failed* conversions are counted separately.\n")
    for o in OSES:
        tp = {(r["library"], r["mode"], r["concurrency"]): r for r in run.rows(o, "throughput")}
        hl = [l for l in libs if any(k[0] == l and num(v["per_minute"]) is not None for k, v in tp.items())]
        rows = []
        for mode in ("sync", "async"):
            for c in ("1", "4", "8"):
                cells = []
                for l in hl:
                    r = tp.get((l, mode, c))
                    v = num(r["per_minute"]) if r else None
                    if v is None:
                        cells.append("not supported" if r else "–")
                    else:
                        failed = int(num(r["failed"], 0))
                        cells.append(fmt(v) + (f" ({failed:,} failed)" if failed else ""))
                rows.append([f"{mode} ×{c}"] + cells)
        w(f"### {OS_NAME[o]}\n")
        w(table(["", *[name[l] for l in hl]], rows))
        w("")

    # ---- memory and stability
    w("## Memory and stability\n")
    w("Memory is the proportional set size (PSS) of the library's whole process tree, so shared browser processes are "
      "counted fairly. *One page* is the median of the per-page peaks; *8 at once* is the peak with 8 concurrent "
      "conversions. *1,000 conversions*: memory at the start and after 1,000 conversions in one process, and the median "
      "conversion time of the first and the last 100.\n")
    for o in OSES:
        mem = run.rows(o, "memory")
        stab = run.rows(o, "stability")
        rows = []
        for l in libs:
            one = [num(r["peak_pss_mb"]) for r in mem if r["library"] == l and r["parallel"] == "1" and num(r["peak_pss_mb"])]
            eight = [num(r["peak_pss_mb"]) for r in mem if r["library"] == l and r["parallel"] != "1" and num(r["peak_pss_mb"])]
            s = [r for r in stab if r["library"] == l and num(r["pss_mb"]) is not None]
            if not one and not s:
                continue
            fails = int(num(s[-1]["failures"], 0)) if s else None
            rows.append([name[l], fmt(statistics.median(one)) if one else "–", fmt(max(eight)) if eight else "–",
                         f"{fmt(num(s[0]['pss_mb']))} → {fmt(num(s[-1]['pss_mb']))}" if s else "–",
                         f"{fmt(num(s[1]['median_ms']))} → {fmt(num(s[-1]['median_ms']))}" if len(s) > 1 and num(s[1]["median_ms"]) else "–",
                         fmt(fails) if s else "–"])
        w(f"### {OS_NAME[o]}\n")
        w(table(["Library", "One page (MB)", "8 at once (MB)", "1,000 conversions: MB", "1,000 conversions: ms", "Failures"], rows))
        w("")

    # ---- output correctness
    w("## Output correctness\n")
    w("*Checks*: content checks on every page (expected text present, nothing cut off, fonts embedded, images loaded, "
      "page numbers, bookmarks, form fields, encryption, isolation between conversions); not-supported options are "
      "excluded. *Fidelity*: mean SSIM of the PDF against a Chrome screenshot of the same page (1.0 = identical), with "
      "evaluation watermarks masked. Failed checks are listed per library.\n")
    for o in OSES:
        ch = run.rows(o, "checks")
        fi = run.rows(o, "fidelity")
        rows = []
        for l in libs:
            rr = [r for r in ch if r["library"] == l and r["pass"] != "n/a"]
            if not rr:
                continue
            failed = sorted({f"{r['fixture']} ({r['check']})" for r in rr if not ok(r["pass"])})
            v = {r["fixture"]: float(r["ssim_mean"]) for r in fi if r["library"] == l and r["ssim_mean"]}
            low = min(v, key=v.get) if v else None
            rows.append([name[l], f"{sum(ok(r['pass']) for r in rr)} / {len(rr)}",
                         f"{statistics.mean(v.values()):.3f}" if v else "–",
                         f"{low} {v[low]:.2f}" if low else "–", ", ".join(failed) or "none"])
        w(f"### {OS_NAME[o]}\n")
        w(table(["Library", "Checks passed", "Fidelity (mean SSIM)", "Lowest page", "Failed checks"], rows))
        w("")

    # ---- standards
    w("## Standards\n")
    w("Each library is asked for every level it documents; each file is validated by veraPDF twice (against the "
      "requested profile and with automatic profile detection). **pass** = both validations compliant; **fail** = "
      "produced but not compliant; *not offered* = the library has no option for it. *Tag structure* counts the checks "
      "that the tagged files really contain headings, tables, lists and figures with alternative text.\n")
    o = "windows"
    sr = run.rows(o, "standards_runs")
    sd = run.rows(o, "standards")
    stc = run.rows(o, "structure")
    px = run.rows(o, "pdfx")

    def compliant(lib, s):
        if s.startswith("pdfx-"):   # veraPDF does not validate PDF/X: the structural checks in pdfx.csv
            x = [r for r in px if r["library"] == lib]
            return bool(x) and all(ok(r["pass"]) for r in x)
        vals = [ok(r["compliant"]) for r in sd if r["library"] == lib and r["standard"] == s]
        return bool(vals) and all(vals)

    std_libs = [l for l in libs if any(r["library"] == l and r["ok"] == "true" for r in sr)]
    standards = list(dict.fromkeys(r["standard"] for r in sr))
    rows = []
    for s in standards:
        cells = []
        for l in std_libs:
            runr = next((r for r in sr if r["library"] == l and r["standard"] == s), None)
            if runr is None or runr["ok"] != "true":
                cells.append("not offered" if runr is not None and ok(runr["unsupported"]) else "refused (error)")
                continue
            cells.append(("pass" if compliant(l, s) else "fail") + (" ¹" if s.startswith("pdfx-") else ""))
        rows.append([s.replace("pdfa-", "PDF/A-").replace("pdfua-", "PDF/UA-").replace("pdfx-", "PDF/X-").replace("+", " + ")] + cells)
    produced = {(r["library"], r["standard"]) for r in sr if r["ok"] == "true"}
    rows.append(["*files compliant*"] + [f"*{sum(1 for s in standards if (l, s) in produced and compliant(l, s))} / {len(standards)}*" for l in std_libs])
    rows.append(["*tag structure checks*"] + [f"*{sum(ok(r['pass']) for r in stc if r['library'] == l)} / {sum(1 for r in stc if r['library'] == l)}*" for l in std_libs])
    w("Windows (the Linux results are the same):\n")
    w(table(["Standard", *[name[l] for l in std_libs]], rows))
    w("")
    w("¹ veraPDF does not validate PDF/X; the PDF/X-1a file is checked structurally instead (output intent, colour, "
      "transparency, fonts, page boxes): " + ", ".join(
          f"{name[l]} {sum(ok(r['pass']) for r in px if r['library'] == l)} / {sum(1 for r in px if r['library'] == l)} checks"
          for l in std_libs if any(r["library"] == l for r in px)) + ".\n")
    w("*refused (error)*: the library offers the level but stopped with its own conformance error on this page.\n")
    others = [name[l] for l in libs if l not in std_libs]
    w(f"No standard output: {', '.join(others)}.\n")

    # ---- PDF tools
    w("## Working with existing PDFs and Word files\n")
    w("Each tool's output is checked independently (PyMuPDF, pypdf, PDFium, pyHanko, veraPDF). Text extraction is "
      "scored as character-bigram F1 against the source text (1.00 = identical); the score shown is the lowest across "
      "Latin, Arabic, CJK, Indic/Thai and long documents. Cells give checks passed / checks; *not supported* means the "
      "library has no API for it.\n")
    o = "windows"
    tr = run.rows(o, "tools_runs")
    unsupported = {(r["library"], r["tool"]) for r in tr if ok(r["unsupported"])}
    tool_libs = [l for l in libs if any(r["library"] == l and r["tool"] != "einvoice" and not ok(r["unsupported"]) for r in tr)]

    def cell(lib, tool, rows_, key="pass"):
        if (lib, tool) in unsupported:
            return "not supported"
        x = [r for r in rows_ if r["library"] == lib and r.get(key) != "n/a"]
        return f"{sum(ok(r[key]) for r in x)} / {len(x)}" if x else "–"
    ex = run.rows(o, "extraction")
    se = run.rows(o, "search")
    ra = run.rows(o, "raster")
    wd = run.rows(o, "word")
    ms = run.rows(o, "merge_split")
    rows = [
        ["Extract text (lowest F1)"] + [("not supported" if (l, "extract") in unsupported else
                                        (f"{min(float(r['text_f1']) for r in ex if r['library'] == l and r['text_f1']):.2f}"
                                         if any(r['library'] == l and r['text_f1'] for r in ex) else "–")) for l in tool_libs],
        ["Search"] + [cell(l, "search", se) for l in tool_libs],
        ["PDF to image (page size right)"] + [("not supported" if (l, "rasterize") in unsupported else
                                              (f"{sum(ok(r['size_ok']) for r in ra if r['library'] == l)} / {sum(1 for r in ra if r['library'] == l)}"
                                               if any(r['library'] == l for r in ra) else "–")) for l in tool_libs],
        ["Word to PDF (pages / SSIM vs Word)"] + [("not supported" if (l, "word") in unsupported else
                                                  ", ".join(f"{r['pages']} of {r['pages_word']} / {num(r['ssim_mean'], 0):.2f}" for r in wd if r["library"] == l) or "–")
                                                 for l in tool_libs],
        ["Redact"] + [cell(l, "redact", run.rows(o, "redaction")) for l in tool_libs],
        ["Merge"] + [cell(l, "merge", [r for r in ms if r["operation"] == "merge"]) for l in tool_libs],
        ["Split"] + [cell(l, "split", [r for r in ms if r["operation"] == "split"]) for l in tool_libs],
        ["Watermark"] + [cell(l, "watermark", run.rows(o, "watermark")) for l in tool_libs],
        ["Digital signature"] + [cell(l, "sign", run.rows(o, "signature")) for l in tool_libs],
        ["Fill a form"] + [cell(l, "fill", run.rows(o, "fill")) for l in tool_libs],
        ["ZUGFeRD / Factur-X invoice"] + [cell(l, "einvoice", run.rows(o, "einvoice")) for l in tool_libs],
    ]
    w("Windows:\n")
    w(table(["", *[name[l] for l in tool_libs]], rows))
    w("")
    tms = collections.defaultdict(dict)
    for r in tr:
        if r["library"] in tool_libs and num(r["ms"]) is not None and r["tool"] in ("extract", "rasterize", "word", "merge", "split", "watermark", "sign", "fill", "redact", "einvoice"):
            tms[(r["tool"], r["input"])][r["library"]] = num(r["ms"])
    w("Time per operation (ms, Windows):\n")
    w(table(["Operation", *[name[l] for l in tool_libs]],
            [[f"{t} ({i})"] + [fmt(v.get(l)) for l in tool_libs] for (t, i), v in tms.items()]))
    w("")

    # ---- errors, parallel
    w("## Errors, timeouts and parallel output\n")
    w("*Errors*: a page that never finishes loading, a resource that never arrives, a 404, an unreachable host, a "
      "broken image, a script error and a slow resource; each must end the expected way (a PDF or an error) and the "
      "library must convert normally afterwards. *Parallel*: the same pages converted 8 at a time must equal the PDF "
      "converted alone (same text, pixels within tolerance).\n")
    for o in OSES:
        ec = run.rows(o, "errors_checks")
        er = {(r["library"], r["case"]): r for r in run.rows(o, "errors")}
        pa = run.rows(o, "parallel")
        cases = [c for c in dict.fromkeys(r["case"] for r in ec) if c != "all"]
        hl = [l for l in libs if any(r["library"] == l and r["pass"] != "n/a" for r in ec)]
        rows = []
        for case in cases:
            cells = []
            for l in hl:
                c = next((r for r in ec if r["library"] == l and r["case"] == case), None)
                e = er.get((l, case))
                if c is None or c["pass"] == "n/a":
                    cells.append("–")
                    continue
                secs = num(e["ms"], 0) / 1000 if e else 0
                what = {"pdf": "PDF", "error": "error", "hung": "never returned"}.get(c["outcome"], c["outcome"])
                cells.append(("✓ " if ok(c["pass"]) else "✗ ") + f"{what} ({secs:,.1f} s)")
            rows.append([case] + cells)
        rows.append(["*parallel = alone*"] + [f"*{sum(ok(r['same']) for r in pa if r['library'] == l)} / {sum(1 for r in pa if r['library'] == l)}*" for l in hl])
        w(f"### {OS_NAME[o]}\n")
        w(table(["", *[name[l] for l in hl]], rows))
        w("")

    # ---- deployment
    w("## Deployment\n")
    w("Linux images built from `mcr.microsoft.com/dotnet/runtime:8.0` with the published app: *minimal* adds nothing; "
      "*documented* follows the vendor's own Docker instructions where they exist. Each image converts a page (or, "
      "for libraries without HTML, merges a PDF) as root and as a non-root user. Publish size is the `dotnet publish` "
      "output for the runtime identifier.\n")
    dep = run.rows("linux", "deployment")
    ps = {(r["library"], r["rid"]): num(r["publish_mb"]) for o in OSES for r in run.rows(o, "publish_size")}
    rows = []
    for l in libs:
        for r in [r for r in dep if r["library"] == l]:
            if r["root_ok"] == "build failed":
                res = "image does not build"
            else:
                res = ("✓" if ok(r["root_ok"]) else "✗") + " root, " + ("✓" if ok(r["non_root_ok"]) else "✗") + " non-root"
            err = ""
            if r["root_ok"] == "build failed":
                err = "the documented steps do not complete on this base image"
            elif r["error"] and not (ok(r["root_ok"]) and ok(r["non_root_ok"])):
                err = r["error"].replace("Unhandled exception. ", "").split(":")[0].split(".")[-1]
            rows.append([name[l], r["variant"], r["extra_os_packages"], fmt(num(r["image_mb"])), res, err,
                         fmt(ps.get((l, "win-x64")), 1), fmt(ps.get((l, "linux-x64")), 1)])
    w(table(["Library", "Image", "Extra OS packages", "Image (MB)", "Result", "Failure", "Publish win-x64 (MB)", "Publish linux-x64 (MB)"], rows))
    w("")

    # ---- notes
    w("## Reading these results\n")
    w("- One machine, one run. Linux runs in WSL2 on the same hardware as Windows, not on a separate Linux host.")
    w("- Commercial libraries run on their free trial or evaluation, with any evaluation watermark masked before image "
      "comparison (see METHODOLOGY.md, section 2). PDFsharp 6.5 runs under its AGPL licence.")
    w("- Each library is configured as its vendor documents; the adapter code for every library is in "
      f"[`harness/`]({REPO}/tree/main/harness). Vendors are invited to correct it.")
    w(f"- Prices are in [`pricing/`]({REPO}/tree/main/pricing), dated, from each vendor's page.")
    pathlib.Path(a.out).write_text("\n".join(md) + "\n", encoding="utf-8", newline="\n")
    print("wrote", a.out)


if __name__ == "__main__":
    main()
