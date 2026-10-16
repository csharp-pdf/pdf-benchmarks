#!/usr/bin/env bash
# Linux benchmark run: publishes the harness outside the source tree, serves the fixtures,
# runs every step and leaves raw results in results/<run>/ and PDFs in out/<run>/pdf/.
#
#   IRONPDF_LICENSE_KEY=<key> ABCPDF_LICENSE_KEY=<key> DOCOTIC_LICENSE_KEY=<key> LIBRARIES=selectpdf,itext ./run.sh [runner options, e.g. --rounds 3 --only speed]
#
# Every adapter is harness/<Name>/<Name>.Adapter.csproj with AssemblyName adapter-<name in lower case>;
# all of them run unless LIBRARIES names some.
# Needs the .NET 8 SDK and python3. Analysis (analysis/analyze.py) can run here or on another machine.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
RUN="${RUN:-$(date +%F)-linux}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
BIN="${BIN:-$HOME/.cache/pdf-benchmarks/bin}"   # publish to a local disk: loading engines from a network/9p mount would skew cold starts
PORT="${PORT:-8777}"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET")")}"   # the adapters are apphost executables
# PDFsharp resolves fonts itself off Windows: point it at a TrueType font the distribution ships.
export PDFSHARP_FONT_FILE="${PDFSHARP_FONT_FILE:-/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf}"

cd "$ROOT"
FOUND=()
for d in harness/*/; do
  name="$(basename "$d")"
  ls "$d"*.Adapter.csproj >/dev/null 2>&1 || continue
  lower="$(echo "$name" | tr '[:upper:]' '[:lower:]')"
  if [ -n "${LIBRARIES:-}" ] && [[ ",$LIBRARIES," != *",$lower,"* ]]; then continue; fi
  FOUND+=("$name")
done
for p in Runner "${FOUND[@]}"; do
  "$DOTNET" publish "harness/$p" -c Release -o "$BIN/$p" --nologo -v quiet
done

python3 server/serve.py "$PORT" & SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
sleep 1

ADAPTERS=()
for name in "${FOUND[@]}"; do
  lower="$(echo "$name" | tr '[:upper:]' '[:lower:]')"
  if [ "$lower" = ironpdf ] && [ -z "${IRONPDF_LICENSE_KEY:-}" ]; then
    echo "IRONPDF_LICENSE_KEY not set: IronPDF is skipped" >&2; continue
  fi
  # ABCpdf creates no trial licence on Linux (its "ABCpdf on Linux" docs): without a key every conversion fails
  if [ "$lower" = abcpdf ] && [ -z "${ABCPDF_LICENSE_KEY:-}" ]; then
    echo "ABCPDF_LICENSE_KEY not set: ABCpdf is skipped (Linux has no ABCpdf trial)" >&2; continue
  fi
  if [ "$lower" = docotic ] && [ -z "${DOCOTIC_LICENSE_KEY:-}" ]; then
    echo "DOCOTIC_LICENSE_KEY not set: Docotic.Pdf is skipped" >&2; continue
  fi
  ADAPTERS+=(--adapter "$lower=$BIN/$name/adapter-$lower")
done

"$DOTNET" "$BIN/Runner/runner.dll" "${ADAPTERS[@]}" --base "http://127.0.0.1:$PORT" \
  --results "results/$RUN" --pdfs "out/$RUN/pdf" "$@"
