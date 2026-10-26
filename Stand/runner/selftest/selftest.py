#!/usr/bin/env python3
"""Mutation self-test of the runner module: does the oracle catch injected defects (in the right
rule, with the right status), and does it stay quiet on corrected behaviour?

Each mutant is a patch in Stand/runner/mutants/*.patch: a header of "Key: value" lines, then a
unified diff against the folder that holds KenseiECS/ (paths a/KenseiECS/..., b/KenseiECS/...; the
patch also applies with `git apply` or `patch -p1` there). The self-test copies the package under
test (--src, default KenseiECS/ of this repository) into a temporary folder under
Stand/out/selftest/runner/, applies the patches named in Base: and then the mutant's own, runs the
module on the copy (--quick, --cases N) and compares its report with the run on a copy that has the
Base: patches only. The sources under test are never touched. The mutant run
compiles and runs its repros (the baseline run does not): a mutant counts as killed only when the repro of
a signature that caught it reproduces.

  bug  killed when a signature (rule @ place) appears that the baseline run does not have, or its
       random hits jump (x3 and at least +20), for one of the rules in Expect: with the status given
       there (defect unless "(question)"), and the repro of one such signature reproduces
       (KILLED-NO-REPRO otherwise, a failure). "Expect: any" takes any new signature or a scenario that
       passed on the baseline and fails now.
  fix  accepted when every signature named in Removes: (a rule, or "RULE @ place-prefix") is gone and
       no new signature and no failing scenario appears: no false positive on the corrected
       behaviour. A fix mutant only checks the oracle; it is not a proposal for the package.

Header keys: Mutant, Kind (bug | fix), Class, Configs (rel, dbg), Contract (a --contract-version for
both runs), Base (mutants applied first, in order), Expect, Removes, Why, and Since / Until (the contract versions
of the trees whose code the patch is for; Version-note says why). The contract version of the tree is its
package.json version, or the next patch when its CHANGELOG starts with a non-empty [Unreleased] section and
package.json is not above the last release there. A mutant whose range excludes the tree is n/a ("contract
version ...") and listed; within its range a patch that does not apply fails (not-applicable): refresh it, or give
it an Until and cut a twin for the new code. Copies are fresh files and their build labels name the tree, so no
build of another tree is taken for up to date.

Usage: Stand/runner/run.sh --selftest [--src DIR] [--flavor LABEL] [--debug | --configs rel,dbg]
                                      [--cases N] [--only NAME,NAME] [--list] [--keep]
Report: Stand/out/reports/runner/selftest-<flavor>-<rel|dbg>/selftest.json and selftest.md
Exit code: 0 every applicable mutant behaved as expected, 1 otherwise, 2 the self-test itself failed.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
MODULE = os.path.dirname(HERE)
STAND = os.path.dirname(MODULE)
MUTANTS_DIR = os.path.join(MODULE, "mutants")
WORK = os.path.join(STAND, "out", "selftest", "runner")
REPORTS = os.path.join(STAND, "out", "reports", "runner")

HEADER_KEYS = ("Mutant", "Kind", "Class", "Configs", "Contract", "Base", "Expect", "Removes", "Why", "Since", "Until", "Version-note")


# ----------------------------------------------------------------------------- mutants

def load_mutants():
    mutants = []
    for fname in sorted(os.listdir(MUTANTS_DIR)):
        if not fname.endswith(".patch"):
            continue
        with open(os.path.join(MUTANTS_DIR, fname), encoding="utf-8", newline="") as f:
            text = f.read()
        head, _, _ = text.partition("\ndiff ")
        meta = {}
        for line in head.splitlines():
            m = re.match(r"^([A-Za-z-]+):\s*(.*)$", line)
            if m and m.group(1) in HEADER_KEYS:
                meta[m.group(1)] = m.group(2).strip()
        name = meta.get("Mutant") or fname[:-6]
        mutants.append({
            "name": name, "file": fname, "patch": text,
            "kind": meta.get("Kind", "bug"), "class": meta.get("Class", ""),
            "configs": [c.strip() for c in meta.get("Configs", "rel, dbg").split(",") if c.strip()],
            "contract": meta.get("Contract") or None,
            "base": [b.strip() for b in meta.get("Base", "").split(",") if b.strip()],
            "expect": [e.strip() for e in meta.get("Expect", "").split(",") if e.strip()],
            "removes": [r.strip() for r in meta.get("Removes", "").split(",") if r.strip()],
            "why": meta.get("Why", ""),
            "since": meta.get("Since") or None, "until": meta.get("Until") or None, "versionNote": meta.get("Version-note") or None,
        })
    return mutants


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


def version_skip(m, version):
    """None when the mutant is meant for trees of this contract version, else why not (shown as n/a)."""
    if not version:
        return None
    v = _vparse(version)
    why = ""
    if m.get("since") and v < _vparse(m["since"]):
        why = "contract version %s: the mutant is for %s and newer (it changes code of those versions)" % (version, m["since"])
    elif m.get("until") and v > _vparse(m["until"]):
        why = "contract version %s: the mutant is for %s and older (it changes code that later versions rewrote)" % (version, m["until"])
    if why and m.get("versionNote"):
        why += "; " + m["versionNote"]
    return why or None


class PatchError(Exception):
    pass


def parse_patch(text):
    """[(path, [(old_start, old_lines, new_lines)])] of a unified diff; paths without the a/ b/ prefix."""
    files = []
    lines = text.splitlines(keepends=True)
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            target = lines[i + 1][4:].strip().split("\t")[0]
            if target.startswith("b/"):
                target = target[2:]
            hunks = []
            i += 2
            while i < len(lines) and lines[i].startswith("@@"):
                m = re.match(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", lines[i])
                if not m:
                    raise PatchError("bad hunk header: " + lines[i].strip())
                old_start = int(m.group(1))
                old_count = int(m.group(2)) if m.group(2) is not None else 1
                new_count = int(m.group(4)) if m.group(4) is not None else 1
                i += 1
                old, new = [], []
                while i < len(lines) and (old_count > 0 or new_count > 0):
                    body = lines[i]
                    if body.startswith("\\"):
                        # "\ No newline at end of file": the patches here always end their lines.
                        i += 1
                        continue
                    tag, content = body[:1], body[1:]
                    if tag == " ":
                        old.append(content)
                        new.append(content)
                        old_count -= 1
                        new_count -= 1
                    elif tag == "-":
                        old.append(content)
                        old_count -= 1
                    elif tag == "+":
                        new.append(content)
                        new_count -= 1
                    elif body in ("\n", "\r\n"):
                        old.append(body)
                        new.append(body)
                        old_count -= 1
                        new_count -= 1
                    else:
                        raise PatchError("bad hunk line: " + body.rstrip())
                    i += 1
                hunks.append((old_start, old, new))
            files.append((target, hunks))
            continue
        i += 1
    if not files:
        raise PatchError("no file in the patch")
    return files


def apply_patch(root, text):
    """Apply a unified diff to the files under root. Each hunk must match exactly once (an offset is fine)."""
    for path, hunks in parse_patch(text):
        full = os.path.join(root, path)
        if not os.path.isfile(full):
            raise PatchError("%s is not in the tree" % path)
        with open(full, encoding="utf-8", newline="") as f:
            content = f.read()
        crlf = "\r\n" in content
        lines = content.replace("\r\n", "\n").splitlines(keepends=True)
        offset = 0
        for old_start, old, new in hunks:
            old = [x.replace("\r\n", "\n") for x in old]
            new = [x.replace("\r\n", "\n") for x in new]
            n = len(old)
            matches = [k for k in range(0, len(lines) - n + 1) if lines[k:k + n] == old]
            if not matches:
                raise PatchError("a hunk of %s (at line %d) does not match this tree" % (path, old_start))
            if len(matches) > 1:
                expected = old_start - 1 + offset
                matches.sort(key=lambda k: abs(k - expected))
                if abs(matches[0] - expected) == abs(matches[1] - expected):
                    raise PatchError("a hunk of %s (at line %d) matches %d places" % (path, old_start, len(matches)))
            at = matches[0]
            lines[at:at + n] = new
            offset += len(new) - n
        out = "".join(lines)
        if crlf:
            out = out.replace("\n", "\r\n")
        os.chmod(full, stat.S_IRUSR | stat.S_IWUSR)
        with open(full, "w", encoding="utf-8", newline="") as f:
            f.write(out)


# ----------------------------------------------------------------------------- trees and runs

def writable(path):
    for root, dirs, files in os.walk(path):
        os.chmod(root, stat.S_IRWXU)
        for f in files:
            os.chmod(os.path.join(root, f), stat.S_IRUSR | stat.S_IWUSR)


def remove_tree(path):
    if os.path.isdir(path):
        writable(path)
        shutil.rmtree(path)


def make_tree(src, dest, patches):
    """Copy the package to dest/KenseiECS and apply the patches in order; None or the reason it failed."""
    remove_tree(dest)
    os.makedirs(dest)
    # Fresh modification times: no build of the same label is ever taken for up to date.
    shutil.copytree(src, os.path.join(dest, "KenseiECS"), copy_function=shutil.copy,
                    ignore=shutil.ignore_patterns("*.meta", "Samples~", "Plugins", "Editor", "Tests", "bin", "obj"))
    writable(dest)
    for name, text in patches:
        try:
            apply_patch(dest, text)
        except PatchError as e:
            return "%s: %s" % (name, e)
    return None


def run_module(tree, flavor, debug, cases, contract, logdir, verify=False):
    cmd = ["bash", os.path.join(MODULE, "run.sh"), "--quick", "--src", os.path.join(tree, "KenseiECS"), "--flavor", flavor,
           "--cases", str(cases), "--report-dir", os.path.join(tree, "report")]
    if not verify:
        cmd.append("--no-verify")
    if debug:
        cmd.append("--debug")
    if contract:
        cmd += ["--contract-version", contract]
    log = os.path.join(logdir, flavor + ".log")
    with open(log, "w") as out:
        code = subprocess.call(cmd, stdout=out, stderr=subprocess.STDOUT)
    report = os.path.join(tree, "report", "report.json")
    if code != 0 or not os.path.isfile(report):
        return None, log
    with open(report, encoding="utf-8") as f:
        return json.load(f), log


def signatures(report):
    """{signature: (rule, place, status, hits, repro verified)} over the defects and the rule questions."""
    out = {}
    items = list(report.get("defects") or []) + [q for q in report.get("contractQuestions") or [] if q.get("kind") == "rule"]
    for item in items:
        for s in item.get("signatures") or []:
            out[s["signature"]] = (s.get("rule"), s.get("place"), s.get("status") or item.get("status"), s.get("hits") or 0,
                                   s.get("reproVerified") is True)
    return out


def scenario_states(report):
    return {s["name"]: s["status"] for s in report.get("scenarios") or []}


def parse_expect(entry):
    m = re.match(r"^(\S+)(?:\s*\((defect|question)\))?$", entry)
    if not m:
        return entry, "defect"
    return m.group(1), m.group(2) or "defect"


def matches_target(target, rule, place):
    if " @ " in target:
        t_rule, prefix = target.split(" @ ", 1)
        return rule == t_rule and (place or "").startswith(prefix.strip())
    return rule == target


# ----------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Mutation self-test of the runner module")
    ap.add_argument("--src", default=os.path.join(os.path.dirname(STAND), "KenseiECS"))
    ap.add_argument("--flavor", default=None)
    ap.add_argument("--debug", action="store_true")
    ap.add_argument("--configs", default=None, help="rel, dbg or rel,dbg (default: rel, or dbg with --debug)")
    ap.add_argument("--cases", type=int, default=4000)
    ap.add_argument("--only", default=None)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--keep", action="store_true", help="keep the mutated copies under Stand/out/selftest/runner/")
    ap.add_argument("--quick", action="store_true", help="accepted like the module run takes it; the self-test always runs the module --quick")
    ap.add_argument("--full", action="store_true", help="accepted and ignored: the self-test always runs the module --quick")
    a = ap.parse_args()

    mutants = load_mutants()
    by_name = {m["name"]: m for m in mutants}
    if a.list:
        for m in mutants:
            print("%-48s %-4s %-22s %-8s %s%s" % (m["name"], m["kind"], m["class"], "/".join(m["configs"]),
                                                 ", ".join(m["expect"] or m["removes"]), (" [contract " + m["contract"] + "]") if m["contract"] else ""))
        return 0
    for m in mutants:
        for b in m["base"]:
            if b not in by_name:
                print("selftest: %s names an unknown base mutant %s" % (m["name"], b))
                return 2
    src = os.path.abspath(a.src)
    if not os.path.isfile(os.path.join(src, "Systems", "SystemsRunner.cs")):
        print("selftest: %s is not a KenseiECS package folder" % src)
        return 2
    flavor = a.flavor or ("wt" if os.path.normpath(src) == os.path.normpath(os.path.join(os.path.dirname(STAND), "KenseiECS")) else os.path.basename(os.path.dirname(src)))
    configs = [c.strip() for c in (a.configs or ("dbg" if a.debug else "rel")).split(",") if c.strip()]
    only = set(x.strip() for x in a.only.split(",")) if a.only else None
    exit_code = 0
    for cfg in configs:
        code = run_config(a, src, flavor, cfg, mutants, by_name, only)
        exit_code = max(exit_code, code)
    return exit_code


def run_config(a, src, flavor, cfg, mutants, by_name, only):
    debug = cfg == "dbg"
    outdir = os.path.join(REPORTS, "selftest-%s-%s" % (flavor, cfg))
    logdir = os.path.join(outdir, "logs")
    os.makedirs(logdir, exist_ok=True)
    started = time.time()
    baselines = {}
    results = []
    version = contract_version(src)
    # Copies and build labels of this tree only.
    work = os.path.join(WORK, flavor, cfg)
    print("[%s] selftest of %s (%s, contract version %s)" % (cfg, src, flavor, version or "unknown"))

    def baseline(base_names, contract):
        key = (tuple(base_names), contract or "")
        if key in baselines:
            return baselines[key]
        tag = "base-" + hashlib.sha1(repr(key).encode()).hexdigest()[:8]
        tree = os.path.join(work, tag)
        problem = make_tree(src, tree, [(b, by_name[b]["patch"]) for b in base_names])
        if problem:
            baselines[key] = (None, "base does not apply: " + problem)
            return baselines[key]
        rep, log = run_module(tree, "st-%s-%s" % (flavor, tag), debug, a.cases, contract, logdir)
        baselines[key] = (rep, None if rep else "baseline run failed, see " + os.path.relpath(log, outdir))
        if rep is not None:
            print("[%s] baseline %s%s%s: %d defect signatures, %d question signatures" % (
                cfg, ", ".join(base_names) or "unmutated", " (contract " + contract + ")" if contract else "", "",
                sum(1 for v in signatures(rep).values() if v[2] == "defect"), sum(1 for v in signatures(rep).values() if v[2] == "question")))
        if not a.keep:
            remove_tree(os.path.join(tree, "KenseiECS"))
        return baselines[key]

    for m in mutants:
        if only and m["name"] not in only:
            continue
        row = {"name": m["name"], "kind": m["kind"], "class": m["class"], "why": m["why"], "patch": "Stand/runner/mutants/" + m["file"],
               "contract": m["contract"], "base": m["base"], "expect": m["expect"], "removes": m["removes"],
               "since": m["since"], "until": m["until"]}
        skip = version_skip(m, version)
        if skip:
            row.update(status="n/a", detail=skip)
            results.append(row)
            print("[%s] %-48s n/a (%s)" % (cfg, m["name"], skip))
            continue
        if cfg not in m["configs"]:
            row.update(status="n/a", detail="the mutation concerns " + "/".join(m["configs"]) + " only")
            results.append(row)
            print("[%s] %-48s n/a" % (cfg, m["name"]))
            continue
        base_rep, base_problem = baseline(m["base"], m["contract"])
        if base_rep is None:
            row.update(status="not-applicable" if base_problem.startswith("base does not apply") else "error", detail=base_problem)
            results.append(row)
            print("[%s] %-48s %s: %s" % (cfg, m["name"], row["status"], base_problem))
            continue
        tree = os.path.join(work, m["name"])
        problem = make_tree(src, tree, [(b, by_name[b]["patch"]) for b in m["base"]] + [(m["name"], m["patch"])])
        if problem:
            row.update(status="not-applicable", detail="the patch does not apply to this tree within its version range: " + problem)
            results.append(row)
            print("[%s] %-48s not applicable: %s" % (cfg, m["name"], problem))
            continue
        rep, log = run_module(tree, "st-%s-%s" % (flavor, m["name"]), debug, a.cases, m["contract"], logdir, verify=m["kind"] == "bug")
        if not a.keep:
            remove_tree(os.path.join(tree, "KenseiECS"))
        if rep is None:
            row.update(status="error", detail="build or run failed, see " + os.path.relpath(log, outdir))
            results.append(row)
            print("[%s] %-48s ERROR (%s)" % (cfg, m["name"], log))
            continue
        judge(m, row, signatures(base_rep), scenario_states(base_rep), signatures(rep), scenario_states(rep))
        results.append(row)
        print("[%s] %-48s %-13s %s" % (cfg, m["name"], row["status"], row["detail"]))

    bugs = [r for r in results if r["kind"] == "bug" and r["status"] in ("killed", "SURVIVED", "KILLED-NO-REPRO")]
    fixes = [r for r in results if r["kind"] == "fix" and r["status"] in ("fix-accepted", "FIX-REJECTED")]
    # A mutant within its version range that does not apply is a broken self-test, not a skip.
    errors = [r for r in results if r["status"] in ("error", "not-applicable")]
    killed = sum(1 for r in bugs if r["status"] == "killed")
    accepted = sum(1 for r in fixes if r["status"] == "fix-accepted")
    ok = killed == len(bugs) and accepted == len(fixes) and not errors
    summary = {"module": "runner", "format": 1, "src": src, "flavor": flavor, "config": cfg, "cases": a.cases,
               "contractVersion": version,
               "bugMutants": len(bugs), "killed": killed, "fixMutants": len(fixes), "fixAccepted": accepted,
               "errors": len(errors), "notApplicable": sum(1 for r in results if r["status"] == "n/a"),
               "skippedByVersion": [r["name"] for r in results if r["status"] == "n/a" and "contract version" in (r.get("detail") or "")],
               "ok": ok, "seconds": round(time.time() - started, 1), "mutants": results}
    with open(os.path.join(outdir, "selftest.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    write_markdown(outdir, summary)
    print("[%s] selftest: %d/%d bug mutants killed, %d/%d fix mutants accepted, %d errors -> %s" % (
        cfg, killed, len(bugs), accepted, len(fixes), len(errors), os.path.join(outdir, "selftest.md")))
    return 0 if ok else 1


def judge(m, row, base_sigs, base_scen, sigs, scen):
    new = []
    verified = {sig for sig, v in sigs.items() if v[4]}
    for sig, (rule, place, status, hits, _) in sorted(sigs.items()):
        if sig not in base_sigs:
            new.append((sig, rule, place, status, "new"))
        else:
            b_hits = base_sigs[sig][3]
            if hits >= 20 and hits >= 3 * max(b_hits, 1):
                new.append((sig, rule, place, status, "hits %d -> %d" % (b_hits, hits)))
            elif base_sigs[sig][2] != status:
                new.append((sig, rule, place, status, "status %s -> %s" % (base_sigs[sig][2], status)))
    regressed = sorted(n for n, s in scen.items() if base_scen.get(n) == "pass" and s in ("finding", "question", "stand-error"))
    gone = sorted(sig for sig in base_sigs if sig not in sigs)

    def show(items):
        return "; ".join("%s [%s, %s]" % (sig, status, why) for sig, rule, place, status, why in items)

    if m["kind"] == "bug":
        if not m["expect"] or m["expect"] == ["any"]:
            hit = new
            caught = bool(new or regressed)
        else:
            wanted = [parse_expect(e) for e in m["expect"]]
            hit = [x for x in new if any(x[1] == rule and x[3] == status for rule, status in wanted)]
            caught = bool(hit)
        # One repro of a catching signature must have compiled and reproduced against the mutated tree.
        reproduced = [x for x in hit if x[0] in verified]
        killed = caught and (bool(reproduced) or (not hit and bool(regressed)))
        detail = []
        if hit:
            detail.append("caught by: " + show(hit[:6]) + (" (+%d more)" % (len(hit) - 6) if len(hit) > 6 else ""))
            detail.append("repro reproduces: " + (", ".join(x[0] for x in reproduced[:3]) if reproduced else "NONE"))
        other = [x for x in new if x not in hit]
        if other:
            detail.append("also new: " + show(other[:4]) + (" (+%d more)" % (len(other) - 4) if len(other) > 4 else ""))
        if regressed:
            detail.append("scenarios pass -> fail: " + ", ".join(regressed))
        if not caught:
            detail.insert(0, "NOT caught" + (" as " + ", ".join(m["expect"]) if m["expect"] and m["expect"] != ["any"] else ""))
        row.update(status="killed" if killed else ("KILLED-NO-REPRO" if caught else "SURVIVED"), detail="; ".join(detail) or "nothing new",
                   newSignatures=[x[0] for x in new], reproduced=[x[0] for x in reproduced], scenariosFailing=regressed)
        return
    targets = m["removes"]
    present = [sig for sig, v in base_sigs.items() if any(matches_target(t, v[0], v[1]) for t in targets)]
    remaining = [sig for sig, v in sigs.items() if any(matches_target(t, v[0], v[1]) for t in targets)]
    brand_new = [x for x in new if x[4] == "new"]
    if not present:
        row.update(status="n/a", detail="nothing to remove in this build: " + ", ".join(targets) + " is not in the baseline")
        return
    clean = not brand_new and not regressed
    accepted = not remaining and clean
    detail = ["gone: %d signature(s) of %s" % (len(present), ", ".join(targets))]
    if remaining:
        detail.append("STILL THERE: " + ", ".join(remaining))
    if brand_new:
        detail.append("NEW: " + show(brand_new))
    if regressed:
        detail.append("scenarios pass -> fail: " + ", ".join(regressed))
    row.update(status="fix-accepted" if accepted else "FIX-REJECTED", detail="; ".join(detail), removedSignatures=present,
               newSignatures=[x[0] for x in brand_new], scenariosFailing=regressed)


def write_markdown(outdir, s):
    with open(os.path.join(outdir, "selftest.md"), "w", encoding="utf-8") as md:
        md.write("# Stand / runner — self-test (%s-%s)\n\n" % (s["flavor"], s["config"]))
        md.write("Every mutant is a patch in `Stand/runner/mutants/`, applied to a temporary copy of `%s` (with the patches of its Base first); "
                 "the sources under test are not touched. Each run is `run.sh --quick --cases %d` on the copy, compared with the run on the copy "
                 "with the Base patches only.\n\n" % (s["src"], s["cases"]))
        md.write("- **bug**: an injected defect; it must show up as a new signature (rule @ place, or a jump in its random hits) of an expected rule "
                 "with the expected status (a defect, or a contract question where the contract profile says so), and the repro of such a signature, "
                 "compiled against the mutated tree, must reproduce (KILLED-NO-REPRO otherwise).\n")
        md.write("- **fix**: an emulated correction of a known finding; its signatures must disappear and nothing new may appear "
                 "(no false positive on the corrected behaviour).\n\n")
        md.write("Contract version of the tree: %s; skipped by it (their Since/Until range): %s.\n\n" % (
            s.get("contractVersion") or "unknown", ", ".join(s.get("skippedByVersion") or []) or "none"))
        md.write("Result: **%s** — %d of %d bug mutants killed, %d of %d fix mutants accepted, %d errors (a patch that does not apply within its "
                 "version range is one), %d n/a; %.0f s.\n\n" % (
            "OK" if s["ok"] else "FAILED", s["killed"], s["bugMutants"], s["fixAccepted"], s["fixMutants"], s["errors"], s["notApplicable"], s["seconds"]))
        md.write("| mutant | kind | class | contract | what it injects | expected | result | details |\n|---|---|---|---|---|---|---|---|\n")
        for r in s["mutants"]:
            md.write("| %s | %s | %s | %s | %s | %s | %s | %s |\n" % (
                r["name"], r["kind"], r["class"], r.get("contract") or "package.json", cell(r["why"]),
                cell(", ".join(r.get("expect") or []) or ("removes " + ", ".join(r.get("removes") or []) if r.get("removes") else "any")),
                r["status"], cell(r.get("detail", ""))))


def cell(text):
    return (text or "").replace("|", "\\|").replace("\n", " ")


if __name__ == "__main__":
    sys.exit(main())
