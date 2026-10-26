#!/usr/bin/env bash
# KenseiECS test stand: every module against one source tree, in release and KENSEI_DEBUG builds, and one
# summary. See Stand/README.md.
#
#   Stand/run.sh [--ref <git-ref> | --src <folder>] [--flavor <label>] [--quick | --full]
#                [--modules fuzz-core,contracts,alloc,runner,generator,unity] [--configs rel,dbg]
#                [--baseline <git-ref | label>] [--rerun-baseline] [--no-unity] [--minutes N]
#                [--module-timeout SEC] [--summary-only] [--no-history]
#   Stand/run.sh --selftest [--ref <git-ref> | --src <folder>] [--flavor <label>] [--modules ...]
#                [--configs rel,dbg] [--no-unity] [--module-timeout SEC]
#
#   --ref REF        test a git ref of the KenseiECS repository (the repository this Stand/ folder is in, or
#                    $KENSEI_REPO). It is extracted read-only with `git archive` into Stand/out/src/<name>/: the
#                    package, the generator, the test projects, the solution, docs/ and README.md. <name> is
#                    the tag itself (v2.2.0), otherwise <ref>-<short commit>. A tree extracted earlier gets the
#                    parts it lacks (for example docs/) from the same commit.
#   --src DIR        test a KenseiECS package folder (or a folder that holds KenseiECS/). Default: KenseiECS/ of
#                    this checkout, label wt. Its documents are docs/ next to the package.
#   --flavor NAME    label of the tree in the report folders (default: the ref name, wt, or the folder above --src)
#   --quick | --full module modes (default --quick); --minutes N goes to fuzz-core --full
#   --modules LIST   modules to run (default: all six). The summary always covers all six: a module that did not
#                    run now is taken from its last report on this tree while that report is current.
#   --configs LIST   rel, dbg or both (default rel,dbg); alloc also runs --ci-parity once (build "ci")
#   --baseline REF   compare with another tree (a git ref, a label whose reports exist, or wt). Every module
#                    gets --baseline (new signatures, growth, regressions) and the summary compares catalog
#                    statuses and signatures. Modules without a report on the baseline run on it first
#                    (--rerun-baseline: always). The baseline's summary is rebuilt from its reports into
#                    summary-<baseline>.as-baseline-of-<flavor>.md/.json; summary-<baseline>.md stays as its own run
#                    wrote it. Without --baseline, alloc still measures against v2.1.1.
#   --no-unity       do not start Unity; the summary takes the last unity reports of the tree if they match it.
#                    Without a Unity editor (UNITY, or the Unity Hub folders) the module is skipped the same way.
#   --summary-only   run nothing, rebuild the summary from the reports that are there
#   --selftest       instead of a run: the mutation self-test of every selected module. Each module applies the
#                    patches in Stand/<module>/mutants/ to a temporary copy of the tree and checks that it
#                    catches every injected defect and stays quiet on every emulated fix.
#                    Summary: Stand/out/reports/selftest-<flavor>.md and .json
#   --module-timeout SEC  stop a module run after SEC seconds (default 1800 with --quick, 14400 with --full,
#                    3600 for a self-test)
#   --no-history     do not copy the previous reports to Stand/out/history/ before overwriting them
#
# Output: Stand/out/reports/summary-<flavor>.md and .json (catalog status of every known problem, module runs,
# defects and regressions outside the catalog, comparison with the baseline); module logs in
# Stand/out/logs/stand/<flavor>-<stamp>/. Exit code: 0 every module ran (with --selftest: every self-test
# passed), 1 a module failed, 2 bad usage.
#
# Needs bash, python3 3.9+, git, perl and a .NET SDK 10 (the net8.0 projects run on it with roll-forward):
# $DOTNET, $DOTNET_ROOT, `dotnet` on PATH or a standard install folder; without one the SDK is downloaded into
# Stand/out/tools/dotnet-sdk/. The unity module needs a Unity editor (see Stand/README.md).
#
# Examples:
#   Stand/run.sh --quick --ref v2.2.0
#   Stand/run.sh --quick --baseline v2.2.0                  (working copy against 2.2.0)
#   Stand/run.sh --full --ref v2.3.0 --baseline v2.2.1
#   Stand/run.sh --selftest
#   Stand/run.sh --summary-only --ref v2.2.0
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAND="$HERE"
WT="$(cd "$STAND/.." && pwd)"
OUT="$STAND/out"
ALL_MODULES="fuzz-core,contracts,alloc,runner,generator,unity"
# Fast modules first: a broken build shows up in seconds, not after the fuzzer.
RUN_ORDER="contracts runner generator fuzz-core alloc unity"
# Everything a module may need from a git ref (alloc --ci-parity builds the test projects with the solution).
EXTRACT_PATHS="KenseiECS KenseiECS.Generators KenseiECS.Tests KenseiECS.Generators.Tests KenseiECS.NET KenseiECS.sln docs README.md"

MODE=quick
REF=""
SRC_ARG=""
FLAVOR=""
MODULES="$ALL_MODULES"
CONFIGS="rel,dbg"
BASELINE=""
RERUN_BASELINE=0
NO_UNITY=0
NO_UNITY_ARG=0
UNITY_NOTE=""
MINUTES=""
TIMEOUT=""
SUMMARY_ONLY=0
SELFTEST=0
HISTORY=1

usage() { awk 'NR > 1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; }
die() { echo "stand: $*" >&2; exit 2; }
say() { echo "stand: $*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --quick) MODE=quick ;;
        --full) MODE=full ;;
        --ref) [ $# -ge 2 ] || die "--ref needs a value"; REF=$2; shift ;;
        --src) [ $# -ge 2 ] || die "--src needs a value"; SRC_ARG=$2; shift ;;
        --flavor) [ $# -ge 2 ] || die "--flavor needs a value"; FLAVOR=$2; shift ;;
        --modules) [ $# -ge 2 ] || die "--modules needs a value"; MODULES=$2; shift ;;
        --configs) [ $# -ge 2 ] || die "--configs needs a value"; CONFIGS=$2; shift ;;
        --baseline) [ $# -ge 2 ] || die "--baseline needs a value"; BASELINE=$2; shift ;;
        --rerun-baseline) RERUN_BASELINE=1 ;;
        --no-unity) NO_UNITY=1; NO_UNITY_ARG=1; UNITY_NOTE="not started: --no-unity" ;;
        --minutes) [ $# -ge 2 ] || die "--minutes needs a value"; MINUTES=$2; shift ;;
        --module-timeout) [ $# -ge 2 ] || die "--module-timeout needs a value"; TIMEOUT=$2; shift ;;
        --summary-only) SUMMARY_ONLY=1 ;;
        --selftest) SELFTEST=1 ;;
        --no-history) HISTORY=0 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument $1 (see --help)" ;;
    esac
    shift
done

[ -n "$REF" ] && [ -n "$SRC_ARG" ] && die "--ref and --src exclude each other"
[ $SELFTEST = 1 ] && [ $SUMMARY_ONLY = 1 ] && die "--selftest and --summary-only exclude each other"
[ $SELFTEST = 1 ] && [ -n "$BASELINE" ] && die "--selftest compares every mutant with the unmutated tree; it takes no --baseline"
[ "$MODULES" = all ] && MODULES="$ALL_MODULES"
for m in $(echo "$MODULES" | tr ',' ' '); do
    case ",$ALL_MODULES," in *",$m,"*) ;; *) die "unknown module $m (modules: $ALL_MODULES)" ;; esac
done
for c in $(echo "$CONFIGS" | tr ',' ' '); do
    case "$c" in rel|dbg) ;; *) die "unknown config $c (rel, dbg)" ;; esac
done
if [ -z "$TIMEOUT" ]; then
    if [ $SELFTEST = 1 ]; then TIMEOUT=3600; elif [ "$MODE" = full ]; then TIMEOUT=14400; else TIMEOUT=1800; fi
fi
case "$TIMEOUT" in ''|*[!0-9]*) die "--module-timeout needs seconds" ;; esac
if [ -n "$MINUTES" ]; then
    case "$MINUTES" in *[!0-9]*) die "--minutes needs a number" ;; esac
fi

export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

has_module() { case ",$MODULES," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }
is_pkg() { [ -f "$1/Core/World.cs" ] && [ -f "$1/package.json" ]; }

# ------------------------------------------------------------------ git repository

# The repository the refs come from: $KENSEI_REPO, else the checkout this Stand/ folder lives in.
REPO="${KENSEI_REPO:-}"
if [ -z "$REPO" ] && git -C "$WT" log -1 --format=%H >/dev/null 2>&1; then
    REPO="$WT"
fi

need_repo() {
    [ -n "$REPO" ] || die "Stand/ is not inside a git checkout of KenseiECS: set KENSEI_REPO to one"
    git -C "$REPO" log -1 --format=%H >/dev/null 2>&1 || die "no git repository at $REPO (set KENSEI_REPO)"
}

# ------------------------------------------------------------------ trees

# extract_paths <commit> <dest> <paths...>: git archive of those paths into <dest> (made read-only).
extract_paths() {
    local commit=$1 dest=$2 tmp
    shift 2
    tmp="$OUT/src/.tmp-$(basename "$dest")-$$"
    rm -rf "$tmp"
    mkdir -p "$tmp"
    if ! git -C "$REPO" archive --format=tar "$commit" "$@" | tar -x -C "$tmp"; then
        chmod -R u+w "$tmp" 2>/dev/null; rm -rf "$tmp"
        return 1
    fi
    mkdir -p "$dest"
    chmod u+w "$dest"
    local p
    for p in "$@"; do
        if [ -e "$tmp/$p" ] && [ ! -e "$dest/$p" ]; then
            # Moving a directory needs write access to it (its ".." entry): lock it after the move.
            mv "$tmp/$p" "$dest/$p" || { chmod -R u+w "$tmp"; rm -rf "$tmp"; return 1; }
            chmod -R a-w "$dest/$p"
        fi
    done
    chmod -R u+w "$tmp" 2>/dev/null; rm -rf "$tmp"
    return 0
}

# resolve_ref REF: prints the package folder of REF under Stand/out/src/, extracting what is missing first.
resolve_ref() {
    local ref=$1 commit name dest entries p missing=() present=() stamp
    need_repo
    commit=$(git -C "$REPO" log -1 --format=%H "$ref" -- 2>/dev/null) || commit=""
    [ -n "$commit" ] || die "git ref $ref not found in $REPO"
    if git -C "$REPO" log -1 --format=%H "refs/tags/$ref" -- >/dev/null 2>&1; then
        name=$(printf '%s' "$ref" | tr -c 'A-Za-z0-9._-' '_')
    else
        case "$commit" in
            "$ref"*) name=${commit:0:12} ;;
            *) name="$(printf '%s' "$ref" | tr -c 'A-Za-z0-9._-' '_')-${commit:0:7}" ;;
        esac
    fi
    dest="$OUT/src/$name"
    entries=$(git -C "$REPO" show "$commit:" 2>/dev/null | tail -n +3)
    for p in $EXTRACT_PATHS; do
        printf '%s\n' "$entries" | grep -qxF -e "$p/" -e "$p" || continue
        present+=("$p")
        [ -e "$dest/$p" ] || missing+=("$p")
    done
    printf '%s\n' "${present[@]+"${present[@]}"}" | grep -qx KenseiECS || die "$ref has no KenseiECS/ folder"
    if [ -d "$dest" ] && [ -f "$dest/.stand-ref" ] && ! grep -q "\"commit\": \"$commit\"" "$dest/.stand-ref"; then
        echo "stand: warning: $dest was extracted from another commit of $ref (delete it to refresh)" >&2
        missing=()
    fi
    if [ ${#missing[@]} -gt 0 ]; then
        mkdir -p "$OUT/src"
        if [ -d "$dest" ]; then
            echo "stand: completing $dest with ${missing[*]} of $ref ($commit)" >&2
        else
            echo "stand: extracting $ref ($commit) into $dest" >&2
        fi
        extract_paths "$commit" "$dest" "${missing[@]}" || die "git archive of $ref failed"
    fi
    is_pkg "$dest/KenseiECS" || die "$dest holds no KenseiECS package"
    if [ ! -f "$dest/.stand-ref" ]; then
        stamp=$(date +%Y-%m-%dT%H:%M:%S%z)
        chmod u+w "$dest"
        printf '{"ref": "%s", "commit": "%s", "extracted": "%s", "paths": "%s"}\n' \
            "$ref" "$commit" "$stamp" "${present[*]}" > "$dest/.stand-ref"
        chmod a-w "$dest/.stand-ref"
    fi
    chmod a-w "$dest"
    echo "$dest/KenseiECS"
}

# resolve_src DIR: prints the package folder for DIR (the package itself or its parent).
resolve_src() {
    local d
    d=$(cd "$1" 2>/dev/null && pwd) || die "--src: no such folder $1"
    if is_pkg "$d"; then echo "$d"; return 0; fi
    if is_pkg "$d/KenseiECS"; then echo "$d/KenseiECS"; return 0; fi
    die "$d is not a KenseiECS package folder (no Core/World.cs and package.json)"
}

default_flavor() {
    local pkg=$1
    if [ "$pkg" = "$WT/KenseiECS" ]; then echo wt; else basename "$(dirname "$pkg")"; fi
}

check_flavor() {
    case "$1" in ''|*[!A-Za-z0-9._-]*) die "label '$1' may only contain letters, digits, '.', '_' and '-' (use --flavor)" ;; esac
}

if [ -n "$REF" ]; then
    SRC=$(resolve_ref "$REF") || exit 2
    [ -n "$FLAVOR" ] || FLAVOR=$(basename "$(dirname "$SRC")")
elif [ -n "$SRC_ARG" ]; then
    SRC=$(resolve_src "$SRC_ARG") || exit 2
    [ -n "$FLAVOR" ] || FLAVOR=$(default_flavor "$SRC")
else
    SRC="$WT/KenseiECS"
    is_pkg "$SRC" || die "no KenseiECS package in $WT"
    [ -n "$FLAVOR" ] || FLAVOR=wt
fi
check_flavor "$FLAVOR"

BASE_FLAVOR=""
BASE_SRC=""
if [ -n "$BASELINE" ]; then
    if [ "$BASELINE" = wt ]; then
        BASE_SRC="$WT/KenseiECS"
    elif [ -n "$REPO" ] && git -C "$REPO" log -1 --format=%H "$BASELINE" -- >/dev/null 2>&1; then
        BASE_SRC=$(resolve_ref "$BASELINE") || exit 2
    elif is_pkg "$OUT/src/$BASELINE/KenseiECS"; then
        BASE_SRC="$OUT/src/$BASELINE/KenseiECS"
    else
        die "--baseline $BASELINE is neither a git ref, wt, nor a tree under Stand/out/src/"
    fi
    BASE_FLAVOR=$(default_flavor "$BASE_SRC")
    check_flavor "$BASE_FLAVOR"
    if [ "$BASE_FLAVOR" = "$FLAVOR" ]; then
        say "baseline $BASELINE is the tree under test, no comparison"
        BASELINE=""; BASE_FLAVOR=""; BASE_SRC=""
    fi
fi

# alloc measures against the baseline, or against v2.1.1 (its reference version) when there is none.
ALLOC_BASE_FLAVOR=""
ALLOC_BASE_SRC=""
if [ $SUMMARY_ONLY = 0 ] && [ $SELFTEST = 0 ] && has_module alloc; then
    if [ -n "$BASE_FLAVOR" ]; then
        ALLOC_BASE_FLAVOR=$BASE_FLAVOR; ALLOC_BASE_SRC=$BASE_SRC
    elif [ "$FLAVOR" != v2.1.1 ]; then
        ALLOC_BASE_SRC=$(resolve_ref v2.1.1) || exit 2
        ALLOC_BASE_FLAVOR=v2.1.1
    fi
fi

# ------------------------------------------------------------------ tools

# find_dotnet: the dotnet muxer of an installed SDK ($DOTNET, $DOTNET_ROOT, PATH, Stand/out/tools, usual folders).
find_dotnet() {
    local c d
    if [ -n "${DOTNET:-}" ]; then
        c=$(command -v "$DOTNET" 2>/dev/null) && { echo "$c"; return 0; }
    fi
    if [ -n "${DOTNET_ROOT:-}" ]; then
        for c in "$DOTNET_ROOT/dotnet" "$DOTNET_ROOT/dotnet.exe"; do [ -x "$c" ] && { echo "$c"; return 0; }; done
    fi
    c=$(command -v dotnet 2>/dev/null) && { echo "$c"; return 0; }
    for d in "$OUT/tools/dotnet-sdk" /usr/local/share/dotnet /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec \
             /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet "${HOME:-/nonexistent}/.dotnet" "/c/Program Files/dotnet"; do
        for c in "$d/dotnet" "$d/dotnet.exe"; do [ -x "$c" ] && { echo "$c"; return 0; }; done
    done
    return 1
}

install_dotnet() {
    local dest="$OUT/tools/dotnet-sdk"
    mkdir -p "$dest"
    say "no .NET SDK found: installing the .NET 10 SDK into $dest" >&2
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dest/dotnet-install.sh" || die "could not download dotnet-install.sh"
    bash "$dest/dotnet-install.sh" --channel 10.0 --install-dir "$dest" --no-path >/dev/null || die "the .NET SDK install failed"
    echo "$dest/dotnet"
}

# setup_dotnet: one SDK for every module (exported as DOTNET and DOTNET_ROOT). A wrapper script on PATH
# (Homebrew) is resolved to the SDK folder it starts.
setup_dotnet() {
    local bin root sdk
    bin=$(find_dotnet) || bin=$(install_dotnet) || exit 2
    sdk=$("$bin" --list-sdks 2>/dev/null | tail -n 1 | sed -n 's/^.*\[\(.*\)\]$/\1/p')
    [ -n "$sdk" ] || die "$bin lists no .NET SDK (install a .NET SDK 10, or set DOTNET_ROOT)"
    root=$(dirname "$sdk")
    if [ -x "$root/dotnet" ]; then bin="$root/dotnet"; elif [ -x "$root/dotnet.exe" ]; then bin="$root/dotnet.exe"; fi
    export DOTNET="$bin"
    export DOTNET_ROOT="$root"
    export PATH="$root:$PATH"
}

needs_dotnet() {
    local m
    for m in $(echo "$MODULES" | tr ',' ' '); do [ "$m" = unity ] || return 0; done
    return 1
}

if [ $SUMMARY_ONLY = 0 ] && needs_dotnet; then
    setup_dotnet
fi

# A Unity editor for the unity module (UNITY, or the newest suitable one of the Unity Hub folders).
if [ $SUMMARY_ONLY = 0 ] && has_module unity && [ $NO_UNITY = 0 ]; then
    if ! UNITY_FIND=$(python3 -I "$STAND/unity/lib/unity.py" find 2>&1); then
        NO_UNITY=1
        UNITY_NOTE=$(printf '%s' "$UNITY_FIND" | sed -n 's/.*"error": "\(.*\)".*/\1/p')
        [ -n "$UNITY_NOTE" ] || UNITY_NOTE="no Unity editor found"
    fi
fi

# ------------------------------------------------------------------ bookkeeping

STAMP=$(date +%Y%m%d-%H%M%S)
RUN_START=$(date +%s)
KIND=run
[ $SELFTEST = 1 ] && KIND=selftest
LOGDIR="$OUT/logs/stand/$FLAVOR-$STAMP"
[ $SELFTEST = 1 ] && LOGDIR="$OUT/logs/stand/selftest-$FLAVOR-$STAMP"
STEPS=""
mkdir -p "$OUT/reports"
if [ $SUMMARY_ONLY = 0 ]; then
    STEPS="$LOGDIR/steps.tsv"
    mkdir -p "$LOGDIR"
    : > "$STEPS"
fi
FAILED=0
CHILD=""

LOCK="$OUT/locks/$KIND-$FLAVOR"
mkdir -p "$OUT/locks"
if ! mkdir "$LOCK" 2>/dev/null; then
    owner=$(cat "$LOCK/pid" 2>/dev/null || true)
    if [ -n "$owner" ] && kill -0 "$owner" 2>/dev/null; then
        die "another Stand/run.sh (pid $owner) works on $FLAVOR"
    fi
    rm -rf "$LOCK"
    mkdir "$LOCK" || die "cannot take $LOCK"
fi
echo $$ > "$LOCK/pid"
on_exit() {
    if [ -n "$CHILD" ] && kill -0 "$CHILD" 2>/dev/null; then
        kill -TERM -- "-$CHILD" 2>/dev/null
        sleep 5
        kill -KILL -- "-$CHILD" 2>/dev/null
    fi
    rm -rf "$LOCK"
}
trap on_exit EXIT
trap 'echo "stand: interrupted" >&2; exit 130' INT TERM

archive_report() {   # archive_report <module> <flavor> <cfg>: keep the previous report before a run replaces it
    [ $HISTORY = 1 ] || return 0
    local src_dir="$OUT/reports/$1/$2-$3" dest="$OUT/history/$2/$STAMP/$1"
    [ -d "$src_dir" ] || return 0
    mkdir -p "$dest"
    cp -a "$src_dir" "$dest/" 2>/dev/null || echo "stand: warning: could not archive $src_dir" >&2
}

prune_history() {    # keep the last 5 runs per label
    local dir="$OUT/history/$1" old
    [ -d "$dir" ] || return 0
    for old in $(ls -1 "$dir" | sort -r | tail -n +6); do
        rm -rf "${dir:?}/$old"
    done
}

# run_step <flavor> <role> <module> <cfg> <command...>: one module run with a timeout, logged, recorded in steps.tsv.
run_step() {
    local flavor=$1 role=$2 module=$3 cfg=$4
    shift 4
    local log="$LOGDIR/$flavor-$module-$cfg.log" start end code
    [ "$role" = baseline ] && log="$LOGDIR/baseline-$flavor-$module-$cfg.log"
    [ "$role" = selftest ] && log="$LOGDIR/selftest-$module-$cfg.log"
    [ "$role" = selftest ] || archive_report "$module" "$flavor" "$cfg"
    printf 'stand: %-9s %-3s %s%s ... ' "$module" "$cfg" "$flavor" "$([ "$role" = selftest ] && echo ' (self-test)')"
    start=$(date +%s)
    {
        echo "# $(date '+%Y-%m-%d %H:%M:%S') $*"
        echo
    } > "$log"
    perl -e 'setpgrp(0, 0); exec @ARGV or die "exec failed: $!\n"' "$@" >> "$log" 2>&1 < /dev/null &
    CHILD=$!
    while kill -0 "$CHILD" 2>/dev/null; do
        if [ $(( $(date +%s) - start )) -ge "$TIMEOUT" ]; then
            echo "stand: timeout after ${TIMEOUT}s, stopping the module" >> "$log"
            kill -TERM -- "-$CHILD" 2>/dev/null
            sleep 10
            kill -KILL -- "-$CHILD" 2>/dev/null
            break
        fi
        sleep 1
    done
    wait "$CHILD" 2>/dev/null
    code=$?
    CHILD=""
    end=$(date +%s)
    printf '%s\t%s\t%s\t%s\t%d\t%d\t%d\t%s\n' "$flavor" "$role" "$module" "$cfg" "$code" "$start" "$end" "$log" >> "$STEPS"
    if [ $code = 0 ] || { [ "$role" != selftest ] && [ "$module" = unity ] && [ $code = 1 ]; }; then
        echo "ok ($(( end - start )) s)"
    else
        echo "FAILED, exit $code ($(( end - start )) s), log $log"
        tail -n 15 "$log" | sed 's/^/    | /'
        FAILED=1
    fi
}

# run_module <flavor> <src> <role> <module> <cfg> [<baseline flavor> <baseline src>]
run_module() {
    local flavor=$1 src=$2 role=$3 module=$4 cfg=$5 bflavor=${6:-} bsrc=${7:-}
    local cmd=("$STAND/$module/run.sh")
    if [ "$cfg" = ci ]; then
        cmd+=(--ci-parity "--$MODE" --src "$src" --flavor "$flavor")
    else
        cmd+=("--$MODE" --src "$src" --flavor "$flavor")
        [ "$cfg" = dbg ] && cmd+=(--debug)
        [ "$module" = fuzz-core ] && [ "$MODE" = full ] && [ -n "$MINUTES" ] && cmd+=(--minutes "$MINUTES")
        if [ -n "$bflavor" ]; then
            case "$module" in
                alloc|generator) cmd+=(--baseline "$bflavor" --baseline-src "$bsrc") ;;
                *) cmd+=(--baseline "$bflavor") ;;
            esac
        fi
    fi
    run_step "$flavor" "$role" "$module" "$cfg" "${cmd[@]}"
}

# run_selftest <module> <cfg>: the module's mutation self-test on a copy of the tree under test.
run_selftest() {
    local module=$1 cfg=$2
    local cmd=("$STAND/$module/run.sh" --selftest --src "$SRC" --flavor "$FLAVOR")
    case "$module" in
        alloc) cmd+=(--quick) ;;
        *) [ "$cfg" = dbg ] && cmd+=(--debug) ;;
    esac
    run_step "$FLAVOR" selftest "$module" "$cfg" "${cmd[@]}"
}

report_exists() { [ -f "$OUT/reports/$1/$2-$3/report.json" ]; }

builds_of() {   # builds_of <module>: the configs of this run, plus ci for alloc
    local b
    b=$(echo "$CONFIGS" | tr ',' ' ')
    [ "$1" = alloc ] && b="$b ci"
    echo "$b"
}

# The summary of the baseline that a comparison rebuilds goes to a file of its own: summary-<baseline>.md and
# .json stay what the baseline's own run wrote (with its own "Against ..." section).
baseline_summary_name() { echo "$1.as-baseline-of-$2"; }

summary() {    # summary <flavor> <src> [<baseline flavor>] [as-baseline]
    # Every module goes into the summary: one that did not run now is taken from its last report on this tree
    # (marked as such; a report older than the tree's newest file is left out). The summary of the baseline is
    # rebuilt from its reports, with the runs this invocation made on it, into summary-<baseline>.as-baseline-of-<flavor>.
    local args=(build --flavor "$1" --src "$2" --modules "$ALL_MODULES" --configs "$CONFIGS")
    if [ "${4:-}" = as-baseline ]; then
        args+=(--command "rebuilt from its reports as the baseline of: $CMDLINE" --mode existing --out-name "$(baseline_summary_name "$1" "$FLAVOR")")
        [ -n "$STEPS" ] && args+=(--steps "$STEPS")
    elif [ -n "$STEPS" ]; then
        args+=(--command "$CMDLINE" --steps "$STEPS" --run-start "$RUN_START" --mode "$MODE")
    else
        args+=(--command "$CMDLINE" --mode existing)
    fi
    if [ -n "${3:-}" ]; then
        args+=(--baseline "$3")
        [ -f "$OUT/reports/summary-$(baseline_summary_name "$3" "$1").json" ] && args+=(--baseline-summary "$OUT/reports/summary-$(baseline_summary_name "$3" "$1").json")
    fi
    # "=" keeps a note that starts with "-" from being read as an option.
    [ -n "$UNITY_NOTE" ] && args+=("--unity-note=$UNITY_NOTE")
    python3 -I "$STAND/summary/summary.py" "${args[@]}"
}

CMDLINE="Stand/run.sh"
[ $SELFTEST = 1 ] && CMDLINE="$CMDLINE --selftest" || CMDLINE="$CMDLINE --$MODE"
[ -n "$REF" ] && CMDLINE="$CMDLINE --ref $REF"
[ -n "$SRC_ARG" ] && CMDLINE="$CMDLINE --src $SRC_ARG"
[ "$MODULES" != "$ALL_MODULES" ] && CMDLINE="$CMDLINE --modules $MODULES"
[ "$CONFIGS" != "rel,dbg" ] && CMDLINE="$CMDLINE --configs $CONFIGS"
[ -n "$BASELINE" ] && CMDLINE="$CMDLINE --baseline $BASELINE"
[ $NO_UNITY_ARG = 1 ] && CMDLINE="$CMDLINE --no-unity"
[ $SUMMARY_ONLY = 1 ] && CMDLINE="$CMDLINE --summary-only"

SRC_SHOWN=$SRC
case "$SRC" in "$WT"/*) SRC_SHOWN=${SRC#"$WT"/} ;; esac
PKG_VERSION=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SRC/package.json" | head -n 1)
if [ $SELFTEST = 1 ]; then
    say "self-test on $SRC_SHOWN ($FLAVOR, package $PKG_VERSION), modules $MODULES, configs $CONFIGS"
else
    say "tree $SRC_SHOWN ($FLAVOR, package $PKG_VERSION), --$MODE, modules $MODULES, configs $CONFIGS${BASE_FLAVOR:+, baseline $BASE_FLAVOR}"
fi
[ -n "${DOTNET:-}" ] && [ $SUMMARY_ONLY = 0 ] && say ".NET SDK $DOTNET_ROOT"
if has_module unity && [ -n "$UNITY_NOTE" ] && [ $SUMMARY_ONLY = 0 ]; then
    say "unity skipped ($UNITY_NOTE): the summary takes its last reports for $FLAVOR"
fi
[ -n "$STEPS" ] && say "logs $LOGDIR"

# ------------------------------------------------------------------ self-test

if [ $SELFTEST = 1 ]; then
    for module in $RUN_ORDER; do
        has_module "$module" || continue
        [ "$module" = unity ] && [ $NO_UNITY = 1 ] && continue
        if [ "$module" = alloc ]; then
            run_selftest alloc all       # alloc runs its own builds (KENSEI_DEBUG and --ci-parity) per mutant
            continue
        fi
        for cfg in $(echo "$CONFIGS" | tr ',' ' '); do
            run_selftest "$module" "$cfg"
        done
    done
    st_args=(selftest --flavor "$FLAVOR" --src "$SRC" --steps "$STEPS" --run-start "$RUN_START"
             --modules "$MODULES" --configs "$CONFIGS" --command "$CMDLINE")
    [ -n "$UNITY_NOTE" ] && st_args+=("--unity-note=$UNITY_NOTE")
    python3 -I "$STAND/summary/summary.py" "${st_args[@]}" || FAILED=1
    say "done in $(( $(date +%s) - RUN_START )) s: $OUT/reports/selftest-$FLAVOR.md"
    exit $FAILED
fi

# ------------------------------------------------------------------ runs

if [ $SUMMARY_ONLY = 0 ]; then
    if [ $HISTORY = 1 ]; then
        for f in "$OUT/reports/summary-$FLAVOR.md" "$OUT/reports/summary-$FLAVOR.json"; do
            [ -f "$f" ] && mkdir -p "$OUT/history/$FLAVOR/$STAMP" && cp -p "$f" "$OUT/history/$FLAVOR/$STAMP/"
        done
    fi

    # The baseline first: the modules compare with its reports, the summary with its summary.
    if [ -n "$BASE_FLAVOR" ]; then
        for module in $RUN_ORDER; do
            has_module "$module" || continue
            [ "$module" = unity ] && [ $NO_UNITY = 1 ] && continue
            for cfg in $(builds_of "$module"); do
                if [ $RERUN_BASELINE = 1 ] || ! report_exists "$module" "$BASE_FLAVOR" "$cfg"; then
                    if [ "$module" = alloc ] && [ "$cfg" != ci ] && [ "$BASE_FLAVOR" != v2.1.1 ]; then
                        run_module "$BASE_FLAVOR" "$BASE_SRC" baseline "$module" "$cfg" v2.1.1 "$(resolve_ref v2.1.1)"
                    else
                        run_module "$BASE_FLAVOR" "$BASE_SRC" baseline "$module" "$cfg"
                    fi
                fi
            done
        done
    fi

    for module in $RUN_ORDER; do
        has_module "$module" || continue
        [ "$module" = unity ] && [ $NO_UNITY = 1 ] && continue
        for cfg in $(builds_of "$module"); do
            case "$module" in
                alloc) run_module "$FLAVOR" "$SRC" target "$module" "$cfg" "$ALLOC_BASE_FLAVOR" "$ALLOC_BASE_SRC" ;;
                *) run_module "$FLAVOR" "$SRC" target "$module" "$cfg" "$BASE_FLAVOR" "$BASE_SRC" ;;
            esac
        done
    done
    prune_history "$FLAVOR"
    [ -n "$BASE_FLAVOR" ] && prune_history "$BASE_FLAVOR"
fi

# ------------------------------------------------------------------ summary

if [ -n "$BASE_FLAVOR" ]; then
    # The baseline summary is rebuilt from its reports every time, so both sides use the same summary code; it goes
    # to a file of its own, so the baseline's summary-<label> keeps its own comparison.
    if [ -n "$STEPS" ]; then
        summary "$BASE_FLAVOR" "$BASE_SRC" "" as-baseline > "$LOGDIR/summary-$BASE_FLAVOR.txt" 2>&1 || true
    else
        summary "$BASE_FLAVOR" "$BASE_SRC" "" as-baseline > /dev/null 2>&1 || true
    fi
    say "baseline summary: $OUT/reports/summary-$(baseline_summary_name "$BASE_FLAVOR" "$FLAVOR").md (summary-$BASE_FLAVOR.md is left as it was)"
fi
summary "$FLAVOR" "$SRC" "$BASE_FLAVOR" || FAILED=1
say "done in $(( $(date +%s) - RUN_START )) s: $OUT/reports/summary-$FLAVOR.md"
exit $FAILED
