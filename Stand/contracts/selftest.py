#!/usr/bin/env python3
"""Mutation self-test of the contracts module: does it catch injected defects, and does it stay quiet
on corrected behaviour?

Each mutant is a patch in Stand/contracts/mutants/*.patch: a header of "Key: value" lines, then a
unified diff against the folder that holds KenseiECS/ (paths a/KenseiECS/..., b/KenseiECS/...; it
applies with `patch -p1` or `git apply` there).

    Mutant: <name>
    Kind: bug | fix | doc
    Class: where it comes from (a reverted 2.2.1 fix, the same class in a new place, a stand check)
    Configs: rel, dbg                      the builds it is meant for
    Since: 2.2.2 / Until: 2.2.1            optional: the contract versions of the trees whose code the patch is for
    Version-note: <why>                    optional: why the range (shown when the mutant is skipped)
    Expect: [rel:|dbg:] <condition>        bug: every one must hold (optional)
    Removes: [rel:|dbg:] known <ID>        fix: the known problem it emulates fixing
    Why: <what the patch changes>
    Not-<cfg>: <why it cannot show in that build>

Conditions: "known <ID> found" (the known problem is reported as a defect), "known <ID> documented",
"defect <id prefix>" (a defect whose id starts so), "defect <id prefix> repro-verified" (and its repro
prints what the stand saw), "question <id prefix>", "documented <id prefix>".

The tree under test (--src, default: KenseiECS/ of this repository, with docs/ next to it) is copied
to Stand/out/selftest/contracts/<flavor>/ (the original is never touched): once as it is (the base) and once per
mutant with the patch applied; Stand/contracts/run.sh --quick runs on each copy. The copies are fresh files (new
modification times) and their build labels name the tree (selftest-base-<flavor>, selftest-<flavor>-<mutant>), so a
self-test never runs a binary built from another tree.

The contract version of the tree is its package.json version, or the next patch when its CHANGELOG starts with a
non-empty [Unreleased] section and package.json is not above the last release there. A mutant whose Since/Until range
excludes it is n/a ("contract version ...") and listed; a mutant within its range whose patch does not apply fails.

  bug  killed when every Expect holds and a defect that an Expect names (by id prefix, or the defects of an
       expected known problem) has a signature the base does not have: the mutant is caught by the mechanism
       it was written for, not by something else that changed. A bug mutant without a defect Expect is
       UNSPECIFIED (a failure of the self-test). Otherwise SURVIVED (or KILLED-OTHER: new defects, but not
       the expected ones; also a failure).
  doc  a change the docs of the tree describe: accepted (doc-accepted) when every Expect holds (a documented
       entry, a known problem now "documented") and no new defect signature appears.
  fix  an emulated correction of a known problem: accepted when that problem is no longer found and
       no new defect signature appears (no false defect on the corrected behaviour); FIX-REJECTED
       otherwise. A fix mutant only checks the module; it is not a proposal for the package.

A patch that no longer applies within its version range (the code moved on) is "not-applicable", a failure:
refresh the patch, or give it an Until and cut a twin for the new code.

Usage: Stand/contracts/run.sh --selftest [--src DIR] [--flavor NAME] [--debug | --configs rel,dbg]
                                         [--only m1,m2] [--keep]
Report: Stand/out/reports/contracts/selftest-<flavor>-<rel|dbg>/selftest.md and selftest.json.
Exit code: 0 every bug mutant killed and every fix accepted, 1 otherwise, 2 the base run failed.
Python 3.9+, standard library only; started with -I.
"""
import argparse
import glob
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
STAND = os.path.dirname(HERE)
ROOT = os.path.dirname(STAND)
WORK = os.path.join(STAND, "out", "selftest", "contracts")
REPORTS = os.path.join(STAND, "out", "reports", "contracts")
COPY = ["KenseiECS", "docs"]
SKIP = {"bin", "obj", "node_modules", ".vs", "TestResults", ".vitepress"}


def rel(path):
    p = os.path.abspath(path)
    return os.path.relpath(p, ROOT) if p.startswith(ROOT + os.sep) else p


def split_cfg(value):
    for c in ("rel", "dbg"):
        if value.startswith(c + ":"):
            return c, value[len(c) + 1:].strip()
    return None, value


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


def parse_patch(path):
    m = {"file": path, "name": os.path.splitext(os.path.basename(path))[0], "kind": "bug", "class": "", "why": "",
         "configs": ["rel", "dbg"], "expect": [], "removes": [], "not": {}, "since": None, "until": None, "versionNote": None}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("--- ") or line.startswith("diff "):
                break
            if ":" not in line:
                continue
            key, value = line.split(":", 1)
            key, value = key.strip(), value.strip()
            if key == "Mutant":
                m["name"] = value
            elif key == "Kind":
                m["kind"] = value
            elif key == "Class":
                m["class"] = value
            elif key == "Why":
                m["why"] = value
            elif key == "Configs":
                m["configs"] = [c.strip() for c in value.split(",") if c.strip()]
            elif key == "Expect":
                m["expect"].append(split_cfg(value))
            elif key == "Removes":
                m["removes"].append(split_cfg(value))
            elif key == "Since":
                m["since"] = value
            elif key == "Until":
                m["until"] = value
            elif key == "Version-note":
                m["versionNote"] = value
            elif key.startswith("Not-"):
                m["not"][key[4:]] = value
    return m


def writable(path):
    for d, dirs, files in os.walk(path):
        os.chmod(d, stat.S_IRWXU)
        for f in files:
            os.chmod(os.path.join(d, f), stat.S_IRUSR | stat.S_IWUSR)


def remove(path):
    if os.path.isdir(path):
        writable(path)
        shutil.rmtree(path, ignore_errors=True)


def make_tree(tree, dest):
    """A fresh copy: shutil.copy gives every file a new modification time, so MSBuild never takes an older build of
    the same label for up to date (the base of one tree after the base of another)."""
    remove(dest)
    os.makedirs(dest)
    for p in COPY:
        s = os.path.join(tree, p)
        if os.path.isdir(s):
            shutil.copytree(s, os.path.join(dest, p), copy_function=shutil.copy,
                            ignore=lambda d, names: [n for n in names if n in SKIP or n.endswith(".meta")])
    writable(dest)


def apply_patch(patch, dest, log):
    with open(log, "w") as out:
        code = subprocess.call(["patch", "-p1", "--forward", "--batch", "-d", dest, "-i", patch], stdout=out, stderr=subprocess.STDOUT)
    return code == 0


def run_module(src, flavor, cfg, log):
    args = [os.path.join(HERE, "run.sh"), "--quick", "--src", src, "--flavor", flavor]
    if cfg == "dbg":
        args.append("--debug")
    with open(log, "w") as out:
        code = subprocess.call(args, stdout=out, stderr=subprocess.STDOUT)
    report = os.path.join(REPORTS, "%s-%s" % (flavor, cfg), "report.json")
    if code != 0 or not os.path.isfile(report):
        return None
    with open(report, encoding="utf-8") as f:
        return json.load(f)


def defects(rep):
    return rep.get("findings") or []


def signatures(rep):
    return {s["signature"] for s in rep.get("signatures") or [] if s.get("kind") == "defect"}


def known_status(rep, key):
    k = next((x for x in rep.get("known") or [] if x.get("id") == key), None)
    return k.get("status") if k else None


def check(cond, rep):
    """(passed, why, matched defects) for one Expect condition."""
    words = cond.split()
    if len(words) >= 3 and words[0] == "known":
        status = known_status(rep, words[1])
        k = next((x for x in rep.get("known") or [] if x.get("id") == words[1]), None) or {}
        matched = [f for f in defects(rep) if f.get("id") in (k.get("findings") or [])] if words[2] == "found" else []
        return status == words[2], "known %s is %s" % (words[1], status or "missing"), matched
    if len(words) >= 2 and words[0] in ("defect", "question", "documented"):
        items = defects(rep) if words[0] == "defect" else rep.get("contractQuestions") or [] if words[0] == "question" else rep.get("documented") or []
        hits = [f for f in items if str(f.get("id", "")).startswith(words[1])]
        if not hits:
            return False, "no %s %s*" % (words[0], words[1]), []
        if "repro-verified" in words[2:]:
            ok = [f for f in hits if f.get("reproStatus") == "verified"]
            if not ok:
                return False, "%s has no verified repro (%s)" % (hits[0]["id"], hits[0].get("reproStatus") or "no repro"), []
            return True, "%s, repro verified" % ok[0]["id"], ok if words[0] == "defect" else []
        return True, hits[0]["id"], hits if words[0] == "defect" else []
    return False, "unknown condition: " + cond, []


def judge(m, cfg, row, base, rep):
    new = sorted(signatures(rep) - signatures(base))
    row["newSignatures"] = new
    row["newDefects"] = sorted({f["id"] for f in defects(rep)} - {f["id"] for f in defects(base)})
    notes = []
    if m["kind"] == "fix":
        targets = [t for c, t in m["removes"] if c in (None, cfg)]
        present = [t for t in targets if t.startswith("known ") and known_status(base, t.split()[1]) == "found"]
        if not present:
            row.update(status="n/a", detail="nothing to remove in this build: " + ", ".join(targets) + " is not found on the base")
            return
        remaining = [t for t in present if known_status(rep, t.split()[1]) == "found"]
        accepted = not remaining and not new
        row["status"] = "fix-accepted" if accepted else "FIX-REJECTED"
        row["detail"] = "gone: %s" % ", ".join(t for t in present if t not in remaining) if not remaining else "still found: " + ", ".join(remaining)
        if new:
            row["detail"] += "; new defect signatures: " + "; ".join(new[:4])
        return
    ok = True
    matched = []
    expects = [(c, cond) for c, cond in m["expect"] if c is None or c == cfg]
    for c, cond in expects:
        passed, why, hits = check(cond, rep)
        row["expect"].append({"condition": cond, "passed": passed, "detail": why})
        ok = ok and passed
        matched += hits
        notes.append(("ok: " if passed else "FAILED: ") + why)
    if m["kind"] == "doc":
        accepted = ok and bool(expects) and not new
        row["status"] = "doc-accepted" if accepted else "DOC-REJECTED"
        row["detail"] = ("new defect signatures: " + "; ".join(new[:4]) if new else "no new defect signature") + ("; " + "; ".join(notes) if notes else "")
        return
    base_sigs = signatures(base)
    caught = [f for f in matched if f.get("signature") and f["signature"] not in base_sigs]
    if not any(cond.split()[0] in ("defect", "known") for c, cond in expects):
        status = "UNSPECIFIED"
        notes.append("the mutant names no expected defect (Expect: defect <id prefix> or known <ID> found)")
    elif ok and caught:
        status = "killed"
    elif new:
        # New defects, but not the ones the mutant was written for (or not all its Expect lines hold).
        status = "KILLED-OTHER"
        if ok:
            notes.append("the expected defects are not new: their signatures are on the base too")
    else:
        status = "SURVIVED"
    row["status"] = status
    row["caughtBy"] = sorted({f["signature"] for f in caught})
    row["detail"] = ("new defect signatures: %d" % len(new) if new else "no new defect signature") + ("; " + "; ".join(notes) if notes else "")


def write_report(outdir, summary):
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, "selftest.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    with open(os.path.join(outdir, "selftest.md"), "w", encoding="utf-8") as f:
        f.write("# Stand/contracts: self-test (%s-%s)\n\n" % (summary["flavor"], summary["config"]))
        f.write("Each mutant is a patch from `Stand/contracts/mutants/`, applied to a temporary copy of `%s` (and its docs/) in "
                "`Stand/out/selftest/contracts/`; the tree itself is never changed. The module runs on an unpatched copy (the base) "
                "and on each patched copy, and the reports are compared by defect signature (mechanism and place).\n\n" % summary["src"])
        f.write("- **bug**: an injected defect; killed when a defect signature appears that the base does not have and every Expect of the patch holds.\n")
        f.write("- **fix**: an emulated correction of a known problem; accepted when it is no longer found and nothing new appears "
                "(a check against false defects on corrected behaviour, not a proposal for the package).\n")
        f.write("- **doc**: a change the docs of the tree describe; accepted when its documented entry appears and no new defect does.\n\n"
                "Only an exact kill counts: every Expect holds and an expected defect has a signature the base does not have "
                "(KILLED-OTHER and UNSPECIFIED are failures).\n\n")
        f.write("Contract version of the tree: %s (a mutant whose Since/Until range excludes it is n/a). " % (summary.get("contractVersion") or "unknown"))
        f.write("Base: defects %s. Killed %d of %d bug mutants, accepted %d of %d fix mutants and %d of %d doc mutants%s; %.0f s.\n\n" % (
            ", ".join("`%s`" % x for x in summary["baseline"]) or "none", summary["killed"], summary["bugMutants"], summary["fixAccepted"],
            summary["fixMutants"], summary["docAccepted"], summary["docMutants"],
            ", %d could not be applied or run" % summary["errors"] if summary["errors"] else "", summary["seconds"]))
        f.write("| mutant | kind | class | what it changes | result | details |\n|---|---|---|---|---|---|\n")
        for r in summary["mutants"]:
            f.write("| `%s` | %s | %s | %s | **%s** | %s |\n" % (r["name"], r["kind"], r["class"].replace("|", "\\|"), r["why"].replace("|", "\\|"),
                                                                r["status"], str(r.get("detail", "")).replace("|", "\\|")))


def main():
    ap = argparse.ArgumentParser(prog="run.sh --selftest", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--src", default=os.path.join(ROOT, "KenseiECS"), help="the package folder under test (docs/ next to it)")
    ap.add_argument("--flavor", default=None, help="label of the tree for the report folders (default: wt or the folder above --src)")
    ap.add_argument("--debug", action="store_true", help="KENSEI_DEBUG only (same as --configs dbg)")
    ap.add_argument("--configs", default="rel,dbg")
    ap.add_argument("--only", default=None, help="comma list of mutant names")
    ap.add_argument("--keep", action="store_true", help="keep the copies under Stand/out/selftest/contracts/")
    a = ap.parse_args()
    src = os.path.abspath(a.src)
    if not os.path.isfile(os.path.join(src, "Core", "World.cs")):
        print("selftest: %s is not a KenseiECS package folder" % src, file=sys.stderr)
        return 2
    tree = os.path.dirname(src)
    flavor = a.flavor or ("wt" if src == os.path.join(ROOT, "KenseiECS") else os.path.basename(tree))
    configs = ["dbg"] if a.debug else [c for c in a.configs.split(",") if c in ("rel", "dbg")]
    only = set(a.only.split(",")) if a.only else None
    mutants = [parse_patch(p) for p in sorted(glob.glob(os.path.join(HERE, "mutants", "*.patch")))]
    if only:
        mutants = [m for m in mutants if m["name"] in only]
    version = contract_version(src)
    # Copies and build labels of this tree only: another tree's self-test never shares them.
    work = os.path.join(WORK, flavor)
    logdir = os.path.join(work, "logs")
    os.makedirs(logdir, exist_ok=True)
    started = time.time()
    print("selftest of %s (%s, contract version %s)" % (rel(src), flavor, version or "unknown"))

    base_tree = os.path.join(work, "base")
    make_tree(tree, base_tree)
    base = {}
    for cfg in configs:
        log = os.path.join(logdir, "base-%s.log" % cfg)
        rep = run_module(os.path.join(base_tree, "KenseiECS"), "selftest-base-" + flavor, cfg, log)
        if rep is None:
            print("selftest: the run on the unpatched copy failed (%s), see %s" % (cfg, rel(log)))
            return 2
        base[cfg] = rep
        print("base %s-%s: defects %s" % (flavor, cfg, ", ".join(f["id"] for f in defects(rep)) or "none"))

    results = {cfg: [] for cfg in configs}
    for m in mutants:
        skip = version_skip(m, version)
        dest = os.path.join(work, m["name"])
        patched = False
        if not skip:
            make_tree(tree, dest)
            patched = apply_patch(m["file"], dest, os.path.join(logdir, m["name"] + "-patch.log"))
        for cfg in configs:
            row = {"name": m["name"], "kind": m["kind"], "class": m["class"], "why": m["why"], "config": cfg, "expect": [],
                   "removes": [t for c, t in m["removes"] if c in (None, cfg)], "newSignatures": [], "seconds": 0,
                   "since": m["since"], "until": m["until"]}
            if skip:
                row.update(status="n/a", detail=skip)
                results[cfg].append(row)
                print("[%s] %-34s n/a (%s)" % (cfg, m["name"], skip))
                continue
            if cfg not in m["configs"]:
                row.update(status="n/a", detail=m["not"].get(cfg) or "the mutant is meant for " + "/".join(m["configs"]))
                results[cfg].append(row)
                print("[%s] %-34s n/a" % (cfg, m["name"]))
                continue
            if not patched:
                row.update(status="not-applicable", detail="the patch does not apply to this tree (see %s): refresh it" % rel(os.path.join(logdir, m["name"] + "-patch.log")))
                results[cfg].append(row)
                print("[%s] %-34s not applicable" % (cfg, m["name"]))
                continue
            t0 = time.time()
            log = os.path.join(logdir, "%s-%s.log" % (m["name"], cfg))
            rep = run_module(os.path.join(dest, "KenseiECS"), "selftest-%s-%s" % (flavor, m["name"]), cfg, log)
            row["seconds"] = round(time.time() - t0, 1)
            if rep is None:
                row.update(status="error", detail="build or run failed, see " + rel(log))
                results[cfg].append(row)
                print("[%s] %-34s ERROR (%s)" % (cfg, m["name"], rel(log)))
                continue
            judge(m, cfg, row, base[cfg], rep)
            results[cfg].append(row)
            print("[%s] %-34s %-13s %s" % (cfg, m["name"], row["status"], row["detail"]))
        if not a.keep and not skip:
            remove(dest)

    ok_all = True
    for cfg in configs:
        rows = results[cfg]
        bugs = [r for r in rows if r["kind"] == "bug" and r["status"] in ("killed", "SURVIVED", "KILLED-OTHER", "UNSPECIFIED")]
        fixes = [r for r in rows if r["kind"] == "fix" and r["status"] in ("fix-accepted", "FIX-REJECTED")]
        docs_ = [r for r in rows if r["kind"] == "doc" and r["status"] in ("doc-accepted", "DOC-REJECTED")]
        errors = [r for r in rows if r["status"] in ("error", "not-applicable")]
        # Only an exact kill counts: the expected mechanism, with a signature the base does not have.
        killed = sum(1 for r in bugs if r["status"] == "killed")
        accepted = sum(1 for r in fixes if r["status"] == "fix-accepted")
        doc_ok = sum(1 for r in docs_ if r["status"] == "doc-accepted")
        ok = killed == len(bugs) and accepted == len(fixes) and doc_ok == len(docs_) and not errors
        ok_all = ok_all and ok
        summary = {"module": "contracts", "format": 1, "src": rel(src), "flavor": flavor, "config": cfg, "mode": "quick",
                   "contractVersion": version,
                   "baseline": sorted(f["id"] for f in defects(base[cfg])), "bugMutants": len(bugs), "killed": killed,
                   "fixMutants": len(fixes), "fixAccepted": accepted, "docMutants": len(docs_), "docAccepted": doc_ok, "errors": len(errors),
                   "notApplicable": sum(1 for r in rows if r["status"] == "n/a"), "ok": ok,
                   "seconds": round(time.time() - started, 1), "mutants": rows}
        outdir = os.path.join(REPORTS, "selftest-%s-%s" % (flavor, cfg))
        write_report(outdir, summary)
        print("[%s] selftest: %d/%d bug mutants killed, %d/%d fix mutants accepted, %d/%d doc mutants accepted, %d errors -> %s" % (
            cfg, killed, len(bugs), accepted, len(fixes), doc_ok, len(docs_), len(errors), rel(os.path.join(outdir, "selftest.md"))))
    if not a.keep:
        remove(base_tree)
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
