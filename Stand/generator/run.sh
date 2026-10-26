#!/usr/bin/env bash
# Stand module "generator": systematic and randomized tester of the KenseiECS source generator
# (KenseiECS.Generators). Builds stand programs (system hierarchies over generated, hand-written,
# cross-assembly and generic bases, composite interfaces, OnInit hooks, nesting, keyword and
# case-only names, partial parts), runs the generator of the tree through CSharpGeneratorDriver,
# compiles, emits into memory, loads into a collectible AssemblyLoadContext and calls
# SystemsRunner.Init; the oracle checks diagnostics, generated code, the injected fields and their
# values against a versioned model of the documented contract. Every finding gets a minimized,
# re-checked repro. Cross-version rules compare with the digests of other trees: migration pairs
# against the 2.1 line (the documented upgrade must keep the order), lost compiler signals and
# regressions against the baseline.
#
# Usage:
#   Stand/generator/run.sh [--quick|--full] [--src DIR] [--gensrc DIR] [--flavor NAME] [--debug]
#                          [--seed N] [--iterations N] [--jobs N] [--budget N] [--profile auto|2.1|2.2] [--roslyn VER]
#                          [--baseline LABEL] [--baseline-src DIR] [--reference-src DIR] [--no-xversion]
#                          [--replay FLAVOR-CFG]... [--compare | --compare-only] [--show PREFIX] [--report-dir DIR]
#   Stand/generator/run.sh --selftest [--src DIR] [--debug] [--only a,b] [--keep] [--iterations N]
#
#   --src DIR          package sources (a KenseiECS folder); default: this working copy
#   --gensrc DIR       generator sources; default: the KenseiECS.Generators folder next to --src
#   --flavor NAME      label of that tree (wt, v2.2.0, ...); default: wt for the working copy,
#                      otherwise the name of the folder above --src
#   --debug            build the runtime with KENSEI_DEBUG (the stand programs get the define too)
#   --quick / --full   quick: catalog + 600 random programs, minimization budget 160 runs per
#                      finding; full: catalog + 6000 random programs, budget 400
#   --seed N           first fuzz seed (default 20261023); program i uses seed N+i
#   --iterations N     number of random programs (overrides --quick/--full)
#   --profile P        contract model: auto (2.1 for 2.1.x packages, otherwise 2.2), 2.1 or 2.2
#   --roslyn VER       Roslyn of the generator driver and the compiler (default 4.8.0; 3.8.0 is the generation
#                      Unity 2021.3 compiles with); the report label gets "-roslyn<VER>"
#   --baseline LABEL   the baseline tree (Stand/run.sh --baseline): its report
#                      Stand/out/reports/generator/LABEL-<cfg> is compared by signature and by program, and
#                      its sources are observed for the cross-version rules
#   --baseline-src DIR its package folder (default: Stand/out/src/LABEL/KenseiECS; wt: this working copy)
#   --reference-src DIR the 2.1-line tree of the migration pairs (default: the newest Stand/out/src/v2.1.*/,
#                      extracted from the git repository of this working copy with `git archive` if missing)
#   --no-xversion      no cross-version rules (no other tree is built or observed)
#   --replay F-C       also replay the findings of Stand/out/reports/generator/F-C on this tree and
#                      say for each whether it is a regression (repeatable)
#   --compare          afterwards write Stand/out/reports/generator/compare.md over every report there
#   --compare-only     only write that comparison (no run; builds the stand for the working copy)
#   --show PREFIX      debug: print what the pipeline and the oracle see for catalog layouts with this
#                      id prefix (or fuzz:SEED, or a model .json); no report
#   --report-dir DIR   write the report there instead of Stand/out/reports/generator/<flavor>-<cfg>
#   --selftest         mutation self-test (selftest.py): the patches in Stand/generator/mutants/ are applied
#                      to a temporary copy of the tree, each must be caught; report in
#                      Stand/out/reports/generator/selftest-<flavor>-<cfg>/
#
# .NET: `dotnet` from PATH (or $DOTNET, or $DOTNET_ROOT/dotnet) with any SDK 8+; without one the .NET 8 SDK
# is installed into Stand/out/tools/dotnet-sdk/ with the official dotnet-install script.
#
# Output: Stand/out/reports/generator/<flavor>-<rel|dbg>/{report.md,report.json,repro/,build.log}
#         repro/<id>.cs (clean C# with a header: finding, seed and params, expected ReproMain output),
#         repro/<id>.model.json (replayable), repro/<id>.generated.cs, repro/<id>.observed.txt;
#         xobs/<label>.json: digests of the other trees
#
# Examples:
#   Stand/generator/run.sh --quick --src Stand/out/src/v2.2.0/KenseiECS
#   Stand/generator/run.sh --quick --baseline v2.2.0          (working copy against 2.2.0)
#   Stand/generator/run.sh --selftest
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAND="$(cd "$HERE/.." && pwd)"
WT="$(cd "$STAND/.." && pwd)"

for a in "$@"; do
    if [ "$a" = --selftest ]; then
        args=()
        for b in "$@"; do [ "$b" = --selftest ] || args+=("$b"); done
        exec python3 -I "$HERE/selftest.py" ${args[@]+"${args[@]}"}
    fi
done

MODE=quick
SRC=""
GENSRC=""
FLAVOR=""
DEBUG=false
ROSLYN=""
COMPARE=false
COMPARE_ONLY=false
SHOW=false
BASELINE=""
BASELINE_SRC=""
REFERENCE_SRC=""
XVERSION=true
REPORT_DIR=""
EXTRA=()
OBSERVE_EXTRA=()

while [ $# -gt 0 ]; do
    case "$1" in
        --quick) MODE=quick ;;
        --full) MODE=full ;;
        --src) SRC="$2"; shift ;;
        --gensrc) GENSRC="$2"; shift ;;
        --flavor) FLAVOR="$2"; shift ;;
        --debug) DEBUG=true ;;
        --roslyn) ROSLYN="$2"; shift ;;
        --seed|--iterations|--jobs) EXTRA+=("$1" "$2"); OBSERVE_EXTRA+=("$1" "$2"); shift ;;
        --budget|--profile) EXTRA+=("$1" "$2"); shift ;;
        --replay) EXTRA+=(--replay "$STAND/out/reports/generator/$2"); shift ;;
        --show) EXTRA+=(--show "$2"); SHOW=true; shift ;;
        --baseline) BASELINE="$2"; shift ;;
        --baseline-src) BASELINE_SRC="$2"; shift ;;
        --reference-src) REFERENCE_SRC="$2"; shift ;;
        --no-xversion) XVERSION=false ;;
        --report-dir) REPORT_DIR="$2"; shift ;;
        --compare) COMPARE=true ;;
        --compare-only) COMPARE=true; COMPARE_ONLY=true ;;
        -h|--help) sed -n '2,61p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "generator: unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

# ------------------------------------------------------------------ .NET

export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

has_sdk() { [ -n "$1" ] && [ -x "$1" ] && "$1" --list-sdks 2>/dev/null | grep -q .; }

resolve_dotnet() {
    local d tools="$STAND/out/tools/dotnet-sdk" script="$STAND/out/tools/dotnet-install.sh"
    for d in "${DOTNET:-}" "$(command -v dotnet 2>/dev/null || true)" "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" "$tools/dotnet"; do
        if has_sdk "$d"; then DOTNET="$d"; return 0; fi
    done
    echo "generator: no .NET SDK found; installing the .NET 8 SDK into ${tools#"$WT/"}" >&2
    mkdir -p "$STAND/out/tools"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
    else
        wget -q https://dot.net/v1/dotnet-install.sh -O "$script"
    fi
    bash "$script" --channel 8.0 --install-dir "$tools" --no-path >&2
    has_sdk "$tools/dotnet" || { echo "generator: the .NET 8 SDK could not be installed into $tools" >&2; exit 1; }
    DOTNET="$tools/dotnet"
    export DOTNET_ROOT="$tools"
}
resolve_dotnet

# ------------------------------------------------------------------ trees

pkg_version() { sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1/package.json" 2>/dev/null | head -n 1; }

[ -n "$SRC" ] || SRC="$WT/KenseiECS"
SRC="$(cd "$SRC" && pwd)"
[ -n "$GENSRC" ] || GENSRC="$(dirname "$SRC")/KenseiECS.Generators"
GENSRC="$(cd "$GENSRC" && pwd)"
if [ -z "$FLAVOR" ]; then
    if [ "$SRC" = "$WT/KenseiECS" ]; then FLAVOR=wt; else FLAVOR="$(basename "$(dirname "$SRC")")"; fi
fi
if [ -n "$ROSLYN" ]; then FLAVOR="$FLAVOR-roslyn$ROSLYN"; fi
CFG=rel
if [ "$DEBUG" = true ]; then CFG=dbg; fi
REPORTS="$STAND/out/reports/generator"
OUT="${REPORT_DIR:-$REPORTS/$FLAVOR-$CFG}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
BUILDLOG="$OUT/build.log"
if [ "$COMPARE_ONLY" = true ]; then BUILDLOG="$REPORTS/compare-build.log"; fi
rel() { case "$1" in "$WT"/*) echo "${1#"$WT/"}" ;; *) echo "$1" ;; esac; }

# build <flavor> <src> <gensrc> <log>: builds the stand against that tree; prints the folder of GenStand.dll.
build() {
    local props=(-p:KenseiSrc="$2" -p:KenseiGenSrc="$3" -p:StandFlavor="$1" -p:KenseiDebug="$DEBUG")
    if [ -n "$ROSLYN" ]; then props+=(-p:KenseiDriverRoslyn="$ROSLYN"); fi
    "$DOTNET" build "$HERE/src/GenStand/GenStand.csproj" -c Release -nologo -v:q "${props[@]}" > "$4" 2>&1 || return 1
    echo "$STAND/out/bin/$1-$CFG/GenStand/Release/net8.0"
}

echo "generator: building against $(rel "$SRC") and $(rel "$GENSRC") ($FLAVOR-$CFG)"
BIN=$(build "$FLAVOR" "$SRC" "$GENSRC" "$BUILDLOG") || { cat "$BUILDLOG"; exit 1; }

ARGS=(--src "$SRC" --gensrc "$GENSRC" --flavor "$FLAVOR" --mode "$MODE" --root "$WT")
if [ "$DEBUG" = true ]; then ARGS+=(--debug); fi
if [ ${#EXTRA[@]} -gt 0 ]; then ARGS+=("${EXTRA[@]}"); fi
if [ "$SHOW" = true ]; then
    "$DOTNET" "$BIN/GenStand.dll" "${ARGS[@]}"
    exit 0
fi

# extract_tag <tag>: the package folder of a release tag under Stand/out/src/<tag>/, extracted read-only from the
# git repository of this working copy (git archive) when it is not there yet. Prints nothing on failure.
extract_tag() {
    local tag=$1 dest="$STAND/out/src/$1" tmp paths=() p
    if [ -f "$dest/KenseiECS/package.json" ]; then echo "$dest/KenseiECS"; return 0; fi
    git -C "$WT" log -1 --format=%H "refs/tags/$tag" -- >/dev/null 2>&1 || return 0
    tmp="$STAND/out/src/.tmp-$tag-$$"
    rm -rf "$tmp"
    mkdir -p "$tmp"
    for p in KenseiECS KenseiECS.Generators docs; do
        if git -C "$WT" show "$tag:$p" >/dev/null 2>&1; then paths+=("$p"); fi
    done
    if git -C "$WT" archive --format=tar "$tag" "${paths[@]}" | tar -x -C "$tmp"; then
        printf '{"ref": "%s", "commit": "%s", "extracted": "%s", "paths": "%s"}\n' "$tag" "$(git -C "$WT" log -1 --format=%H "refs/tags/$tag" --)" \
            "$(date +%Y-%m-%dT%H:%M:%S%z)" "${paths[*]}" > "$tmp/.stand-ref"
        chmod -R a-w "$tmp"/* "$tmp/.stand-ref"
        if [ -d "$dest" ]; then chmod -R u+w "$tmp"; rm -rf "$tmp"; else mv "$tmp" "$dest" && chmod a-w "$dest"; fi
        [ -f "$dest/KenseiECS/package.json" ] && echo "$dest/KenseiECS"
    else
        chmod -R u+w "$tmp" 2>/dev/null; rm -rf "$tmp"
    fi
    return 0
}

# reference_tree: the newest tree of the previous minor line (2.1.x for a 2.2.x package): extracted ones first, then tags.
# Only from 2.2 on, whose documents have the upgrade note the migration pairs check ("Upgrading from 2.1").
reference_tree() {
    local v major minor line best="" bestv="" d dv tag
    v=$(pkg_version "$SRC")
    major=${v%%.*}; minor=${v#*.}; minor=${minor%%.*}
    case "$major$minor" in ''|*[!0-9]*) return 0 ;; esac
    [ "$minor" -gt 0 ] || return 0
    [ "$major" -gt 2 ] || [ "$minor" -ge 2 ] || return 0
    line="$major.$((minor - 1))"
    for d in "$STAND"/out/src/v"$line".*/KenseiECS; do
        [ -f "$d/package.json" ] || continue
        dv=$(pkg_version "$d")
        if [ -z "$bestv" ] || [ "$(printf '%s\n%s\n' "$bestv" "$dv" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)" = "$dv" ]; then best="$d"; bestv="$dv"; fi
    done
    if [ -n "$best" ]; then echo "$best"; return 0; fi
    tag=$(git -C "$WT" log --no-walk --tags --format=%D 2>/dev/null | tr ',' '\n' | sed -n "s/^ *tag: \(v$line\.[0-9][0-9]*\)$/\1/p" | sort -t. -k3,3n | tail -n 1)
    [ -n "$tag" ] && extract_tag "$tag"
    return 0
}

# observe <label> <src>: builds the stand against that tree and writes the digests of the same programs; prints the file.
observe() {
    local label=$1 src=$2 gensrc bin file="$OUT/xobs/$1.json" log="$OUT/xobs/$1-build.log"
    gensrc="$(dirname "$src")/KenseiECS.Generators"
    mkdir -p "$OUT/xobs"
    if ! bin=$(build "$label" "$src" "$gensrc" "$log"); then
        echo "generator: the stand does not build against $(rel "$src") (see $(rel "$log")); no cross-version rules against it" >&2
        return 0
    fi
    local args=(--observe "$file" --src "$src" --gensrc "$gensrc" --flavor "$label" --mode "$MODE" --root "$WT")
    if [ "$DEBUG" = true ]; then args+=(--debug); fi
    if [ ${#OBSERVE_EXTRA[@]} -gt 0 ]; then args+=("${OBSERVE_EXTRA[@]}"); fi
    if "$DOTNET" "$bin/GenStand.dll" "${args[@]}" >&2; then echo "$file"; fi
    return 0
}

XOBS=()
if [ "$COMPARE_ONLY" = false ] && [ "$XVERSION" = true ]; then
    seen="$SRC"
    REF="$REFERENCE_SRC"
    [ -n "$REF" ] || REF=$(reference_tree)
    if [ -n "$REF" ]; then
        REF="$(cd "$REF" && pwd)"
        if [ "$REF" != "$SRC" ]; then
            f=$(observe "$(basename "$(dirname "$REF")")" "$REF")
            [ -n "$f" ] && XOBS+=(--xobs "$f")
            seen="$seen $REF"
        fi
    else
        echo "generator: no tree of the previous minor line found; no migration pairs" >&2
    fi
    if [ -n "$BASELINE" ]; then
        BSRC="$BASELINE_SRC"
        if [ -z "$BSRC" ]; then
            if [ "$BASELINE" = wt ]; then BSRC="$WT/KenseiECS"; else BSRC="$STAND/out/src/$BASELINE/KenseiECS"; fi
        fi
        if [ -f "$BSRC/package.json" ]; then
            BSRC="$(cd "$BSRC" && pwd)"
            case " $seen " in
                *" $BSRC "*) ;;
                *) f=$(observe "$BASELINE" "$BSRC"); [ -n "$f" ] && XOBS+=(--xobs "$f") ;;
            esac
        else
            echo "generator: baseline sources $BSRC not found; only its report is compared" >&2
        fi
    fi
fi
if [ -n "$BASELINE" ] && [ -f "$REPORTS/$BASELINE-$CFG/report.json" ]; then
    XOBS+=(--baseline-report "$REPORTS/$BASELINE-$CFG")
fi

if [ "$COMPARE_ONLY" = false ]; then
    "$DOTNET" "$BIN/GenStand.dll" --out "$OUT" "${ARGS[@]}" ${XOBS[@]+"${XOBS[@]}"}
fi

if [ "$COMPARE" = true ]; then
    CMP=()
    for d in "$REPORTS"/*/; do
        if [ -f "$d/report.json" ]; then CMP+=(--compare "${d%/}"); fi
    done
    "$DOTNET" "$BIN/GenStand.dll" --out "$REPORTS" --root "$WT" "${CMP[@]}"
fi
