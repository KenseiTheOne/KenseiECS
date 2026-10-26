#!/usr/bin/env python3
"""KenseiECS test stand, module unity: the documented contract of the tree under test.

Two things decide whether a rule violation is a defect of the tree under test:

* The version profile. The module reads the package version from package.json of the tree under
  test (and whether CHANGELOG.md starts with an [Unreleased] section). A rule in rules.json names
  the version whose documents first make its promise ("since"). A violation in an older released
  version is a question to the contract, not a defect: the stand never holds a version to a
  promise that only a later version makes. An unreleased tree (the working copy of the next
  release) is held to a newer promise only when its own documents already make it.
* The contract quotes. A rule lists "anchors": quotes from the docs (docs/...) and the package
  (KenseiECS/...: CHANGELOG, XML docs of the code) that the promise relies on. Each version words
  its contract its own way, so the report cites the anchors found in *this* tree; a rule none of
  whose anchors is in this tree (all of them, for a rule with "anchorsAll") is a question.

  contract.py <pkg dir> <docs dir|-> <rules.json>   -> profile, and which anchors of every rule are found
"""
import json
import os
import re
import sys

_cache = {}


def normalize(text):
    """Markdown and comment markup removed, whitespace collapsed, lower case."""
    text = re.sub(r"^\s*///?\s?", " ", text, flags=re.M)      # XML doc and comment prefixes
    text = text.replace("**", "").replace("__", "").replace("`", "")
    text = re.sub(r"<[^>]+>", " ", text)                        # <see cref=.../>, <c>, <para>
    text = re.sub(r"\s+", " ", text)
    return text.lower()


def resolve(file, pkg, docs):
    """Repository-relative path of an anchor -> path in the tree under test (None if not available)."""
    if file.startswith("KenseiECS/"):
        return os.path.join(pkg, file[len("KenseiECS/"):])
    if file.startswith("docs/"):
        return os.path.join(docs, file[len("docs/"):]) if docs else None
    return None


def read_normalized(path):
    if path not in _cache:
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                _cache[path] = normalize(f.read())
        except OSError:
            _cache[path] = None
    return _cache[path]


def anchors_found(rule, pkg, docs):
    """(found, checked): the anchors of the rule present in the tree, and whether the rule has anchors at all.
    A rule without anchors (package layout, compile, runtime) always applies."""
    anchors = rule.get("anchors") or []
    found = []
    for a in anchors:
        path = resolve(a["file"], pkg, docs)
        text = read_normalized(path) if path else None
        if text is not None and normalize(a["quote"]).strip() in text:
            found.append(a)
    return found, bool(anchors)


def missing_files(rules, pkg, docs):
    files = sorted({a["file"] for r in rules.values() for a in r.get("anchors") or []})
    out = []
    for f in files:
        path = resolve(f, pkg, docs)
        if not path or not os.path.exists(path):
            out.append(f)
    return out


# ------------------------------------------------------------------------------------ version profile

_VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)")


def parse_version(text):
    """'2.2.1' -> (2, 2, 1); anything unparsable -> None."""
    m = _VERSION.match(str(text or "").strip())
    return tuple(int(x) for x in m.groups()) if m else None


def version_text(v):
    return ".".join(str(x) for x in v) if v else "?"


def profile(pkg):
    """The version profile of the tree under test: package.json version, and whether CHANGELOG.md
    starts with an [Unreleased] section that lists changes (the tree is then the next release in the
    making, judged by the promises of its own documents)."""
    out = {"version": None, "unreleased": False, "label": "?", "changelogTop": None, "source": "package.json"}
    try:
        with open(os.path.join(pkg, "package.json"), encoding="utf-8") as f:
            out["version"] = json.load(f).get("version")
    except (OSError, ValueError):
        out["source"] = "package.json unreadable"
    top, entries = None, 0
    try:
        with open(os.path.join(pkg, "CHANGELOG.md"), encoding="utf-8") as f:
            for line in f:
                m = re.match(r"^## \[([^\]]+)\]", line)
                if m:
                    if top is not None:
                        break
                    top = m.group(1).strip()
                    continue
                if top is not None and line.lstrip().startswith(("- ", "* ")):
                    entries += 1
    except OSError:
        pass
    out["changelogTop"] = top
    out["unreleased"] = bool(top and top.lower() == "unreleased" and entries > 0)
    out["label"] = (out["version"] or "?") + (" + unreleased changes" if out["unreleased"] else "")
    return out


def judge(rule, prof, pkg, docs):
    """Whether a violation of <rule> is a defect of the tree under test.

    Returns (state, found, note): state "applies" (a defect), "later" (the promise is newer than the
    tree: a question) or "undocumented" (no quote of the promise in this tree: a question)."""
    found, checked = anchors_found(rule, pkg, docs)
    need_all = bool(rule.get("anchorsAll"))
    anchors = rule.get("anchors") or []
    documented = not checked or (len(found) == len(anchors) if need_all else bool(found))
    since = parse_version(rule.get("since"))
    version = parse_version(prof.get("version"))
    if since and version and version < since:
        if prof.get("unreleased") and checked and documented:
            return "applies", found, "promise of %s, made by the documents of this unreleased tree" % rule.get("since")
        return "later", found, "the promise first appears in %s; this tree is %s" % (rule.get("since"), prof.get("label"))
    if not documented:
        missing = [a for a in anchors if a not in found]
        return "undocumented", found, "the documents of this tree do not have %s of the quotes: %s" % (
            "all" if need_all else "any", "; ".join("%s: \"%s\"" % (a["file"], a["quote"]) for a in missing))
    return "applies", found, ""


def main():
    pkg, docs, rules_path = sys.argv[1], sys.argv[2], sys.argv[3]
    docs = None if docs == "-" else docs
    with open(rules_path, encoding="utf-8") as f:
        rules = json.load(f)
    prof = profile(pkg)
    print("version profile: %s (CHANGELOG top: %s)" % (prof["label"], prof["changelogTop"]))
    for rid, rule in rules.items():
        if rid.startswith("_"):
            continue
        state, found, note = judge(rule, prof, pkg, docs)
        print("%-34s since %-6s %-12s %d/%d %s" % (rid, rule.get("since", "-"), state, len(found), len(rule.get("anchors") or []), note))
    miss = missing_files({k: v for k, v in rules.items() if not k.startswith("_")}, pkg, docs)
    if miss:
        print("files not found: " + ", ".join(miss))


if __name__ == "__main__":
    main()
