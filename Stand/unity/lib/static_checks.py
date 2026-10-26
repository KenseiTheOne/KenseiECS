#!/usr/bin/env python3
"""KenseiECS test stand, module unity: package checks that need no Unity (group "static").

  static_checks.py <package folder> <raw out dir> <case ids;...>

Writes <raw>/cases/<id>.json and .log in the schema of StandModel.cs, so report.py treats them like
the Unity cases.

M1 (static-meta): every file and folder Unity imports has a .meta with a unique GUID. Installed from
   a git URL (README: "Add package from git URL") the package is immutable: Unity ignores an asset
   without a .meta ("has no meta file, but it's in an immutable folder. The asset will be ignored"),
   so a .cs file without one disappears from the compile. The stand itself installs a writable copy
   (file:), where Unity silently creates the missing .meta, so only this check sees it.
M2 (static-manifest): package.json is what UPM needs (name, version, unity, samples that exist) and
   its version is the latest released CHANGELOG entry. A candidate before its release is formatted (a
   non-empty "## [Unreleased]" on top, package.json already at the next version above the latest release)
   is not a finding: the check says so and passes.
"""
import datetime
import json
import os
import re
import sys

CODE_EXT = (".cs", ".asmdef", ".asmref", ".dll", ".rsp", ".so", ".a", ".dylib", ".bundle", ".jslib", ".aar", ".jar")


def ignored(name):
    # Unity skips these when importing (Manual: "Special folder names" / hidden assets).
    low = name.lower()
    return name.startswith(".") or name.endswith("~") or low == "cvs" or low.endswith(".tmp")


def new_result(case):
    return {
        "id": case["id"], "group": case["group"], "kind": case["kind"], "title": case["title"], "rule": case["rule"],
        "tier": case.get("tier", "quick"), "verdict": "", "expected": case.get("expect", "pass"), "actual": "",
        "reason": "", "naReason": "", "durationSec": 0.0, "started": datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
        "log": "cases/%s.log" % case["id"], "applied": {}, "evidence": {"logLines": [], "errors": []},
        "checks": [], "violations": [], "questions": [],
    }


def violate(r, rule, signature, detail):
    r["violations"].append({"rule": rule, "signature": signature, "detail": detail})
    r["checks"].append("FAIL [%s] %s" % (rule, detail))


def contains_code(folder):
    for d, dirs, files in os.walk(folder):
        dirs[:] = [x for x in dirs if not ignored(x)]
        if any(f.endswith(CODE_EXT) for f in files if not ignored(f)):
            return True
    return False


def static_meta(case, pkg):
    r = new_result(case)
    missing, orphans, guids = [], [], {}
    count = 0
    for d, dirs, files in os.walk(pkg):
        dirs[:] = sorted(x for x in dirs if not ignored(x))
        for name in dirs + sorted(files):
            if ignored(name) or name.endswith(".meta"):
                continue
            count += 1
            path = os.path.join(d, name)
            if not os.path.exists(path + ".meta"):
                missing.append(os.path.relpath(path, pkg))
        for name in files:
            if not name.endswith(".meta") or ignored(name):
                continue
            path = os.path.join(d, name)
            if not os.path.exists(path[:-5]):
                orphans.append(os.path.relpath(path, pkg))
            guid = None
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    if line.startswith("guid:"):
                        guid = line.split(":", 1)[1].strip()
                        break
            if guid is None:
                violate(r, "package-layout", "meta-without-guid", "%s has no guid" % os.path.relpath(path, pkg))
            else:
                guids.setdefault(guid, []).append(os.path.relpath(path, pkg))
    r["evidence"]["logLines"].append("assets (files and folders) of the package: %d, .meta: %d" % (count, sum(len(v) for v in guids.values())))
    for rel in missing:
        full = os.path.join(pkg, rel)
        code = rel.endswith(CODE_EXT) or (os.path.isdir(full) and contains_code(full))
        if code:
            violate(r, "package-layout-code", "missing-meta-code",
                    "%s has no .meta: in a package from a git URL Unity ignores it (the code drops out of the compile)" % rel)
        else:
            violate(r, "package-layout", "missing-meta",
                    "%s has no .meta: in a package from a git URL Unity ignores it with a warning" % rel)
    for guid, paths in sorted(guids.items()):
        if len(paths) > 1:
            violate(r, "package-layout-code", "duplicate-guid", "GUID %s in several .meta files: %s" % (guid, ", ".join(paths)))
    for rel in orphans:
        violate(r, "package-layout", "orphan-meta", "%s has no asset: Unity warns about it in a package from a git URL" % rel)
    if not missing:
        r["checks"].append("ok: all %d files and folders of the package have a .meta" % count)
    if all(len(p) == 1 for p in guids.values()):
        r["checks"].append("ok: the GUIDs of all .meta files are unique")
    if not orphans:
        r["checks"].append("ok: no .meta without its asset")
    return r


SEMVER = re.compile(r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")


def semver_key(version):
    """major.minor.patch as numbers, a pre-release (2.3.0-rc1) just below its release."""
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)(-.*)?$", version or "")
    if not m:
        return (-1, -1, -1, 0)
    return (int(m.group(1)), int(m.group(2)), int(m.group(3)), 0 if m.group(4) else 1)


def static_manifest(case, pkg):
    r = new_result(case)
    path = os.path.join(pkg, "package.json")
    try:
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    except (OSError, ValueError) as e:
        violate(r, "package-manifest", "package-json-unreadable", "package.json cannot be read: %s" % e)
        return r
    def check(ok, sig, what):
        if ok:
            r["checks"].append("ok: " + what)
        else:
            violate(r, "package-manifest", sig, what)
    check(manifest.get("name") == "com.kensei.ecs", "name", "name = com.kensei.ecs (%s)" % manifest.get("name"))
    version = manifest.get("version", "")
    check(bool(SEMVER.match(version)), "version", "version is SemVer (%s)" % version)
    check(bool(re.match(r"^\d{4}\.\d+$|^\d+\.\d+$", str(manifest.get("unity", "")))), "unity",
          "field unity is <major>.<minor> (%s)" % manifest.get("unity"))
    for sample in manifest.get("samples", []):
        sp = sample.get("path", "")
        check(bool(sp) and os.path.isdir(os.path.join(pkg, sp)), "sample-path:" + sp,
              "sample \"%s\": folder %s is in the package" % (sample.get("displayName", "?"), sp))
    changelog = os.path.join(pkg, "CHANGELOG.md")
    if os.path.exists(changelog):
        with open(changelog, encoding="utf-8") as f:
            text = f.read()
        released = None
        for line in text.splitlines():
            m = re.match(r"^## \[(\d+\.\d+\.\d+[^\]]*)\]", line)
            if m:
                released = m.group(1)
                break
        # Keep a Changelog: changes of the next release collect under [Unreleased] until the release is formatted.
        top = re.search(r"(?m)^## \[([^\]]+)\]", text)
        unreleased = re.search(r"(?ms)^## \[Unreleased\][^\n]*\n(.*?)(?=^## |\Z)", text)
        pending = bool(top and top.group(1).strip().lower() == "unreleased" and unreleased and unreleased.group(1).strip())
        if released != version and pending and released and semver_key(version) > semver_key(released):
            r["checks"].append("ok: CHANGELOG starts with [Unreleased] above [%s] and package.json is already the next version %s: "
                               "a release candidate whose CHANGELOG section is not formatted yet (rename [Unreleased] to [%s] for the release)"
                               % (released, version, version))
        else:
            check(released == version, "changelog-version",
                  "the latest released section of CHANGELOG (%s) is the version of package.json (%s)%s" % (
                      released, version, " (an [Unreleased] section is on top, but package.json is not above the latest release)" if pending else ""))
    r["evidence"]["logLines"].append("package.json: %s %s, unity %s" % (manifest.get("name"), version, manifest.get("unity")))
    return r


def main():
    pkg, raw, ids = sys.argv[1], sys.argv[2], sys.argv[3]
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "..", "cases.json"), encoding="utf-8") as f:
        cases = {c["id"]: c for c in json.load(f)["cases"]}
    os.makedirs(os.path.join(raw, "cases"), exist_ok=True)
    for cid in [x for x in ids.split(";") if x]:
        case = cases[cid]
        start = datetime.datetime.now()
        if case["kind"] == "static-meta":
            r = static_meta(case, pkg)
        elif case["kind"] == "static-manifest":
            r = static_manifest(case, pkg)
        else:
            r = new_result(case)
            r["verdict"] = "inconclusive"
            r["reason"] = "unknown kind " + case["kind"]
        if not r["verdict"]:
            r["actual"] = "fail" if r["violations"] else "pass"
            r["verdict"] = "finding" if r["violations"] else "ok"
        r["durationSec"] = (datetime.datetime.now() - start).total_seconds()
        with open(os.path.join(raw, "cases", cid + ".json"), "w", encoding="utf-8") as f:
            json.dump(r, f, ensure_ascii=False, indent=2)
        with open(os.path.join(raw, "cases", cid + ".log"), "w", encoding="utf-8") as f:
            f.write("case %s (%s): %s\nverdict: %s\npackage: %s\n\n" % (cid, case["kind"], case["title"], r["verdict"], pkg))
            f.write("\n".join(r["evidence"]["logLines"] + r["checks"]) + "\n")
        print("  END case %s: %s (expected pass, actual %s)" % (cid, r["verdict"], r["actual"]))


if __name__ == "__main__":
    main()
