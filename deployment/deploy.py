"""Deployment (METHODOLOGY.md, 4.9): what it takes to run each library on Linux in Docker, and how big it is.

For every Dockerfile in deployment/<library>/ (minimal first, then the vendor's documented setup if there is
one; a generic minimal image when the library has none), the adapter is published for linux-x64, the image is
built, and one HTML string is converted inside a container - as root and as the image's non-root user ("app"
in the .NET images), with no extra docker flags. A library that does not convert HTML merges a sample PDF
baked into the image instead. Also records the size of `dotnet publish` output per runtime identifier.

    python deployment/deploy.py --results results/<run> [--libraries selectpdf,itext]   (Docker, Linux containers)

Writes results/<run>/deployment.csv and publish_size.csv.
"""
import argparse
import csv
import json
import os
import pathlib
import shutil
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
HTML = ("<html><head><style>body{font-family:sans-serif;margin:40px}</style></head><body>"
        "<h1>Docker deployment test</h1><p>Plain text, a table and a gradient.</p>"
        "<table border=1><tr><td>A1</td><td>B1</td></tr></table>"
        "<div style='height:80px;background:linear-gradient(90deg,#2563eb,#16a34a)'></div></body></html>")


def adapter(lib: str) -> tuple[str, str]:
    """harness/<Name>/ for a library name in lower case, and its executable adapter-<name>."""
    for d in (ROOT / "harness").iterdir():
        if d.name.lower() == lib and list(d.glob("*.Adapter.csproj")):
            return d.name, f"adapter-{lib}"
    raise SystemExit(f"no adapter for {lib}")


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)


def publish(project: str, rid: str, out: pathlib.Path) -> int:
    shutil.rmtree(out, ignore_errors=True)
    r = run(["dotnet", "publish", str(ROOT / "harness" / project), "-c", "Release", "-r", rid, "--self-contained", "false",
             f"-p:NativeRid={rid}", "-o", str(out), "--nologo", "-v", "quiet"])
    if r.returncode:
        raise SystemExit(f"publish {project} {rid} failed:\n{r.stdout}\n{r.stderr}")
    return sum(f.stat().st_size for f in out.rglob("*") if f.is_file())


def in_container(image: str, user: str | None, command: dict) -> tuple[bool, float, str]:
    """Runs one command in a fresh container; returns (ok, seconds to the answer, error)."""
    cmds = "\n".join(json.dumps(c) for c in [command, {"op": "quit"}]) + "\n"
    args = ["docker", "run", "-i", "--rm", "-e", "IRONPDF_LICENSE_KEY", "-e", "ABCPDF_LICENSE_KEY", "-e", "DOCOTIC_LICENSE_KEY"]
    if user:
        args += ["--user", user]
    start = time.perf_counter()
    r = run(args + [image], input=cmds, timeout=900, env=os.environ)
    seconds = time.perf_counter() - start
    for line in r.stdout.splitlines():
        if line.startswith("{"):
            reply = json.loads(line)
            if reply.get("ok") is False:
                return False, seconds, ("unsupported: " if reply.get("unsupported") else "") + reply.get("error", "")[:300]
            if "pages" in reply or "bytes" in reply:
                return True, seconds, ""
    # the exception message (e.g. "Unhandled exception. System.DllNotFoundException: ..."), not the last stack frame
    lines = r.stderr.strip().splitlines()
    cause = next((l for l in lines if "Exception:" in l), lines[-1] if lines else "no answer")
    return False, seconds, cause.strip()[:300]


def convert_in_container(image: str, user: str | None) -> tuple[bool, float, str]:
    ok, seconds, error = in_container(image, user, {"op": "convert", "html": HTML, "baseUrl": "", "out": "/tmp/test.pdf"})
    if not ok and error.startswith("unsupported"):   # not an HTML converter: merge the sample PDF instead
        return in_container(image, user, {"op": "merge", "files": ["/app/sample.pdf", "/app/sample.pdf"], "out": "/tmp/test.pdf"})
    return ok, seconds, error


def packages(dockerfile: pathlib.Path) -> list[str]:
    text = dockerfile.read_text(encoding="utf-8").replace("\\\n", " ")
    pkgs = []
    for line in text.splitlines():
        if "apt-get install" in line:
            part = line.split("apt-get install", 1)[1].split("&&")[0]
            pkgs += [w for w in part.split() if not w.startswith("-")]
    return pkgs


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", required=True)
    ap.add_argument("--libraries", default="", help="comma list; default: every adapter in harness/")
    a = ap.parse_args()
    results = pathlib.Path(a.results)
    results.mkdir(parents=True, exist_ok=True)
    build = ROOT / "deployment" / "build"
    libs = [l for l in a.libraries.split(",") if l] or sorted(
        d.name.lower() for d in (ROOT / "harness").iterdir() if list(d.glob("*.Adapter.csproj")))
    deploy_rows, size_rows = [], []
    for lib in libs:
        project, exe = adapter(lib)
        for rid in ("win-x64", "linux-x64"):
            size = publish(project, rid, build / lib / rid)
            size_rows.append([lib, rid, round(size / 1048576, 1)])
            print(f"publish {lib} {rid}: {size / 1048576:.1f} MB", flush=True)
        dockerfiles = sorted((ROOT / "deployment" / lib).glob("*.Dockerfile"), key=lambda p: p.name != "minimal.Dockerfile")
        if not dockerfiles:   # no library-specific setup: the generic minimal image
            generated = build / lib / "minimal.Dockerfile"
            generated.write_text((ROOT / "deployment" / "minimal.Dockerfile.template").read_text(encoding="utf-8")
                                 .replace("{exe}", exe), encoding="utf-8")
            dockerfiles = [generated]
        for dockerfile in dockerfiles:
            variant = dockerfile.stem
            context = build / lib / "context"
            shutil.rmtree(context, ignore_errors=True)
            shutil.copytree(build / lib / "linux-x64", context / "app")
            shutil.copy(ROOT / "fixtures" / "inputs" / "pdf" / "invoice.pdf", context / "app" / "sample.pdf")
            image = f"pdf-benchmarks/{lib}:{variant}"
            r = run(["docker", "build", "-f", str(dockerfile), "-t", image, str(context)], timeout=1800)
            if r.returncode:
                deploy_rows.append([lib, variant, len(packages(dockerfile)), "", "build failed", "", "", "", r.stderr[-300:]])
                continue
            size = int(run(["docker", "image", "inspect", image, "--format", "{{.Size}}"]).stdout.strip())
            root_ok, root_s, root_err = convert_in_container(image, None)
            user_ok, user_s, user_err = convert_in_container(image, "app")
            deploy_rows.append([lib, variant, len(packages(dockerfile)), round(size / 1048576), root_ok, f"{root_s:.1f}",
                                user_ok, f"{user_s:.1f}", root_err or user_err])
            print(f"docker {lib} {variant}: {size / 1048576:.0f} MB, root {root_ok} ({root_s:.1f} s), non-root {user_ok} ({user_s:.1f} s)"
                  + (f"  {(root_err or user_err)[:160]}" if (root_err or user_err) else ""), flush=True)
    for name, header, rows in (("deployment.csv", ["library", "variant", "extra_os_packages", "image_mb", "root_ok",
                                                    "root_seconds", "non_root_ok", "non_root_seconds", "error"], deploy_rows),
                               ("publish_size.csv", ["library", "rid", "publish_mb"], size_rows)):
        with open(results / name, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(header)
            w.writerows(rows)


if __name__ == "__main__":
    main()
