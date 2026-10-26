"""What each scenario is promised, read from the documentation of the tree under test.

Two gates decide whether a measured allocation is a contract violation (a defect):

  1. the corpus: one of the promise's sentences is found in the documentation of the tree itself
     (the XML docs of Core/ and Systems/, the package CHANGELOG.md and README.md, and the
     repository docs/ and README.md, from the tree or from `git archive` of its commit);
  2. the version profile: the promise is known to be documented since a version (`since`), and the
     tree's version (package.json) is at least that. A tree whose CHANGELOG starts with a non-empty
     [Unreleased] section is the next release in the making: what its own docs promise applies.

A measured allocation is
  * a contract violation only against a "zero" rule whose promise passes both gates,
  * documented when a "documented" rule's sentence is in the tree's docs,
  * otherwise a contract question (never a finding): no promise, or a promise that only later
    documentation makes ("promised since 2.2.1, the tree is 2.2.0").
A promise the profile expects (since <= version) but whose sentences are all missing is reported as a
stale rule: the docs were reworded and tools/contracts.py needs the new wording.
"""
import html
import os
import re
import subprocess

import standlib as S

# ---------------------------------------------------------------------------
# promises: id -> sentences (matched after normalisation: lower case, tags and markdown removed)
# `since` is the first version whose documentation is known to contain the promise (the stand
# checked 2.1.1, 2.2.0 and 2.2.1; "2.1.1" means "at least since 2.1.1").
# ---------------------------------------------------------------------------

PROMISES = {}


def promise(pid, since, what, *fragments):
    PROMISES[pid] = {"id": pid, "since": since, "what": what, "fragments": list(fragments)}
    return pid


GENERAL_ZERO = promise(
    "frame-zero", "2.1.1", "a frame allocates nothing once pools, filters and buffers reached their peak",
    "zero allocations at runtime once pools, filters and buffers have grown to their peak capacity",
    "after warm-up a frame allocates nothing; allocations happen only when a pool, filter or buffer doubles its capacity",
    "once pool, filter and buffer capacities have settled, a frame allocates nothing")
RESERVE_ZERO = promise(
    "reserve-zero", "2.2.0", "after Reserve(n, n) creating and destroying n entities that enter filters allocates nothing",
    "after reserve(n, n), creating and destroying up to n entities, each with any of the world's component types and entering any of its filters, allocates nothing",
    "after world.reserve(n, n), creating and destroying up to n entities with any of the world's components, entering any of its filters, allocates nothing",
    "after reserve(n, n), creating and destroying n entities with components that enter filters allocates nothing")
POOL_OBJECT = promise(
    "pool-object", "2.2.0", "creating a pool or filter allocates the object itself",
    "creating a pool or filter still allocates the object itself",
    "creating a pool or a filter still allocates the object itself")
POOL_IN_FRAME_DOCUMENTED = promise(
    "pool-first-add-in-frame", "2.2.1", "a pool first created by an Add during play allocates there, Reserve or not",
    "a pool that first appears with its first add during play allocates there")
POOL_NAME_UNWATCHED = promise(
    "pool-name-unwatched", "2.2.1", "KENSEI_DEBUG: a pool builds its type name only for a growth that Reserve watches",
    "the name is now built only for a growth that reserve watches")
COMMANDBUFFER_ZERO = promise(
    "commandbuffer-zero", "2.1.1", "after the first frame a CommandBuffer allocates nothing",
    "after the first frame the buffer allocates nothing", "after warmup the buffer allocates nothing")
FILTER_ZERO = promise(
    "filter-iteration-zero", "2.1.1", "filter iteration allocates nothing",
    "iteration is zero-allocation via struct enumerator", "get enumerator for foreach. no heap allocation")
ALIVE_ZERO = promise(
    "alive-entities-zero", "2.1.1", "enumerating AliveEntities allocates nothing",
    "enumerate all alive entities. zero-allocation")
EVENTS_ZERO = promise(
    "eventbuffer-zero", "2.1.1", "a OneFrame<EventBuffer<T>> frame allocates nothing once the list pool reached the peak",
    "once the pool holds as many lists as are alive at the peak, a oneframe<eventbuffer<t>> registration allocates nothing")
EVENTS_NO_PREWARM_DOCUMENTED = promise(
    "eventbuffer-first-peak", "2.1.1", "without Prewarm the first peak frame creates the lists",
    "once the pool holds as many lists as are alive at the peak",
    "allocates nothing only once its static, process-wide list pool has reached the peak")
PREWARM_ZERO = promise(
    "prewarm-zero", "2.2.0", "after EventBuffer.Prewarm the first peak frame allocates nothing",
    "entities can hold a buffer of up to capacity events each without allocating",
    "with the pool prewarmed for the peak, a oneframe<eventbuffer<t>> frame allocates nothing")
INVOKE_STATIC_ZERO = promise(
    "invoke-static-zero", "2.2.0", "Listeners.Invoke with TArg and a static lambda allocates nothing",
    "allocates nothing: the state travels in arg instead of a closure",
    "with the targ overload and a static lambda a dispatch allocates nothing")
INVOKE_ONERROR_ZERO = promise(
    "invoke-onerror-zero", "2.2.0", "static lambdas for call and onError allocate nothing",
    "static lambdas for both call and onerror allocate nothing")
INVOKE_CAPTURING_DOCUMENTED = promise(
    "invoke-capturing", "2.2.0", "a capturing lambda allocates a closure per call",
    "a capturing lambda allocates a closure per call", "a lambda that captures variables allocates a closure per call")
INVOKE_METHOD_GROUP_DOCUMENTED = promise(
    "invoke-method-group", None, "a method group allocates a delegate per call (not documented so far)",
    "a method group allocates", "method groups allocate", "method group allocates a delegate", "method group creates a delegate")
LISTENERS_DROP_DOCUMENTED = promise(
    "listeners-drop", "2.1.1", "removing Listeners drops the list",
    "removing the component (or destroying the entity) clears the list and drops it")
LISTENERS_COPY_DOCUMENTED = promise(
    "listeners-copy", "2.1.1", "CopyEntity gives the copy its own Listeners list",
    "copyentity gives the copy its own list", "give the copy its own list with the same listeners")
AUTORESET_ZERO = promise(
    "autoreset-zero", "2.1.1", "IAutoReset: zero allocations per Remove",
    "zero allocations per remove")
WRITEBACK_PATTERN = promise(
    "eventbuffer-writeback", "2.1.1", "a modified EventBuffer copy can be written back (cb.Set)",
    "a modified copy of the component (sharing its eventbuffer list) can be written back",
    "writing back a modified copy")
PARALLEL_ZERO = promise(
    "parallel-zero", "2.1.1", "ParallelRunner.Run allocates nothing after the first call for a job type",
    "no allocations per run after the first call for a job type",
    "after the first run for a job type, run allocates nothing")
# Costs a version documents for its own design (a field, an array, an object more): a one-time difference against
# the baseline that is exactly such per-object differences (Probe/Census.cs counts and sizes the objects a first pass
# creates), each with its sentence in the target's docs, is documented, not a regression. See COSTS below.
POOL_FRAME_FIELDS = promise(
    "pool-hook-fields", "2.2.2", "a pool keeps the innermost hook frame of a re-entrant Add/Remove in plain fields (the pool object holds them)",
    "the innermost frame lives in plain fields (_topentity, _topnext, _topremoving, _topdone)")
POOL_HOOK_FRAMES = promise(
    "pool-hook-frames", "2.2.2", "the first AddListener of a pool allocates its hook frames (four entries)",
    "_frames (four entries, allocated by the first addlistener, doubled for deeper nesting)")
LIST_HOLDER = promise(
    "pooled-list-holder", "2.2.2", "the EventBuffer list pool hands out lists that record whether they are free and which entity holds them",
    "the pool hands out a pooledlist<t> that records whether it is free and which entity holds it")
LIST_OWNER = promise(
    "pool-list-owner", "2.2.2", "an EventBuffer pool takes a list owner once",
    "from which its pool takes a pooledlistowner<t> once")
STAGED_GROWTH = promise(
    "staged-growth", "2.2.2", "a growth allocates every new array before it replaces an old one (the entity growth stages its mask rows)",
    "every array is allocated before it replaces the old one", "everything is allocated before it replaces the old storage")
# The element of an EventBuffer<T> pool's data array: the buffer keeps the number of the rent of its list (a field more
# than the list reference, 8 -> 16 B on 64-bit), so every dense data element of such a pool is that much bigger.
EVENTBUFFER_LEASE = promise(
    "eventbuffer-lease", "2.2.2", "an EventBuffer<T> keeps the number of the rent of its pooled list (a field more per buffer)",
    "every rent of a pooled list is numbered and the renting buffer keeps the number")

RESERVE_SPARSE_SIZE = promise(
    "reserve-entities-cost", "2.2.0", "Reserve(entities) grows the sparse arrays of every pool and filter",
    "the sparse arrays of every pool and filter")
FILTER_RESERVE_SIZE = promise(
    "filter-reserve-size", "2.2.0", "Filter.Reserve / World.Reserve(n, n): a filter's dense array holds at least the reserved count",
    "grow the dense entity array so that count matching entities fit without further growth",
    "the dense arrays of every pool (entities, data, change versions) and every filter to at least components")


# A rule: {"kind": zero | documented | question | zero-with-pattern, "promise": id, "pattern": id, "configs": [...]}
#   zero: must not allocate (a violation when the promise passes both gates);
#   documented: an allocation the docs describe; question: allocations are contract questions;
#   zero-with-pattern: must not allocate when the scenario's usage pattern is documented too.
# configs: the builds the rule applies to ("rel", "dbg"; None = both).
def zero(pid, configs=None):
    return {"kind": "zero", "promise": pid, "configs": configs}


def documented(pid):
    return {"kind": "documented", "promise": pid}


def question(pid=None, why=None):
    return {"kind": "question", "promise": pid, "why": why}


def zero_with_pattern(pid, pattern):
    return {"kind": "zero-with-pattern", "promise": pid, "pattern": pattern}


FIRST_CALL = ("the docs neither include nor exclude the first call of a process, which creates static state "
              "(EcsProfiler's event buffer under KENSEI_DEBUG); the package tests warm up before measuring")
BEYOND_ARRAYS = ("the docs allow allocations when a pool or filter grows, but do not say what a growth allocates "
                 "beyond the grown arrays")


def _r(steady=None, first=None, spans=None, first_extra=None):
    """steady: bytes per operation; first: one-time bytes of the first pass; first_extra: one-time
    bytes beyond the arrays the growth itself needs; spans: a long run in geometric spans."""
    return {"steady": steady, "first": first, "spans": spans, "first_extra": first_extra}


RULES = {
    "create_destroy": _r(zero(GENERAL_ZERO)),
    "add_remove.plain": _r(zero(GENERAL_ZERO)),
    "add_remove.tag": _r(zero(GENERAL_ZERO)),
    "add_remove.big": _r(zero(GENERAL_ZERO)),
    "add_remove.managed": _r(zero(GENERAL_ZERO)),
    "add_remove.autoreset": _r([zero(AUTORESET_ZERO), zero(GENERAL_ZERO)]),
    "add_remove.autocopy": _r(zero(GENERAL_ZERO)),
    "add_remove.tracked": _r(zero(GENERAL_ZERO)),
    "add_remove.listener": _r(zero(GENERAL_ZERO)),
    "add_remove.group": _r(zero(GENERAL_ZERO)),
    "add_remove.filtered": _r(zero(GENERAL_ZERO)),
    "add_remove.eventbuffer": _r([zero(EVENTS_ZERO), zero(GENERAL_ZERO)]),
    "add_remove.listeners": _r(documented(LISTENERS_DROP_DOCUMENTED)),
    "events.first_frame_prewarm": _r(zero(EVENTS_ZERO), zero(PREWARM_ZERO)),
    "events.first_frame_no_prewarm": _r(zero(EVENTS_ZERO), documented(EVENTS_NO_PREWARM_DOCUMENTED)),
    "events.stable_frame": _r(zero(EVENTS_ZERO)),
    "events.writeback_frames": _r(spans=zero_with_pattern(EVENTS_ZERO, WRITEBACK_PATTERN)),
    "invoke.static_lambda": _r(zero(INVOKE_STATIC_ZERO)),
    "invoke.static_lambda_onerror": _r(zero(INVOKE_ONERROR_ZERO)),
    "invoke.method_group": _r(documented(INVOKE_METHOD_GROUP_DOCUMENTED)),
    "invoke.capturing_lambda": _r(documented(INVOKE_CAPTURING_DOCUMENTED)),
    "invoke.manual_loop": _r(zero(GENERAL_ZERO)),
    "invoke.delegate_overhead": _r(),
    "invoke.ratio_paired": _r(),
    "runner.inline_groups": _r(zero(GENERAL_ZERO)),
    "runner.named_group": _r(zero(GENERAL_ZERO)),
    "runner.clean_when_disabled": _r(zero(GENERAL_ZERO)),
    "commandbuffer.playback": _r(zero(COMMANDBUFFER_ZERO)),
    "copy_entity.autocopy": _r(zero(GENERAL_ZERO)),
    "copy_entity.eventbuffer": _r([zero(EVENTS_ZERO), zero(GENERAL_ZERO)]),
    "copy_entity.listeners": _r(documented(LISTENERS_COPY_DOCUMENTED)),
    "foreach.filter": _r(zero(FILTER_ZERO)),
    "foreach.nested5": _r(zero(FILTER_ZERO)),
    "foreach.alive_entities": _r(zero(ALIVE_ZERO)),
    # Reads and writes of existing components in a steady frame (frame-zero holds for every build).
    "access.world_get_has": _r(zero(GENERAL_ZERO)),
    "access.modify_mark_changed": _r(zero(GENERAL_ZERO)),
    "access.get_singleton": _r(zero(GENERAL_ZERO)),
    "access.group_data": _r(zero(GENERAL_ZERO)),
    "access.filter_contains": _r(zero(GENERAL_ZERO)),
    "parallel.run": _r(zero(PARALLEL_ZERO)),
    # The first CreateEntity of a process initialises static state (EcsProfiler under KENSEI_DEBUG):
    # the docs neither include nor exclude a process's first call, so it is a question.
    "create.first_entity_of_process": _r(zero(GENERAL_ZERO), question(RESERVE_ZERO, FIRST_CALL)),
    # First growth without Reserve: the arrays are expected; bytes beyond them are a question, and under
    # KENSEI_DEBUG a pool's type name is a violation since the 2.2.1 docs promise it is built only for a
    # growth that Reserve watches.
    "growth.first_n_pools": _r(zero(GENERAL_ZERO), first_extra=[zero(POOL_NAME_UNWATCHED, configs=["dbg"]), question(GENERAL_ZERO, BEYOND_ARRAYS)]),
    "growth.first_n_filters": _r(zero(GENERAL_ZERO), first_extra=[question(GENERAL_ZERO, BEYOND_ARRAYS)]),
    "reserve.create_destroy_n": _r(zero(RESERVE_ZERO), zero(RESERVE_ZERO)),
    "reserve.first_entities_of_process": _r(zero(RESERVE_ZERO), question(RESERVE_ZERO, FIRST_CALL)),
    # Documented since the 2.2.1 docs; before that the docs said only that creating a pool
    # "allocates the object itself" (and that pools created after Reserve start with reserved sizes).
    "reserve.pool_first_add_in_frame": _r(first=documented(POOL_IN_FRAME_DOCUMENTED)),
    "reserve.pool_created_at_load": _r(first=zero(RESERVE_ZERO)),
}

# One-time costs that Probe/Census.cs measures per object a first pass creates, and the promise that documents a
# change of each. "census": the census keys (count, bytes) of the kind; a "staging" cost is computed from the
# growths and the mask words for a tree whose docs make the promise (the census cannot see the temporary array).
COSTS = [
    {"kind": "pool object", "promise": POOL_FRAME_FIELDS, "count": "cost_pools", "bytes": "cost_pool_bytes"},
    {"kind": "hook frames", "promise": POOL_HOOK_FRAMES, "count": "cost_frames", "bytes": "cost_frame_bytes"},
    {"kind": "list owner", "promise": LIST_OWNER, "count": "cost_owners", "bytes": "cost_owner_bytes"},
    {"kind": "pooled list", "promise": LIST_HOLDER, "count": "cost_lists", "bytes": "cost_list_bytes"},
    {"kind": "staged mask rows", "promise": STAGED_GROWTH, "count": "cost_growths", "per": "cost_staging_bytes_per_growth", "staging": True},
    # The component data arrays a pass allocated (Census.cs, cost_data "Type:elements:bytes"): a component type whose
    # element grew costs elements x the bytes it grew; ELEMENT_PROMISES names the sentence for a type, any other growth
    # (or more elements than the baseline allocated) stays unexplained.
    {"kind": "component data", "data": True},
]

# Component types whose element size a version documents (a prefix of Census.TypeKey) -> the promise.
ELEMENT_PROMISES = {"EventBuffer`1[": EVENTBUFFER_LEASE}



# ---------------------------------------------------------------------------
# corpus
# ---------------------------------------------------------------------------

_CREF = re.compile(r'<see\s+cref="([^"]*)"\s*/?>')
_NAMEREF = re.compile(r'<(?:paramref|typeparamref)\s+name="([^"]*)"\s*/?>')
_TAG = re.compile(r"<[^<>\s][^<>]*>")
_MDLINK = re.compile(r"\[([^\]]*)\]\([^)]*\)")


def _cref_text(ref):
    ref = re.sub(r"^[A-Z]:", "", ref)
    ref = re.sub(r"\(.*\)", "", ref)
    ref = ref.replace("{", "<").replace("}", ">")
    return ref.split(".")[-1]


def _plain(text):
    text = text.replace("`", "").replace("**", "")
    return re.sub(r"\s+", " ", text).lower()


def normalize_xml(text):
    """XML doc comment text: cref/paramref become their names, other tags go, entities unescape."""
    text = _CREF.sub(lambda m: _cref_text(m.group(1)), text)
    text = _NAMEREF.sub(lambda m: m.group(1), text)
    text = _TAG.sub(" ", text)
    return _plain(html.unescape(text))


def normalize_md(text):
    """Markdown: links become their text, code spans and bold marks go; generics stay as written."""
    return _plain(_MDLINK.sub(lambda m: m.group(1), text))


def normalize_fragment(text):
    return _plain(text)


def _xml_docs(path):
    lines = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            s = line.strip()
            if s.startswith("///"):
                lines.append(s[3:])
    return "\n".join(lines)


def _docs_from_commit(commit):
    """Repository docs of a commit via git archive (read-only), cached in Stand/out/alloc/contract/<commit>/."""
    dest = os.path.join(S.WORK, "contract", commit[:12])
    marker = os.path.join(dest, ".complete")
    if os.path.exists(marker):
        return dest
    os.makedirs(dest, exist_ok=True)
    archive = S.git(["archive", commit, "docs", "README.md"], binary=True)
    if archive is None:
        return None
    try:
        tar = subprocess.run(["tar", "-x", "-C", dest, "--exclude", "*.png", "--exclude", "*.jpg"],
                             input=archive, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
    except Exception:
        return None
    if tar.returncode != 0:
        return None
    open(marker, "w").close()
    return dest


class Corpus:
    def __init__(self, src, flavor):
        self.src = os.path.abspath(src)
        self.sources = []
        parts = []
        for sub in ("Core", "Systems"):
            for root, _, files in os.walk(os.path.join(self.src, sub)):
                for f in sorted(files):
                    if f.endswith(".cs"):
                        parts.append(normalize_xml(_xml_docs(os.path.join(root, f))))
        self.sources.append(S.rel(os.path.join(self.src, "Core")) + " and Systems (XML docs)")
        for f in ("CHANGELOG.md", "README.md"):
            p = os.path.join(self.src, f)
            if os.path.exists(p):
                parts.append(normalize_md(open(p, encoding="utf-8", errors="replace").read()))
                self.sources.append(S.rel(p))
        tree = os.path.dirname(self.src)
        docs_root = None
        if os.path.isdir(os.path.join(tree, "docs")):
            docs_root = tree
        else:
            commit = S.tree_commit(self.src, flavor)
            if commit:
                docs_root = _docs_from_commit(commit)
        if docs_root:
            p = os.path.join(docs_root, "README.md")
            if os.path.exists(p):
                parts.append(normalize_md(open(p, encoding="utf-8", errors="replace").read()))
                self.sources.append(S.rel(p))
            docs_dir = os.path.join(docs_root, "docs")
            for root, dirs, files in os.walk(docs_dir):
                dirs[:] = [d for d in dirs if d not in ("node_modules", "bin", "obj", ".vitepress")]
                for f in sorted(files):
                    rel = os.path.relpath(os.path.join(root, f), docs_dir).replace(os.sep, "/")
                    # The API pages docs/scripts/gen-api.mjs writes into docs/api/ are not in a clean checkout (only
                    # docs/api/index.md is tracked): the XML docs they come from are read from the sources above.
                    if f.endswith(".md") and not (rel.startswith("api/") and rel != "api/index.md"):
                        parts.append(normalize_md(open(os.path.join(root, f), encoding="utf-8", errors="replace").read()))
            self.sources.append(S.rel(os.path.join(docs_root, "docs")) + " (*.md)")
        else:
            self.sources.append("repository docs/: not available for this tree")
        self.docs_root = docs_root
        self.text = "\n".join(parts)

    def find(self, fragments):
        """The first fragment present in the documentation, or None."""
        for frag in fragments or []:
            if normalize_fragment(frag) in self.text:
                return frag
        return None

    def promise(self, pid):
        return self.find(PROMISES[pid]["fragments"]) if pid else None


# ---------------------------------------------------------------------------
# version profile
# ---------------------------------------------------------------------------

def parse_version(text):
    m = re.match(r"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?", text or "")
    if not m:
        return None
    return (int(m.group(1)), int(m.group(2)), int(m.group(3) or 0))


def _unreleased(src):
    """True when CHANGELOG.md starts its entries with a non-empty [Unreleased] section."""
    try:
        text = open(os.path.join(src, "CHANGELOG.md"), encoding="utf-8", errors="replace").read()
    except OSError:
        return False
    m = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.M | re.S | re.I)
    return bool(m and re.search(r"^\s*[-*] \S", m.group(1), re.M))


class Profile:
    """The contract of one tree: its version (package.json, plus an unreleased CHANGELOG section)
    and, for every promise the rules use, whether its sentences are in the tree's docs and whether
    the version is new enough for it."""

    def __init__(self, src, corpus):
        self.version = S.package_version(src)
        self.parsed = parse_version(self.version)
        self.unreleased = _unreleased(src)
        self.label = (self.version or "unknown") + (" + unreleased changes" if self.unreleased else "")
        self.corpus = corpus
        self._cache = {}

    def state(self, pid):
        if pid in self._cache:
            return self._cache[pid]
        p = PROMISES[pid]
        quote = self.corpus.promise(pid)
        since = parse_version(p["since"]) if p["since"] else None
        if since is None:
            by_version, reason = quote is not None, "no version is known to document it"
        elif self.parsed is None:
            by_version, reason = quote is not None, "the tree has no package.json version: the docs decide"
        elif since <= self.parsed:
            by_version, reason = True, "documented since %s, the tree is %s" % (p["since"], self.label)
        elif self.unreleased and quote is not None:
            by_version, reason = True, "documented since %s; the tree (%s) documents it in its unreleased changes" % (p["since"], self.label)
        else:
            by_version, reason = False, "promised only by the docs of %s and later; the tree is %s" % (p["since"], self.label)
        st = {"id": pid, "what": p["what"], "since": p["since"], "found": quote is not None, "quote": quote,
              "applies": quote is not None and by_version, "by_version": by_version, "reason": reason,
              "stale_rule": quote is None and since is not None and self.parsed is not None and since <= self.parsed}
        self._cache[pid] = st
        return st

    def used_promises(self):
        ids = []
        for rules in RULES.values():
            for key in ("steady", "first", "spans", "first_extra"):
                rs = rules.get(key)
                for r in (rs if isinstance(rs, list) else [rs]):
                    if not r:
                        continue
                    for pid in (r.get("promise"), r.get("pattern")):
                        if pid and pid not in ids:
                            ids.append(pid)
        for pid in [POOL_OBJECT, RESERVE_SPARSE_SIZE, FILTER_RESERVE_SIZE] + [c["promise"] for c in COSTS if c.get("promise")] + list(ELEMENT_PROMISES.values()):
            if pid not in ids:
                ids.append(pid)
        return ids

    def to_json(self):
        states = [self.state(pid) for pid in self.used_promises()]
        return {"package_version": self.version, "unreleased": self.unreleased, "label": self.label,
                "promises": states, "stale_rules": [s["id"] for s in states if s["stale_rule"]]}


def drift(target, baseline):
    """Promises active on the baseline tree that are not on the target, and the other way round."""
    out = {"dropped": [], "added": []}
    if baseline is None:
        return out
    for pid in target.used_promises():
        t, b = target.state(pid), baseline.state(pid)
        if b["applies"] and not t["applies"]:
            out["dropped"].append({"id": pid, "what": t["what"], "baseline_quote": b["quote"], "target_reason": t["reason"]})
        elif t["applies"] and not b["applies"]:
            out["added"].append({"id": pid, "what": t["what"], "quote": t["quote"]})
    return out


def rules_for(sid, key, config):
    """The rules of one scenario and metric that apply to a build ("rel" or "dbg"), most specific first."""
    rs = (RULES.get(sid) or {}).get(key)
    if rs is None:
        return []
    out = []
    for r in (rs if isinstance(rs, list) else [rs]):
        if r.get("configs") and config not in r["configs"]:
            continue
        out.append(r)
    return out


def evaluate(rules, measured, profile):
    """(verdict, basis) for one measured number against the rules of a scenario metric.
    verdict: ok | violation | documented | question | no-rule. The first rule that gives a verdict
    other than "question" wins; otherwise the first question. basis: what decided it."""
    if not rules:
        return ("no-rule", None) if measured else ("ok", None)
    if not measured:
        return "ok", None
    first_question = None
    for rule in rules:
        verdict, basis = _evaluate_one(rule, profile)
        if verdict != "question":
            return verdict, basis
        if first_question is None:
            first_question = (verdict, basis)
    return first_question


def _evaluate_one(rule, profile):
    kind = rule["kind"]
    st = profile.state(rule["promise"]) if rule.get("promise") else None
    basis = {"rule": kind, "promise": rule.get("promise"), "quote": st["quote"] if st else None,
             "since": st["since"] if st else None, "reason": st["reason"] if st else "no promise covers it"}
    if kind == "zero":
        if st and st["applies"]:
            return "violation", basis
        if st and st["found"]:
            basis["reason"] = st["reason"]
        elif st:
            basis["reason"] = "no promise in this tree's docs (%s)" % st["what"]
        return "question", basis
    if kind == "zero-with-pattern":
        pt = profile.state(rule["pattern"])
        basis["pattern_quote"] = pt["quote"]
        if st and st["applies"] and pt["applies"]:
            basis["quote"] = "%s» and «%s" % (st["quote"], pt["quote"])
            return "violation", basis
        basis["reason"] = "the promise or the usage pattern is not documented for this tree"
        return "question", basis
    if kind == "documented":
        if st and st["found"]:
            return "documented", basis
        basis["reason"] = "the docs of this tree do not describe it"
        return "question", basis
    basis["reason"] = rule.get("why") or "the rule leaves it a question for the contract"
    return "question", basis
