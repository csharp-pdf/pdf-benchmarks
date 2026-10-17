"""Fills the "Benchmark" section of each migration guide in selectpdf/migrate-to-selectpdf with the library and
SelectPdf side by side, on every axis of one run.

    python analysis/pair_sections.py --run 2026-10-15 --guides ../../migrate-to-selectpdf
        [--override linux:wkhtmltopdf=2026-10-15-wkhtmltopdf-linux ...]   (as for report.py)

A guide folder from-<library>/README.md that contains the marker line below gets the table; anything between the
marker and the next "## " heading is replaced. Every row is generated from results/<run>-<os>/*.csv: all axes, in
both directions, with no selection and no commentary.
"""
import argparse
import collections
import pathlib
import statistics

import report

MARKER = "<!-- benchmark-pair: generated, do not edit -->"
GUIDE_LIBRARY = {"from-puppeteersharp": "puppeteer"}
SELF = "selectpdf"


def values(run: report.Run, lib: str) -> dict:
    ok, num = report.ok, report.num
    v = {}
    for o in report.OSES:
        sp = collections.defaultdict(list)
        for r in run.rows(o, "speed"):
            if r["ok"] == "true" and r["library"] == lib:
                sp[r["fixture"]].append(float(r["ms"]))
        v[("speed", o)] = {f: statistics.median(x) for f, x in sp.items()}
        cold = [num(r["process_start_to_pdf_ms"]) for r in run.rows(o, "cold") if r["library"] == lib and r["ok"] == "true"]
        v[("cold", o)] = statistics.median(cold) if cold else None
        v[("tp", o)] = {(r["mode"], r["concurrency"]): num(r["per_minute"]) for r in run.rows(o, "throughput") if r["library"] == lib}
        mem = [r for r in run.rows(o, "memory") if r["library"] == lib]
        one = [num(r["peak_pss_mb"]) for r in mem if r["parallel"] == "1" and num(r["peak_pss_mb"])]
        eight = [num(r["peak_pss_mb"]) for r in mem if r["parallel"] != "1" and num(r["peak_pss_mb"])]
        v[("mem1", o)] = statistics.median(one) if one else None
        v[("mem8", o)] = max(eight) if eight else None
        st = [r for r in run.rows(o, "stability") if r["library"] == lib and num(r["pss_mb"]) is not None]
        v[("stab", o)] = st
        ch = [r for r in run.rows(o, "checks") if r["library"] == lib and r["pass"] != "n/a"]
        v[("checks", o)] = (sum(ok(r["pass"]) for r in ch), len(ch))
        fi = [float(r["ssim_mean"]) for r in run.rows(o, "fidelity") if r["library"] == lib and r["ssim_mean"]]
        v[("fid", o)] = statistics.mean(fi) if fi else None
        pa = [r for r in run.rows(o, "parallel") if r["library"] == lib]
        v[("parallel", o)] = (sum(ok(r["same"]) for r in pa), len(pa))
        er = {r["case"]: r for r in run.rows(o, "errors") if r["library"] == lib}
        v[("hang", o)] = er.get("hanging-page")
    o = "windows"
    sr = [r for r in run.rows(o, "standards_runs") if r["library"] == lib]
    sd = [r for r in run.rows(o, "standards") if r["library"] == lib]
    px = [r for r in run.rows(o, "pdfx") if r["library"] == lib]

    def compliant(s):
        if s.startswith("pdfx-"):
            return bool(px) and all(ok(r["pass"]) for r in px)
        x = [ok(r["compliant"]) for r in sd if r["standard"] == s]
        return bool(x) and all(x)
    produced = [r["standard"] for r in sr if r["ok"] == "true"]
    v["standards"] = (len(produced), sum(1 for s in produced if compliant(s)), len(sr))
    stc = [r for r in run.rows(o, "structure") if r["library"] == lib]
    v["structure"] = (sum(ok(r["pass"]) for r in stc), len(stc))
    tr = [r for r in run.rows(o, "tools_runs") if r["library"] == lib]
    v["tools"] = {r["tool"]: ("not supported" if ok(r["unsupported"]) else "supported") for r in tr}
    ex = [float(r["text_f1"]) for r in run.rows(o, "extraction") if r["library"] == lib and r["text_f1"]]
    v["extract"] = min(ex) if ex else None
    v["deploy"] = [r for r in run.rows("linux", "deployment") if r["library"] == lib]
    v["publish"] = {r["rid"]: num(r["publish_mb"]) for o2 in report.OSES for r in run.rows(o2, "publish_size") if r["library"] == lib}
    return v


def failures(n) -> str:
    return f"{report.fmt(n)} failure" + ("" if n == 1 else "s")


def section(run: report.Run, lib: str, run_name: str, rows_only: bool = False):
    fmt = report.fmt
    name = run.packages("windows")[lib]["library"]
    a, b = values(run, lib), values(run, SELF)
    rows = []

    def row(label, f):
        rows.append([label, f(a), f(b)])

    for o in report.OSES:
        osn = report.OS_NAME[o]
        common = sorted(set(a[("speed", o)]) & set(b[("speed", o)]))
        if common:
            row(f"HTML to PDF, geometric mean of {len(common)} page medians, ms ({osn})",
                lambda v: fmt(statistics.geometric_mean([v[("speed", o)][f] for f in common])))
            row(f"Pages converted faster than the other library ({osn})",
                lambda v: f"{sum(1 for f in common if v[('speed', o)][f] < (b if v is a else a)[('speed', o)][f])} of {len(common)}")
        if a[("cold", o)] is not None or b[("cold", o)] is not None:
            row(f"Cold start, ms ({osn})", lambda v: fmt(v[("cold", o)]))
        for mode in ("sync", "async"):
            if any(x is not None for x in list(a[("tp", o)].values()) + list(b[("tp", o)].values())):
                row(f"Throughput, conversions/min, 1 / 4 / 8 at once, {mode} ({osn})",
                    lambda v: " / ".join(fmt(v[("tp", o)].get((mode, c))) for c in ("1", "4", "8"))
                    if any(v[("tp", o)].get((mode, c)) is not None for c in ("1", "4", "8")) else "not supported")
        row(f"Memory, one page / 8 at once, MB ({osn})", lambda v: f"{fmt(v[('mem1', o)])} / {fmt(v[('mem8', o)])}")
        row(f"Memory over 1,000 conversions, MB ({osn})",
            lambda v: f"{fmt(report.num(v[('stab', o)][0]['pss_mb']))} → {fmt(report.num(v[('stab', o)][-1]['pss_mb']))}, "
                      + failures(report.num(v[('stab', o)][-1]['failures'], 0)) if v[("stab", o)] else "–")
        row(f"Page that never loads: gives up after, s ({osn})",
            lambda v: (f"{report.num(v[('hang', o)]['ms'], 0) / 1000:,.0f}" + (" (never returned)" if v[("hang", o)]["outcome"] == "hung" else ""))
            if v[("hang", o)] else "–")
        row(f"Parallel output equals sequential ({osn})", lambda v: f"{v[('parallel', o)][0]} / {v[('parallel', o)][1]}" if v[("parallel", o)][1] else "–")
    row("Content checks passed (Windows)", lambda v: f"{v[('checks', 'windows')][0]} / {v[('checks', 'windows')][1]}" if v[("checks", "windows")][1] else "–")
    row("Content checks passed (Linux)", lambda v: f"{v[('checks', 'linux')][0]} / {v[('checks', 'linux')][1]}" if v[("checks", "linux")][1] else "–")
    row("Fidelity vs Chrome, mean SSIM (Windows)", lambda v: f"{v[('fid', 'windows')]:.3f}" if v[("fid", "windows")] else "–")
    row("PDF/A, PDF/UA, PDF/X: levels produced / valid (of 17)",
        lambda v: f"{v['standards'][0]} / {v['standards'][1]}" if v["standards"][2] else "–")
    row("Tagged structure checks", lambda v: f"{v['structure'][0]} / {v['structure'][1]}" if v["structure"][1] else "–")
    row("Text extraction, lowest F1", lambda v: f"{v['extract']:.2f}" if v["extract"] is not None else
        ("not supported" if v["tools"].get("extract") == "not supported" else "–"))
    tools = [t for t in ("search", "rasterize", "word", "redact", "merge", "split", "watermark", "sign", "fill", "einvoice")
             if t in a["tools"] or t in b["tools"]]
    label = {"search": "Search", "rasterize": "PDF to image", "word": "Word to PDF", "redact": "Redaction", "merge": "Merge",
             "split": "Split", "watermark": "Watermark", "sign": "Digital signature", "fill": "Form filling",
             "einvoice": "ZUGFeRD / Factur-X"}
    for t in tools:
        row(label[t], lambda v: v["tools"].get(t, "–"))

    def deploy(v, variant):
        r = next((x for x in v["deploy"] if x["variant"] == variant), None)
        if r is None:
            minimal = next((x for x in v["deploy"] if x["variant"] == "minimal"), None)
            if variant == "documented" and minimal and report.ok(minimal["root_ok"]) and report.ok(minimal["non_root_ok"]):
                return "not needed (the minimal image runs)"
            return "–"
        if r["root_ok"] == "build failed":
            return "image does not build"
        res = "runs" if report.ok(r["root_ok"]) and report.ok(r["non_root_ok"]) else (
            "runs as root only" if report.ok(r["root_ok"]) else "does not run")
        return res + (f", {fmt(report.num(r['image_mb']))} MB" if report.num(r["image_mb"]) else "")
    row("Docker, minimal image (Linux)", lambda v: deploy(v, "minimal"))
    row("Docker, vendor-documented image (Linux)", lambda v: deploy(v, "documented"))
    row("Publish size win-x64 / linux-x64, MB", lambda v: f"{fmt(v['publish'].get('win-x64'), 1)} / {fmt(v['publish'].get('linux-x64'), 1)}")

    if rows_only:
        return name, rows
    page = f"https://github.com/csharp-pdf/pdf-libraries-compared/blob/main/benchmarks/{run_name}.md"
    head = [MARKER, "",
            f"{name} and SelectPdf on every axis of the [{run_name} benchmark run]({page}), generated from its raw data "
            "(same machine, Windows and Linux; methodology, per-page numbers and every other library are on that page). "
            "Lower is better for times and memory, higher for throughput, checks and fidelity.", ""]
    return "\n".join(head + [report.table(["", name, "SelectPdf"], rows), ""])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--guides")
    ap.add_argument("--html", metavar="LIBRARY", help="write one library's table as an HTML fragment (for a Razor partial)")
    ap.add_argument("--out", help="the file --html writes")
    ap.add_argument("--override", action="append", default=[])
    a = ap.parse_args()
    overrides = {}
    for o in a.override:
        left, folder = o.split("=")
        os_, lib = left.split(":")
        overrides[(os_, lib)] = folder
    run = report.Run(a.run, overrides)
    if a.html:
        import html as h
        name, rows = section(run, a.html, a.run, rows_only=True)
        lines = [f"@* Generated by csharp-pdf/pdf-benchmarks analysis/pair_sections.py --run {a.run} --html {a.html}; do not edit. *@",
                 '<table class="spdf-howto-table">',
                 f"  <thead><tr><th></th><th>{h.escape(name)}</th><th>SelectPdf</th></tr></thead>", "  <tbody>"]
        lines += [f"    <tr><td>{h.escape(r[0])}</td><td>{h.escape(str(r[1]))}</td><td>{h.escape(str(r[2]))}</td></tr>" for r in rows]
        lines += ["  </tbody>", "</table>", ""]
        pathlib.Path(a.out).write_text("\n".join(lines), encoding="utf-8", newline="\n")
        print("wrote", a.out)
        return
    for readme in sorted(pathlib.Path(a.guides).glob("from-*/README.md")):
        text = readme.read_text(encoding="utf-8")
        if MARKER not in text:
            continue
        guide = readme.parent.name
        lib = GUIDE_LIBRARY.get(guide, guide[len("from-"):])
        start = text.index(MARKER)
        nxt = text.find("\n## ", start)
        end = nxt + 1 if nxt >= 0 else len(text)
        readme.write_text(text[:start] + section(run, lib, a.run) + "\n" + text[end:], encoding="utf-8", newline="\n")
        print("updated", readme)


if __name__ == "__main__":
    main()
