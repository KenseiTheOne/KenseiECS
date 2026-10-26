#!/usr/bin/env python3
"""Stand/alloc: allocation and memory audit across runtimes, builds and versions.

Normal mode measures the target tree and the baseline tree with the same probes, on .NET 10 and
.NET 8, in one build (release, or KENSEI_DEBUG with --debug), and writes
Stand/out/reports/alloc/<flavor>-<rel|dbg>/report.md, report.json and repro/ (one folder per item:
Program.cs + .csproj, built and run once to check that it reproduces). Statuses: findings (contract
violations of the tree's own docs), regressions (more than the baseline, the contract does not forbid
it), contract questions, documented allocations; see tools/analyze.py and tools/contracts.py.

Every candidate is confirmed before it is reported: its scenario runs again in fresh processes (target
and baseline), and a one-time difference is explained by attribution runs (--pretouch-type-names,
--run-cctors-of, DOTNET_TieredCompilation=0) instead of a guess; a difference that only the runtime's
code generation explains is listed, not reported.

--ci-parity runs the test projects of the tree on .NET 8 the way CI does (KenseiECS.Tests in release
and KENSEI_DEBUG, KenseiECS.Generators.Tests in release) and writes Stand/out/reports/alloc/<flavor>-ci/.
--check-repros builds and runs the repro template of every scenario against the tree and compares it
with the stand's own numbers (maintenance of tools/repro.py).
--selftest applies the mutants of Stand/alloc/mutants/*.patch to a copy of the tree and checks that the
module catches each one (tools/selftest.py).
"""
import argparse
import datetime
import json
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import standlib as S  # noqa: E402

RUNTIMES = ("net10", "net8")
CONFIRM_RUNS = 3
ATTRIBUTION_RUNS = 2


def parse_args(argv):
    p = argparse.ArgumentParser(prog="Stand/alloc/run.sh", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    g = p.add_mutually_exclusive_group()
    g.add_argument("--quick", action="store_true", help="short windows, one process per scenario, N = 100/1000 (default)")
    g.add_argument("--full", action="store_true", help="long windows, 3 fresh processes per scenario, N = 100/300/1000")
    p.add_argument("--src", default=os.path.join(S.WT, "KenseiECS"), help="KenseiECS package folder under test (default: the working copy)")
    p.add_argument("--flavor", help="label of that tree (default: wt, the version folder name, or a hash)")
    p.add_argument("--debug", action="store_true", help="build with KENSEI_DEBUG")
    p.add_argument("--baseline", default="v2.1.1", help="baseline flavor; its sources are Stand/out/src/<flavor>/KenseiECS (default v2.1.1)")
    p.add_argument("--baseline-src", help="baseline KenseiECS folder, if not under Stand/out/src")
    p.add_argument("--runtimes", default=",".join(RUNTIMES), help="comma list of net10, net8")
    p.add_argument("--only", help="comma list of scenario ids (memory scenarios: mem)")
    p.add_argument("--no-isolate", action="store_true", help="all scenarios of a version in one process instead of one process each")
    p.add_argument("--no-mem", action="store_true", help="skip the Reserve memory scenarios")
    p.add_argument("--no-timing", action="store_true", help="skip the ns/op pass")
    p.add_argument("--no-confirm", action="store_true", help="skip confirmation and attribution runs of candidates")
    p.add_argument("--no-repro-check", action="store_true", help="write repro folders but do not build and run them")
    p.add_argument("--ci-parity", action="store_true", help="run the test projects of the tree on .NET 8 like CI instead")
    p.add_argument("--check-repros", action="store_true", help="build and run every repro template against the tree")
    p.add_argument("--selftest", action="store_true", help="mutation self-test: every mutant of Stand/alloc/mutants must be caught")
    p.add_argument("--mutants", help="--selftest: comma list of mutant names (default: all)")
    p.add_argument("--keep", action="store_true", help="--selftest: keep the mutated copies under Stand/out/selftest/alloc/")
    p.add_argument("--report-dir", help=argparse.SUPPRESS)   # used by --selftest for its own runs
    return p.parse_args(argv)


def resolve(args):
    src = os.path.abspath(args.src)
    if not os.path.isdir(os.path.join(src, "Core")):
        raise S.StandError("--src must be a KenseiECS package folder (with Core/): " + src)
    flavor = args.flavor or S.default_flavor(src)
    if args.baseline_src:
        bsrc = os.path.abspath(args.baseline_src)
    else:
        bsrc = os.path.join(S.OUT, "src", args.baseline, "KenseiECS")
    if not os.path.isdir(os.path.join(bsrc, "Core")):
        raise S.StandError("baseline sources not found: %s\n(Stand/run.sh extracts a version read-only, for example\n"
                           "  Stand/run.sh --summary-only --ref %s --modules alloc\n"
                           "or pass --baseline-src <KenseiECS folder>)" % (S.rel(bsrc), args.baseline))
    runtimes = [r.strip() for r in args.runtimes.split(",") if r.strip()]
    for r in runtimes:
        if r not in RUNTIMES:
            raise S.StandError("unknown runtime " + r)
    return src, flavor, bsrc, args.baseline, runtimes


def plan(mode):
    if mode == "full":
        return {"repeats": 3, "mem_types": [100, 300, 1000],
                "mem_layouts": ["organic_rare", "organic_spread", "reserve_rare", "reserve_spread", "reserve_late_rare"],
                "mem_filters_for_1000": [0, 100]}
    return {"repeats": 1, "mem_types": [100, 1000],
            "mem_layouts": ["organic_rare", "organic_spread", "reserve_rare", "reserve_late_rare"],
            "mem_filters_for_1000": [0, 100]}


def build_probes(src, flavor, debug, mem):
    with S.Lock("%s-%s" % (flavor, "dbg" if debug else "rel")):
        probe_dll = S.build("Probe", src, flavor, debug)
        mem_dll = S.build("Mem", src, flavor, debug) if mem else None
    return probe_dll, mem_dll


def measure_version(role, src, flavor, debug, runtimes, mode, pl, only, isolate, timing, mem):
    """Build the probes for one tree and run every scenario on every runtime."""
    data = {"role": role, "flavor": flavor, "src": src, "package_version": S.package_version(src),
            "runtimes": {}}
    probe_dll, mem_dll = build_probes(src, flavor, debug, mem)
    data["probe_dll"] = probe_dll
    host, env = S.runtime_env(runtimes[0])
    _, out, _ = S.run([host, probe_dll, "--list"], env)
    ids = [x.strip() for x in out.splitlines() if x.strip()]
    if only:
        ids = [i for i in ids if i in only]
    data["scenarios"] = ids
    for rt in runtimes:
        rd = {"info": None, "results": {}, "timing": {}, "timing_info": None, "mem": []}
        t0 = time.time()
        # Allocation pass: default tiered JIT, each scenario in a fresh process (repeats times).
        groups = [[i] for i in ids] if isolate else [ids]
        for rep in range(pl["repeats"]):
            for g in groups:
                if not g:
                    continue
                info, results, err = S.probe(probe_dll, rt, ["--run", ",".join(g), "--mode", mode, "--no-timing"])
                rd["info"] = rd["info"] or info
                if info and not info.get("selftest_ok"):
                    rd.setdefault("selftest_failures", []).append(info)
                for r in results:
                    rd["results"].setdefault(r["id"], []).append(r)
        # Timing pass: fully optimized code (TieredCompilation=0), close to IL2CPP and stable.
        if timing and ids:
            info, results, err = S.probe(probe_dll, rt, ["--run", ",".join(ids), "--mode", mode],
                                         extra_env={"DOTNET_TieredCompilation": "0"})
            rd["timing_info"] = info
            for r in results:
                rd["timing"][r["id"]] = r
        # Memory pass: one layout per process.
        if mem_dll and (not only or "mem" in only):
            for n in pl["mem_types"]:
                filters_list = pl["mem_filters_for_1000"] if n == 1000 else [0]
                for layout in pl["mem_layouts"]:
                    for f in filters_list:
                        if f and layout not in ("organic_rare", "reserve_rare"):
                            continue
                        rd["mem"].append(S.mem_probe(mem_dll, rt, n, 50000, layout, f))
        S.log("%s %s on %s: %d scenarios in %.1fs" % (role, flavor, rt, len(ids), time.time() - t0))
        data["runtimes"][rt] = rd
    return data


_STATIC_TYPES = {}


def static_types(dll, rt):
    """KenseiECS types with static fields, listed by the probe in a process of its own (cached per dll)."""
    if dll not in _STATIC_TYPES:
        host, env = S.runtime_env(rt)
        _, out, _ = S.run([host, dll, "--list-static-types"], env)
        _STATIC_TYPES[dll] = [x.strip() for x in out.splitlines() if x.strip()]
    return _STATIC_TYPES[dll]


def confirm_and_attribute(raw, mode, isolate):
    """Re-runs every candidate scenario in fresh processes (target and baseline) and runs the
    attribution knobs for one-time candidates. Adds the runs to the results; records what ran."""
    import analyze
    cands = analyze.candidates(raw)
    raw["confirmations"] = {}
    raw["attribution"] = {}
    if not cands:
        return
    roles = [("target", raw["target"])]
    if raw["baseline_data"] is not raw["target"]:
        roles.append(("baseline", raw["baseline_data"]))
    for sid, per_rt in sorted(cands.items()):
        for rt, metrics in sorted(per_rt.items()):
            for role, data in roles:
                if sid not in data.get("scenarios", []):
                    continue
                prior = data["runtimes"][rt]["results"].get(sid, [])
                if prior and not all(r.get("available", True) for r in prior):
                    continue
                dll = data["probe_dll"]
                n = CONFIRM_RUNS if isolate else 0
                for _ in range(n):
                    _, results, _ = S.probe(dll, rt, ["--run", sid, "--mode", mode, "--no-timing"])
                    for r in results:
                        data["runtimes"][rt]["results"].setdefault(r["id"], []).append(r)
                raw["confirmations"].setdefault(sid, {}).setdefault(rt, {})[role] = n
                if "one-time bytes" in metrics:
                    slot = raw["attribution"].setdefault(sid, {}).setdefault(rt, {}).setdefault(role, {})
                    # The candidate types come from another process, so the measured process looks them up by name
                    # and touches no other type by reflection.
                    statics = static_types(dll, rt)
                    # (probe arguments, environment): two kinds of one-time work done beforehand by the probe, and
                    # the runtime compiling every method fully optimized at once (no tier-0 code, no ReadyToRun
                    # rejit; object stack allocation off, so no real allocation is optimized away), which tells
                    # code generation of the runtime apart from allocations of the library.
                    knobs = {"pretouch_type_names": (["--pretouch-type-names"], None),
                             "run_cctors": (["--run-cctors-of", ",".join(statics)], None) if statics else None,
                             "fullopts": ([], {"DOTNET_TieredCompilation": "0", "DOTNET_JitObjectStackAllocation": "0"})}
                    for knob, spec in knobs.items():
                        if spec is None:
                            continue
                        extra, env = spec
                        firsts = []
                        for _ in range(ATTRIBUTION_RUNS):
                            _, results, _ = S.probe(dll, rt, ["--run", sid, "--mode", mode, "--no-timing"] + extra, extra_env=env)
                            firsts += [r["first_bytes"] for r in results if r.get("first_bytes") is not None]
                        slot[knob] = firsts
                    plain = [r["first_bytes"] for r in data["runtimes"][rt]["results"].get(sid, []) if r.get("first_bytes") is not None]
                    if slot.get("run_cctors") and plain and min(slot["run_cctors"]) < min(plain):
                        # Which type's static constructor it is: one fresh process per candidate type.
                        per_type = {}
                        for name in statics:
                            _, results, _ = S.probe(dll, rt, ["--run", sid, "--mode", mode, "--no-timing", "--run-cctors-of", name])
                            vals = [r["first_bytes"] for r in results if r.get("first_bytes") is not None]
                            if vals:
                                per_type[name] = min(vals)
                        slot["cctor_of"] = per_type
            S.log("confirmed %s on %s (%s)" % (sid, rt, ", ".join(sorted(metrics))))


def run_report(args, argv):
    import analyze
    import repro
    mode = "full" if args.full else "quick"
    src, flavor, bsrc, bflavor, runtimes = resolve(args)
    debug = args.debug
    tag = "dbg" if debug else "rel"
    pl = plan(mode)
    only = set(args.only.split(",")) if args.only else None
    S.log("target %s (%s), baseline %s (%s), %s, %s, runtimes %s" % (flavor, S.rel(src), bflavor, S.rel(bsrc), tag, mode, ",".join(runtimes)))
    if "net8" in runtimes:
        v8 = S.ensure_mixed_root()
        S.log(".NET 8 runtime %s in %s" % (v8, S.rel(S.MIXED_ROOT)))
    started = time.time()
    raw = {
        "module": S.MODULE, "flavor": flavor, "config": tag, "mode": mode, "baseline": bflavor,
        "generated": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "command": "Stand/alloc/run.sh " + " ".join(S.rel(a) if os.path.isabs(a) else a for a in argv),
        "isolate": not args.no_isolate, "plan": pl, "runtimes": runtimes,
        "confirm_runs": 0 if args.no_confirm or args.no_isolate else CONFIRM_RUNS,
    }
    common = dict(debug=debug, runtimes=runtimes, mode=mode, pl=pl, only=only,
                  isolate=not args.no_isolate, timing=not args.no_timing, mem=not args.no_mem)
    raw["target"] = measure_version("target", src, flavor, **common)
    if os.path.abspath(bsrc) == os.path.abspath(src):
        raw["baseline_data"] = raw["target"]
    else:
        raw["baseline_data"] = measure_version("baseline", bsrc, bflavor, **common)
    if not args.no_confirm:
        confirm_and_attribute(raw, mode, not args.no_isolate)

    report_dir = os.path.abspath(args.report_dir) if args.report_dir else os.path.join(S.REPORTS, "%s-%s" % (flavor, tag))
    rep = analyze.Report(raw, report_dir).analyze()
    folders = rep.write_repros()
    if folders and not args.no_repro_check:
        items = [i for i in rep.findings + rep.regressions + rep.questions if i["id"] in folders]
        baseline = None if os.path.abspath(bsrc) == os.path.abspath(src) else (bsrc, bflavor)
        n = repro.check(items, folders, src, flavor, debug, runtimes, baseline)
        S.log("repro check: %d of %d runtime runs reproduce the stand's numbers" % (n, len(items) * len(runtimes)))
    raw["seconds"] = round(time.time() - started, 1)
    run_dir = os.path.join(S.WORK, "runs", "%s-%s" % (flavor, tag))
    os.makedirs(run_dir, exist_ok=True)
    with open(os.path.join(run_dir, "raw.json"), "w", encoding="utf-8") as f:
        json.dump(raw, f, ensure_ascii=False, indent=1, default=list)
    summary = rep.write()
    S.log("report: %s (%s)" % (S.rel(os.path.join(report_dir, "report.md")), summary))
    return 0


def check_repros(args, argv):
    """Every template of tools/repro.py against the tree: build, run, compare with a fresh probe run."""
    import repro
    mode = "full" if args.full else "quick"
    src, flavor, _, _, runtimes = resolve(args)
    debug = args.debug
    tag = "dbg" if debug else "rel"
    if "net8" in runtimes:
        S.ensure_mixed_root()
    probe_dll, _ = build_probes(src, flavor, debug, False)
    host, env = S.runtime_env(runtimes[0])
    _, out, _ = S.run([host, probe_dll, "--list"], env)
    ids = [x.strip() for x in out.splitlines() if x.strip()]
    if args.only:
        ids = [i for i in ids if i in set(args.only.split(","))]
    out_dir = os.path.join(S.REPORTS, "%s-%s-repro-check" % (flavor, tag))
    repro_dir = os.path.join(out_dir, "repro")
    shutil.rmtree(repro_dir, ignore_errors=True)
    items, folders, missing = [], {}, []
    for sid in ids:
        if not repro.available(sid):
            missing.append(sid)
            continue
        per_rt = {}
        for rt in runtimes:
            _, results, _ = S.probe(probe_dll, rt, ["--run", sid, "--mode", mode, "--no-timing"])
            per_rt[rt] = results[0] if results else {}
        r0 = per_rt[runtimes[0]]
        if not r0.get("available", True):
            missing.append(sid + " (not in this version)")
            continue
        metrics = []
        if r0.get("first_bytes") is not None and repro.T[sid].get("first"):
            metrics.append(("one-time bytes", "first"))
        if r0.get("steady_windows") and repro.T[sid].get("steady") and not repro.T[sid].get("spans"):
            metrics.append(("bytes/op", "steady"))
        if r0.get("span_bytes"):
            metrics.append(("long-run", "longrun"))
        for metric, short in metrics:
            det = {}
            for rt, r in per_rt.items():
                if metric == "one-time bytes":
                    det[rt] = {"first": r.get("first_bytes")}
                elif metric == "bytes/op":
                    w = r.get("steady_windows") or [0]
                    det[rt] = {"per_op": sum(w) / (len(w) * r["per_window"])}
                else:
                    det[rt] = {"span_bytes": r.get("span_bytes")}
            item = {"id": "check.%s.%s" % (short, sid), "scenario": sid, "title": sid, "metric": metric, "runtimes": det,
                    "params": dict(r0.get("extra", {}), mode=mode, kensei_debug=debug), "seed": 0}
            folder, _ = repro.write(item, repro_dir, src, flavor, "KENSEI_DEBUG" if debug else "release")
            if folder:
                folders[item["id"]] = folder
                items.append(item)
    n = repro.check(items, folders, src, flavor, debug, runtimes)
    os.makedirs(out_dir, exist_ok=True)
    lines = ["# Stand/alloc --check-repros: %s, %s" % (flavor, "KENSEI_DEBUG" if debug else "release"), "",
             "Every template of tools/repro.py built against `%s` and run on its own; its number is compared with a fresh "
             "measurement of the stand." % S.rel(src), "",
             "| repro | metric | runtime | status | repro | stand |", "|---|---|---|---|---|---|"]
    bad = 0
    for it in items:
        rc = it.get("repro_check") or {}
        if rc.get("build") == "failed":
            bad += 1
            lines.append("| %s | %s | - | **build failed** | | |" % (it["scenario"], it["metric"]))
            continue
        for rt, v in rc.items():
            if not (v.get("status") or "").startswith(("reproduces", "n/a")):
                bad += 1
            lines.append("| %s | %s | %s | %s | %s | %s |" % (it["scenario"], it["metric"], rt, v.get("status"),
                                                             json.dumps(v.get("repro")), json.dumps(v.get("stand"))))
    if missing:
        lines += ["", "No template or not available: " + ", ".join(missing)]
    lines += ["", "Result: %d runs reproduce, %d do not or did not build." % (n, bad)]
    with open(os.path.join(out_dir, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"flavor": flavor, "config": tag, "items": items, "missing": missing, "reproduced": n, "not_reproduced": bad},
                  f, ensure_ascii=False, indent=1, default=list)
    S.log("repro check: %s (%d reproduce, %d do not)" % (S.rel(os.path.join(out_dir, "report.md")), n, bad))
    return 0 if bad == 0 else 1


def main(argv):
    args = parse_args(argv)
    mode = "full" if args.full else "quick"
    if args.selftest:
        import selftest
        return selftest.main(args, mode)
    if args.ci_parity:
        import cipar
        return cipar.main(args, mode, args.report_dir)
    if args.check_repros:
        return check_repros(args, argv)
    return run_report(args, argv)


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except S.StandError as e:
        S.log("error: " + str(e))
        sys.exit(2)
