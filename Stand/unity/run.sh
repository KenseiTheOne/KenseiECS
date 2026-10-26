#!/bin/bash
# KenseiECS test stand, module unity: the package in a scratch Unity project, in batchmode.
# Checks the build pipeline and the editor integration against the documented contract of the tree under
# test (docs/unity/debug-tools.md, docs/guides/debug-mode.md, docs/unity/entity-view.md, CHANGELOG, XML
# docs): Debug Mode toggle, release build guard, #error / check after the script compile, Development
# builds, SBP / AssetBundle / Addressables content builds, the source generator, EcsEntityView.Spawn and
# a runtime smoke test. It only finds; it fixes nothing.
#
# Usage:
#   Stand/unity/run.sh [--quick|--full] [--src <KenseiECS folder>] [--flavor <label>] [--debug]
#                      [--cases <id;id>] [--baseline <label>] [--addressables <version|none>]
#                      [--target auto|android|osx|win64|linux64] [--backend auto|il2cpp|mono]
#                      [--unity <editor>] [--unity-version <version>] [--timeout <seconds>]
#                      [--docs <docs folder>] [--no-verify-repro] [--keep-builds] [--clean] [--list]
#   Stand/unity/run.sh --selftest [--src <KenseiECS folder>] [--debug] [--only <mutant,...>]  (see lib/selftest.py)
#
#   --quick        quick tier of cases.json (default); --full: every case
#   --src          package folder under test (default: KenseiECS/ next to Stand/)
#   --flavor       label of that tree (default wt); scratch project Stand/out/unity/<flavor>/Proj-<Unity version>
#   --debug        editor baseline in Debug Mode (KENSEI_DEBUG through KenseiECS -> Debug Mode);
#                  without it Debug Mode is off. Report: Stand/out/reports/unity/<flavor>-<rel|dbg>/
#   --cases        run only these case ids (see --list)
#   --baseline     compare with the report of this label (Stand/out/reports/unity/<label>-<rel|dbg>/report.json):
#                  findings by signature (new, known, grown) and regressions (a case fine there that changed
#                  without breaking a contract)
#   --addressables com.unity.addressables version from the registry (default 2.9.1), none to leave it out
#   --target       build target of the build cases (default auto: android if its module is installed, else the
#                  standalone target of this machine). A target whose module is missing: its cases are skipped
#   --backend      scripting backend (default auto: il2cpp where installed, else mono); builds stop after the
#                  script compile
#   --unity        the Unity editor (binary, Unity.app or editor folder); default: environment UNITY, else the
#                  Unity Hub install folders (--unity-version / UNITY_VERSION, else the newest editor that has
#                  the module of the target)
#   --timeout      limit per Unity process, seconds (default 900)
#   --docs         docs/ of the tree under test, for the contract quotes of rules.json (default: docs/ next
#                  to --src, else the docs of git tag v<package version>, extracted to Stand/out/unity/docs/)
#   --no-verify-repro  do not run the C# repro of each build finding in Unity (one extra process)
#   --keep-builds  keep Stand/out/unity/<flavor>/builds (the build output of the cases)
#   --clean        delete the scratch project of the flavor first (full reimport)
#   --list         print the cases and exit
#   --selftest     mutation self-test of this module (patches in Stand/unity/mutants/); see lib/selftest.py
#
# One Unity process of this module at a time: runs wait for each other (lock in Stand/out/unity/). Unity
# processes of other projects are never touched.
# Exit code: 0 no findings, 1 findings, 2 the stand could not run.
set -u

HERE=$(cd "$(dirname "$0")" && pwd)
STAND=$(cd "$HERE/.." && pwd)
ROOT=$(cd "$STAND/.." && pwd)

if [ "${1:-}" = --selftest ]; then
    shift
    exec python3 -I "$HERE/lib/selftest.py" "$@"
fi

MODE=quick
SRC="$ROOT/KenseiECS"
FLAVOR=wt
DEBUG=0
ONLY=""
AA=2.9.1
TARGET=auto
BACKEND=auto
TIMEOUT=900
KEEP=0
CLEAN=0
LIST=0
DOCS_ARG=""
VERIFY=1
BASELINE=""
UNITY_ARG="${UNITY:-}"
UNITY_VERSION_ARG="${UNITY_VERSION:-}"

die() { echo "unity stand: $*" >&2; exit 2; }

while [ $# -gt 0 ]; do
    case "$1" in
        --quick) MODE=quick ;;
        --full) MODE=full ;;
        --src) [ $# -ge 2 ] || die "--src needs a value"; SRC=$2; shift ;;
        --flavor) [ $# -ge 2 ] || die "--flavor needs a value"; FLAVOR=$2; shift ;;
        --debug) DEBUG=1 ;;
        --cases) [ $# -ge 2 ] || die "--cases needs a value"; ONLY=$2; shift ;;
        --baseline) [ $# -ge 2 ] || die "--baseline needs a value"; BASELINE=$2; shift ;;
        --addressables) [ $# -ge 2 ] || die "--addressables needs a value"; AA=$2; shift ;;
        --target) [ $# -ge 2 ] || die "--target needs a value"; TARGET=$2; shift ;;
        --backend) [ $# -ge 2 ] || die "--backend needs a value"; BACKEND=$2; shift ;;
        --unity) [ $# -ge 2 ] || die "--unity needs a value"; UNITY_ARG=$2; shift ;;
        --unity-version) [ $# -ge 2 ] || die "--unity-version needs a value"; UNITY_VERSION_ARG=$2; shift ;;
        --timeout) [ $# -ge 2 ] || die "--timeout needs a value"; TIMEOUT=$2; shift ;;
        --docs) [ $# -ge 2 ] || die "--docs needs a value"; DOCS_ARG=$2; shift ;;
        --no-verify-repro) VERIFY=0 ;;
        --keep-builds) KEEP=1 ;;
        --clean) CLEAN=1 ;;
        --list) LIST=1 ;;
        -h|--help) sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown argument $1 (see --help)" ;;
    esac
    shift
done

if [ $LIST = 1 ]; then
    python3 -I "$HERE/lib/cases.py" "$HERE/cases.json" list "$MODE"
    exit 0
fi

case "$TARGET" in auto|android|osx|win64|linux64) ;; *) die "--target auto|android|osx|win64|linux64" ;; esac
case "$BACKEND" in auto|il2cpp|mono) ;; *) die "--backend auto|il2cpp|mono" ;; esac
case "$TIMEOUT" in ''|*[!0-9]*) die "--timeout needs seconds" ;; esac
case "$FLAVOR" in ''|*[!A-Za-z0-9._-]*) die "--flavor may only contain letters, digits, '.', '_' and '-'" ;; esac

SRC=$(cd "$SRC" 2>/dev/null && pwd) || die "--src: no such folder"
[ -f "$SRC/package.json" ] && grep -q '"com.kensei.ecs"' "$SRC/package.json" || die "--src $SRC is not the com.kensei.ecs package"
PKG_VERSION=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$SRC/package.json") || die "package.json unreadable"

# ---------------------------------------------------------------- the Unity editor and its build modules
UNITY_JSON=$(UNITY="$UNITY_ARG" UNITY_VERSION="$UNITY_VERSION_ARG" python3 -I "$HERE/lib/unity.py" find --target "$TARGET")
jget() { python3 -I -c 'import json,sys; v=json.loads(sys.argv[1]).get(sys.argv[2], ""); print(" ".join(v) if isinstance(v, list) else ("1" if v is True else ("" if v is False else v)))' "$UNITY_JSON" "$1"; }
[ -n "$(jget error)" ] && die "$(jget error)"
UNITY=$(jget path)
UNITY_VERSION=$(jget version)
UNITY_SOURCE=$(jget source)
MODULES=$(jget modules)
TARGET=$(jget target)
BUILD_TARGET=$(jget buildTarget)
TARGET_MODULE=$(jget targetModule)
TARGET_INSTALLED=$(jget targetInstalled)
HAS_IL2CPP=$(jget il2cpp)
[ -x "$UNITY" ] || die "Unity not found or not executable: $UNITY (set UNITY or --unity)"
NOTES=()
if [ "$BACKEND" = auto ]; then
    if [ -n "$HAS_IL2CPP" ]; then BACKEND=il2cpp; else BACKEND=mono; NOTES+=("no IL2CPP for target $TARGET in this editor: backend mono"); fi
fi
# Processes that only need the editor (setup, editor cases) start on the target; without its module, on the
# standalone target of this machine.
EDITOR_TARGET=$BUILD_TARGET
SKIP_TARGET_REASON=""
if [ -z "$TARGET_INSTALLED" ]; then
    SKIP_TARGET_REASON="the $TARGET_MODULE build module (target $TARGET) is not installed in Unity $UNITY_VERSION"
    EDITOR_TARGET=$(python3 -I -c 'import platform; s=platform.system(); print("OSXUniversal" if s=="Darwin" else "Win64" if s=="Windows" or s.startswith(("MINGW","MSYS","CYGWIN")) else "Linux64")')
    NOTES+=("$SKIP_TARGET_REASON: its build cases are skipped")
fi

# Docs of the tree under test (the contract quotes of rules.json): --docs, docs/ next to the package, or
# the docs of the release tag of the package version (git archive, read-only, into Stand/out/unity/docs/).
DOCS=""
DOCS_NOTE=""
if [ -n "$DOCS_ARG" ]; then
    DOCS=$(cd "$DOCS_ARG" 2>/dev/null && pwd) || die "--docs: no such folder"
    DOCS_NOTE="given with --docs"
elif [ -d "$SRC/../docs" ]; then
    DOCS=$(cd "$SRC/../docs" && pwd)
    DOCS_NOTE="docs/ next to the package"
elif git -C "$ROOT" rev-parse -q --verify "refs/tags/v$PKG_VERSION" >/dev/null 2>&1; then
    DOCS_TAG_DIR="$STAND/out/unity/docs/v$PKG_VERSION"
    if [ ! -f "$DOCS_TAG_DIR/docs/unity/debug-tools.md" ]; then
        rm -rf "$DOCS_TAG_DIR"
        mkdir -p "$DOCS_TAG_DIR"
        git -C "$ROOT" archive "v$PKG_VERSION" docs | tar -x -C "$DOCS_TAG_DIR" || echo "unity stand: could not extract the docs of v$PKG_VERSION"
    fi
    if [ -d "$DOCS_TAG_DIR/docs" ]; then
        DOCS="$DOCS_TAG_DIR/docs"
        DOCS_NOTE="docs/ of tag v$PKG_VERSION (git archive)"
    fi
fi
[ -n "$DOCS" ] || echo "unity stand: no docs/ for this tree: rules with docs quotes become questions"

CFG=rel; [ $DEBUG = 1 ] && CFG=dbg
WORK="$STAND/out/unity/$FLAVOR"
PROJ="$WORK/Proj-$UNITY_VERSION"
PKG="$WORK/pkg"
BUILDS="$WORK/builds"
REPORT="$STAND/out/reports/unity/$FLAVOR-$CFG"
RAW="$REPORT/raw"
LOCK="$STAND/out/unity/.lock"
BASELINE_REPORT=""
if [ -n "$BASELINE" ]; then
    case "$BASELINE" in *[!A-Za-z0-9._-]*) die "--baseline is a label of a report (letters, digits, '.', '_', '-')" ;; esac
    BASELINE_REPORT="$STAND/out/reports/unity/$BASELINE-$CFG/report.json"
    [ -f "$BASELINE_REPORT" ] || echo "unity stand: no report for baseline $BASELINE ($BASELINE_REPORT): run the module on it first; no comparison"
fi

SELECTION=$(python3 -I "$HERE/lib/cases.py" "$HERE/cases.json" groups "$MODE" "$ONLY") || die "case selection failed"
[ -n "$SELECTION" ] || die "no cases selected"

# ---------------------------------------------------------------- lock: one Unity of this module at a time
mkdir -p "$STAND/out/unity"
waited=0
until mkdir "$LOCK" 2>/dev/null; do
    owner=$(cat "$LOCK/pid" 2>/dev/null || echo "")
    if [ -n "$owner" ] && ! kill -0 "$owner" 2>/dev/null; then
        echo "unity stand: removing the stale lock of pid $owner"
        rm -rf "$LOCK"
        continue
    fi
    if [ $waited -ge 3600 ]; then die "another run holds $LOCK (pid $owner) for an hour"; fi
    [ $waited = 0 ] && echo "unity stand: waiting for the run of pid $owner ($LOCK)"
    sleep 10
    waited=$((waited + 10))
done
echo $$ > "$LOCK/pid"
UNITY_PID=""
cleanup() {
    if [ -n "$UNITY_PID" ] && kill -0 "$UNITY_PID" 2>/dev/null; then
        kill_group "$UNITY_PID"
    fi
    kill_leftovers
    rm -rf "$LOCK"
}
trap cleanup EXIT
trap 'exit 2' INT TERM

# Processes of this scratch project only (main editor, asset import workers): never other projects.
project_pids() {
    if command -v pgrep >/dev/null 2>&1; then
        pgrep -f -- "-[pP]rojectPath $PROJ( |\$)" 2>/dev/null
    fi
}

kill_group() {
    local pg=$1 i=0
    kill -TERM -- "-$pg" 2>/dev/null || kill -TERM "$pg" 2>/dev/null
    while [ $i -lt 20 ] && kill -0 "$pg" 2>/dev/null; do sleep 1; i=$((i + 1)); done
    kill -KILL -- "-$pg" 2>/dev/null || kill -KILL "$pg" 2>/dev/null
}

kill_leftovers() {
    local i=0 pids
    while [ $i -lt 15 ]; do
        pids=$(project_pids)
        [ -z "$pids" ] && return 0
        sleep 1
        i=$((i + 1))
    done
    echo "unity stand: stopping leftover processes of $PROJ: $(echo $pids)"
    kill -TERM $pids 2>/dev/null
    sleep 5
    pids=$(project_pids)
    [ -n "$pids" ] && kill -KILL $pids 2>/dev/null
    return 0
}

# A scratch project made before the folder carried the Unity version: keep it (its Library is the import cache).
if [ ! -d "$PROJ" ] && [ -f "$WORK/Proj/ProjectSettings/ProjectVersion.txt" ] && \
        grep -q "m_EditorVersion: $UNITY_VERSION\$" "$WORK/Proj/ProjectSettings/ProjectVersion.txt" && \
        { ! command -v pgrep >/dev/null 2>&1 || ! pgrep -f -- "-[pP]rojectPath $WORK/Proj( |\$)" >/dev/null 2>&1; }; then
    mv "$WORK/Proj" "$PROJ" && echo "unity stand: scratch project $WORK/Proj moved to $PROJ"
fi

if [ -n "$(project_pids)" ]; then
    die "a Unity process already has $PROJ open: $(echo $(project_pids))"
fi

# ---------------------------------------------------------------- scratch project
STARTED=$(date +%Y-%m-%dT%H:%M:%S)
T0=$(date +%s)
if [ $CLEAN = 1 ]; then
    echo "unity stand: --clean, deleting $PROJ"
    rm -rf "$PROJ"
fi
mkdir -p "$PKG" "$PROJ/Assets" "$PROJ/Packages" "$BUILDS"
rm -rf "$REPORT"
mkdir -p "$RAW/cases" "$RAW/groups"

# A copy of the package: Unity writes into local (file:) packages, the tree under test stays untouched.
rsync -a --delete "$SRC/" "$PKG/" || die "copy of the package failed"
chmod -R u+w "$PKG"
# Digest of the tree under test at the start (paths and contents): the working copy can change while a run goes.
SRC_DIGEST=$(python3 -I - "$PKG" <<'PY'
import hashlib, os, sys
src = sys.argv[1]
h = hashlib.sha256()
for d, dirs, files in os.walk(src):
    dirs.sort()
    for name in sorted(files):
        p = os.path.join(d, name)
        h.update(os.path.relpath(p, src).encode() + b"\0")
        with open(p, "rb") as f:
            h.update(f.read())
print(h.hexdigest()[:16])
PY
)

rsync -a --delete --exclude '*.meta' --exclude 'StandScene.unity' --exclude 'StandControl.unity' --exclude 'StandData.asset' --exclude 'Profiles' \
    "$HERE/Template/Assets/KenseiStand/" "$PROJ/Assets/KenseiStand/" || die "copy of the template failed"

python3 -I - "$HERE/Template/Packages/manifest.base.json" "$PROJ/Packages/manifest.json" "$AA" <<'PY' || die "manifest failed"
import json, sys
base, out, aa = sys.argv[1], sys.argv[2], sys.argv[3]
m = json.load(open(base))
deps = m["dependencies"]
deps["com.kensei.ecs"] = "file:../../pkg"
if aa != "none":
    deps["com.unity.addressables"] = aa
text = json.dumps(m, indent=2) + "\n"
try:
    old = open(out).read()
except OSError:
    old = None
if old != text:
    open(out, "w").write(text)
PY

echo "unity stand: $FLAVOR-$CFG, $MODE, package $PKG_VERSION from $SRC"
echo "unity stand: Unity $UNITY_VERSION ($UNITY_SOURCE: $UNITY), target $BUILD_TARGET$([ -n "$SKIP_TARGET_REASON" ] && echo " (module missing)"), backend $BACKEND, Addressables $AA"
echo "unity stand: build modules installed: $MODULES; project $PROJ"
for n in "${NOTES[@]+"${NOTES[@]}"}"; do echo "unity stand: note: $n"; done

# ---------------------------------------------------------------- Unity runs
RUNS="$RAW/runs.jsonl"
: > "$RUNS"
MISSING_REASONS="$RAW/missing.tsv"
: > "$MISSING_REASONS"

# run_unity <name> <args...>: one Unity process in its own process group, with the timeout.
run_unity() {
    local name=$1
    shift
    local log="$RAW/unity-$name.log" start code timed_out=0
    rm -f "$log"
    start=$(date +%s)
    perl -e 'eval { setpgrp(0, 0) }; exec @ARGV or die "exec failed: $!\n"' "$UNITY" -batchmode -nographics -quit -ignorecompilererrors \
        -projectPath "$PROJ" -buildTarget "${RUN_TARGET:-$EDITOR_TARGET}" -logFile "$log" \
        -standOut "$RAW" -standBuilds "$BUILDS" -standBaseline "$CFG" -standBackend "$BACKEND" \
        -standCasesFile "$HERE/cases.json" -standTier "$MODE" "$@" &
    UNITY_PID=$!
    while kill -0 "$UNITY_PID" 2>/dev/null; do
        if [ $(( $(date +%s) - start )) -ge "$TIMEOUT" ]; then
            timed_out=1
            echo "unity stand: $name: timeout after ${TIMEOUT}s, stopping Unity"
            kill_group "$UNITY_PID"
            break
        fi
        sleep 2
    done
    wait "$UNITY_PID" 2>/dev/null
    code=$?
    UNITY_PID=""
    kill_leftovers
    local secs=$(( $(date +%s) - start ))
    printf '{"name": "%s", "exit": %d, "seconds": %d, "timedOut": %s, "log": "raw/unity-%s.log"}\n' \
        "$name" "$code" "$secs" "$([ $timed_out = 1 ] && echo true || echo false)" "$name" >> "$RUNS"
    grep -h "\[KENSEI-STAND\] END case" "$log" 2>/dev/null | sed 's/^.*\[KENSEI-STAND\] /  /'
    echo "unity stand: $name: exit $code, ${secs}s$([ $timed_out = 1 ] && echo ', TIMEOUT')"
    LAST_EXIT=$code
    LAST_TIMEOUT=$timed_out
    return 0
}

setup() {
    run_unity "setup${1:-}" -executeMethod KenseiStand.Editor.StandRunner.Setup
    if [ ! -f "$RAW/setup.json" ]; then
        echo "unity stand: setup wrote no setup.json, see $RAW/unity-setup${1:-}.log"
        return 1
    fi
    return 0
}

# Static checks of the package tree: no Unity.
STATIC_IDS=$(echo "$SELECTION" | awk '$1 == "static" { print $2 }')
if [ -n "$STATIC_IDS" ]; then
    python3 -I "$HERE/lib/static_checks.py" "$SRC" "$RAW" "$STATIC_IDS"
fi
UNITY_SELECTION=$(echo "$SELECTION" | awk '$1 != "static"')
# Build cases of a target whose module is not installed: skipped, not failed.
if [ -n "$SKIP_TARGET_REASON" ]; then
    SKIP_IDS=$(echo "$UNITY_SELECTION" | awk '$1 == "player" || $1 == "sbp" || $1 == "aa" { print $2 }' | paste -sd ';' -)
    [ -n "$SKIP_IDS" ] && python3 -I "$HERE/lib/cases.py" "$HERE/cases.json" skip "$RAW" "$SKIP_IDS" "$SKIP_TARGET_REASON"
    UNITY_SELECTION=$(echo "$UNITY_SELECTION" | awk '$1 != "player" && $1 != "sbp" && $1 != "aa"')
fi

SETUP_OK=1
if [ -n "$UNITY_SELECTION" ]; then
    setup "" || SETUP_OK=0
fi
if [ $SETUP_OK = 1 ] && [ -n "$UNITY_SELECTION" ]; then
    while read -r group ids; do
        [ -n "$group" ] || continue
        # The run group builds and starts macOS players: its process starts on that target.
        RUN_TARGET=""
        [ "$group" = run ] && RUN_TARGET=OSXUniversal
        remaining=$ids
        attempt=1
        while [ -n "$remaining" ]; do
            run_unity "$group$([ $attempt -gt 1 ] && echo "-$attempt")" -executeMethod KenseiStand.Editor.StandRunner.RunGroup \
                -standGroup "$group" -standOnly "$remaining"
            # Cases with no result: the process crashed or timed out. The case that was running gets the
            # blame, the others run again in a new process (after a new setup, the project may be dirty).
            left=""
            hung=""
            log="$RAW/unity-$group$([ $attempt -gt 1 ] && echo "-$attempt").log"
            for id in $(echo "$remaining" | tr ';' ' '); do
                [ -f "$RAW/cases/$id.json" ] && continue
                if [ -z "$hung" ] && grep -q "\[KENSEI-STAND\] BEGIN case $id " "$log" 2>/dev/null; then
                    hung=$id
                    printf '%s\t%s\n' "$id" "$([ "$LAST_TIMEOUT" = 1 ] && echo "the Unity process timed out (${TIMEOUT} s) during the case" || echo "the Unity process ended (exit $LAST_EXIT) during the case"); log raw/$(basename "$log")" >> "$MISSING_REASONS"
                    continue
                fi
                left="$left${left:+;}$id"
            done
            if [ -z "$left" ]; then
                break
            fi
            if [ -z "$hung" ] || [ $attempt -ge 3 ]; then
                for id in $(echo "$left" | tr ';' ' '); do
                    printf '%s\t%s\n' "$id" "the Unity process did not get to the case (exit $LAST_EXIT); log raw/$(basename "$log")" >> "$MISSING_REASONS"
                done
                break
            fi
            attempt=$((attempt + 1))
            setup "-$group-$attempt" || break
            remaining=$left
        done
    done <<< "$UNITY_SELECTION"
    RUN_TARGET=""
fi

# ---------------------------------------------------------------- report
write_meta() {
    FINISHED=$(date +%Y-%m-%dT%H:%M:%S)
    DURATION=$(( $(date +%s) - T0 ))
    NOTES_TEXT=$(printf '%s\n' "${NOTES[@]+"${NOTES[@]}"}")
    python3 -I - "$RAW/meta.json" "$RUNS" "$MISSING_REASONS" <<PY || die "meta failed"
import json, sys
out, runs, missing = sys.argv[1], sys.argv[2], sys.argv[3]
sel = """$SELECTION"""
selected = [i for line in sel.splitlines() if line.strip() for i in line.split(None, 1)[1].split(";")]
reasons = {}
for line in open(missing, encoding="utf-8"):
    if "\t" in line:
        k, v = line.rstrip("\n").split("\t", 1)
        reasons.setdefault(k, v)
meta = {
    "flavor": "$FLAVOR", "cfg": "$CFG", "mode": "$MODE", "only": "$ONLY", "src": "$SRC", "pkg": "$PKG",
    "unity": "$UNITY", "unitySource": "$UNITY_SOURCE", "target": "$BUILD_TARGET", "backend": "$BACKEND", "addressablesRequested": "$AA",
    "started": "$STARTED", "finished": "$FINISHED", "durationSec": $DURATION,
    "selected": selected, "missingReason": reasons, "srcDigest": "$SRC_DIGEST",
    "unityVersion": "$UNITY_VERSION", "project": "$PROJ", "docs": "$DOCS", "docsNote": "$DOCS_NOTE", "modules": "$MODULES",
    "notes": [n for n in """$NOTES_TEXT""".splitlines() if n.strip()],
    "runs": [json.loads(l) for l in open(runs, encoding="utf-8") if l.strip()],
}
json.dump(meta, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}

make_report() {
    write_meta
    local extra=()
    [ -n "$BASELINE_REPORT" ] && extra=(--baseline-report "$BASELINE_REPORT" --baseline-label "$BASELINE")
    python3 -I "$HERE/lib/report.py" --raw "$RAW" --out "$REPORT" --cases "$HERE/cases.json" --rules "$HERE/rules.json" \
        --meta "$RAW/meta.json" --pkg "$SRC" --docs="$DOCS" --docs-note="$DOCS_NOTE" --known "$HERE/known.json" "${extra[@]+"${extra[@]}"}"
    REPORT_EXIT=$?
}

make_report

# Each build finding (and the Spawn finding) has a C# repro (report/repro/*.cs). Run them in Unity the way
# their header says (editor scripts in Assets/KenseiStandRepro/Editor/, runtime scripts - "Place: runtime" -
# in Assets/KenseiStandRepro/Runtime/) and check that each one shows what its case showed; the result goes
# into the report. The folder is removed afterwards.
REPRO_ASSETS="$PROJ/Assets/KenseiStandRepro"
rm -rf "$REPRO_ASSETS" "$REPRO_ASSETS.meta"
if [ $VERIFY = 1 ] && [ $SETUP_OK = 1 ] && [ -f "$RAW/repro-plan.json" ] && \
        python3 -I -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1]))["items"] else 1)' "$RAW/repro-plan.json"; then
    mkdir -p "$REPRO_ASSETS/Editor" "$REPRO_ASSETS/Runtime" "$RAW/repro-verify"
    for f in "$REPORT"/repro/*.cs; do
        if grep -q "^// Place: runtime\." "$f"; then cp "$f" "$REPRO_ASSETS/Runtime/"; else cp "$f" "$REPRO_ASSETS/Editor/"; fi
    done
    run_unity verify -executeMethod KenseiStand.Editor.StandRunner.VerifyRepros -standReproPlan "$RAW/repro-plan.json"
    grep -h "\[KENSEI-STAND\] END repro" "$RAW/unity-verify.log" 2>/dev/null | sed 's/^.*\[KENSEI-STAND\] /  /'
    rm -rf "$REPRO_ASSETS" "$REPRO_ASSETS.meta"
    make_report
fi

if [ $KEEP = 0 ]; then
    rm -rf "$BUILDS"
fi
echo "unity stand: report $REPORT/report.md ($(( $(date +%s) - T0 ))s)"
if [ $SETUP_OK = 0 ] || [ ! -f "$REPORT/report.json" ]; then
    echo "unity stand: the stand could not run (setup failed or no report)"
    exit 2
fi
exit $REPORT_EXIT
