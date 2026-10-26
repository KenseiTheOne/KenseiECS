#!/usr/bin/env python3
"""Summary of the KenseiECS test stand, built from the module reports.

  summary.py build --flavor F --src DIR [--steps FILE] [--run-start EPOCH] [--mode quick|full|existing]
                   [--command TEXT] [--modules m1,m2] [--configs rel,dbg] [--baseline B] [--baseline-summary FILE]
                   [--out-name NAME] [--unity-note TEXT]
      Reads Stand/out/reports/<module>/<F>-<rel|dbg|ci>/report.json, gives every entry of the catalog
      (Stand/summary/catalog.json) a status on this tree and writes Stand/out/reports/summary-F.md and .json
      (summary-NAME.* with --out-name: Stand/run.sh rebuilds a baseline as summary-B.as-baseline-of-F, so the
      baseline's own summary keeps its comparison). With --baseline B it also compares with summary-B.json (or
      --baseline-summary FILE): catalog statuses, signatures (new ones, new ones inside known families, gone ones,
      sharp growth of hits, changed status) and the promises the modules check. The summary ends with the API
      coverage: the public members of World, ComponentPool, Filter and Group against the contracts matrix and the
      calls of the fuzz-core and alloc code.
      --steps is the TSV of Stand/run.sh (flavor, role, module, cfg, exit, start, end, log): it tells which
      reports this run wrote and which ones come from an earlier run.

  summary.py selftest --flavor F [--src DIR] [--steps FILE] [--run-start EPOCH] [--modules m1,m2]
                      [--configs rel,dbg] [--command TEXT] [--unity-note TEXT]
      Collects the mutation self-tests of the modules (Stand/out/reports/<module>/selftest-F-<cfg>/selftest.json,
      alloc: selftest-F/) into Stand/out/reports/selftest-F.md and .json. Exit code 1 when a self-test failed
      or did not write its report.

  summary.py catalog-md [--release v2.2.0] [--current wt] [--current-name TEXT] [--also LABELS]
      Writes Stand/CATALOG.md from catalog.json and the summaries summary-<release>.json and summary-<current>.json.

Statuses of a catalog entry: defect (a promise in the documents of this version is broken, the repro is
verified), regression (behaviour changed against the baseline without breaking a promise), question (the
documents of this version neither promise nor forbid it, or the promise only appears in a later version),
documented (the documents of this version describe the behaviour), absent (checked, not reproduced), not-run
(not checked in this run). Per build, n/a means the problem cannot occur in that build.

Python 3.9 standard library only; run with -I.
"""
import argparse
import datetime
import hashlib
import html
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
STAND = os.path.dirname(HERE)
WT = os.path.dirname(STAND)
OUT = os.path.join(STAND, "out")
REPORTS = os.path.join(OUT, "reports")
CATALOG = os.path.join(HERE, "catalog.json")
REPO = os.environ.get("KENSEI_REPO") or WT
FORMAT = 2

MODULES = ["fuzz-core", "contracts", "alloc", "runner", "generator", "unity"]
CFG_TEXT = {"rel": "release", "dbg": "KENSEI_DEBUG", "ci": "CI (.NET 8)", "all": "all builds"}
CFG_RANK = {"rel": 0, "dbg": 1, "ci": 2, "all": 3}
SEV_RANK = {"critical": 0, "high": 1, "medium": 2, "low": 3, "info": 4, "question": 5}
# Most important first: several matchers or builds of one entry reduce to the first status present.
STATUSES = ["defect", "regression", "question", "documented", "absent", "not-run"]
NA = "n/a"
ITEM_STATUSES = ["defect", "regression", "question", "documented"]
OPEN = ("defect", "regression", "question")
STATUS_HELP = [
    ("defect", "a promise in the documents of this version is broken; the module has a verified repro"),
    ("regression", "the behaviour changed against the baseline without breaking a documented promise"),
    ("question", "reproduces, but the documents of this version neither promise nor forbid it (or the promise "
                 "appears only in a later version): the contract's author decides"),
    ("documented", "the documents of this version describe this behaviour"),
    ("absent", "checked, does not reproduce"),
    ("not-run", "not checked in this run: module not selected or failed, case only in --full, build not run, "
                "or the problem cannot occur in the builds that ran"),
]
# Sharp growth of one signature against the baseline (hits from the same fixed inputs; alloc: bytes).
SPIKE_FACTOR, SPIKE_MIN = 3, 20
MAGNITUDE_FACTOR = 2


# ----------------------------------------------------------------------------- helpers

def load_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def write_text(path, text):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, path)


def now_iso():
    return datetime.datetime.now().astimezone().isoformat(timespec="seconds")


def iso_of(epoch):
    return datetime.datetime.fromtimestamp(epoch).astimezone().isoformat(timespec="seconds")


def realpath(p):
    try:
        return os.path.realpath(p)
    except OSError:
        return p


def rel_stand(path):
    """A path relative to Stand/ (for links), or the path itself when it lies elsewhere."""
    if not path:
        return None
    p = os.path.abspath(path)
    if p.startswith(STAND + os.sep):
        return os.path.relpath(p, STAND)
    return p


def rel_root(path):
    """A path relative to the repository root (KenseiECS, Stand/out/src/v2.2.0/KenseiECS), for display."""
    if not path:
        return None
    p = realpath(os.path.abspath(path))
    root = realpath(WT)
    if p == root:
        return "."
    if p.startswith(root + os.sep):
        return os.path.relpath(p, root)
    return p


def md(text):
    """Text for a markdown table cell."""
    if text is None:
        return ""
    return str(text).replace("|", "\\|").replace("\n", " ").strip()


def short(text, n):
    text = md(text)
    return text if len(text) <= n else text[: n - 1].rstrip() + "…"


def pkg_version(src):
    d = load_json(os.path.join(src, "package.json")) or {}
    return d.get("version")


def git(args, cwd):
    """Read-only git; --no-optional-locks keeps even `status` from refreshing (writing) the index."""
    try:
        p = subprocess.run(["git", "--no-optional-locks", "-C", cwd] + args, stdout=subprocess.PIPE,
                           stderr=subprocess.DEVNULL, text=True, timeout=30)
        return p.stdout.strip() if p.returncode == 0 else None
    except (OSError, subprocess.SubprocessError):
        return None


def tree_identity(flavor, src):
    """Which commit the tree is: .stand-ref of an extracted ref, a tag, or this checkout."""
    tree = os.path.dirname(realpath(src))
    info = load_json(os.path.join(tree, ".stand-ref"))
    if info:
        return {"ref": info.get("ref"), "commit": info.get("commit"), "note": "extracted %s" % info.get("extracted", "?")}
    if realpath(tree) == realpath(WT):
        head = git(["log", "-1", "--format=%H"], WT)
        dirty = git(["status", "--porcelain", "--untracked-files=no", "--", "KenseiECS", "KenseiECS.Generators", "docs"], WT)
        return {"ref": "working copy", "commit": head,
                "note": "uncommitted changes in the package or docs" if dirty else "no uncommitted changes"}
    commit = git(["log", "-1", "--format=%H", "refs/tags/" + flavor, "--"], REPO)
    if commit:
        return {"ref": flavor, "commit": commit, "note": "tag"}
    return {"ref": None, "commit": None, "note": "origin of the tree unknown"}


def unity_digest(src):
    """The digest Stand/unity/run.sh records for the package copy (paths and contents)."""
    h = hashlib.sha256()
    for d, dirs, files in os.walk(src):
        dirs.sort()
        for name in sorted(files):
            p = os.path.join(d, name)
            h.update(os.path.relpath(p, src).encode() + b"\0")
            try:
                with open(p, "rb") as f:
                    h.update(f.read())
            except OSError:
                return None
    return h.hexdigest()[:16]


_TREE_MTIME = {}


def generated_doc(docs_root, path):
    """Files under docs/ that a clean checkout does not have (the repository's .gitignore): the API pages that
    docs/scripts/gen-api.mjs writes into docs/api/ (only docs/api/index.md is tracked) and VitePress' cache and
    build output in docs/.vitepress/cache/ and dist/. The stand never reads them, so a tree gives the same results
    with or without a local docs build."""
    rel = os.path.relpath(path, docs_root).replace(os.sep, "/")
    if rel.startswith("api/") and rel != "api/index.md":
        return True
    return rel.startswith((".vitepress/cache/", ".vitepress/dist/", "node_modules/")) or "/node_modules/" in rel


def tree_mtime(src):
    """Newest file of the package, its generator and its docs (bin/ and obj/ and generated docs left out)."""
    if src not in _TREE_MTIME:
        latest, newest = 0.0, None
        parent = os.path.dirname(src)
        docs_root = os.path.join(parent, "docs")
        for root in (src, os.path.join(parent, "KenseiECS.Generators"), docs_root):
            for d, dirs, files in os.walk(root):
                dirs[:] = [x for x in dirs if x not in ("bin", "obj", "node_modules", ".vitepress")]
                for n in files:
                    p = os.path.join(d, n)
                    if root == docs_root and generated_doc(docs_root, p):
                        continue
                    try:
                        m = os.path.getmtime(p)
                    except OSError:
                        continue
                    if m > latest:
                        latest, newest = m, p
        _TREE_MTIME[src] = (latest, newest)
    return _TREE_MTIME[src]


# ----------------------------------------------------------------------------- the tree's documents

_XML_CREF = re.compile(r'<see(?:also)?\s+(?:cref|langword|href)="([^"]*)"\s*/>')
_XML_NAMEREF = re.compile(r'<(?:paramref|typeparamref)\s+name="([^"]*)"\s*/>')
# Only the tags of XML doc comments: generics (Filter<T>, EventBuffer<T>) in a quote or a page must stay.
_XML_TAG = re.compile(r"</?(?:summary|remarks|para|c|code|see|seealso|param|paramref|typeparam|typeparamref|returns|exception|"
                      r"example|list|item|term|description|value|inheritdoc|b|i|br|note)\b[^<>]*>")
_MD_LINK = re.compile(r"\[([^\]]*)\]\([^)]*\)")


def _cref_name(ref):
    ref = re.sub(r"^[A-Z]:", "", ref)
    ref = re.sub(r"\(.*\)", "", ref)
    ref = ref.replace("{", "<").replace("}", ">")
    return ref.split(".")[-1]


def norm_doc_text(text):
    """A form of documentation text that a quote can be looked up in: XML doc markup and markdown removed (a cref
    becomes its name, a link its text, code spans and emphasis lose their marks), quotes unified, lower case,
    whitespace collapsed. Quotes from the module reports are normalized the same way."""
    text = _XML_CREF.sub(lambda m: _cref_name(m.group(1)), text or "")
    text = _XML_NAMEREF.sub(lambda m: m.group(1), text)
    text = _XML_TAG.sub(" ", text)
    text = html.unescape(text)
    text = _MD_LINK.sub(lambda m: m.group(1), text)
    text = text.replace("`", "").replace("**", "").replace("“", '"').replace("”", '"').replace("’", "'")
    return re.sub(r"\s+", " ", text).strip().lower()


_TREE_DOCS = {}


def tree_docs(src):
    """The documents of a tree as one normalized text: the XML doc comments of the package sources, the package's
    CHANGELOG.md and README.md, docs/ next to it (*.md, generated pages left out, see generated_doc) and the
    repository README.md. What a "documented" status must quote."""
    if src in _TREE_DOCS:
        return _TREE_DOCS[src]
    parts = []
    for d, dirs, files in os.walk(src):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj", "Samples~")]
        for n in sorted(files):
            if not n.endswith(".cs"):
                continue
            try:
                with open(os.path.join(d, n), encoding="utf-8", errors="replace") as f:
                    lines = [ln.strip()[3:] for ln in f if ln.strip().startswith("///")]
            except OSError:
                continue
            if lines:
                parts.append(" ".join(lines))
    parent = os.path.dirname(src)
    for p in (os.path.join(src, "CHANGELOG.md"), os.path.join(src, "README.md"), os.path.join(parent, "README.md")):
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                parts.append(f.read())
        except OSError:
            pass
    docs_root = os.path.join(parent, "docs")
    for d, dirs, files in os.walk(docs_root):
        dirs[:] = [x for x in dirs if x not in ("node_modules", ".vitepress")]
        for n in sorted(files):
            p = os.path.join(d, n)
            if not n.endswith(".md") or generated_doc(docs_root, p):
                continue
            try:
                with open(p, encoding="utf-8", errors="replace") as f:
                    parts.append(f.read())
            except OSError:
                pass
    _TREE_DOCS[src] = norm_doc_text("\n".join(parts))
    return _TREE_DOCS[src]


def quote_in_docs(src, quote):
    """Whether a quote of a module report is real text of the tree's documents (normalized, see norm_doc_text)."""
    q = norm_doc_text(quote)
    return len(q) >= 12 and q in tree_docs(src)


def report_dir(module, flavor, cfg):
    return os.path.join(REPORTS, module, "%s-%s" % (flavor, cfg))


def report_src(rep):
    s = rep.get("src") or rep.get("target_src")
    if s and not os.path.isabs(s):
        s = os.path.join(WT, s)
    return s


def rel_repro(rdir, path):
    if not path or not isinstance(path, str):
        return None
    return rel_stand(os.path.join(rdir, path))


def by_id(items, key):
    return next((x for x in items or [] if isinstance(x, dict) and x.get("id") == key), None)


def pick(statuses):
    for s in STATUSES:
        if s in statuses:
            return s
    return NA if NA in statuses else "not-run"


def dedupe(items):
    seen, out = set(), []
    for x in items:
        if x and x not in seen:
            seen.add(x)
            out.append(x)
    return out


def num(x):
    return x if isinstance(x, (int, float)) and not isinstance(x, bool) else None


# ----------------------------------------------------------------------------- module reports

class ModuleState:
    """One module report of one build (rel, dbg, or ci for alloc --ci-parity)."""

    def __init__(self, module, cfg):
        self.module = module
        self.cfg = cfg
        self.rep = None          # report.json when usable
        self.dir = None
        self.state = "missing"   # ok | exit | failed | stale | reused | mismatch | missing | not-selected | skipped
        self.note = ""
        self.exit = None
        self.seconds = None
        self.log = None
        self.generated = None
        self.mode = None
        self.items = []          # every finding, regression, question and documented item, normalized
        self.report_mtime = None

    def usable(self):
        return self.rep is not None

    def count(self, status):
        return len({i["signature"] for i in self.items if i["status"] == status})

    def to_json(self):
        sev = {}
        for i in self.items:
            if i["status"] == "defect":
                sev[i["severity"] or "?"] = sev.get(i["severity"] or "?", 0) + 1
        return {"module": self.module, "cfg": self.cfg, "state": self.state, "note": self.note, "exit": self.exit,
                "seconds": self.seconds, "log": rel_stand(self.log),
                "report": rel_stand(os.path.join(self.dir, "report.json")) if self.dir else None,
                "generated": self.generated, "mode": self.mode, "reportMtime": self.report_mtime,
                "baseline": module_baseline(self) if self.usable() else None,
                "counts": {s: self.count(s) for s in ITEM_STATUSES} if self.usable() else None,
                "defectsBySeverity": sev}


def exit_ok(module, code):
    return code == 0 or (module == "unity" and code == 1)


def read_steps(path, flavor, role=None):
    steps = {}
    if not path or not os.path.exists(path):
        return steps
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 8 or parts[0] != flavor:
                continue
            fl, rl, module, cfg, code, start, end, log = parts[:8]
            if role and rl != role:
                continue
            if not role and rl == "selftest":
                continue
            steps[(module, cfg)] = {"role": rl, "exit": int(code), "start": int(start), "end": int(end), "log": log}
    return steps


def load_state(module, cfg, flavor, src, steps, run_start, prev_runs=None):
    st = ModuleState(module, cfg)
    st.dir = report_dir(module, flavor, cfg)
    path = os.path.join(st.dir, "report.json")
    rep = load_json(path)
    if rep is not None:
        st.report_mtime = int(os.path.getmtime(path))
    step = steps.get((module, cfg))
    if step:
        st.exit = step["exit"]
        st.seconds = step["end"] - step["start"]
        st.log = step["log"]
        if rep is None:
            st.state, st.note = "failed", "the module wrote no report.json (exit %d)" % step["exit"]
            return st
        if os.path.getmtime(path) + 1 < step["start"]:
            st.state, st.note = "failed", "report.json was not updated (exit %d): left out of the summary" % step["exit"]
            return st
        if exit_ok(module, step["exit"]):
            st.state = "ok"
        else:
            st.state, st.note = "exit", "the module exited with %d, but its report is fresh" % step["exit"]
    else:
        if rep is None:
            st.state, st.note = "missing", "no report"
            return st
        st.state = "reused"
        st.note = "from an earlier run (%s)" % iso_of(os.path.getmtime(path))
        if run_start and os.path.getmtime(path) >= run_start:
            st.note = "written during this run"
        prev = (prev_runs or {}).get((module, cfg))
        if prev and prev.get("reportMtime") == st.report_mtime and prev.get("state") in ("ok", "exit", "reused"):
            # The same report as in the previous summary: keep how that run went.
            st.exit, st.seconds, st.log = prev.get("exit"), prev.get("seconds"), prev.get("log")
            if st.log and not os.path.isabs(st.log):
                st.log = os.path.join(STAND, st.log)
            st.note = prev.get("note") if prev.get("state") == "reused" else \
                "from the run of %s" % (prev.get("runStarted") or iso_of(os.path.getmtime(path)))
    rsrc = report_src(rep)
    if rsrc and realpath(rsrc) != realpath(src):
        st.state, st.note = "mismatch", "the report was made on another tree: %s" % rel_root(rsrc)
        return st
    if st.state == "reused":
        recorded = (rep.get("package") or {}).get("srcDigest") if module == "unity" else None
        if recorded:
            now = unity_digest(src)
            if now and recorded != now:
                st.state, st.note = "stale", "the package changed after the Unity run (digest %s -> %s): left out" % (recorded, now)
                return st
        else:
            latest, newest = tree_mtime(src)
            if latest > os.path.getmtime(path) + 1:
                st.state, st.note = "stale", "the tree changed after the report (%s, %s): left out" % (rel_root(newest), iso_of(latest))
                return st
    st.rep = rep
    st.generated = rep.get("generated") or rep.get("started") or rep.get("finished")
    st.mode = rep.get("mode")
    try:
        st.items = NORMALIZE[module](st, rep)
    except Exception as e:  # a report this code does not understand must not hide the others
        st.items = []
        st.note = (st.note + "; " if st.note else "") + "could not read the report: %s: %s" % (type(e).__name__, e)
    return st


# ----------------------------------------------------------------------------- normalization

def item(st, status, signature, **kw):
    d = {"module": st.module, "cfg": st.cfg, "status": status, "signature": signature, "id": kw.pop("id", None) or signature,
         "family": None, "hits": None, "magnitude": None, "severity": None, "title": None, "repro": None,
         "verified": None, "seed": None}
    d.update(kw)
    d["uid"] = "%s|%s" % (d["id"], d["signature"])
    return d


def norm_status(s):
    s = (s or "").lower()
    if s in ("defect", "finding", "found", "contract-violation"):
        return "defect"
    if s in ("regression",):
        return "regression"
    if s in ("question", "contract-question", "finding-question"):
        return "question"
    if s in ("documented",):
        return "documented"
    return None


def facts_of(signature):
    if not signature or "{" not in signature:
        return []
    inner = signature[signature.rfind("{") + 1:]
    inner = inner[: inner.find("}")] if "}" in inner else inner
    return [x.strip() for x in inner.split(",") if x.strip()]


def norm_fuzz(st, rep):
    rdir = st.dir
    details = {}
    for key in ("findings", "regressions"):
        for f in rep.get(key) or []:
            details[f.get("id")] = f
    for q in rep.get("questions") or []:
        if q.get("kind") == "finding-question":
            details[q.get("id")] = q
    fam_title = {f.get("family"): f for f in rep.get("families") or []}
    out = []
    seen = set()
    for s in rep.get("signatures") or []:
        status = norm_status(s.get("status"))
        if not status:
            continue
        f = details.get(s.get("finding")) or {}
        fam = fam_title.get(s.get("family")) or {}
        out.append(item(st, status, s.get("signature"), id=s.get("finding") or s.get("signature"), family=s.get("family"),
                        hits=s.get("count"), rate=s.get("per_1000"), severity=f.get("severity") or fam.get("severity"),
                        title=f.get("title") or fam.get("title") or s.get("rule"), rule=s.get("rule"),
                        repro=rel_repro(rdir, f.get("repro")), seed=(s.get("seeds") or [None])[0],
                        verified=(f.get("verified") == "REPRODUCED") if f.get("verified") else None,
                        facts=facts_of(s.get("signature"))))
        seen.add(s.get("signature"))
    # Items whose signature is not in the signature list (older reports): from the lists themselves.
    for key, status in (("findings", "defect"), ("regressions", "regression")):
        for f in rep.get(key) or []:
            sig = f.get("signature") or f.get("id")
            if sig in seen or any(o in seen for o in f.get("origin_signatures") or []):
                continue
            out.append(item(st, status, sig, id=f.get("id"), family=f.get("family"), hits=f.get("count"),
                            severity=f.get("severity"), title=f.get("title"), rule=f.get("rule"),
                            repro=rel_repro(rdir, f.get("repro")), facts=f.get("mechanism") or facts_of(sig),
                            verified=(f.get("verified") == "REPRODUCED") if f.get("verified") else None))
    # Contract questions the model met on the way (mirrors): one item per question id.
    for q in rep.get("questions") or []:
        if q.get("kind") == "finding-question":
            continue
        out.append(item(st, "question", q.get("id"), hits=q.get("count"), title=q.get("text") or q.get("title"),
                        severity="question"))
    # Behaviour the model met that the documents of the tree describe, with the sentence found there.
    for d in rep.get("documented") or []:
        out.append(item(st, "documented", d.get("id"), hits=d.get("count"), title=d.get("text") or d.get("title"),
                        severity="info", quote=d.get("quote"), quoteSource=d.get("source")))
    return out


def quote_of_contract(text):
    """contracts: the quoted sentence of a "contract" field ('<where>: "sentence" (...)' or '<where>: sentence')."""
    if not text:
        return None
    m = re.search(r'"([^"]{12,})"', text)
    if m:
        return m.group(1)
    if ": " in text:
        return text.split(": ", 1)[1]
    return None


def norm_contracts(st, rep):
    rdir = st.dir
    hits = {s.get("signature"): s.get("hits") for s in rep.get("signatures") or []}
    out = []
    for key, status in (("findings", "defect"), ("regressions", "regression"), ("contractQuestions", "question"),
                        ("documented", "documented")):
        for f in rep.get(key) or []:
            if key == "findings" and f.get("kind", "defect") != "defect":
                continue
            sig = f.get("signature") or f.get("id")
            out.append(item(st, status, sig, id=f.get("id"), family=f.get("known"), hits=hits.get(sig, f.get("hits")),
                            severity=f.get("severity"), title=f.get("title"), rule=f.get("rule"),
                            repro=rel_repro(rdir, f.get("repro")), seed=f.get("seed"),
                            verified=(f.get("reproStatus") == "verified") if f.get("reproStatus") else None,
                            quote=f.get("quote") or (quote_of_contract(f.get("contract")) if key == "documented" else None),
                            quoteSource=f.get("quoteSource")))
    return out


def alloc_verified(f):
    """alloc: True when every runtime run of the item's repro reproduced the stand's number."""
    rc = f.get("repro_check") or {}
    if not isinstance(rc, dict) or not rc:
        return None
    return all(str((v or {}).get("status", "")).startswith(("reproduces", "n/a"))
               for v in rc.values() if isinstance(v, dict))


def norm_alloc(st, rep):
    rdir = st.dir
    out = []
    if st.cfg == "ci":
        # Red CI tests: findings (a cause in the library or a failing test) and test_artifacts (the assert of the test
        # allocates, not the library: catalog entry A5). A red CI is a defect of the package either way: of its test
        # suite for an artifact.
        for key in ("findings", "test_artifacts"):
            for f in rep.get(key) or []:
                msg = (f.get("message") or "").splitlines()
                cause = f.get("cause_class") or ("test-measurement-artifact" if key == "test_artifacts" else None)
                title = "%s: %s" % (f.get("test"), msg[-1].strip() if msg else "")
                if key == "test_artifacts":
                    title += " (test artifact: the assert allocates, not the library)"
                elif cause and cause != "test-measurement-artifact":
                    title += " (%s: %s)" % (cause, f.get("cause") or "")
                out.append(item(st, "defect", f.get("signature") or f.get("id"), id=f.get("id"), family=cause,
                                severity=f.get("severity"), title=title, repro=rel_repro(rdir, f.get("repro")),
                                cause_class=cause))
        return out
    lists = [("findings", "defect"), ("regressions", "regression"), ("contract_questions", "question"),
             ("documented", "documented")]
    for key, status in lists:
        for f in rep.get(key) or []:
            if key == "findings" and f.get("class") == "regression":
                status_here = "regression"   # reports of format 1 kept the regressions among the findings
            else:
                status_here = status
            quote = f.get("contract_quote") or ((f.get("contract_basis") or {}).get("quote"))
            if not quote and status_here == "documented":
                quote = " | ".join(dict.fromkeys(p.get("quote") for acc in (f.get("documented_cost") or {}).values()
                                                 for p in acc.get("explained") or [] if p.get("quote"))) or None
            out.append(item(st, status_here, f.get("signature") or f.get("id"), id=f.get("id"), severity=f.get("severity"),
                            title=f.get("title"), magnitude=num(f.get("magnitude")), repro=rel_repro(rdir, f.get("repro")),
                            verified=alloc_verified(f), scenario=f.get("scenario"), metric=f.get("metric"),
                            cause_keys=f.get("cause_keys") or [], quote=quote if status_here == "documented" else None))
    return out


def norm_runner(st, rep):
    rdir = st.dir
    out = []
    for key, default in (("findings", "defect"), ("regressions", "regression"), ("contractQuestions", "question")):
        for f in rep.get(key) or []:
            status = norm_status(f.get("status")) or default
            sig = f.get("signature") or f.get("id")
            out.append(item(st, status, sig, id=f.get("id"), family=f.get("known"),
                            hits=f.get("hits"), magnitude=num(f.get("magnitude")), severity=f.get("severity"),
                            title=f.get("title"), repro=rel_repro(rdir, f.get("repro")), seed=f.get("seed"),
                            verified=f.get("reproVerified")))
    return out


def norm_generator(st, rep):
    rdir = st.dir
    out = []
    for key, status in (("findings", "defect"), ("regressions", "regression"), ("questions", "question"), ("documented", "documented")):
        for f in rep.get(key) or []:
            verified = str(f.get("reproStatus") or "").startswith("verified") if f.get("reproStatus") else None
            sigs = f.get("signatures") or [{"text": f.get("signature") or f.get("key") or f.get("id")}]
            for s in sigs:
                hits = (s.get("catalogHits") or 0) + (s.get("fuzzHits") or 0) if ("catalogHits" in s or "fuzzHits" in s) else None
                out.append(item(st, status, s.get("text"), id=f.get("id"), family=f.get("known"), hits=hits,
                                severity=f.get("severity"), title=f.get("title"), rule=f.get("rule"),
                                repro=rel_repro(rdir, f.get("repro")), seed=f.get("seed"), verified=verified,
                                quote=f.get("quote") if status == "documented" else None, quoteSource=f.get("quoteSource")))
    return out


def norm_unity(st, rep):
    rdir = st.dir
    hits = {s.get("signature"): s.get("hits") for s in rep.get("signatures") or []}
    out = []
    for key, status in (("findings", "defect"), ("regressions", "regression"), ("contractQuestions", "question")):
        for f in rep.get(key) or []:
            sig = f.get("signature") or f.get("id") or "%s:%s" % (f.get("case"), f.get("kind"))
            rv = f.get("reproVerified")
            cases = f.get("cases") or ([f["case"]] if isinstance(f.get("case"), str) else [])
            out.append(item(st, status, sig, id=f.get("id") or sig, family=f.get("family") or ",".join(f.get("known") or []) or None,
                            hits=hits.get(sig) or (len(cases) or None), severity=f.get("severity"),
                            title=f.get("ruleTitle") or f.get("title") or f.get("detail"), rule=f.get("rule"),
                            repro=rel_repro(rdir, f.get("repro")), cases=cases,
                            verified=rv.get("reproduced") if isinstance(rv, dict) else None))
    return out


NORMALIZE = {"fuzz-core": norm_fuzz, "contracts": norm_contracts, "alloc": norm_alloc, "runner": norm_runner,
             "generator": norm_generator, "unity": norm_unity}


def module_baseline(st):
    """The tree this module report compared itself with (its own --baseline), if any."""
    rep = st.rep or {}
    if st.module == "unity":
        bc = rep.get("baselineComparison")
        return (bc.get("label") or bc.get("flavor")) if isinstance(bc, dict) else None
    b = rep.get("baseline")
    if isinstance(b, str):
        return b
    if isinstance(b, dict):
        return b.get("flavor")
    return None


def comparability(st):
    """What must be equal on both sides for hits of two reports to be compared."""
    rep = st.rep or {}
    m = st.module
    if m == "fuzz-core":
        fixed = rep.get("scenarios_planned")
        return {"mode": rep.get("mode"), "baseSeed": rep.get("base_seed"), "scenarios": fixed,
                "ops": rep.get("ops_per_scenario"), "excluded": sorted(rep.get("exclude_families") or []),
                "fixedSeeds": bool(fixed) and not rep.get("partial")}
    if m == "contracts":
        return {"mode": rep.get("mode"), "seed": rep.get("seed")}
    if m == "runner":
        return {"mode": rep.get("mode"), "baseSeed": rep.get("baseSeed"), "cases": rep.get("randomCases")}
    if m == "generator":
        return {"mode": rep.get("mode"), "seed": rep.get("seed"), "iterations": rep.get("iterations")}
    if m == "unity":
        return {"mode": rep.get("mode"), "target": rep.get("target")}
    return {"mode": rep.get("mode")}


def comparable(a, b):
    if not a or not b:
        return False, "no parameters"
    if "fixedSeeds" in a and not (a.get("fixedSeeds") and b.get("fixedSeeds")):
        return False, "not a fixed seed set on both sides"
    diff = [k for k in sorted(set(a) | set(b)) if a.get(k) != b.get(k)]
    if diff:
        return False, "; ".join("%s %s vs %s" % (k, b.get(k), a.get(k)) for k in diff)
    return True, ""


def promises(st):
    """The ids of the promises (claims, rules) this report checked as active, per module."""
    rep = st.rep or {}
    if st.module == "contracts":
        return sorted(c.get("id") for c in rep.get("claims") or [] if c.get("state") == "active")
    if st.module == "generator":
        return sorted(c.get("id") for c in rep.get("claims") or [] if c.get("active"))
    if st.module == "runner":
        return sorted(r.get("rule") for r in (rep.get("contractProfile") or {}).get("rules") or [] if r.get("status") == "defect")
    if st.module == "alloc" and st.cfg != "ci":
        return sorted(p.get("id") for p in (rep.get("contract_profile") or {}).get("promises") or [] if p.get("applies"))
    return None


def warnings_of(st):
    """What the module itself says may make its result incomplete."""
    rep = st.rep or {}
    out = []

    def add(label, items):
        items = [x for x in items or [] if x]
        if items:
            out.append("%s: %s" % (label, "; ".join(short(json.dumps(x, ensure_ascii=False) if isinstance(x, (dict, list)) else x, 160) for x in items[:3])
                                   + (" (+%d)" % (len(items) - 3) if len(items) > 3 else "")))

    m = st.module
    if m == "contracts":
        add("claim warnings", rep.get("claimWarnings"))
        add("stand errors", rep.get("standErrors"))
        add("unstable cells", rep.get("unstable"))
        add("not run", rep.get("notRun"))
    elif m == "alloc":
        add("invalid measurements", rep.get("invalid"))
        add("stale rules", (rep.get("contract_profile") or {}).get("stale_rules"))
        add("dropped promises", [p.get("id") for p in (rep.get("promise_drift") or {}).get("dropped") or []])
    elif m == "fuzz-core":
        add("harness errors", rep.get("harness_errors"))
        add("documented sentences not found", rep.get("stand_warnings"))
        if rep.get("verify_error"):
            out.append("repro check: %s" % short(rep.get("verify_error"), 160))
        if rep.get("partial"):
            out.append("partial run (stopped before the planned scenarios)")
    elif m == "generator":
        add("inconclusive", rep.get("inconclusive"))
    elif m == "unity":
        rv = rep.get("reproVerification") or {}
        if rv.get("planned") and rv.get("reproduced") != rv.get("planned"):
            out.append("repros reproduced: %s of %s" % (rv.get("reproduced"), rv.get("planned")))
        add("skipped cases", rep.get("skipped"))
    unverified = [i["id"] for i in st.items if i["status"] == "defect" and i.get("verified") is False]
    if unverified:
        out.append("defects whose repro did not reproduce: %s" % ", ".join(dedupe(unverified)[:5]))
    return out


def module_comparison(st):
    """The comparison a module made itself against its --baseline (numbers only)."""
    rep = st.rep or {}
    m = st.module
    b = rep.get("baseline")
    if m == "fuzz-core" and isinstance(b, dict):
        seeds = lambda groups: sum((g.get("count") or 1) if isinstance(g, dict) else 1 for g in groups or [])
        return {"baseline": b.get("flavor"), "seedsCompared": b.get("seeds_compared") if b.get("seed_comparable") else None,
                "newlyFailingSeeds": seeds(b.get("seeds_newly_failing")),
                "seedsFailingDifferently": seeds(b.get("seeds_failing_differently")),
                "fixedSeeds": b.get("seeds_fixed"), "newSignatures": len(b.get("new_signatures") or []),
                "newInKnownFamilies": len(b.get("new_signatures_in_known_families") or []),
                "growth": len(b.get("growth") or []), "gone": len(b.get("gone_signatures") or [])}
    if m == "contracts" and isinstance(b, dict):
        sig = b.get("signatures") or {}
        return {"baseline": b.get("flavor"), "changedCells": b.get("changed"), "addedCells": b.get("added"),
                "removedCells": b.get("removed"), "regressions": b.get("regressions"),
                "newSignatures": len(sig.get("new") or []), "gone": len(sig.get("gone") or []),
                "growth": len(sig.get("grown") or sig.get("growth") or [])}
    if m == "runner" and isinstance(b, dict):
        return {"baseline": b.get("flavor"), "newSignatures": len(b.get("newSignatures") or []),
                "growth": len(b.get("grownSignatures") or []), "statusChanged": len(b.get("statusChanged") or []),
                "gone": len(b.get("goneSignatures") or [])}
    if m == "generator" and isinstance(b, dict):
        return {"baseline": b.get("flavor"), "newSignatures": len(b.get("newSignatures") or []),
                "growth": len(b.get("spikes") or []), "gone": len(b.get("goneSignatures") or []),
                "newQuestions": len(b.get("newQuestions") or []), "programsChanged": b.get("programsChanged")}
    if m == "unity" and isinstance(rep.get("baselineComparison"), dict):
        bc = rep["baselineComparison"]
        return {"baseline": bc.get("label") or bc.get("flavor") or bc.get("baseline"),
                **{k: (len(v) if isinstance(v, list) else v) for k, v in bc.items()
                   if k not in ("flavor", "baseline", "label", "report", "error")}}
    if m == "alloc" and isinstance(b, str):
        return {"baseline": b, "regressions": len(rep.get("regressions") or []),
                "droppedPromises": len((rep.get("promise_drift") or {}).get("dropped") or [])}
    return None


# ----------------------------------------------------------------------------- matchers

class Result:
    def __init__(self, status, evidence="", claims=None, repro=None, corpus=None):
        self.status = status
        self.evidence = evidence
        self.claims = list(claims or [])      # uids of the items this entry claims
        self.repro = [r for r in (repro or []) if r]
        self.corpus = [c for c in (corpus or []) if c]


def best(items):
    return pick([i["status"] for i in items]) if items else None


def repros(items):
    return dedupe([i["repro"] for i in sorted(items, key=lambda i: ITEM_STATUSES.index(i["status"])) if i.get("repro")])


def describe(items):
    by = {}
    for i in items:
        by.setdefault(i["status"], set()).add(i["signature"])
    hits = sum(i["hits"] for i in items if num(i.get("hits")) is not None)
    text = ", ".join("%s %d" % (s, len(by[s])) for s in ITEM_STATUSES if s in by)
    return "signatures: %s%s" % (text or "0", ("; hits %d" % hits) if hits else "")


def cases_ran(rep, wanted):
    """unity: the listed cases that ran in this report (None when the entry names no cases)."""
    if not wanted:
        return None
    return [c["id"] for c in rep.get("cases") or [] if c.get("id") in wanted and c.get("verdict") not in (None, "na", "missing", "skipped")]


def m_family(st, m):
    """fuzz-core: a defect family (its signatures and its corpus replays)."""
    rep, key = st.rep, m["key"]
    items = [i for i in st.items if i.get("family") == key]
    corpus = [c for c in rep.get("corpus") or [] if c.get("family") == key]
    refs = ["fuzz-core/" + c["file"] for c in corpus if c.get("file")]
    repro_ok = [c for c in corpus if c.get("status") == "reproduces"]
    ev = "%s; corpus reproduces %d of %d" % (describe(items), len(repro_ok), len(corpus))
    status = best(items)
    if status is None and repro_ok:
        status = "defect" if any(c.get("expect", "defect") == "defect" for c in repro_ok) else "question"
    if status:
        return Result(status, ev, [i["uid"] for i in items], repros(items), refs)
    excluded = rep.get("exclude_families") or []
    if key in excluded or "all" in excluded:
        return Result("not-run", "family excluded from the generator (--exclude-families)", corpus=refs)
    if corpus and all(c.get("status") == "skipped" for c in corpus):
        return Result(NA, "the corpus of %s is skipped in this build" % key, corpus=refs)
    return Result("absent", ev, corpus=refs)


def m_facts(st, m):
    """fuzz-core: signatures by mechanism facts (a mechanism that has no family of its own yet)."""
    want = set(m.get("facts") or [])
    fams = set(m.get("families") or [])
    items = [i for i in st.items if want & set(i.get("facts") or []) and (not fams or i.get("family") in fams)]
    if items:
        return Result(best(items), "%s; facts %s" % (describe(items), ", ".join(sorted(want))), [i["uid"] for i in items], repros(items))
    return Result("absent", "no signature with the facts %s" % ", ".join(sorted(want)))


def m_known(st, m):
    """contracts, runner, generator, unity: the module's own record of a known problem."""
    module, rep, key = st.module, st.rep, m["key"]
    if module == "fuzz-core":
        return m_family(st, m)
    if module == "unity":
        k = (rep.get("known") or {}).get(key)
        items = [i for i in st.items if i.get("family") == key or (k and i["id"] in (k.get("found") or []))]
        ran = cases_ran(rep, m.get("cases"))
        refs = ["unity: case " + c for c in (ran or [])[:4]]
        if items:
            return Result(best(items), "%s; cases %s" % (", ".join(dedupe(i["id"] for i in items)),
                                                        ", ".join(dedupe(c for i in items for c in i.get("cases") or [])) or "-"),
                          [i["uid"] for i in items], repros(items), refs)
        if ran is not None and not ran:
            return Result("not-run", "cases %s did not run" % ", ".join(m.get("cases") or []))
        if k is None and ran is None:
            return Result("not-run", "the report does not know %s" % key)
        return Result("absent", "cases pass: %s" % ", ".join(ran or []), corpus=refs)
    k = by_id(rep.get("known"), key)
    if not k:
        return Result("not-run", "the report does not know %s" % key)
    raw = str(k.get("status") or "")
    if module == "runner" and not raw:
        raw = "defect" if k.get("found") else "absent"
    if module == "generator" and not raw:
        raw = "found" if k.get("detected") else "not-found"
    mapped = norm_status(raw) or {"not-found": "absent", "absent": "absent", "pass": "absent", "ok": "absent",
                                  "not-applicable": NA, "n/a": NA, "not-checked": "not-run"}.get(raw, "not-run")
    ids = set()
    for key2 in ("findings", "questions", "documented", "regressions"):
        ids |= set(x for x in k.get(key2) or [] if isinstance(x, str))
    if k.get("finding"):
        ids.add(k["finding"])
    items = [i for i in st.items if i.get("family") == key or i["id"] in ids]
    if mapped in ITEM_STATUSES:
        # The module's verdict decides; the items it lists are claimed whatever their own status.
        claimed = [i for i in items if i["status"] == mapped] or items
        ev = k.get("note") or k.get("detail") or describe(claimed)
        corpus = []
        if module == "runner":
            corpus = ["runner: " + t.strip() for t in (k.get("detail") or "").replace(";", ",").split(",") if t.strip().startswith("scenario:")][:3]
        return Result(mapped, short(ev, 300), [i["uid"] for i in items], repros(claimed), corpus)
    plain = {"absent": "not reproduced", NA: "cannot occur in this build", "not-run": "not checked"}.get(mapped, raw)
    return Result(mapped, short(k.get("note") or k.get("detail") or plain, 300))


def m_question(st, m):
    key = m["key"]
    if st.module == "unity":
        ran = cases_ran(st.rep, m.get("cases"))
        if ran is not None and not ran:
            return Result("not-run", "cases %s did not run" % ", ".join(m.get("cases") or []))
    items = [i for i in st.items if i["status"] == "question" and (str(i["id"]).startswith(key) or str(i["signature"]).startswith(key))]
    corpus = []
    if st.module == "fuzz-core":
        corpus = ["fuzz-core/" + c["file"] for c in st.rep.get("corpus") or [] if c.get("question") == key and c.get("file")]
    if items:
        return Result("question", short(items[0]["title"], 200), [i["uid"] for i in items], repros(items), corpus)
    return Result("absent", "no question %s" % key, corpus=corpus)


def m_item(st, m):
    key = m["key"]
    pref = m.get("prefix")
    if st.module == "unity":
        ran = cases_ran(st.rep, m.get("cases"))
        if ran is not None and not ran:
            return Result("not-run", "cases %s did not run" % ", ".join(m.get("cases") or []))
    items = [i for i in st.items if i["id"] == key or i["signature"] == key or
             (pref and (str(i["id"]).startswith(key) or str(i["signature"]).startswith(key)))]
    if items:
        return Result(best(items), short(items[0]["title"], 200), [i["uid"] for i in items], repros(items))
    return Result("absent", "nothing matches %s" % key)


def alloc_classify(st, m):
    """An alloc entry is a place (its scenarios) and a mechanism (the causes an experiment established). An item
    matches when its scenario is listed, its metric too (when the entry names metrics) and, when the entry names
    causes, its established cause is one of them or none was established: a new mechanism in a known place, or a
    known mechanism in a new place, stays outside the entry."""
    scen = list(m.get("scenarios") or [])
    causes = set(m.get("causes") or [])
    metrics = set(m.get("metrics") or [])

    def hit(x):
        if x.get("scenario") not in scen:
            return False
        if metrics and x.get("metric") not in metrics:
            return False
        got = set(x.get("cause_keys") or [])
        return not causes or not got or bool(causes & got)

    items = sorted([i for i in st.items if hit(i)], key=lambda i: scen.index(i["scenario"]))
    if not items:
        return None
    status = best(items)
    top = [i for i in items if i["status"] == status]
    ev = "%s: %s" % (status, ", ".join(dedupe(i["scenario"] for i in top)[:4]) + (" …" if len(top) > 4 else ""))
    if status == "regression":
        ev += " (against %s)" % (module_baseline(st) or "the baseline")
    return Result(status, ev, [i["uid"] for i in items], repros(top))


# alloc canary statuses.
ALLOC_WATCH = {"present": "present", "absent": "absent", "n/a": NA, "not in this version": "no-api"}


def m_alloc_watch(st, m):
    w = by_id(st.rep.get("watchlist"), m["key"])
    cls = alloc_classify(st, m)
    refs = ["alloc: scenario " + s for s in m.get("scenarios") or []]
    if w is None:
        if cls:
            cls.corpus = refs
            return cls
        return Result("not-run", "the report has no %s" % m["key"], corpus=refs)
    kind, ev = ALLOC_WATCH.get(w.get("status")), w.get("evidence") or ""
    if kind == "present":
        r = cls or Result("question", "present, not classified by the module")
        r.evidence = (r.evidence + "; " if r.evidence else "") + short(ev, 220)
        r.corpus = refs
        return r
    if kind == "absent":
        return Result("absent", short(ev, 220), corpus=refs)
    if kind == NA:
        return Result(NA, short(ev, 220), corpus=refs)
    if kind == "no-api":
        return Result(NA, "the API is not in this version: " + short(ev, 200), corpus=refs)
    return Result("not-run", "%s: %s" % (w.get("status"), short(ev, 200)), corpus=refs)


def m_alloc_scenario(st, m):
    cls = alloc_classify(st, m)
    refs = ["alloc: scenario " + s for s in m.get("scenarios") or []]
    if cls:
        cls.corpus = refs
        return cls
    ran = {x.get("scenario") for x in st.rep.get("diff") or []}
    if ran & set(m.get("scenarios") or []):
        return Result("absent", "the scenario allocates nothing beyond the expected", corpus=refs)
    return Result("not-run", "the scenarios did not run", corpus=refs)


def m_alloc_ci(st, m):
    # A5 is the assert artifact only (the box of the expected 0 inside the measuring window, confirmed by the
    # diagnostic copy with Is.Zero). Any other red CI test is not A5: it stays outside the catalog with its cause.
    want = m.get("cause", "test-measurement-artifact")
    fs = [i for i in st.items if i.get("cause_class") == want]
    others = [i for i in st.items if i not in fs]
    runs = st.rep.get("runs") or {}
    summ = ", ".join("%s %s/%s" % (k, (v.get("summary") or {}).get("passed"), (v.get("summary") or {}).get("total"))
                     for k, v in runs.items() if k.startswith("ci-"))
    rest = ("; other red tests: %d" % len(others)) if others else ""
    if fs:
        names = sorted({str(i["title"]).split(":", 1)[0] for i in fs})
        return Result("defect", "assert artifact in CI: %d (%s): %s%s" % (len(names), summ, ", ".join(names[:3]) + (" …" if len(names) > 3 else ""), rest),
                      [i["uid"] for i in fs], [rel_stand(os.path.join(st.dir, "report.md"))], ["alloc: --ci-parity"])
    if runs:
        return Result("absent", "no assert artifact: %s%s" % (summ, rest), corpus=["alloc: --ci-parity"])
    return Result("not-run", "no CI runs")


MATCHERS = {"known": m_known, "family": m_family, "facts": m_facts, "question": m_question, "item": m_item,
            "finding": m_item, "alloc-watch": m_alloc_watch, "alloc-scenario": m_alloc_scenario, "alloc-ci": m_alloc_ci}


# ----------------------------------------------------------------------------- evaluation

def evaluate(catalog, states, configs, selected, notes):
    """Status of every catalog entry per build, plus {(module, build, item uid): entry id} of the items it claims."""
    claims = {}
    entries = []
    for e in catalog["entries"]:
        per_cfg = {}
        all_repro, all_corpus, all_ids = [], [], []
        builds = list(configs) + (["ci"] if any(mm["kind"] == "alloc-ci" for mm in e["match"]) else [])
        for cfg in builds:
            results = []
            for mm in e["match"]:
                if (mm["kind"] == "alloc-ci") != (cfg == "ci"):
                    continue
                module = mm["module"]
                if module not in selected:
                    results.append((module, Result("not-run", "module %s not selected" % module)))
                    continue
                st = states.get((module, cfg))
                if st is None or not st.usable():
                    reason = (st.note if st else "") or "no report"
                    if st and st.state in ("failed", "stale", "mismatch"):
                        reason = "module %s: %s" % (st.state, reason)
                    if module == "unity" and notes.get("unity") and (st is None or st.state == "missing"):
                        reason = "unity skipped (%s)" % notes["unity"]
                    results.append((module, Result("not-run", reason)))
                    continue
                r = MATCHERS[mm["kind"]](st, mm)
                for uid in r.claims:
                    claims.setdefault((module, cfg, uid), e["id"])
                claimed = set(r.claims)
                # The sentences a "documented" status rests on: the quotes of the documented items the entry claims.
                r.quotes = dedupe(i.get("quote") for i in st.items if i["uid"] in claimed and i["status"] == "documented")
                results.append((module, r))
            if not results:
                continue
            status = pick([r.status for _, r in results])
            inside = cfg in e.get("configs", ["rel", "dbg"])
            note = ""
            if not inside:
                if status in ITEM_STATUSES:
                    note = "also outside the builds where it is expected"
                else:
                    status = NA
                    note = "occurs only in %s" % ", ".join(e.get("configs", []))
            # The evidence of the matchers that decided the status (all of them when none did).
            deciding = [(mod, r) for mod, r in results if r.status == status] or results
            ev = "; ".join("%s: %s" % (mod, r.evidence) for mod, r in deciding if r.evidence) if len(deciding) > 1 \
                else (deciding[0][1].evidence or "")
            if note:
                ev = note + (": " + ev if ev and status in ITEM_STATUSES else "")
            per_cfg[cfg] = {"status": status, "evidence": ev}
            if status == "documented":
                per_cfg[cfg]["quotes"] = dedupe(q for _, r in deciding for q in getattr(r, "quotes", []))
            for mod, r in results:
                if status in ITEM_STATUSES and r.status == status:
                    all_repro += r.repro
                    all_ids += ["%s/%s:%s" % (mod, cfg, c.split("|", 1)[0]) for c in r.claims]
                all_corpus += r.corpus
        considered = [v["status"] for v in per_cfg.values() if v["status"] != NA]
        overall = pick(considered) if considered else "not-run"
        entries.append({
            "id": e["id"], "group": e["group"], "source": e.get("source"), "module": e.get("module"),
            "severity": e.get("severity"), "title": e["title"], "example": e.get("example"), "note": e.get("note"),
            "fixedIn": e.get("fixedIn"), "configs": e.get("configs"), "status": overall,
            "builds": per_cfg, "findings": dedupe(all_ids), "repro": dedupe(all_repro), "corpus": dedupe(all_corpus),
        })
    return entries, claims


def version_tuple(v):
    core = re.split(r"[-+]", v or "")[0].split(".")
    out = []
    for i in range(3):
        try:
            out.append(int(core[i]))
        except (IndexError, ValueError):
            out.append(0)
    return tuple(out)


MUST_CATCH_RULE = ("caught means not absent and not not-run: defect, regression, question and documented all count, a "
                   "documented status only with a quote that is real text of this tree's documents (its XML docs, docs/, "
                   "CHANGELOG.md, README.md)")


def must_catch(catalog, entries, src):
    """The release check of the catalog: the entries marked "mustCatchOn": <version> (the problems found on that
    release by the game project and the Windows review) must all be caught on a tree of that version (see
    MUST_CATCH_RULE). None when the tree is of no such version."""
    version = pkg_version(src)
    wanted = [e for e in catalog["entries"] if e.get("mustCatchOn") and version
              and version_tuple(e["mustCatchOn"]) == version_tuple(version)]
    if not wanted:
        return None
    by = {e["id"]: e for e in entries}
    rows = []
    for e in wanted:
        ent = by.get(e["id"]) or {"status": "not-run", "builds": {}}
        st = ent["status"]
        caught = st in ITEM_STATUSES
        why = st
        if st == "documented":
            quotes = dedupe(q for b in ent["builds"].values() if b["status"] == "documented" for q in b.get("quotes") or [])
            real = [q for q in quotes if quote_in_docs(src, q)]
            caught = bool(real)
            why = ('documented, quoting "%s"' % short(real[0], 160)) if real else (
                "documented, but no quote of the module is real text of this tree's documents%s" % (
                    (" (quoted: \"%s\")" % short(quotes[0], 120)) if quotes else " (the module gave no quote)"))
        rows.append({"id": e["id"], "status": st, "caught": caught, "why": why})
    return {"release": wanted[0]["mustCatchOn"], "rule": MUST_CATCH_RULE, "total": len(rows),
            "caught": sum(1 for r in rows if r["caught"]), "missed": [r["id"] for r in rows if not r["caught"]], "rows": rows}


def status_text(entry):
    """"defect: rel, dbg" / "question: dbg (rel n/a)" / "absent"."""
    if not entry:
        return "—"
    b = entry["builds"]
    ov = entry["status"]
    same = [c for c, v in b.items() if v["status"] == ov]
    other = ["%s %s" % (c, v["status"]) for c, v in b.items() if v["status"] != ov]
    if not same:
        return ov + (" (%s)" % ", ".join(other) if other else "")
    text = "%s: %s" % (ov, ", ".join(same))
    if other:
        text += " (%s)" % ", ".join(other)
    return text


def signature_rows(states, claims):
    """One row per (module, build, signature): its strongest status, hits, family and catalog entry."""
    rows = {}
    for (module, cfg), st in states.items():
        for i in st.items:
            k = (module, cfg, i["signature"])
            r = rows.get(k)
            cat = claims.get((module, cfg, i["uid"]))
            if r is None:
                rows[k] = r = {"module": module, "cfg": cfg, "signature": i["signature"], "status": i["status"],
                               "timing": module == "fuzz-core" and str(i["signature"]).startswith("C-"),
                               "family": i.get("family"), "catalog": cat, "hits": i.get("hits"),
                               "magnitude": i.get("magnitude"), "rate": i.get("rate"), "id": i["id"],
                               "title": i.get("title"), "severity": i.get("severity"), "repro": i.get("repro"),
                               "verified": i.get("verified")}
                continue
            if ITEM_STATUSES.index(i["status"]) < ITEM_STATUSES.index(r["status"]):
                r["status"], r["id"], r["title"] = i["status"], i["id"], i.get("title")
            r["catalog"] = r["catalog"] or cat
            r["family"] = r["family"] or i.get("family")
            r["repro"] = r["repro"] or i.get("repro")
            if num(i.get("hits")) is not None and num(r.get("hits")) is not None and module not in ("contracts", "unity"):
                r["hits"] = max(r["hits"], i["hits"])
            if num(i.get("magnitude")) is not None and (r.get("magnitude") is None or i["magnitude"] > r["magnitude"]):
                r["magnitude"] = i["magnitude"]
    return sorted(rows.values(), key=lambda r: (MODULES.index(r["module"]), CFG_RANK.get(r["cfg"], 9),
                                                ITEM_STATUSES.index(r["status"]), str(r["signature"])))


def merge_rows(rows, status):
    """Rows of one status merged over builds by (module, signature)."""
    merged = {}
    for r in rows:
        if r["status"] != status:
            continue
        k = (r["module"], r["signature"])
        m = merged.get(k)
        if m is None:
            m = dict(r)
            m.pop("cfg")
            m["configs"] = [r["cfg"]]
            merged[k] = m
        else:
            m["configs"].append(r["cfg"])
            m["catalog"] = m["catalog"] or r["catalog"]
            m["repro"] = m["repro"] or r["repro"]
            if SEV_RANK.get(r["severity"], 9) < SEV_RANK.get(m["severity"], 9):
                m["severity"] = r["severity"]
    return list(merged.values())


# ----------------------------------------------------------------------------- API coverage

# The types whose public members the coverage table lists, the files that declare them (extension methods with
# `this World` count for World) and the classes whose members belong to them.
API_TYPES = {
    "World": (("World.cs", "WorldListenerExtensions.cs"), ("World",)),
    "ComponentPool": (("ComponentPool.cs", "ComponentPoolBase.cs"), ("ComponentPool", "ComponentPoolBase")),
    "Filter": (("Filter.cs",), ("Filter",)),
    "Group": (("Group.cs",), ("Group",)),
}
# How the stand's code usually names a receiver of each type (a static scan: approximate by design).
API_RECEIVERS = {
    "World": r"(?:\b(?:w|W|world|World|_w|_world|wld)|\bs\.W|\bctx\.World|\bWorld\(\))",
    "ComponentPool": r"(?:\b(?:pool|p|_pool|pa|pb|poolA|poolB|basePool)|\bPool<[^<>]*>\(\)|\bpools\[[^\]]*\])",
    "Filter": r"(?:\b(?:filter|f|_filter|flt|filt|fa|fb)|\bEnd\(\))",
    "Group": r"(?:\b(?:group|g|_group|grp)|\bGroup<[^<>]*>\(\)|\bGetGroup\([^()]*\))",
}
API_SCANNED = {"fuzz-core": ("fuzz-core", "src"), "alloc": ("alloc", "Probe")}


def _strip_code(text):
    """C# without comments and string literals (their braces would confuse the brace count)."""
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r'@"(?:[^"]|"")*"', '""', text)
    text = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', text)
    return re.sub(r"'(?:\\.|[^'\\\n])'", "''", text)


def public_members(src):
    """{type: {member: kind}} of the public members of API_TYPES in the package sources (overloads merged)."""
    out = {t: {} for t in API_TYPES}
    for t, (files, classes) in API_TYPES.items():
        for name in files:
            path = os.path.join(src, "Core", name)
            try:
                with open(path, encoding="utf-8") as f:
                    code = _strip_code(f.read())
            except OSError:
                continue
            depth, stack, pending = 0, [], None
            for line in code.splitlines():
                head = line.strip()
                m = re.match(r"(?:\[[^\]]*\]\s*)*public\s+(?:(?:static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*(class|struct|interface|enum)\s+(\w+)", head)
                if m:
                    pending = m.group(2)
                body = stack[-1] if stack else None
                if body and depth == body[1] and not m:
                    decl = re.sub(r"^(?:\[[^\]]*\]\s*)+", "", head)
                    if decl.startswith("public ") and " delegate " not in decl and " operator " not in decl:
                        owner, member, kind = body[0], None, None
                        before = re.split(r"=>|\{|=(?!=)|;", decl, 1)[0]
                        if "this[" in before:
                            member, kind = "this[]", "indexer"
                        elif "(" in before:
                            # The last identifier before the parameter list, after its type parameters:
                            # "public ComponentPool<T> Pool<T>(" -> Pool.
                            name_part = before.split("(", 1)[0].rstrip()
                            while re.search(r"<[^<>]*>$", name_part):
                                name_part = re.sub(r"\s*<[^<>]*>$", "", name_part).rstrip()
                            mm = re.search(r"(\w+)$", name_part)
                            member, kind = (mm.group(1), "method") if mm else (None, None)
                            if member == owner:
                                member, kind = "ctor", "constructor"
                            ext = re.search(r"\(\s*this\s+([\w.]+)", before)
                            if ext:
                                owner = ext.group(1).split(".")[-1]
                        else:
                            mm = re.search(r"(\w+)\s*$", before.strip())
                            member, kind = (mm.group(1), "event" if " event " in decl else "property/field") if mm else (None, None)
                        if member and (owner in classes or (owner == "World" and t == "World") or owner.startswith(classes)):
                            out[t].setdefault(member, kind)
                for ch in line:
                    if ch == "{":
                        depth += 1
                        if pending:
                            stack.append((pending, depth))
                            pending = None
                    elif ch == "}":
                        if stack and stack[-1][1] == depth:
                            stack.pop()
                        depth -= 1
    # Extension methods of these types anywhere in Core (World.AddEvent lives with EventBuffer, for example).
    try:
        core = sorted(os.listdir(os.path.join(src, "Core")))
    except OSError:
        core = []
    for name in core:
        if not name.endswith(".cs"):
            continue
        try:
            with open(os.path.join(src, "Core", name), encoding="utf-8") as f:
                code = _strip_code(f.read())
        except OSError:
            continue
        for mm in re.finditer(r"public\s+static\s+[^;{=]*?\b(\w+)\s*(?:<[^()]*?>)?\s*\(\s*this\s+([\w.]+)", code):
            owner = mm.group(2).split(".")[-1]
            for t, (_, classes) in API_TYPES.items():
                if owner in classes:
                    out[t].setdefault(mm.group(1), "extension")
    return out


def api_coverage(src, states):
    """Which module reaches each public member of World, ComponentPool, Filter and Group: the cells of the contracts
    matrix (exact), and a static scan of the fuzz-core and alloc code (a call on a receiver named like that type, or of a
    member name no other of these types has). Members no module reaches are the holes a new class of bugs falls in."""
    members = public_members(src)
    if not any(members.values()):
        return {"note": "no public members found under %s/Core" % rel_root(src)}
    covered = {"contracts": {t: set() for t in API_TYPES}}
    for cfg in ("rel", "dbg"):
        st = states.get(("contracts", cfg))
        if not st or not st.usable():
            continue
        matrix = load_json(os.path.join(st.dir, (st.rep.get("files") or {}).get("matrixJson") or "matrix.json")) or {}
        for cell in matrix.get("Cells") or []:
            mm = re.match(r"^(\w+)(?:<[^>]*>)?\.(this\[\]|\w+)", cell.get("Member") or "")
            if not mm:
                continue
            owner = mm.group(1)
            for t, (_, classes) in API_TYPES.items():
                if owner in classes:
                    covered["contracts"][t].add(mm.group(2))
    unique = {}
    for t, ms in members.items():
        for m in ms:
            unique[m] = t if m not in unique else None
    sources = {}
    for module, (folder, sub) in API_SCANNED.items():
        texts = []
        for d, dirs, files in os.walk(os.path.join(STAND, folder, sub)):
            dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
            for n in files:
                if n.endswith(".cs"):
                    try:
                        with open(os.path.join(d, n), encoding="utf-8") as f:
                            texts.append(_strip_code(f.read()))
                    except OSError:
                        pass
        sources[module] = "\n".join(texts)
        covered[module] = {t: set() for t in API_TYPES}
        for t, ms in members.items():
            for m in ms:
                if m == "ctor":
                    hit = re.search(r"\bnew\s+%s\s*[(<]" % t, sources[module])
                elif m == "this[]":
                    hit = re.search(API_RECEIVERS[t] + r"\s*\[", sources[module])
                else:
                    hit = re.search(API_RECEIVERS[t] + r"\s*\??\.\s*" + re.escape(m) + r"\b", sources[module]) or \
                        (unique.get(m) == t and re.search(r"\.\s*" + re.escape(m) + r"\s*[<(]", sources[module]))
                if hit:
                    covered[module][t].add(m)
    modules = ["contracts"] + list(API_SCANNED)
    out = {"modules": modules, "types": {}, "method": "contracts: the members of its matrix cells; fuzz-core, alloc: calls in "
                                                       "their code (a static scan, overloads merged, approximate)"}
    for t, ms in members.items():
        rows = []
        for m in sorted(ms, key=str.lower):
            rows.append({"member": m, "kind": ms[m], **{mod: m in covered[mod][t] for mod in modules}})
        out["types"][t] = {"members": rows, "count": len(rows),
                           "covered": {mod: sum(1 for r in rows if r[mod]) for mod in modules},
                           "uncovered": [r["member"] for r in rows if not any(r[mod] for mod in modules)]}
    return out


# ----------------------------------------------------------------------------- build

def cmd_build(a):
    catalog = load_json(CATALOG)
    if not catalog:
        sys.exit("summary: cannot read %s" % CATALOG)
    src = realpath(a.src)
    flavor = a.flavor
    configs = [c for c in a.configs.split(",") if c]
    selected = [m for m in a.modules.split(",") if m]
    steps = read_steps(a.steps, flavor)
    run_start = a.run_start or 0
    prev = load_json(os.path.join(REPORTS, "summary-%s.json" % flavor)) or {}
    prev_runs = {}
    for r in prev.get("moduleRuns") or []:
        row = dict(r)
        row.setdefault("runStarted", prev.get("runStarted"))
        prev_runs[(r.get("module"), r.get("cfg"))] = row
    notes = {"unity": a.unity_note} if a.unity_note else {}
    states = {}
    for module in MODULES:
        for cfg in list(configs) + (["ci"] if module == "alloc" else []):
            if module not in selected:
                st = ModuleState(module, cfg)
                st.state, st.note = "not-selected", "module not selected"
                states[(module, cfg)] = st
                continue
            st = load_state(module, cfg, flavor, src, steps, run_start, prev_runs)
            if module == "unity" and notes.get("unity") and (module, cfg) not in steps:
                st.note = ("skipped (%s); " % notes["unity"]) + st.note
            states[(module, cfg)] = st
    entries, claims = evaluate(catalog, states, configs, selected, notes)
    rows = signature_rows({k: v for k, v in states.items() if v.usable()}, claims)
    defects = merge_rows(rows, "defect")
    regressions = merge_rows(rows, "regression")
    counts = {s: 0 for s in STATUSES}
    for e in entries:
        counts[e["status"]] = counts.get(e["status"], 0) + 1
    usable = [st for st in states.values() if st.usable()]
    summary = {
        "format": FORMAT, "flavor": flavor, "src": rel_root(src), "packageVersion": pkg_version(src),
        "tree": tree_identity(flavor, src), "generated": now_iso(), "mode": a.mode, "command": a.command,
        "configs": configs, "modules": selected, "runStarted": iso_of(run_start) if run_start else None,
        "moduleRuns": [states[k].to_json() for k in sorted(states, key=lambda k: (MODULES.index(k[0]), CFG_RANK.get(k[1], 9)))],
        "catalog": {"source": rel_stand(CATALOG), "counts": counts, "entries": entries},
        "signatures": rows,
        "defects": defects, "uncatalogued": [d for d in defects if not d["catalog"]],
        "regressions": regressions, "uncataloguedRegressions": [r for r in regressions if not r["catalog"]],
        "questions": {m: {c: states[(m, c)].count("question") for c in configs if states[(m, c)].usable()} for m in selected},
        "comparability": {"%s/%s" % (st.module, st.cfg): comparability(st) for st in usable},
        "promises": {"%s/%s" % (st.module, st.cfg): promises(st) for st in usable if promises(st) is not None},
        "warnings": {"%s/%s" % (st.module, st.cfg): warnings_of(st) for st in usable if warnings_of(st)},
        "moduleComparisons": {"%s/%s" % (st.module, st.cfg): module_comparison(st) for st in usable if module_comparison(st)},
    }
    summary["apiCoverage"] = api_coverage(src, states)
    summary["mustCatch"] = must_catch(catalog, entries, src)
    if a.baseline:
        summary["baseline"] = diff_baseline(a.baseline, summary, a.baseline_summary)
    os.makedirs(REPORTS, exist_ok=True)
    name = a.out_name or flavor
    jpath = os.path.join(REPORTS, "summary-%s.json" % name)
    write_text(jpath, json.dumps(summary, ensure_ascii=False, indent=1) + "\n")
    write_text(os.path.join(REPORTS, "summary-%s.md" % name), render_summary(summary, catalog))
    print_digest(summary, jpath)
    bad = [s for s in states.values() if s.state in ("failed", "stale", "mismatch")]
    return 1 if bad else 0


# ----------------------------------------------------------------------------- baseline

def diff_baseline(base_flavor, cur, path=None):
    path = path or os.path.join(REPORTS, "summary-%s.json" % base_flavor)
    base = load_json(path)
    if not base:
        return {"flavor": base_flavor, "note": "no %s: build the baseline summary first" % os.path.basename(path)}
    if base.get("format", 1) < FORMAT:
        return {"flavor": base_flavor, "note": "%s is of an older format: rebuild it (Stand/run.sh --summary-only --ref %s)"
                % (os.path.basename(path), base_flavor)}
    # 1. Catalog statuses.
    bmap = {e["id"]: e for e in base["catalog"]["entries"]}
    changes, uncompared = [], []
    for e in cur["catalog"]["entries"]:
        b = bmap.get(e["id"])
        if not b or b["status"] == e["status"]:
            continue
        bs, cs = b["status"], e["status"]
        if "not-run" in (bs, cs):
            uncompared.append(e["id"])
            continue
        if bs in OPEN and cs == "absent":
            kind = "fixed"
        elif bs == "absent" and cs in OPEN:
            kind = "appeared"
        elif cs == "documented":
            kind = "now documented"
        elif bs in ("question", "regression") and cs == "defect":
            kind = "now a defect"
        else:
            kind = "changed"
        changes.append({"id": e["id"], "baseline": bs, "current": cs, "change": kind})
    # 2. Signatures, per module and build that have a usable report on both sides with comparable inputs.
    def usable_pairs(s):
        return {"%s/%s" % (r["module"], r["cfg"]) for r in s.get("moduleRuns") or []
                if r.get("state") in ("ok", "exit", "reused") and r.get("counts") is not None}
    pairs = sorted(usable_pairs(cur) & usable_pairs(base), key=lambda p: (MODULES.index(p.split("/")[0]), p))
    skipped = []
    brows, crows = {}, {}
    for r in base.get("signatures") or []:
        brows.setdefault("%s/%s" % (r["module"], r["cfg"]), {})[r["signature"]] = r
    for r in cur.get("signatures") or []:
        crows.setdefault("%s/%s" % (r["module"], r["cfg"]), {})[r["signature"]] = r
    new, gone, spikes, changed = [], [], [], []
    compared, reg_notes = [], []
    own_base = lambda s: {"%s/%s" % (r["module"], r["cfg"]): r.get("baseline") for r in s.get("moduleRuns") or []}
    cur_own, base_own = own_base(cur), own_base(base)
    for p in pairs:
        ok, why = comparable((cur.get("comparability") or {}).get(p), (base.get("comparability") or {}).get(p))
        if not ok:
            skipped.append({"pair": p, "reason": why})
            continue
        compared.append(p)
        b, c = brows.get(p, {}), crows.get(p, {})
        if cur_own.get(p) != base_own.get(p):
            # A regression is relative to the module's own baseline: with different ones on the two sides
            # (alloc: v2.1.1 there, this baseline here) the regression rows say nothing about each other.
            nb = sum(1 for v in b.values() if v["status"] == "regression")
            nc = sum(1 for v in c.values() if v["status"] == "regression")
            if nb or nc:
                b = {k: v for k, v in b.items() if v["status"] != "regression"}
                c = {k: v for k, v in c.items() if v["status"] != "regression"}
                reg_notes.append({"pair": p, "baseline": nb, "current": nc, "baselineVs": base_own.get(p), "currentVs": cur_own.get(p)})
        for sig, r in c.items():
            br = b.get(sig)
            if br is None:
                new.append(r)
                continue
            if br["status"] != r["status"]:
                changed.append(dict(r, baselineStatus=br["status"]))
            hb, hc = num(br.get("hits")), num(r.get("hits"))
            mb, mc = num(br.get("magnitude")), num(r.get("magnitude"))
            if hb is not None and hc is not None and hb > 0 and hc >= SPIKE_FACTOR * hb and hc - hb >= SPIKE_MIN:
                spikes.append(dict(r, baselineHits=hb))
            elif mb is not None and mc is not None and mb > 0 and mc > MAGNITUDE_FACTOR * mb:
                spikes.append(dict(r, baselineMagnitude=mb))
        for sig, r in b.items():
            if sig not in c:
                gone.append(r)
    # 3. Promises the modules checked: active on the baseline, not here (a rewritten sentence switches a rule off).
    dropped, added = [], []
    for p, ids in (cur.get("promises") or {}).items():
        bids = (base.get("promises") or {}).get(p)
        if bids is None:
            continue
        for i in sorted(set(bids) - set(ids)):
            dropped.append({"pair": p, "promise": i})
        for i in sorted(set(ids) - set(bids)):
            added.append({"pair": p, "promise": i})

    def merged(rows, extra=()):
        out = {}
        for r in rows:
            k = (r["module"], r["signature"])
            m = out.get(k)
            if m is None:
                m = {x: r.get(x) for x in ("module", "signature", "status", "family", "catalog", "id", "title", "severity",
                                           "hits", "magnitude", "repro") + tuple(extra)}
                m["configs"] = [r["cfg"]]
                out[k] = m
            else:
                m["configs"].append(r["cfg"])
        return sorted(out.values(), key=lambda m: (ITEM_STATUSES.index(m["status"]), MODULES.index(m["module"]), str(m["signature"])))

    # fuzz-core sees a hang by a timeout, so a slow scenario of a known family can flip between a hang, a stack
    # overflow and a plain failure from one run to the next: such crash/hang signatures are listed apart.
    flaky = lambda r: r.get("timing") and r.get("family") not in (None, "NEW")
    timing = merged([r for r in new if flaky(r)]) + merged([dict(r, gone=True) for r in gone if flaky(r)], ("gone",))
    new = [r for r in new if not flaky(r)]
    gone = [r for r in gone if not flaky(r)]
    new_m = merged(new)
    return {"flavor": base_flavor, "timingDependent": timing, "baselineGenerated": base.get("generated"), "baselineMode": base.get("mode"),
            "changes": changes, "uncompared": uncompared, "comparedPairs": compared, "notCompared": skipped,
            "regressionsNotCompared": reg_notes,
            "newSignatures": new_m,
            "newInKnown": [r for r in new_m if r["status"] in OPEN and (r.get("family") not in (None, "NEW") or r.get("catalog"))],
            "newUnknown": [r for r in new_m if r["status"] in OPEN and not (r.get("family") not in (None, "NEW") or r.get("catalog"))],
            "goneSignatures": merged(gone),
            "spikes": merged(spikes, ("baselineHits", "baselineMagnitude")),
            "statusChanged": merged(changed, ("baselineStatus",)),
            "droppedPromises": dropped, "addedPromises": added}


# ----------------------------------------------------------------------------- markdown

STATE_TEXT = {"ok": "ok", "exit": "exit ≠ 0", "failed": "**failed**", "stale": "stale", "reused": "earlier run",
              "mismatch": "other tree", "missing": "no report", "not-selected": "not selected", "skipped": "skipped"}


def link(path, base_dir, label=None):
    """A markdown link from a file in base_dir to a path relative to Stand/."""
    if not path:
        return ""
    target = path if os.path.isabs(path) else os.path.join(STAND, path)
    rel = os.path.relpath(target, base_dir)
    return "[%s](%s)" % (label or os.path.basename(path), rel.replace(" ", "%20"))


def fmt_hits(r):
    parts = []
    if num(r.get("hits")) is not None:
        parts.append("%s hits" % r["hits"])
    if num(r.get("magnitude")) is not None and r["module"] == "alloc":
        parts.append("%s" % r["magnitude"])
    return ", ".join(parts)


def render_summary(s, catalog):
    L = []
    t = s["tree"]
    L.append("# Stand summary: %s" % s["flavor"])
    L.append("")
    L.append("- Tree: `%s`, package %s, %s%s." % (s["src"], s["packageVersion"] or "?", t.get("ref") or "?",
                                                 (", commit %s" % t["commit"][:12]) if t.get("commit") else ""))
    if t.get("note"):
        L.append("- Origin: %s." % t["note"])
    mode = "as in the module reports (column \"mode\")" if s["mode"] in (None, "existing") else s["mode"]
    L.append("- Module mode: %s; builds: %s." % (mode, ", ".join(CFG_TEXT.get(c, c) for c in s["configs"])))
    if s.get("runStarted"):
        L.append("- Run started %s, summary built %s." % (s["runStarted"], s["generated"]))
    else:
        L.append("- Summary built %s from the reports that were there." % s["generated"])
    if s.get("command"):
        L.append("- Command: `%s`." % s["command"])
    c = s["catalog"]["counts"]
    L.append("")
    L.append("**Catalog (%d entries):** %s. **Defects outside the catalog: %d; regressions outside the catalog: %d.**" % (
        len(s["catalog"]["entries"]), ", ".join("%s %d" % (k, c.get(k, 0)) for k in STATUSES),
        len(s["uncatalogued"]), len(s.get("uncataloguedRegressions") or [])))
    mc = s.get("mustCatch")
    if mc:
        L.append("")
        L.append("**Must-catch entries of %s: %d of %d caught%s.**" % (
            mc["release"], mc["caught"], mc["total"], (" (missed: %s)" % ", ".join(mc["missed"])) if mc["missed"] else ""))
    b = s.get("baseline")
    if b and not b.get("note"):
        L.append("")
        L.append("**Against %s:** catalog changes %d; new signatures %d (in known families %d, unknown %d); gone %d; "
                 "sharp growth %d; status changed %d; promises switched off %d; crash/hang flips %d." % (
                     b["flavor"], len(b["changes"]), len(b["newSignatures"]), len(b["newInKnown"]), len(b["newUnknown"]),
                     len(b["goneSignatures"]), len(b["spikes"]), len(b["statusChanged"]), len(b["droppedPromises"]),
                     len(b.get("timingDependent") or [])))
    L.append("")
    L.append("## Module runs")
    L.append("")
    L.append("Counts are signatures (mechanism and place). Regressions are changes against the module's baseline "
             "(column \"vs\") that break no promise; questions are behaviour the documents do not decide.")
    L.append("")
    L.append("| module | build | state | mode | vs | time, s | defects | regressions | questions | documented | report | note |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for r in s["moduleRuns"]:
        rep = r["report"]
        rmd = rep[:-len("report.json")] + "report.md" if rep else None
        cnt = r.get("counts")
        sev = ""
        if cnt and r.get("defectsBySeverity"):
            sev = " (%s)" % ", ".join("%s %d" % (k, v) for k, v in sorted(r["defectsBySeverity"].items(), key=lambda kv: SEV_RANK.get(kv[0], 9)))
        L.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            r["module"], r["cfg"], STATE_TEXT.get(r["state"], r["state"]), md(r["mode"]), md(r.get("baseline") or ""),
            r["seconds"] if r["seconds"] is not None else "",
            ("%d%s" % (cnt["defect"], sev)) if cnt else "", cnt["regression"] if cnt else "",
            cnt["question"] if cnt else "", cnt["documented"] if cnt else "",
            link(rmd, REPORTS, "report.md") if rmd and os.path.exists(os.path.join(STAND, rmd)) else "",
            md(r["note"]) + ((" " + link(r["log"], REPORTS, "log")) if r.get("log") else "")))
    L.append("")
    L.append("## Catalog of known problems")
    L.append("")
    L.append("Statuses: " + "; ".join("**%s**: %s" % (k, v) for k, v in STATUS_HELP) +
             ". Per build, **n/a** means it cannot occur in that build. Catalog: `Stand/summary/catalog.json`.")
    groups = {g["id"]: g["title"] for g in catalog["groups"]}
    for gid, gtitle in groups.items():
        rows = [e for e in s["catalog"]["entries"] if e["group"] == gid]
        if not rows:
            continue
        L.append("")
        L.append("### %s" % gtitle)
        L.append("")
        L.append("| id | problem | module | status | what the modules see | repro |")
        L.append("|---|---|---|---|---|---|")
        for e in rows:
            ev = "; ".join("%s: %s" % (cfg, v["evidence"]) for cfg, v in e["builds"].items() if v["evidence"] and v["status"] != NA)
            rp = e["repro"][:1]
            more = len(e["repro"]) - len(rp)
            L.append("| %s | %s | %s | %s | %s | %s |" % (
                e["id"], short(e["title"], 200), md(e["module"]), md(status_text(e)), short(ev, 260),
                " ".join(link(p, REPORTS) for p in rp) + (" (+%d)" % more if more > 0 else "")))
    if mc:
        L.append("")
        L.append("## Must-catch entries of %s" % mc["release"])
        L.append("")
        L.append("The problems found on %s by the game project and the Windows review (catalog field `mustCatchOn`): "
                 "the stand must catch every one of them on a %s tree. %s." % (mc["release"], mc["release"], mc["rule"][0].upper() + mc["rule"][1:]))
        L.append("")
        L.append("| id | status | caught | why |")
        L.append("|---|---|---|---|")
        for r in mc["rows"]:
            L.append("| %s | %s | %s | %s |" % (r["id"], r["status"], "yes" if r["caught"] else "**no**", md(r["why"])))
    L.append("")
    L.append("## Defects outside the catalog")
    L.append("")
    if not s["uncatalogued"]:
        L.append("None: a catalog entry claims every defect the modules report.")
    else:
        L.append("No catalog entry claims these defects (one row per signature). Triage each one: a real defect gets a "
                 "corpus entry in its module and an entry in `Stand/summary/catalog.json` (see Stand/README.md, "
                 "\"Corpus\"); a false one is fixed in the module's rule, never hidden in the catalog.")
        L.append("")
        L.append("| module | builds | signature | family | severity | what | hits | repro |")
        L.append("|---|---|---|---|---|---|---|---|")
        for f in sorted(s["uncatalogued"], key=lambda f: (SEV_RANK.get(f["severity"], 9), MODULES.index(f["module"]), str(f["signature"]))):
            L.append("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
                f["module"], ", ".join(f["configs"]), short(f["signature"], 120), md(f.get("family") or ""), md(f["severity"]),
                short(f["title"], 160), fmt_hits(f), link(f["repro"], REPORTS) +
                (" (repro not verified)" if f.get("verified") is False else "")))
    if s.get("uncataloguedRegressions"):
        L.append("")
        L.append("## Regressions outside the catalog")
        L.append("")
        L.append("Changes against the module's baseline that break no documented promise and that no catalog entry "
                 "claims. Not defects, but each needs a decision: an expected cost of a change gets a catalog entry, "
                 "anything else is a regression of the version.")
        L.append("")
        L.append("| module | builds | signature | severity | what | size | repro |")
        L.append("|---|---|---|---|---|---|---|")
        for f in sorted(s["uncataloguedRegressions"], key=lambda f: (SEV_RANK.get(f["severity"], 9), MODULES.index(f["module"]))):
            L.append("| %s | %s | %s | %s | %s | %s | %s |" % (f["module"], ", ".join(f["configs"]), short(f["signature"], 120),
                                                          md(f["severity"]), short(f["title"], 160), fmt_hits(f),
                                                          link(f["repro"], REPORTS)))
    if b:
        render_baseline(L, s, b)
    if s.get("warnings"):
        L.append("")
        L.append("## Stand warnings")
        L.append("")
        L.append("What the modules report about their own run (a result may be incomplete):")
        L.append("")
        for p, ws in s["warnings"].items():
            for w in ws:
                L.append("- %s: %s" % (p, md(w)))
    L.append("")
    L.append("## Contract questions")
    L.append("")
    L.append("Questions are not defects: the behaviour is suspicious, but no written promise forbids it. The lists are in "
             "the module reports; a question the catalog tracks is shown above with its entry.")
    L.append("")
    L.append("; ".join("%s: %s" % (m, ", ".join("%s %d" % (c, n) for c, n in per.items()) or "no reports")
                       for m, per in s["questions"].items()) + ".")
    L.append("")
    render_api_coverage(L, s.get("apiCoverage"))
    return "\n".join(L)


def render_api_coverage(L, cov):
    if not cov or not cov.get("types"):
        return
    mods = cov["modules"]
    L.append("## API coverage by module")
    L.append("")
    L.append("The public members of World, ComponentPool, Filter and Group in this tree and the modules that reach them (%s). "
             "A member no module reaches is where a new class of bugs goes unseen: give it a matrix target, an operation or "
             "a scenario." % cov.get("method", ""))
    L.append("")
    L.append("| type | members | %s | none |" % " | ".join(mods))
    L.append("|---|---|%s---|" % "".join("---|" for _ in mods))
    for t, v in cov["types"].items():
        L.append("| %s | %d | %s | %d |" % (t, v["count"], " | ".join(str(v["covered"][m]) for m in mods), len(v["uncovered"])))
    L.append("")
    for t, v in cov["types"].items():
        if v["uncovered"]:
            L.append("- %s, reached by no module: %s." % (t, ", ".join("`%s`" % md(x) for x in v["uncovered"])))
    L.append("")
    L.append("<details><summary>Every member</summary>")
    L.append("")
    L.append("| type | member | kind | %s |" % " | ".join(mods))
    L.append("|---|---|---|%s" % "".join("---|" for _ in mods))
    for t, v in cov["types"].items():
        for r in v["members"]:
            L.append("| %s | `%s` | %s | %s |" % (t, md(r["member"]), r["kind"], " | ".join("yes" if r[m] else "" for m in mods)))
    L.append("")
    L.append("</details>")
    L.append("")


def render_baseline(L, s, b):
    L.append("")
    L.append("## Against %s" % b["flavor"])
    L.append("")
    if b.get("note"):
        L.append(b["note"] + ".")
        return
    L.append("Baseline summary built %s (mode %s)." % (b.get("baselineGenerated"), b.get("baselineMode")))
    L.append("")
    L.append("### Catalog")
    L.append("")
    if b["changes"]:
        L.append("| id | %s | %s | change |" % (b["flavor"], s["flavor"]))
        L.append("|---|---|---|---|")
        for ch in b["changes"]:
            L.append("| %s | %s | %s | %s |" % (ch["id"], ch["baseline"], ch["current"], ch["change"]))
    else:
        L.append("No catalog entry changed its status.")
    if b.get("uncompared"):
        L.append("")
        L.append("Not compared (not run on one side): %s." % ", ".join(b["uncompared"]))
    L.append("")
    L.append("### Signatures")
    L.append("")
    L.append("Compared per module and build where both sides have a usable report with the same inputs (%s). A new "
             "signature inside a known family or catalog entry is the case a status comparison would hide." %
             (", ".join(b["comparedPairs"]) or "none"))
    if b.get("notCompared"):
        L.append("")
        L.append("Not compared: %s." % "; ".join("%s (%s)" % (x["pair"], x["reason"]) for x in b["notCompared"]))
    if b.get("regressionsNotCompared"):
        L.append("")
        L.append("Regressions are relative to the module's own baseline, so they are compared only where it is the same on "
                 "both sides. Left out: %s." % "; ".join(
                     "%s (%d against %s there, %d against %s here)" % (x["pair"], x["baseline"], x["baselineVs"] or "nothing",
                                                                       x["current"], x["currentVs"] or "nothing")
                     for x in b["regressionsNotCompared"]))

    def table(title, rows, extra=None):
        L.append("")
        L.append("**%s: %d**" % (title, len(rows)))
        if not rows:
            return
        L.append("")
        head = "| module | builds | status | signature | family / entry | %s |" % (extra[0] if extra else "hits")
        L.append(head)
        L.append("|---|---|---|---|---|---|")
        for r in rows[:60]:
            fam = " / ".join(x for x in (r.get("family"), r.get("catalog")) if x) or "—"
            L.append("| %s | %s | %s | %s | %s | %s |" % (r["module"], ", ".join(r["configs"]), r["status"],
                                                        short(r["signature"], 140), md(fam),
                                                        extra[1](r) if extra else fmt_hits(r)))
        if len(rows) > 60:
            L.append("")
            L.append("… and %d more in summary-%s.json." % (len(rows) - 60, s["flavor"]))

    table("New defect, regression or question signatures inside a known family or catalog entry", b["newInKnown"])
    table("New defect, regression or question signatures of no known family", b["newUnknown"])
    table("Sharp growth (hits x%d and +%d, or alloc bytes x%d)" % (SPIKE_FACTOR, SPIKE_MIN, MAGNITUDE_FACTOR), b["spikes"],
          ("baseline -> now", lambda r: "%s -> %s" % (r.get("baselineHits") if r.get("baselineHits") is not None else r.get("baselineMagnitude"),
                                                     r.get("hits") if r.get("baselineHits") is not None else r.get("magnitude"))))
    table("Status changed", b["statusChanged"], ("baseline -> now", lambda r: "%s -> %s" % (r.get("baselineStatus"), r["status"])))
    table("Gone signatures", b["goneSignatures"])
    if b.get("timingDependent"):
        L.append("")
        L.append("**Crash and hang signatures of known families that came or went: %d.** fuzz-core detects a hang by a "
                 "timeout, so a slow scenario can be a hang in one run and a stack overflow or a plain failure in the "
                 "next; the module's seed-by-seed comparison counts such a seed as failing differently. Not counted above:"
                 % len(b["timingDependent"]))
        L.append("")
        for r in b["timingDependent"]:
            L.append("- %s %s %s `%s` (%s)" % (r["module"], ", ".join(r["configs"]), "gone" if r.get("gone") else "new",
                                              md(r["signature"]), " / ".join(x for x in (r.get("family"), r.get("catalog")) if x)))
    other_new = [r for r in b["newSignatures"] if r["status"] not in OPEN]
    if other_new:
        L.append("")
        L.append("New documented signatures: %d." % len(other_new))
    L.append("")
    L.append("### Promises")
    L.append("")
    if b["droppedPromises"]:
        L.append("Promises a module checked on %s and no longer checks here (a rewritten sentence in the documents "
                 "switches a rule off silently; check each one):" % b["flavor"])
        L.append("")
        for x in b["droppedPromises"]:
            L.append("- %s: `%s`" % (x["pair"], x["promise"]))
    else:
        L.append("Every promise the modules checked on %s is checked here too." % b["flavor"])
    if b["addedPromises"]:
        L.append("")
        L.append("Checked here and not on %s: %s." % (b["flavor"], ", ".join("%s `%s`" % (x["pair"], x["promise"]) for x in b["addedPromises"])))
    comps = s.get("moduleComparisons") or {}
    if comps:
        L.append("")
        L.append("### The modules' own comparisons")
        L.append("")
        for p, cmp_ in comps.items():
            L.append("- %s: %s" % (p, ", ".join("%s %s" % (k, v) for k, v in cmp_.items() if v not in (None, ""))))


def print_digest(s, jpath):
    c = s["catalog"]["counts"]
    print("summary %s: catalog %s; outside the catalog: defects %d, regressions %d" % (
        s["flavor"], ", ".join("%s %d" % (k, c.get(k, 0)) for k in STATUSES), len(s["uncatalogued"]),
        len(s.get("uncataloguedRegressions") or [])))
    for r in s["moduleRuns"]:
        if r["state"] in ("failed", "stale", "mismatch", "missing", "exit"):
            print("  %s %s: %s - %s" % (r["module"], r["cfg"], r["state"], r["note"]))
    mc = s.get("mustCatch")
    if mc:
        print("  must-catch entries of %s: %d of %d caught%s" % (mc["release"], mc["caught"], mc["total"],
                                                                (" (missed: %s)" % ", ".join(mc["missed"])) if mc["missed"] else ""))
    b = s.get("baseline")
    if b and b.get("changes") is not None:
        if b["changes"]:
            print("  against %s: %s" % (b["flavor"], ", ".join("%s %s" % (x["id"], x["change"]) for x in b["changes"])))
        print("  signatures against %s: new %d (in known families %d, unknown %d), gone %d, sharp growth %d, status changed %d, "
              "promises off %d, crash/hang flips %d"
              % (b["flavor"], len(b["newSignatures"]), len(b["newInKnown"]), len(b["newUnknown"]), len(b["goneSignatures"]),
                 len(b["spikes"]), len(b["statusChanged"]), len(b["droppedPromises"]), len(b.get("timingDependent") or [])))
    elif b and b.get("note"):
        print("  against %s: %s" % (b["flavor"], b["note"]))
    print("  %s" % jpath[:-5] + ".md")


# ----------------------------------------------------------------------------- self-test

def selftest_dir(module, flavor, cfg):
    if module == "alloc":
        return os.path.join(REPORTS, "alloc", "selftest-%s" % flavor)
    return os.path.join(REPORTS, module, "selftest-%s-%s" % (flavor, cfg))


def judge_mutant(m):
    """ok, fail or n/a for one mutant row of a module self-test. n/a: the mutant is out of scope, meant for another
    build, or for trees of another contract version (its Since/Until range: the code it changes is another
    version's). A mutant within its range whose patch does not apply ("not-applicable": the code moved on) fails: the
    patch must be refreshed, a class of defects must never drop out of the self-test silently."""
    kind, status = m.get("kind") or "bug", str(m.get("status") or "")
    if status in ("n/a", "skipped") or kind == "out-of-scope":
        return "n/a"
    if kind == "bug":
        # Only an exact "killed": "killed (other rule)", "killed-other", ABSORBED, KILLED-NO-REPRO are failures.
        return "ok" if status == "killed" else "fail"
    if kind == "fix":
        return "ok" if status in ("fix-accepted", "accepted") else "fail"
    if kind == "regression":
        return "ok" if status in ("classified", "regression") else "fail"
    if kind == "doc":
        # A change the documents of the tree describe: accepted when its documented entry appears and no defect does.
        return "ok" if status in ("doc-accepted", "accepted") else "fail"
    return "ok" if status.startswith(("killed", "fix-accepted", "classified")) else "fail"


def skipped_by_version(m):
    """Whether a mutant row is n/a because of the tree's contract version (its Since/Until range)."""
    return str(m.get("status") or "") == "n/a" and "contract version" in str(m.get("detail") or "")


def cmd_selftest(a):
    flavor = a.flavor
    configs = [c for c in a.configs.split(",") if c]
    selected = [m for m in a.modules.split(",") if m]
    steps = read_steps(a.steps, flavor, role="selftest")
    rows, failed = [], []
    for module in [m for m in MODULES if m in selected]:
        for cfg in (["all"] if module == "alloc" else configs):
            d = selftest_dir(module, flavor, cfg)
            path = os.path.join(d, "selftest.json")
            doc = load_json(path)
            step = steps.get((module, cfg))
            row = {"module": module, "cfg": cfg, "report": rel_stand(os.path.join(d, "selftest.md")),
                   "exit": step["exit"] if step else None, "seconds": (step["end"] - step["start"]) if step else None,
                   "log": rel_stand(step["log"]) if step else None}
            if module == "unity" and a.unity_note and not step:
                row.update(result="not run", note="unity skipped (%s)" % a.unity_note)
                rows.append(row)
                continue
            if not step and a.steps:
                row.update(result="not run", note="no step in this run")
                rows.append(row)
                continue
            if doc is None or (step and os.path.getmtime(path) + 1 < step["start"]):
                row.update(result="FAILED", note="no fresh selftest.json (exit %s)" % row["exit"])
                rows.append(row)
                failed.append(row)
                continue
            muts = doc.get("mutants") or []
            judged = [(m, judge_mutant(m)) for m in muts]
            by_kind = {}
            for m, j in judged:
                k = m.get("kind") or "bug"
                d2 = by_kind.setdefault(k, {"ok": 0, "fail": 0, "n/a": 0})
                d2[j] += 1
            bad = [m for m, j in judged if j == "fail"]
            errors = doc.get("errors") or doc.get("broken") or 0
            control = doc.get("controlClean")
            ok = not bad and not errors and control is not False and (row["exit"] in (None, 0))
            row.update(result="ok" if ok else "FAILED", kinds=by_kind, errors=errors, controlClean=control,
                       seconds=row["seconds"] if row["seconds"] is not None else doc.get("seconds"),
                       contractVersion=doc.get("contractVersion"),
                       skippedByVersion=[m.get("name") for m, j in judged if skipped_by_version(m)],
                       mutants=[{"name": m.get("name"), "kind": m.get("kind"), "status": m.get("status"), "verdict": j,
                                 "detail": short(m.get("detail") or "", 300), "why": short(m.get("why") or "", 300)}
                                for m, j in judged],
                       note="" if ok else ("not caught: %s" % ", ".join(m.get("name") for m in bad) if bad else
                                           "errors %s, control clean %s, exit %s" % (errors, control, row["exit"])))
            rows.append(row)
            if not ok:
                failed.append(row)
    doc = {"format": FORMAT, "flavor": flavor, "src": rel_root(a.src) if a.src else None, "generated": now_iso(),
           "command": a.command, "runStarted": iso_of(a.run_start) if a.run_start else None,
           "modules": selected, "configs": configs, "ok": not failed, "rows": rows}
    jpath = os.path.join(REPORTS, "selftest-%s.json" % flavor)
    os.makedirs(REPORTS, exist_ok=True)
    write_text(jpath, json.dumps(doc, ensure_ascii=False, indent=1) + "\n")
    write_text(jpath[:-5] + ".md", render_selftest(doc))
    print("selftest %s: %s" % (flavor, "every self-test passed" if not failed else "FAILED: " + ", ".join(
        "%s %s" % (r["module"], r["cfg"]) for r in failed)))
    for r in rows:
        k = r.get("kinds") or {}
        print("  %-9s %-3s %-8s bug %s, fix %s%s%s%s" % (
            r["module"], r["cfg"], r["result"], frac(k.get("bug")), frac(k.get("fix")),
            (", regression %s" % frac(k.get("regression"))) if k.get("regression") else "",
            (", doc %s" % frac(k.get("doc"))) if k.get("doc") else "",
            ("  (%s)" % r["note"]) if r.get("note") else ""))
        if r.get("skippedByVersion"):
            print("      skipped by contract version%s: %s" % (
                " (tree %s)" % r["contractVersion"] if r.get("contractVersion") else "", ", ".join(r["skippedByVersion"])))
    print("  %s" % jpath[:-5] + ".md")
    return 1 if failed else 0


def frac(d):
    if not d:
        return "-"
    total = d["ok"] + d["fail"]
    return "%d/%d%s" % (d["ok"], total, (" (+%d n/a)" % d["n/a"]) if d["n/a"] else "")


def render_selftest(doc):
    L = ["# Stand self-test: %s" % doc["flavor"], ""]
    L.append("- Tree: `%s`." % doc.get("src"))
    if doc.get("command"):
        L.append("- Command: `%s`." % doc["command"])
    L.append("- Built %s%s." % (doc["generated"], (", run started %s" % doc["runStarted"]) if doc.get("runStarted") else ""))
    L.append("")
    L.append("Each module applies the patches in `Stand/<module>/mutants/` to a temporary copy of the tree and runs on it. "
             "A **bug** mutant (a rollback of a fix, or the same class of defect in a new place) must be caught as a new "
             "signature of the expected rule with a reproducing repro; a **fix** mutant (an emulated correction of a known "
             "defect) must remove that defect without any new finding; a **doc** mutant (a change the documents of the tree "
             "describe) must come out as documented without a defect; a **regression** mutant must come out as a regression "
             "only. n/a: the mutant is meant for another build, or for trees of another contract version (its `Since`/`Until` "
             "range: the code it changes is another version's; listed below). A mutant within its range whose patch does not "
             "apply fails (not-applicable): no class of defects drops out of the self-test silently.")
    L.append("")
    L.append("**Result: %s.**" % ("every self-test passed" if doc["ok"] else "FAILED"))
    L.append("")
    L.append("| module | build | result | bug caught | fix accepted | doc accepted | regression classified | time, s | report | note |")
    L.append("|---|---|---|---|---|---|---|---|---|---|")
    for r in doc["rows"]:
        k = r.get("kinds") or {}
        L.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            r["module"], r["cfg"], r["result"], frac(k.get("bug")), frac(k.get("fix")), frac(k.get("doc")), frac(k.get("regression")),
            int(r["seconds"]) if num(r.get("seconds")) is not None else "",
            link(r["report"], REPORTS, "selftest.md") if r.get("report") and os.path.exists(os.path.join(STAND, r["report"])) else "",
            md(r.get("note") or "") + ((" " + link(r["log"], REPORTS, "log")) if r.get("log") else "")))
    skipped = [r for r in doc["rows"] if r.get("skippedByVersion")]
    if skipped:
        L.append("")
        L.append("### Skipped by contract version")
        L.append("")
        L.append("Mutants whose `Since`/`Until` range excludes this tree (the code they change belongs to another version; "
                 "a twin cut for this version's code covers the same class where the class still exists):")
        L.append("")
        for r in skipped:
            L.append("- %s, %s%s: %s" % (r["module"], r["cfg"], " (tree %s)" % r["contractVersion"] if r.get("contractVersion") else "",
                                         ", ".join(r["skippedByVersion"])))
    for r in doc["rows"]:
        if not r.get("mutants"):
            continue
        L.append("")
        L.append("## %s, %s" % (r["module"], r["cfg"]))
        L.append("")
        L.append("| mutant | kind | status | detail |")
        L.append("|---|---|---|---|")
        for m in r["mutants"]:
            st = m["status"] if m["verdict"] != "fail" else "**%s**" % m["status"]
            L.append("| %s | %s | %s | %s |" % (md(m["name"]), md(m["kind"]), st, short(m["detail"] or m["why"], 220)))
    L.append("")
    return "\n".join(L)


# ----------------------------------------------------------------------------- CATALOG.md

def cmd_catalog_md(a):
    catalog = load_json(CATALOG)
    rel = load_json(os.path.join(REPORTS, "summary-%s.json" % a.release))
    cur = load_json(os.path.join(REPORTS, "summary-%s.json" % a.current))
    if not rel or not cur:
        sys.exit("summary: summary-%s.json and summary-%s.json are needed (run Stand/run.sh first)" % (a.release, a.current))
    for s, label in ((rel, a.release), (cur, a.current)):
        if s.get("format", 1) < FORMAT:
            sys.exit("summary: summary-%s.json is of an older format (rebuild it with Stand/run.sh --summary-only)" % label)
    rmap = {e["id"]: e for e in rel["catalog"]["entries"]}
    cmap = {e["id"]: e for e in cur["catalog"]["entries"]}
    cur_name = a.current_name or ("working copy" if a.current == "wt" else a.current)
    L = []
    L.append("# Known problems of KenseiECS %s" % a.release.lstrip("v"))
    L.append("")
    L.append("One row per problem: what it is, where it is known from, which module of the stand catches it, what the "
             "stand sees on %s and on the %s, and where the repro is. How the statuses come about: "
             "[README.md](README.md#statuses)." % (a.release, cur_name))
    L.append("")
    cmd = "python3 -I Stand/summary/summary.py catalog-md" + ("" if a.release == "v2.2.0" else " --release " + a.release) + \
          ("" if a.current == "wt" else " --current " + a.current) + \
          (" --current-name '%s'" % a.current_name if a.current_name else "") + (" --also " + a.also if a.also else "")
    L.append("Generated by `%s` from `Stand/summary/catalog.json` and the summaries `Stand/out/reports/summary-*.json`. "
             "Edit catalog.json, not this file." % cmd)
    L.append("")
    for name, s in ((a.release, rel), ("%s (%s)" % (cur_name, a.current), cur)):
        t = s.get("tree") or {}
        src = s.get("src") or ""
        if os.path.isabs(src):
            # A tree outside this checkout: its folder name only (the page goes into git, the machine's paths do not).
            src = os.path.join(os.path.basename(os.path.dirname(src)), os.path.basename(src)) + "`, a tree outside this checkout"
        L.append("- %s: summary of %s, mode %s, `%s%s%s." % (
            name, s.get("generated"), s.get("mode"), src if src.endswith("checkout") else src + "`",
            (", commit %s" % t["commit"][:12]) if t.get("commit") else "",
            (" (%s)" % t["note"]) if t.get("note") and t.get("note") != "tag" and not t["note"].startswith("extracted") else ""))
    L.append("")
    L.append("Statuses: " + "; ".join("**%s**: %s" % (k, v) for k, v in STATUS_HELP) +
             ". After the colon: the builds where it shows (rel: release, dbg: KENSEI_DEBUG, ci: `alloc --ci-parity`); "
             "n/a: it cannot occur in that build.")
    L.append("")
    L.append("Repro: a permanent corpus entry of the module (`Stand/<module>/...`) where there is one, then the repro of "
             "the latest run under `Stand/out/reports/` (regenerated by every run, not in git).")
    must = sorted({e["mustCatchOn"] for e in catalog["entries"] if e.get("mustCatchOn")})
    if must:
        L.append("")
        L.append("An id marked \u2020 must be caught on a %s tree (catalog field `mustCatchOn`: the problems the game project and "
                 "the Windows review found there): %s." % (", ".join(must), MUST_CATCH_RULE))
    groups = {g["id"]: g["title"] for g in catalog["groups"]}
    for gid, gtitle in groups.items():
        ids = [e["id"] for e in catalog["entries"] if e["group"] == gid]
        if not ids:
            continue
        L.append("")
        L.append("## %s" % gtitle)
        L.append("")
        L.append("| id | problem | source | module | %s | %s | repro |" % (a.release, cur_name))
        L.append("|---|---|---|---|---|---|---|")
        for i in ids:
            e = next(x for x in catalog["entries"] if x["id"] == i)
            r, c = rmap.get(i), cmap.get(i)
            repro = []
            for p in (r or {}).get("corpus") or []:
                if p.startswith("fuzz-core/") and len(repro) < 2:
                    repro.append(link(p, STAND, os.path.basename(p)))
            if not repro and r:
                repro += ["`%s`" % md(p) for p in (r.get("corpus") or [])[:1]]
            for p in ((r or {}).get("repro") or (c or {}).get("repro") or [])[:1]:
                repro.append(link(p, STAND, os.path.basename(p)))
            if not repro:
                for ref in e.get("refs") or []:
                    if ref.startswith("out/") and os.path.exists(os.path.join(STAND, ref)):
                        repro.append(link(ref, STAND, os.path.basename(ref)))
                    elif not ref.startswith("out/"):
                        repro.append("`%s`" % md(ref))
            parts = [e["title"].rstrip(".")]
            if e.get("example"):
                parts.append("Example: `%s`" % e["example"].replace("`", "'"))
            if e.get("note"):
                parts.append(e["note"].rstrip("."))
            if e.get("fixedIn") and "fixed in" not in (e.get("note") or "").lower():
                parts.append("Fixed in %s" % e["fixedIn"])
            desc = ". ".join(parts) + "."
            L.append("| %s | %s | %s | %s | %s | %s | %s |" % (
                i + (" \u2020" if e.get("mustCatchOn") else ""), md(desc), md(e.get("source")), md(e.get("module")),
                md(status_text(r)), md(status_text(c)), "<br>".join(repro[:3])))
    L.append("")
    L.append("## Totals")
    L.append("")
    for name, s in ((a.release, rel), (cur_name, cur)):
        cnt = {}
        for e in s["catalog"]["entries"]:
            cnt[e["status"]] = cnt.get(e["status"], 0) + 1
        L.append("- **%s**: of %d entries %s; defects outside the catalog %d, regressions outside the catalog %d." % (
            name, len(s["catalog"]["entries"]), ", ".join("%s %d" % (k, cnt.get(k, 0)) for k in STATUSES),
            len(s.get("uncatalogued") or []), len(s.get("uncataloguedRegressions") or [])))
    for name, s in ((a.release, rel), (cur_name, cur)):
        mc = s.get("mustCatch")
        if mc:
            L.append("- Must-catch entries of %s on the %s: %d of %d caught%s." % (
                mc["release"], name, mc["caught"], mc["total"], (" (missed: %s)" % ", ".join(mc["missed"])) if mc["missed"] else ""))
    fixed = [i for i in rmap if rmap[i]["status"] in OPEN and cmap.get(i, {}).get("status") == "absent"]
    documented = [i for i in rmap if rmap[i]["status"] in OPEN and cmap.get(i, {}).get("status") == "documented"]
    if fixed:
        L.append("- Fixed in the %s: %s." % (cur_name, ", ".join(fixed)))
    if documented:
        L.append("- Documented in the %s (open on %s): %s." % (cur_name, a.release, ", ".join(documented)))
    unchecked = [i for i in cmap if cmap[i]["status"] == "not-run"]
    if unchecked:
        L.append("- Not run on the %s: %s (see the row)." % (cur_name, ", ".join(unchecked)))
    for label in [x for x in (a.also or "").split(",") if x]:
        o = load_json(os.path.join(REPORTS, "summary-%s.json" % label))
        if not o or o.get("format", 1) < FORMAT:
            continue
        omap = {e["id"]: e for e in o["catalog"]["entries"]}
        cnt = {}
        for e in o["catalog"]["entries"]:
            cnt[e["status"]] = cnt.get(e["status"], 0) + 1
        diff = [i for i in omap if i in cmap and omap[i]["status"] != cmap[i]["status"]]
        L.append("- **%s** (summary of %s, mode %s): %s; %s." % (
            label, o.get("generated"), o.get("mode"), ", ".join("%s %d" % (k, cnt.get(k, 0)) for k in STATUSES),
            "the same as the %s for every entry" % cur_name if not diff else "differs from the %s: " % cur_name + ", ".join(
                "%s (%s / %s)" % (i, omap[i]["status"], cmap[i]["status"]) for i in diff)))
    L.append("")
    write_text(os.path.join(STAND, "CATALOG.md"), "\n".join(L))
    print("catalog: %s" % os.path.join(STAND, "CATALOG.md"))
    return 0


def main(argv):
    p = argparse.ArgumentParser(prog="summary.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd")
    b = sub.add_parser("build")
    b.add_argument("--flavor", required=True)
    b.add_argument("--src", required=True)
    b.add_argument("--steps")
    b.add_argument("--run-start", type=int, default=0)
    b.add_argument("--mode", default=None)
    b.add_argument("--command", default=None)
    b.add_argument("--modules", default=",".join(MODULES))
    b.add_argument("--configs", default="rel,dbg")
    b.add_argument("--baseline")
    b.add_argument("--baseline-summary", default=None, help="the baseline summary to compare with (default: summary-<baseline>.json)")
    b.add_argument("--out-name", default=None, help="write summary-<NAME>.md/.json instead of summary-<flavor>")
    b.add_argument("--unity-note", default="")
    s = sub.add_parser("selftest")
    s.add_argument("--flavor", required=True)
    s.add_argument("--src")
    s.add_argument("--steps")
    s.add_argument("--run-start", type=int, default=0)
    s.add_argument("--modules", default=",".join(MODULES))
    s.add_argument("--configs", default="rel,dbg")
    s.add_argument("--command", default=None)
    s.add_argument("--unity-note", default="")
    c = sub.add_parser("catalog-md")
    c.add_argument("--release", default="v2.2.0")
    c.add_argument("--current", default="wt")
    c.add_argument("--current-name", default=None, help="how the --current tree is named in the page (default: working copy for wt, else the label)")
    c.add_argument("--also", default="", help="comma list of other labels whose summaries go into the totals")
    a = p.parse_args(argv)
    if a.cmd == "build":
        return cmd_build(a)
    if a.cmd == "selftest":
        return cmd_selftest(a)
    if a.cmd == "catalog-md":
        return cmd_catalog_md(a)
    p.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
