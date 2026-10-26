#!/usr/bin/env python3
"""Stand/fuzz-core driver (started by run.sh).

1. Replays the corpus (corpus/*.json): minimised scenarios of known defects and contract questions.
2. Explores: worker processes (dotnet FuzzCore.dll --worker --seed N --count C --ops M) run random scenarios
   against the reference model. A worker that dies (stack overflow, abort) or hangs is noticed from outside;
   the seed it was on is recorded and the worker restarts after it. With --scenarios N the seed set is fixed
   (the same N seeds every run), so two trees can be compared seed by seed.
3. Groups failures by signature: rule @ operation [reaction] {mechanism facts}, plus the status (defect,
   question, regression). Signatures that no known family's mechanism matches (NEW) are shrunk first.
4. Shrinks one scenario per signature (delta debugging in a separate process; the family and status are kept,
   so a new bug cannot shrink into a known family's scenario), confirms crashes without the in-process guard.
5. Writes an NUnit repro per signature, compiles them against the same tree and checks that they still fail.
6. Compares with a baseline run of the same module (--baseline): new signatures, new signatures inside known
   families, sharp growth of a signature, and seed by seed when both runs used the same fixed seed set.
7. Writes report.md, report.json and outcomes.json (every failing seed and its signature).
"""
import argparse
import collections
import datetime
import glob
import json
import math
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

ENV = dict(os.environ)
ENV['DOTNET_ROLL_FORWARD'] = 'Major'
ENV['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
ENV['DOTNET_NOLOGO'] = '1'
DOTNET = ENV.get('DOTNET', 'dotnet')

HANG_SECONDS = 8.0          # no output from a worker for this long: it hangs
CHUNK = 150                 # seeds per worker process
# Exploration mix, cycled per chunk: compare mode on the three type layouts, and crash mode
# (uncapped reactions, no guard: a runaway recursion kills the worker).
SCHEDULE = [('compare', 'narrow')] * 5 + [('compare', 'wideA'), ('compare', 'wideB'), ('crash', 'narrow'), ('compare', 'narrow'), ('crash', 'wideA')]

FAMILY_TITLES = {
    'W1': 'Warmup keeps adding default components into the slot after a listener destroyed its temporary entity',
    'W2': 'Re-entrant OnRemoved: mutual removal recurses without end (stack overflow), interleaved removal notifies twice',
    'W3': 'Add/Remove/Create do not notice that a listener destroyed the entity and a new one took the slot: they act on the replacement',
    'W3c': 'Remove does not notice that a listener removed and re-added the component: the new one is removed silently or loses OnRemoved',
    'W4': 'Adding T drops the entity from Exc<T> filters after a filter listener already removed T again',
    'W5': 'EventBuffer copy written back after its component was removed: one pooled list for two owners',
    'W6': 'KENSEI_DEBUG: the stale-handle check is skipped while any entity is being destroyed',
    'N1': 'CopyEntity keeps copying into the copy after a listener destroyed it (components on a free slot or on the slot\'s new entity)',
    'N2': 'Re-entrant Remove resumes the OnRemoved dispatch on a changed listener array: a listener is skipped (or called twice)',
    'N3': 'CopyEntity inside the removal of the last component creates a live entity without components',
    'N4': 'A dying entity enters a filter: OnEntityAdded for an entity that is being destroyed',
    'NEW': 'New signature: no known defect mechanism matches (triage it; it has its own minimal repro)',
}
FAMILY_MECHANISM = {
    'W1': 'fact warmup-temp-died',
    'W2': 'fact reentrant-remove-interleaved with a typed-removed or crash rule, or an OnRemoved at the first difference',
    'W3': 'a fact add-replaced, ref-replaced, remove-replaced, dispatch-replaced, filter-update-replaced or nested-removal-replaced',
    'W3c': 'fact readd-in-removed or nested-removal-readded',
    'W4': 'fact removed-in-add-filter-update with a filter rule or a filter event at the first difference',
    'W5': 'fact writeback-stale with an ownership or content rule',
    'W6': 'fact stale-accepted:free+destroying',
    'N1': 'fact copy-dst-died',
    'N2': 'fact reentrant-remove-listeners-changed with N-missing or N-extra:typed-removed',
    'N3': 'fact copy-src-empty with R-alive-empty',
    'N4': 'rule N-dying-enter',
    'NEW': 'none of the above',
}
FAMILY_SEVERITY = {'W1': 'high', 'W2': 'critical', 'W3': 'high', 'W3c': 'high', 'W4': 'high', 'W5': 'high', 'W6': 'medium',
                   'N1': 'high', 'N2': 'medium', 'N3': 'low', 'N4': 'low', 'NEW': 'medium'}
FAMILY_CONTRACTS = {
    'W1': 'World.Warmup XML doc: the temporary entity gets a default of every type and is destroyed; existing entities are not touched; a freed slot holds nothing (entities.md: alive if and only if it has a component).',
    'W2': 'ComponentPool.Remove XML doc (2.1.1): a listener may remove the same component again from OnRemoved; it is removed once and every listener gets exactly one OnRemoved. The documented semantics terminate; the process must not crash.',
    'W3': 'ComponentPool.Add XML doc and CHANGELOG 2.1.1: once the component is removed before Add returns, the remaining listeners get no OnAdded and the ref points at a scratch value. Remove removes its own entity\'s component; a slot reused by a new entity is another entity.',
    'W3c': 'IComponentListener.OnRemoved runs before each removal; every listener gets exactly one OnRemoved; a component a listener added again is a new component.',
    'W4': 'concepts/filters.md: a filter is the set of entities with every Inc type, none of the Exc types and, if given, one of the Any types; the world keeps it up to date on every structural change.',
    'W5': 'EventBuffer<T>: lists are pooled, AutoReset returns the list on remove; CommandBuffer.Set: a modified copy (sharing the list) can be written back; architecture.md: a copy written back after a remove would otherwise hand a pooled list to two entities.',
    'W6': 'debug-mode.md: under KENSEI_DEBUG, Add/Get/Has/Remove with a dead or stale Entity throw; architecture.md: a dead handle is accepted only when its own slot is mid-destroy; AddEvent XML doc: a stale handle throws.',
    'N1': 'World.CopyEntity XML doc: a new entity with copies of all components, OnEntityCreated after the copy; components go to the copy only.',
    'N2': 'architecture.md: listener lists are copy-on-write, a dispatch loops over the array captured at its start; ComponentPool.Remove: every listener gets exactly one OnRemoved.',
    'N3': 'entities.md, FAQ: an entity is alive if and only if it has at least one component.',
    'N4': 'IFilterListener XML doc, concepts/filters.md: the entity is alive on enter (it may be dying on leave).',
    'NEW': '',
}
CLASS_OF = [('C-', 'crash'), ('X-missing-throw', 'validation'), ('X-nested-missing-throw', 'validation'), ('X-', 'exception'),
            ('N-', 'notification'), ('I-', 'iteration'), ('L-', 'ownership'), ('R-', 'state'), ('S-', 'state')]
EXPECTED = ['W1', 'W2', 'W3', 'W4', 'W5', 'W6']
KNOWN_FAMILIES = ['W1', 'W2', 'W3', 'W3c', 'W4', 'W5', 'W6', 'N1', 'N2', 'N3', 'N4']
# Behaviour the model meets that the documents describe ("D-id: text" from the model, src/ModelSide.cs): the sentence
# the report quotes, looked up in the documents of the tree itself (its XML docs, CHANGELOG.md, README.md and docs/;
# the generated docs/api pages and .vitepress are not read). A tree whose documents do not have the sentence gets the
# question the item replaces and a stand warning (the documents were reworded: update the sentence here).
DOCUMENTED = {
    'D-dying-enter': {
        'question': 'Q-dying-enter', 'source': 'IFilterListener XML doc, docs/concepts/filters.md',
        'quotes': ['a component that a listener adds to an entity whose DestroyEntity is running lets the dying entity (IsAlive false) '
                   'enter the filters it matches',
                   'The one exception on enter is a component that a listener adds to an entity whose `DestroyEntity` is running']},
    'D-warmup-nested-world-events': {
        'question': 'Q-warmup-nested-world-events', 'source': 'docs/guides/world-lifecycle.md',
        'quotes': ['world event listeners do not see their creation: events are suppressed for the whole call']},
    'D-stale-copy': {
        'question': 'Q-stale-copy', 'source': 'EventBuffer XML doc',
        'quotes': ["the copy is stale: its list is back in the pool and may already belong to another entity's buffer, so do not use it any more"]},
}

# Growth of a signature against the baseline that is reported: at least this ratio of hits per scenario and
# this many hits more than the baseline rate predicts.
GROWTH_RATIO = 3.0
GROWTH_MIN = 20


def class_of(rule):
    for p, c in CLASS_OF:
        if rule.startswith(p):
            return c
    return 'other'


def run(cmd, timeout=None):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, env=ENV, timeout=timeout)
        return p.returncode, p.stdout, p.stderr
    except subprocess.TimeoutExpired as e:
        def text(x):
            if x is None:
                return ''
            return x.decode('utf-8', 'replace') if isinstance(x, bytes) else x
        return -999, text(e.stdout), text(e.stderr) + '\n(timeout)'


def result_line(stdout):
    res = None
    for line in stdout.splitlines():
        try:
            d = json.loads(line)
        except ValueError:
            continue
        if d.get('t') == 'result':
            res = d
    return res


def tail(text, n=1800):
    text = text or ''
    return text[-n:]


def crash_kind(stderr, rc, hang):
    if hang:
        return 'hang'
    if 'Stack overflow' in (stderr or ''):
        return 'stack-overflow'
    return 'exit-%s' % rc


def key_of(d):
    return '%s|%s' % (d.get('sig'), d.get('fstatus') or 'defect')


class Driver:
    def __init__(self, a):
        self.a = a
        self.out = a.out
        self.t0 = time.time()
        self.lock = threading.Lock()
        self.stats = collections.Counter()
        self.sigs = collections.OrderedDict()      # key (signature|status) -> smallest failing result (with scenario)
        self.sig_count = collections.Counter()
        self.sig_seeds = collections.defaultdict(list)
        self.crashes = []                          # {seed, mode, layout, rc, kind, stderr}
        self.questions = collections.Counter()
        self.question_text = {}
        self.harness_errors = []
        self.outcomes = {}                         # seed -> key (fails), 'crash:<kind>' (crashes)
        self.ranges = []                           # explored seed ranges [start, end)
        self.events = 0
        self.ops = 0
        self.exclude = [x for x in (a.exclude or '').replace(' ', '').split(',') if x]
        if 'all' in self.exclude or 'known' in self.exclude:
            self.exclude = list(KNOWN_FAMILIES)
        unknown = [x for x in self.exclude if x not in KNOWN_FAMILIES]
        if unknown:
            raise SystemExit('fuzz-core: unknown families in --exclude-families: %s (known: %s, or all)' % (', '.join(unknown), ', '.join(KNOWN_FAMILIES)))
        for d in ('cases', 'min', 'repro', 'logs'):
            os.makedirs(os.path.join(self.out, d), exist_ok=True)

    def log(self, msg):
        print('[fuzz-core %5.1fs] %s' % (time.time() - self.t0, msg), flush=True)

    def common(self):
        c = []
        if self.a.pkg_version:
            c += ['--pkg-version', self.a.pkg_version]
        if self.a.unreleased:
            c.append('--unreleased')
            if self.a.last_release:
                c += ['--last-release', self.a.last_release]
        return c

    def dll(self, *args, timeout=None):
        return run([DOTNET, self.a.bin] + list(args) + self.common(), timeout=timeout)

    # ------------------------------------------------------------------ corpus

    def corpus(self):
        files = sorted(glob.glob(os.path.join(self.a.corpus, '*.json')))

        def one(path):
            with open(path) as f:
                doc = json.load(f)
            configs = doc.get('Configs') or ['rel', 'dbg']
            name = os.path.basename(path)
            entry = {'file': 'corpus/' + name, 'id': doc.get('CorpusId', name), 'family': doc.get('Family'),
                     'expect_rule': doc.get('Rule'), 'expect': doc.get('Expect', 'defect'), 'question': doc.get('Question'),
                     'note': doc.get('Note', ''), 'configs': configs}
            if self.a.cfg not in configs:
                entry['status'] = 'skipped'
                entry['why'] = 'reproduces only in ' + '/'.join(configs)
                return entry
            # "Until": the entry's expectation is the contract of that version and older; a later version changed the
            # contract there (the model follows its documents), and the scenario must pass on it.
            dv = docs_version(self.a)
            until = doc.get('Until')
            superseded = bool(until and dv and vcmp(dv, until) > 0)
            if until:
                entry['until'] = until
            if superseded:
                entry['expect'] = 'pass'
                entry['why'] = 'expectation of %s and older; the documents of %s changed the contract here (%s): it must pass' % (
                    until, dv, doc.get('ChangedIn') or 'see the claims since %s' % dv)
            mode = 'crash' if doc.get('Uncapped') else 'compare'
            rc, so, se = self.dll('--replay', path, '--mode', mode, '--layout', doc.get('Layout', 'narrow'), timeout=120)
            res = result_line(so)
            if res is None:
                entry['status'] = 'crash'
                entry['why'] = crash_kind(se, rc, rc == -999)
                return entry
            entry['rule'] = res.get('rule')
            entry['sig'] = res.get('sig')
            entry['got_family'] = res.get('family')
            entry['got_status'] = res.get('fstatus')
            qids = sorted({q.split(':')[0] for q in res.get('questions') or []})
            entry['questions'] = qids
            if superseded:
                entry['status'] = 'passes' if res['status'] != 'fail' else 'fails'
            elif entry['expect'] == 'question':
                # A contract question kept as a regression test: the scenario must reach that question and pass.
                if res['status'] == 'fail':
                    entry['status'] = 'fails'
                elif entry['question'] in qids:
                    entry['status'] = 'question-met'
                else:
                    entry['status'] = 'question-missing'
            elif res['status'] != 'fail':
                entry['status'] = 'passes'
            elif res.get('family') != doc.get('Family'):
                entry['status'] = 'fails-differently'
            elif (res.get('fstatus') or 'defect') != 'defect':
                entry['status'] = 'reproduces-' + res.get('fstatus')
            else:
                entry['status'] = 'reproduces'
            entry['detail'] = (res.get('detail') or '').split('\n')[0][:300]
            return entry

        with ThreadPoolExecutor(self.workers) as ex:
            entries = list(ex.map(one, files))
        self.corpus_entries = entries
        n = collections.Counter(e['status'] for e in entries)
        self.log('corpus: %d entries, %s' % (len(entries), dict(n)))

    # ------------------------------------------------------------------ exploration

    def chunk(self, idx, deadline):
        mode, layout = SCHEDULE[idx % len(SCHEDULE)]
        seed0 = self.a.seed + idx * CHUNK
        s, end = seed0, seed0 + CHUNK
        if self.a.scenarios:
            end = min(end, self.a.seed + self.a.scenarios)
        start = s
        while s < end and time.time() < deadline:
            cmd = [DOTNET, self.a.bin, '--worker', '--seed', str(s), '--count', str(end - s), '--ops', str(self.a.ops),
                   '--mode', mode, '--layout', layout] + self.common()
            if self.exclude:
                cmd += ['--exclude', ','.join(self.exclude)]
            if mode == 'crash':
                cmd.append('--no-guard')
            p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=ENV)
            err = []
            te = threading.Thread(target=lambda: err.append(p.stderr.read()))
            te.start()
            last = [time.time()]
            hung = [False]

            def watchdog():
                while p.poll() is None:
                    if time.time() - last[0] > HANG_SECONDS:
                        hung[0] = True
                        p.kill()
                        return
                    time.sleep(0.25)
            tw = threading.Thread(target=watchdog)
            tw.start()
            begun = None
            for line in p.stdout:
                last[0] = time.time()
                try:
                    d = json.loads(line)
                except ValueError:
                    continue
                if d.get('t') == 'begin':
                    begun = d['seed']
                elif d.get('t') == 'result':
                    begun = None
                    self.record(d)
                    s = d['seed'] + 1
            p.wait()
            tw.join()
            te.join()
            if begun is None:
                break
            stderr = ''.join(err)
            kind = crash_kind(stderr, p.returncode, hung[0])
            with self.lock:
                self.stats['seeds'] += 1
                self.stats['crashes' if kind != 'hang' else 'hangs'] += 1
                self.crashes.append({'seed': begun, 'mode': mode, 'layout': layout, 'rc': p.returncode, 'kind': kind, 'stderr': tail(stderr)})
                # The way a runaway recursion kills a process varies (a stack overflow message, or SIGBUS on macOS);
                # for the seed comparison a crash is a crash.
                self.outcomes[str(begun)] = 'hang' if kind == 'hang' else 'crash'
            s = begun + 1
        with self.lock:
            if s > start:
                self.ranges.append([start, s])

    def record(self, d):
        with self.lock:
            self.stats['seeds'] += 1
            st = d.get('status')
            self.stats[st] += 1
            self.events += d.get('events') or 0
            self.ops += d.get('ops') or 0
            for q in d.get('questions') or []:
                qid = q.split(':')[0]
                self.questions[qid] += 1
                self.question_text.setdefault(qid, q)
            if st == 'harness-error':
                if len(self.harness_errors) < 20:
                    self.harness_errors.append({'seed': d['seed'], 'mode': d.get('mode'), 'layout': d.get('layout'), 'detail': tail(d.get('detail'), 1500)})
                self.outcomes[str(d['seed'])] = 'harness-error'
                return
            if st != 'fail':
                return
            key = key_of(d)
            self.outcomes[str(d['seed'])] = key
            self.sig_count[key] += 1
            if len(self.sig_seeds[key]) < 5:
                self.sig_seeds[key].append(d['seed'])
            prev = self.sigs.get(key)
            if prev is None or (d.get('ops') or 999) < (prev.get('ops') or 999):
                self.sigs[key] = d

    def explore(self, seconds):
        t_start = time.time()
        deadline = t_start + seconds
        idx = 0
        futures = []
        nchunks = int(math.ceil(self.a.scenarios / float(CHUNK))) if self.a.scenarios else None
        with ThreadPoolExecutor(self.workers) as ex:
            while time.time() < deadline and (nchunks is None or idx < nchunks):
                futures = [f for f in futures if not f.done()]
                while len(futures) < self.workers * 2 and (nchunks is None or idx < nchunks):
                    futures.append(ex.submit(self.chunk, idx, deadline))
                    idx += 1
                time.sleep(0.05)
            for f in futures:
                f.result()
        self.chunks = idx
        self.explored_seconds = time.time() - t_start
        self.partial = bool(self.a.scenarios) and self.stats['seeds'] < self.a.scenarios
        self.log('explore: %d seeds%s, %d fail, %d crashes, %d hangs, %d signatures' % (
            self.stats['seeds'], ' (time cap hit: %d planned)' % self.a.scenarios if self.partial else '',
            self.stats['fail'], self.stats['crashes'], self.stats['hangs'], len(self.sigs)))

    # ------------------------------------------------------------------ shrink, confirm, classify

    def shrink_one(self, item):
        key, kind = item['key'], item['kind']
        case = os.path.join(self.out, 'cases', item['file'] + '.json')
        mn = os.path.join(self.out, 'min', item['file'] + '.json')
        budget_s = item.get('budget', self.shrink_budget)
        if kind == 'fail':
            d = self.sigs[key]
            with open(case, 'w') as f:
                f.write(d['scenario'])
            mode, layout = d.get('mode', 'compare'), d.get('layout', 'narrow')
        else:
            c = item['crash']
            mode, layout = 'crash', c['layout']
            gen = ['--gen', '--seed', str(c['seed']), '--ops', str(self.a.ops), '--crash', '--layout', layout]
            if self.exclude:
                gen += ['--exclude', ','.join(self.exclude)]
            rc, so, se = self.dll(*gen, timeout=60)
            with open(case, 'w') as f:
                f.write(so)
        budget = str(int(budget_s * 1000))
        rc, so, se = self.dll('--shrink', case, '--mode', mode, '--layout', layout, '--out', mn, '--budget-ms', budget, timeout=budget_s + 60)
        res = result_line(so)
        item.update({'case': case, 'mode': mode, 'layout': layout, 'shrink_log': (se or '').strip()[-300:]})
        if res is None or res.get('status') != 'fail':
            # Not reproducible in-process (a crash the guard does not see, or a flaky harness error): keep the original.
            item['min'] = case if kind == 'fail' else None
            item['res'] = self.sigs.get(key) if kind == 'fail' else None
            if kind != 'fail':
                item['res'] = {'status': 'fail', 'rule': 'C-crash', 'sig': 'C-crash@?[top]', 'family': 'NEW', 'fstatus': 'defect',
                               'detail': item['crash']['stderr'][-600:], 'opText': '?', 'op': -1, 'claims': ['terminates']}
            return item
        item['min'] = mn
        item['res'] = res
        if res.get('rule') == 'C-runaway' or kind != 'fail':
            # Confirm the crash: the same scenario without the in-process guard must kill the process.
            rc2, so2, se2 = self.dll('--replay', mn, '--mode', 'crash', '--no-guard', '--layout', layout, timeout=HANG_SECONDS)
            r2 = result_line(so2)
            item['crash_confirmed'] = r2 is None
            item['crash_kind'] = crash_kind(se2, rc2, rc2 == -999) if r2 is None else None
            item['crash_stderr'] = tail(se2, 900) if r2 is None else ''
            # Root cause: the same scenario with capped reactions in compare mode. A crash whose own mechanism
            # matches no family takes the family of the defect that the capped run shows (W1's phantom component
            # makes a later drain endless, for example).
            rc3, so3, se3 = self.dll('--replay', mn, '--mode', 'compare', '--capped', '--layout', layout, timeout=60)
            r3 = result_line(so3)
            if r3 and r3.get('status') == 'fail' and r3.get('family') not in (None, 'NEW', 'U'):
                item['root_family'] = r3.get('family')
                item['root_rule'] = r3.get('rule')
                item['root_sig'] = r3.get('sig')
        return item

    def shrink_all(self):
        items = []
        # New signatures first: they are what a run is for once the known families are saturated.
        def prio(k):
            d = self.sigs[k]
            new = d.get('family') in (None, 'NEW')
            return (0 if new else 1, -self.sig_count[k])
        order = sorted(self.sigs.keys(), key=prio)
        for i, key in enumerate(order[:self.max_sigs]):
            new = self.sigs[key].get('family') in (None, 'NEW')
            items.append({'key': key, 'kind': 'fail', 'file': 'sig-%03d' % (i + 1),
                          'budget': self.shrink_budget if new else self.shrink_budget_known})
        self.unshrunk = order[self.max_sigs:]
        seen = collections.Counter()
        for c in self.crashes:
            k = (c['kind'], c['layout'])
            if seen[k] >= 2:
                continue
            seen[k] += 1
            items.append({'key': 'crash-%d' % c['seed'], 'kind': 'crash', 'crash': c, 'file': 'crash-%d' % c['seed']})
        with ThreadPoolExecutor(self.workers) as ex:
            self.items = list(ex.map(self.shrink_one, items))
        self.log('shrink: %d cases (%d signatures not shrunk: budget)' % (len(self.items), len(self.unshrunk)))

    # ------------------------------------------------------------------ findings

    def findings(self):
        rows = []
        for it in self.items:
            res = it.get('res') or {}
            fam = res.get('family') or 'NEW'
            if fam == 'U':
                fam = 'NEW'
            rule = res.get('rule') or ''
            if rule in ('C-runaway', 'C-crash') and fam == 'NEW' and it.get('root_family'):
                fam = it['root_family']
            sig = res.get('sig') or it['key']
            status = res.get('fstatus') or 'defect'
            if it['kind'] == 'crash':
                count = sum(1 for c in self.crashes if c['kind'] == it['crash']['kind'])
                seeds = [c['seed'] for c in self.crashes if c['kind'] == it['crash']['kind']][:5]
                origin = ['crash:' + it['crash']['kind']]
            else:
                count = self.sig_count[it['key']]
                seeds = list(self.sig_seeds[it['key']])
                origin = [it['key']]
            mn = it.get('min')
            ops = None
            if mn and os.path.exists(mn):
                with open(mn) as f:
                    try:
                        ops = len(json.load(f).get('Ops', []))
                    except ValueError:
                        ops = None
            rows.append({'family': fam, 'rule': rule, 'sig': sig, 'status': status, 'origin': origin, 'count': count, 'seeds': seeds,
                         'mode': it.get('mode'), 'layout': it.get('layout'), 'min': mn, 'case': it.get('case'), 'ops': ops,
                         'detail': (res.get('detail') or ''), 'op_text': res.get('opText'), 'questions': res.get('questions') or [],
                         'facts': res.get('facts') or [], 'mirrors': res.get('mirrors') or [], 'claims': res.get('claims') or [],
                         'inactive': res.get('inactive') or [], 'ctx': res.get('ctx'), 'regression': res.get('regression'),
                         'crash_confirmed': it.get('crash_confirmed'),
                         'crash_kind': it.get('crash_kind') or (it['crash']['kind'] if it['kind'] == 'crash' else None),
                         'crash_stderr': it.get('crash_stderr') or (it['crash']['stderr'] if it['kind'] == 'crash' else ''),
                         'root_rule': it.get('root_rule'), 'root_family': it.get('root_family'), 'shrink_log': it.get('shrink_log')})
        # One row per (family, signature, status): several exploration signatures can shrink to the same minimal one.
        merged = collections.OrderedDict()
        for r in rows:
            k = (r['family'], r['sig'], r['status'])
            m = merged.get(k)
            if m is None:
                merged[k] = r
                continue
            if not (r['origin'][0].startswith('crash:') and r['origin'][0] in m['origin']):
                m['count'] += r['count']
            m['origin'] = m['origin'] + [o for o in r['origin'] if o not in m['origin']]
            m['seeds'] = (m['seeds'] + [s for s in r['seeds'] if s not in m['seeds']])[:5]
            if (r['ops'] or 999) < (m['ops'] or 999):
                for f in ('min', 'case', 'ops', 'layout', 'mode', 'detail', 'op_text'):
                    m[f] = r[f]
            m['crash_confirmed'] = m.get('crash_confirmed') or r.get('crash_confirmed')
        rows = list(merged.values())
        rows.sort(key=lambda r: ({'defect': 0, 'question': 1, 'regression': 2}.get(r['status'], 3), r['family'], -r['count']))
        per = collections.Counter()
        for r in rows:
            if r['status'] == 'defect':
                per[r['family']] += 1
                r['id'] = 'FC-%s-%d' % (r['family'], per[r['family']])
            else:
                per[r['status']] += 1
                r['id'] = '%s-%d' % ('FCQ' if r['status'] == 'question' else 'FCR', per[r['status']])
            r['class'] = class_of(r['rule'])
            r['severity'] = 'critical' if r['rule'].startswith('C-') and r.get('crash_confirmed') else FAMILY_SEVERITY.get(r['family'], 'medium')
        self.rows = rows
        self.key_to_row = {}
        for r in rows:
            for o in r['origin']:
                self.key_to_row[o] = r['id']

    def repros(self):
        def one(r):
            if not r['min'] or not os.path.exists(r['min']):
                r['repro'] = None
                return r
            path = os.path.join(self.out, 'repro', 'repro-%s.cs' % r['id'])
            title = FAMILY_TITLES.get(r['family'], r['family'])
            rc, so, se = self.dll('--repro', r['min'], '--out', path, '--id', r['id'], '--family', r['family'], '--title', title,
                                  '--flavor', self.a.flavor, '--cfg', self.a.cfg, '--case', os.path.relpath(r['min'], self.out),
                                  '--layout', r['layout'] or 'narrow', timeout=120)
            r['repro'] = path if rc == 0 and os.path.exists(path) else None
            if r['repro'] is None:
                r['repro_error'] = (se or so or '').strip()[-300:]
            return r
        with ThreadPoolExecutor(self.workers) as ex:
            self.rows = list(ex.map(one, self.rows))
        self.log('repro: %d written' % sum(1 for r in self.rows if r.get('repro')))

    def verify(self):
        if self.a.verify != 'yes' or not self.a.check_project:
            return
        repro_dir = os.path.join(self.out, 'repro')
        if not glob.glob(os.path.join(repro_dir, 'repro-*.cs')):
            return
        cmd = [DOTNET, 'build', self.a.check_project, '-c', 'Release', '-nologo', '-v', 'q', '-p:ReproDir=' + repro_dir] + self.a.build_prop
        rc, so, se = run(cmd, timeout=600)
        with open(os.path.join(self.out, 'logs', 'repro-check-build.log'), 'w') as f:
            f.write(so + se)
        if rc != 0:
            self.verify_error = 'repro-check build failed (logs/repro-check-build.log)'
            self.log(self.verify_error)
            return
        rc, so, se = run([DOTNET, self.a.check_bin, repro_dir], timeout=180)
        if rc == -999:
            self.verify_error = 'repro-check did not finish in 180 s (a repro hangs); results so far are used'
        status = {}
        for line in so.splitlines():
            m = re.match(r'^(REPRODUCED|NOT REPRODUCED|IGNORED|ERROR) Repro_([A-Za-z0-9_]+)\.', line)
            if m:
                status[m.group(2)] = (m.group(1), line)
        for r in self.rows:
            ident = re.sub(r'[^A-Za-z0-9]', '_', r['id'])
            st = status.get(ident)
            r['verified'] = st[0] if st else None
            r['verify_line'] = st[1][:400] if st else None
        self.log('verify: %s' % dict(collections.Counter(r.get('verified') for r in self.rows)))

    # ------------------------------------------------------------------ corpus candidates

    def corpus_candidates(self):
        cand_dir = os.path.join(self.out, 'corpus-candidates')
        os.makedirs(cand_dir, exist_ok=True)
        known = {(e.get('family'), e.get('expect_rule')) for e in getattr(self, 'corpus_entries', [])}
        self.candidates = []
        for r in self.rows:
            if not r['min'] or not os.path.exists(r['min']) or r['status'] != 'defect':
                continue
            if (r['family'], r['rule']) in known:
                continue
            with open(r['min']) as f:
                doc = json.load(f)
            doc = dict({'CorpusId': r['id'] + '-' + self.a.flavor + '-' + self.a.cfg, 'Family': r['family'], 'Rule': r['rule'],
                        'Configs': [self.a.cfg] if r['family'] == 'W6' or r['rule'].startswith('X-') else ['rel', 'dbg'],
                        'Note': FAMILY_TITLES.get(r['family'], '') + '; ' + r['sig'],
                        'FoundOn': '%s-%s, seed %s, %s' % (self.a.flavor, self.a.cfg, (r['seeds'] or ['?'])[0], r['sig'])}, **doc)
            path = os.path.join(cand_dir, '%s.json' % r['id'])
            with open(path, 'w') as f:
                json.dump(doc, f, indent=2, ensure_ascii=False)
            self.candidates.append(path)
            if self.a.save_corpus:
                shutil.copy(path, os.path.join(self.a.corpus, '%s-%s.json' % (r['family'], re.sub(r'[^A-Za-z0-9]+', '-', r['rule']).strip('-'))))

    # ------------------------------------------------------------------ baseline

    def signatures(self):
        scen = max(1, self.stats['seeds'])
        out = []
        for key, cnt in self.sig_count.most_common():
            sig, status = key.rsplit('|', 1)
            d = self.sigs[key]
            out.append({'signature': sig, 'status': status, 'family': d.get('family') or 'NEW', 'rule': d.get('rule'), 'count': cnt,
                        'per_1000': round(cnt * 1000.0 / scen, 3), 'seeds': self.sig_seeds[key][:5],
                        'shrunk': key not in set(getattr(self, 'unshrunk', [])), 'finding': self.key_to_row.get(key)})
        return out

    def params(self):
        return {'base_seed': self.a.seed, 'ops': self.a.ops, 'scenarios': self.a.scenarios or None, 'chunk': CHUNK,
                'schedule': ['%s/%s' % x for x in SCHEDULE], 'exclude': sorted(self.exclude), 'config': self.a.cfg}

    def baseline(self, signatures):
        if not self.a.baseline:
            return None
        bdir = os.path.join(os.path.dirname(self.out), '%s-%s' % (self.a.baseline, self.a.cfg))
        bpath = os.path.join(bdir, 'report.json')
        if not os.path.isfile(bpath):
            return {'flavor': self.a.baseline, 'note': 'no fuzz-core report for %s-%s: run the module on the baseline first' % (self.a.baseline, self.a.cfg)}
        with open(bpath) as f:
            b = json.load(f)
        bscen = max(1, (b.get('stats') or {}).get('seeds') or 1)
        cscen = max(1, self.stats['seeds'])
        bsigs = {}
        for s in b.get('signatures') or []:
            bsigs[(s['signature'], s.get('status', 'defect'))] = s
        if not bsigs:
            for f in b.get('findings') or []:
                bsigs[(f.get('signature'), f.get('status', 'defect'))] = {'signature': f.get('signature'), 'count': f.get('count', 0),
                                                                          'family': f.get('family'), 'status': f.get('status', 'defect')}
        new, new_in_known, growth, gone = [], [], [], []
        cur = {(s['signature'], s['status']): s for s in signatures}
        for k, s in cur.items():
            bs = bsigs.get(k)
            if bs is None:
                row = {'signature': s['signature'], 'status': s['status'], 'family': s['family'], 'count': s['count'], 'finding': s.get('finding')}
                (new_in_known if s['family'] in KNOWN_FAMILIES else new).append(row)
                continue
            expect = bs.get('count', 0) * cscen / float(bscen)
            if s['count'] >= GROWTH_MIN and s['count'] >= GROWTH_RATIO * max(expect, 1.0) and s['count'] - expect >= GROWTH_MIN:
                growth.append({'signature': s['signature'], 'status': s['status'], 'family': s['family'], 'count': s['count'],
                               'baseline_count': bs.get('count', 0), 'expected': round(expect, 1), 'ratio': round(s['count'] / max(expect, 1.0), 1),
                               'finding': s.get('finding')})
        for k, bs in bsigs.items():
            if k not in cur:
                gone.append({'signature': bs['signature'], 'status': bs.get('status', 'defect'), 'family': bs.get('family'), 'baseline_count': bs.get('count', 0)})
        result = {'flavor': self.a.baseline, 'baseline_package': b.get('package_version'), 'baseline_seeds': bscen, 'seeds': cscen,
                  'new_signatures': sorted(new, key=lambda x: -x['count']),
                  'new_signatures_in_known_families': sorted(new_in_known, key=lambda x: -x['count']),
                  'growth': sorted(growth, key=lambda x: -x['ratio']), 'gone_signatures': sorted(gone, key=lambda x: -x['baseline_count'])}
        # Seed by seed, when both runs explored the same fixed seed set with the same generator settings.
        bout = os.path.join(bdir, 'outcomes.json')
        if os.path.isfile(bout):
            with open(bout) as f:
                bo = json.load(f)
            same = bo.get('params') == self.params() and bo.get('stand') == STAND_VERSION
            result['seed_comparable'] = same
            if same:
                bkeys = bo.get('keys') or []
                bfails = {s: bkeys[i] for s, i in (bo.get('outcomes') or {}).items()}
                both = self.common_seeds(bo.get('ranges') or [])
                newly, changed, fixed = collections.Counter(), collections.Counter(), 0
                for seed in both:
                    k, bk = self.outcomes.get(str(seed)), bfails.get(str(seed))
                    if k == bk:
                        continue
                    if k and not bk:
                        newly[k] += 1
                    elif bk and not k:
                        fixed += 1
                    else:
                        changed[k] += 1
                result['seeds_compared'] = len(both)
                result['seeds_newly_failing'] = [{'key': k, 'count': c, 'finding': self.key_to_row.get(k)} for k, c in newly.most_common(30)]
                result['seeds_failing_differently'] = [{'key': k, 'count': c, 'finding': self.key_to_row.get(k)} for k, c in changed.most_common(30)]
                result['seeds_fixed'] = fixed
            else:
                result['seed_note'] = 'the baseline used another seed set or generator settings (%s): compared by signature only' % (
                    'stand version' if bo.get('stand') != STAND_VERSION else 'parameters')
        return result

    def common_seeds(self, branges):
        def expand(rs):
            s = set()
            for a, b in rs:
                s.update(range(a, b))
            return s
        return sorted(expand(self.ranges) & expand(branges))

    # ------------------------------------------------------------------ report

    def report(self):
        dur = time.time() - self.t0
        defects = [r for r in self.rows if r['status'] == 'defect']
        qrows = [r for r in self.rows if r['status'] == 'question']
        rrows = [r for r in self.rows if r['status'] == 'regression']
        fams = collections.OrderedDict()
        for r in self.rows:
            f = fams.setdefault(r['family'], {'family': r['family'], 'title': FAMILY_TITLES.get(r['family'], r['family']),
                                              'severity': FAMILY_SEVERITY.get(r['family'], 'medium'), 'contract': FAMILY_CONTRACTS.get(r['family'], ''),
                                              'mechanism': FAMILY_MECHANISM.get(r['family'], ''), 'findings': [], 'questions': [],
                                              'regressions': [], 'hits': 0})
            {'defect': f['findings'], 'question': f['questions'], 'regression': f['regressions']}[r['status']].append(r['id'])
            if r['status'] == 'defect':
                f['hits'] += r['count']
        for f in fams.values():
            f['status'] = 'defect' if f['findings'] else ('question' if f['questions'] else 'regression')
        corpus_by_family = collections.defaultdict(list)
        for e in getattr(self, 'corpus_entries', []):
            corpus_by_family[e.get('family')].append(e)
        expected = []
        for w in EXPECTED:
            rows = [r for r in defects if r['family'] == w]
            cor = [e for e in corpus_by_family.get(w, []) if e.get('status') == 'reproduces']
            expected.append({'id': w, 'title': FAMILY_TITLES[w], 'detected_random': bool(rows), 'detected_corpus': bool(cor),
                             'finding_ids': [r['id'] for r in rows], 'hits': sum(r['count'] for r in rows),
                             'question_ids': [r['id'] for r in qrows if r['family'] == w],
                             'corpus': [e['id'] for e in corpus_by_family.get(w, [])],
                             'corpus_status': [e.get('status') for e in corpus_by_family.get(w, [])]})
        signatures = self.signatures()
        inactive = sorted({c for r in self.rows for c in r['inactive']})

        def claims(r):
            return [{'id': c, 'since': (CLAIMS.get(c) or {}).get('since'), 'source': (CLAIMS.get(c) or {}).get('source'),
                     'active': c not in r['inactive']} for c in r['claims']]

        def finding(r):
            return {
                'id': r['id'], 'family': r['family'], 'title': FAMILY_TITLES.get(r['family'], ''), 'class': r['class'], 'rule': r['rule'],
                'signature': r['sig'], 'status': r['status'], 'mechanism': r['facts'], 'context': r['ctx'], 'claims': claims(r),
                'origin_signatures': [o.rsplit('|', 1)[0] for o in r['origin']],
                'inactive_claims': r['inactive'], 'regression_reason': r.get('regression'), 'mirrored_questions': r['mirrors'],
                'severity': r['severity'], 'count': r['count'], 'seeds': r['seeds'], 'mode': r['mode'], 'layout': r['layout'],
                'ops': r['ops'], 'repro': os.path.relpath(r['repro'], self.out) if r.get('repro') else None,
                'case': os.path.relpath(r['min'], self.out) if r.get('min') else None,
                'original_case': os.path.relpath(r['case'], self.out) if r.get('case') else None,
                'verified': r.get('verified'), 'crash_confirmed': r.get('crash_confirmed'), 'crash_kind': r.get('crash_kind'),
                'root_rule': r.get('root_rule'), 'root_family': r.get('root_family'), 'operation': r.get('op_text'),
                'detail': r['detail'][:1500], 'replay': 'run.sh --replay <report dir>/%s%s' % (
                    os.path.relpath(r['min'], self.out) if r.get('min') else '?', ' --mode crash' if r['mode'] == 'crash' else ''),
            }

        questions, documented, warnings = [], [], []
        for k, v in self.questions.most_common():
            if not k.startswith('D-'):
                questions.append({'id': k, 'kind': 'question', 'count': v, 'text': self.question_text[k]})
                continue
            d = DOCUMENTED.get(k) or {}
            quote, where = find_quote(self.a.src, d.get('quotes') or [])
            if quote:
                documented.append({'id': k, 'kind': 'documented', 'count': v, 'text': self.question_text[k], 'quote': quote,
                                   'source': where or d.get('source')})
                continue
            qid = d.get('question') or 'Q-' + k[2:]
            warnings.append('%s: the documents of this tree do not have the sentence it rests on (%s): reported as the question %s; '
                            'update DOCUMENTED in driver.py if they were reworded' % (k, d.get('source') or '?', qid))
            questions.append({'id': qid, 'kind': 'question', 'count': v, 'text': qid + ': ' + self.question_text[k].split(':', 1)[-1].strip()})
        for r in qrows:
            questions.append(dict(finding(r), kind='finding-question',
                                  text='%s rests on promises the documents of %s do not make yet (%s)' % (
                                      r['sig'], self.a.pkg_version or '?', ', '.join(r['inactive']))))
        doc = {
            'module': 'fuzz-core', 'format': 2, 'stand': STAND_VERSION, 'flavor': self.a.flavor, 'config': self.a.cfg,
            'package_version': self.a.pkg_version, 'src': self.a.src, 'mode': self.a.mode,
            'contract': {'package_version': self.a.pkg_version, 'unreleased': bool(self.a.unreleased),
                         'last_release': self.a.last_release or None, 'docs_version': docs_version(self.a),
                         'inactive_claims_met': inactive,
                         'claims_not_yet_made': sorted(c for c, v in CLAIMS.items() if docs_version(self.a) and vcmp(v['since'], docs_version(self.a)) > 0)},
            'started': datetime.datetime.fromtimestamp(self.t0).isoformat(timespec='seconds'),
            'duration_s': round(dur, 1), 'explore_s': round(getattr(self, 'explored_seconds', self.explore_seconds), 1), 'workers': self.workers,
            'ops_per_scenario': self.a.ops,
            'base_seed': self.a.seed, 'scenarios_planned': self.a.scenarios or None, 'partial': getattr(self, 'partial', False),
            'exclude_families': sorted(self.exclude),
            'stats': {'seeds': self.stats['seeds'], 'fail': self.stats['fail'], 'pass': self.stats['pass'], 'discard': self.stats['discard'],
                      'crashes': self.stats['crashes'], 'hangs': self.stats['hangs'], 'harness_errors': self.stats['harness-error'],
                      'ops': self.ops, 'callbacks': self.events, 'signatures': len(self.sigs)},
            'findings': [finding(r) for r in defects],
            'regressions': [finding(r) for r in rrows],
            'families': list(fams.values()),
            'expected': expected,
            'questions': questions,
            'documented': documented,
            'stand_warnings': warnings,
            'signatures': signatures,
            'corpus': getattr(self, 'corpus_entries', []),
            'corpus_candidates': [os.path.relpath(p, self.out) for p in getattr(self, 'candidates', [])],
            'crashes': self.crashes[:20],
            'harness_errors': self.harness_errors,
            'verify_error': getattr(self, 'verify_error', None),
        }
        doc['baseline'] = self.baseline(signatures)
        with open(os.path.join(self.out, 'report.json'), 'w') as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)
        keys = sorted(set(self.outcomes.values()))
        index = {k: i for i, k in enumerate(keys)}
        with open(os.path.join(self.out, 'outcomes.json'), 'w') as f:
            json.dump({'stand': STAND_VERSION, 'params': self.params(), 'ranges': sorted(self.ranges), 'keys': keys,
                       'outcomes': {s: index[k] for s, k in self.outcomes.items()}}, f)
        with open(os.path.join(self.out, 'report.md'), 'w') as f:
            f.write(markdown(doc))
        self.log('report: %s' % os.path.join(self.out, 'report.md'))
        return doc

    def main(self):
        a = self.a
        self.workers = a.workers or max(2, min(8, (os.cpu_count() or 4) - 1))
        quick = a.mode == 'quick'
        total = 115.0 if quick else (a.minutes * 60.0 if a.minutes else 600.0)
        spent_before = a.spent or 0.0
        self.max_sigs = 60 if quick else 120
        self.shrink_budget = 8.0 if quick else 30.0          # signatures of no known family
        self.shrink_budget_known = 4.0 if quick else 15.0    # signatures of a known family
        self.corpus()
        # Leave time for shrinking, repros and their check.
        tail_time = 50.0 if quick else max(90.0, total * 0.25)
        self.explore_seconds = max(10.0, total - spent_before - (time.time() - self.t0) - tail_time)
        if not quick and a.minutes:
            self.explore_seconds = a.minutes * 60.0
        if a.scenarios:
            # A fixed seed set: run all of it, with a generous time cap for slow machines.
            self.explore_seconds = max(self.explore_seconds, 600.0 if quick else self.explore_seconds * 2)
        self.explore(self.explore_seconds)
        self.shrink_all()
        self.findings()
        self.repros()
        self.verify()
        self.corpus_candidates()
        doc = self.report()
        return 1 if (a.fail_on_findings and doc['findings']) else 0


# ---------------------------------------------------------------------- claims (mirrors src/Mechanism.cs)

STAND_VERSION = 'fuzz-core/5'
CLAIMS = {}


def load_claims(src_dir):
    """Reads the claim table from src/Mechanism.cs, so the report can show since-versions without a second copy."""
    path = os.path.join(src_dir, 'Mechanism.cs')
    try:
        text = open(path, encoding='utf-8').read()
    except OSError:
        return
    for m in re.finditer(r'C\("([^"]+)", "([^"]+)", "([^"]*)",\s*"((?:[^"\\]|\\.)*)"\);', text):
        CLAIMS[m.group(1)] = {'since': m.group(2), 'source': m.group(3), 'text': m.group(4)}


_DOC_TEXT = {}
_XML_CREF = re.compile(r'<see(?:also)?\s+(?:cref|langword|href)="([^"]*)"\s*/>')
_XML_NAMEREF = re.compile(r'<(?:paramref|typeparamref)\s+name="([^"]*)"\s*/>')
_XML_TAG = re.compile(r'</?(?:summary|remarks|para|c|code|see|seealso|param|paramref|typeparam|typeparamref|returns|exception|'
                      r'example|list|item|term|description|value|inheritdoc|b|i|br|note)\b[^<>]*>')
_MD_LINK = re.compile(r'\[([^\]]*)\]\([^)]*\)')


def norm_doc(text):
    """Documentation text in a form a sentence can be looked up in: XML doc markup and markdown removed, lower case,
    whitespace collapsed (the same form as Stand/summary/summary.py uses to check documented quotes)."""
    import html
    text = _XML_CREF.sub(lambda m: re.sub(r'\(.*\)', '', re.sub(r'^[A-Z]:', '', m.group(1))).split('.')[-1], text or '')
    text = _XML_NAMEREF.sub(lambda m: m.group(1), text)
    text = _XML_TAG.sub(' ', text)
    text = html.unescape(text)
    text = _MD_LINK.sub(lambda m: m.group(1), text)
    text = text.replace('`', '').replace('**', '')
    return re.sub(r'\s+', ' ', text).strip().lower()


def doc_texts(src):
    """[(where, normalized text)] of the tree's documents: the XML doc comments of the package sources, its CHANGELOG.md
    and README.md, and the *.md of docs/ next to it (the generated API pages and VitePress files left out)."""
    if src in _DOC_TEXT:
        return _DOC_TEXT[src]
    out = []
    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj', 'Samples~')]
        for f in sorted(files):
            if f.endswith('.cs'):
                try:
                    with open(os.path.join(root, f), encoding='utf-8', errors='replace') as fh:
                        lines = [ln.strip()[3:] for ln in fh if ln.strip().startswith('///')]
                except OSError:
                    continue
                if lines:
                    out.append(('XML doc of ' + os.path.relpath(os.path.join(root, f), os.path.dirname(src)), norm_doc(' '.join(lines))))
    for f in ('CHANGELOG.md', 'README.md'):
        p = os.path.join(src, f)
        if os.path.isfile(p):
            out.append(('KenseiECS/' + f, norm_doc(open(p, encoding='utf-8', errors='replace').read())))
    docs = os.path.join(os.path.dirname(src), 'docs')
    for root, dirs, files in os.walk(docs):
        dirs[:] = [d for d in dirs if d not in ('node_modules', '.vitepress')]
        for f in sorted(files):
            p = os.path.join(root, f)
            rel = os.path.relpath(p, docs).replace(os.sep, '/')
            if not f.endswith('.md') or (rel.startswith('api/') and rel != 'api/index.md'):
                continue
            out.append(('docs/' + rel, norm_doc(open(p, encoding='utf-8', errors='replace').read())))
    _DOC_TEXT[src] = out
    return out


def find_quote(src, quotes):
    """(the first sentence of quotes that is in the tree's documents, where), or (None, None)."""
    if not src:
        return None, None
    texts = doc_texts(src)
    for q in quotes:
        n = norm_doc(q)
        for where, text in texts:
            if n in text:
                return q, where
    return None, None


def docs_version(a):
    """The version whose documents the tree has (src/Mechanism.cs, Contract.DocsVersion): the package version, or the next
    patch version when the CHANGELOG has unreleased changes and package.json still names the CHANGELOG's last release (a
    package.json newer than that last release is the release in the making itself)."""
    if not a.pkg_version:
        return None
    if not a.unreleased:
        return a.pkg_version
    if getattr(a, 'last_release', None) and vcmp(a.pkg_version, a.last_release) > 0:
        return a.pkg_version
    x = vparse(a.pkg_version)
    return '%d.%d.%d' % (x[0], x[1], x[2] + 1)


def vparse(v):
    core = re.split(r'[-+]', v or '')[0].split('.')
    out = []
    for i in range(3):
        try:
            out.append(int(core[i]))
        except (IndexError, ValueError):
            out.append(0)
    return out


def vcmp(a, b):
    x, y = vparse(a), vparse(b)
    return (x > y) - (x < y)


# ---------------------------------------------------------------------- markdown

def md_escape(s):
    return (s or '').replace('|', '\\|').replace('\n', ' ')


def finding_table(L, items):
    L.append('| id | signature | hits | seed | layout | ops | repro | check |')
    L.append('|---|---|---|---|---|---|---|---|')
    for f in items:
        ver = f['verified'] or '—'
        if f.get('crash_confirmed'):
            ver += ', process crash confirmed (%s)' % f.get('crash_kind')
        L.append('| %s | `%s` | %d | %s | %s | %s | %s | %s |' % (
            f['id'], md_escape(f['signature']), f['count'], ', '.join(str(s) for s in f['seeds'][:3]), f['layout'],
            f['ops'] if f['ops'] is not None else '?', ('`%s`' % f['repro']) if f['repro'] else '—', ver))
    L.append('')


def markdown(doc):
    L = []
    st = doc['stats']
    L.append('# fuzz-core — %s-%s' % (doc['flavor'], doc['config']))
    L.append('')
    L.append('Model-based random tester of the KenseiECS core. Package `%s` (%s), mode `%s`, %s.' % (
        doc['package_version'] or '?', doc['src'] or '?', doc['mode'], 'KENSEI_DEBUG' if doc['config'] == 'dbg' else 'release'))
    L.append('')
    planned = (' of %d planned (time cap hit)' % doc['scenarios_planned']) if doc.get('partial') else (
        ' (fixed seed set)' if doc.get('scenarios_planned') else ' (time-bounded)')
    L.append('Run: %.0f s (exploration %.0f s, %d workers), %d scenarios%s of %d operations (base seed %d): '
             '%d failed, %d passed, %d discarded (the model does not terminate or the contract is silent), %d process crashes, %d hangs. '
             '%d top-level operations and %d callbacks, %d distinct signatures.' % (
                 doc['duration_s'], doc['explore_s'], doc['workers'], st['seeds'], planned, doc['ops_per_scenario'], doc['base_seed'],
                 st['fail'], st['pass'], st['discard'], st['crashes'], st['hangs'], st['ops'], st['callbacks'], st['signatures']))
    if doc.get('exclude_families'):
        L.append('')
        L.append('Excluded families (the generator avoids their known triggers): %s.' % ', '.join(doc['exclude_families']))
    c = doc.get('contract') or {}
    L.append('')
    L.append('Contract profile: package %s%s, so the documents of %s. Claims made only by later documents (a finding resting on one is a question here, not a defect): %s.' % (
        c.get('package_version') or '?', ' with unreleased changes in its CHANGELOG' if c.get('unreleased') else '', c.get('docs_version') or '?',
        ', '.join(c.get('claims_not_yet_made') or []) or 'none'))
    L.append('')
    L.append('A signature is `rule@operation[reaction]{mechanism facts}`. A family is assigned only when its mechanism facts are present; '
             'anything else is NEW, with its own minimal scenario and repro.')
    L.append('')

    L.append('## Known defect families (review of 2.2.0)')
    L.append('')
    L.append('| | Defect | Random search | Corpus | Findings |')
    L.append('|---|---|---|---|---|')
    for e in doc['expected']:
        cor = ', '.join('%s: %s' % (i, s) for i, s in zip(e['corpus'], e['corpus_status'])) or '—'
        L.append('| %s | %s | %s | %s | %s |' % (e['id'], md_escape(e['title']), ('yes (%d)' % e['hits']) if e['detected_random'] else 'no',
                                               cor, ', '.join(e['finding_ids']) or '—'))
    L.append('')

    new = [f for f in doc['findings'] if f['family'] == 'NEW']
    L.append('## New signatures (no known mechanism)')
    L.append('')
    if not new:
        L.append('None.')
        L.append('')
    else:
        finding_table(L, new)
        for f in new[:10]:
            L.append('- **%s** (`%s`, facts: %s): %s' % (f['id'], f['rule'], ', '.join(f['mechanism']) or 'none', md_escape(f['detail'].split('\n')[0])[:400]))
        L.append('')

    L.append('## Findings by family')
    L.append('')
    if not doc['findings']:
        L.append('No defects.')
        L.append('')
    for fam in doc['families']:
        if fam['family'] == 'NEW' or not fam['findings']:
            continue
        L.append('### %s — %s' % (fam['family'], fam['title']))
        L.append('')
        L.append('Severity estimate: %s. Hits in this run: %d. Mechanism: %s.' % (fam['severity'], fam['hits'], fam['mechanism']))
        L.append('')
        if fam.get('contract'):
            L.append('Contract: %s' % md_escape(fam['contract']))
            L.append('')
        items = [x for x in doc['findings'] if x['family'] == fam['family']]
        finding_table(L, items)
        for f in items[:3]:
            L.append('- **%s**: %s' % (f['id'], md_escape(f['detail'].split('\n')[0])[:400]))
        L.append('')

    b = doc.get('baseline')
    if b:
        L.append('## Against the baseline %s' % b.get('flavor'))
        L.append('')
        if b.get('note'):
            L.append(b['note'])
            L.append('')
        else:
            L.append('Baseline package %s, %d scenarios; this run %d. %s' % (
                b.get('baseline_package') or '?', b.get('baseline_seeds', 0), b.get('seeds', 0),
                ('Seed by seed over %d common seeds: %d newly failing, %d failing differently, %d fixed.' % (
                    b.get('seeds_compared', 0), sum(x['count'] for x in b.get('seeds_newly_failing') or []),
                    sum(x['count'] for x in b.get('seeds_failing_differently') or []), b.get('seeds_fixed', 0)))
                if b.get('seed_comparable') else (b.get('seed_note') or 'No seed-level outcomes for the baseline.')))
            L.append('')
            for title, k in (('New signatures (no known family)', 'new_signatures'),
                             ('New signatures inside known families', 'new_signatures_in_known_families'),
                             ('Sharp growth (at least %.0fx the baseline rate and +%d hits)' % (GROWTH_RATIO, GROWTH_MIN), 'growth'),
                             ('Gone (in the baseline, not in this run)', 'gone_signatures')):
                rows = b.get(k) or []
                L.append('**%s:** %s' % (title, 'none' if not rows else ''))
                for r in rows[:25]:
                    if k == 'growth':
                        L.append('- `%s` (%s, %s): %d hits, baseline %d (expected %.1f, x%.1f)%s' % (
                            md_escape(r['signature']), r['family'], r['status'], r['count'], r['baseline_count'], r['expected'], r['ratio'],
                            (' — ' + r['finding']) if r.get('finding') else ''))
                    elif k == 'gone_signatures':
                        L.append('- `%s` (%s, %s): %d hits in the baseline' % (md_escape(r['signature']), r.get('family'), r['status'], r['baseline_count']))
                    else:
                        L.append('- `%s` (%s, %s): %d hits%s' % (md_escape(r['signature']), r['family'], r['status'], r['count'],
                                                                (' — ' + r['finding']) if r.get('finding') else ''))
                L.append('')

    qf = [q for q in doc['questions'] if q.get('kind') == 'finding-question']
    L.append('## Findings that are contract questions for this version')
    L.append('')
    if not qf:
        L.append('None.')
    else:
        L.append('They rest on a promise that only later documents make (see the contract profile above).')
        L.append('')
        finding_table(L, qf)
        for q in qf:
            L.append('- **%s** (%s): inactive claims %s' % (q['id'], q['family'], ', '.join(q['inactive_claims'])))
    L.append('')

    L.append('## Regressions without a contract violation')
    L.append('')
    if not doc['regressions']:
        L.append('None.')
    else:
        L.append('The world behaves differently from the reference implementation where the contract is silent (or only the order within one kind of callback changed). Triage them; they are not defects.')
        L.append('')
        finding_table(L, doc['regressions'])
        for f in doc['regressions']:
            L.append('- **%s**: %s' % (f['id'], md_escape(f.get('regression_reason') or '')))
    L.append('')

    L.append('## Documented')
    L.append('')
    if not doc.get('documented'):
        L.append('None.')
    else:
        L.append('Behaviour the model met that the documents of this tree describe; the model follows them (a deviation would be a defect).')
        L.append('')
        for d in doc['documented']:
            L.append('- `%s` (%d scenarios): %s. %s: "%s"' % (d['id'], d['count'], md_escape(d['text'].split(':', 1)[-1].strip()), d['source'], md_escape(d['quote'])))
    L.append('')
    if doc.get('stand_warnings'):
        L.append('## Stand warnings')
        L.append('')
        for w in doc['stand_warnings']:
            L.append('- ' + md_escape(w))
        L.append('')

    L.append('## Contract questions')
    L.append('')
    L.append('Behaviour the contract (XML docs, docs/, CHANGELOG) does not describe. These are not findings: the model mirrors the implementation or skips the step.')
    L.append('')
    for q in doc['questions']:
        if q.get('kind') == 'finding-question':
            continue
        L.append('- `%s` (%d scenarios): %s' % (q['id'], q['count'], md_escape(q['text'].split(':', 1)[-1].strip())))
    L.append('')

    unshrunk = [s for s in doc['signatures'] if not s['shrunk']]
    L.append('## All signatures of the exploration')
    L.append('')
    L.append('%d signatures; %d of them were not shrunk in this run (shrink budget: NEW first, then by hits) and have no repro here.' % (
        len(doc['signatures']), len(unshrunk)))
    L.append('')
    L.append('| signature | family | status | hits | per 1000 | finding |')
    L.append('|---|---|---|---|---|---|')
    for s in doc['signatures'][:200]:
        L.append('| `%s` | %s | %s | %d | %.2f | %s |' % (md_escape(s['signature']), s['family'], s['status'], s['count'], s['per_1000'], s.get('finding') or '—'))
    L.append('')

    L.append('## Corpus (regressions)')
    L.append('')
    if not doc['corpus']:
        L.append('The corpus is empty.')
    else:
        L.append('| file | family | status | what the run saw |')
        L.append('|---|---|---|---|')
        for e in doc['corpus']:
            L.append('| `%s` | %s | %s | %s |' % (e['file'], e.get('family'), e.get('status'),
                                                md_escape(e.get('why') or (e.get('sig') or '') + ' ' + (e.get('detail') or ''))[:200]))
    L.append('')
    if doc['corpus_candidates']:
        L.append('Corpus candidates (new minimal scenarios): %s.' % ', '.join('`%s`' % p for p in doc['corpus_candidates']))
        L.append('')
    if doc['crashes']:
        L.append('## Worker crashes')
        L.append('')
        for c in doc['crashes'][:10]:
            L.append('- seed %d (%s, %s): %s, exit code %s' % (c['seed'], c['mode'], c['layout'], c['kind'], c['rc']))
        L.append('')
    if doc['harness_errors']:
        L.append('## Stand errors')
        L.append('')
        for h in doc['harness_errors'][:10]:
            L.append('- seed %s: %s' % (h['seed'], md_escape(h['detail'])[:300]))
        L.append('')
    if doc.get('verify_error'):
        L.append('Repro check: %s' % doc['verify_error'])
        L.append('')
    L.append('## How to reproduce')
    L.append('')
    L.append('- A scenario: `Stand/fuzz-core/run.sh --replay <report dir>/min/<case>.json [--debug] [--src <tree>]` (prints both event logs, the claims and the facts).')
    L.append('- A seed: `dotnet <FuzzCore.dll> --worker --seed N --count 1 --ops %d --mode compare|crash --layout <layout> --pkg-version <version>`, built from the same tree.' % doc['ops_per_scenario'])
    L.append('- Repros: `repro/repro-*.cs` are NUnit tests that compile with `Stand/fuzz-core/kit/ReproKit.cs` and the package sources; `Stand/fuzz-core/repro-check` runs them.')
    L.append('')
    return '\n'.join(L) + '\n'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--bin', required=True)
    ap.add_argument('--check-project')
    ap.add_argument('--check-bin')
    ap.add_argument('--build-prop', action='append', default=[])
    ap.add_argument('--flavor', required=True)
    ap.add_argument('--cfg', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--corpus', required=True)
    ap.add_argument('--mode', default='quick')
    ap.add_argument('--minutes', type=float)
    ap.add_argument('--scenarios', type=int, default=0, help='fixed seed set: run exactly this many scenarios')
    ap.add_argument('--workers', type=int, default=0)
    ap.add_argument('--seed', type=int, default=20261023)
    ap.add_argument('--ops', type=int, default=60)
    ap.add_argument('--src', default='')
    ap.add_argument('--pkg-version', default='')
    ap.add_argument('--unreleased', action='store_true')
    ap.add_argument('--last-release', default='', help='the newest released version the CHANGELOG names (with --unreleased)')
    ap.add_argument('--exclude', default='')
    ap.add_argument('--baseline', default='')
    ap.add_argument('--verify', choices=['yes', 'no'], default='yes')
    ap.add_argument('--save-corpus', action='store_true')
    ap.add_argument('--fail-on-findings', action='store_true')
    ap.add_argument('--spent', type=float, default=0.0, help='seconds already spent (build) in a --quick run')
    a = ap.parse_args()
    load_claims(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'src'))
    sys.exit(Driver(a).main())


if __name__ == '__main__':
    main()
