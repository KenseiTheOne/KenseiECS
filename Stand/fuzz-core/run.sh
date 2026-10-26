#!/usr/bin/env bash
# Stand module "fuzz-core": randomized, model-based tester of the KenseiECS core (World, pools, filters,
# listeners, CommandBuffer, EventBuffer, OneFrame cleanup, Warmup). See README.md next to this script.
#
#   Stand/fuzz-core/run.sh [--quick | --full [--minutes N]] [--src <KenseiECS folder>] [--flavor <label>] [--debug]
#                          [--scenarios N | --timed] [--seed N] [--ops N] [--workers N] [--exclude-families W1,W2,...|all]
#                          [--baseline <label>] [--no-verify] [--save-corpus] [--fail-on-findings]
#   Stand/fuzz-core/run.sh --replay <case.json> [--layout narrow|wideA|wideB] [--mode compare|crash] [--reading Q-id]
#                          [--src ...] [--flavor ...] [--debug]
#   Stand/fuzz-core/run.sh --selftest [--src ...] [--flavor ...] [--debug] [--scenarios N] [--only name,name]
#
#   --quick            corpus + 30000 scenarios (a fixed seed set) + shrinking, repros and their check (about 2 minutes)
#   --full             corpus + N minutes (default 10) of random scenarios, larger shrink budgets
#   --scenarios N      run exactly N scenarios (the same seeds every run, so two trees compare seed by seed)
#   --timed            --quick without a fixed seed set: explore for about a minute instead
#   --src              package sources under test (default: KenseiECS of this working copy)
#   --flavor           label of that tree for output folders (default: wt, or the folder above --src)
#   --debug            compile package and stand with KENSEI_DEBUG
#   --seed / --ops     base seed (default 20261023) / top-level operations per random scenario (default 60)
#   --workers          worker processes (default: CPU count - 1, at most 8)
#   --exclude-families the generator avoids the known triggers of these families (all: every known one), so a
#                      saturated search looks for the rest
#   --baseline LABEL   compare with Stand/out/reports/fuzz-core/<LABEL>-<rel|dbg>/ (new signatures, new signatures
#                      inside known families, sharp growth, and seed by seed when both used the same fixed seed set)
#   --no-verify        do not compile and run the generated NUnit repros
#   --save-corpus      copy minimised scenarios of new defect signatures into corpus/
#   --fail-on-findings exit 1 when the report has defects (for CI)
#   --replay FILE      run one saved scenario against the model and print what it violates (logs, facts, claims);
#                      --reading Q-id replays it with the alternative reading of a contract question
#   --selftest         mutation check of the module itself: applies each patch of mutants/ to a copy of the tree and
#                      checks that the module reports it as a new signature with a repro (selftest.py)
#
# Report: Stand/out/reports/fuzz-core/<flavor>-<rel|dbg>/report.md, report.json and outcomes.json, minimised
# scenarios in min/, NUnit repros in repro/ (checked by repro-check/), corpus candidates in corpus-candidates/.
# Builds go to Stand/out/bin|obj/<flavor>-<rel|dbg>/ (see ../Directory.Build.props).
#
# Needs a .NET SDK 8 or newer: $DOTNET, else dotnet on PATH, else $DOTNET_ROOT, Stand/out/tools/dotnet or the
# usual install folders. And python3 (3.8+).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAND="$(cd "$HERE/.." && pwd)"
T0=$(date +%s)

MODE=--quick
MINUTES=""
SRC=""
FLAVOR=""
DEBUG=false
SEED=20261023
OPS=60
WORKERS=0
SCENARIOS=""
TIMED=false
EXCLUDE=""
BASELINE=""
VERIFY=yes
SAVE_CORPUS=false
FAIL_ON_FINDINGS=false
REPLAY=""
LAYOUT=""
RMODE=""
READINGS=()
SELFTEST=false
ONLY=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quick|--full) MODE="$1"; shift ;;
        --minutes) MINUTES="$2"; shift 2 ;;
        --src) SRC="$2"; shift 2 ;;
        --flavor) FLAVOR="$2"; shift 2 ;;
        --debug) DEBUG=true; shift ;;
        --seed) SEED="$2"; shift 2 ;;
        --ops) OPS="$2"; shift 2 ;;
        --workers) WORKERS="$2"; shift 2 ;;
        --scenarios) SCENARIOS="$2"; shift 2 ;;
        --timed) TIMED=true; shift ;;
        --exclude-families) EXCLUDE="$2"; shift 2 ;;
        --baseline) BASELINE="$2"; shift 2 ;;
        --no-verify) VERIFY=no; shift ;;
        --save-corpus) SAVE_CORPUS=true; shift ;;
        --fail-on-findings) FAIL_ON_FINDINGS=true; shift ;;
        --replay) REPLAY="$2"; shift 2 ;;
        --layout) LAYOUT="$2"; shift 2 ;;
        --mode) RMODE="$2"; shift 2 ;;
        --reading) READINGS+=(--reading "$2"); shift 2 ;;
        --selftest) SELFTEST=true; shift ;;
        --only) ONLY="$2"; shift 2 ;;
        -h|--help) sed -n '2,38p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "fuzz-core/run.sh: unknown argument $1" >&2; exit 64 ;;
    esac
done

if $SELFTEST; then
    ARGS=()
    [[ -n "$SRC" ]] && ARGS+=(--src "$SRC")
    [[ -n "$FLAVOR" ]] && ARGS+=(--flavor "$FLAVOR")
    $DEBUG && ARGS+=(--debug)
    [[ -n "$SCENARIOS" ]] && ARGS+=(--scenarios "$SCENARIOS")
    [[ -n "$ONLY" ]] && ARGS+=(--only "$ONLY")
    exec python3 "$HERE/selftest.py" ${ARGS[@]+"${ARGS[@]}"}
fi

if [[ -z "$SRC" ]]; then
    SRC="$STAND/../KenseiECS"
    [[ -z "$FLAVOR" ]] && FLAVOR=wt
fi
SRC="$(cd "$SRC" && pwd)"
if [[ -z "$FLAVOR" ]]; then
    FLAVOR="$(basename "$(dirname "$SRC")")"
fi
GEN_SRC="$(dirname "$SRC")/KenseiECS.Generators"
if [[ ! -f "$SRC/Core/World.cs" ]]; then
    echo "fuzz-core/run.sh: $SRC is not a KenseiECS package folder (no Core/World.cs)" >&2
    exit 66
fi
# The contract profile: the package version of the tree (package.json) and whether its CHANGELOG starts with an
# [Unreleased] section (documents newer than the version number).
PKG_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SRC/package.json" | head -n 1)"
UNRELEASED=false
LAST_RELEASE=""
if [[ -f "$SRC/CHANGELOG.md" ]] && [[ "$(grep -m 1 '^## \[' "$SRC/CHANGELOG.md" || true)" == "## [Unreleased]"* ]]; then
    UNRELEASED=true
    # The newest released version the CHANGELOG names: a package.json newer than it is the release in the making.
    LAST_RELEASE="$(grep -m 1 '^## \[[0-9]' "$SRC/CHANGELOG.md" | sed 's/^## \[\([^]]*\)\].*/\1/' || true)"
fi
CFG=rel
$DEBUG && CFG=dbg

# .NET SDK: $DOTNET, dotnet on PATH, $DOTNET_ROOT, Stand/out/tools/dotnet, the usual install folders.
find_dotnet() {
    if [[ -n "${DOTNET:-}" ]]; then echo "$DOTNET"; return 0; fi
    if command -v dotnet >/dev/null 2>&1; then echo dotnet; return 0; fi
    local d
    for d in "${DOTNET_ROOT:-}" "$STAND/out/tools/dotnet" "${HOME:-}/.dotnet" /usr/local/share/dotnet /usr/share/dotnet /usr/lib/dotnet \
             /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec "${ProgramFiles:-}/dotnet"; do
        [[ -n "$d" && -x "$d/dotnet" ]] && { echo "$d/dotnet"; return 0; }
        [[ -n "$d" && -x "$d/dotnet.exe" ]] && { echo "$d/dotnet.exe"; return 0; }
    done
    return 1
}
if ! DOTNET="$(find_dotnet)"; then
    echo "fuzz-core/run.sh: no .NET SDK found (.NET 8 or newer). Set DOTNET or DOTNET_ROOT, put dotnet on PATH, or install" >&2
    echo "  one into the stand's tool folder: dotnet-install.sh --channel 8.0 --install-dir \"$STAND/out/tools/dotnet\"" >&2
    echo "  (https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)" >&2
    exit 69
fi
export DOTNET
export DOTNET_ROLL_FORWARD=Major
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PROPS=(-p:KenseiSrc="$SRC" -p:KenseiGenSrc="$GEN_SRC" -p:StandFlavor="$FLAVOR")
$DEBUG && PROPS+=(-p:KenseiDebug=true)

BIN="$STAND/out/bin/$FLAVOR-$CFG/FuzzCore/Release/net8.0/FuzzCore.dll"
CHECK_BIN="$STAND/out/bin/$FLAVOR-$CFG/FuzzCore.ReproCheck/Release/net8.0/FuzzCore.ReproCheck.dll"
OUT="$STAND/out/reports/fuzz-core/$FLAVOR-$CFG"
LOGDIR="$STAND/out/logs/fuzz-core"
mkdir -p "$LOGDIR"

VERSION_ARGS=(--pkg-version "$PKG_VERSION")
$UNRELEASED && VERSION_ARGS+=(--unreleased)
[[ -n "$LAST_RELEASE" ]] && VERSION_ARGS+=(--last-release "$LAST_RELEASE")

echo "fuzz-core: building against $SRC ($FLAVOR-$CFG, package $PKG_VERSION$($UNRELEASED && echo " + unreleased changes${LAST_RELEASE:+ after $LAST_RELEASE}"))"
if ! "$DOTNET" build "$HERE/FuzzCore.csproj" -c Release -nologo -v q "${PROPS[@]}" > "$LOGDIR/build-$FLAVOR-$CFG.log" 2>&1; then
    tail -n 30 "$LOGDIR/build-$FLAVOR-$CFG.log" >&2
    echo "fuzz-core/run.sh: build failed (log: $LOGDIR/build-$FLAVOR-$CFG.log)" >&2
    exit 70
fi

if [[ -n "$REPLAY" ]]; then
    REPLAY="$(cd "$(dirname "$REPLAY")" && pwd)/$(basename "$REPLAY")"
    if [[ -z "$LAYOUT" ]]; then
        LAYOUT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Layout","narrow"))' "$REPLAY")"
    fi
    ARGS=(--replay "$REPLAY" --layout "$LAYOUT" --verbose "${VERSION_ARGS[@]}")
    [[ -n "$RMODE" ]] && ARGS+=(--mode "$RMODE")
    exec "$DOTNET" "$BIN" "${ARGS[@]}" ${READINGS[@]+"${READINGS[@]}"}
fi

# --quick runs a fixed seed set by default (comparable seed by seed); --timed or --full explore for a time.
if [[ -z "$SCENARIOS" && "$MODE" == "--quick" ]] && ! $TIMED; then
    SCENARIOS=30000
fi

rm -rf "$OUT"
mkdir -p "$OUT"
SPENT=$(( $(date +%s) - T0 ))

DRIVER=(python3 "$HERE/driver.py" --bin "$BIN" --check-project "$HERE/repro-check/FuzzCore.ReproCheck.csproj" --check-bin "$CHECK_BIN"
        --flavor "$FLAVOR" --cfg "$CFG" --out "$OUT" --corpus "$HERE/corpus" --mode "${MODE#--}" --seed "$SEED" --ops "$OPS"
        --workers "$WORKERS" --src "$SRC" --pkg-version "$PKG_VERSION" --verify "$VERIFY" --spent "$SPENT")
for p in "${PROPS[@]}"; do
    DRIVER+=("--build-prop=$p")
done
$UNRELEASED && DRIVER+=(--unreleased)
[[ -n "$LAST_RELEASE" ]] && DRIVER+=(--last-release "$LAST_RELEASE")
[[ -n "$MINUTES" ]] && DRIVER+=(--minutes "$MINUTES")
[[ -n "$SCENARIOS" ]] && DRIVER+=(--scenarios "$SCENARIOS")
[[ -n "$EXCLUDE" ]] && DRIVER+=(--exclude "$EXCLUDE")
[[ -n "$BASELINE" ]] && DRIVER+=(--baseline "$BASELINE")
$SAVE_CORPUS && DRIVER+=(--save-corpus)
$FAIL_ON_FINDINGS && DRIVER+=(--fail-on-findings)

STATUS=0
"${DRIVER[@]}" || STATUS=$?
echo "fuzz-core: report $OUT/report.md ($(( $(date +%s) - T0 )) s)"
exit $STATUS
