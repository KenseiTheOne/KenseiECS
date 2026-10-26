#!/usr/bin/env python3
"""Self-test of the fuzz-core module: does it catch injected defects as new signatures, and does it stay quiet on
corrected behaviour?

Each mutant (mutants/<name>.patch, described in mutants/mutants.json) is a unified diff against the KenseiECS
package folder. It is applied to a copy of the tree under test in Stand/out/mutants/fuzz-core/<name>/; the tree
itself is never touched. The module runs on the unmutated copy first (the baseline) and then on every mutant,
all with the same fixed seed set, so their outcomes compare seed by seed.

  bug  killed      the mutant run has a defect of family NEW whose signature the baseline does not have, whose repro
                   reproduces, and whose rule and mechanism are the ones the mutant was written for: the rule starts
                   with one of the "match" rules of mutants.json and the mechanism facts contain that entry's "fact"
                   (a trailing * matches a prefix). KILLED-OTHER: new NEW signatures, but none of the expected
                   mechanism; UNSPECIFIED: the mutant has no "match". absorbed: the new failures all went into known
                   families (the hiding problem this test exists for). survived: nothing new. Only killed passes.
  fix  accepted    the families the fix corrects are gone and no new signature of family NEW appeared.
  regression       a change where the contract is silent: classified when every new signature has the status
                   regression and no new defect appears (the regression status works, no false defect).

A mutant may name the contract versions of the trees whose code it is for ("since", "until" in mutants.json,
"version_note": why). The contract version of the tree is its package.json version, or the next patch when its
CHANGELOG starts with a non-empty [Unreleased] section and package.json is not above the last release there (the
module's own Contract.DocsVersion). A mutant whose range excludes the tree is n/a ("contract version ...") and listed;
within its range a patch that does not apply is not-applicable, a failure: refresh it, or give it an "until" and cut a
twin for the new code. Copies are fresh files (new modification times), so no build of another tree is ever taken for
up to date.

Usage: Stand/fuzz-core/run.sh --selftest [--src <KenseiECS folder>] [--flavor <label>] [--debug] [--scenarios N] [--only a,b]
Report: Stand/out/reports/fuzz-core/selftest-<flavor>-<rel|dbg>/selftest.md and selftest.json
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
MUTANTS = os.path.join(HERE, 'mutants')
# What the module compiles from a package (FuzzCore.csproj) plus what run.sh reads.
COPY = ['Core', 'Systems', 'DevTools', 'package.json', 'CHANGELOG.md']


class PatchError(Exception):
    pass


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
    """The version whose documents the package folder has (src/Mechanism.cs, Contract.DocsVersion): package.json, or the
    next patch when CHANGELOG.md starts with an [Unreleased] section and package.json is not above the CHANGELOG's last
    release (a working copy between releases); a package.json above that release is the release in the making itself."""
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
        last = next((h for h in heads[1:] if re.match(r"\d", h)), None)
        if not last or _vparse(version) <= _vparse(last):
            x = _vparse(version)
            return "%d.%d.%d" % (x[0], x[1], x[2] + 1)
    return version


def version_skip(m, version):
    """None when the mutant is meant for trees of this contract version, else why not (shown as n/a)."""
    if not version:
        return None
    v = _vparse(version)
    why = ""
    if m.get("since") and v < _vparse(m["since"]):
        why = "contract version %s: the mutant is for %s and newer (it changes code of those versions)" % (version, m["since"])
    elif m.get("until") and v > _vparse(m["until"]):
        why = "contract version %s: the mutant is for %s and older (it changes code that later versions rewrote)" % (version, m["until"])
    if why and m.get("version_note"):
        why += "; " + m["version_note"]
    return why or None


def parse_patch(text):
    """Unified diff -> {path: [(old_lines, new_lines), ...]} (paths without the a/ b/ prefix)."""
    files, cur, hunk = {}, None, None
    for line in text.splitlines(True):
        if line.startswith('--- '):
            hunk = None
            continue
        if line.startswith('+++ '):
            path = line[4:].strip().split('\t')[0]
            if path.startswith('b/'):
                path = path[2:]
            cur = files.setdefault(path, [])
            continue
        if line.startswith('@@'):
            hunk = ([], [])
            cur.append(hunk)
            continue
        if hunk is None:
            continue
        if line.startswith('\\'):
            continue
        tag, body = line[:1], line[1:]
        if tag == ' ':
            hunk[0].append(body)
            hunk[1].append(body)
        elif tag == '-':
            hunk[0].append(body)
        elif tag == '+':
            hunk[1].append(body)
    return files


def apply_patch(tree, patch_path):
    """Applies each hunk by its exact text (context included); line numbers are not used, so a patch made on one
    version applies to another as long as that code is the same. A hunk that does not match exactly once fails."""
    with open(patch_path, encoding='utf-8') as f:
        files = parse_patch(f.read())
    if not files:
        raise PatchError('empty patch')
    for rel, hunks in files.items():
        path = os.path.join(tree, rel)
        if not os.path.isfile(path):
            raise PatchError('%s: no such file in the tree' % rel)
        with open(path, encoding='utf-8') as f:
            text = f.read()
        for old, new in hunks:
            o, n = ''.join(old), ''.join(new)
            count = text.count(o)
            if count != 1:
                raise PatchError('%s: a hunk matches %d times (the code differs from the one the patch was made for)' % (rel, count))
            text = text.replace(o, n)
        with open(path, 'w', encoding='utf-8') as f:
            f.write(text)


def make_tree(src, dest):
    if os.path.isdir(dest):
        for root, dirs, files in os.walk(dest):
            os.chmod(root, stat.S_IRWXU)
            for name in files:
                os.chmod(os.path.join(root, name), stat.S_IRUSR | stat.S_IWUSR)
        shutil.rmtree(dest)
    os.makedirs(dest)
    for p in COPY:
        s = os.path.join(src, p)
        if os.path.isdir(s):
            # Fresh modification times: no build of the same label is ever taken for up to date.
            shutil.copytree(s, os.path.join(dest, p), ignore=shutil.ignore_patterns('*.meta'), copy_function=shutil.copy)
        elif os.path.isfile(s):
            shutil.copy(s, os.path.join(dest, p))
    for root, dirs, files in os.walk(dest):
        os.chmod(root, stat.S_IRWXU)
        for name in files:
            os.chmod(os.path.join(root, name), stat.S_IRUSR | stat.S_IWUSR)


def run_module(src, flavor, debug, scenarios, baseline, log):
    args = [os.path.join(HERE, 'run.sh'), '--quick', '--scenarios', str(scenarios), '--src', src, '--flavor', flavor]
    if debug:
        args.append('--debug')
    if baseline:
        args += ['--baseline', baseline]
    with open(log, 'w') as out:
        code = subprocess.call(args, stdout=out, stderr=subprocess.STDOUT)
    report = os.path.join(STAND, 'out', 'reports', 'fuzz-core', '%s-%s' % (flavor, 'dbg' if debug else 'rel'), 'report.json')
    if code != 0 or not os.path.isfile(report):
        return None
    with open(report, encoding='utf-8') as f:
        return json.load(f)


def signature_set(rep):
    # Exploration signatures and the signatures of the minimal scenarios (shrinking can change the reaction or the
    # facts a signature names), with their status.
    sigs = {(s['signature'], s['status']) for s in rep.get('signatures') or []}
    for f in (rep.get('findings') or []) + (rep.get('regressions') or []) + [q for q in rep.get('questions') or [] if q.get('signature')]:
        sigs.add((f['signature'], f.get('status', 'defect')))
    return sigs


def is_new(f, base_sigs):
    status = f.get('status', 'defect')
    if (f['signature'], status) in base_sigs:
        return False
    origins = f.get('origin_signatures') or []
    return not origins or any((o, status) not in base_sigs for o in origins if not o.startswith('crash:'))


def expected(f, alternatives):
    """Whether a finding is the mechanism a bug mutant was written for: a rule prefix and a fact of its mechanism."""
    facts = f.get('mechanism') or []
    for a in alternatives or []:
        if not (f.get('rule') or '').startswith(a['rule']):
            continue
        want = a.get('fact')
        if not want:
            return True
        if want.endswith('*') and any(x.startswith(want[:-1]) for x in facts):
            return True
        if want in facts:
            return True
    return False


def judge(m, base, rep):
    base_sigs = signature_set(base)
    findings = rep.get('findings') or []
    new_rows = [f for f in findings if is_new(f, base_sigs)]
    b = rep.get('baseline') or {}
    seeds = ''
    if b.get('seed_comparable'):
        seeds = '; seeds newly failing: %d, failing differently: %d, fixed: %d' % (
            sum(x['count'] for x in b.get('seeds_newly_failing') or []),
            sum(x['count'] for x in b.get('seeds_failing_differently') or []), b.get('seeds_fixed', 0))
    if m['kind'] == 'bug':
        alternatives = m.get('match')
        verified = [f for f in new_rows if f['family'] == 'NEW' and f.get('verified') == 'REPRODUCED']
        killers = [f for f in verified if expected(f, alternatives)]
        unverified = [f for f in new_rows if f['family'] == 'NEW' and f.get('verified') != 'REPRODUCED']
        absorbed = [f for f in new_rows if f['family'] != 'NEW']
        new_unshrunk = [s for s in (b.get('new_signatures') or [])]
        if not alternatives:
            status = 'UNSPECIFIED'
            detail = 'the mutant has no "match" in mutants.json (rule prefix and fact of the expected mechanism)'
        elif killers:
            status = 'killed'
            detail = 'new signatures of the expected mechanism with a reproducing repro: ' + '; '.join('%s `%s` (%d hits)' % (f['id'], f['signature'], f['count']) for f in killers[:4])
            others = [f for f in verified if f not in killers]
            if others:
                detail += '; other new signatures: ' + ', '.join('`%s`' % f['signature'] for f in others[:3])
        elif verified:
            status = 'KILLED-OTHER'
            detail = 'new signatures, but none of the expected mechanism (%s): ' % '; '.join(
                a['rule'] + ('+' + a['fact'] if a.get('fact') else '') for a in alternatives) + '; '.join(
                '%s `%s` (%d hits)' % (f['id'], f['signature'], f['count']) for f in verified[:4])
        elif unverified:
            status = 'KILLED-NO-REPRO'
            detail = 'new signatures, but no repro reproduces: ' + '; '.join('%s `%s` (%s)' % (f['id'], f['signature'], f.get('verified')) for f in unverified[:4])
        elif absorbed:
            status = 'ABSORBED'
            detail = 'the new failures went into known families: ' + '; '.join('%s `%s`' % (f['id'], f['signature']) for f in absorbed[:4])
        elif new_unshrunk:
            status = 'KILLED-NOT-SHRUNK'
            detail = 'new signatures beyond the shrink budget: ' + '; '.join('`%s` (%s)' % (s['signature'], s['family']) for s in new_unshrunk[:4])
        else:
            status = 'SURVIVED'
            detail = 'no new signature'
        if absorbed and status == 'killed':
            detail += '; also new inside known families: ' + ', '.join('`%s`' % f['signature'] for f in absorbed[:3])
        return status, detail + seeds
    if m['kind'] == 'regression':
        regs = [f for f in (rep.get('regressions') or []) if is_new(f, base_sigs)]
        new_defects = new_rows + [s for s in (b.get('new_signatures') or []) + (b.get('new_signatures_in_known_families') or [])
                                  if s.get('status') == 'defect']
        if regs and not new_defects:
            return 'classified', 'new signatures, all with status regression: ' + '; '.join(
                '%s `%s` (%d hits)' % (f['id'], f['signature'], f['count']) for f in regs[:4]) + seeds
        if new_defects:
            return 'FALSE-DEFECT', 'reported as defects: ' + '; '.join('`%s`' % x['signature'] for x in new_defects[:4]) + seeds
        return 'SURVIVED', 'no new signature' + seeds
    gone_families = m.get('removes') or []
    left = [f for f in findings if f['family'] in gone_families]
    new_new = [f for f in new_rows if f['family'] == 'NEW']
    new_any = [s for s in (b.get('new_signatures') or [])]
    ok = not left and not new_new and not new_any
    detail = ('%s still found: %s' % ('/'.join(gone_families), ', '.join('`%s`' % f['signature'] for f in left[:3])) if left else
              '%s gone' % '/'.join(gone_families))
    if new_new or new_any:
        detail += '; new signatures: ' + ', '.join('`%s`' % x['signature'] for x in (new_new or new_any)[:4])
    newk = b.get('new_signatures_in_known_families') or []
    if newk:
        detail += '; new signatures inside other known families (unmasked by the fix): %d' % len(newk)
    return ('fix-accepted' if ok else 'FIX-REJECTED'), detail + seeds


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default=os.path.join(os.path.dirname(STAND), 'KenseiECS'))
    ap.add_argument('--flavor', default=None)
    ap.add_argument('--debug', action='store_true')
    ap.add_argument('--scenarios', type=int, default=20000)
    ap.add_argument('--only', default=None)
    a = ap.parse_args()
    src = os.path.abspath(a.src)
    if not os.path.isfile(os.path.join(src, 'Core', 'World.cs')):
        if os.path.isfile(os.path.join(src, 'KenseiECS', 'Core', 'World.cs')):
            src = os.path.join(src, 'KenseiECS')
        else:
            print('selftest: %s is not a KenseiECS package folder' % src)
            return 2
    default_flavor = 'wt' if os.path.realpath(src) == os.path.realpath(os.path.join(os.path.dirname(STAND), 'KenseiECS')) else os.path.basename(os.path.dirname(src))
    flavor = a.flavor or default_flavor
    cfg = 'dbg' if a.debug else 'rel'
    outdir = os.path.join(STAND, 'out', 'reports', 'fuzz-core', 'selftest-%s-%s' % (flavor, cfg))
    logdir = os.path.join(outdir, 'logs')
    os.makedirs(logdir, exist_ok=True)
    only = set(a.only.split(',')) if a.only else None
    with open(os.path.join(MUTANTS, 'mutants.json'), encoding='utf-8') as f:
        manifest = json.load(f)

    started = time.time()
    version = contract_version(src)
    print('selftest: %s (%s, contract version %s)' % (src, flavor, version or 'unknown'), flush=True)
    base_flavor = 'st-%s' % flavor
    print('selftest: baseline run on an unmutated copy of %s (%s, %d scenarios)' % (src, cfg, a.scenarios), flush=True)
    base_tree = os.path.join(STAND, 'out', 'mutants', 'fuzz-core', '_base-' + flavor, 'KenseiECS')
    make_tree(src, base_tree)
    base = run_module(base_tree, base_flavor, a.debug, a.scenarios, None, os.path.join(logdir, 'baseline.log'))
    if base is None:
        print('selftest: the baseline run failed, see %s' % os.path.join(logdir, 'baseline.log'))
        return 2
    print('selftest: baseline: %d defects (%s), %d signatures' % (
        len(base.get('findings') or []), ', '.join(sorted({f['family'] for f in base.get('findings') or []})), len(base.get('signatures') or [])), flush=True)

    results = []
    for m in manifest['mutants']:
        if only and m['name'] not in only:
            continue
        row = {'name': m['name'], 'kind': m['kind'], 'origin': m.get('origin'), 'why': m.get('what'), 'expect': m.get('expect'),
               'file': 'mutants/%s.patch' % m['name'] if m['kind'] != 'out-of-scope' else None}
        if m['kind'] == 'out-of-scope':
            row.update(status='n/a', detail=m.get('expect') or 'outside this module')
            results.append(row)
            print('%-32s n/a (outside fuzz-core)' % m['name'], flush=True)
            continue
        row.update(since=m.get('since'), until=m.get('until'))
        skip = version_skip(m, version)
        if skip:
            row.update(status='n/a', detail=skip)
            results.append(row)
            print('%-32s n/a (%s)' % (m['name'], skip), flush=True)
            continue
        if cfg not in (m.get('configs') or ['rel', 'dbg']):
            row.update(status='n/a', detail='the mutation only exists in ' + '/'.join(m['configs']))
            results.append(row)
            print('%-32s n/a in %s' % (m['name'], cfg), flush=True)
            continue
        tree = os.path.join(STAND, 'out', 'mutants', 'fuzz-core', flavor, m['name'], 'KenseiECS')
        make_tree(src, tree)
        try:
            apply_patch(tree, os.path.join(MUTANTS, m['name'] + '.patch'))
        except PatchError as e:
            row.update(status='not-applicable', detail='the patch does not apply within its version range: %s' % e)
            results.append(row)
            print('%-32s not applicable: %s' % (m['name'], e), flush=True)
            continue
        t0 = time.time()
        rep = run_module(tree, 'st-%s-%s' % (flavor, m['name']), a.debug, a.scenarios, base_flavor, os.path.join(logdir, m['name'] + '.log'))
        if rep is None:
            row.update(status='build-or-run-failed', detail='see logs/%s.log' % m['name'])
            results.append(row)
            print('%-32s build/run failed' % m['name'], flush=True)
            continue
        status, detail = judge(m, base, rep)
        row.update(status=status, detail=detail, seconds=round(time.time() - t0, 1),
                   report=os.path.relpath(os.path.join(STAND, 'out', 'reports', 'fuzz-core', 'st-%s-%s-%s' % (flavor, m['name'], cfg), 'report.md'), STAND))
        results.append(row)
        print('%-32s %-14s %s' % (m['name'], status, detail), flush=True)

    # A mutant within its version range that does not apply counts against the self-test, as a bug that was not killed.
    bugs = [r for r in results if r['kind'] == 'bug' and r['status'] != 'n/a']
    killed = sum(1 for r in bugs if r['status'] == 'killed')
    fixes = [r for r in results if r['kind'] == 'fix' and r['status'] != 'n/a']
    accepted = sum(1 for r in fixes if r['status'] == 'fix-accepted')
    regs = [r for r in results if r['kind'] == 'regression' and r['status'] != 'n/a']
    classified = sum(1 for r in regs if r['status'] == 'classified')
    out = {'module': 'fuzz-core', 'src': src, 'flavor': flavor, 'config': cfg, 'scenarios': a.scenarios, 'contractVersion': version,
           'skippedByVersion': [r['name'] for r in results if r['status'] == 'n/a' and 'contract version' in (r.get('detail') or '')],
           'errors': sum(1 for r in results if r['status'] in ('not-applicable', 'build-or-run-failed')),
           'baseline': {'flavor': base_flavor, 'defect_families': sorted({f['family'] for f in base.get('findings') or []}),
                        'signatures': len(base.get('signatures') or [])},
           'killed': killed, 'bugMutants': len(bugs), 'fixAccepted': accepted, 'fixMutants': len(fixes),
           'regressionClassified': classified, 'regressionMutants': len(regs),
           'seconds': round(time.time() - started, 1), 'mutants': results}
    with open(os.path.join(outdir, 'selftest.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    with open(os.path.join(outdir, 'selftest.md'), 'w', encoding='utf-8') as md:
        md.write('# Stand / fuzz-core — self-test (%s-%s)\n\n' % (flavor, cfg))
        md.write('Mutants are copies of `%s` with one patch from `Stand/fuzz-core/mutants/` applied, in `Stand/out/mutants/fuzz-core/`; '
                 'the tree itself is not changed. Every run uses the same %d seeds. ' % (src, a.scenarios))
        md.write('bug: an injected defect; killed when the module reports a new signature of family NEW (not absorbed into a known family) '
                 'with a repro that reproduces, of the expected mechanism: its rule starts with a "match" rule of mutants.json and its facts '
                 'contain that entry\'s fact (KILLED-OTHER, UNSPECIFIED, KILLED-NO-REPRO, ABSORBED and SURVIVED fail). fix: an emulated '
                 'correction of a known family; accepted when that family is gone and no new signature appears (no false positive on '
                 'corrected behaviour).\n\n')
        md.write('regression: a change where the contract is silent; classified when every new signature has the status regression and no '
                 'new defect appears.\n\n')
        md.write('Contract version of the tree: %s; skipped by it (their since/until range): %s.\n\n' % (
            version or 'unknown', ', '.join(out['skippedByVersion']) or 'none'))
        md.write('Baseline: defects in %s; %d signatures. Killed %d of %d bug mutants; accepted %d of %d fix mutants; classified %d of %d '
                 'regression mutants; %.0f s.\n\n' % (
            ', '.join(out['baseline']['defect_families']) or 'none', out['baseline']['signatures'], killed, len(bugs), accepted, len(fixes),
            classified, len(regs), out['seconds']))
        md.write('| mutant | kind | origin | what it changes | result | details |\n|---|---|---|---|---|---|\n')
        for r in results:
            md.write('| %s | %s | %s | %s | %s | %s |\n' % (r['name'], r['kind'], (r.get('origin') or '').replace('|', '\\|'),
                                                      (r.get('why') or '').replace('|', '\\|'), r['status'], (r.get('detail') or '').replace('|', '\\|')))
    print('selftest: %d/%d bug mutants killed, %d/%d fix mutants accepted, %d/%d regression mutants classified -> %s' % (
        killed, len(bugs), accepted, len(fixes), classified, len(regs), os.path.join(outdir, 'selftest.md')))
    return 0 if killed == len(bugs) and accepted == len(fixes) and classified == len(regs) else 1


if __name__ == '__main__':
    sys.exit(main())
