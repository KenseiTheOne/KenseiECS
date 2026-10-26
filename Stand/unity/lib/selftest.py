#!/usr/bin/env python3
"""Self-test of the unity module: does it catch injected defects, and does it stay quiet on corrected
behaviour?

Each mutant is a patch in Stand/unity/mutants/<name>.patch (a unified diff with paths relative to the
package folder, after a header of "Key: value" lines). The self-test applies it to a temporary copy of
the tree under test (Stand/out/unity/selftest/trees/<name>/KenseiECS; the tree itself is never touched)
and runs the module on that copy with the cases the mutant names, against a baseline run of the
unmutated tree with the same cases:

  Kind: bug   an injected defect (a rollback of a fix, or the same class of defect in a new place).
              Killed only when the module reports a finding of a rule listed in "Expect" that the baseline
              does not have (a new signature) and the C# repro of that finding, run in Unity against the
              mutated package, reproduces it (the mutant runs verify their repros; a finding whose kind has
              no Unity repro counts without one). Otherwise: KILLED-NO-REPRO (no expected finding reproduced),
              KILLED-OTHER-RULE (new findings, none of an expected rule) or SURVIVED, all failures.
  Kind: fix   an emulated fix of a known finding. Accepted when the findings of the rules listed in
              "Removes" are gone and nothing new appears (no false positive on the fixed behaviour).

A mutant may name the contract versions of the trees whose code it is for (Since: / Until:, Version-note: why). The
contract version of the tree is its package.json version, or the next patch when its CHANGELOG starts with a non-empty
[Unreleased] section and package.json is not above the last release there. A mutant whose range excludes the tree is
n/a ("contract version ...") and listed; within its range a patch that does not apply is not-applicable, a failure
(refresh it, or give it an Until and cut a twin for the new code). All mutant runs share
one scratch project (flavor "selftest"), so only the package recompiles between them; one Unity process
at a time (the module's lock), each with the module's per-process timeout (--timeout, default 900 s).

Usage: Stand/unity/run.sh --selftest [--src <KenseiECS folder>] [--flavor <label>] [--debug] [--only a,b]
                                     [--timeout <seconds>] [--unity <editor>] [--unity-version <v>]
                                     [--target <t>] [--addressables <v>] [--keep-trees]
Default --src: KenseiECS/ next to Stand/. Report: Stand/out/reports/unity/selftest-<flavor>-<rel|dbg>/
selftest.md and selftest.json, with the module report of every run under runs/.
Exit code: 0 every bug mutant killed and every fix accepted, 1 otherwise, 2 the baseline run failed.
"""
import argparse
import glob
import json
import os
import platform
import re
import shutil
import signal
import stat
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
MODULE = os.path.dirname(HERE)
STAND = os.path.dirname(MODULE)
ROOT = os.path.dirname(STAND)
OUT = os.path.join(STAND, "out")
WORK_FLAVOR = "selftest"
# Kinds whose finding has a C# repro the module runs in Unity (lib/report.py, VERIFIABLE).
VERIFIABLE_KINDS = ("player", "sbp-compile", "legacy-bundles", "sbp-bundles", "aa-content", "editor-spawn")


# ------------------------------------------------------------------------------------------- patches


def read_mutant(path):
    text = open(path, encoding="utf-8").read()
    head = text.split("\n--- ", 1)[0]
    meta = {"name": os.path.splitext(os.path.basename(path))[0], "file": os.path.relpath(path, STAND), "text": text}
    for line in head.splitlines():
        m = re.match(r"^([A-Z][A-Za-z-]+):\s*(.*)$", line)
        if m:
            meta[m.group(1).lower()] = m.group(2).strip()
    meta["kind"] = meta.get("kind", "bug")
    meta["cases"] = [c for c in re.split(r"[;,\s]+", meta.get("cases", "")) if c]
    meta["expect"] = [r for r in re.split(r"[;,\s]+", meta.get("expect", "")) if r]
    meta["removes"] = [r for r in re.split(r"[;,\s]+", meta.get("removes", "")) if r]
    meta["configs"] = [c for c in re.split(r"[;,\s]+", meta.get("configs", "")) if c]
    return meta


def _vparse(v):
    core = re.split(r"[-+]", v or "")[0].split(".")
    out = []
    for i in range(3):
        try:
            out.append(int(core[i]))
        except (IndexError, ValueError):
            out.append(0)
    return tuple(out)


def contract_version(pkg):
    """The version whose documents the package folder has: package.json, or the next patch when CHANGELOG.md starts
    with a non-empty [Unreleased] section and package.json is not above the CHANGELOG's last release (a working copy
    between releases); a package.json above that release is the release in the making itself."""
    try:
        with open(os.path.join(pkg, "package.json"), encoding="utf-8") as f:
            version = json.load(f).get("version")
    except (OSError, ValueError):
        return None
    try:
        with open(os.path.join(pkg, "CHANGELOG.md"), encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return version
    heads = re.findall(r"^## \[([^\]]+)\]", text, re.M)
    if version and heads and heads[0].strip().lower() == "unreleased":
        body = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.M | re.S | re.I)
        last = next((h for h in heads[1:] if re.match(r"\d", h)), None)
        if body and body.group(1).strip() and (not last or _vparse(version) <= _vparse(last)):
            x = _vparse(version)
            return "%d.%d.%d" % (x[0], x[1], x[2] + 1)
    return version


def version_skip(meta, version):
    """None when the mutant (header fields since/until) is meant for trees of this contract version, else why not."""
    if not version:
        return None
    v = _vparse(version)
    why = ""
    if meta.get("since") and v < _vparse(meta["since"]):
        why = "contract version %s: the mutant is for %s and newer (it changes code of those versions)" % (version, meta["since"])
    elif meta.get("until") and v > _vparse(meta["until"]):
        why = "contract version %s: the mutant is for %s and older (it changes code that later versions rewrote)" % (version, meta["until"])
    if why and meta.get("version-note"):
        why += "; " + meta["version-note"]
    return why or None


def parse_patch(text):
    """Unified diff -> [{"path", "hunks": [{"start", "old": [lines], "new": [lines]}]}]."""
    lines = text.splitlines(keepends=True)
    files, cur, i = [], None, 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            old = line[4:].split("\t")[0].strip()
            new = lines[i + 1][4:].split("\t")[0].strip()
            path = new if new != "/dev/null" else old
            if path.startswith(("a/", "b/")):
                path = path[2:]
            cur = {"path": path, "hunks": [], "create": old == "/dev/null", "delete": new == "/dev/null"}
            files.append(cur)
            i += 2
            continue
        m = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
        if m and cur is not None:
            old_len = int(m.group(2)) if m.group(2) is not None else 1
            new_len = int(m.group(4)) if m.group(4) is not None else 1
            hunk = {"start": int(m.group(1)), "old": [], "new": []}
            i += 1
            o = n = 0
            last = None
            while i < len(lines) and (o < old_len or n < new_len or lines[i].startswith("\\")):
                h = lines[i]
                if h.startswith("\\"):
                    for side in (("old", "new") if last == "both" else (last,) if last else ()):
                        hunk[side][-1] = hunk[side][-1].rstrip("\r\n")
                    i += 1
                    continue
                tag, body = h[:1], h[1:]
                if tag == " " or h in ("\n", "\r\n"):
                    body = body if tag == " " else h
                    hunk["old"].append(body)
                    hunk["new"].append(body)
                    o += 1
                    n += 1
                    last = "both"
                elif tag == "-":
                    hunk["old"].append(body)
                    o += 1
                    last = "old"
                elif tag == "+":
                    hunk["new"].append(body)
                    n += 1
                    last = "new"
                else:
                    raise ValueError("unexpected line in a hunk of %s: %r" % (cur["path"], h))
                i += 1
            cur["hunks"].append(hunk)
            continue
        i += 1
    return files


def apply_patch(text, tree):
    """Applies a unified diff to the files under <tree>; exact context, any line offset. None or the problem."""
    try:
        files = parse_patch(text)
    except ValueError as e:
        return str(e)
    if not files:
        return "no file in the patch"
    for f in files:
        path = os.path.join(tree, f["path"])
        if f["create"]:
            content = []
        else:
            if not os.path.isfile(path):
                return "%s is not in this tree" % f["path"]
            content = open(path, encoding="utf-8", newline="").read().splitlines(keepends=True)
        # delta: how far the current content is shifted against the line numbers of the patch.
        delta = 0
        for h in f["hunks"]:
            old = h["old"]
            # "@@ -N,0" inserts after line N; otherwise the hunk starts at line N (1-based).
            base_index = h["start"] if not old else h["start"] - 1
            if not old:
                pos = min(max(base_index + delta, 0), len(content))
            else:
                expected = base_index + delta
                pos = None
                stripped = [x.rstrip("\r\n") for x in old]
                for p in sorted(range(len(content) - len(old) + 1), key=lambda p: abs(p - expected)):
                    window = content[p:p + len(old)]
                    if window == old or [x.rstrip("\r\n") for x in window] == stripped:
                        pos = p
                        break
                if pos is None:
                    return "a hunk at line %d of %s does not match this tree" % (h["start"], f["path"])
            content[pos:pos + len(old)] = h["new"]
            delta = (pos - base_index) + len(h["new"]) - len(old)
        if f["delete"]:
            os.remove(path)
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="") as out:
                out.write("".join(content))
    return None


# ------------------------------------------------------------------------------------------- runs


def make_writable(root):
    for d, dirs, files in os.walk(root):
        os.chmod(d, stat.S_IRWXU | stat.S_IRGRP | stat.S_IXGRP)
        for f in files:
            p = os.path.join(d, f)
            if not os.path.islink(p):
                os.chmod(p, stat.S_IRUSR | stat.S_IWUSR | stat.S_IRGRP)


def fresh_copy(src, dest):
    if os.path.exists(dest):
        make_writable(dest)
        shutil.rmtree(dest)
    shutil.copytree(src, dest, symlinks=True)
    make_writable(dest)


def docs_of(src):
    d = os.path.join(os.path.dirname(src), "docs")
    return d if os.path.isdir(d) else None


def seed_project(src_flavor, unity_version):
    """The shared scratch project starts as a copy of a warm project of the same Unity version (the import
    cache of Library), so the first self-test run does not import everything from scratch."""
    dest = os.path.join(OUT, "unity", WORK_FLAVOR, "Proj-" + unity_version)
    if os.path.isdir(dest) or not unity_version:
        return None
    candidates = [os.path.join(OUT, "unity", src_flavor, "Proj-" + unity_version)] + \
        sorted(glob.glob(os.path.join(OUT, "unity", "*", "Proj-" + unity_version)))
    for c in candidates:
        if os.path.isdir(os.path.join(c, "Library")) and os.path.dirname(c) != os.path.dirname(dest):
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            ignore = shutil.ignore_patterns("Temp", "Logs", "KenseiStandRepro*")
            if platform.system() == "Darwin":
                # APFS clone: no data is copied.
                if subprocess.call(["cp", "-cR", c, dest], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0:
                    for junk in ("Temp", "Logs"):
                        shutil.rmtree(os.path.join(dest, junk), ignore_errors=True)
                    return c
                shutil.rmtree(dest, ignore_errors=True)
            shutil.copytree(c, dest, symlinks=True, ignore=ignore)
            return c
    return None


def run_module(args, src, cases, docs, log, verify=False):
    cmd = [os.path.join(MODULE, "run.sh"), "--quick", "--src", src, "--flavor", WORK_FLAVOR, "--cases", ";".join(cases),
           "--timeout", str(args.timeout)]
    if not verify:
        cmd.append("--no-verify-repro")
    if args.debug:
        cmd.append("--debug")
    if docs:
        cmd += ["--docs", docs]
    for opt in ("unity", "unity_version", "target", "addressables"):
        v = getattr(args, opt)
        if v:
            cmd += ["--" + opt.replace("_", "-"), v]
    started = time.time()
    with open(log, "w", encoding="utf-8") as out:
        out.write("# " + " ".join(cmd) + "\n\n")
        out.flush()
        p = subprocess.Popen(cmd, stdout=out, stderr=subprocess.STDOUT, start_new_session=(os.name == "posix"))
        # A module run is a setup process and one per group, each limited by --timeout; the whole run gets
        # room for four of them.
        try:
            code = p.wait(timeout=args.timeout * 4 + 120)
        except subprocess.TimeoutExpired:
            if os.name == "posix":
                os.killpg(p.pid, signal.SIGTERM)
            else:
                p.terminate()
            try:
                p.wait(timeout=60)
            except subprocess.TimeoutExpired:
                if os.name == "posix":
                    os.killpg(p.pid, signal.SIGKILL)
                else:
                    p.kill()
            code = 124
    report_dir = os.path.join(OUT, "reports", "unity", WORK_FLAVOR + "-" + ("dbg" if args.debug else "rel"))
    report = None
    if code in (0, 1):
        try:
            report = json.load(open(os.path.join(report_dir, "report.json"), encoding="utf-8"))
        except (OSError, ValueError):
            report = None
    return code, report, report_dir, round(time.time() - started)


def keep_run(report_dir, dest):
    if os.path.isdir(dest):
        shutil.rmtree(dest)
    if os.path.isdir(report_dir):
        shutil.copytree(report_dir, dest, ignore=shutil.ignore_patterns("unity-*.log"))


def facts(report, cases):
    """Finding signatures, question signatures and case verdicts of a module report, for the given cases."""
    want = set(cases)
    sigs = {f["signature"]: f for f in report.get("findings") or [] if want & set(f.get("cases") or [])}
    qs = {q.get("signature") or q.get("id"): q for q in report.get("contractQuestions") or [] if want & set(q.get("cases") or [])}
    verdicts = {c["id"]: c.get("verdict") for c in report.get("cases") or [] if c["id"] in want}
    return sigs, qs, verdicts


def judge(m, base, mut):
    bsig, bq, bver = facts(base, m["cases"])
    msig, mq, mver = facts(mut, m["cases"])
    new = [f for s, f in msig.items() if s not in bsig]
    new_q = [q for s, q in mq.items() if s not in bq]
    gone = [f for s, f in bsig.items() if s not in msig]
    worse = sorted(c for c in m["cases"] if bver.get(c) == "ok" and mver.get(c) == "finding")
    not_run = sorted(c for c in m["cases"] if mver.get(c) in (None, "na", "skipped", "inconclusive"))
    detail = []
    if new:
        detail.append("new findings: " + ", ".join("%s (%s)" % (f["id"], f["signature"]) for f in new))
    if new_q:
        detail.append("new questions: " + ", ".join(q.get("id", "?") for q in new_q))
    if gone:
        detail.append("gone findings: " + ", ".join(f["id"] for f in gone))
    if worse:
        detail.append("cases ok -> finding: " + ", ".join(worse))
    if not_run:
        detail.append("cases without a verdict: " + ", ".join("%s %s" % (c, mver.get(c) or "missing") for c in not_run))
    if m["kind"] == "fix":
        target = [f for f in bsig.values() if f["rule"] in m["removes"]]
        still = [f for f in msig.values() if f["rule"] in m["removes"]]
        if not target:
            return "n/a", "the baseline has no finding of %s in these cases: nothing for the fix to remove" % ", ".join(m["removes"]), new, new_q
        ok = not still and not new and not new_q and not [c for c in not_run if bver.get(c) not in (None, "na", "skipped")]
        return ("fix-accepted" if ok else "FIX-REJECTED"), "; ".join(detail) or "nothing changed", new, new_q
    expected = [f for f in new if f["rule"] in m["expect"]]
    # A finding of a kind the stand reproduces in Unity counts only with its repro reproduced; one without such a
    # repro (no build or Spawn case) counts by the case itself.
    reproduced = [f for f in expected if (f.get("reproVerified") or {}).get("reproduced")]
    without_repro = [f for f in expected if (f.get("params") or {}).get("kind") not in VERIFIABLE_KINDS]
    if expected:
        detail.append("repro reproduced: " + (", ".join(f["id"] for f in reproduced) if reproduced else
                                              "no Unity repro for " + ", ".join(f["id"] for f in without_repro) if without_repro else "NONE"))
    if reproduced or without_repro:
        status = "killed"
    elif expected:
        status = "KILLED-NO-REPRO"
    elif new or worse:
        status = "KILLED-OTHER-RULE"
    else:
        status = "SURVIVED"
        if not detail:
            detail.append("the module reports the same as on the baseline")
    return status, "; ".join(detail), new, new_q


def main():
    ap = argparse.ArgumentParser(description="Mutation self-test of the unity module")
    ap.add_argument("--src", default=os.path.join(ROOT, "KenseiECS"))
    ap.add_argument("--flavor", default=None)
    ap.add_argument("--debug", action="store_true")
    ap.add_argument("--only", default=None)
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--unity", default=None)
    ap.add_argument("--unity-version", dest="unity_version", default=None)
    ap.add_argument("--target", default=None)
    ap.add_argument("--addressables", default=None)
    ap.add_argument("--keep-trees", action="store_true")
    a = ap.parse_args()
    src = os.path.abspath(a.src)
    if not os.path.isfile(os.path.join(src, "package.json")):
        print("selftest: --src %s is not a package folder" % src)
        return 2
    flavor = a.flavor or ("wt" if os.path.realpath(src) == os.path.realpath(os.path.join(ROOT, "KenseiECS")) else os.path.basename(os.path.dirname(src)))
    cfg = "dbg" if a.debug else "rel"
    outdir = os.path.join(OUT, "reports", "unity", "selftest-%s-%s" % (flavor, cfg))
    if os.path.isdir(outdir):
        shutil.rmtree(outdir)
    os.makedirs(os.path.join(outdir, "logs"), exist_ok=True)
    only = set(a.only.split(",")) if a.only else None
    mutants = [read_mutant(p) for p in sorted(glob.glob(os.path.join(MODULE, "mutants", "*.patch")))]
    mutants = [m for m in mutants if not only or m["name"] in only]
    if not mutants:
        print("selftest: no mutants selected")
        return 2
    version = contract_version(src)
    skipped = {m["name"]: version_skip(m, version) for m in mutants}
    docs = docs_of(src)
    started = time.time()

    # The unity version the module will pick, for seeding the shared scratch project.
    env = dict(os.environ)
    if a.unity:
        env["UNITY"] = a.unity
    if a.unity_version:
        env["UNITY_VERSION"] = a.unity_version
    try:
        info = json.loads(subprocess.run([sys.executable, "-I", os.path.join(HERE, "unity.py"), "find", "--target", a.target or "auto"],
                                         stdout=subprocess.PIPE, env=env, text=True, timeout=120).stdout or "{}")
    except (OSError, ValueError, subprocess.SubprocessError):
        info = {}
    seeded = seed_project(flavor, info.get("version", ""))
    if seeded:
        print("selftest: scratch project seeded from %s" % os.path.relpath(seeded, STAND))

    union = []
    for m in mutants:
        if skipped.get(m["name"]):
            continue
        for c in m["cases"]:
            if c not in union:
                union.append(c)
    print("selftest: %s-%s, %d mutant(s), baseline with cases %s" % (flavor, cfg, len(mutants), ";".join(union)))
    code, base, report_dir, secs = run_module(a, src, union, docs, os.path.join(outdir, "logs", "baseline.log"))
    keep_run(report_dir, os.path.join(outdir, "runs", "baseline"))
    if base is None:
        print("selftest: the baseline run failed (exit %s), see %s" % (code, os.path.join(outdir, "logs", "baseline.log")))
        return 2
    base_ids = sorted(f["id"] for f in base.get("findings") or [])
    print("selftest: baseline (%d s): %s" % (secs, ", ".join(base_ids) or "no findings"))

    results = []
    for m in mutants:
        row = {"name": m["name"], "kind": m["kind"], "origin": m.get("origin", ""), "why": m.get("why", ""), "patch": m["file"],
               "cases": m["cases"], "expect": m["expect"] if m["kind"] == "bug" else m["removes"],
               "since": m.get("since"), "until": m.get("until")}
        if skipped.get(m["name"]):
            row.update(status="n/a", detail=skipped[m["name"]])
            results.append(row)
            print("%-28s n/a (%s)" % (m["name"], skipped[m["name"]]))
            continue
        if m["configs"] and cfg not in m["configs"]:
            row.update(status="n/a", detail="the mutant applies to %s only" % "/".join(m["configs"]))
            results.append(row)
            print("%-28s n/a" % m["name"])
            continue
        tree = os.path.join(OUT, "unity", WORK_FLAVOR, "trees", m["name"], "KenseiECS")
        fresh_copy(src, tree)
        problem = apply_patch(m["text"], tree)
        if problem:
            row.update(status="not-applicable", detail=problem)
            results.append(row)
            print("%-28s not applicable: %s" % (m["name"], problem))
            continue
        code, rep, report_dir, secs = run_module(a, tree, m["cases"], docs, os.path.join(outdir, "logs", m["name"] + ".log"), verify=m["kind"] == "bug")
        keep_run(report_dir, os.path.join(outdir, "runs", m["name"]))
        row["seconds"] = secs
        if rep is None:
            row.update(status="run-failed", detail="module exit %s, see logs/%s.log" % (code, m["name"]))
        else:
            status, detail, new, new_q = judge(m, base, rep)
            row.update(status=status, detail=detail, newFindings=[f["signature"] for f in new], newQuestions=[q.get("id") for q in new_q])
        results.append(row)
        print("%-28s %-14s %s" % (m["name"], row["status"], row["detail"]))
        if not a.keep_trees:
            shutil.rmtree(os.path.dirname(tree), ignore_errors=True)

    bugs = [r for r in results if r["kind"] == "bug" and r["status"] in ("killed", "KILLED-NO-REPRO", "KILLED-OTHER-RULE", "SURVIVED", "run-failed")]
    killed = sum(1 for r in bugs if r["status"] == "killed")
    fixes = [r for r in results if r["kind"] == "fix" and r["status"] in ("fix-accepted", "FIX-REJECTED", "run-failed")]
    accepted = sum(1 for r in fixes if r["status"] == "fix-accepted")
    na = [r for r in results if r["status"] == "n/a"]
    # A mutant within its version range whose patch does not apply is a broken self-test.
    broken = [r for r in results if r["status"] == "not-applicable"]
    out = {"module": "unity", "src": src, "flavor": flavor, "config": cfg, "baseline": base_ids, "contractVersion": version,
           "skippedByVersion": [r["name"] for r in na if "contract version" in (r.get("detail") or "")], "errors": len(broken),
           "baselineSignatures": [f["signature"] for f in base.get("findings") or []],
           "killed": killed, "bugMutants": len(bugs), "fixAccepted": accepted, "fixMutants": len(fixes), "notApplicable": len(na),
           "unity": base.get("unity"), "target": base.get("target"), "seconds": round(time.time() - started, 1), "mutants": results}
    with open(os.path.join(outdir, "selftest.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    with open(os.path.join(outdir, "selftest.md"), "w", encoding="utf-8") as md:
        md.write("# Stand module unity: self-test (%s-%s)\n\n" % (flavor, cfg))
        md.write("Mutants are the patches in `Stand/unity/mutants/`, each applied to a temporary copy of the tree under test; the tree is not changed. ")
        md.write("bug: an injected defect (a rollback of a fix, or the same class of defect in a new place), which the module must report as a finding of the expected rule that the baseline run does not have, "
                 "with its C# repro reproducing it in Unity (KILLED-NO-REPRO, KILLED-OTHER-RULE and SURVIVED fail). ")
        md.write("fix: an emulated fix of a known finding, which must remove that finding without anything new (a check for false positives).\n\n")
        md.write("Unity %s, target %s. Baseline: %s. Killed %d of %d bug mutants; accepted %d of %d fix mutants; %d not applicable; %.0f s.\n\n"
                 % (base.get("unity"), base.get("target"), ", ".join(base_ids) or "no findings", killed, len(bugs), accepted, len(fixes), len(na), out["seconds"]))
        md.write("| mutant | kind | origin | cases | expected rules | result | details |\n|---|---|---|---|---|---|---|\n")
        for r in results:
            md.write("| %s | %s | %s | %s | %s | %s | %s |\n" % (r["name"], r["kind"], r["origin"].replace("|", "/"), ", ".join(r["cases"]),
                                                             ", ".join(r["expect"]), r["status"], r.get("detail", "").replace("|", "\\|")))
    print("selftest: %d/%d bug mutants killed, %d/%d fix mutants accepted, %d not applicable -> %s"
          % (killed, len(bugs), accepted, len(fixes), len(na), os.path.join(outdir, "selftest.md")))
    return 0 if killed == len(bugs) and accepted == len(fixes) and not broken else 1


if __name__ == "__main__":
    sys.exit(main())
