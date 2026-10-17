"""Fills the "Benchmark results" section of each library page in csharp-pdf/pdf-libraries-compared.

    python analysis/library_sections.py --run 2026-10-15 --pages ../pdf-libraries-compared
        [--override windows:pdfsharp=2026-10-15-pdfsharp-windows ...]   (as for report.py)

Every page whose README.md contains the marker line below gets a short table for its library, generated from
results/<run>-<os>/*.csv, and a link to the full results page. Anything between the marker and the next "## "
heading is replaced, so the section can be regenerated after every run.
"""
import argparse
import collections
import pathlib
import statistics

import report

MARKER = "<!-- benchmark-results: generated, do not edit -->"
# page folder -> library id in the results
LIBRARY = {"puppeteersharp": "puppeteer"}


def section(run: report.Run, lib: str, run_name: str) -> str:
    ok, num, fmt = report.ok, report.num, report.fmt
    page = f"../benchmarks/{run_name}.md"
    lines = [MARKER, "",
             f"From the [{run_name} run]({page}) (Windows and Linux on the same machine; method and raw data are "
             "linked there). Each value is this library's own result; compare it with the other libraries on the "
             "results page.", ""]
    rows = []
    for o in report.OSES:
        osn = report.OS_NAME[o]
        sp = collections.defaultdict(list)
        for r in run.rows(o, "speed"):
            if r["ok"] == "true":
                sp[(r["fixture"], r["library"])].append(float(r["ms"]))
        mine = {f: statistics.median(v) for (f, l), v in sp.items() if l == lib}
        if mine:
            rows.append([f"HTML to PDF, median per page ({osn})",
                         f"{fmt(min(mine.values()))}–{fmt(max(mine.values()))} ms over {len(mine)} pages; geometric mean "
                         f"{fmt(statistics.geometric_mean(mine.values()))} ms"])
        cold = [num(r["process_start_to_pdf_ms"]) for r in run.rows(o, "cold") if r["library"] == lib and r["ok"] == "true"]
        if cold:
            rows.append([f"Cold start ({osn})", f"{fmt(statistics.median(cold))} ms"])
        tp = {(r["mode"], r["concurrency"]): num(r["per_minute"]) for r in run.rows(o, "throughput") if r["library"] == lib}
        if any(v is not None for v in tp.values()):
            rows.append([f"Throughput, conversions/min, 1 / 8 at once ({osn})",
                         f"{fmt(tp.get(('sync', '1')))} / {fmt(tp.get(('sync', '8')))} (sync); "
                         f"{fmt(tp.get(('async', '1')))} / {fmt(tp.get(('async', '8')))} (async)"])
        st = [r for r in run.rows(o, "stability") if r["library"] == lib and num(r["pss_mb"]) is not None]
        if st:
            rows.append([f"Memory over 1,000 conversions ({osn})",
                         f"{fmt(num(st[0]['pss_mb']))} → {fmt(num(st[-1]['pss_mb']))} MB, "
                         f"{fmt(num(st[-1]['failures'], 0))} failure" + ("" if num(st[-1]['failures'], 0) == 1 else "s")])
    o = "windows"
    ch = [r for r in run.rows(o, "checks") if r["library"] == lib and r["pass"] != "n/a"]
    if ch:
        failed = sorted({f"{r['fixture']} ({r['check']})" for r in ch if not ok(r["pass"])})
        rows.append(["Content checks (Windows)", f"{sum(ok(r['pass']) for r in ch)} / {len(ch)}"
                     + (f"; failed: {', '.join(failed)}" if failed else "")])
    fi = [float(r["ssim_mean"]) for r in run.rows(o, "fidelity") if r["library"] == lib and r["ssim_mean"]]
    if fi:
        rows.append(["Fidelity vs Chrome, mean SSIM (Windows)", f"{statistics.mean(fi):.3f}"])
    sr = [r for r in run.rows(o, "standards_runs") if r["library"] == lib]
    sd = [r for r in run.rows(o, "standards") if r["library"] == lib]
    px = [r for r in run.rows(o, "pdfx") if r["library"] == lib]
    produced = [r["standard"] for r in sr if r["ok"] == "true"]
    if produced:
        def compliant(s):
            if s.startswith("pdfx-"):
                return bool(px) and all(ok(r["pass"]) for r in px)
            v = [ok(r["compliant"]) for r in sd if r["standard"] == s]
            return bool(v) and all(v)
        rows.append(["PDF/A, PDF/UA, PDF/X levels produced / valid (of 17)",
                     f"{len(produced)} / {sum(1 for s in produced if compliant(s))}"])
    tr = [r for r in run.rows(o, "tools_runs") if r["library"] == lib]
    if tr:
        checks = {"extract": ("extraction", None), "search": ("search", "pass"), "rasterize": ("raster", "size_ok"),
                  "redact": ("redaction", "pass"), "merge": ("merge_split", "pass"), "split": ("merge_split", "pass"),
                  "watermark": ("watermark", "pass"), "sign": ("signature", "pass"), "fill": ("fill", "pass"),
                  "einvoice": ("einvoice", "pass")}
        label = {"extract": "text extraction", "search": "search", "rasterize": "PDF to image", "word": "Word to PDF",
                 "redact": "redaction", "merge": "merge", "split": "split", "watermark": "watermark",
                 "sign": "signature", "fill": "form filling", "einvoice": "ZUGFeRD/Factur-X"}
        passed, partial, unsupported = [], [], []
        for tool in label:
            runs = [r for r in tr if r["tool"] == tool]
            if not runs:
                continue
            if all(ok(r["unsupported"]) for r in runs):
                unsupported.append(label[tool])
                continue
            name, key = checks.get(tool, (None, None))
            rows_ = [r for r in run.rows(o, name) if r["library"] == lib] if name else []
            if name == "merge_split":
                rows_ = [r for r in rows_ if r["operation"] == tool]
            if name == "extraction":
                f1 = [float(r["text_f1"]) for r in rows_ if r["text_f1"]]
                (passed if f1 and min(f1) >= 0.9 else partial).append(
                    f"{label[tool]}" + (f" (lowest F1 {min(f1):.2f})" if f1 and min(f1) < 0.9 else ""))
                continue
            if key:
                good = sum(ok(r[key]) for r in rows_ if r.get(key) != "n/a")
                total = sum(1 for r in rows_ if r.get(key) != "n/a")
                (passed if total and good == total else partial).append(
                    label[tool] + ("" if total and good == total else f" ({good}/{total} checks)"))
            else:
                (passed if all(ok(r["ok"]) for r in runs) else partial).append(label[tool])
        text = []
        if passed:
            text.append("all checks pass: " + ", ".join(passed))
        if partial:
            text.append("some checks fail: " + ", ".join(partial))
        if unsupported:
            text.append("not supported: " + ", ".join(unsupported))
        rows.append(["Working with existing PDFs and Word files (Windows)", "; ".join(text)])
    dep = [r for r in run.rows("linux", "deployment") if r["library"] == lib]
    for r in dep:
        res = ("image does not build" if r["root_ok"] == "build failed" else
               ("runs" if ok(r["root_ok"]) and ok(r["non_root_ok"]) else
                "runs as root only" if ok(r["root_ok"]) else "does not run"))
        rows.append([f"Docker, {r['variant']} image", f"{res}" + (f", {fmt(num(r['image_mb']))} MB" if num(r["image_mb"]) else "")])
    if not rows:
        return "\n".join(lines + ["No results for this library in this run.", ""])
    return "\n".join(lines + [report.table(["", "Result"], rows), ""])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--pages", required=True)
    ap.add_argument("--override", action="append", default=[])
    a = ap.parse_args()
    overrides = {}
    for o in a.override:
        left, folder = o.split("=")
        os_, lib = left.split(":")
        overrides[(os_, lib)] = folder
    run = report.Run(a.run, overrides)
    for readme in sorted(pathlib.Path(a.pages).glob("*/README.md")):
        text = readme.read_text(encoding="utf-8")
        if MARKER not in text:
            continue
        lib = LIBRARY.get(readme.parent.name, readme.parent.name)
        start = text.index(MARKER)
        nxt = text.find("\n## ", start)
        end = nxt + 1 if nxt >= 0 else len(text)
        readme.write_text(text[:start] + section(run, lib, a.run) + "\n" + text[end:], encoding="utf-8", newline="\n")
        print("updated", readme)


if __name__ == "__main__":
    main()
