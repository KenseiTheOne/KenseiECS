"""Turns raw.json of one run into report.md, report.json and repro folders.

Statuses (report.json lists):
  findings            contract violations: an allocation where the documentation of the tree under
                      test promises none (both gates of tools/contracts.py: the tree's own docs and the
                      version profile). These are the only defects.
  regressions         more than the baseline (same runtime, same build) where the contract does not
                      forbid it: bytes per operation in every window; one-time bytes (target minimum >
                      baseline maximum over all fresh-process runs, confirmation runs included); a long
                      run that no longer settles, or allocates more than twice the baseline in every span;
                      world memory past a threshold; a larger filter dense capacity for the same request.
  contract_questions  an allocation the documentation of this tree does not cover, without baseline growth
  documented          an allocation the documentation of this tree describes
A violation that also grew carries the baseline numbers in "regression"; a regression the contract leaves
open carries the question in "contract". Every item has a "signature" (metric : scenario : mechanism),
which the summary compares with the baseline and the catalog matches, so a new mechanism in a known place,
or a known mechanism in a new place, is never folded into a known problem.

Byte counts are deterministic on CoreCLR, so steady state is "at least 1 B/op more, in every window"; a
window that allocates once while the others do not is reported as sporadic and never becomes an item.
"""
import json
import os
import shutil
import statistics

import contracts as C
import repro as R
import standlib as S

TITLES = {
    "create_destroy": "CreateEntity + DestroyEntity",
    "add_remove.plain": "Add/Remove: struct without references",
    "add_remove.tag": "Add/Remove: empty tag",
    "add_remove.big": "Add/Remove: 64-byte struct",
    "add_remove.managed": "Add/Remove: struct with a reference",
    "add_remove.autoreset": "Add/Remove: IAutoReset",
    "add_remove.autocopy": "Add/Remove: IAutoCopy",
    "add_remove.tracked": "Add/Remove: TrackChanges",
    "add_remove.listener": "Add/Remove: IComponentListener",
    "add_remove.group": "Add/Remove: member of an owning group",
    "add_remove.filtered": "Add/Remove: type in 4 filters (Inc/Exc/Any)",
    "add_remove.eventbuffer": "AddEvent + Remove EventBuffer",
    "add_remove.listeners": "Subscribe + Remove Listeners",
    "events.first_frame_prewarm": "OneFrame<EventBuffer>: first peak frame after Prewarm",
    "events.first_frame_no_prewarm": "OneFrame<EventBuffer>: first peak frame without Prewarm",
    "events.stable_frame": "OneFrame<EventBuffer>: stable frame",
    "events.writeback_frames": "EventBuffer: documented write-back of a copy through cb.Set, long run",
    "invoke.static_lambda": "Listeners.Invoke(arg, static lambda)",
    "invoke.static_lambda_onerror": "Listeners.Invoke(arg, static lambda, static onError)",
    "invoke.method_group": "Listeners.Invoke(arg, method group)",
    "invoke.capturing_lambda": "Listeners.Invoke(capturing lambda)",
    "invoke.manual_loop": "hand-written loop over Listeners.Values",
    "invoke.delegate_overhead": "empty call through the stand's delegate (reflection overhead)",
    "invoke.ratio_paired": "ns Invoke / hand-written loop, paired rounds (timing pass only)",
    "runner.inline_groups": "SystemsRunner.Run: inline groups and disabled slots",
    "runner.named_group": "SystemsRunner.Run: named group (AddGroup) switched off",
    "runner.clean_when_disabled": "SystemsRunner.Run: OneFrame(cleanWhenDisabled) in a disabled group",
    "commandbuffer.playback": "CommandBuffer: a frame of commands + Playback",
    "copy_entity.autocopy": "CopyEntity + DestroyEntity: IAutoCopy without allocations",
    "copy_entity.eventbuffer": "CopyEntity + DestroyEntity: EventBuffer",
    "copy_entity.listeners": "CopyEntity + DestroyEntity: Listeners",
    "foreach.filter": "foreach over a filter (4096 matches)",
    "foreach.nested5": "5 nested foreach loops over a filter",
    "foreach.alive_entities": "foreach over World.AliveEntities",
    "parallel.run": "ParallelRunner.Run (3 workers + the caller), all threads",
    "create.first_entity_of_process": "the first CreateEntity of the process",
    "growth.first_n_pools": "first growth of 100 pools without Reserve",
    "growth.first_n_filters": "first growth of 100 filters without Reserve (sparse page + dense array)",
    "filter.dense_capacity": "filter dense capacity after Reserve and after organic growth",
    "reserve.create_destroy_n": "after Reserve(n, n): create and destroy n entities",
    "reserve.first_entities_of_process": "after Reserve(n, n): the first entities of the process",
    "reserve.pool_first_add_in_frame": "after Reserve(n, n): a pool first created by the first Add in a frame",
    "reserve.pool_created_at_load": "after Reserve(n, n): pool created at load, first Add in a frame",
    "mem.reserve_entities": "memory of Reserve(entities): N types x 50 000 entities",
    "access.world_get_has": "world.Get<T>(Entity) + world.Has<T>(Entity) in a steady frame",
    "access.modify_mark_changed": "pool.Modify + MarkChanged + ChangedSince (TrackChanges) in a steady frame",
    "access.get_singleton": "world.GetSingleton<T>() + GetSingletonEntity<T>() in a steady frame",
    "access.group_data": "a loop over an owning group's Entities, Data1, Data2",
    "access.filter_contains": "filter.Contains(index) over every slot",
}

CLASSES = {
    "contract-violation": ("finding", "contract violation"),
    "regression": ("regression", "regression"),
    "contract-question": ("question", "contract question"),
    "documented": ("documented", "documented allocation"),
}
SHORT = {"regression": "reg", "contract-violation": "contract", "contract-question": "question", "documented": "doc"}
METRICS = {"bytes/op": ("steady", "bytes per operation"), "one-time bytes": ("first", "one-time bytes (first pass)"),
           "long-run": ("longrun", "long run (geometric spans)"), "memory": ("mem", "world memory"),
           "capacity": ("capacity", "dense capacity against the request")}

KNOBS = {
    "pretouch_type_names": ("--pretouch-type-names",
                            "typeof(T).Name of every component type called beforehand",
                            "the first typeof(T).Name of a component type inside the library (the type-name string; on CoreCLR "
                            "also its RuntimeTypeCache, on Mono/IL2CPP only the string the pool caches)"),
    "run_cctors": ("--run-cctors-of <KenseiECS types with static fields>",
                   "the static constructors of those types, looked up by name, run beforehand",
                   "static state of KenseiECS types that the first call creates"),
    "fullopts": ("DOTNET_TieredCompilation=0 (with DOTNET_JitObjectStackAllocation=0)",
                 "every method compiled fully optimized at once: no tier-0 code, no rejit of ReadyToRun code, no objects moved to the stack",
                 "code generation of the runtime (tiered JIT, ReadyToRun), not an allocation of the library"),
}
# Knobs that do the library's own one-time work beforehand; "fullopts" changes only how the runtime compiles.
LIBRARY_KNOBS = ("pretouch_type_names", "run_cctors")

# Memory regression threshold: the World object alone may grow by a field (8 B) between versions.
MEM_REGRESSION_MIN = 4096
MEM_REGRESSION_SHARE = 0.01
# A filter's dense array counts as oversized when it holds this many times the requested slots.
DENSE_OVERSHOOT = 1.25


# ---------------------------------------------------------------------------
# summaries
# ---------------------------------------------------------------------------

def _parse_list(text):
    if text is None:
        return None
    if isinstance(text, list):
        return text
    return [int(x) for x in str(text).split(",") if x != ""]


def summarize(runs):
    if not runs:
        return None
    s = {"available": all(r.get("available", True) for r in runs),
         "reason": next((r.get("reason") for r in runs if r.get("reason")), None),
         "errors": [r["error"] for r in runs if r.get("error")],
         "extra": runs[0].get("extra", {}), "runs": len(runs),
         "counter": runs[0].get("counter", "thread")}
    firsts = [r["first_bytes"] for r in runs if r.get("first_bytes") is not None]
    if firsts:
        s["first_min"], s["first_max"], s["first_all"] = min(firsts), max(firsts), firsts
    extras = [r["first_bytes"] - (r.get("extra") or {}).get("expected_array_bytes", 0) for r in runs
              if r.get("first_bytes") is not None and "expected_array_bytes" in (r.get("extra") or {})]
    if extras:
        s["extra_min"], s["extra_max"] = min(extras), max(extras)
    windows = [w for r in runs for w in (r.get("steady_windows") or [])]
    if windows:
        per_window = runs[0]["per_window"]
        total = sum(windows)
        s["steady"] = {
            "per_op": total / (len(windows) * per_window),
            "min_per_op": min(windows) / per_window,
            "max_window": max(windows), "min_window": min(windows),
            "windows": len(windows), "per_window": per_window,
            "kind": "zero" if total == 0 else ("per-op" if min(windows) > 0 else "sporadic"),
        }
    spans = [r["span_bytes"] for r in runs if r.get("span_bytes")]
    if spans:
        controls = [_parse_list(r.get("extra", {}).get("control_span_bytes")) for r in runs if r.get("span_bytes")]
        never = all(all(b > 0 for b in sp) for sp in spans)
        settles = all(all(b == 0 for b in sp) for sp in spans)
        control_settles = all(c is not None and all(b == 0 for b in c) for c in controls) if any(controls) else None
        s["spans"] = {"bytes": spans, "runs": runs[0].get("span_runs"), "warmup": runs[0].get("span_warmup"),
                      "never_settles": never, "settles": settles, "control": controls, "control_settles": control_settles,
                      "min": [min(sp[i] for sp in spans) for i in range(len(spans[0]))],
                      "max": [max(sp[i] for sp in spans) for i in range(len(spans[0]))]}
    return s


def ns_of(timing_result):
    if not timing_result or not timing_result.get("ns"):
        return None
    return statistics.median(timing_result["ns"])


def fmt_b(v):
    if v is None:
        return "-"
    if isinstance(v, float) and not v.is_integer():
        return "%.2f" % v
    return "{:,}".format(int(v)).replace(",", " ")


def fmt_mb(v):
    return "%.2f MB" % (v / 1e6)


def fmt_num(k, v):
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, (int, float)):
        return fmt_b(v)
    if isinstance(v, list):
        if all(isinstance(x, (int, float)) for x in v):
            return "/".join(fmt_b(x) for x in v)
        return "; ".join(str(x) for x in v)
    if isinstance(v, dict):
        return json.dumps(v, ensure_ascii=False)
    return str(v)


def doubling_signature(spans):
    """Each span equal to one reference array of 2^k slots on 64-bit (24 + 8 * 2^k bytes), with k rising
    by one per span: one doubling of a List<T> of references per span, i.e. a collection that keeps growing."""
    ks = []
    for b in spans:
        n = (b - 24) / 8
        if n <= 0 or n != int(n) or int(n) & (int(n) - 1):
            return None
        ks.append(int(n).bit_length() - 1)
    if all(ks[i + 1] == ks[i] + 1 for i in range(len(ks) - 1)):
        return [1 << k for k in ks]
    return None


def parse_dense_cases(text):
    out = []
    for part in (text or "").split(";"):
        bits = part.split("/")
        if len(bits) == 4:
            out.append({"kind": bits[0], "requested": int(bits[1]), "filter": int(bits[2]), "pool": int(bits[3])})
    return out


def plural(n, word):
    return "%d %s%s" % (n, word, "" if n == 1 else "s")


# ---------------------------------------------------------------------------
# analysis
# ---------------------------------------------------------------------------

class Report:
    def __init__(self, raw, out_dir):
        self.raw = raw
        self.out_dir = out_dir
        self.cfg = raw["config"]
        self.debug = self.cfg == "dbg"
        self.target = raw["target"]
        self.base = raw["baseline_data"]
        self.same_tree = os.path.abspath(self.target["src"]) == os.path.abspath(self.base["src"])
        self.runtimes = raw["runtimes"]
        self.corpus = C.Corpus(self.target["src"], raw["flavor"])
        self.profile = C.Profile(self.target["src"], self.corpus)
        self.base_profile = None if self.same_tree else C.Profile(self.base["src"], C.Corpus(self.base["src"], raw["baseline"]))
        self.findings, self.regressions, self.questions, self.documented = [], [], [], []
        self.obs = {}
        self.diff, self.sporadic, self.jit_dependent, self.invalid, self.notes = [], [], [], [], []
        self.mem_rows, self.mem_diff, self.invoke, self.watch = [], [], {}, []
        self.runtime_diff = []
        self.dense = {}
        self.jit_first = []

    # -- helpers ---------------------------------------------------------
    def sm(self, data, rt, sid):
        rd = data["runtimes"].get(rt)
        if not rd:
            return None
        return summarize(rd["results"].get(sid, []))

    def timing(self, data, rt, sid):
        rd = data["runtimes"].get(rt)
        return (rd or {}).get("timing", {}).get(sid)

    def command(self, sid):
        cmd = "Stand/alloc/run.sh --only %s --src %s --flavor %s --baseline %s" % (
            sid, S.rel(self.target["src"]), self.raw["flavor"], self.raw["baseline"])
        if not self.same_tree:
            cmd += " --baseline-src %s" % S.rel(self.base["src"])
        if self.debug:
            cmd += " --debug"
        return cmd

    def severity(self, cls, metric):
        recurring = metric in ("bytes/op", "long-run")
        if cls == "contract-violation":
            if recurring:
                return "medium" if self.debug else "high"
            return "low" if self.debug else "medium"
        if cls == "regression":
            if recurring:
                return "low" if self.debug else "medium"
            return "low"
        return "info"

    def params(self, sid):
        for rt in self.runtimes:
            s = self.sm(self.target, rt, sid)
            if s:
                return dict(s["extra"], mode=self.raw["mode"], kensei_debug=self.debug)
        return {"mode": self.raw["mode"], "kensei_debug": self.debug}

    def observe(self, sid, metric):
        return self.obs.setdefault((sid, metric), {"regression": {}, "contract": {}})

    # -- passes ----------------------------------------------------------
    def check_meter(self):
        for role, data in (("target", self.target), ("baseline", self.base)):
            for rt, rd in data["runtimes"].items():
                info = rd.get("info") or {}
                if not info.get("selftest_ok", False) or rd.get("selftest_failures"):
                    self.invalid.append("%s/%s: the meter's self-test failed: %s" % (role, rt, info))

    def compare(self):
        ids = list(dict.fromkeys(self.target.get("scenarios", []) + self.base.get("scenarios", [])))
        for sid in ids:
            for rt in self.runtimes:
                t = self.sm(self.target, rt, sid)
                b = None if self.same_tree else self.sm(self.base, rt, sid)
                row = {"scenario": sid, "title": TITLES.get(sid, sid), "runtime": rt, "marks": []}
                for key, data, s in (("target", self.target, t), ("baseline", self.base, b)):
                    if s is None:
                        row[key] = None
                        continue
                    row[key] = {"available": s["available"], "reason": s["reason"], "errors": s["errors"],
                                "first_min": s.get("first_min"), "first_max": s.get("first_max"), "runs": s["runs"],
                                "extra_min": s.get("extra_min"),
                                "per_op": (s.get("steady") or {}).get("per_op"),
                                "steady_kind": (s.get("steady") or {}).get("kind"),
                                "spans": (s.get("spans") or {}).get("min"),
                                "ns": ns_of(self.timing(data, rt, sid))}
                    if (s.get("steady") or {}).get("kind") == "sporadic":
                        self.sporadic.append({"scenario": sid, "runtime": rt, "role": key, "steady": s["steady"]})
                    tr = self.timing(data, rt, sid)
                    if s.get("steady") and tr and tr.get("steady_windows"):
                        fo = sum(tr["steady_windows"]) / (len(tr["steady_windows"]) * tr["per_window"])
                        if abs(fo - s["steady"]["per_op"]) >= 1:
                            self.jit_dependent.append({"scenario": sid, "runtime": rt, "role": key,
                                                       "tiered_per_op": s["steady"]["per_op"], "fullopts_per_op": fo})
                tv = t and t["available"] and not t["errors"]
                bv = b and b["available"] and not b["errors"]
                if t and t["errors"]:
                    row["marks"].append("error in target")
                if b and b["errors"]:
                    row["marks"].append("error in baseline")
                if tv and not bv and not self.same_tree:
                    row["marks"].append("not in baseline" if b and not b["available"] else "no baseline data")
                if bv and not tv and not (t and t["errors"]):
                    row["marks"].append("not in target")
                if tv and t.get("spans"):
                    sp = t["spans"]
                    if sp["never_settles"]:
                        row["marks"].append("never settles: %s B per span" % "/".join(fmt_b(x) for x in sp["min"]))
                if tv and bv:
                    self.compare_pair(sid, rt, t, b, row)
                self.diff.append(row)

    def compare_runtimes(self):
        """Same tree, same scenario, two runtimes: bytes per operation should agree. Fewer bytes on .NET 10
        usually means its JIT put an object on the stack (object stack allocation after inlining, also of
        methods with try/finally); IL2CPP and Mono allocate it, so .NET 8 is the reference then."""
        if "net10" not in self.runtimes or "net8" not in self.runtimes:
            return
        for sid in self.target.get("scenarios", []):
            a, b = self.sm(self.target, "net10", sid), self.sm(self.target, "net8", sid)
            if not a or not b or not a["available"] or not b["available"]:
                continue
            sa, sb = a.get("steady"), b.get("steady")
            if sa and sb and abs(sa["per_op"] - sb["per_op"]) >= 1:
                self.runtime_diff.append({"scenario": sid, "net10_per_op": sa["per_op"], "net8_per_op": sb["per_op"],
                                          "hint": "the .NET 10 JIT put an object on the stack; for IL2CPP read net8"
                                          if sa["per_op"] < sb["per_op"] else "more allocations on .NET 10"})

    def compare_pair(self, sid, rt, t, b, row):
        # One process for all scenarios: the first pass depends on what ran before it in that process,
        # which differs between versions (missing scenarios), so one-time bytes are not compared.
        if "first_min" in t and "first_min" in b and self.raw.get("isolate", True):
            if t["first_min"] > b["first_max"]:
                delta = t["first_min"] - b["first_max"]
                row["marks"].append("regression: one-time +%s B" % fmt_b(delta))
                self.observe(sid, "one-time bytes")["regression"][rt] = {
                    "target_first": t["first_min"], "baseline_first": b["first_max"], "delta": delta,
                    "target_runs": t["runs"], "baseline_runs": b["runs"],
                    "rule": "one-time bytes (first pass) not above the baseline: target minimum > baseline maximum over "
                            "all fresh-process runs"}
            elif t["first_max"] < b["first_min"]:
                row["marks"].append("fewer one-time bytes: -%s B" % fmt_b(b["first_min"] - t["first_max"]))
        ts, bs = t.get("steady"), b.get("steady")
        if ts and bs:
            delta = ts["per_op"] - bs["per_op"]
            if delta >= 1 and ts["min_window"] > 0:
                row["marks"].append("regression: +%.2f B/op" % delta)
                self.observe(sid, "bytes/op")["regression"][rt] = {
                    "target_per_op": ts["per_op"], "baseline_per_op": bs["per_op"], "delta": delta,
                    "windows": ts["windows"], "per_window": ts["per_window"],
                    "rule": "bytes per operation not above the baseline (in every window)"}
            elif delta <= -1 and bs["min_window"] > 0:
                row["marks"].append("fewer B/op: %.2f" % delta)
            elif delta != 0:
                row["marks"].append("sporadic: delta %.3f B/op" % delta)
        tsp, bsp = t.get("spans"), b.get("spans")
        if tsp and bsp and tsp["never_settles"] and tsp.get("control_settles"):
            if bsp["settles"]:
                row["marks"].append("regression: the long run no longer settles")
                self.observe(sid, "long-run")["regression"][rt] = {
                    "target_span_bytes": tsp["min"], "baseline_span_bytes": bsp["min"], "span_runs": tsp["runs"],
                    "rule": "a frame that settled in the baseline (0 B in every span) settles in the target too"}
            elif bsp["never_settles"] and all(a > 2 * c for a, c in zip(tsp["min"], bsp["max"])):
                row["marks"].append("regression: the long run allocates more than twice the baseline")
                self.observe(sid, "long-run")["regression"][rt] = {
                    "target_span_bytes": tsp["min"], "baseline_span_bytes": bsp["max"], "span_runs": tsp["runs"],
                    "rule": "a long run that never settles grows no faster than in the baseline (not more than twice its "
                            "bytes in every span)"}

    def check_contracts(self):
        for sid in self.target.get("scenarios", []):
            for rt in self.runtimes:
                s = self.sm(self.target, rt, sid)
                if not s or not s["available"] or s["errors"]:
                    continue
                st = s.get("steady")
                if st and st["kind"] == "per-op":
                    self.contract(sid, rt, "bytes/op", "steady", st["per_op"],
                                  {"per_op": st["per_op"], "windows": st["windows"], "per_window": st["per_window"],
                                   "counter": s["counter"]})
                if s.get("first_min") is not None and C.rules_for(sid, "first", self.cfg):
                    self.contract(sid, rt, "one-time bytes", "first", s["first_min"],
                                  {"first": s["first_min"], "first_max": s["first_max"], "runs": s["runs"]})
                if s.get("extra_min") is not None and C.rules_for(sid, "first_extra", self.cfg):
                    # Growth scenarios: the arrays are expected, the bytes beyond them are what a rule judges.
                    ex = s["extra"]
                    units = ex.get("pools_grown") or ex.get("filters") or 1
                    self.contract(sid, rt, "one-time bytes", "first_extra", s["extra_min"],
                                  {"first": s["first_min"], "extra": s["extra_min"], "extra_max": s.get("extra_max"),
                                   "expected_array_bytes": ex.get("expected_array_bytes"), "units": units,
                                   "extra_per_unit": s["extra_min"] / units if units else None, "runs": s["runs"]})
                sp = s.get("spans")
                if sp and C.rules_for(sid, "spans", self.cfg):
                    excess = None
                    if sp["never_settles"] and sp.get("control_settles") is False:
                        # The control (the same frames and the same Get calls without the write-back) allocates too: only
                        # what the long run allocates beyond it, in every span, is the write-back's own.
                        controls = [c for c in (sp.get("control") or []) if c]
                        cmax = [max(c[i] for c in controls) for i in range(len(sp["min"]))] if controls else None
                        if not cmax or not all(t > c for t, c in zip(sp["min"], cmax)):
                            self.notes.append("%s (%s): the long run does not settle in the control either, and allocates no more "
                                              "than the control in every span: no item from it" % (sid, rt))
                            continue
                        excess = [t - c for t, c in zip(sp["min"], cmax)]
                    if sp["never_settles"]:
                        numbers = {"span_bytes": excess or sp["min"], "span_runs": sp["runs"], "warmup_runs": sp["warmup"],
                                   "control_span_bytes": (sp["control"] or [None])[0],
                                   "shared_pairs": s["extra"].get("shared_pairs"),
                                   "last_span_bytes_per_frame": s["extra"].get("last_span_bytes_per_frame")}
                        if excess:
                            numbers["total_span_bytes"] = sp["min"]
                            numbers["beyond_control"] = True
                        o = self.contract(sid, rt, "long-run", "spans", 1, numbers)
                        if o is not None:
                            o["explanation"] = self.explain_long_run(sid, sp, s["extra"])

    def contract(self, sid, rt, metric, key, measured, numbers):
        verdict, basis = C.evaluate(C.rules_for(sid, key, self.cfg), measured, self.profile)
        if verdict in ("ok", "no-rule"):
            return None
        o = {"verdict": verdict, "basis": basis, "numbers": numbers}
        slot = self.observe(sid, metric)["contract"]
        rank = {"violation": 0, "question": 1, "documented": 2}
        if rt not in slot or rank[verdict] < rank[slot[rt]["verdict"]]:
            slot[rt] = o
        return slot[rt]

    def explain_long_run(self, sid, sp, extra):
        parts = []
        sig = doubling_signature(sp["min"])
        if sig:
            parts.append("every span holds exactly one doubling of a reference array (%s slots, 24 + 8 x n B): a collection "
                         "of references grows every frame without bound, and the allocations are its doublings"
                         % " -> ".join(fmt_b(x) for x in sig))
        if sid == "events.writeback_frames":
            parts.append("mechanism: the group's OneFrame cleanup returns the buffer's list to the static pool, then Playback "
                         "applies the recorded Set of the copy, which holds the same List: the component is added again (it "
                         "is gone, so this is an Add, not an overwrite) with a list that already sits in the pool; the next "
                         "cleanup returns it once more")
            if extra.get("shared_pairs"):
                parts.append("%s pair(s) of entities hold one and the same List (EventBuffer.Values): events of one are seen "
                             "in the other" % extra["shared_pairs"])
            parts.append("control (the same frame without the write-back): 0 B in every span")
        return "; ".join(parts)

    # -- memory (A3) ----------------------------------------------------
    def memory(self):
        rows = []
        for role, data in (("target", self.target), ("baseline", self.base)):
            if role == "baseline" and self.same_tree:
                continue
            for rt, rd in data["runtimes"].items():
                for m in rd.get("mem", []):
                    rows.append(dict(m, role=role, runtime=rt, flavor=data["flavor"]))
        self.mem_rows = rows
        # Version comparison (thread bytes are exact; the live heap varies with fragmentation).
        base = {(r["runtime"], r["types"], r["filters"], r["layout"]): r for r in rows if r["role"] == "baseline" and r.get("available")}
        for r in rows:
            if r["role"] != "target" or not r.get("available"):
                continue
            b = base.get((r["runtime"], r["types"], r["filters"], r["layout"]))
            if not b:
                continue
            delta = r["alloc_thread"] - b["alloc_thread"]
            limit = max(MEM_REGRESSION_MIN, MEM_REGRESSION_SHARE * b["alloc_thread"])
            d = {"runtime": r["runtime"], "types": r["types"], "filters": r["filters"], "layout": r["layout"],
                 "target": r["alloc_thread"], "baseline": b["alloc_thread"], "delta": delta, "limit": limit}
            self.mem_diff.append(d)
            if delta > limit:
                sid = "mem.%s.%d.%d" % (r["layout"], r["types"], r["filters"])
                self.observe(sid, "memory")["regression"][r["runtime"]] = {
                    "target_bytes": r["alloc_thread"], "baseline_bytes": b["alloc_thread"], "delta": delta,
                    "rule": "world memory with the same N, layout and runtime not above the baseline (threshold %s B or 1%%)"
                            % fmt_b(MEM_REGRESSION_MIN)}
        target = [r for r in rows if r["role"] == "target" and r.get("available")]
        if not target:
            return
        cmp = {}
        for r in target:
            cmp.setdefault((r["runtime"], r["types"], r["filters"]), {})[r["layout"]] = r
        details = {}
        for (rt, n, f), lay in sorted(cmp.items()):
            if "reserve_rare" in lay and "organic_rare" in lay:
                res, org = lay["reserve_rare"], lay["organic_rare"]
                case = {"reserve_alloc": res["alloc_total"], "reserve_live": res["live"],
                        "organic_alloc": org["alloc_total"], "organic_live": org["live"],
                        "ratio_alloc": res["alloc_total"] / org["alloc_total"] if org["alloc_total"] else None,
                        "sparse_bytes": res["pool_sparse_ints"] * 4, "pools": res["pools"],
                        "filter_bytes": res["filter_allocated_bytes"], "world_reserve_bytes": res["reserve_call"]}
                late = lay.get("reserve_late_rare")
                if late:
                    case["late_alloc"] = late["alloc_total"]
                    case["late_live"] = late["live"]
                d = details.setdefault(rt, {"sparse_bytes_per_pool": res["pool_sparse_ints"] * 4 // max(1, res["pools"]), "cases": {}})
                d["cases"]["%d types, %d filters" % (n, f)] = case
        if details:
            st = self.profile.state(C.RESERVE_SPARSE_SIZE)
            o = self.observe("mem.reserve_entities", "memory")
            for rt, d in details.items():
                o["contract"][rt] = {"verdict": "question", "numbers": d,
                                     "basis": {"rule": "question", "promise": C.RESERVE_SPARSE_SIZE, "quote": st["quote"],
                                               "since": st["since"], "reason": "the docs name what grows, not what it costs"}}
            o["question"] = ("Reserve(entities) gives every pool (and every page of a filter) a sparse array for all `entities` "
                             "slots: 4 B x entities x the number of pools, singletons and rare types included. The docs say WHAT "
                             "grows («%s») and price only `components` («costs components slots of every component type»); "
                             "the price entities x pools is not named." % (st["quote"] or "sparse arrays of every pool"))

    # -- filter dense capacity --------------------------------------------
    def filter_dense(self):
        sid = "filter.dense_capacity"
        per_rt = {}
        for rt in self.runtimes:
            s = self.sm(self.target, rt, sid)
            if s and s["available"] and not s["errors"]:
                per_rt[rt] = parse_dense_cases(s["extra"].get("cases"))
        if not per_rt:
            return
        base = {}
        if not self.same_tree:
            for rt in self.runtimes:
                s = self.sm(self.base, rt, sid)
                if s and s["available"] and not s["errors"]:
                    base[rt] = {(c["kind"], c["requested"]): c for c in parse_dense_cases(s["extra"].get("cases"))}
        st = self.profile.state(C.FILTER_RESERVE_SIZE)
        o = self.observe(sid, "capacity")
        for rt, cases in per_rt.items():
            over = [c for c in cases if c["kind"] != "organic" and c["filter"] >= DENSE_OVERSHOOT * c["requested"]]
            organic_over = [c for c in cases if c["kind"] == "organic" and c["filter"] > 2 * c["requested"] + 2]
            worst = max(over, key=lambda c: c["filter"] / c["requested"]) if over else None
            self.dense[rt] = {"cases": cases, "oversized": over, "organic_over_doubling": organic_over, "worst": worst}
            if over or organic_over:
                o["contract"][rt] = {"verdict": "question", "numbers": {
                    "oversized": ["%s(%d): filter %d, pool %d" % (c["kind"], c["requested"], c["filter"], c["pool"]) for c in over],
                    "organic_over_doubling": ["%d matching: filter %d" % (c["requested"], c["filter"]) for c in organic_over],
                    "worst_ratio": round(worst["filter"] / worst["requested"], 2) if worst else None,
                    "worst_extra_bytes": 4 * (worst["filter"] - worst["requested"]) if worst else None},
                    "basis": {"rule": "question", "promise": C.FILTER_RESERVE_SIZE, "quote": st["quote"], "since": st["since"],
                              "reason": "the docs promise room for at least the reserved count, not an exact size"}}
            grown = []
            for c in cases:
                b = (base.get(rt) or {}).get((c["kind"], c["requested"]))
                if b and c["filter"] > b["filter"]:
                    grown.append({"case": "%s(%d)" % (c["kind"], c["requested"]), "target": c["filter"], "baseline": b["filter"],
                                  "delta_bytes": 4 * (c["filter"] - b["filter"])})
            if grown:
                o["regression"][rt] = {"cases": grown, "delta": max(g["delta_bytes"] for g in grown),
                                       "rule": "a filter's dense capacity for the same request not above the baseline"}
        if o["contract"]:
            rt0 = next(iter(self.dense))
            d = self.dense[rt0]
            w = d["worst"]
            org = {c["requested"]: c for c in d["cases"] if c["kind"] == "organic"}
            org_text = "; ".join("%d matching: filter %d, pool %d" % (n, c["filter"], c["pool"]) for n, c in sorted(org.items()))
            o["question"] = (
                "Filter.Reserve(count) and World.Reserve(n, n) on an existing filter size its dense array to max(2 x its length, "
                "count + 1): up to twice the stated need, while ComponentPool.Reserve, asked the same, sizes exactly. %s. The "
                "docs promise room for at least the reserved count («%s»), so it is not a violation; whether a reserve should "
                "size a filter exactly, like a pool, is for the contract to say. Filters created after World.Reserve start "
                "exactly at the reserved size, and organic growth doubles like pools (%s)." % (
                    ("Worst case %s(%d): %d slots for %d requested (+%s B per filter)" % (
                        w["kind"], w["requested"], w["filter"], w["requested"], fmt_b(4 * (w["filter"] - w["requested"]))))
                    if w else "No reserve case oversized", st["quote"] or "-", org_text))

    # -- invoke (A4) ----------------------------------------------------
    def invoke_table(self):
        ids = ("invoke.static_lambda", "invoke.static_lambda_onerror", "invoke.method_group",
               "invoke.capturing_lambda", "invoke.manual_loop", "invoke.delegate_overhead")
        for rt in self.runtimes:
            row = {}
            for sid in ids:
                s = self.sm(self.target, rt, sid)
                if not s:
                    continue
                row[sid] = {"available": s["available"], "reason": s["reason"],
                            "per_op": (s.get("steady") or {}).get("per_op"), "ns": ns_of(self.timing(self.target, rt, sid))}
            paired = (self.timing(self.target, rt, "invoke.ratio_paired") or {}).get("extra") or {}
            if paired.get("ratio_static_median"):
                row["paired"] = paired
            ml, ov = row.get("invoke.manual_loop"), row.get("invoke.delegate_overhead")
            if ml and ml.get("ns"):
                for sid in ids[:4]:
                    x = row.get(sid)
                    if x and x.get("ns"):
                        x["ratio_to_loop"] = x["ns"] / ml["ns"]
                        if ov and ov.get("ns"):
                            x["ratio_to_loop_without_stand_delegate"] = (x["ns"] - ov["ns"]) / ml["ns"]
            self.invoke[rt] = row

    # -- attribution of one-time numbers ------------------------------------
    def attribute(self, item, sid, rt, delta, regression, offset=0):
        """Explains a one-time number with the attribution runs (knobs) of raw["attribution"]."""
        att = (self.raw.get("attribution") or {}).get(sid, {}).get(rt)
        lines, causes = [], []
        if att:
            for knob, (flag, done, cause) in KNOBS.items():
                tk = (att.get("target") or {}).get(knob)
                if not tk:
                    continue
                if regression:
                    bk = (att.get("baseline") or {}).get(knob)
                    if not bk:
                        continue
                    dk = min(tk) - max(bk)
                    explained = delta - max(dk, 0)
                    lines.append("with %s (%s) the difference is %s B instead of %s B" % (flag, done, fmt_b(max(dk, 0)), fmt_b(delta)))
                    if dk <= 0:
                        causes.append({"knob": knob, "cause": cause, "explained_bytes": delta, "full": True})
                    elif explained > 0:
                        causes.append({"knob": knob, "cause": cause, "explained_bytes": explained, "full": False})
                else:
                    # offset: the arrays a growth scenario expects, so knob runs compare bytes beyond them.
                    rest = min(tk) - offset
                    lines.append("with %s (%s) the one-time bytes%s are %s B instead of %s B" % (
                        flag, done, " beyond the arrays" if offset else "", fmt_b(rest), fmt_b(delta)))
                    if rest < delta:
                        causes.append({"knob": knob, "cause": cause, "explained_bytes": delta - rest, "full": rest == 0})
            # The run_cctors knob narrowed to one type: the type whose static constructor alone gives the same drop.
            per_type = (att.get("target") or {}).get("cctor_of") or {}
            all_knob = (att.get("target") or {}).get("run_cctors")
            if per_type and all_knob:
                best = min(per_type, key=lambda k: per_type[k])
                if per_type[best] <= min(all_knob):
                    alone = sum(1 for v in per_type.values() if v <= min(all_knob)) == 1
                    lines.append("with --run-cctors-of %s the one-time bytes are %s B%s" % (
                        best, fmt_b(per_type[best] - (0 if regression else offset)), " (no other type alone changes them)" if alone else ""))
                    for c in causes:
                        if c["knob"] == "run_cctors":
                            c["cause"] = "the static constructor of %s (static state the first call creates)" % best
                            c["type"] = best
        # Calibration of the filter scenario: a description string (Filter.ToString) per filter.
        if sid == "growth.first_n_filters":
            ex = self.params(sid)
            n = ex.get("filters") or 0
            lo, hi = ex.get("filter_description_second_bytes"), ex.get("filter_description_first_bytes")
            if n and lo and hi:
                per = delta / n
                lines.append("calibration: Filter.ToString() allocates %s B (type name already cached) to %s B (first call for "
                             "the type) in this process; the extra is %s B per filter" % (fmt_b(lo), fmt_b(hi), fmt_b(per)))
                if lo - 8 <= per <= hi + 16:
                    causes.append({"knob": "filter_description", "full": True, "explained_bytes": delta,
                                   "cause": "one filter description string per filter (Filter.ToString: a StringBuilder, the "
                                            "string and, the first time, the type names)"})
        if not lines:
            return
        causes.sort(key=lambda c: (not c["full"], -c["explained_bytes"]))
        item.setdefault("attribution", {})[rt] = {"experiments": lines, "causes": causes}
        full = [c for c in causes if c["full"]]
        if full:
            keys = item.setdefault("cause_keys", [])
            for c in full:
                k = "cctor:" + c["type"] if c.get("type") else c["knob"]
                for kk in (k, c["knob"]):
                    if kk not in keys:
                        keys.append(kk)

    # -- documented costs ---------------------------------------------------
    def documented_cost(self, sid, reg):
        """A one-time regression that is exactly a set of documented per-object costs of the target (contracts.COSTS):
        the census of the first pass (Probe/Census.cs) on both trees, object by object, the differences summed. Every
        difference that adds bytes needs its promise in the target's docs; fewer bytes need none. {rt: accounting} when
        every runtime is accounted for to the byte, otherwise None (the regression stays)."""
        out = {}
        base_profile = self.base_profile or self.profile
        for rt, d in reg.items():
            t, b = self.sm(self.target, rt, sid), self.sm(self.base, rt, sid)
            te, be = (t or {}).get("extra") or {}, (b or {}).get("extra") or {}
            if "cost_pools" not in te or "cost_pools" not in be:
                return None   # a probe without the census on one side
            parts, total = [], 0
            for c in C.COSTS:
                if c.get("data"):
                    data = self.data_cost(te, be, base_profile)
                    if data is None:
                        return None
                    for p in data:
                        parts.append(p)
                        total += p["bytes"]
                    continue
                st = self.profile.state(c["promise"])
                if c.get("staging"):
                    # The temporary array of a staged growth is no object the census can see afterwards: the tree's docs
                    # say whether its growths stage (2.2.2: every array is allocated before it replaces the old one).
                    tv = te.get(c["count"], 0) * te.get(c["per"], 0) if st["applies"] else 0
                    bv = be.get(c["count"], 0) * be.get(c["per"], 0) if base_profile.state(c["promise"])["applies"] else 0
                else:
                    tv, bv = te.get(c["bytes"], 0), be.get(c["bytes"], 0)
                diff = tv - bv
                if diff == 0:
                    continue
                if diff > 0 and not st["applies"]:
                    return None
                parts.append({"kind": c["kind"], "bytes": diff, "target": tv, "baseline": bv, "count": te.get(c["count"], 0),
                              "promise": c["promise"], "quote": st["quote"] if diff > 0 else None, "since": st["since"]})
                total += diff
            if not parts or total != d["delta"]:
                return None
            out[rt] = {"delta": d["delta"], "target_first": d.get("target_first"), "baseline_first": d.get("baseline_first"),
                       "explained": parts}
        return out

    def data_cost(self, te, be, base_profile):
        """The component data arrays of a first pass against the baseline's (Census.cs, "Type:elements:bytes" in cost_data):
        per component type, elements x (element bytes on the target - on the baseline) when both allocated the same
        elements, the element growth documented for that type (C.ELEMENT_PROMISES) when it adds bytes. Parts with a
        difference, [] when nothing differs or a side has no census of its data arrays, None when a difference is not
        accounted for (elements the doublings do not explain, a type allocated on one side only, an undocumented growth)."""
        if "cost_data" not in te or "cost_data" not in be:
            return []

        def parse(text):
            out = {}
            for part in (text or "").split(";"):
                bits = part.rsplit(":", 2)
                if len(bits) == 3:
                    out[bits[0]] = (None if bits[1] == "?" else int(bits[1]), int(bits[2]))
            return out
        t, b = parse(te.get("cost_data")), parse(be.get("cost_data"))
        parts = []
        for key in sorted(set(t) | set(b)):
            (tn, tb), (bn, bb) = t.get(key, (0, 0)), b.get(key, (0, 0))
            if tn == bn and tb == bb:
                continue
            if tn is None or bn is None or tn != bn:
                return None
            diff = tn * (tb - bb)
            if diff == 0:
                continue
            pid = next((p for pre, p in C.ELEMENT_PROMISES.items() if key.startswith(pre)), None)
            st = self.profile.state(pid) if pid else None
            if diff > 0 and not (st and st["applies"]):
                return None
            parts.append({"kind": "component data (%s, %d -> %d B per element)" % (key, bb, tb), "bytes": diff, "target": tn * tb,
                          "baseline": bn * bb, "count": tn, "promise": pid, "quote": st["quote"] if st and diff > 0 else None,
                          "since": st["since"] if st else None})
        return parts

    @staticmethod
    def cost_text(acc):
        return "; ".join("%s %s%s B (%s on the target, %s on the baseline, %d created)" % (
            p["kind"], "+" if p["bytes"] > 0 else "", fmt_b(p["bytes"]), fmt_b(p["target"]), fmt_b(p["baseline"]), p["count"])
            for p in acc["explained"])

    # -- items ------------------------------------------------------------
    def merge(self):
        """Observations -> items. Violation > regression > question > documented for one scenario metric. A one-time
        regression that documented costs of the target account for to the byte is documented (signature
        first:<scenario>:documented-cost), unless the scenario's own rules make it a violation or a question."""
        for (sid, metric), o in sorted(self.obs.items()):
            reg, con = o["regression"], o["contract"]
            viol = {rt: c for rt, c in con.items() if c["verdict"] == "violation"}
            ques = {rt: c for rt, c in con.items() if c["verdict"] == "question"}
            docd = {rt: c for rt, c in con.items() if c["verdict"] == "documented"}
            cost = self.documented_cost(sid, reg) if reg and metric == "one-time bytes" and not viol else None
            if cost:
                reg = {}
            if viol:
                cls, numbers = "contract-violation", {rt: c["numbers"] for rt, c in viol.items()}
                rule = "the documentation of this tree promises no allocation here"
                basis = next(iter(viol.values()))["basis"]
            elif reg:
                cls, numbers = "regression", {rt: {k: v for k, v in d.items() if k != "rule"} for rt, d in reg.items()}
                rule = next(iter(reg.values()))["rule"]
                basis = next(iter(con.values()))["basis"] if con else None
            elif ques:
                cls, numbers = "contract-question", {rt: c["numbers"] for rt, c in ques.items()}
                rule = "the documentation of this tree does not cover it"
                basis = next(iter(ques.values()))["basis"]
            elif docd:
                cls, numbers = "documented", {rt: c["numbers"] for rt, c in docd.items()}
                rule = "the documentation of this tree describes this allocation"
                basis = next(iter(docd.values()))["basis"]
                if cost:
                    rule += "; the bytes it has more than the baseline are documented costs of this version"
            elif cost:
                cls, numbers = "documented", cost
                rule = "more one-time bytes than the baseline, all of them documented costs of this version (per object, from the census of the first pass)"
                quotes = [p["quote"] for acc in cost.values() for p in acc["explained"] if p.get("quote")]
                basis = {"rule": "documented-cost", "promise": ",".join(sorted({p["promise"] for acc in cost.values() for p in acc["explained"] if p.get("quote")})),
                         "quote": " | ".join(dict.fromkeys(quotes)), "since": "2.2.2",
                         "reason": "documented costs of the target: " + "; ".join("%s: %s" % (rt, self.cost_text(acc)) for rt, acc in cost.items())}
            else:
                continue
            item = self.make_item(cls, rule, sid, metric, numbers, basis)
            if cost:
                item["documented_cost"] = cost
                item["cause_keys"] = ["documented-cost"]
                item["explanations"] = {rt: "the difference against the baseline (%s B) is exactly: %s" % (fmt_b(acc["delta"]), self.cost_text(acc))
                                        for rt, acc in cost.items()}
            if cls == "contract-violation" and reg:
                item["regression"] = reg
            if cls == "regression" and con:
                item["contract"] = {rt: {"verdict": c["verdict"], "reason": (c["basis"] or {}).get("reason"),
                                         "quote": (c["basis"] or {}).get("quote")} for rt, c in con.items()}
            if cls == "contract-question" or (cls == "regression" and ques):
                item["question"] = o.get("question") or self.question_text(sid, metric, next(iter(ques.values())))
            elif o.get("question") and cls != "documented":
                item["question"] = o["question"]
            expl = {rt: c.get("explanation") for rt, c in con.items() if c.get("explanation")}
            if expl:
                item["explanations"] = expl
            if metric == "one-time bytes" and not cost:
                for rt in item["runtimes"]:
                    if cls == "regression":
                        self.attribute(item, sid, rt, reg[rt]["delta"], regression=True)
                    else:
                        num = item["runtimes"][rt]
                        if "extra" in num:
                            self.attribute(item, sid, rt, num["extra"], regression=False, offset=num.get("expected_array_bytes") or 0)
                        else:
                            self.attribute(item, sid, rt, num.get("first"), regression=False)
                if cls == "regression" and self.jit_only(item):
                    # The first passes are equal once the runtime compiles everything fully optimized at once, and no
                    # experiment that does the library's own one-time work explains it: code generation of the
                    # runtime (which methods ReadyToRun covers, tier-0 paths), not a regression of the library.
                    self.jit_first.append({"scenario": sid, "runtimes": item["runtimes"], "attribution": item.get("attribution")})
                    continue
            item["signature"] = self.signature(item)
            item["magnitude"] = self.magnitude(item)
            {"contract-violation": self.findings, "regression": self.regressions,
             "contract-question": self.questions, "documented": self.documented}[cls].append(item)

    def jit_only(self, item):
        att = item.get("attribution") or {}
        if not att or set(att) != set(item["runtimes"]):
            return False
        for a in att.values():
            full = {c["knob"] for c in a["causes"] if c["full"]}
            if "fullopts" not in full or full & set(LIBRARY_KNOBS):
                return False
        return True

    def make_item(self, cls, rule, sid, metric, numbers, basis):
        mshort = METRICS[metric][0]
        fid = "alloc.%s.%s" % (SHORT[cls], sid) if sid.startswith("mem.") else "alloc.%s.%s.%s" % (SHORT[cls], mshort, sid)
        item = {"id": fid, "class": cls, "status": CLASSES[cls][0], "class_text": CLASSES[cls][1], "rule": rule,
                "scenario": sid, "title": TITLES.get(sid, sid), "metric": metric, "metric_text": METRICS[metric][1],
                "severity": self.severity(cls, metric), "runtimes": numbers,
                "seed": 0, "params": self.params(sid), "flavor": self.raw["flavor"], "config": self.cfg,
                "baseline": self.raw["baseline"], "command": self.command(sid)}
        if basis:
            item["contract_basis"] = basis
            if basis.get("quote"):
                item["contract_quote"] = basis["quote"]
        return item

    def signature(self, item):
        """metric : scenario : mechanism. The mechanism of a one-time number is the cause an experiment
        established (or "unexplained"); of the other metrics, the kind of measurement."""
        mshort = METRICS[item["metric"]][0]
        if item["metric"] == "one-time bytes":
            keys = item.get("cause_keys") or []
            mech = next((k for k in keys if k.startswith("cctor:")), None) or (keys[0] if keys else
                                                                                ("unexplained" if item.get("attribution") else "unattributed"))
        else:
            mech = {"bytes/op": "per-op", "long-run": "unbounded", "memory": "memory", "capacity": "oversized"}[item["metric"]]
        return "%s:%s:%s" % (mshort, item["scenario"], mech)

    def magnitude(self, item):
        """One number per item for the summary's growth check: the largest over runtimes."""
        vals = []
        for d in item["runtimes"].values():
            if not isinstance(d, dict):
                continue
            for k in ("delta", "extra", "first", "per_op", "worst_extra_bytes", "target_bytes"):
                if isinstance(d.get(k), (int, float)):
                    vals.append(d[k])
                    break
            else:
                sb = d.get("span_bytes") or d.get("target_span_bytes")
                if sb:
                    vals.append(sb[-1])
        return max(vals) if vals else None

    def question_text(self, sid, metric, c):
        numbers = c.get("numbers") or {}
        basis = c.get("basis") or {}
        reason = basis.get("reason") or ""
        ex = self.params(sid)
        if sid == "reserve.pool_first_add_in_frame":
            obj = self.corpus.promise(C.POOL_OBJECT)
            return ("The docs of this tree say «%s» and, in the Reserve table, that pools created after Reserve «start with sparse "
                    "arrays of at least that size». Measured: the first Add of a new type in a frame after Reserve(%s, %s) "
                    "allocated %s B: %s B are reserve-sized arrays (sparse %s, dense %s x (4 + %s B)), %s B the pool object. "
                    "«Allocates nothing» is about entities with the world's components; that a filter does not create the pool "
                    "of its type, and that a pool first created in a frame allocates reserve-sized arrays there, the docs do "
                    "not say." % (obj or "creating a pool ... allocates the object itself", ex.get("n"), ex.get("n"),
                                  fmt_b(numbers.get("first")), fmt_b(ex.get("reserve_sized_array_bytes")),
                                  ex.get("pool_sparse_capacity"), ex.get("pool_dense_capacity"), ex.get("component_size"),
                                  fmt_b(ex.get("other_bytes"))))
        if sid in ("reserve.first_entities_of_process", "create.first_entity_of_process"):
            return ("The first CreateEntity of the process allocated %s B%s. The package tests leave first-call work out by "
                    "warming up, and the docs of Reserve and of Debug Mode do not say whether a process's first call may "
                    "allocate. The cause is in the experiments below." % (
                        fmt_b(numbers.get("first")), " after Reserve(n, n)" if sid.startswith("reserve") else ""))
        if sid == "invoke.method_group":
            return ("A method group instead of a static lambda: %s B per call. This is the caller's delegate: C# 9 (Unity's "
                    "language version and the stand's) creates a new delegate at every conversion of a method group; C# 11+ "
                    "caches only static ones. The docs promise 0 B for a static lambda and describe the closure of a capturing "
                    "lambda; a method group is not mentioned." % fmt_b(numbers.get("per_op")))
        if "extra" in numbers:
            return ("The first growth without Reserve allocated %s B beyond the arrays it grew (%s B per %s); %s." % (
                fmt_b(numbers.get("extra")), fmt_b(numbers.get("extra_per_unit")),
                "pool" if sid == "growth.first_n_pools" else "filter", reason or "no promise covers it"))
        if "per_op" in numbers:
            return "%s B per operation; %s." % (fmt_b(numbers["per_op"]), reason or "no promise in the docs of this tree")
        if "span_bytes" in numbers:
            return ("The long run never settles (%s B per span); %s (%s)." % (
                "/".join(fmt_b(x) for x in numbers["span_bytes"]), reason, basis.get("quote") or "-"))
        return "%s B once; %s." % (fmt_b(numbers.get("first")), reason or "no promise in the docs of this tree")

    # -- canaries ---------------------------------------------------------
    def ran(self, sid):
        return sid in self.target.get("scenarios", [])

    def watchlist(self):
        def per_rt(sid, fn):
            out = {}
            for rt in self.runtimes:
                s = self.sm(self.target, rt, sid)
                if s and s["available"] and not s["errors"]:
                    out[rt] = fn(s)
            return out

        def item_of(sid):
            return next((x for x in self.findings + self.regressions + self.questions + self.documented if x["scenario"] == sid), None)

        def classified(sid):
            x = item_of(sid)
            return ("%s (%s)" % (x["class_text"], x["signature"])) if x else "no item"

        a1 = per_rt("growth.first_n_pools", lambda s: (s.get("extra_min", 0), s["extra"].get("pools_grown"),
                                                      s["extra"].get("type_name_first_call_bytes")))
        if not self.debug:
            st, ev = "n/a", "appears only under KENSEI_DEBUG; release: beyond the arrays %s B" % (
                ", ".join("%s %s" % (rt, fmt_b(v[0])) for rt, v in a1.items()) or "-")
        elif not a1:
            st, ev = "no data", "scenario growth.first_n_pools did not run"
        else:
            st = "present" if any(v[0] > 0 for v in a1.values()) else "absent"
            ev = "; ".join("%s: %s B beyond the arrays for %s pools (%s B per pool; a first typeof(T).Name costs %s B here)" % (
                rt, fmt_b(v[0]), v[1], fmt_b(v[0] / v[1]) if v[1] else "?", fmt_b(v[2])) for rt, v in a1.items())
            if st == "present":
                ev += "; classified: " + classified("growth.first_n_pools")
        self.watch.append({"id": "2.2.0-A1", "title": "KENSEI_DEBUG: a type-name string per pool at its first growth without Reserve",
                           "status": st, "evidence": ev})

        f1 = per_rt("growth.first_n_filters", lambda s: (s.get("extra_min", 0), s["extra"].get("filters")))
        if not self.debug:
            st, ev = "n/a", "names are built only under KENSEI_DEBUG; release: beyond the arrays %s B" % (
                ", ".join("%s %s" % (rt, fmt_b(v[0])) for rt, v in f1.items()) or "-")
        elif not f1:
            st, ev = "no data", "scenario growth.first_n_filters did not run"
        else:
            st = "present" if any(v[0] > 0 for v in f1.values()) else "absent"
            ev = "; ".join("%s: %s B beyond the arrays for %s filters" % (rt, fmt_b(v[0]), v[1]) for rt, v in f1.items())
            if st == "present":
                ev += "; classified: " + classified("growth.first_n_filters")
        self.watch.append({"id": "A1-FILTERS", "title": "KENSEI_DEBUG: a description string per filter at its first growth without Reserve",
                           "status": st, "evidence": ev})

        a2 = per_rt("reserve.pool_first_add_in_frame", lambda s: s.get("first_min"))
        if not self.ran("reserve.pool_first_add_in_frame"):
            st, ev = "no data", "scenario reserve.pool_first_add_in_frame did not run"
        elif not a2:
            st, ev = "not in this version", "World.Reserve is missing"
        else:
            st = "present" if any(v for v in a2.values()) else "absent"
            ev = "first Add of a new type in a frame after Reserve(5000, 5000): %s; classified: %s" % (
                ", ".join("%s %s B" % (rt, fmt_b(v)) for rt, v in a2.items()), classified("reserve.pool_first_add_in_frame"))
        self.watch.append({"id": "2.2.0-A2", "title": "a pool first created after Reserve(n, n) allocates in the frame",
                           "status": st, "evidence": ev})

        m = [r for r in self.mem_rows if r["role"] == "target" and r.get("available") and r["types"] == 1000 and r["filters"] == 0]
        res = {r["runtime"]: r for r in m if r["layout"] == "reserve_rare"}
        org = {r["runtime"]: r for r in m if r["layout"] == "organic_rare"}
        if res and org:
            ev = "; ".join("%s: Reserve %s against %s without Reserve (x%.1f)" % (rt, fmt_mb(res[rt]["alloc_total"]), fmt_mb(org[rt]["alloc_total"]),
                                                                                  res[rt]["alloc_total"] / org[rt]["alloc_total"])
                           for rt in res if rt in org)
            st = "present"
        elif any(r["role"] == "target" and not r.get("available") for r in self.mem_rows):
            st, ev = "not in this version", "World.Reserve is missing"
        else:
            st, ev = "no data", "needs the memory pass with 1000 types (without --no-mem)"
        self.watch.append({"id": "2.2.0-A3", "title": "memory of Reserve(entities) grows with the number of types (1000 types x 50 000 entities)",
                           "status": st, "evidence": ev})

        a4 = {rt: row.get("invoke.method_group") for rt, row in self.invoke.items()}
        if not self.ran("invoke.method_group"):
            st, ev = "no data", "scenario invoke.method_group did not run"
        elif not any(a4.values()) or not any(x and x["available"] for x in a4.values()):
            st, ev = "not in this version", "Listeners.Invoke is missing"
        else:
            st = "present" if any(x and x["available"] and (x["per_op"] or 0) > 0 for x in a4.values()) else "absent"

            def ratio(rt):
                p = self.invoke[rt].get("paired")
                if p:
                    return "%.1fx (method group %.1fx; median of %d paired rounds, %.1f-%.1fx)" % (
                        p["ratio_static_median"], p["ratio_method_group_median"], p["rounds"], p["ratio_static_min"], p["ratio_static_max"])
                x = self.invoke[rt].get("invoke.static_lambda", {})
                return ("%.1fx" % x["ratio_to_loop"]) if x.get("ratio_to_loop") else "-"
            ev = "; ".join("%s: %s B per call, ns Invoke(static) / hand-written loop = %s" % (rt, fmt_b(x["per_op"]), ratio(rt))
                           for rt, x in a4.items() if x and x["available"])
        self.watch.append({"id": "2.2.0-A4", "title": "Listeners.Invoke with a method group allocates per call (C# 9)",
                           "status": st, "evidence": ev})
        self.watch.append({"id": "2.2.0-A5", "title": "CI on .NET 8 in Release: «But was: 24» in allocation tests",
                           "status": "see --ci-parity", "evidence": "checked by Stand/alloc/run.sh --ci-parity"})

        wb = per_rt("events.writeback_frames", lambda s: (s.get("spans") or {}))
        if wb:
            present = any(v.get("never_settles") for v in wb.values())
            ev = "; ".join("%s: %s B per span, control %s" % (rt, "/".join(fmt_b(x) for x in v["min"]),
                                                              "/".join(fmt_b(x) for x in (v["control"] or [[]])[0]))
                           for rt, v in wb.items() if v)
            self.watch.append({"id": "2.2.0-W1", "title": "write-back of an EventBuffer copy through cb.Set (documented): the list pool grows without end",
                               "status": "present" if present else "absent", "evidence": ev})

        prof = per_rt("create.first_entity_of_process", lambda s: s.get("first_min"))
        if not prof:
            st, ev = "no data", "scenario create.first_entity_of_process did not run"
        elif not self.debug:
            st, ev = "n/a", "appears only under KENSEI_DEBUG; release: %s" % ", ".join("%s %s B" % (rt, fmt_b(v)) for rt, v in prof.items())
        else:
            st = "present" if any(v for v in prof.values()) else "absent"
            x = item_of("create.first_entity_of_process")
            cause = None
            for a in ((x or {}).get("attribution") or {}).values():
                for c in a["causes"]:
                    if c.get("type"):
                        cause = c["type"]
            ev = "first CreateEntity of the process: %s; cause: %s; classified: %s" % (
                ", ".join("%s %s B" % (rt, fmt_b(v)) for rt, v in prof.items()),
                ("static constructor of " + cause) if cause else "not established in this run", classified("create.first_entity_of_process"))
        self.watch.append({"id": "A-PROFILER", "title": "KENSEI_DEBUG: the first CreateEntity of a process allocates EcsProfiler's ring buffer",
                           "status": st, "evidence": ev})

        if self.dense:
            rt0 = next(iter(self.dense))
            w = self.dense[rt0]["worst"]
            reserve_cases = [c for c in self.dense[rt0]["cases"] if c["kind"] != "organic"]
            if not reserve_cases:
                st, ev = "not in this version", "Filter.Reserve / World.Reserve are missing"
            else:
                st = "present" if w else "absent"
                ev = ("worst %s(%d): filter %d slots, pool %d (x%.2f); classified: %s" % (
                    w["kind"], w["requested"], w["filter"], w["pool"], w["filter"] / w["requested"], classified("filter.dense_capacity"))
                    if w else "every reserve sizes the filter within x%.2f of the request" % DENSE_OVERSHOOT)
            self.watch.append({"id": "FILTER-DENSE", "title": "a reserve sizes a filter's dense array up to twice the request (pools: exactly)",
                               "status": st, "evidence": ev})

    # -- output ----------------------------------------------------------
    def analyze(self):
        self.check_meter()
        self.compare()
        self.compare_runtimes()
        if not self.invalid:
            self.check_contracts()
        self.memory()
        self.filter_dense()
        if self.invalid:
            # A meter that is not exact gives no items; the measurements stay in the tables.
            self.obs = {}
        self.invoke_table()
        self.merge()
        self.watchlist()
        return self

    def write_repros(self):
        """Repro folders for findings, regressions and questions. Returns {id: folder}."""
        folders = {}
        repro_dir = os.path.join(self.out_dir, "repro")
        # A repro folder belongs to one run: folders of items an earlier run had must not linger.
        shutil.rmtree(repro_dir, ignore_errors=True)
        os.makedirs(repro_dir, exist_ok=True)
        cfg = "KENSEI_DEBUG" if self.debug else "release"
        for item in self.findings + self.regressions + self.questions:
            sid = item["scenario"]
            rsid = "mem.reserve_entities" if sid.startswith("mem.") else sid
            proxy = dict(item, scenario=rsid)
            folder, path = R.write(proxy, repro_dir, os.path.abspath(self.target["src"]), self.raw["flavor"], cfg)
            if folder:
                folders[item["id"]] = folder
                item["repro"] = os.path.relpath(path, self.out_dir)
                item["repro_run"] = "dotnet run -c Release --project %s" % S.rel(os.path.join(folder, R.safe_name(item["id"]) + ".csproj"))
            else:
                item["repro"] = None
                item["repro_note"] = path
            with open(os.path.join(repro_dir, R.safe_name(item["id"]) + ".json"), "w", encoding="utf-8") as f:
                json.dump(item, f, ensure_ascii=False, indent=1, default=list)
        return folders

    def write(self):
        os.makedirs(self.out_dir, exist_ok=True)
        doc = {
            "module": S.MODULE, "format": 2, "flavor": self.raw["flavor"], "config": self.cfg, "mode": self.raw["mode"],
            "baseline": self.raw["baseline"], "generated": self.raw["generated"], "command": self.raw["command"],
            "target_src": self.target["src"], "baseline_src": self.base["src"],
            "target_src_rel": S.rel(self.target["src"]), "baseline_src_rel": S.rel(self.base["src"]),
            "package_versions": {"target": self.target.get("package_version"), "baseline": self.base.get("package_version")},
            "runtimes": {rt: (self.target["runtimes"][rt].get("info") or {}).get("runtime") for rt in self.runtimes},
            "meter": {rt: {k: v for k, v in (self.target["runtimes"][rt].get("info") or {}).items()
                           if k.startswith("selftest") or k.startswith("artifact")} for rt in self.runtimes},
            "contract_sources": self.corpus.sources,
            "contract_profile": self.profile.to_json(),
            "promise_drift": C.drift(self.profile, self.base_profile),
            "invalid": self.invalid,
            "findings": self.findings,
            "regressions": self.regressions,
            "contract_questions": self.questions,
            "documented": self.documented,
            "watchlist": self.watch,
            "sporadic": self.sporadic,
            "jit_dependent": self.jit_dependent,
            "notes": self.notes,
            "runtime_diff": self.runtime_diff,
            "jit_dependent_first": self.jit_first,
            "diff": self.diff,
            "invoke": self.invoke,
            "filter_dense": self.dense,
            "memory": self.mem_rows,
            "memory_diff": self.mem_diff,
            "confirmations": self.raw.get("confirmations"),
            "seconds": self.raw.get("seconds"),
        }
        with open(os.path.join(self.out_dir, "report.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, indent=1, default=list)
        with open(os.path.join(self.out_dir, "report.md"), "w", encoding="utf-8") as f:
            f.write(self.markdown(doc))
        return "%s, %s, %s, %s" % (plural(len(self.findings), "finding"), plural(len(self.regressions), "regression"),
                                   plural(len(self.questions), "contract question"), plural(len(self.documented), "documented allocation"))

    # -- markdown ---------------------------------------------------------
    def md_item_numbers(self, item):
        out = []
        for rt, d in item["runtimes"].items():
            if item["metric"] == "memory" and isinstance(d, dict) and "cases" in d and isinstance(d["cases"], dict):
                out.append("- %s: sparse per pool after Reserve: %s B" % (rt, fmt_b(d["sparse_bytes_per_pool"])))
                for k, v in d["cases"].items():
                    late = (", Reserve after the pools exist %s" % fmt_mb(v["late_alloc"])) if v.get("late_alloc") else ""
                    out.append("  - %s: Reserve %s (live %s) against %s without Reserve (live %s), x%.1f allocated; of it pool "
                               "sparse arrays %s, filters %s, world arrays %s%s"
                               % (k, fmt_mb(v["reserve_alloc"]), fmt_mb(v["reserve_live"]), fmt_mb(v["organic_alloc"]),
                                  fmt_mb(v["organic_live"]), v["ratio_alloc"] or 0, fmt_mb(v["sparse_bytes"]),
                                  fmt_mb(v["filter_bytes"]), fmt_mb(v["world_reserve_bytes"]), late))
                continue
            out.append("- %s: %s" % (rt, ", ".join("%s = %s" % (k, fmt_num(k, v)) for k, v in d.items()
                                             if k != "extra" and v not in (None, [], {}, ""))))
        return out

    def md_attribution(self, item):
        out = []
        for rt, a in (item.get("attribution") or {}).items():
            exps = "; ".join(a["experiments"]) or "-"
            if a["causes"]:
                c = a["causes"][0]
                verdict = "cause: %s, explains %s%s B" % (c["cause"], "all " if c["full"] else "", fmt_b(c["explained_bytes"]))
            else:
                verdict = "no experiment removed the difference: cause not established"
            out.append("- Experiments (%s, fresh processes): %s -> %s" % (rt, exps, verdict))
        return out

    def md_repro(self, item):
        out = []
        if item.get("repro"):
            out.append("- Repro: `%s` (`%s`)" % (item["repro"], item.get("repro_run")))
            rc = item.get("repro_check")
            if rc:
                if rc.get("build") == "failed":
                    out.append("- Repro check: **build failed**: %s" % rc.get("output", "")[-300:].replace("\n", " "))
                else:
                    out.append("- Repro check (built and run on its own): " + "; ".join(
                        "%s: %s (repro %s, stand %s%s)" % (rt, v.get("status"), fmt_num("", v.get("repro")), fmt_num("", v.get("stand")),
                                                         "; " + v["note"] if v.get("note") else "")
                        for rt, v in rc.items()))
        elif item.get("repro_note"):
            out.append("- Repro: none (%s)" % item["repro_note"])
        return out

    def md_item(self, f, w, L):
        w("### %s" % f["id"])
        w("")
        w("- Status: **%s**, severity **%s**; signature `%s`; rule: %s" % (f["class_text"], f["severity"], f["signature"], f["rule"]))
        w("- Scenario: `%s`: %s; metric: %s" % (f["scenario"], f["title"], f["metric_text"]))
        if f.get("contract_quote"):
            w("- Contract: «%s»" % f["contract_quote"])
        basis = f.get("contract_basis") or {}
        if basis.get("reason") and f["class"] != "documented":
            w("- Contract profile: %s" % basis["reason"])
        if f.get("question"):
            w("- %s" % f["question"])
        L.extend(self.md_item_numbers(f))
        if f.get("regression"):
            w("- Also grew against the baseline: " + "; ".join("%s: %s" % (rt, ", ".join("%s = %s" % (k, fmt_num(k, v)) for k, v in d.items() if k != "rule"))
                                                              for rt, d in f["regression"].items()))
        if f.get("contract"):
            w("- Contract on this regression: " + "; ".join("%s: %s (%s)" % (rt, c["verdict"], c.get("reason") or "-") for rt, c in f["contract"].items()))
        L.extend(self.md_attribution(f))
        for rt, text in (f.get("explanations") or {}).items():
            if text:
                w("- Analysis (%s): %s" % (rt, text))
        w("- Parameters: %s; seed: %s (the scenario is deterministic)" % (json.dumps(f["params"], ensure_ascii=False), f["seed"]))
        L.extend(self.md_repro(f))
        w("- Rerun: `%s`" % f["command"])
        w("")

    def markdown(self, doc):
        L = []
        w = L.append
        cfg_name = "KENSEI_DEBUG" if self.debug else "release"
        prof = doc["contract_profile"]
        w("# Stand/alloc: %s, %s (baseline %s)" % (self.raw["flavor"], cfg_name, self.raw["baseline"]))
        w("")
        w("- Target: `%s` (package.json %s; contract profile: %s)" % (doc["target_src_rel"], doc["package_versions"]["target"], prof["label"]))
        w("- Baseline: `%s` (package.json %s)" % (doc["baseline_src_rel"], doc["package_versions"]["baseline"]))
        w("- Runtimes: " + ", ".join("%s = %s" % (k, v) for k, v in doc["runtimes"].items()))
        w("- Mode: %s; one fresh process per scenario: %s; one-time repeats: %d (+%d confirmation runs for candidates); %s s"
          % (self.raw["mode"], "yes" if self.raw.get("isolate") else "no", self.raw["plan"]["repeats"],
             self.raw.get("confirm_runs", 0), self.raw.get("seconds")))
        w("- Command: `%s`" % self.raw["command"])
        w("- Generated: %s" % self.raw["generated"])
        w("")
        w("**%s** (contract violations), **%s** (grew against the baseline, the contract does not forbid it), %s, %s." % (
            plural(len(self.findings), "finding"), plural(len(self.regressions), "regression"),
            plural(len(self.questions), "contract question"), plural(len(self.documented), "documented allocation")))
        w("")
        w("Meter (Stand/alloc/Meter): a window is its own NoInlining + AggressiveOptimization method holding only the two counter "
          "reads and the loop over the operation (no Assert, boxing or string formatting, no OSR inside the window); warm-up is "
          "separate; bytes per operation per window, one-time bytes as the first pass in a fresh process. Self-test (empty "
          "window = 0, new object() = 24, new byte[100] = 128):")
        for rt, m in doc["meter"].items():
            w("- %s: %s; the pattern `Assert.That(allocated, Is.EqualTo(0))` without NUnit in an optimized build would show %s B"
              % (rt, "OK" if m.get("selftest_ok") else "FAILED: no items are reported", m.get("artifact_boxed_expected_after_window")))
        if self.invalid:
            w("")
            w("**Measurements invalid:** " + "; ".join(self.invalid))
        w("")
        w("## Canaries")
        w("")
        w("| id | problem | status | data |")
        w("|---|---|---|---|")
        for x in self.watch:
            w("| %s | %s | **%s** | %s |" % (x["id"], x["title"], x["status"], x["evidence"].replace("|", "\\|")))
        for title, items, empty in (
                ("Findings (contract violations)", self.findings, "None: no allocation where the documentation of this tree promises none."),
                ("Regressions (not defects)", self.regressions, "None: nothing allocates more than the baseline."),
                ("Contract questions (not defects)", self.questions, "None.")):
            w("")
            w("## %s" % title)
            w("")
            if not items:
                w(empty)
                continue
            groups = {}
            for f in items:
                groups.setdefault((f["metric"], tuple(f.get("cause_keys") or ())), []).append(f)
            for (metric, causes), group in groups.items():
                if causes and len(group) > 1 and causes[0] in KNOBS:
                    knob = causes[0]
                    w("### Group: %s, one cause" % plural(len(group), "scenario"))
                    w("")
                    w("Cause (established by %s in fresh processes): %s. Scenarios: %s." % (
                        KNOBS[knob][0], KNOBS[knob][2], ", ".join("`%s`" % i["scenario"] for i in group)))
                    w("")
                for f in group:
                    self.md_item(f, w, L)
        w("")
        w("## Documented allocations (not defects)")
        w("")
        if not self.documented:
            w("None.")
        for d in self.documented:
            nums = "; ".join("%s: %s" % (rt, ", ".join("%s = %s" % (k, fmt_num(k, v)) for k, v in x.items() if k != "extra"))
                             for rt, x in d["runtimes"].items())
            w("- `%s`: %s: %s. Documentation: «%s»" % (d["scenario"], d["title"], nums, d.get("contract_quote")))
        w("")
        w("## Contract profile")
        w("")
        w("Tree version: %s. A promise is a defect gate only when one of its sentences is in this tree's docs and the version "
          "is at least the one that first documented it (tools/contracts.py)." % prof["label"])
        w("")
        w("| promise | since | in these docs | applies | why |")
        w("|---|---|---|---|---|")
        for st in prof["promises"]:
            w("| %s: %s | %s | %s | %s | %s |" % (st["id"], st["what"], st["since"] or "-", "yes" if st["found"] else "no",
                                                "yes" if st["applies"] else "no", st["reason"]))
        if prof["stale_rules"]:
            w("")
            w("**Stale rules:** %s: the version should document them, but no sentence matched (reworded docs?). Update "
              "tools/contracts.py, otherwise these checks are silently off." % ", ".join(prof["stale_rules"]))
        drift = doc["promise_drift"]
        if drift["dropped"] or drift["added"]:
            w("")
            if drift["dropped"]:
                w("**Promises active on the baseline but not here:** " + "; ".join("%s (%s)" % (d["id"], d["target_reason"]) for d in drift["dropped"]))
            if drift["added"]:
                w("Promises new against the baseline: " + ", ".join(d["id"] for d in drift["added"]) + ".")
        w("")
        w("## Listeners.Invoke against a hand-written loop (8 listeners)")
        w("")
        w("B/op: the usual tiered JIT; ns/op: fully optimized code (DOTNET_TieredCompilation=0), closer to IL2CPP. Invoke is "
          "called through a delegate bound by reflection (the stand compiles against 2.1.1 too); its cost is the last row, and "
          "the ratio without the stand's delegate subtracts it.")
        w("")
        w("| variant | " + " | ".join("%s B/op | %s ns/op | %s x loop" % (rt, rt, rt) for rt in self.runtimes) + " |")
        w("|---|" + "---|---|---|" * len(self.runtimes))
        for sid in ("invoke.static_lambda", "invoke.static_lambda_onerror", "invoke.method_group",
                    "invoke.capturing_lambda", "invoke.manual_loop", "invoke.delegate_overhead"):
            cells = []
            for rt in self.runtimes:
                x = self.invoke.get(rt, {}).get(sid)
                if not x:
                    cells += ["-", "-", "-"]
                elif not x["available"]:
                    cells += ["not in this version", "-", "-"]
                else:
                    ratio = ""
                    if x.get("ratio_to_loop"):
                        ratio = "%.1fx (%.1fx)" % (x["ratio_to_loop"], x.get("ratio_to_loop_without_stand_delegate") or x["ratio_to_loop"])
                    cells += [fmt_b(x["per_op"]), "%.1f" % x["ns"] if x["ns"] else "-", ratio or "-"]
            w("| %s | %s |" % (TITLES.get(sid, sid), " | ".join(cells)))
        w("")
        w("In brackets: the ratio without the stand's delegate. The ratios of this table use medians of different scenarios "
          "measured one after another; the paired measurement below is steadier.")
        for rt in self.runtimes:
            p = self.invoke.get(rt, {}).get("paired")
            if p:
                w("")
                w("- %s, paired rounds (windows of the loop, Invoke(static), Invoke(method group) and the stand's empty delegate "
                  "alternate in one process; %d rounds): Invoke(static) / loop = **%.1fx** (%.1f-%.1fx; without the stand's "
                  "delegate %.1fx), Invoke(method group) / loop = **%.1fx** (%.1f-%.1fx)" % (
                      rt, p["rounds"], p["ratio_static_median"], p["ratio_static_min"], p["ratio_static_max"],
                      p["ratio_static_without_stand_delegate_median"], p["ratio_method_group_median"],
                      p["ratio_method_group_min"], p["ratio_method_group_max"]))
        if self.dense:
            w("")
            w("## Filter dense capacity against the request")
            w("")
            w("Slots of the filter's dense array (without the terminator slot) and of the pool of its type after the same request. "
              "organic: N entities entered, no Reserve; filter_reserve: Filter.Reserve(N) (pool: ComponentPool.Reserve(N)); "
              "world_reserve_existing / _new: World.Reserve(N, N) with the filter created before / after; "
              "filter_reserve_after_world: World.Reserve(N - 1, N - 1), then Filter.Reserve(N).")
            w("")
            w("| case | N | filter | pool | filter / N | runtime |")
            w("|---|---|---|---|---|---|")
            for rt, d in self.dense.items():
                for c in d["cases"]:
                    w("| %s | %d | %d | %d | %.2f | %s |" % (c["kind"], c["requested"], c["filter"], c["pool"], c["filter"] / c["requested"], rt))
                break
        w("")
        w("## Memory of Reserve(entities): N types x 50 000 entities")
        w("")
        w("alloc: GC.GetTotalAllocatedBytes(true) while the world was built; live: growth of GC.GetTotalMemory(true) after a full "
          "collection (includes heap fragmentation, may exceed alloc); pool: the sum of ComponentPoolBase.AllocatedBytes; thread: "
          "GC.GetAllocatedBytesForCurrentThread (exact, versions are compared by it). Layouts: rare: the types on the first 16 "
          "entities, spread: on the last 16; reserve_*: World.Reserve(50 000) before pools and filters exist; reserve_late_rare: "
          "Reserve after the systems created pools and filters.")
        for rt in self.runtimes:
            t = {(r["types"], r["filters"], r["layout"]): r for r in self.mem_rows
                 if r["role"] == "target" and r["runtime"] == rt and r.get("available")}
            a, b, o = t.get((1000, 0, "reserve_rare")), t.get((1000, 100, "reserve_rare")), t.get((1000, 0, "organic_rare"))
            if a and o:
                per_filter = ((b["alloc_total"] - a["alloc_total"]) / 100) if b else None
                w("")
                w("- %s, 1000 types: Reserve **%s** against **%s** without Reserve (x%.1f allocated; live %s against %s). Of it pool "
                  "sparse arrays %s (%s B per pool), world arrays %s%s."
                  % (rt, fmt_mb(a["alloc_total"]), fmt_mb(o["alloc_total"]), a["alloc_total"] / o["alloc_total"], fmt_mb(a["live"]),
                     fmt_mb(o["live"]), fmt_mb(a["pool_sparse_ints"] * 4), fmt_b(a["pool_sparse_ints"] * 4 // max(1, a["pools"])),
                     fmt_mb(a["reserve_call"]),
                     ("; every filter after Reserve adds %s (100 filters: %s)" % (fmt_mb(per_filter), fmt_mb(b["alloc_total"])))
                     if per_filter else ""))
        w("")
        w("| version | runtime | N | filters | layout | alloc | live | pool | sparse int | thread |")
        w("|---|---|---|---|---|---|---|---|---|---|")
        for r in sorted(self.mem_rows, key=lambda r: (r["role"] != "target", r["runtime"], r["types"], r["filters"], r["layout"])):
            if not r.get("available"):
                w("| %s | %s | %s | %s | %s | not in this version | | | | |" % (r["flavor"], r["runtime"], r["types"], r["filters"], r["layout"]))
                continue
            w("| %s | %s | %d | %d | %s | %s | %s | %s | %s | %s |" % (
                r["flavor"], r["runtime"], r["types"], r["filters"], r["layout"], fmt_mb(r["alloc_total"]),
                fmt_mb(r["live"]), fmt_mb(r["pool_allocated_bytes"]), fmt_b(r["pool_sparse_ints"]), fmt_b(r["alloc_thread"])))
        if self.mem_diff:
            w("")
            w("Memory against the baseline (thread bytes; a regression grows by more than %s B and more than 1%%): %s" % (
                fmt_b(MEM_REGRESSION_MIN), "; ".join("%s %s N=%d f=%d: %+d B" % (d["runtime"], d["layout"], d["types"], d["filters"], d["delta"])
                                                     for d in self.mem_diff)))
        w("")
        w("## Difference table: %s -> %s (%s)" % (self.raw["baseline"], self.raw["flavor"], cfg_name))
        w("")
        w("One-time: the first pass of a scenario (minimum over fresh-process runs); B/op: steady state; long run: bytes per "
          "geometric span; ns: FullOpts, for reference.")
        w("")
        w("| scenario | runtime | one-time baseline | one-time target | B/op baseline | B/op target | ns baseline | ns target | marks |")
        w("|---|---|---|---|---|---|---|---|---|")
        for row in self.diff:
            def cell(x, k):
                if x is None:
                    return "-"
                if not x["available"]:
                    return "not in version"
                if x["errors"]:
                    return "error"
                v = x.get(k)
                if k == "ns":
                    return "%.1f" % v if v else "-"
                if k == "per_op" and v is None and x.get("spans"):
                    return "spans " + "/".join(fmt_b(s) for s in x["spans"])
                return fmt_b(v)
            b, t = row["baseline"], row["target"]
            w("| %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
                row["scenario"], row["runtime"], cell(b, "first_min"), cell(t, "first_min"), cell(b, "per_op"),
                cell(t, "per_op"), cell(b, "ns"), cell(t, "ns"), "; ".join(row["marks"]) or ""))
        w("")
        unavailable = []
        for r in self.diff:
            for key, label in (("target", self.raw["flavor"]), ("baseline", self.raw["baseline"])):
                x = r[key]
                if x and not x["available"] and (r["scenario"], label, x["reason"]) not in unavailable:
                    unavailable.append((r["scenario"], label, x["reason"]))
        if unavailable:
            w("## Unavailable scenarios")
            w("")
            for sid, label, reason in unavailable:
                w("- `%s` in %s: %s" % (sid, label, reason))
            w("")
        errors = [(r["scenario"], r["runtime"], (r["target"] or {}).get("errors") or (r["baseline"] or {}).get("errors"))
                  for r in self.diff if (r["target"] and r["target"]["errors"]) or (r["baseline"] and r["baseline"]["errors"])]
        if errors:
            w("## Scenario errors")
            w("")
            for sid, rt, e in errors:
                w("- `%s` (%s): %s" % (sid, rt, e))
            w("")
        if self.sporadic:
            w("## Sporadic allocations (in some windows only, not items)")
            w("")
            for s in self.sporadic:
                w("- `%s` %s %s: windows %s..%s B per %d operations" % (s["scenario"], s["role"], s["runtime"],
                                                                       s["steady"]["min_window"], s["steady"]["max_window"],
                                                                       s["steady"]["per_window"]))
            w("")
        if self.jit_dependent:
            w("## JIT dependence (B/op tiered against FullOpts, not items)")
            w("")
            for j in self.jit_dependent:
                w("- `%s` %s %s: tiered %.2f B/op, FullOpts %.2f B/op" % (j["scenario"], j["role"], j["runtime"],
                                                                         j["tiered_per_op"], j["fullopts_per_op"]))
            w("")
        if self.jit_first:
            w("## One-time differences of the runtime's code generation (not items)")
            w("")
            w("More one-time bytes than the baseline that vanish once the runtime compiles every method fully optimized at "
              "once (DOTNET_TieredCompilation=0), while no experiment doing the library's own one-time work explains them: "
              "which methods ReadyToRun covers and what tier-0 code does differ between two builds of different code. Not "
              "regressions of the library; IL2CPP compiles ahead of time.")
            w("")
            for j in self.jit_first:
                w("- `%s`: %s" % (j["scenario"], "; ".join("%s: %s B against %s B (+%s B); %s" % (
                    rt, fmt_b(d.get("target_first")), fmt_b(d.get("baseline_first")), fmt_b(d.get("delta")),
                    "; ".join((j.get("attribution") or {}).get(rt, {}).get("experiments") or []))
                    for rt, d in j["runtimes"].items())))
            w("")
        if self.runtime_diff:
            w("## Runtimes disagree (bytes per operation net10 against net8, not items)")
            w("")
            for d in self.runtime_diff:
                w("- `%s`: net10 %s B/op, net8 %s B/op: %s" % (d["scenario"], fmt_b(d["net10_per_op"]), fmt_b(d["net8_per_op"]), d["hint"]))
            w("")
        if self.notes:
            w("## Notes")
            w("")
            for n in self.notes:
                w("- %s" % n)
            w("")
        if not self.raw.get("isolate", True):
            w("One-time bytes were not compared: every scenario ran in one process (--no-isolate), and a first pass depends on "
              "the scenarios before it.")
            w("")
        w("## Contract sources")
        w("")
        for src in self.corpus.sources:
            w("- `%s`" % src)
        w("")
        return "\n".join(L) + "\n"


def candidates(raw):
    """{sid: {runtime: set(metrics)}} of everything that would become an item other than documented, for
    the confirmation and attribution runs. Memory rows and capacity cases are not probe windows."""
    rep = Report(raw, None)
    rep.check_meter()
    rep.compare()
    if not rep.invalid:
        rep.check_contracts()
    out = {}
    for (sid, metric), o in rep.obs.items():
        if sid.startswith("mem.") or metric in ("memory", "capacity"):
            continue
        rts = set(o["regression"]) | {rt for rt, c in o["contract"].items() if c["verdict"] != "documented"}
        for rt in rts:
            out.setdefault(sid, {}).setdefault(rt, set()).add(metric)
    return out
