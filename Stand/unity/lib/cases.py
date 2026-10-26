#!/usr/bin/env python3
"""KenseiECS test stand, module unity: case selection for run.sh (no Unity needed).

  cases.py <cases.json> groups <tier> [<only>]          -> "group id;id;..." per line, in run order
  cases.py <cases.json> list [<tier>]                   -> table of the cases
  cases.py <cases.json> skip <raw dir> <id;id> <reason> -> writes raw/cases/<id>.json with verdict "skipped"
                                                           (a case the environment cannot run: a build module
                                                           or package that is not installed)
"""
import datetime
import json
import os
import sys

GROUP_ORDER = ["static", "editor", "player", "sbp", "aa", "run"]
# Groups that build for the active build target (the module of --target must be installed).
TARGET_GROUPS = ["player", "sbp", "aa"]


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["cases"]


def selected(cases, tier, only):
    if only:
        wanted = set(x for x in only.replace(",", ";").split(";") if x)
        unknown = wanted - set(c["id"] for c in cases)
        if unknown:
            sys.stderr.write("unknown case id(s): %s\n" % ", ".join(sorted(unknown)))
            sys.exit(2)
        return [c for c in cases if c["id"] in wanted]
    return [c for c in cases if tier == "full" or c.get("tier", "quick") == "quick"]


def skip(cases, raw, ids, reason):
    by_id = {c["id"]: c for c in cases}
    os.makedirs(os.path.join(raw, "cases"), exist_ok=True)
    for cid in [x for x in ids.split(";") if x]:
        c = by_id[cid]
        r = {
            "id": cid, "group": c["group"], "kind": c["kind"], "title": c["title"], "rule": c["rule"], "tier": c.get("tier", "quick"),
            "verdict": "skipped", "expected": c.get("expect", ""), "actual": "n/a", "reason": "", "naReason": reason,
            "durationSec": 0.0, "started": datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S"), "log": "cases/%s.log" % cid,
            "applied": {}, "evidence": {}, "checks": [], "violations": [], "questions": [],
        }
        with open(os.path.join(raw, "cases", cid + ".json"), "w", encoding="utf-8") as f:
            json.dump(r, f, ensure_ascii=False, indent=2)
        with open(os.path.join(raw, "cases", cid + ".log"), "w", encoding="utf-8") as f:
            f.write("case %s (%s): %s\nverdict: skipped\nnot run: %s\n" % (cid, c["kind"], c["title"], reason))
        print("  END case %s: skipped (%s)" % (cid, reason))


def main():
    path, cmd = sys.argv[1], sys.argv[2]
    cases = load(path)
    if cmd == "groups":
        tier = sys.argv[3]
        only = sys.argv[4] if len(sys.argv) > 4 else ""
        chosen = selected(cases, tier, only)
        for g in GROUP_ORDER + sorted(set(c["group"] for c in chosen) - set(GROUP_ORDER)):
            ids = [c["id"] for c in chosen if c["group"] == g]
            if ids:
                print(g, ";".join(ids))
    elif cmd == "list":
        tier = sys.argv[3] if len(sys.argv) > 3 else "full"
        for c in selected(cases, tier, ""):
            print("%-18s %-5s %-7s %-17s %s" % (c["id"], c.get("tier", "quick"), c["group"], c["kind"], c["title"]))
    elif cmd == "skip":
        skip(cases, sys.argv[3], sys.argv[4], sys.argv[5])
    else:
        sys.stderr.write(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
