#!/bin/bash
# Stand/alloc: allocation and memory audit of KenseiECS across runtimes (.NET 10, .NET 8),
# builds (release, KENSEI_DEBUG) and versions (baseline vs target).
#
#   Stand/alloc/run.sh [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>] [--debug]
#                      [--baseline <flavor>] [--baseline-src <folder>] [--runtimes net10,net8]
#                      [--only id,id] [--no-isolate] [--no-mem] [--no-timing] [--no-confirm] [--no-repro-check]
#   Stand/alloc/run.sh --ci-parity [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>]
#   Stand/alloc/run.sh --check-repros [--debug] [--src <KenseiECS folder>] [--only id,id]
#   Stand/alloc/run.sh --selftest [--quick|--full] [--src <KenseiECS folder>] [--mutants a,b] [--keep]
#
# Reports: Stand/out/reports/alloc/<flavor>-<rel|dbg>/report.md, report.json, repro/<id>/ (Program.cs + .csproj)
#          Stand/out/reports/alloc/<flavor>-ci/ for --ci-parity
#          Stand/out/reports/alloc/<flavor>-<rel|dbg>-repro-check/ for --check-repros
#          Stand/out/reports/alloc/selftest-<flavor>/selftest.md and .json for --selftest
# Needs Python 3.9+ (no packages), git, and a .NET SDK that builds net8.0 with a .NET 10 runtime
# (DOTNET_ROOT, `dotnet` on PATH or a standard install folder). The official .NET 8 runtime is
# downloaded into Stand/out/tools/dotnet8 on first use. Run with --help for every option.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
py=python3
command -v "$py" >/dev/null 2>&1 || py=python
exec "$py" -I -B "$here/tools/alloc.py" "$@"
