"""PDF/A and PDF/UA results (METHODOLOGY.md, section 4.7). Writes:

  standards.csv   library, standard, profile, compliant, failed_rules, rules
                  - every file is validated against each profile it was ASKED for, and once with
                    veraPDF's automatic profile (-f 0), which uses the standard the file CLAIMS
                    in its metadata (profile "auto: <name>")
  structure.csv   library, standard, check, expected, found, pass
                  - for files asked to be accessible: language, title, and the tag structure
                    compared with what the HTML contains

veraPDF is found through the VERAPDF environment variable (path to verapdf / verapdf.bat) or on
PATH; VERAPDF_JAVA_HOME may point at a Java runtime when none is on PATH. A missing veraPDF is an
error, not a skip: a published table must not lose a column silently.
"""
import csv
import json
import os
import pathlib
import re
import shutil
import subprocess

from pypdf import PdfReader
from pypdf.generic import ArrayObject, DictionaryObject, IndirectObject

ROOT = pathlib.Path(__file__).resolve().parents[1]


def verapdf() -> tuple[str, dict]:
    exe = os.environ.get("VERAPDF") or shutil.which("verapdf") or shutil.which("verapdf.bat")
    if not exe or not pathlib.Path(exe).exists():
        raise SystemExit("veraPDF not found: set VERAPDF to the verapdf launcher (and VERAPDF_JAVA_HOME if java is not on PATH)")
    env = dict(os.environ)
    if env.get("VERAPDF_JAVA_HOME"):
        env["JAVA_HOME"] = env["VERAPDF_JAVA_HOME"]
        env["PATH"] = str(pathlib.Path(env["VERAPDF_JAVA_HOME"]) / "bin") + os.pathsep + env.get("PATH", "")
    return exe, env


def profiles(standard: str) -> list[str]:
    """pdfa-2a+pdfua-1 -> ["2a", "ua1"]"""
    out = []
    for part in standard.split("+"):
        if part.startswith("pdfa-"):
            out.append(part[5:])
        elif part.startswith("pdfua-"):
            out.append("ua" + part[6:])
    return out


def validate(exe: str, env: dict, pdf: pathlib.Path, flavour: str) -> dict:
    run = subprocess.run([exe, "-f", flavour, str(pdf)], env=env, capture_output=True, text=True,
                         encoding="utf-8", errors="replace", timeout=600)
    xml = run.stdout + run.stderr
    if "isCompliant=" not in xml:
        return {"profile": flavour, "compliant": "", "failed_rules": "", "rules": "veraPDF error: " + xml.strip()[:200]}
    m = re.search(r'profileName="([^"]*)"', xml)
    rules = []
    for r in re.finditer(r'<rule\b([^>]*status="failed"[^>]*)>(.*?)</rule>', xml, re.S):
        attrs = dict(re.findall(r'(\w+)="([^"]*)"', r.group(1)))
        msg = re.search(r"<description>(.*?)</description>", r.group(2), re.S)
        rules.append(f"{attrs.get('specification', '')} {attrs.get('clause', '?')}-{attrs.get('testNumber', '?')}"
                     + (f": {msg.group(1).strip()[:90]}" if msg else ""))
    name = m.group(1).replace(" validation profile", "") if m else flavour
    return {"profile": flavour if flavour != "0" else "auto: " + name,
            "compliant": 'isCompliant="true"' in xml, "failed_rules": len(rules), "rules": " | ".join(rules[:6])}


# ---- tag structure ------------------------------------------------------------------------------

def resolve(o):
    return o.get_object() if isinstance(o, IndirectObject) else o


def structure(pdf: pathlib.Path) -> dict:
    reader = PdfReader(str(pdf))
    root = reader.trailer["/Root"]
    title = (reader.metadata or {}).get("/Title", "") or ""
    if not title:   # PDF 2.0 deprecates the Info dictionary: the title lives in the XMP packet
        xmp = resolve(root.get("/Metadata"))
        if xmp is not None:
            m = re.search(r"<dc:title>.*?<rdf:li[^>]*>(.*?)</rdf:li>", xmp.get_data().decode("utf-8", "replace"), re.S)
            title = m.group(1).strip() if m else ""
    info = {"lang": str(root.get("/Lang", "")), "title": title,
            "displayDocTitle": bool(resolve(root.get("/ViewerPreferences", {})).get("/DisplayDocTitle", False)),
            "counts": {}, "figureAlts": []}
    tree = resolve(root.get("/StructTreeRoot"))
    if tree is None:
        return info
    role_map = {str(k): str(v) for k, v in resolve(tree.get("/RoleMap", {})).items()}

    def std(name: str) -> str:
        seen = set()
        while name in role_map and name not in seen:   # follow custom roles to a standard type
            seen.add(name)
            name = role_map[name]
        return name.lstrip("/")

    def walk(node, depth=0):
        node = resolve(node)
        if depth > 200:
            return
        if isinstance(node, ArrayObject):
            for k in node:
                walk(k, depth + 1)
            return
        if not isinstance(node, DictionaryObject) or "/S" not in node:
            return
        s = std(str(node["/S"]))
        info["counts"][s] = info["counts"].get(s, 0) + 1
        if s == "Figure":
            info["figureAlts"].append(str(node.get("/Alt", "")))
        if "/K" in node:
            walk(node["/K"], depth + 1)

    walk(tree.get("/K"))
    return info


def run(run_dir: pathlib.Path, results: pathlib.Path) -> None:
    manifest = json.loads((ROOT / "fixtures" / "fixtures.json").read_text(encoding="utf-8"))
    spec = manifest["standards"]
    exe, env = verapdf()
    std_rows, struct_rows = [], []
    for lib_dir in sorted(d for d in (run_dir / "pdf").iterdir() if (d / "standards").is_dir()):
        lib = lib_dir.name
        for level in spec["levels"]:
            pdf = lib_dir / "standards" / f"{level}.pdf"
            if not pdf.exists():
                continue   # unsupported or failed: recorded in standards_runs.csv
            if level.startswith("pdfx"):
                continue   # no veraPDF profile for PDF/X: checked structurally in features.py (pdfx.csv)
            for flavour in profiles(level) + ["0"]:
                v = validate(exe, env, pdf, flavour)
                std_rows.append([lib, level, v["profile"], v["compliant"], v["failed_rules"], v["rules"]])
                print(f"{lib:10} {level:18} {v['profile']:22} {'compliant' if v['compliant'] is True else 'NOT compliant' if v['compliant'] is False else v['rules'][:40]}"
                      + (f"  ({v['failed_rules']} rules failed)" if v["failed_rules"] else ""), flush=True)
            if "pdfua" in level or re.search(r"pdfa-\da(\+|$)", level):   # asked for an accessible (tagged) file
                s = structure(pdf)
                def row(check, expected, found, ok):
                    struct_rows.append([lib, level, check, expected, found, ok])
                row("lang", spec["language"], s["lang"], s["lang"].lower().startswith(spec["language"]))
                row("title", spec["title"], s["title"], s["title"] == spec["title"])
                row("displayDocTitle", True, s["displayDocTitle"], s["displayDocTitle"])
                for tag, n in spec["structure"].items():
                    if tag == "figureAlt":
                        row("Figure /Alt", n, " | ".join(s["figureAlts"]), n in s["figureAlts"])
                    else:
                        found = s["counts"].get(tag, 0)
                        row(tag, n, found, found == n)

    def write(name, header, rows):
        with open(results / name, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(header)
            w.writerows(rows)

    write("standards.csv", ["library", "standard", "profile", "compliant", "failed_rules", "rules"], std_rows)
    write("structure.csv", ["library", "standard", "check", "expected", "found", "pass"], struct_rows)


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--results", required=True)
    a = ap.parse_args()
    run(pathlib.Path(a.run), pathlib.Path(a.results))
