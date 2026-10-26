#!/usr/bin/env bash
# Stand module "runner": SystemsRunner / EcsBootstrap contracts and a randomized tester of runner trees.
#
#   Stand/runner/run.sh [--quick|--full] [--src DIR] [--flavor LABEL] [--debug] [--seed N] [--cases N]
#                       [--time-budget SEC] [--no-verify] [--fail-on-findings] [--contract-version X.Y.Z]
#                       [--baseline LABEL | --baseline-report FILE]
#   Stand/runner/run.sh --replay CASE.json [--src DIR] [--flavor LABEL] [--debug] [--contract-version X.Y.Z]
#   Stand/runner/run.sh --gen-seed N [--quick|--full] [--src DIR] [--flavor LABEL] [--debug]
#   Stand/runner/run.sh --selftest [--src DIR] [--flavor LABEL] [--debug | --configs rel,dbg] [--cases N]
#                       [--only NAME,NAME] [--list]
#
#   --quick / --full     20 000 random trees (<=14 entries, depth 3, <=16 steps; ~1 s, cap 60 s) /
#                        300 000 trees (<=28 entries, depth 4, <=32 steps; ~30 s, cap 600 s)
#   --src DIR            the KenseiECS package under test (default: KenseiECS/ of this repository)
#   --flavor LABEL       label of that tree for the output folders (default: wt, or the folder above --src)
#   --debug              compile the package and the stand with KENSEI_DEBUG
#   --seed / --cases     base seed (default 20261023) / number of random trees
#   --contract-version   judge the tree by the contract profile of this version instead of its package.json
#                        (for example 2.2.2 before the version is bumped)
#   --baseline LABEL     compare with Stand/out/reports/runner/LABEL-<rel|dbg>/report.json: new signatures,
#                        jumps in hits, and regressions (open behaviour that changed without breaking the contract)
#   --baseline-src DIR   accepted like the other modules take it; the comparison reads the baseline report only
#   --report-dir DIR     write the report to DIR instead of Stand/out/reports/runner/<flavor>-<rel|dbg>/
#   --no-verify          skip compiling and running the generated repros
#   --fail-on-findings   exit 1 when the report has defects (for CI)
#   --replay FILE        run one saved case (cases/*.json of a report) and print what it violates
#   --gen-seed N         regenerate the random tree of seed N (the "seed" of a signature in report.json, with the
#                        --quick/--full parameters it names), print what it violates and the shrunk case
#   --selftest           mutation self-test: each patch in Stand/runner/mutants/ is applied to a temporary copy of
#                        the tree (default: KenseiECS/ of this repository) and the module must catch it; see
#                        Stand/runner/README.md. Report: Stand/out/reports/runner/selftest-<flavor>-<rel|dbg>/
#
# Report: Stand/out/reports/runner/<flavor>-<rel|dbg>/report.md and report.json, repros in repro/, shrunk cases
# in cases/. Builds go to Stand/out/bin|obj/<flavor>-<rel|dbg>/ (see ../Directory.Build.props).
#
# .NET: $DOTNET, else $DOTNET_ROOT/dotnet, else dotnet on PATH, else a .NET 8 SDK that is downloaded once into
# Stand/out/tools/dotnet-sdk/ with the official dotnet-install script.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAND="$(cd "$HERE/.." && pwd)"

for arg in "$@"; do
    if [[ "$arg" == "--selftest" ]]; then
        args=()
        for a in "$@"; do [[ "$a" == "--selftest" ]] || args+=("$a"); done
        PY="$(command -v python3 || command -v python || true)"
        [[ -n "$PY" ]] || { echo "runner/run.sh: --selftest needs Python 3" >&2; exit 69; }
        exec "$PY" -I "$HERE/selftest/selftest.py" ${args[@]+"${args[@]}"}
    fi
done

MODE=--quick
SRC=""
FLAVOR=""
DEBUG=false
SEED=""
CASES=""
BUDGET=""
VERIFY=true
FAIL_ON_FINDINGS=false
REPLAY=""
GEN_SEED=""
CONTRACT_VERSION=""
BASELINE=""
BASELINE_REPORT=""
REPORT_DIR=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quick|--full) MODE="$1"; shift ;;
        --src) SRC="$2"; shift 2 ;;
        --flavor) FLAVOR="$2"; shift 2 ;;
        --debug) DEBUG=true; shift ;;
        --seed) SEED="$2"; shift 2 ;;
        --cases) CASES="$2"; shift 2 ;;
        --time-budget) BUDGET="$2"; shift 2 ;;
        --no-verify) VERIFY=false; shift ;;
        --fail-on-findings) FAIL_ON_FINDINGS=true; shift ;;
        --replay) REPLAY="$2"; shift 2 ;;
        --gen-seed) GEN_SEED="$2"; shift 2 ;;
        --contract-version) CONTRACT_VERSION="$2"; shift 2 ;;
        --baseline) BASELINE="$2"; shift 2 ;;
        --baseline-src) shift 2 ;;
        --baseline-report) BASELINE_REPORT="$2"; shift 2 ;;
        --report-dir) REPORT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,37p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "runner/run.sh: unknown argument $1" >&2; exit 64 ;;
    esac
done

if [[ -z "$SRC" ]]; then
    SRC="$STAND/../KenseiECS"
    [[ -z "$FLAVOR" ]] && FLAVOR=wt
fi
[[ -d "$SRC" ]] || { echo "runner/run.sh: no such folder: $SRC" >&2; exit 66; }
SRC="$(cd "$SRC" && pwd)"
if [[ -z "$FLAVOR" ]]; then
    FLAVOR="$(basename "$(dirname "$SRC")")"
fi
GEN_SRC="$(dirname "$SRC")/KenseiECS.Generators"
if [[ ! -f "$SRC/Systems/SystemsRunner.cs" ]]; then
    echo "runner/run.sh: $SRC is not a KenseiECS package folder (no Systems/SystemsRunner.cs)" >&2
    exit 66
fi
PKG_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SRC/package.json" | head -n 1)"
CFG=rel
$DEBUG && CFG=dbg

# ------------------------------------------------------------------ .NET

case "$(uname -s 2>/dev/null || echo unknown)" in
    MINGW*|MSYS*|CYGWIN*) OS_KIND=windows ;;
    *) OS_KIND=unix ;;
esac

resolve_dotnet() {
    if [[ -n "${DOTNET:-}" ]]; then
        return 0
    fi
    if [[ -n "${DOTNET_ROOT:-}" ]]; then
        for exe in "$DOTNET_ROOT/dotnet" "$DOTNET_ROOT/dotnet.exe"; do
            if [[ -x "$exe" ]]; then DOTNET="$exe"; return 0; fi
        done
    fi
    if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q .; then
        DOTNET=dotnet
        return 0
    fi
    local dir="$STAND/out/tools/dotnet-sdk"
    local exe="$dir/dotnet"
    [[ "$OS_KIND" == windows ]] && exe="$dir/dotnet.exe"
    if [[ ! -x "$exe" ]]; then
        echo "runner: no .NET SDK found; downloading the .NET 8 SDK into $dir" >&2
        mkdir -p "$dir"
        if [[ "$OS_KIND" == windows ]]; then
            local wdir="$dir"
            command -v cygpath >/dev/null 2>&1 && wdir="$(cygpath -w "$dir")"
            powershell -NoProfile -ExecutionPolicy Bypass -Command \
                "\$ErrorActionPreference='Stop'; Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile '$wdir\\dotnet-install.ps1'; & '$wdir\\dotnet-install.ps1' -Channel 8.0 -InstallDir '$wdir' -NoPath" >&2
        else
            if command -v curl >/dev/null 2>&1; then
                curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dir/dotnet-install.sh"
            else
                wget -q -O "$dir/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
            fi
            bash "$dir/dotnet-install.sh" --channel 8.0 --install-dir "$dir" --no-path >&2
        fi
        [[ -x "$exe" ]] || { echo "runner/run.sh: the .NET SDK download into $dir failed" >&2; exit 69; }
    fi
    export DOTNET_ROOT="$dir"
    DOTNET="$exe"
}

resolve_dotnet
export DOTNET_ROLL_FORWARD=Major
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PROPS=(-p:KenseiSrc="$SRC" -p:KenseiGenSrc="$GEN_SRC" -p:StandFlavor="$FLAVOR")
$DEBUG && PROPS+=(-p:KenseiDebug=true)

OUT="${REPORT_DIR:-$STAND/out/reports/runner/$FLAVOR-$CFG}"
BIN="$STAND/out/bin/$FLAVOR-$CFG/Stand.Runner/Release/net8.0/Stand.Runner.dll"

echo "runner: building against $SRC ($FLAVOR, $CFG, package $PKG_VERSION${CONTRACT_VERSION:+, contract $CONTRACT_VERSION})"
BUILD_LOG="$STAND/out/logs/runner/build-$FLAVOR-$CFG.log"
mkdir -p "$(dirname "$BUILD_LOG")"
"$DOTNET" build "$HERE/Stand.Runner.csproj" -c Release -nologo -v q "${PROPS[@]}" > "$BUILD_LOG" 2>&1 || {
    cat "$BUILD_LOG" >&2
    echo "runner/run.sh: build failed (log: $BUILD_LOG)" >&2
    exit 70
}

ARGS=(--flavor "$FLAVOR" --src "$SRC" --pkg-version "$PKG_VERSION")
$DEBUG && ARGS+=(--debug)
[[ -n "$CONTRACT_VERSION" ]] && ARGS+=(--contract-version "$CONTRACT_VERSION")

if [[ -n "$REPLAY" ]]; then
    exec "$DOTNET" "$BIN" "${ARGS[@]}" --replay "$REPLAY"
fi
if [[ -n "$GEN_SEED" ]]; then
    exec "$DOTNET" "$BIN" "${ARGS[@]}" "$MODE" --gen-seed "$GEN_SEED"
fi

ARGS+=("$MODE" --out "$OUT" --kit "$HERE/kit")
[[ -n "$SEED" ]] && ARGS+=(--seed "$SEED")
[[ -n "$CASES" ]] && ARGS+=(--cases "$CASES")
[[ -n "$BUDGET" ]] && ARGS+=(--time-budget "$BUDGET")
if [[ -n "$BASELINE" && -z "$BASELINE_REPORT" ]]; then
    BASELINE_REPORT="$STAND/out/reports/runner/$BASELINE-$CFG/report.json"
fi
if [[ -n "$BASELINE_REPORT" ]]; then
    if [[ -f "$BASELINE_REPORT" ]]; then
        ARGS+=(--baseline-report "$BASELINE_REPORT")
        [[ -n "$BASELINE" ]] && ARGS+=(--baseline-label "$BASELINE")
    else
        echo "runner: no baseline report $BASELINE_REPORT (run the module on the baseline first); not compared" >&2
    fi
fi
mkdir -p "$OUT"
"$DOTNET" "$BIN" "${ARGS[@]}"

if $VERIFY; then
    echo "runner: compiling and running the repros"
    # A repro that does not compile is set aside (repro/not-compiling/) and reported as such, so it does not keep the
    # other repros from being checked; up to three rounds.
    NOT_COMPILING=()
    BUILT=false
    for round in 1 2 3; do
        if "$DOTNET" build "$HERE/repro-check/Stand.Runner.ReproCheck.csproj" -c Release -nologo -v q "${PROPS[@]}" -p:ReproDir="$OUT/repro" > "$OUT/repro-check-build.log" 2>&1; then
            BUILT=true
            break
        fi
        BAD=()
        while IFS= read -r f; do
            [ -n "$f" ] && [ -f "$f" ] && BAD+=("$f")
        done < <(grep -o "$OUT/repro/[^ (]*\.cs" "$OUT/repro-check-build.log" | grep -v '/ReproKit.cs$\|/ReproUnityStubs.cs$' | sort -u)
        [ ${#BAD[@]} -gt 0 ] || break
        mkdir -p "$OUT/repro/not-compiling"
        for f in "${BAD[@]}"; do
            mv "$f" "$OUT/repro/not-compiling/"
            NOT_COMPILING+=("$(basename "$f" .cs)")
        done
        cp "$OUT/repro-check-build.log" "$OUT/repro/not-compiling/build-round-$round.log"
    done
    if $BUILT; then
        "$DOTNET" "$STAND/out/bin/$FLAVOR-$CFG/Stand.Runner.ReproCheck/Release/net8.0/Stand.Runner.ReproCheck.dll" "$OUT/repro" > "$OUT/repro-check.log" 2>&1 || true
        for name in ${NOT_COMPILING[@]+"${NOT_COMPILING[@]}"}; do
            printf 'ERROR Repro_%s: the repro does not compile (repro/not-compiling/%s.cs, see the build-round logs there)\n' "$(printf '%s' "$name" | tr -c 'A-Za-z0-9\n' '_')" "$name" >> "$OUT/repro/verify.txt"
        done
        [ ${#NOT_COMPILING[@]} -eq 0 ] || echo "runner: ${#NOT_COMPILING[@]} repro(s) do not compile: ${NOT_COMPILING[*]}" >&2
    else
        echo "runner: repro-check build failed, see $OUT/repro-check-build.log" >&2
        printf 'ERROR build: repro-check did not compile (see repro-check-build.log)\n' > "$OUT/repro/verify.txt"
    fi
    "$DOTNET" "$BIN" --finalize "$OUT"
    REPRODUCED="$(grep -c '^REPRODUCED' "$OUT/repro/verify.txt" || true)"
    TOTAL="$(wc -l < "$OUT/repro/verify.txt" | tr -d ' ')"
    echo "runner: $REPRODUCED of $TOTAL repros compile and reproduce"
fi

echo "runner: report $OUT/report.md"
if $FAIL_ON_FINDINGS && ! grep -q '"findingCount": 0,' "$OUT/report.json"; then
    exit 1
fi
exit 0
