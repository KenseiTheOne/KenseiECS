"""--selftest: does the module catch the defects it exists for, and stay quiet on a clean tree?

Mutants are unified diffs in Stand/alloc/mutants/*.patch (paths relative to the repository root, so
`git apply` and `patch -p1` take them too). The "Key: value" lines before the diff name the mutant and
what must happen:

    Mutant: nov-filter-watches-growth
    Kind: bug                         (a defect that must be caught)
    Origin: ...                       (a reverted 2.2.1 fix, or the same class in a new place)
    Why: ...
    Since: 2.2.2 / Until: 2.2.1       (optional: the contract versions of the trees whose code the patch is for;
                                       Version-note: says why; a tree outside the range lists the mutant as n/a)
    Run: dbg growth.first_n_filters   (a module run: rel | dbg | ci, then its conditions; one line per run)

Conditions of a run line: scenario ids (a new item must be in one of them), status=finding|regression|question
(the new item's status), cause=X / cause!=X (for ci: the cause class of a new red test).

The tree under test (default: the working copy) is copied to Stand/out/selftest/alloc/: base/ (the
baseline of every mutant run), control/ (an unmutated copy measured against base: whatever it reports
beyond the contract items is a false positive) and one folder per mutant with its patch applied. Only
the copies change; they and the self-test's build outputs are removed before the run (so no build of another tree is
taken for up to date) and at the end (--keep keeps them). A mutant within its version range whose patch does not apply
is not-applicable, a failure: refresh the patch, or give it an Until and cut a twin for the new code. Every module run is a fresh `Stand/alloc/run.sh` process (--quick --no-mem
--no-timing; ci runs are --ci-parity) whose report goes under the self-test's report folder:
Stand/out/reports/alloc/selftest-<flavor>/selftest.md and selftest.json, runs/<run>/ for each run.
A mutant is killed when one of its runs reports an item the control run does not have (compared by
signature and status) and the run's conditions hold. Exit code 0 when every bug mutant is killed and the
control is clean.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

import standlib as S

MUTANTS = os.path.join(S.MOD, "mutants")
WORKDIR = os.path.join(S.OUT, "selftest", S.MODULE)
# Flavor prefix of the self-test's runs: build outputs land in Stand/out/bin|obj/<flavor>-<cfg>/, which other
# modules' self-tests share, so the prefix is this module's own and cleanup touches nothing else.
PREFIX = "alloc-st-"
COPY_PARTS = ("KenseiECS", "KenseiECS.Generators", "KenseiECS.Tests", "KenseiECS.Generators.Tests", "KenseiECS.NET",
              "docs", "README.md", "KenseiECS.sln")


class PatchError(Exception):
    pass


# ---------------------------------------------------------------------------
# mutants
# ---------------------------------------------------------------------------

def load_mutants(names=None):
    out = []
    for f in sorted(os.listdir(MUTANTS)):
        if not f.endswith(".patch"):
            continue
        text = open(os.path.join(MUTANTS, f), encoding="utf-8").read()
        meta = {"file": S.rel(os.path.join(MUTANTS, f)), "runs": [], "text": text}
        for line in text.split("\n--- ", 1)[0].splitlines():
            m = re.match(r"^([A-Z][A-Za-z-]+):\s*(.*)$", line)
            if not m:
                continue
            key, value = m.group(1).lower(), m.group(2).strip()
            if key == "run":
                bits = value.split()
                meta["runs"].append({"cfg": bits[0], "conditions": bits[1:]})
            else:
                meta["name" if key == "mutant" else key] = value
        meta.setdefault("name", f[:-len(".patch")])
        meta.setdefault("kind", "bug")
        if names and meta["name"] not in names:
            continue
        out.append(meta)
    return out


def parse_patch(text):
    files, cur, lines, i = [], None, text.splitlines(), 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            path = lines[i + 1][4:].split("\t")[0].strip()
            if path.startswith("b/"):
                path = path[2:]
            cur = {"path": path, "hunks": []}
            files.append(cur)
            i += 2
            continue
        if line.startswith("@@") and cur is not None:
            hunk = {"old": [], "new": []}
            cur["hunks"].append(hunk)
            i += 1
            while i < len(lines) and not lines[i].startswith("@@") and not (lines[i].startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ ")):
                h = lines[i]
                if h.startswith(" ") or h == "":
                    hunk["old"].append(h[1:])
                    hunk["new"].append(h[1:])
                elif h.startswith("-"):
                    hunk["old"].append(h[1:])
                elif h.startswith("+"):
                    hunk["new"].append(h[1:])
                i += 1
            continue
        i += 1
    if not files:
        raise PatchError("no file in the patch")
    return files


def apply_patch(text, root):
    """Strict apply: every hunk's old lines must occur exactly once (line numbers are ignored)."""
    for f in parse_patch(text):
        path = os.path.join(root, f["path"])
        if not os.path.isfile(path):
            raise PatchError("%s: no such file in the tree" % f["path"])
        with open(path, encoding="utf-8", newline="") as fh:
            content = fh.read()
        nl = "\r\n" if "\r\n" in content else "\n"
        for k, h in enumerate(f["hunks"]):
            old = nl.join(h["old"]) + nl
            new = nl.join(h["new"]) + nl
            hits, start = [], 0
            while True:
                pos = content.find(old, start)
                if pos < 0:
                    break
                if pos == 0 or content[pos - 1] == "\n":
                    hits.append(pos)
                start = pos + 1
            if len(hits) != 1:
                raise PatchError("%s: hunk %d matches %d times (the tree differs from the one the mutant was written for)"
                                 % (f["path"], k + 1, len(hits)))
            content = content[:hits[0]] + new + content[hits[0] + len(old):]
        os.chmod(path, 0o644)
        with open(path, "w", encoding="utf-8", newline="") as fh:
            fh.write(content)


# ---------------------------------------------------------------------------
# copies and runs
# ---------------------------------------------------------------------------

def _rmtree(path):
    if os.path.lexists(path):
        for root, dirs, files in os.walk(path):
            os.chmod(root, 0o755)
        shutil.rmtree(path)


def copy_tree(tree, commit, dest):
    """The parts of the tree a module run needs; parts an extracted tree lacks come from git archive."""
    _rmtree(dest)
    os.makedirs(dest)
    ignore = shutil.ignore_patterns("bin", "obj", "node_modules", "*.meta", "Samples~", ".vitepress")
    lacking = []
    for name in COPY_PARTS:
        p = os.path.join(tree, name)
        if os.path.isdir(p):
            # Fresh modification times: no build of the same label is ever taken for up to date.
            shutil.copytree(p, os.path.join(dest, name), ignore=ignore, copy_function=shutil.copy)
        elif os.path.isfile(p):
            shutil.copy(p, os.path.join(dest, name))
        else:
            lacking.append(name)
    got = S.git_extract(commit, lacking, dest) if lacking and commit else []
    for root, dirs, files in os.walk(dest):
        os.chmod(root, 0o755)
        for f in files:
            os.chmod(os.path.join(root, f), 0o644)
    return [x for x in lacking if x not in got]


def module_run(src, flavor, cfg, mode, base_src, report_dir, log_path):
    args = [sys.executable, "-I", "-B", os.path.join(S.TOOLS, "alloc.py")]
    if cfg == "ci":
        args += ["--ci-parity", "--" + mode, "--src", src, "--flavor", flavor, "--report-dir", report_dir]
    else:
        args += ["--" + mode, "--src", src, "--flavor", flavor, "--baseline", PREFIX + "base", "--baseline-src", base_src,
                 "--no-mem", "--no-timing", "--report-dir", report_dir]
        if cfg == "dbg":
            args.append("--debug")
    t0 = time.time()
    with open(log_path, "w", encoding="utf-8") as log:
        log.write("# " + " ".join(S.rel(a) if os.path.isabs(a) else a for a in args) + "\n\n")
        log.flush()
        code = subprocess.call(args, stdout=log, stderr=subprocess.STDOUT)
    rep = None
    try:
        with open(os.path.join(report_dir, "report.json"), encoding="utf-8") as f:
            rep = json.load(f)
        if os.path.getmtime(os.path.join(report_dir, "report.json")) < t0:
            rep = None
    except (OSError, ValueError):
        rep = None
    return code, rep, round(time.time() - t0, 1)


def items_of(rep, cfg):
    """{key: item} of what a run reports beyond documented allocations; key = (status, signature)."""
    out = {}
    if rep is None:
        return out
    if cfg == "ci":
        for key in ("findings", "test_artifacts"):
            for f in rep.get(key) or []:
                status = f.get("status") or "finding"
                out[(status, f.get("signature") or f["id"])] = {"id": f["id"], "status": status, "scenario": f.get("test"),
                                                                "cause": f.get("cause_class"), "signature": f.get("signature") or f["id"]}
        return out
    for key, status in (("findings", "finding"), ("regressions", "regression"), ("contract_questions", "question")):
        for f in rep.get(key) or []:
            out[(status, f.get("signature") or f["id"])] = {"id": f["id"], "status": status, "scenario": f.get("scenario"),
                                                            "cause": ",".join(f.get("cause_keys") or []), "signature": f.get("signature") or f["id"]}
    return out


def check_conditions(new, conditions):
    """(ok, unmet) for the new items of a run against the run line's conditions. A run line must name what it expects
    (a scenario or a status, and for a ci run a cause): only a new item of that kind kills, anything else is
    caught-elsewhere (a failure of the self-test)."""
    scen = [c for c in conditions if "=" not in c]
    unmet = []
    if not scen and not any(c.startswith(("status=", "cause=", "cause!=")) for c in conditions):
        return False, ["the Run line names no expected scenario, status or cause (unspecified)"]
    cands = list(new)
    if scen:
        cands = [x for x in cands if x["scenario"] in scen]
        if not cands:
            unmet.append("no new item in " + ", ".join(scen))
    for c in conditions:
        if "=" not in c:
            continue
        neg = "!=" in c
        k, v = c.split("!=" if neg else "=", 1)
        attr = {"status": "status", "cause": "cause"}.get(k, k)
        good = [x for x in cands if ((x.get(attr) or "") != v) == neg]
        if not good:
            unmet.append("no new item with %s" % c)
        else:
            cands = good
    return not unmet and bool(new), unmet


def cleanup():
    """The copies are temporary: remove them and the build outputs of the self-test flavors (the reports stay;
    their repro folders point into the copies, so pass --keep to rebuild a repro of a mutant)."""
    _rmtree(WORKDIR)
    for parent in (os.path.join(S.OUT, "bin"), os.path.join(S.OUT, "obj"), os.path.join(S.OUT, "ci"),
                   os.path.join(S.WORK, "ci"), os.path.join(S.WORK, "runs")):
        if os.path.isdir(parent):
            for name in os.listdir(parent):
                if name.startswith(PREFIX):
                    _rmtree(os.path.join(parent, name))


def main(args, mode):
    src = os.path.abspath(args.src)
    if not os.path.isdir(os.path.join(src, "Core")):
        raise S.StandError("--src must be a KenseiECS package folder (with Core/): " + src)
    flavor = args.flavor or S.default_flavor(src)
    tree = S.tree_of(src)
    commit = S.tree_commit(src, flavor)
    names = set(args.mutants.split(",")) if args.mutants else None
    mutants = load_mutants(names)
    if not mutants:
        raise S.StandError("no mutants in %s%s" % (S.rel(MUTANTS), " named " + args.mutants if names else ""))
    out_dir = os.path.join(S.REPORTS, "selftest-" + flavor)
    runs_dir = os.path.join(out_dir, "runs")
    _rmtree(out_dir)
    os.makedirs(runs_dir)
    started = time.time()
    version = S.contract_version(src)
    S.log("selftest of %s (%s, contract version %s): %d mutants, copies in %s" % (S.rel(src), flavor, version or "unknown",
                                                                                len(mutants), S.rel(WORKDIR)))

    # The copies and build outputs of an earlier self-test (of this tree or another one) go first.
    cleanup()
    os.makedirs(WORKDIR)
    missing = copy_tree(tree, commit, os.path.join(WORKDIR, "base"))
    base_src = os.path.join(WORKDIR, "base", "KenseiECS")
    copy_tree(tree, commit, os.path.join(WORKDIR, "control"))
    control_src = os.path.join(WORKDIR, "control", "KenseiECS")

    # Mutated copies first: a mutant that does not apply needs no runs.
    rows = []
    for m in mutants:
        row = {"name": m["name"], "kind": m.get("kind"), "origin": m.get("origin"), "why": m.get("why"), "patch": m["file"],
               "runs": [], "since": m.get("since"), "until": m.get("until")}
        skip = S.version_skip(m, version)
        if skip:
            row["status"], row["detail"] = "n/a", skip
            rows.append(row)
            continue
        dest = os.path.join(WORKDIR, m["name"])
        copy_tree(tree, commit, dest)
        try:
            apply_patch(m["text"], dest)
            row["src"] = os.path.join(dest, "KenseiECS")
        except PatchError as e:
            row["status"], row["detail"] = "not-applicable", str(e)
            _rmtree(dest)
        rows.append(row)

    cfgs = []
    for m, row in zip(mutants, rows):
        if row.get("src"):
            for r in m["runs"]:
                if r["cfg"] not in cfgs:
                    cfgs.append(r["cfg"])
    controls = {}
    for cfg in cfgs:
        rd = os.path.join(runs_dir, "control-" + cfg)
        code, rep, secs = module_run(control_src, PREFIX + "control", cfg, mode, base_src, rd, os.path.join(runs_dir, "control-%s.log" % cfg))
        controls[cfg] = {"code": code, "rep": rep, "seconds": secs, "items": items_of(rep, cfg),
                         "report": S.rel(os.path.join(rd, "report.md")), "log": S.rel(os.path.join(runs_dir, "control-%s.log" % cfg))}
        S.log("control %s: exit %d, %d items, %.0f s" % (cfg, code, len(controls[cfg]["items"]), secs))

    for m, row in zip(mutants, rows):
        if not row.get("src"):
            S.log("%-28s %s: %s" % (m["name"], "n/a" if row["status"] == "n/a" else "not applicable", row["detail"]))
            continue
        verdicts = []
        for r in m["runs"]:
            cfg = r["cfg"]
            rd = os.path.join(runs_dir, "%s-%s" % (m["name"], cfg))
            log = os.path.join(runs_dir, "%s-%s.log" % (m["name"], cfg))
            code, rep, secs = module_run(row["src"], PREFIX + m["name"], cfg, mode, base_src, rd, log)
            run = {"cfg": cfg, "conditions": r["conditions"], "exit": code, "seconds": secs,
                   "report": S.rel(os.path.join(rd, "report.md")), "log": S.rel(log)}
            ctrl = controls.get(cfg) or {}
            if rep is None or ctrl.get("rep") is None:
                run.update(status="failed", detail="the %s run wrote no report (see %s)" % ("control" if ctrl.get("rep") is None else "mutant", run["log"]))
            else:
                items = items_of(rep, cfg)
                new = [v for k, v in items.items() if k not in ctrl["items"]]
                ok, unmet = check_conditions(new, r["conditions"])
                run["new"] = new
                run["status"] = "killed" if ok else ("caught-elsewhere" if new else "SURVIVED")
                run["detail"] = ("new: " + ", ".join("%s %s" % (x["status"], x["signature"]) for x in new[:6]) +
                                 (" ..." if len(new) > 6 else "") if new else "nothing new against the control") + \
                                ("" if ok else "; " + "; ".join(unmet))
            verdicts.append(run["status"])
            row["runs"].append(run)
            S.log("%-28s %-4s %-16s %s" % (m["name"], cfg, run["status"], run["detail"][:160]))
        row["status"] = "killed" if verdicts and all(v == "killed" for v in verdicts) else (
            "failed" if "failed" in verdicts else "SURVIVED")
        row["detail"] = "; ".join("%s: %s" % (r["cfg"], r["status"]) for r in row["runs"])

    # The control run must be clean: no finding or regression the baseline copy does not explain.
    false_pos = {}
    for cfg, c in controls.items():
        if c["rep"] is None:
            false_pos[cfg] = ["no report (see %s)" % c["log"]]
            continue
        extra = [v for v in c["items"].values() if v["status"] == "regression"]
        if extra:
            false_pos[cfg] = ["%s %s" % (x["status"], x["signature"]) for x in extra]
    bugs = [r for r in rows if r["kind"] == "bug" and r["status"] not in ("n/a", "not-applicable")]
    killed = sum(1 for r in bugs if r["status"] == "killed")
    broken = [r for r in rows if r["status"] == "not-applicable"]
    doc = {"module": S.MODULE, "format": 1, "src": src, "src_rel": S.rel(src), "flavor": flavor, "mode": mode,
           "contractVersion": version, "copies": S.rel(WORKDIR), "missing_parts": missing, "errors": len(broken),
           "killed": killed, "bugMutants": len(bugs), "notApplicable": len(broken),
           "skippedByVersion": [r["name"] for r in rows if r["status"] == "n/a"],
           "controlClean": not false_pos, "falsePositives": false_pos,
           "controls": {cfg: {"exit": c["code"], "seconds": c["seconds"], "report": c["report"], "items": sorted(
               "%s %s" % k for k in c["items"])} for cfg, c in controls.items()},
           "seconds": round(time.time() - started, 1), "mutants": rows}
    with open(os.path.join(out_dir, "selftest.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    with open(os.path.join(out_dir, "selftest.md"), "w", encoding="utf-8") as f:
        f.write(markdown(doc))
    if not args.keep:
        cleanup()
    S.log("selftest: %d/%d bug mutants killed, control %s -> %s" % (
        killed, len(bugs), "clean" if not false_pos else "NOT clean", S.rel(os.path.join(out_dir, "selftest.md"))))
    return 0 if killed == len(bugs) and not false_pos and not broken else 1


def markdown(doc):
    L = []
    w = L.append
    w("# Stand/alloc: self-test (%s)" % doc["flavor"])
    w("")
    w("Mutants are patches in `Stand/alloc/mutants/`, applied to copies of `%s` in `%s`; the tree itself is not changed. "
      "Each mutant run is compared with a run on an unmutated copy (the control), both measured against the same baseline "
      "copy: a mutant is killed when its run reports an item (by status and signature) the control does not, where the "
      "patch says it must. The control itself must report no regression: anything it reports against an identical tree "
      "would be a false positive." % (doc["src_rel"], doc["copies"]))
    w("")
    w("Contract version of the tree: %s; skipped by it (their Since/Until range): %s." % (
        doc.get("contractVersion") or "unknown", ", ".join(doc.get("skippedByVersion") or []) or "none"))
    w("")
    w("Killed %d of %d bug mutants (%d within their version range do not apply: a failure); control %s; mode --%s; %.0f s." % (
        doc["killed"], doc["bugMutants"], doc["notApplicable"], "clean" if doc["controlClean"] else
        "**NOT clean**: " + "; ".join("%s: %s" % (k, ", ".join(v)) for k, v in doc["falsePositives"].items()), doc["mode"], doc["seconds"]))
    if doc["missing_parts"]:
        w("")
        w("Parts the tree lacks (neither in the folder nor in git): %s." % ", ".join(doc["missing_parts"]))
    w("")
    w("| mutant | kind | origin | run | result | details |")
    w("|---|---|---|---|---|---|")
    for r in doc["mutants"]:
        if not r["runs"]:
            w("| %s | %s | %s | - | %s | %s |" % (r["name"], r["kind"], r.get("origin") or "", r["status"], (r.get("detail") or "").replace("|", "\\|")))
            continue
        for k, run in enumerate(r["runs"]):
            w("| %s | %s | %s | %s %s | %s | %s |" % (
                r["name"] if k == 0 else "", r["kind"] if k == 0 else "", (r.get("origin") or "") if k == 0 else "",
                run["cfg"], " ".join(run["conditions"]), run["status"], run["detail"].replace("|", "\\|")))
    w("")
    w("What each mutant does:")
    w("")
    for r in doc["mutants"]:
        w("- `%s` (`%s`): %s" % (r["name"], r["patch"], r.get("why") or ""))
    w("")
    w("Controls: " + "; ".join("%s: exit %s, %s s, [report](%s), items: %s" % (
        cfg, c["exit"], c["seconds"], os.path.relpath(os.path.join(S.WT, c["report"]), os.path.join(S.REPORTS, "selftest-" + doc["flavor"])),
        ", ".join(c["items"]) or "none") for cfg, c in doc["controls"].items()) + ".")
    w("")
    return "\n".join(L) + "\n"
