#!/usr/bin/env python3
"""Self-test of the generator module: does the oracle catch injected defects, and does it stay quiet
on corrected behaviour?

Every mutant is a patch in Stand/generator/mutants/<name>.patch (a unified diff against the repository
root, with a header: Mutant, Kind, Expect or Removes, Why, and optionally Since / Until: the contract versions of
the trees whose generator code the patch is for, Version-note: why). Each one is applied to a temporary copy of
the tree under test (KenseiECS, KenseiECS.Generators and docs, copied to Stand/out/selftest/generator/);
the tree itself is never touched. The contract version of the tree is its package.json version, or the next patch
when its CHANGELOG starts with a non-empty [Unreleased] section and package.json is not above the last release there:
a mutant whose range excludes it is n/a ("contract version ...") and listed, and within its range a patch that does
not apply fails (refresh it, or give it an Until and cut a twin for the new code). Copies are fresh files and their
build labels name the tree, so no build of another tree is taken for up to date. The module runs once on an unmutated copy (the base) and once per
mutant, with the same programs (--quick, the same seeds), and the reports are compared by signature
(rule, key and place) and by program:

  bug  killed only when a defect or regression signature appears that the base does not have, of a rule
       named by Expect, outside every known issue the base already shows (a new signature inside GN1-GN8 or
       NF1-NF5 is the hiding problem this test exists for), and the repro of its finding was verified (the
       clean form compiled again and gave the same output). Otherwise: ABSORBED (every new signature went
       into a known issue of the base, or only known signatures gained programs), KILLED-OTHER-RULE (new
       signatures, none of an Expect rule), KILLED-NO-REPRO (the repro of no candidate was verified),
       SURVIVED (nothing new). Only "killed" passes.
  fix  an emulated correction of a known issue: the issues named by Removes must not be found any more
       and nothing new may appear (no false defect on corrected behaviour). Not a proposal for the package.

Usage: Stand/generator/run.sh --selftest [--src DIR] [--flavor NAME] [--debug] [--only a,b]
                                        [--iterations N] [--jobs N] [--keep]
Report: Stand/out/reports/generator/selftest-<flavor>-<rel|dbg>/selftest.md and selftest.json.
Exit code: 0 every bug mutant killed and every fix accepted, 1 otherwise, 2 the base run failed.
Python 3.8+, standard library only.
"""
import argparse
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
WT = os.path.dirname(STAND)
MUTANTS = os.path.join(HERE, "mutants")
COPY = ["KenseiECS", "KenseiECS.Generators", "docs"]
SKIP_DIRS = {"bin", "obj", "node_modules", ".vitepress", "dist", ".vs", "TestResults"}


def rel(path):
    p = os.path.abspath(path)
    return os.path.relpath(p, WT) if p.startswith(WT + os.sep) else p


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


# ----------------------------------------------------------------------------- patches

def read_patch(path):
    """Header fields (lower-case keys) and the file sections [(path, [(old lines, new lines)])] of a unified diff."""
    text = open(path, encoding="utf-8").read()
    meta = {}
    for line in text.split("\n--- ", 1)[0].splitlines():
        m = re.match(r"^([A-Za-z][A-Za-z-]*):\s*(.*)$", line)
        if m and not line.startswith("diff "):
            meta[m.group(1).lower()] = m.group(2).strip()
    lines = text.splitlines(keepends=True)
    files, i = [], 0
    while i < len(lines):
        if lines[i].startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            target = lines[i + 1][4:].strip().split("\t")[0]
            target = target[2:] if target.startswith("b/") else target
            hunks = []
            i += 2
            while i < len(lines) and lines[i].startswith("@@"):
                i += 1
                old, new = [], []
                while i < len(lines) and not lines[i].startswith(("@@", "--- ", "diff ")):
                    line = lines[i]
                    i += 1
                    if line.startswith("\\"):
                        continue
                    tag, body = line[:1], line[1:]
                    if tag in (" ", ""):
                        old.append(body)
                        new.append(body)
                    elif tag == "-":
                        old.append(body)
                    elif tag == "+":
                        new.append(body)
                hunks.append(("".join(old), "".join(new)))
            files.append((target, hunks))
        else:
            i += 1
    return meta, files


def apply_patch(tree, files):
    """Applies the hunks by exact context (each must occur once); returns an error text or None."""
    for target, hunks in files:
        path = os.path.join(tree, target)
        if not os.path.isfile(path):
            return "%s is not in the tree" % target
        with open(path, encoding="utf-8", newline="") as f:
            text = f.read()
        for old, new in hunks:
            count = text.count(old)
            if count != 1:
                return "a hunk of %s matches %d times (the tree differs from the one the patch was made for)" % (target, count)
            text = text.replace(old, new)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return None


# ----------------------------------------------------------------------------- trees and runs

def writable(path):
    for root, dirs, files in os.walk(path):
        os.chmod(root, stat.S_IRWXU)
        for name in files:
            os.chmod(os.path.join(root, name), stat.S_IRUSR | stat.S_IWUSR)


def remove(path):
    if os.path.isdir(path):
        writable(path)
        shutil.rmtree(path)


def copy_tree(src_root, dest):
    """A fresh copy: new modification times, so MSBuild never takes an older build of the same label for up to date."""
    remove(dest)
    os.makedirs(dest)
    for name in COPY:
        s = os.path.join(src_root, name)
        if os.path.isdir(s):
            shutil.copytree(s, os.path.join(dest, name), copy_function=shutil.copy,
                            ignore=lambda d, names: [n for n in names if n in SKIP_DIRS])
    writable(dest)


def run_module(tree, flavor, report_dir, a, log):
    args = ["bash", os.path.join(HERE, "run.sh"), "--quick", "--src", os.path.join(tree, "KenseiECS"),
            "--flavor", flavor, "--report-dir", report_dir, "--no-xversion"]
    if a.debug:
        args.append("--debug")
    if a.iterations is not None:
        args += ["--iterations", str(a.iterations)]
    if a.jobs is not None:
        args += ["--jobs", str(a.jobs)]
    with open(log, "w", encoding="utf-8") as out:
        code = subprocess.call(args, stdout=out, stderr=subprocess.STDOUT)
    path = os.path.join(report_dir, "report.json")
    if code != 0 or not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def signatures(rep):
    """Defect and regression signatures: id -> (rule, finding, known, text, repro verified)."""
    out = {}
    for lst in ("findings", "regressions"):
        for f in rep.get(lst) or []:
            verified = str(f.get("reproStatus") or "").startswith("verified")
            for s in f.get("signatures") or []:
                out[s["id"]] = (f.get("rule"), f.get("id"), f.get("known"), s.get("text"), verified)
    return out


def found_known(rep):
    """The known issues the report shows at all (found, regression, question or documented)."""
    return {k["id"] for k in rep.get("known") or [] if (k.get("status") or "") not in ("", "not-found", "not-checked", "not-applicable")}


def programs(rep):
    return {src: set((m.get("d") or []) + (m.get("r") or [])) for src, m in (rep.get("programs") or {}).items()}


def known_status(rep):
    return {k["id"]: k.get("status") or ("found" if k.get("detected") else "not-found") for k in rep.get("known") or []}


# ----------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Mutation self-test of Stand/generator.")
    ap.add_argument("--src", default=os.path.join(WT, "KenseiECS"), help="the KenseiECS package folder under test (default: this working copy)")
    ap.add_argument("--flavor", default=None)
    ap.add_argument("--debug", action="store_true")
    ap.add_argument("--only", default=None, help="comma-separated mutant names")
    ap.add_argument("--iterations", type=int, default=None, help="random programs per run (default: the --quick count)")
    ap.add_argument("--jobs", type=int, default=None)
    ap.add_argument("--keep", action="store_true", help="keep the mutated copies under Stand/out/selftest/generator/")
    a = ap.parse_args()

    src = os.path.abspath(a.src)
    root = os.path.dirname(src)
    if not os.path.isfile(os.path.join(src, "package.json")) or not os.path.isdir(os.path.join(root, "KenseiECS.Generators")):
        print("selftest: %s is not a KenseiECS package folder with KenseiECS.Generators next to it" % src)
        return 2
    flavor = a.flavor or ("wt" if os.path.realpath(src) == os.path.realpath(os.path.join(WT, "KenseiECS")) else os.path.basename(root))
    cfg = "dbg" if a.debug else "rel"
    outdir = os.path.join(STAND, "out", "reports", "generator", "selftest-%s-%s" % (flavor, cfg))
    work = os.path.join(STAND, "out", "selftest", "generator", "%s-%s" % (flavor, cfg))
    remove(os.path.join(outdir, "runs"))
    os.makedirs(os.path.join(outdir, "runs"), exist_ok=True)
    os.makedirs(work, exist_ok=True)
    only = set(a.only.split(",")) if a.only else None
    patches = sorted(p for p in os.listdir(MUTANTS) if p.endswith(".patch"))
    if only:
        unknown = only - {p[:-6] for p in patches}
        if unknown:
            print("selftest: unknown mutants: " + ", ".join(sorted(unknown)))
            return 2
        patches = [p for p in patches if p[:-6] in only]

    started = time.time()
    version = contract_version(src)
    print("selftest of %s (%s-%s, contract version %s)" % (rel(src), flavor, cfg, version or "unknown"))
    base_tree = os.path.join(work, "base")
    copy_tree(root, base_tree)
    base = run_module(base_tree, "st-%s-base" % flavor, os.path.join(outdir, "runs", "base"), a, os.path.join(outdir, "runs", "base.log"))
    if base is None:
        print("selftest: the base run failed, see " + rel(os.path.join(outdir, "runs", "base.log")))
        return 2
    base_sigs, base_progs, base_known = signatures(base), programs(base), known_status(base)
    base_shown = found_known(base)
    print("selftest base %s-%s: %d defects, %d regressions, %d signatures (%.0f s)"
          % (flavor, cfg, len(base.get("findings") or []), len(base.get("regressions") or []), len(base_sigs), time.time() - started))

    results = []
    for name in patches:
        mutant = name[:-6]
        meta, files = read_patch(os.path.join(MUTANTS, name))
        kind = meta.get("kind", "bug")
        expect = [x.strip() for x in meta.get("expect", "").split(",") if x.strip()]
        removes = [x.strip() for x in meta.get("removes", "").split(",") if x.strip()]
        row = {"name": mutant, "kind": kind, "why": meta.get("why", ""), "files": [f for f, _ in files], "expect": expect, "removes": removes,
               "since": meta.get("since"), "until": meta.get("until")}
        skip = version_skip(meta, version)
        if skip:
            row.update(status="n/a", detail=skip)
            results.append(row)
            print("%-28s n/a (%s)" % (mutant, skip))
            continue
        tree = os.path.join(work, mutant)
        copy_tree(root, tree)
        problem = apply_patch(tree, files)
        if problem:
            row.update(status="not-applicable", detail=problem)
            results.append(row)
            print("%-28s not applicable: %s" % (mutant, problem))
            remove(tree)
            continue
        t0 = time.time()
        rep = run_module(tree, "st-%s-%s" % (flavor, mutant), os.path.join(outdir, "runs", mutant), a, os.path.join(outdir, "runs", mutant + ".log"))
        if not a.keep:
            remove(tree)
        if rep is None:
            row.update(status="build-or-run-failed", detail="see " + rel(os.path.join(outdir, "runs", mutant + ".log")))
            results.append(row)
            print("%-28s build/run failed (%s)" % (mutant, row["detail"]))
            continue
        sigs, progs, known = signatures(rep), programs(rep), known_status(rep)
        new = {k: v for k, v in sigs.items() if k not in base_sigs}
        gained = sorted(p for p, s in progs.items() if s - base_progs.get(p, set()))
        row["newSignatures"] = [{"id": k, "rule": v[0], "finding": v[1], "known": v[2], "text": v[3], "reproVerified": v[4]} for k, v in sorted(new.items(), key=lambda kv: kv[1][3])]
        row["programsGained"] = len(gained)
        row["programs"] = gained[:10]
        row["seconds"] = round(time.time() - t0, 1)
        if kind == "fix":
            still = [k for k in removes if known.get(k) == "found"]
            gone = [k for k in removes if base_known.get(k) == "found" and known.get(k) != "found"]
            accepted = not still and not new and not gained
            row["status"] = "fix-accepted" if accepted else "FIX-REJECTED"
            row["detail"] = ("gone: " + ", ".join(gone) if gone else "nothing gone") + \
                            ("; still found: " + ", ".join(still) if still else "") + \
                            ("; new signatures: " + ", ".join(sorted({v[0] for v in new.values()})) if new else "") + \
                            ("; programs with a new signature: %d" % len(gained) if gained else "")
        else:
            rules = sorted({v[0] for v in new.values()})
            # A new signature counts only outside the known issues the base already shows: inside one it hides.
            fresh = {k: v for k, v in new.items() if not v[2] or v[2] not in base_shown}
            absorbed = {k: v for k, v in new.items() if k not in fresh}
            expected = {k: v for k, v in fresh.items() if v[0] in expect}
            verified = {k: v for k, v in expected.items() if v[4]}
            if verified:
                row["status"] = "killed"
            elif expected:
                row["status"] = "KILLED-NO-REPRO"
            elif fresh:
                row["status"] = "KILLED-OTHER-RULE"
            elif absorbed or gained:
                row["status"] = "ABSORBED"
            else:
                row["status"] = "SURVIVED"
            row["detail"] = ("new signatures: %d (%s)" % (len(new), ", ".join(rules)) if new else "no new signature") + \
                            ("; caught by %s with a verified repro" % ", ".join(sorted({v[1] + " " + v[0] for v in verified.values()})[:4]) if verified else "") + \
                            ("; inside known issues of the base: %s" % ", ".join(sorted({v[2] for v in absorbed.values()})) if absorbed else "") + \
                            ("; programs with a new defect or regression signature: %d" % len(gained))
        results.append(row)
        print("%-28s %-20s %s (%.0f s)" % (mutant, row["status"], row["detail"], row["seconds"]))

    bugs = [r for r in results if r["kind"] == "bug" and r["status"] not in ("n/a", "not-applicable", "build-or-run-failed")]
    killed = sum(1 for r in bugs if r["status"] == "killed")
    fixes = [r for r in results if r["kind"] == "fix" and r["status"] not in ("n/a", "not-applicable", "build-or-run-failed")]
    accepted = sum(1 for r in fixes if r["status"] == "fix-accepted")
    broken = [r for r in results if r["status"] in ("not-applicable", "build-or-run-failed")]
    summary = dict(module="generator", format=1, src=rel(src), flavor=flavor, config=cfg, iterations=a.iterations,
                   contractVersion=version, skippedByVersion=[r["name"] for r in results if r["status"] == "n/a"],
                   baseline=sorted(f["id"] for f in base.get("findings") or []), baseSignatures=len(base_sigs),
                   killed=killed, bugMutants=len(bugs), fixAccepted=accepted, fixMutants=len(fixes), broken=len(broken),
                   seconds=round(time.time() - started, 1), mutants=results)
    with open(os.path.join(outdir, "selftest.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=1)
    with open(os.path.join(outdir, "selftest.md"), "w", encoding="utf-8") as md:
        md.write("# Stand/generator: self-test (%s-%s)\n\n" % (flavor, cfg))
        md.write("Each mutant is a patch from `Stand/generator/mutants/`, applied to a temporary copy of `%s` (with KenseiECS.Generators and docs); "
                 "the tree itself is not changed. The module runs on an unmutated copy (the base) and on each mutant with the same programs, "
                 "and the reports are compared by signature (rule, key and place) and by program.\n\n" % rel(src))
        md.write("- **bug**: an injected defect; killed only by a new defect or regression signature of an expected rule, outside the known issues the base shows, "
                 "whose repro was verified. ABSORBED (only inside known issues), KILLED-OTHER-RULE, KILLED-NO-REPRO and SURVIVED fail.\n")
        md.write("- **fix**: an emulated correction of a known issue; accepted when the issues it names are no longer found and nothing new appears "
                 "(a check against false defects on corrected behaviour, not a proposal for the package).\n\n")
        md.write("Contract version of the tree: %s; skipped by it (their Since/Until range): %s.\n\n" % (
            version or "unknown", ", ".join(summary["skippedByVersion"]) or "none"))
        md.write("Base: %d defects, %d signatures. Killed %d of %d bug mutants, accepted %d of %d fix mutants%s; %.0f s.\n\n"
                 % (len(base.get("findings") or []), len(base_sigs), killed, len(bugs), accepted, len(fixes),
                    ", %d could not be applied or run" % len(broken) if broken else "", summary["seconds"]))
        md.write("| mutant | kind | what it changes | expected | result | details |\n|---|---|---|---|---|---|\n")
        for r in results:
            md.write("| `%s` | %s | %s | %s | **%s** | %s |\n" % (r["name"], r["kind"], r["why"].replace("|", "\\|"),
                     ", ".join(r.get("expect") or []) or ("removes " + ", ".join(r.get("removes") or []) if r.get("removes") else "-"),
                     r["status"], r.get("detail", "").replace("|", "\\|")))
        md.write("\nNew signatures per mutant:\n\n")
        for r in results:
            sigs = r.get("newSignatures") or []
            if sigs:
                md.write("- `%s`: %s\n" % (r["name"], "; ".join("`%s` (%s%s)" % (s["text"], s["finding"], ", " + s["known"] if s.get("known") else "") for s in sigs[:8])
                                             + (" (+%d)" % (len(sigs) - 8) if len(sigs) > 8 else "")))
    print("selftest: %d/%d bug mutants killed, %d/%d fix mutants accepted -> %s"
          % (killed, len(bugs), accepted, len(fixes), rel(os.path.join(outdir, "selftest.md"))))
    if not a.keep:
        remove(base_tree)
    return 0 if killed == len(bugs) and accepted == len(fixes) and not broken else 1


if __name__ == "__main__":
    sys.exit(main())
