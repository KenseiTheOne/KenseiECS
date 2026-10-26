using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace KenseiStand.Generator {
    /// <summary> What one tree did with one program, in a form a run on another tree can read back. </summary>
    public sealed class Digest {
        public string Source;
        /// <summary> Hash of the program model: only the same program is compared. </summary>
        public string Hash;
        public bool Invalid;
        public string RuntimeError;
        public List<DigestUnit> Units = new();
        public List<DigestRun> Runs = new();

        public bool Built => !Invalid && Units.Count > 0 && Units.All(u => u.Built);

        public static Digest Of(string source, string hash, Observation obs) {
            var d = new Digest { Source = source, Hash = hash, Invalid = obs.Invalid, RuntimeError = obs.RuntimeError };
            foreach (var u in obs.Units) {
                var du = new DigestUnit {
                    Asm = u.Asm.ToString(), Built = u.Image != null,
                    Crashed = u.GeneratorException != null || u.GeneratorDiagnostics.Any(x => x.Id == "CS8785"),
                };
                foreach (var x in u.GeneratorDiagnostics.Where(x => x.Severity == "Error")) {
                    du.Errors.Add($"{x.Id}@{x.ClassId ?? x.ClassName ?? "?"}:kecs");
                }
                foreach (var x in u.GeneratorDiagnostics.Where(x => x.Severity != "Error" && x.Id.StartsWith("KECS"))) {
                    du.Warnings.Add($"{x.Id}@{x.ClassId ?? x.ClassName ?? "?"}");
                }
                du.Warnings = du.Warnings.Distinct().ToList();
                foreach (var x in u.Compile.Where(x => x.Severity == "Error")) {
                    du.Errors.Add($"{x.Id}@{x.ClassId ?? x.ClassName ?? "?"}:{(x.InGenerated ? "generated" : "program")}");
                }
                du.Errors = du.Errors.Distinct().ToList();
                d.Units.Add(du);
            }
            foreach (var r in obs.Runs) {
                d.Runs.Add(new DigestRun {
                    Cls = r.ClassId, InitSystem = r.IsInitSystem, Exception = r.Exception, NullAfter = r.NullAfter.ToList(),
                    Events = r.Events.Select(e => new DigestEvent { Tag = e.Tag, Nulls = e.NullFields.ToList() }).ToList(),
                });
            }
            return d;
        }
    }

    public sealed class DigestUnit {
        public string Asm;
        public bool Built;
        public bool Crashed;
        /// <summary> "CS0506@K2:program", "KECS006@K3:kecs", "CS1002@K1:generated". </summary>
        public List<string> Errors = new();
        /// <summary> KECS warnings of the generator ("KECS013@K2"), which do not fail the build. </summary>
        public List<string> Warnings = new();
    }

    public sealed class DigestRun {
        public string Cls;
        public bool InitSystem;
        public string Exception;
        public List<string> NullAfter = new();
        public List<DigestEvent> Events = new();
    }

    public sealed class DigestEvent {
        public string Tag;
        public List<string> Nulls = new();
    }

    /// <summary> The digests of every program of one run (Stand/generator --observe). </summary>
    public sealed class DigestFile {
        public string Module = "generator";
        public int Format = 1;
        public string Flavor;
        public string Config;
        public string Version;
        public string Profile;
        public string Mode;
        public int Seed;
        public int Iterations;
        public string ProgramSet;
        public List<Digest> Programs = new();

        public string Tag => $"{Flavor}-{Config}";
    }

    /// <summary>
    /// Rules that compare the tree under test with another tree, from that tree's digests of the same
    /// programs (the stand generates the same programs from the same seeds on every tree):
    /// <list type="bullet">
    /// <item>R-MIGRATION-ORDER (defect, claim upgrade-order): a program in the 2.1 layout (the derived
    /// class lists IInitSystem and calls base.Init from OnInit) that works on a 2.1 tree, upgraded as the
    /// 2.2 documents say (delete the base.Init line), must show the base the same state here: "The order
    /// stays the same, the base Init runs first". Compared is what every base Init/OnInit/hook sees of
    /// the derived fields, and whether it runs at all.</item>
    /// <item>R-LOST-SIGNAL (regression): the other tree rejected the program with a build error, this one
    /// builds it without a diagnostic and injected fields stay null.</item>
    /// <item>R-REGRESSION-NULL (regression): the other tree filled every injected field of a run, this one
    /// leaves some null without a diagnostic, and no promise is broken (otherwise it is a defect).</item>
    /// </list>
    /// </summary>
    public static class CrossVersion {
        public static string Hash(ProgramModel p) {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Json.Write(p)));
            return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
        }

        public static DigestFile Load(string path) => Json.Read<DigestFile>(File.ReadAllText(path));

        private static string Who(string tag) => tag.Substring(tag.IndexOf(':') + 1);

        private static string What(string tag) => tag.Substring(0, tag.IndexOf(':'));

        /// <summary> Adds the cross-version violations of every sample against <paramref name="other"/>; returns the number of compared programs. </summary>
        public static (int Compared, int Pairs) Apply(List<Sample> samples, DigestFile other, Settings settings, int jobs) {
            var digests = other.Programs.GroupBy(d => d.Source).ToDictionary(g => g.Key, g => g.First());
            bool migration = other.Profile == "2.1" && settings.Profile != "2.1";
            int compared = 0, pairs = 0;
            var found = new ConcurrentDictionary<Sample, List<Violation>>();
            Parallel.ForEach(samples, new ParallelOptions { MaxDegreeOfParallelism = jobs }, s => {
                if (s.Invalid || s.Digest == null || !digests.TryGetValue(s.Source, out var od) || od.Hash != s.Hash || od.Invalid) {
                    return;
                }
                System.Threading.Interlocked.Increment(ref compared);
                var list = new List<Violation>();
                if (migration && MigrationPair(s, od, other, settings, list)) {
                    System.Threading.Interlocked.Increment(ref pairs);
                }
                LostSignal(s, od, other, settings, list);
                NullRegression(s, od, other, settings, list);
                if (list.Count > 0) {
                    found[s] = list;
                }
            });
            foreach (var (s, list) in found) {
                // The versioned profile applies here too: a migration pair rests on the upgrade note.
                Oracle.ApplyClaims(settings.Claims, list);
                foreach (var v in list) {
                    v.Known = KnownIssue.Of(v);
                }
                s.Violations.AddRange(list);
            }
            return (compared, pairs);
        }

        private static bool MigrationPair(Sample s, Digest od, DigestFile other, Settings settings, List<Violation> result) {
            var p = s.Program;
            var from = new Contract(p, "2.1");
            var to = new Contract(p, settings.Profile, settings.Claims);
            var upgraded = p.Classes.Where(c => p.IsActive(c) && c.IsGenerated && c.OnInit && c.OnInitCallsBase && from.ListsInitIface(c)
                && from.For(c).Kind == ExpKind.Works && to.For(c).Kind == ExpKind.Reports && to.For(c).Ids.SequenceEqual(new[] { "KECS012" })).ToList();
            if (upgraded.Count == 0 || !od.Built || od.RuntimeError != null) {
                return false;
            }
            var after = p.Clone();
            after.Title = p.Title + " (upgraded to 2.2: base.Init deleted from OnInit)";
            foreach (var u in upgraded) {
                after.Cls(u.Id).OnInitCallsBase = false;
            }
            var toAfter = new Contract(after, settings.Profile, settings.Claims);
            if (upgraded.Any(u => toAfter.For(after.Cls(u.Id)).Kind != ExpKind.Works)) {
                // The upgrade does not lead to a documented layout here; other rules judge that layout.
                return false;
            }
            Observation obs;
            try {
                obs = Pipeline.Execute(after, settings);
            } catch (Exception) {
                return false;
            }
            if (obs.Invalid || obs.Units.Any(u => u.Image == null) || obs.RuntimeError != null) {
                return false;
            }
            // A defect of the upgraded layout itself (GN3 over a composite interface, say) is reported by the
            // ordinary rules on that layout; the pair only adds what the upgrade changes.
            var ownDefects = Oracle.Check(after, toAfter, obs).Where(v => v.IsDefect).ToList();
            var seen = new HashSet<string>();
            foreach (var ra in obs.Runs) {
                var rb = od.Runs.FirstOrDefault(r => r.Cls == ra.ClassId);
                var leaf = after.Cls(ra.ClassId);
                if (rb == null || leaf == null || rb.Exception != null || ra.Exception != null) {
                    continue;
                }
                var chain = new List<ClassModel> { leaf };
                chain.AddRange(after.Ancestors(leaf));
                foreach (var u in upgraded.Select(x => after.Cls(x.Id)).Where(chain.Contains)) {
                    var bases = new HashSet<string>(after.Ancestors(u).Select(a => a.Id));
                    if (ownDefects.Any(v => v.ClassId == u.Id || (v.ClassId != null && bases.Contains(v.ClassId)) || v.RunId == leaf.Id)) {
                        continue;
                    }
                    var counts = new Dictionary<string, int>();
                    foreach (var ea in ra.Events.Where(e => bases.Contains(Who(e.Tag)))) {
                        counts[ea.Tag] = counts.TryGetValue(ea.Tag, out int k) ? k + 1 : 1;
                        var eb = rb.Events.Where(e => e.Tag == ea.Tag).ElementAtOrDefault(counts[ea.Tag] - 1);
                        if (eb == null) {
                            continue;
                        }
                        var derived = DerivedFields(chain, Who(ea.Tag));
                        var newly = ea.NullFields.Where(derived.Contains).Except(eb.Nulls).ToList();
                        if (newly.Count == 0 || !seen.Add(u.Id + "|" + ea.Tag)) {
                            continue;
                        }
                        var b = after.Cls(Who(ea.Tag));
                        string names = string.Join(", ", newly.Select(n => (after.Cls(n.Split('.')[0])?.Plain ?? "?") + "." + n.Split('.')[1]));
                        // 2.2.2 promises the 2.1 state ("so the base Init still sees the derived fields"); 2.2.0 and 2.2.1
                        // describe this order themselves and contradict it in the next sentence: a question there.
                        bool promised = settings.Claims == null || settings.Claims.Has("upgrade-order-222");
                        result.Add(new Violation {
                            Rule = "R-MIGRATION-ORDER", Class = "upgrade-order", Severity = "medium", ClassId = u.Id, RunId = leaf.Id, Claim = "upgrade-order-222",
                            Key = "MIGRATION|derived-null-in-base", Place = toAfter.For(u).Shape, Against = other.Tag, Subject = after,
                            Message = $"After the documented upgrade from 2.1 (base.Init deleted from {u.Plain}.OnInit), {b?.Plain}.{What(ea.Tag)} runs while {names} " +
                                      $"{(newly.Count == 1 ? "is" : "are")} still null; on {other.Tag} ({other.Version}) the 2.1 layout of the same program ran it from {u.Plain}.OnInit, " +
                                      "after the derived fields were filled, so the base sees another state",
                            Doc = promised
                                ? "docs/guides/source-generator.md 2.2.2, Upgrading from 2.1: 'The generated Init makes the call after the derived fields and before the derived OnInit, so the base Init still sees the derived fields, as it did when the call stood at the top of OnInit.'"
                                : Claims.UpgradeContradiction220,
                            Evidence = $"{other.Tag}: {Describe(after, rb)} | here: {Renderer.ExpectedReproLine(after, ra)}",
                        });
                    }
                    // A base Init/OnInit/hook that ran in the 2.1 layout must still run after the upgrade.
                    foreach (var g in rb.Events.Where(e => bases.Contains(Who(e.Tag))).GroupBy(e => e.Tag)) {
                        int now = ra.Events.Count(e => e.Tag == g.Key);
                        if (now >= g.Count() || !seen.Add(u.Id + "|gone|" + g.Key)) {
                            continue;
                        }
                        var b = after.Cls(Who(g.Key));
                        result.Add(new Violation {
                            Rule = "R-MIGRATION-ORDER", Class = "upgrade-order", Severity = "high", ClassId = u.Id, RunId = leaf.Id, Claim = "upgrade-delete-line",
                            Key = "MIGRATION|base-no-longer-runs", Place = toAfter.For(u).Shape, Against = other.Tag, Subject = after,
                            Message = $"After the documented upgrade from 2.1 (base.Init deleted from {u.Plain}.OnInit), {b?.Plain}.{What(g.Key)} runs {now} time(s); " +
                                      $"on {other.Tag} ({other.Version}) the 2.1 layout ran it {g.Count()} time(s)",
                            Doc = "docs/guides/source-generator.md, Upgrading from 2.1: 'that call is KECS012: delete the line' (the generated Init makes the call)",
                            Evidence = $"{other.Tag}: {Describe(after, rb)} | here: {Renderer.ExpectedReproLine(after, ra)}",
                        });
                    }
                }
            }
            return true;
        }

        // Injected fields of the classes of the chain below <baseId> (the derived side of that base).
        private static HashSet<string> DerivedFields(List<ClassModel> chain, string baseId) {
            var set = new HashSet<string>();
            foreach (var c in chain) {
                if (c.Id == baseId) {
                    break;
                }
                foreach (var f in c.Fields.Where(f => !f.Static)) {
                    set.Add(c.Id + "." + f.Name.TrimStart('@'));
                }
            }
            return set;
        }

        private static bool DefectOnChain(Sample s, ProgramModel p, string leafId) {
            var leaf = p.Cls(leafId);
            var chain = leaf == null ? new HashSet<string>() : new HashSet<string>(new[] { leaf.Id }.Concat(p.Ancestors(leaf).Select(a => a.Id)));
            return s.Violations.Any(v => v.IsDefect && (v.ClassId == null || chain.Contains(v.ClassId) || (v.RunId != null && chain.Contains(v.RunId))));
        }

        /// <summary>
        /// Why injected fields of a run are null on this tree, from the trace: the class just below the
        /// topmost class with null fields that ran ("breaker") — a hand-written Init that does not reach the
        /// generated Init above it ("drop-" + its Init kind) or a generated Init that does not ("skip-" + its
        /// layout shape) —, fields the generator does not see at all (alias-only classes, the same on every
        /// tree: null), or nothing that ran ("unexplained").
        /// </summary>
        private static string NullCause(ProgramModel p, Contract contract, DigestRun r, out ClassModel breaker) {
            breaker = null;
            var leaf = p.Cls(r.Cls);
            if (leaf == null) {
                return "unexplained";
            }
            var chain = new[] { leaf }.Concat(p.Ancestors(leaf)).ToList();
            var silent = SilentClasses(p, r);
            if (silent.Count == 0) {
                return null;
            }
            int top = silent.Select(c => chain.FindIndex(x => x.Id == c.Id)).Where(i => i >= 0).DefaultIfEmpty(-1).Max();
            for (int i = top - 1; i >= 0; i--) {
                var c = chain[i];
                if (!c.IsGenerated) {
                    if (Contract.IsExactInitMethod(c.Init) && r.Events.Any(e => e.Tag.StartsWith("Init") && Who(e.Tag) == c.Id)) {
                        breaker = c;
                        return "drop-" + c.Init;
                    }
                    continue;
                }
                var own = c.Fields.Where(f => !f.Static).Select(f => c.Id + "." + f.Name.TrimStart('@')).ToList();
                bool ran = own.Any(f => !r.NullAfter.Contains(f)) || r.Events.Any(e => e.Tag == "OnInit:" + c.Id);
                if (ran) {
                    breaker = c;
                    return "skip-" + contract.For(c).Shape;
                }
            }
            return "unexplained";
        }

        /// <summary>
        /// Classes of the run's chain whose injected fields stay null, alias-only classes left out (the
        /// generator never sees them, on any tree). A generated Init that is never called leaves its fields
        /// null too; a hand-written Init the documents chain to and that does not run is a defect of its own
        /// (R-BASE-INIT-SKIPPED), and one they do not chain to (a non-system base) is not expected to run.
        /// </summary>
        private static List<ClassModel> SilentClasses(ProgramModel p, DigestRun r) {
            var leaf = p.Cls(r.Cls);
            var result = new List<ClassModel>();
            if (leaf == null) {
                return result;
            }
            foreach (var c in new[] { leaf }.Concat(p.Ancestors(leaf))) {
                if (c.IsGenerated && !Contract.IsAliasOnly(c) && r.NullAfter.Any(n => n.StartsWith(c.Id + "."))) {
                    result.Add(c);
                }
            }
            return result;
        }

        private static void LostSignal(Sample s, Digest od, DigestFile other, Settings settings, List<Violation> result) {
            var td = s.Digest;
            var failed = od.Units.Where(u => !u.Built && u.Errors.Count > 0).ToList();
            if (failed.Count == 0 || !td.Built || td.RuntimeError != null) {
                return;
            }
            var p = s.Program;
            var contract = new Contract(p, settings.Profile, settings.Claims);
            // The other tree must have rejected the code this tree now accepts: the class that stops the chain
            // (a hand-written override of a generated Init was CS0506 in 2.1), or, when nothing visible stops
            // it, a class between the run's leaf and the class whose fields stay null.
            string ErrorOn(IEnumerable<string> classIds) {
                var ids = classIds.ToList();
                return failed.SelectMany(u => u.Errors)
                    .Select(e => (Error: e, Index: ids.IndexOf(e.Split('@').ElementAtOrDefault(1)?.Split(':')[0] ?? "")))
                    .Where(x => x.Index >= 0).OrderBy(x => x.Index).ThenBy(x => x.Error.EndsWith(":program") ? 0 : 1).Select(x => x.Error).FirstOrDefault();
            }
            foreach (var run in td.Runs.Where(r => r.Exception == null && !DefectOnChain(s, p, r.Cls))) {
                string cause = NullCause(p, contract, run, out var breaker);
                var leaf = p.Cls(run.Cls);
                if (cause == null || leaf == null) {
                    continue;
                }
                var chain = new[] { leaf }.Concat(p.Ancestors(leaf)).ToList();
                var silent = SilentClasses(p, run);
                var silentIds = new HashSet<string>(silent.Select(c => c.Id));
                int top = chain.FindLastIndex(c => silentIds.Contains(c.Id));
                string error = breaker != null ? ErrorOn(new[] { breaker.Id }) : ErrorOn(chain.Take(top + 1).Select(c => c.Id));
                if (error == null) {
                    continue;
                }
                // The signal is not lost when this tree still gives one that does not fail the build: a KECS warning on
                // the class that stops the chain, or on a class of the run between it and the null fields (2.2.2: KECS013
                // on a hand-written override of a generated Init without base.Init).
                var signalled = new HashSet<string>(chain.Take(top + 1).Select(c => c.Id));
                if (breaker != null) {
                    signalled.Add(breaker.Id);
                }
                var warning = td.Units.SelectMany(u => u.Warnings ?? new List<string>()).FirstOrDefault(w => signalled.Contains(w.Split('@').ElementAtOrDefault(1) ?? ""));
                if (warning != null) {
                    continue;
                }
                string id = error.Split('@')[0];
                string where = error.Contains(':') ? error.Substring(error.LastIndexOf(':') + 1) : "?";
                string clsId = error.Split('@').ElementAtOrDefault(1)?.Split(':')[0];
                var errorCls = p.Cls(clsId);
                var cls = breaker ?? errorCls ?? p.Cls(run.Cls);
                var skippedInits = silent.Where(c => !c.IsGenerated).Select(c => c.Plain + ".Init").ToList();
                string nulls = string.Join(", ", run.NullAfter.Select(n => (p.Cls(n.Split('.')[0])?.Plain ?? "?") + "." + n.Split('.')[1]));
                string what = run.NullAfter.Count > 0
                    ? $"{nulls} {(run.NullAfter.Count == 1 ? "stays" : "stay")} null after SystemsRunner.Init" + (skippedInits.Count > 0 ? $" and {string.Join(", ", skippedInits)} {(skippedInits.Count == 1 ? "does" : "do")} not run" : "")
                    : $"{string.Join(", ", skippedInits)} {(skippedInits.Count == 1 ? "does" : "do")} not run in SystemsRunner.Init";
                // 2.2.2 documents the one case its KECS013 does not see: "for a base in another assembly it is recognised
                // only by injected fields visible from the derived assembly, protected or public".
                bool documented = breaker != null && !breaker.IsGenerated && contract.Kecs013Site(breaker) == "documented-miss"
                    && settings.Claims != null && settings.Claims.Has("kecs013-lib-visible");
                result.Add(new Violation {
                    Rule = "R-LOST-SIGNAL", Class = "lost-compiler-signal", Severity = documented ? "low" : "medium", Regression = !documented, Documented = documented,
                    ClassId = cls?.Id, RunId = run.Cls,
                    Key = $"LOSTSIGNAL|{(cause.StartsWith("skip-") ? "skip" : cause)}", Place = cls == null ? "-" : contract.For(cls).Shape + (documented ? ":base-fields-invisible" : ""), Against = other.Tag,
                    Message = $"On {other.Tag} ({other.Version}) this program did not build ({id} in {(where == "kecs" ? "a generator diagnostic" : where == "generated" ? "the generated code" : "the program's own code")} of {errorCls?.Plain ?? clsId}); " +
                              $"on this tree it builds without any diagnostic and {what}" +
                              (breaker != null ? $" ({breaker.Plain}.Init{(breaker.IsGenerated ? "" : ", " + breaker.Init + ",")} runs and does not reach the generated Init above it)" : "") +
                              (documented
                                  ? ". The documents of this tree describe it: KECS013 does not see a generated base in another assembly whose injected fields are all private or internal"
                                  : ". No document of this tree forbids that, but the compiler signal is gone"),
                    Doc = documented
                        ? "docs/guides/source-generator.md 2.2.2, Inheritance: 'which is the warning KECS013 (also syntactic; for a base in another assembly it is recognised only by injected fields visible from the derived assembly, protected or public)'"
                        : $"cross-version: the build error of {other.Tag}",
                    Claim = documented ? "kecs013-lib-visible" : null,
                    Evidence = $"{other.Tag}: {string.Join("; ", failed.Select(u => u.Asm + " fails: " + string.Join(", ", u.Errors.Take(4))))}",
                });
                return;
            }
        }

        private static void NullRegression(Sample s, Digest od, DigestFile other, Settings settings, List<Violation> result) {
            var td = s.Digest;
            if (!od.Built || od.RuntimeError != null || !td.Built || td.RuntimeError != null) {
                return;
            }
            var p = s.Program;
            var contract = new Contract(p, settings.Profile, settings.Claims);
            foreach (var rt in td.Runs.Where(r => r.Exception == null && r.NullAfter.Count > 0)) {
                var rb = od.Runs.FirstOrDefault(r => r.Cls == rt.Cls);
                if (rb == null || rb.Exception != null || rb.NullAfter.Count > 0 || DefectOnChain(s, p, rt.Cls) || NullCause(p, contract, rt, out _) == null) {
                    continue;
                }
                var cls = p.Cls(rt.NullAfter[0].Split('.')[0]) ?? p.Cls(rt.Cls);
                string nulls = string.Join(", ", rt.NullAfter.Select(n => (p.Cls(n.Split('.')[0])?.Plain ?? "?") + "." + n.Split('.')[1]));
                result.Add(new Violation {
                    Rule = "R-REGRESSION-NULL", Class = "silent-non-injection", Severity = "medium", Regression = true, ClassId = cls?.Id, RunId = rt.Cls,
                    Key = "REGRESS|null-after-init", Place = cls == null ? "-" : contract.For(cls).Shape, Against = other.Tag,
                    Message = $"On {other.Tag} ({other.Version}) SystemsRunner.Init filled every injected field of {p.Cls(rt.Cls)?.Plain}; here {nulls} {(rt.NullAfter.Count == 1 ? "stays" : "stay")} null without a diagnostic. " +
                              "The documents of this tree do not forbid it (otherwise it would be a defect)",
                    Doc = $"cross-version: the behaviour of {other.Tag}",
                    Evidence = $"{other.Tag}: {Describe(p, rb)}",
                });
                return;
            }
        }

        /// <summary> A digest run in the words of <see cref="Renderer.ExpectedReproLine"/>. </summary>
        public static string Describe(ProgramModel p, DigestRun r) {
            string Map(string idDotRest) {
                int dot = idDotRest.IndexOf('.');
                if (dot < 0) {
                    return idDotRest;
                }
                return (p.Cls(idDotRest.Substring(0, dot))?.Plain ?? idDotRest.Substring(0, dot)) + idDotRest.Substring(dot);
            }
            var calls = r.Events.Select(e => {
                string what = (p.Cls(Who(e.Tag))?.Plain ?? Who(e.Tag)) + "." + What(e.Tag);
                var nulls = e.Nulls.Select(Map).ToList();
                return nulls.Count == 0 ? what : what + " (null: " + string.Join(", ", nulls) + ")";
            }).ToList();
            var after = r.NullAfter.Select(Map).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return $"{p.Cls(r.Cls)?.Plain ?? r.Cls}: calls: {(calls.Count == 0 ? "-" : string.Join(" -> ", calls))}; null after Init: {(after.Count == 0 ? "-" : string.Join(", ", after))}" +
                   (r.Exception == null ? "" : "; Init threw " + r.Exception.Split(':')[0]);
        }
    }
}
