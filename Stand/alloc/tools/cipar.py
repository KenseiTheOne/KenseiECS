"""--ci-parity: the test projects of a tree on the official .NET 8 runtime, the way CI runs them.

CI (.github/workflows/ci.yml, ubuntu, setup-dotnet 8.0.x + 10.0.x):
    dotnet build KenseiECS.sln -c Release
    dotnet test KenseiECS.Tests -c Release --no-build
    dotnet test KenseiECS.Generators.Tests -c Release --no-build
    dotnet test KenseiECS.Tests -c Release -p:KenseiDebug=true
The tree is copied to Stand/out/ci/<flavor>/ (the extracted versions under Stand/out/src are read-only)
with an empty Directory.Build.props, so the stand's own props do not apply, as in the repository. Test
projects or KenseiECS.sln that an extracted tree lacks come from `git archive` of its commit. The test
projects are built one by one (the solution also holds Example, Demo and Benchmark, which the copy does
not need), then tested with --no-build, as CI does.

Controls show why a failure is "green locally": the same tests in the Debug configuration (the default of
`dotnet test` and of IDEs) and on .NET 10. A failing allocation assert is checked by a diagnostic copy
(Stand/out/ci/<flavor>-assertzero/) in which only the failing assert lines change from Is.EqualTo(0) to
Is.Zero: if the test passes there, the library did not allocate and the 24 bytes were the assert's own
box. Every red test gets a cause class:
    test-measurement-artifact  the box of the expected 0 inside the window (the diagnostic copy passes)
    fails-locally-too          red in the local Debug run as well: a failing test, not a CI-parity effect
    ci-only                    red only in the CI configuration, and not that artifact: check it as a real failure
Nothing in the source trees is changed.
"""
import datetime
import json
import os
import re
import shutil
import xml.etree.ElementTree as ET

import standlib as S

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
CORE, GEN = "KenseiECS.Tests", "KenseiECS.Generators.Tests"
PARTS = ("KenseiECS", "KenseiECS.Tests", "KenseiECS.Generators", "KenseiECS.Generators.Tests", "KenseiECS.NET")
ROOT_FILES = ("KenseiECS.sln",)

# name, project, runtime, build args, what it is
CI_RUNS = [
    ("ci-release", CORE, "net8", ["-c", "Release"], "CI: dotnet test KenseiECS.Tests -c Release --no-build (.NET 8)"),
    ("ci-gen-release", GEN, "net8", ["-c", "Release"], "CI: dotnet test KenseiECS.Generators.Tests -c Release --no-build (.NET 8)"),
    ("ci-kdebug", CORE, "net8", ["-c", "Release", "-p:KenseiDebug=true"], "CI: dotnet test KenseiECS.Tests -c Release -p:KenseiDebug=true (.NET 8)"),
]
CI_NAMES = [r[0] for r in CI_RUNS]
CONTROLS_QUICK = [
    ("local-debug", CORE, "net8", ["-c", "Debug"], "control: dotnet test without -c (Debug), .NET 8"),
    ("net10-debug", CORE, "net10", ["-c", "Debug"], "control: dotnet test without -c on the local .NET 10 (how tests run locally)"),
    ("local-debug-kdebug", CORE, "net8", ["-c", "Debug", "-p:KenseiDebug=true"], "control: Debug + KENSEI_DEBUG, .NET 8"),
    ("net10-release", CORE, "net10", ["-c", "Release"], "control: -c Release on .NET 10"),
    ("gen-local-debug", GEN, "net8", ["-c", "Debug"], "control: generator tests without -c (Debug), .NET 8"),
    ("gen-net10-debug", GEN, "net10", ["-c", "Debug"], "control: generator tests without -c on the local .NET 10"),
]
CONTROLS_FULL = CONTROLS_QUICK + [
    ("net10-kdebug", CORE, "net10", ["-c", "Release", "-p:KenseiDebug=true"], "control: -c Release -p:KenseiDebug=true on .NET 10"),
    ("net10-debug-kdebug", CORE, "net10", ["-c", "Debug", "-p:KenseiDebug=true"], "control: Debug + KENSEI_DEBUG on .NET 10"),
    ("gen-net10-release", GEN, "net10", ["-c", "Release"], "control: generator tests -c Release on .NET 10"),
]
# Runs in the Debug configuration: how the tests run locally (`dotnet test` without -c, an IDE).
LOCAL_RUNS = ("local-debug", "net10-debug", "local-debug-kdebug", "net10-debug-kdebug", "gen-local-debug", "gen-net10-debug")
SEVERITY = {"test-measurement-artifact": "low", "ci-only": "medium", "fails-locally-too": "high"}
CAUSE_TEXT = {
    "test-measurement-artifact": "test artifact: the box of the expected 0 of Is.EqualTo(0) lands inside the measured window in the "
                                 "optimized test build; the library allocates nothing (with Is.Zero in this line the test passes)",
    "fails-locally-too": "red in the local Debug run as well: a failing test of the package, not a CI-parity effect",
    "ci-only": "red only in the CI configuration (Release, .NET 8) and not the assert artifact: check it as a real failure",
}


def copy_tree(tree, dest, commit, parts=PARTS):
    """The projects of `tree` into a fresh `dest` without bin/ and obj/; what the tree lacks comes from
    `git archive <commit>`. Returns {"copied": [...], "from_git": [...], "missing": [...]}."""
    if os.path.exists(dest):
        for root, dirs, files in os.walk(dest):
            os.chmod(root, 0o755)
        shutil.rmtree(dest)
    os.makedirs(dest)
    ignore = shutil.ignore_patterns("bin", "obj", "*.meta", "Samples~")
    out = {"copied": [], "from_git": [], "missing": []}
    lacking = []
    for name in list(parts) + list(ROOT_FILES):
        p = os.path.join(tree, name)
        if os.path.isdir(p):
            shutil.copytree(p, os.path.join(dest, name), ignore=ignore)
            out["copied"].append(name)
        elif os.path.isfile(p):
            shutil.copy2(p, os.path.join(dest, name))
            out["copied"].append(name)
        else:
            lacking.append(name)
    if lacking and commit:
        got = S.git_extract(commit, lacking, dest)
        out["from_git"] = got
        lacking = [x for x in lacking if x not in got]
    out["missing"] = lacking
    for root, dirs, files in os.walk(dest):
        os.chmod(root, 0o755)
        for f in files:
            os.chmod(os.path.join(root, f), 0o644)
    if "KenseiECS.sln" in lacking:
        # The packaged-DLL tests of the generator find the repository root by this file.
        with open(os.path.join(dest, "KenseiECS.sln"), "w") as f:
            f.write("# Stand/alloc ci-parity copy: marks the repository root (the tree had no KenseiECS.sln)\n")
    # Stops MSBuild at the copy root: the repository has no Directory.Build.props, the stand has one.
    with open(os.path.join(dest, "Directory.Build.props"), "w") as f:
        f.write("<Project>\n  <!-- Stand/alloc ci-parity copy: keeps Stand/Directory.Build.props out, as in the repository. -->\n</Project>\n")
    return out


_RUNTIME_LOCATION = re.compile(r"Runtime location: (\S+)")


def dotnet_test(root, project, runtime, args, trx_name, results_dir, no_build=False):
    """One `dotnet test` of a test project. The test host's own diagnostic log names the runtime it
    loaded ("Runtime location: .../Microsoft.NETCore.App/<version>"), which the report records per run."""
    host, env = S.runtime_env(runtime)
    diag_dir = os.path.join(results_dir, "diag-" + os.path.splitext(trx_name)[0])
    os.makedirs(diag_dir, exist_ok=True)
    cmd = [host, "test", os.path.join(root, project)] + args
    if no_build:
        cmd.append("--no-build")
    # No build servers: MSBuild nodes and the compiler server started with this DOTNET_ROOT must not be
    # reused by builds of other tools on the machine (and theirs not by these runs).
    cmd += ["--disable-build-servers", "-p:UseSharedCompilation=false",
            "--logger", "trx;LogFileName=" + trx_name, "--results-directory", results_dir,
            "--diag:" + os.path.join(diag_dir, "vstest.log"),
            "--", "RunConfiguration.DotNetHostPath=" + host]
    code, out, err = S.run(cmd, env, timeout=3600, check=False)
    trx = os.path.join(results_dir, trx_name)
    if not os.path.exists(trx):
        raise S.StandError("no test results (%d) for %s:\n%s\n%s" % (code, " ".join(cmd), out[-3000:], err[-2000:]))
    result = parse_trx(trx)
    locations = set()
    for f in os.listdir(diag_dir):
        if ".host." in f:
            with open(os.path.join(diag_dir, f), encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    m = _RUNTIME_LOCATION.search(line)
                    if m:
                        locations.add(m.group(1))
    result["runtime_locations"] = sorted(S.rel(x) for x in locations)
    result["runtime_versions"] = sorted({os.path.basename(x.rstrip("/")) for x in locations})
    result["project"] = project
    return result, show_command(cmd)


def show_command(cmd):
    """A command for reports: the host by name, paths inside the repository relative to its root."""
    root = os.path.abspath(S.WT) + os.sep
    return " ".join([os.path.basename(cmd[0])] + [x.replace(root, "") for x in cmd[1:]])


def parse_trx(path):
    root = ET.parse(path).getroot()
    tests = []
    for r in root.iter("{%s}UnitTestResult" % NS["t"]):
        msg = r.find("t:Output/t:ErrorInfo/t:Message", NS)
        st = r.find("t:Output/t:ErrorInfo/t:StackTrace", NS)
        tests.append({"name": r.get("testName"), "outcome": r.get("outcome"),
                      "message": (msg.text or "") if msg is not None else "",
                      "stack": (st.text or "") if st is not None else ""})
    counters = root.find("t:ResultSummary/t:Counters", NS)
    summary = {k: int(counters.get(k, 0)) for k in ("total", "executed", "passed", "failed")} if counters is not None else {}
    return {"summary": summary, "tests": tests}


_LOC = re.compile(r"in (\S+\.cs):line (\d+)")
_BUT_WAS = re.compile(r"But was:\s+(-?\d+)")
_ASSIGN = re.compile(r"long\s+(\w+)\s*=\s*GC\.GetAllocatedBytesForCurrentThread\(\)\s*-\s*\w+\s*;")


def assert_site(test, copy_root, project=CORE):
    """(file, line, source) of the failing assert, from the stack trace. In an optimized build the
    trace points at `long allocated = GC.GetAllocatedBytesForCurrentThread() - before;` (the value
    never gets its own slot), so the assert on that variable is looked up in the next lines."""
    for m in _LOC.finditer(test["stack"]):
        path, line = m.group(1), int(m.group(2))
        name = os.path.basename(path)
        local = os.path.join(copy_root, project, name)
        if not os.path.exists(local):
            continue
        with open(local, encoding="utf-8") as f:
            lines = f.read().splitlines()
        if not 0 < line <= len(lines):
            continue
        text = lines[line - 1].strip()
        if "Assert." in text:
            return name, line, text
        a = _ASSIGN.search(text)
        if a:
            var = a.group(1)
            for k in range(line, min(len(lines), line + 8)):
                cand = lines[k].strip()
                if cand.startswith("Assert.That(" + var + ","):
                    return name, k + 1, cand
        return name, line, text
    return None, None, None


def failing(result):
    return [t for t in result.get("tests", []) if t["outcome"] == "Failed"]


def jitbox():
    """Empty windows asserted different ways, Debug and Release builds, both runtimes. A second run per
    build and runtime prints the JIT's listing of IsEqualToZero (DOTNET_JitDisasm, release runtimes
    since .NET 7), which shows where the box of the expected 0 is allocated relative to the two reads."""
    rows = []
    for config in ("Debug", "Release"):
        dll = S.build("JitBox", os.path.join(S.WT, "KenseiECS"), "tools", False, configuration=config)
        for rt in ("net8", "net10"):
            host, env = S.runtime_env(rt)
            _, out, _ = S.run([host, dll], env, timeout=300)
            for line in out.splitlines():
                if line.startswith("JITBOX "):
                    row = json.loads(line[7:])
                    row["configuration"] = config
                    row["runtime_name"] = rt
                    row["disasm"] = disasm(dll, rt, config)
                    rows.append(row)
    return rows


def disasm(dll, rt, config):
    """Order of the calls in the JIT listing of JitBox's IsEqualToZero: the two counter reads and the
    allocation helper (CORINFO_HELP_NEWSFAST, the box of 0)."""
    path = os.path.join(S.WORK, "ci", "jitbox-%s-%s.asm" % (config.lower(), rt))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.exists(path):
        os.remove(path)
    host, env = S.runtime_env(rt, {"DOTNET_JitDisasm": "IsEqualToZero", "DOTNET_JitStdOutFile": path})
    S.run([host, dll], env, timeout=300, check=False)
    if not os.path.exists(path):
        return {"available": False}
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    # The first listing (Tier0 or FullOpts) of the method.
    parts = text.split("; Assembly listing for method")
    listing = ("; Assembly listing for method" + parts[1]) if len(parts) > 1 else text
    order = []
    for line in listing.splitlines():
        if "GetAllocatedBytesForCurrentThread" in line:
            order.append("read")
        elif "CORINFO_HELP_NEWSFAST" in line or "CORINFO_HELP_BOX" in line:
            order.append("alloc")
    box_inside = False
    if order.count("read") >= 2 and "alloc" in order:
        first = order.index("read")
        second = order.index("read", first + 1)
        box_inside = any(x == "alloc" for x in order[first + 1:second])
    code = [l for l in listing.splitlines() if l.strip() and not l.lstrip().startswith(";")]
    head = listing.splitlines()[0] if listing else ""
    return {"available": True, "order": order, "box_between_reads": box_inside, "kind": head.strip(),
            "listing": "\n".join(code[:40]), "file": S.rel(path)}


def main(args, mode, report_dir=None):
    src = os.path.abspath(args.src)
    flavor = args.flavor or S.default_flavor(src)
    tree = S.tree_of(src)
    commit = S.tree_commit(src, flavor)
    v8 = S.ensure_mixed_root()
    copy_root = os.path.join(S.OUT, "ci", flavor)
    diag_root = os.path.join(S.OUT, "ci", flavor + "-assertzero")
    results_dir = os.path.join(S.WORK, "ci", flavor)
    report_dir = report_dir or os.path.join(S.REPORTS, flavor + "-ci")
    started = datetime.datetime.now()
    runs, commands, skipped = {}, {}, {}
    with S.Lock("ci-" + flavor):
        if os.path.exists(results_dir):
            shutil.rmtree(results_dir)
        os.makedirs(results_dir)
        copied = copy_tree(tree, copy_root, commit)
        if CORE not in copied["copied"] + copied["from_git"]:
            raise S.StandError("no KenseiECS.Tests next to %s (nor in git archive of %s)" % (src, commit or "an unknown commit"))
        has_gen = GEN in copied["copied"] + copied["from_git"] and "KenseiECS.Generators" in copied["copied"] + copied["from_git"]
        S.log("ci-parity %s: copy in %s (%s), .NET 8 runtime %s" % (flavor, S.rel(copy_root), json.dumps(copied), v8))
        # The CI sequence: build release, test it without building, then KENSEI_DEBUG (rebuilds).
        host, env = S.runtime_env("net8")
        for project in [CORE] + ([GEN] if has_gen else []):
            S.run([host, "build", os.path.join(copy_root, project), "-c", "Release", "-nologo", "-v:q",
                   "--disable-build-servers", "-p:UseSharedCompilation=false"], env, timeout=3600)
        controls = CONTROLS_FULL if mode == "full" else CONTROLS_QUICK
        for name, project, rt, a, _ in CI_RUNS + controls:
            if project == GEN and not has_gen:
                skipped[name] = "the tree has no KenseiECS.Generators.Tests"
                continue
            no_build = name in ("ci-release", "ci-gen-release")
            runs[name], commands[name] = dotnet_test(copy_root, project, rt, a, name + ".trx", results_dir, no_build=no_build)
            S.log("%s: %s" % (name, runs[name]["summary"]))

        # Diagnostic copy: only the failing allocation asserts change from Is.EqualTo(0) to Is.Zero.
        sites = {}
        for name in ("ci-release", "ci-kdebug"):
            for t in failing(runs[name]):
                f, line, text = assert_site(t, copy_root)
                if f and re.search(r"Is\.EqualTo\(0L?\)", text or "") and _BUT_WAS.search(t["message"]):
                    sites.setdefault((f, line), text)
        diag = {}
        if sites:
            copy_tree(tree, diag_root, commit, parts=("KenseiECS", "KenseiECS.Tests"))
            for (f, line), text in sites.items():
                p = os.path.join(diag_root, CORE, f)
                with open(p, encoding="utf-8") as fh:
                    lines = fh.read().split("\n")
                lines[line - 1] = re.sub(r"Is\.EqualTo\(0L?\)", "Is.Zero", lines[line - 1], count=1)
                with open(p, "w", encoding="utf-8") as fh:
                    fh.write("\n".join(lines))
            diag["ci-release"], _ = dotnet_test(diag_root, CORE, "net8", ["-c", "Release"], "diag-release.trx", results_dir)
            diag["ci-kdebug"], _ = dotnet_test(diag_root, CORE, "net8", ["-c", "Release", "-p:KenseiDebug=true"], "diag-kdebug.trx", results_dir)
    jb = jitbox()
    return write(flavor, src, tree, mode, runs, commands, skipped, copied, diag, sites, jb, report_dir, started, v8, commit)


def classify(test_name, project, runs, diag, copy_root):
    cfgs = [c for c in CI_NAMES if c in runs and runs[c]["project"] == project and any(t["name"] == test_name for t in failing(runs[c]))]
    t = next(x for x in failing(runs[cfgs[0]]) if x["name"] == test_name)
    f, line, text = assert_site(t, copy_root, project)
    was = _BUT_WAS.search(t["message"])
    same_project = [c for c in runs if runs[c]["project"] == project]
    passes_in = [c for c in same_project if c not in CI_NAMES and any(x["name"] == test_name and x["outcome"] == "Passed" for x in runs[c]["tests"])]
    fails_in = [c for c in same_project if any(x["name"] == test_name for x in failing(runs[c]))]
    diag_outcomes = {c: next((x["outcome"] for x in diag[c]["tests"] if x["name"] == test_name), None) for c in cfgs if c in diag}
    artifact = bool(project == CORE and was and was.group(1) == "24" and text and re.search(r"Is\.EqualTo\(0L?\)", text)
                    and diag_outcomes and all(v == "Passed" for v in diag_outcomes.values()))
    local_fail = any(c in LOCAL_RUNS for c in fails_in)
    cause_class = "test-measurement-artifact" if artifact else ("fails-locally-too" if local_fail else "ci-only")
    return {"configs": cfgs, "test": t, "assert": {"file": f, "line": line, "code": text}, "passes_in": passes_in,
            "fails_in": fails_in, "diagnostic_is_zero_outcome": diag_outcomes, "cause_class": cause_class,
            "locally_green": any(c in LOCAL_RUNS for c in passes_in) and not local_fail}


def write(flavor, src, tree, mode, runs, commands, skipped, copied, diag, sites, jb, report_dir, started, v8, commit):
    shutil.rmtree(os.path.join(report_dir, "repro"), ignore_errors=True)
    os.makedirs(os.path.join(report_dir, "repro"), exist_ok=True)
    copy_root = os.path.join(S.OUT, "ci", flavor)
    findings = []
    seen = []
    for c in CI_NAMES:
        if c not in runs:
            continue
        for t in failing(runs[c]):
            key = (runs[c]["project"], t["name"])
            if key not in seen:
                seen.append(key)
    for project, test_name in seen:
        k = classify(test_name, project, runs, diag, copy_root)
        t = k["test"]
        cause_class = k["cause_class"]
        fid = "alloc.ci." + ("" if project == CORE else "gen.") + test_name
        item = {
            "id": fid, "class": "ci-parity", "status": "finding", "project": project,
            "rule": "a test that is green locally (dotnet test in Debug) is green in CI too (.NET 8, -c Release, with and without KENSEI_DEBUG)",
            "severity": SEVERITY[cause_class],
            "test": test_name, "configs": k["configs"], "message": t["message"].strip(),
            "assert": k["assert"], "fails_in": k["fails_in"], "passes_in": k["passes_in"], "locally_green": k["locally_green"],
            "diagnostic_is_zero_outcome": k["diagnostic_is_zero_outcome"],
            "cause": CAUSE_TEXT[cause_class], "cause_class": cause_class,
            "signature": "ci:%s:%s:%s" % (project, test_name, cause_class),
            "seed": 0, "flavor": flavor,
            "command": "; ".join(commands[c] for c in k["configs"]),
        }
        if cause_class == "test-measurement-artifact":
            # The library allocates nothing in the window (the diagnostic copy with Is.Zero passes): a defect of the test,
            # not of the library's contract. Its own status, outside the findings (catalog entry A5).
            item["status"] = "test-artifact"
            item["class"] = "test-artifact"
            path = os.path.join(report_dir, "repro", fid + ".cs")
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(REPRO_24 % {"test": test_name, "file": k["assert"]["file"], "line": k["assert"]["line"], "code": k["assert"]["code"],
                                     "cfg": ", ".join(k["configs"]), "message": t["message"].strip().replace("\n", " ")})
            item["repro"] = os.path.relpath(path, report_dir)
        else:
            item["repro"] = None
            item["repro_note"] = "the failing test itself: %s" % commands[k["configs"][0]]
        findings.append(item)

    doc = {"module": S.MODULE, "format": 2, "mode": "ci-parity", "flavor": flavor, "src": src, "src_rel": S.rel(src),
           "commit": commit, "tree_copy": S.rel(copy_root), "copied": copied,
           "dotnet8": v8, "generated": started.astimezone().isoformat(timespec="seconds"),
           "runs": {k: {"project": v["project"], "summary": v["summary"], "failed": [t["name"] for t in failing(v)],
                        "command": commands.get(k), "runtime_versions": v.get("runtime_versions"),
                        "runtime_locations": v.get("runtime_locations")}
                    for k, v in runs.items()},
           "skipped": skipped,
           "diagnostic_assert_zero": {k: {"summary": v["summary"], "failed": [t["name"] for t in failing(v)]} for k, v in diag.items()},
           "diagnostic_lines": ["%s:%d: %s" % (f, l, t) for (f, l), t in sorted(sites.items())],
           "jitbox": jb, "findings": [f for f in findings if f["status"] == "finding"],
           # Red CI tests whose cause is the assert, not the library (status "test-artifact", catalog entry A5).
           "test_artifacts": [f for f in findings if f["status"] == "test-artifact"]}
    os.makedirs(report_dir, exist_ok=True)
    with open(os.path.join(report_dir, "report.json"), "w", encoding="utf-8") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=1)
    with open(os.path.join(report_dir, "report.md"), "w", encoding="utf-8") as fh:
        fh.write(markdown(doc))
    S.log("report: " + S.rel(os.path.join(report_dir, "report.md")))
    return 0


REPRO_24 = """// Repro of a CI-only failure (%(cfg)s): %(test)s
// %(file)s:%(line)s: %(code)s
// Failure: %(message)s
// The library allocates nothing here; the assert does. Minimal form, no KenseiECS needed:
using System;
using NUnit.Framework;

public class BoxInWindow {
    [Test] // fails with "But was: 24" in -c Release (both .NET 8 and .NET 10), passes in -c Debug
    public void EmptyWindow_IsEqualToZero() {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Release: Roslyn keeps `allocated` on the stack, the JIT allocates the box of 0 for
        // Is.EqualTo(object) before the second read, inside the window.
        Assert.That(allocated, Is.EqualTo(0));
    }

    [Test] // passes in every configuration: Is.Zero boxes nothing at the call site
    public void EmptyWindow_IsZero() {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero);
    }
}
"""


def markdown(doc):
    L = []
    w = L.append
    w("# Stand/alloc --ci-parity: %s" % doc["flavor"])
    w("")
    w("- Tree: `%s`%s; build copy: `%s` (copied %s%s%s)" % (
        doc["src_rel"], (", commit %s" % doc["commit"][:12]) if doc.get("commit") else "", doc["tree_copy"],
        ", ".join(doc["copied"]["copied"]) or "-",
        ("; from git archive: " + ", ".join(doc["copied"]["from_git"])) if doc["copied"]["from_git"] else "",
        ("; missing: " + ", ".join(doc["copied"]["missing"])) if doc["copied"]["missing"] else ""))
    w("- .NET 8: the official runtime %s (Stand/out/tools/dotnet8); .NET 10: the local one. Generated: %s" % (doc["dotnet8"], doc["generated"]))
    w("")
    w("## Runs")
    w("")
    w("| run | project | testhost runtime (from --diag) | passed | failed | total | command |")
    w("|---|---|---|---|---|---|---|")
    for name, r in doc["runs"].items():
        s = r["summary"]
        w("| %s | %s | %s | %s | %s | %s | `%s` |" % (name, r["project"], ", ".join(r.get("runtime_versions") or ["?"]), s.get("passed"),
                                                     s.get("failed"), s.get("total"), r.get("command")))
    for name, why in doc["skipped"].items():
        w("| %s | | | not run: %s | | | |" % (name, why))
    w("")
    for name, r in doc["runs"].items():
        if r["failed"]:
            w("**%s: failed tests:** %s" % (name, ", ".join("`%s`" % x for x in r["failed"])))
            w("")
    w("## Summary")
    w("")
    for name in CI_NAMES:
        r = doc["runs"].get(name)
        if r:
            w("- %s (%s): %s of %s failed; testhost runtime %s." % (name, r["project"], r["summary"].get("failed"), r["summary"].get("total"),
                                                                  ", ".join(r.get("runtime_versions") or []) or "unknown"))
    by = {}
    for f in doc["findings"] + doc.get("test_artifacts", []):
        by[f["cause_class"]] = by.get(f["cause_class"], 0) + 1
    w("- Red tests by cause: %s." % (", ".join("%s %d" % kv for kv in sorted(by.items())) or "none"))
    if doc["findings"]:
        w("- Green locally (Debug, as `dotnet test` without `-c` and IDEs run them): %d of %d red in CI." % (
            sum(1 for f in doc["findings"] if f["locally_green"]), len(doc["findings"])))
    if "net10-release" in doc["runs"] and any("ci-release" in f["configs"] for f in doc["findings"]):
        n10 = set(doc["runs"]["net10-release"]["failed"])
        rel = [f for f in doc["findings"] if "ci-release" in f["configs"]]
        w("- On .NET 10 in Release the same tests fail: %d of %d red in CI release, so it does not depend on the runtime." % (
            sum(1 for f in rel if f["test"] in n10), len(rel)))
    w("")
    w("## Findings: red in CI")
    w("")
    if not doc["findings"]:
        w("None%s." % (": every test passes in the CI configurations on .NET 8" if not doc.get("test_artifacts") else
                        " beyond the test artifacts below"))
        w("")
    arts = doc.get("test_artifacts") or []
    for f in doc["findings"] + ([None] if arts else []) + arts:
        if f is None:
            w("## Test artifacts: red in CI, not a defect of the library")
            w("")
            w("The assert of the test allocates inside its own measuring window (the box of the expected `0`); the diagnostic copy "
              "with `Is.Zero` in that line passes, so the library allocates nothing there. Status `test-artifact` (catalog entry A5): "
              "a defect of the test suite, not a broken promise of the documents.")
            w("")
            continue
        w("### %s" % f["id"])
        w("")
        w("- Test `%s` (%s), red in: %s; rule: %s; severity: %s" % (f["test"], f["project"], ", ".join(f["configs"]), f["rule"], f["severity"]))
        w("- Message: `%s`" % f["message"].replace("\n", " ").replace("  ", " "))
        if f["assert"]["file"]:
            w("- Assert: `%s:%s`: `%s`" % (f["assert"]["file"], f["assert"]["line"], f["assert"]["code"]))
        w("- Fails in: %s; passes in: %s" % (", ".join(f["fails_in"]) or "-", ", ".join(f["passes_in"]) or "-"))
        if f["project"] == CORE:
            w("- Diagnostic copy (only this assert: Is.EqualTo(0) -> Is.Zero): %s" % (
                ", ".join("%s: %s" % kv for kv in f["diagnostic_is_zero_outcome"].items()) or "not run"))
        w("- Cause: **%s** (`%s`)" % (f["cause"], f["cause_class"]))
        if f.get("repro"):
            w("- Repro: `%s`" % f["repro"])
        w("- Rerun: `Stand/alloc/run.sh --ci-parity --src %s --flavor %s`" % (doc["src_rel"], doc["flavor"]))
        w("")
    w("## Where «But was: 24» comes from")
    w("")
    w("An empty measured window (nothing between the two counter reads), checked different ways (Stand/alloc/JitBox, NUnit 3.14.0):")
    w("")
    w("| build | runtime | Is.EqualTo(0) | Is.EqualTo(0L) | Is.Zero | without NUnit, object parameter | the stand's meter |")
    w("|---|---|---|---|---|---|---|")
    for r in doc["jitbox"]:
        w("| %s | %s | %s | %s | %s | %s | %s |" % (r["configuration"], r["runtime"], r["is_equal_to_0"], r["is_equal_to_0L"],
                                                  r["is_zero"], r["replica_object_param"], r["meter_empty"]))
    w("")
    w("JIT listing of `IsEqualToZero` (DOTNET_JitDisasm), order of the calls: read = `GC.GetAllocatedBytesForCurrentThread()`, "
      "alloc = `CORINFO_HELP_NEWSFAST` (the box of `0`):")
    w("")
    for r in doc["jitbox"]:
        d = r.get("disasm") or {}
        if d.get("available"):
            w("- %s, %s: %s -> the box is %s the window (%s)" % (r["configuration"], r["runtime"], " -> ".join(d["order"]),
                                                               "**inside**" if d["box_between_reads"] else "after", d.get("kind", "")))
    rel8 = next((r for r in doc["jitbox"] if r["configuration"] == "Release" and r["runtime_name"] == "net8"
                 and (r.get("disasm") or {}).get("available")), None)
    if rel8:
        w("")
        w("```")
        w(rel8["disasm"].get("kind", ""))
        w(rel8["disasm"]["listing"])
        w("```")
    w("")
    w("Conclusion: 24 B is the box of the expected value `0` (an Int32 on the heap, 24 B on a 64-bit platform) for "
      "`Is.EqualTo(object)`. In the optimized test build (`-c Release`) the C# compiler gives `allocated` no slot: the "
      "difference stays on the IL evaluation stack as the first argument of `Assert.That`, and when the JIT imports the "
      "`box` it spills the allocation into its own statement before the argument that is not yet evaluated, that is before "
      "the second `GC.GetAllocatedBytesForCurrentThread()`: inside the window (see the listing above, already at Tier0). In "
      "the Debug build `allocated` is stored in a local before `Is.EqualTo(0)`, and the test is green: that is how the tests "
      "run locally (`dotnet test` without `-c`, IDEs). It does not depend on the runtime (.NET 8 and .NET 10, arm64 and x64 "
      "alike): CI is red because it runs `-c Release`. The library allocates nothing in these windows: the diagnostic copy "
      "with `Is.Zero` in the failing lines passes.")
    if doc["diagnostic_lines"]:
        w("")
        w("Lines changed in the diagnostic copy: " + "; ".join("`%s`" % x for x in doc["diagnostic_lines"]))
        for k, v in doc["diagnostic_assert_zero"].items():
            w("- diagnostic %s: passed %s, failed %s %s" % (k, v["summary"].get("passed"), v["summary"].get("failed"),
                                                           ("(" + ", ".join(v["failed"]) + ")") if v["failed"] else ""))
    w("")
    return "\n".join(L) + "\n"
