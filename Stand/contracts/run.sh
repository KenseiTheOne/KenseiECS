#!/usr/bin/env bash
# Stand module "contracts": the lifecycle and misuse contract matrix of the public API, the state
# probe after every call, and the promises outside the matrix (Obsolete texts, StorageGrown owners,
# the FirstGrowthAfterReserve contract, Filter spans around Reserve, references kept by World.Destroy).
# A defect is only a contradiction of a promise written in the docs of the version under test.
#
# Usage:
#   Stand/contracts/run.sh [--quick|--full] [--src DIR] [--flavor NAME] [--debug]
#                          [--baseline FLAVOR] [--seed N] [--docs DIR] [--no-repro-check]
#   Stand/contracts/run.sh --selftest [--src DIR] [--flavor NAME] [--debug | --configs rel,dbg] [--only m1,m2] [--keep]
#
#   --src DIR         package sources (a KenseiECS folder); default: KenseiECS/ of this repository
#   --flavor NAME     label of that tree (wt, v2.2.0, ...); default: wt for this repository's package,
#                     otherwise the name of the folder above --src
#   --debug           build with KENSEI_DEBUG
#   --quick / --full  quick (default): -1 and int.MaxValue per int argument, one case order;
#                     full: also int.MinValue, and the whole matrix a second time in another case
#                     order (a cell whose outcome depends on the order is reported as unstable and
#                     never counts as a defect)
#   --baseline F      compare with Stand/out/reports/contracts/F-<cfg>/ (run F in the same configuration
#                     first): changed cells, defects, signatures, regressions
#   --seed N          shuffle the case order (0 keeps the sorted order); written into every finding
#   --docs DIR        docs/ of that version. Default: <src>/../docs; otherwise `git archive` of the commit in
#                     <src>/../.stand-ref or of the tag named like the flavor (read-only), cached in
#                     Stand/out/contracts-cache/docs/; otherwise none: docs-page promises then come from
#                     the version profile (the package.json version of the tree)
#   --no-repro-check  do not compile and run the generated repros
#   --selftest        mutation self-test: apply each patch of Stand/contracts/mutants/ to a temporary
#                     copy of the tree and check that the module catches it (bug) or stays quiet on an
#                     emulated fix (fix); report Stand/out/reports/contracts/selftest-<flavor>-<cfg>/
#                     (see selftest.py)
#
# Environment: DOTNET (the dotnet command; default: dotnet on PATH, else Stand/out/tools/dotnet-sdk/dotnet,
# downloaded there with the official dotnet-install script when nothing is installed), KENSEI_REPO (the git
# repository for `git archive` of docs; default: the repository this stand lives in).
#
# Output: Stand/out/reports/contracts/<flavor>-<rel|dbg>/
#   report.md          defects, regressions, contract questions, documented, known problems, claims, signatures
#   report.json        machine-readable: findings[] (defects), contractQuestions[], documented[], regressions[],
#                      signatures[], known[], claims[] (see src/PublicReport.cs)
#   matrix.md/.json    outcome class of every member x scenario cell
#   diff-vs-<F>.md/.json  against the baseline (with --baseline)
#   repro/             one compilable C# repro per defect (repro/questions/ for questions); repro-check.json
#                      says whether each one prints what the stand saw
#   model.json, cases.ndjson   the full internal model and every executed case
#
# Examples:
#   Stand/contracts/run.sh --src Stand/out/src/v2.2.0/KenseiECS --debug
#   Stand/contracts/run.sh --debug --baseline v2.2.0
#   Stand/contracts/run.sh --selftest
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAND="$(cd "$HERE/.." && pwd)"
ROOT="$(cd "$STAND/.." && pwd)"

for a in "$@"; do
    if [ "$a" = "--selftest" ]; then
        exec python3 -I "$HERE/selftest.py" "$@"
    fi
done

MODE=quick
SRC=""
FLAVOR=""
DEBUG=false
BASELINE=""
SEED=0
DOCS=""
DOCS_SOURCE=""
REPRO_CHECK=true

while [ $# -gt 0 ]; do
    case "$1" in
        --quick) MODE=quick ;;
        --full) MODE=full ;;
        --src) SRC="$2"; shift ;;
        --flavor) FLAVOR="$2"; shift ;;
        --debug) DEBUG=true ;;
        --baseline) BASELINE="$2"; shift ;;
        --seed) SEED="$2"; shift ;;
        --docs) DOCS="$2"; DOCS_SOURCE=given; shift ;;
        --no-repro-check) REPRO_CHECK=false ;;
        -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "contracts: unknown argument $1" >&2; exit 2 ;;
    esac
    shift
done

[ -n "$SRC" ] || SRC="$ROOT/KenseiECS"
SRC="$(cd "$SRC" && pwd)"
if [ ! -f "$SRC/Core/World.cs" ]; then
    echo "contracts: $SRC is not a KenseiECS package folder (no Core/World.cs)" >&2
    exit 2
fi
TREE="$(dirname "$SRC")"
GENSRC="$TREE/KenseiECS.Generators"
if [ -z "$FLAVOR" ]; then
    if [ "$SRC" = "$ROOT/KenseiECS" ]; then FLAVOR=wt; else FLAVOR="$(basename "$TREE")"; fi
fi
CFG=rel
if [ "$DEBUG" = true ]; then CFG=dbg; fi
OUT="$STAND/out/reports/contracts/$FLAVOR-$CFG"
mkdir -p "$OUT"

# The docs of the same version: a promise is held against the code only where it is written.
if [ -z "$DOCS" ]; then
    if [ -d "$TREE/docs/guides" ]; then
        DOCS="$TREE/docs"
        DOCS_SOURCE=tree
    else
        REPO="${KENSEI_REPO:-$ROOT}"
        REF=""
        if [ -f "$TREE/.stand-ref" ]; then
            REF="$(sed -n 's/.*"commit": "\([0-9a-f]*\)".*/\1/p' "$TREE/.stand-ref" | head -n 1)"
        fi
        if [ -z "$REF" ] && git -C "$REPO" rev-parse -q --verify "refs/tags/$FLAVOR" >/dev/null 2>&1; then
            REF="$FLAVOR"
        fi
        if [ -n "$REF" ]; then
            CACHE="$STAND/out/contracts-cache/docs/$(printf '%s' "$REF" | tr -c 'A-Za-z0-9._-' '_')"
            if [ ! -d "$CACHE/docs" ]; then
                mkdir -p "$CACHE"
                git -C "$REPO" archive "$REF" docs 2>/dev/null | tar -x -C "$CACHE" 2>/dev/null || true
            fi
            if [ -d "$CACHE/docs" ]; then
                DOCS="$CACHE/docs"
                DOCS_SOURCE="archive $REF"
            fi
        fi
    fi
fi
[ -n "$DOCS" ] || echo "contracts: no docs/ for $FLAVOR: docs-page promises come from the version profile of package.json"

# dotnet: $DOTNET, the one on PATH, or an SDK downloaded into Stand/out/tools/dotnet-sdk.
if [ -z "${DOTNET:-}" ]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET=dotnet
    else
        TOOLS="$STAND/out/tools/dotnet-sdk"
        if [ ! -x "$TOOLS/dotnet" ]; then
            echo "contracts: no dotnet on PATH, downloading the .NET 8 SDK into $TOOLS"
            mkdir -p "$TOOLS"
            curl -sSL https://dot.net/v1/dotnet-install.sh -o "$TOOLS/dotnet-install.sh"
            bash "$TOOLS/dotnet-install.sh" --channel 8.0 --install-dir "$TOOLS" --no-path >/dev/null
        fi
        DOTNET="$TOOLS/dotnet"
        export DOTNET_ROOT="$TOOLS"
    fi
fi
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PROPS=(-p:KenseiSrc="$SRC" -p:KenseiGenSrc="$GENSRC" -p:StandFlavor="$FLAVOR" -p:KenseiDebug="$DEBUG")
BIN="$STAND/out/bin/$FLAVOR-$CFG"

echo "contracts: building against $SRC ($FLAVOR-$CFG)"
"$DOTNET" build "$HERE/Stand.Contracts.csproj" -c Release -nologo -v:q "${PROPS[@]}" > "$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }

ARGS=(--out "$OUT" --flavor "$FLAVOR" --mode "$MODE" --seed "$SEED" --src "$SRC" --root "$ROOT")
if [ -n "$DOCS" ]; then ARGS+=(--docs "$DOCS" --docs-source "$DOCS_SOURCE"); fi
if [ -n "$BASELINE" ]; then ARGS+=(--baseline "$BASELINE"); fi
"$DOTNET" "$BIN/Stand.Contracts/Release/net8.0/Stand.Contracts.dll" "${ARGS[@]}"

if [ "$REPRO_CHECK" = true ]; then
    rm -f "$OUT/repro-check.json" "$OUT/repro-excluded.txt"
    EXCLUDE=""
    # A repro that does not compile is reported as compile-error; the others are still checked.
    for attempt in 1 2 3; do
        if "$DOTNET" build "$HERE/repro/Stand.Contracts.Repro.csproj" -c Release -nologo -v:q "${PROPS[@]}" \
            -p:ReproDir="$OUT/repro" -p:ReproExclude="$EXCLUDE" > "$OUT/repro-build.log" 2>&1; then
            # The same heap limit as the stand's workers: a huge request fails with OutOfMemoryException
            # in both instead of being satisfied by the machine's memory in one of them.
            DOTNET_GCHeapHardLimit=0x80000000 "$DOTNET" "$BIN/Stand.Contracts.Repro/Release/net8.0/Stand.Contracts.Repro.dll" "$OUT/repro-check.json"
            break
        fi
        BAD="$(grep -o "$OUT/repro/[^(]*\.cs" "$OUT/repro-build.log" | sort -u || true)"
        if [ -z "$BAD" ]; then
            echo "repro check: build failed, see $OUT/repro-build.log"
            break
        fi
        echo "$BAD" >> "$OUT/repro-excluded.txt"
        EXCLUDE="$(sort -u "$OUT/repro-excluded.txt" | paste -sd ';' -)"
        echo "repro check: $(echo "$BAD" | wc -l | tr -d ' ') repro(s) do not compile, retrying without them"
    done
fi

"$DOTNET" "$BIN/Stand.Contracts/Release/net8.0/Stand.Contracts.dll" --finalize --out "$OUT" --flavor "$FLAVOR" --root "$ROOT"
echo "report: ${OUT#$ROOT/}/report.md"
