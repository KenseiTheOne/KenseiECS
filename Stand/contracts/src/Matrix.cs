using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KenseiECS;
using Stand.Contracts.Probe;

namespace Stand.Contracts {
    /// <summary> One planned call: member, scenario and argument variant, or a curated case. </summary>
    public sealed class CaseSpec {
        public int Ordinal;
        public Member Member;
        public string Scenario;
        public Variant Variant = Variant.None;
        public string Curated;                       // label of a curated case
        public Func<Ctx, object> TargetOverride;     // curated: another instance than the scenario's
        public string TargetCode;
        public Func<Ctx, ArgValue[]> ArgsOverride;
        public bool PostPlayback;                    // CommandBuffer recording: Playback(world) completes the case
        public bool NullCheck;                       // a null is passed: when the call returns, the smoke steps check the state
        public Func<Ctx, List<ProbeDef>> ExtraProbes; // curated: probes of this case only, after the standard ones (both sides)

        public string VariantLabel => Curated != null ? "curated:" + Curated : Variant.Name;
        public string Key => Member.Target.Name + "|" + Member.Display + "|" + Scenario + "|" + VariantLabel;
    }

    /// <summary> What a repro needs: the setup lines, the call and the expected signature. </summary>
    public sealed class ReproMaterial {
        public List<string> Setup = new();
        public List<string> Preludes = new();
        public string CallBody;                      // C# statements of a lambda returning object
        public List<string> ProbeNames = new();      // probes whose values the repro prints
        public List<string> ProbeCodes = new();
        public string Expected;                      // signature the stand saw
        public string RefExpected;                   // the same probes on a fresh state
        public string Parts;
        public bool PostPlayback;
    }

    /// <summary>
    /// Enumerates the contract matrix (targets x members x scenarios x argument variants, plus curated
    /// cases) and runs one case: build the scenario, call, consume the result, probe the state, build
    /// the same scenario again without the call, probe it, compare, judge.
    /// </summary>
    public static class Matrix {
        public static List<CaseSpec> Enumerate(bool full, int seed) {
            var specs = new List<CaseSpec>();
            foreach (var target in Catalog.Targets()) {
                var members = Catalog.Members(target);
                foreach (var m in members) {
                    foreach (var scenario in target.Scenarios) {
                        if (m.IsCtor && scenario != Scenarios.Fresh && scenario != Scenarios.Negative && scenario != Scenarios.Huge
                            && scenario != Scenarios.NullEntity && scenario != Scenarios.NullArg && scenario != Scenarios.WorldDestroyed) {
                            continue;
                        }
                        foreach (var v in Args.VariantsFor(m, scenario, full)) {
                            specs.Add(new CaseSpec {
                                Member = m, Scenario = scenario, Variant = v,
                                PostPlayback = target.Type == typeof(CommandBuffer) && IsRecording(m),
                                NullCheck = v.Kind == "null"
                            });
                        }
                    }
                }
                specs.AddRange(Curated(target, members));
            }
            if (seed != 0) {
                var rng = new Random(seed);
                for (int i = specs.Count - 1; i > 0; i--) {
                    int j = rng.Next(i + 1);
                    (specs[i], specs[j]) = (specs[j], specs[i]);
                }
            }
            for (int i = 0; i < specs.Count; i++) {
                specs[i].Ordinal = i;
            }
            return specs;
        }

        private static bool IsRecording(Member m) {
            return !m.IsCtor && (m.Name == "CreateEntity" || m.Name == "Add" || m.Name == "Set" || m.Name == "Remove" || m.Name == "DestroyEntity");
        }

        // =====================================================================
        // Curated cases: inputs the generic variants do not produce
        // =====================================================================

        private static IEnumerable<CaseSpec> Curated(Target target, List<Member> members) {
            var t = target.Type;
            Member Find(string display) => members.FirstOrDefault(x => x.Display == display);
            Member Close(string name, Type[] typeArgs, Func<MethodInfo, bool> pick) {
                var open = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == name && x.IsGenericMethodDefinition && pick(x));
                if (open == null) {
                    return null;
                }
                return Catalog.Describe(target, open.MakeGenericMethod(typeArgs), open);
            }
            CaseSpec C(Member m, string scenario, string label, Func<Ctx, ArgValue[]> args, Func<Ctx, object> targetOverride = null, string targetCode = null) {
                return m == null ? null : new CaseSpec { Member = m, Scenario = scenario, Curated = label, ArgsOverride = args, TargetOverride = targetOverride, TargetCode = targetCode };
            }
            ArgValue A(object v, string code) => new ArgValue { Value = v, Code = code, IsNull = v == null };

            var list = new List<CaseSpec>();
            if (t == typeof(World)) {
                var hasCZ = Close("Has", new[] { typeof(CZ) }, x => x.GetParameters().Length == 1);
                var removeCZ = Close("Remove", new[] { typeof(CZ) }, x => x.GetParameters().Length == 1);
                var removeCB = Close("Remove", new[] { typeof(CB) }, x => x.GetParameters().Length == 1);
                list.Add(C(hasCZ, Scenarios.Fresh, "no-pool", c => new[] { A(c.E0, "e0") }));
                list.Add(C(removeCZ, Scenarios.Fresh, "no-pool", c => new[] { A(c.E0, "e0") }));
                list.Add(C(removeCZ, Scenarios.Fresh, "remove-missing-nopool", c => new[] { A(c.E0, "e0") }));
                list.Add(C(removeCB, Scenarios.Fresh, "remove-missing", c => new[] { A(c.E1, "e1") }));
                list.Add(C(Find("World.Add<CA>(Entity, CA)"), Scenarios.Fresh, "already-has", c => new[] { A(c.E0, "e0"), A(new CA { V = 7 }, "new CA { V = 7 }") }));
                // Owning groups (groups.md, architecture.md RegisterGroup): a group over a type another group owns, or one
                // that lists a type twice, throws; the probes after it check that the failed registration left nothing
                // behind: the existing Group<CC, CD> stays aligned when a new member also gets CE, and a group over CE and
                // CF (owned by no registered group) can still be created.
                var group2 = typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Group" && x.IsGenericMethodDefinition && x.GetGenericArguments().Length == 2);
                var group3 = typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Group" && x.IsGenericMethodDefinition && x.GetGenericArguments().Length == 3);
                foreach (var (open, types, label) in new[] {
                    (group2, new[] { typeof(CE), typeof(CD) }, "group-owned"),
                    (group2, new[] { typeof(CD), typeof(CE) }, "group-owned-first"),
                    (group2, new[] { typeof(CE), typeof(CE) }, "group-twice"),
                    (group3, new[] { typeof(CE), typeof(CF), typeof(CD) }, "group-owned-3"),
                }) {
                    if (open == null) {
                        continue;
                    }
                    var spec = C(Catalog.Describe(target, open.MakeGenericMethod(types), open), Scenarios.Fresh, label, c => new ArgValue[0]);
                    spec.ExtraProbes = GroupProbes;
                    list.Add(spec);
                }
            } else if (t == typeof(ComponentPool<CA>)) {
                list.Add(C(Find("ComponentPool<CA>.Add(int, CA)"), Scenarios.Fresh, "already-has", c => new[] { A(c.E0.Index, "e0.Index"), A(new CA { V = 7 }, "new CA { V = 7 }") }));
            } else if (t == typeof(Filter)) {
                foreach (var name in new[] { "Filter.First()", "Filter.Single()", "Filter.TryGetFirst(out int)" }) {
                    var m = Find(name);
                    list.Add(C(m, Scenarios.Fresh, "empty-filter", c => m.Params.Length == 0 ? new ArgValue[0] : new[] { new ArgValue { Code = "out var o0", IsOut = true } },
                        c => c.World.Filter().Inc<CZ>().End(), "world.Filter().Inc<CZ>().End()"));
                }
            } else if (t == typeof(SharedData)) {
                var get = Find("SharedData.Get<ProbeConfig>(string)");
                var tryGet = Find("SharedData.TryGet<ProbeConfig>(out ProbeConfig, string)");
                list.Add(C(get, Scenarios.Fresh, "missing-key", c => new[] { A("missing", "\"missing\"") }));
                list.Add(C(tryGet, Scenarios.Fresh, "missing-key", c => new[] { new ArgValue { Code = "out var o0", IsOut = true }, A("missing", "\"missing\"") }));
            } else if (t == typeof(WorldSerializer)) {
                var load = Find("WorldSerializer.Load(World, Stream)");
                var save = Find("WorldSerializer.Save(World, Stream)");
                list.Add(C(load, Scenarios.Fresh, "load-into-empty", c => { c.Snapshot.Position = 0; return new[] { A(new World(), "new World()"), A(c.Snapshot, "snapshot") }; }));
                list.Add(C(load, Scenarios.Fresh, "load-garbage", c => new[] { A(new World(), "new World()"), A(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })") }));
                list.Add(C(load, Scenarios.Fresh, "load-empty-stream", c => new[] { A(new World(), "new World()"), A(new MemoryStream(), "new MemoryStream()") }));
                list.Add(C(load, Scenarios.WorldDestroyed, "load-into-destroyed", c => { c.Snapshot.Position = 0; return new[] { A(c.World, "world"), A(c.Snapshot, "snapshot") }; }));
                // A snapshot cut short by one byte (a save interrupted at the end), loaded into an empty
                // world: the world Load writes to is probed like the scenario's world.
                list.Add(C(load, Scenarios.Fresh, "load-truncated", c => {
                    var bytes = c.Snapshot.ToArray();
                    var streamArg = new ArgValue {
                        Value = new MemoryStream(bytes, 0, bytes.Length - 1), Code = "truncated",
                        Prelude = "var truncated = new MemoryStream(snapshot.ToArray(), 0, (int)snapshot.Length - 1);"
                    };
                    return new[] { LoadTarget(), streamArg };
                }));
                // A snapshot that names a component type this process does not have (a save from a build
                // that had it): documented to throw, after the entities are already restored.
                list.Add(C(load, Scenarios.Fresh, "load-unknown-type", c => {
                    var bytes = c.Snapshot.ToArray();
                    var streamArg = new ArgValue {
                        Code = "unknownType",
                        Prelude = "var ub = snapshot.ToArray(); var un = System.Text.Encoding.UTF8.GetBytes(" + ReproWriter.Lit(UnknownTypeNeedle) + "); "
                            + "for (int ui = 0; ui + un.Length <= ub.Length; ui++) { int uk = 0; while (uk < un.Length && ub[ui + uk] == un[uk]) { uk++; } "
                            + "if (uk == un.Length) { ub[ui + un.Length - 2] = (byte)'Q'; break; } } var unknownType = new MemoryStream(ub);"
                    };
                    if (RenameType(bytes)) {
                        streamArg.Value = new MemoryStream(bytes);
                    } else {
                        streamArg.Skip = "the snapshot has no type name " + UnknownTypeNeedle + ": the format stores types differently, the case cannot be built";
                    }
                    return new[] { LoadTarget(), streamArg };
                }));
                list.Add(C(save, Scenarios.Fresh, "save-ref-without-formatter", c => {
                    var w = new World();
                    w.CreateEntity(new CRef { S = "x" });
                    return new[] {
                        new ArgValue { Value = w, Code = "crefWorld", Prelude = "var crefWorld = new World(); crefWorld.CreateEntity(new CRef { S = \"x\" });" },
                        A(new MemoryStream(), "new MemoryStream()")
                    };
                }, c => new WorldSerializer(), "new WorldSerializer()"));
            } else if (t == typeof(SystemsRunner)) {
                var setActiveName = Find("SystemsRunner.SetActive(string, bool)");
                var isActive = Find("SystemsRunner.IsActive(string)");
                var getRunner = Find("SystemsRunner.GetRunner(string)");
                var setActiveIndex = Find("SystemsRunner.SetActive(int, bool)");
                var indexOfName = Find("SystemsRunner.IndexOf(string)");
                var indexOfSystem = Find("SystemsRunner.IndexOf(ISystem)");
                list.Add(C(setActiveName, Scenarios.Fresh, "unknown-name", c => new[] { A("no-such-name", "\"no-such-name\""), A(false, "false") }));
                list.Add(C(isActive, Scenarios.Fresh, "unknown-name", c => new[] { A("no-such-name", "\"no-such-name\"") }));
                list.Add(C(getRunner, Scenarios.Fresh, "unknown-name", c => new[] { A("no-such-name", "\"no-such-name\"") }));
                list.Add(C(setActiveName, Scenarios.Fresh, "setactive-no-run", c => new[] { A("initOnly", "\"initOnly\""), A(false, "false") }));
                list.Add(C(setActiveIndex, Scenarios.Fresh, "setactive-no-run", c => new[] { A(1, "1"), A(false, "false") }));
                list.Add(C(indexOfName, Scenarios.Fresh, "indexof-none", c => new[] { A("no-such-name", "\"no-such-name\"") }));
                list.Add(C(indexOfSystem, Scenarios.Fresh, "indexof-none", c => new[] { A(new ProbeSystem(), "new ProbeSystem()") }));
                // A nested runner over another world, registered on an uninitialized runner (so "Add after Init" does not answer first).
                // The rejected runner is probed too: a failed registration must not leave it changed.
                foreach (var display in new[] { "SystemsRunner.Add(ISystem, string)", "SystemsRunner.AddGroup(string, SystemsRunner)", "SystemsRunner.AddPhase(string, SystemsRunner)" }) {
                    var m = Find(display);
                    if (m == null) {
                        continue;
                    }
                    list.Add(C(m, Scenarios.RunnerDestroyed, "nested-other-world", c => {
                        var otherWorld = new World();
                        var other = new SystemsRunner(otherWorld);
                        var otherArg = new ArgValue {
                            Value = other, Code = "other", Prelude = "var otherWorld = new World(); var other = new SystemsRunner(otherWorld);",
                            Probes = new List<ProbeDef> {
                                new ProbeDef {
                                    Name = "arg:other.Run() → otherWorld.Tick", Code = "{ other.Init(); other.Run(); return otherWorld.Tick; }", Extended = true, Mutating = true,
                                    Run = () => { other.Init(); other.Run(); return otherWorld.Tick; }
                                }
                            }
                        };
                        return m.Params[0].ParameterType == typeof(ISystem)
                            ? new[] { otherArg, A("other", "\"other\"") }
                            : new[] { A("other", "\"other\""), otherArg };
                    }));
                }
                // null for each reference parameter of the registration methods on an uninitialized runner: in the
                // null-arg scenario the runner is initialized, and under KENSEI_DEBUG "Add after Init" answers first.
                foreach (var m in members.Where(x => !x.IsCtor && (x.Name == "Add" || x.Name == "AddGroup" || x.Name == "AddPhase") && x.WhyNotRun == null)) {
                    for (int slot = 0; slot < m.Params.Length; slot++) {
                        var p = m.Params[slot];
                        if (p.ParameterType.IsValueType || p.ParameterType.IsByRef) {
                            continue;
                        }
                        int k = slot;
                        var spec = C(m, Scenarios.RunnerDestroyed, "null@uninit:" + p.Name, c => {
                            var a = new ArgValue[m.Params.Length];
                            for (int i = 0; i < a.Length; i++) {
                                a[i] = i == k ? new ArgValue { Value = null, Code = "null", IsNull = true } : Args.Normal(m, i, c);
                            }
                            return a;
                        });
                        spec.NullCheck = true;
                        list.Add(spec);
                    }
                }
            } else if (t == typeof(Listeners<IProbeListener>)) {
                foreach (var m in members.Where(x => x.Name == "Invoke")) {
                    list.Add(C(m, Scenarios.Fresh, "listeners-default", c => {
                        var normal = new ArgValue[m.Params.Length];
                        for (int i = 0; i < normal.Length; i++) {
                            normal[i] = Args.Normal(m, i, c);
                        }
                        return normal;
                    }, c => new Listeners<IProbeListener>(), "new Listeners<IProbeListener>()"));
                }
            }
            return list.Where(x => x != null);
        }

        // The probes of the curated Group<...>() cases (mutating: they add a member and register a group, so they run last).
        public const string GroupAlignedProbe = "groupCD aligned after a new member also gets CE";
        public const string GroupFreeProbe = "Group<CE, CF>() afterwards";

        private static List<ProbeDef> GroupProbes(Ctx c) {
            var list = new List<ProbeDef>();
            var w = c.World;
            var g = c.GroupCD;
            if (w == null || g == null) {
                return list;
            }
            list.Add(new ProbeDef { Name = GroupAlignedProbe, Code = "GroupChecks.AlignedAfterNewMember(world, groupCD)", Mutating = true,
                                    Run = () => GroupChecks.AlignedAfterNewMember(w, g) });
            list.Add(new ProbeDef { Name = GroupFreeProbe, Code = "GroupChecks.FreeTypesGroupable(world)", Mutating = true,
                                    Run = () => GroupChecks.FreeTypesGroupable(w) });
            return list;
        }

        private const string UnknownTypeNeedle = "Stand.Contracts.Probe.CA,";

        // CA -> CQ in the type name of the snapshot: a type no assembly of the process has.
        private static bool RenameType(byte[] bytes) {
            var needle = System.Text.Encoding.UTF8.GetBytes(UnknownTypeNeedle);
            for (int i = 0; i + needle.Length <= bytes.Length; i++) {
                int k = 0;
                while (k < needle.Length && bytes[i + k] == needle[k]) {
                    k++;
                }
                if (k == needle.Length) {
                    bytes[i + needle.Length - 2] = (byte)'Q';
                    return true;
                }
            }
            return false;
        }

        // The empty world a Load writes to, with the safe queries of a world as its probes.
        private static ArgValue LoadTarget() {
            var target = new World();
            return new ArgValue {
                Value = target, Code = "loadWorld", Prelude = "var loadWorld = new World();",
                Probes = new List<ProbeDef> {
                    new ProbeDef { Name = "loadWorld.EntityCount", Code = "loadWorld.EntityCount", Run = () => target.EntityCount },
                    new ProbeDef {
                        Name = "loadWorld.TryGetEntity(0)", Code = "loadWorld.TryGetEntity(0, out var lt0) + \":\" + lt0",
                        Run = () => { bool ok = target.TryGetEntity(0, out var e); return ok + ":" + e; }
                    },
                    // The invariant the docs state: an entity is alive if and only if it has a component.
                    new ProbeDef {
                        Name = "loadWorld " + Probes.AliveWithoutComponents,
                        Code = Probes.AliveWithoutComponentsCode.Replace("world.", "loadWorld."),
                        Run = () => Probes.CountAliveWithoutComponents(target)
                    },
                    // Components of each alive slot, for the report.
                    new ProbeDef {
                        Name = "loadWorld components per alive slot",
                        Code = "{ var lc = new List<string>(); foreach (var le in loadWorld.AliveEntities) { lc.Add(le.Index + \":\" + loadWorld.GetComponentCount(le)); } return string.Join(\",\", lc); }",
                        Run = () => {
                            var lc = new List<string>();
                            foreach (var le in target.AliveEntities) {
                                lc.Add(le.Index + ":" + target.GetComponentCount(le));
                            }
                            return string.Join(",", lc);
                        }
                    }
                }
            };
        }

        // =====================================================================
        // Execution
        // =====================================================================

        public static CaseResult Run(CaseSpec spec, Policy policy, Docs docs, bool debug, Part parts, out ReproMaterial repro) {
            repro = null;
            var m = spec.Member;
            var r = new CaseResult {
                Ordinal = spec.Ordinal, Key = spec.Key, Target = m.Target.Name, Member = m.Display, Name = m.Name, MemberId = m.DocId,
                Scenario = spec.Scenario, Variant = spec.VariantLabel
            };
            if (m.WhyNotRun != null) {
                r.Verdict = Verdict.NotRun;
                r.WhyNotRun = m.WhyNotRun;
                r.Outcome = new Outcome();
                return r;
            }
            var sw = Stopwatch.StartNew();
            var ctx = Scenarios.Build(spec.Scenario, parts);
            object target = null;
            bool needsTarget = !m.IsCtor && !m.IsStatic && !m.IsExtension;
            if (needsTarget) {
                target = spec.TargetOverride != null ? spec.TargetOverride(ctx) : ctx.TargetObject(m.Target);
                if (target == null) {
                    r.Verdict = Verdict.NotRun;
                    r.WhyNotRun = "scenario has no " + m.Target.Name;
                    r.Outcome = new Outcome();
                    return r;
                }
            }
            var args = spec.ArgsOverride != null ? spec.ArgsOverride(ctx) : Args.Build(m, ctx, spec.Variant);
            var skip = args.FirstOrDefault(a => a.Skip != null);
            if (skip != null) {
                r.Verdict = Verdict.NotRun;
                r.WhyNotRun = skip.Skip;
                r.Outcome = new Outcome();
                return r;
            }
            r.ArgCodes = args.Select(a => a.Code).ToList();
            for (int i = 0; i < args.Length; i++) {
                if (args[i].Unknown) {
                    r.UnknownParams.Add(m.Params[i].Name + ": " + TypeNames.CSharp(m.Params[i].ParameterType));
                }
            }
            var values = args.Select(a => a.Value).ToArray();
            var thunk = Invoker.For(m.Method, out _);
            var o = new Outcome { AnyNullArg = args.Any(a => a.IsNull) };
            object result = null;
            try {
                result = thunk(target, values);
                int drained = Invoker.Drain(result);
                o.Value = Fmt.Value(result);
                if (drained >= 0) {
                    o.Value += " [" + drained + "]";
                }
                if (spec.PostPlayback) {
                    ctx.Buffer.Playback(ctx.World);
                }
            } catch (Exception e) {
                Fill(o, e);
            }
            r.Outcome = o;

            var actual = Probes.Capture(ctx);
            actual.AddRange(Probes.CaptureAll(ArgProbes(args)));
            if (spec.ExtraProbes != null) {
                actual.AddRange(Probes.CaptureAll(spec.ExtraProbes(ctx)));
            }
            // The reference state repeats everything but the call itself: a curated target (an extra
            // filter, say) and the arguments are built on it too, so their own effects cancel out.
            var fresh = Scenarios.Build(spec.Scenario, parts);
            if (needsTarget && spec.TargetOverride != null) {
                spec.TargetOverride(fresh);
            }
            var freshArgs = spec.ArgsOverride != null ? spec.ArgsOverride(fresh) : Args.Build(m, fresh, spec.Variant);
            if (spec.PostPlayback) {
                try {
                    fresh.Buffer.Playback(fresh.World);
                } catch (Exception) {
                    // the same failure as in the case itself; the probes compare what is left
                }
            }
            var expected = Probes.Capture(fresh);
            expected.AddRange(Probes.CaptureAll(ArgProbes(freshArgs)));
            if (spec.ExtraProbes != null) {
                expected.AddRange(Probes.CaptureAll(spec.ExtraProbes(fresh)));
            }
            r.Diffs = Probes.Compare(actual, expected);

            // A null that was accepted: does ordinary use still work afterwards? The reference is the same
            // call with a valid argument in place of the null, so the smoke steps tell the effect of the
            // null from the effect of the call itself (a component with references that Save refuses).
            List<KeyValuePair<ProbeDef, string>> smokeActual = null, smokeExpected = null;
            if (spec.NullCheck && !o.Threw && m.Name != "Destroy" && m.Name != "Dispose") {
                var normal = Scenarios.Build(spec.Scenario, parts);
                bool normalOk = true;
                try {
                    object normalTarget = null;
                    if (needsTarget) {
                        normalTarget = spec.TargetOverride != null ? spec.TargetOverride(normal) : normal.TargetObject(m.Target);
                    }
                    var normalArgs = new object[m.Params.Length];
                    for (int i = 0; i < normalArgs.Length; i++) {
                        normalArgs[i] = Args.Normal(m, i, normal).Value;
                    }
                    Invoker.Drain(thunk(normalTarget, normalArgs));
                    if (spec.PostPlayback) {
                        normal.Buffer.Playback(normal.World);
                    }
                } catch (Exception) {
                    normalOk = false;   // the valid call fails too: nothing to compare the null against
                }
                if (normalOk) {
                    Probes.Capture(normal);   // the same mutating probes ran on the state after the call
                    smokeActual = Probes.CaptureAll(Probes.Smoke(ctx));
                    smokeExpected = Probes.CaptureAll(Probes.Smoke(normal));
                    r.SmokeDiffs = Probes.CompareThrows(smokeActual, smokeExpected);
                }
            }
            sw.Stop();
            r.Ms = sw.Elapsed.TotalMilliseconds;

            var cc = new CaseCtx {
                Member = m, Scenario = spec.Scenario, Variant = spec.Variant, Curated = spec.Curated, Debug = debug,
                Args = args, Outcome = o, Result = result, Diffs = r.Diffs, SmokeDiffs = r.SmokeDiffs, Ctx = ctx, Docs = docs
            };
            policy.Judge(cc, r);
            r.CallCode = CallCode(spec, args);

            if (r.Verdict >= Verdict.Question && r.Verdict != Verdict.NotRun) {
                repro = Material(spec, ctx, args, o, result, actual, expected, smokeActual, smokeExpected, r);
            }
            return r;
        }

        private static List<ProbeDef> ArgProbes(ArgValue[] args) {
            var list = new List<ProbeDef>();
            foreach (var a in args) {
                if (a.Probes != null) {
                    list.AddRange(a.Probes);
                }
            }
            return list;
        }

        public static void Fill(Outcome o, Exception e) {
            o.Threw = true;
            o.ExType = e.GetType().FullName;
            o.ExShort = e.GetType().Name;
            o.Message = e.Message;
            o.ParamName = (e as ArgumentException)?.ParamName;
            var st = new StackTrace(e, false);
            foreach (var f in st.GetFrames() ?? Array.Empty<StackFrame>()) {
                var mb = f.GetMethod();
                if (mb == null || mb.DeclaringType == null) {
                    continue;
                }
                var dt = mb.DeclaringType;
                string name = (dt.IsGenericType ? dt.GetGenericTypeDefinition().FullName : dt.FullName) + "." + mb.Name;
                name = Regex.Replace(name, "`\\d+", "");
                if (o.TopFrame == null) {
                    o.TopFrame = name;
                }
                if (dt.Namespace != null && dt.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal)) {
                    if (o.Site == null) {
                        o.Site = name;
                    }
                    o.Frames.Add(name);
                }
            }
        }

        // =====================================================================
        // C# of the call, for the matrix and the repros
        // =====================================================================

        public static string CallCode(CaseSpec spec, ArgValue[] args) {
            var m = spec.Member;
            string argList = string.Join(", ", args.Select(a => a.Code));
            if (m.IsCtor) {
                return "new " + TypeNames.CSharp(m.Target.Type) + "(" + argList + ")";
            }
            var mi = (MethodInfo)m.Method;
            string generic = mi.IsGenericMethod ? "<" + string.Join(", ", mi.GetGenericArguments().Select(TypeNames.CSharp)) + ">" : "";
            if (m.IsExtension) {
                return mi.DeclaringType.Name + "." + mi.Name + generic + "(" + argList + ")";
            }
            string owner = m.IsStatic ? TypeNames.CSharp(m.Target.Type) : spec.TargetCode ?? m.Target.Var;
            if (m.IsGetter) {
                return owner + "." + m.Name;
            }
            return owner + "." + mi.Name + generic + "(" + argList + ")";
        }

        // Statements of a lambda that performs the call the way the stand did and returns the value
        // the stand formatted: a by-ref-like result is consumed in place, an enumerable is walked.
        private static string CallBody(CaseSpec spec, ArgValue[] args) {
            var m = spec.Member;
            string call = CallCode(spec, args);
            Type ret = m.IsCtor ? m.Target.Type : ((MethodInfo)m.Method).ReturnType;
            string post = spec.PostPlayback ? " buffer.Playback(world);" : "";
            if (ret == typeof(void)) {
                return call + ";" + post + " return null;";
            }
            var vt = ret.IsByRef ? ret.GetElementType() : ret;
            if (vt.IsByRefLike) {
                var length = vt.GetProperty("Length");
                if (length != null) {
                    return "var r = " + call + ".Length;" + post + " return r;";
                }
                var dispose = vt.GetMethod("Dispose", Type.EmptyTypes);
                return "var en = " + call + "; int k = 0; while (k < " + Invoker.DrainLimit + " && en.MoveNext()) { var cur = en.Current; k++; }"
                    + (dispose != null ? " en.Dispose();" : "") + post + " return k;";
            }
            if (vt.Namespace != null && vt.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal) && vt.GetMethod("GetEnumerator", Type.EmptyTypes) != null) {
                return "var r = " + call + "; int k = 0; foreach (var x in r) { if (++k >= " + Invoker.DrainLimit + ") break; }" + post + " return r;";
            }
            return "var r = " + call + ";" + post + " return r;";
        }

        private static readonly (string Token, Part Part)[] TokenParts = {
            ("world", Part.World), ("e0", Part.Entities), ("e1", Part.Entities), ("e2", Part.Entities), ("e3", Part.Entities),
            ("stale", Part.Entities), ("reused", Part.Entities), ("poolA", Part.Pool), ("fA", Part.Filters), ("fAnotB", Part.Filters),
            ("builder", Part.Filters), ("groupCD", Part.Group), ("shared", Part.Shared), ("runner", Part.Runner), ("sys", Part.Runner),
            ("inline", Part.Runner), ("phase", Part.Runner), ("buffer", Part.Buffer), ("pending", Part.Buffer), ("serializer", Part.Serializer),
            ("snapshot", Part.Serializer), ("listeners", Part.Listeners), ("events", Part.Events)
        };

        private static readonly Regex Identifier = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);

        // Variables of the scenario and of argument preludes that a probe's code uses.
        private static HashSet<string> Objects(string code) {
            var known = new HashSet<string>(TokenParts.Select(t => t.Token), StringComparer.Ordinal) { "loadWorld", "other", "otherWorld" };
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Identifier.Matches(code ?? "")) {
                if (known.Contains(m.Value)) {
                    set.Add(m.Value);
                }
            }
            return set;
        }

        /// <summary> Setup parts a piece of code refers to. </summary>
        public static Part PartsOf(string code) {
            var p = Part.None;
            foreach (var (token, part) in TokenParts) {
                if (Regex.IsMatch(code, "\\b" + token + "\\b")) {
                    p |= part;
                }
            }
            return p;
        }

        private static ReproMaterial Material(CaseSpec spec, Ctx ctx, ArgValue[] args, Outcome o, object result,
            List<KeyValuePair<ProbeDef, string>> actual, List<KeyValuePair<ProbeDef, string>> expected,
            List<KeyValuePair<ProbeDef, string>> smokeActual, List<KeyValuePair<ProbeDef, string>> smokeExpected, CaseResult r) {
            var mat = new ReproMaterial {
                Setup = new List<string>(ctx.Code),
                Preludes = args.Where(a => a.Prelude != null).Select(a => a.Prelude).ToList(),
                CallBody = CallBody(spec, args),
                Parts = ctx.Parts.ToString(),
                PostPlayback = spec.PostPlayback
            };
            string callSig = o.Threw ? "ex:" + o.ExShort : "ok:" + Fmt.Value(result);
            var exp = new List<string> { "call=" + callSig };
            var refExp = new List<string> { "call=?" };
            // A repro prints the probes that differ, in the stand's order; a mutating probe (DestroyEntity,
            // pool or filter creation, a smoke step) that ran before one of them runs in the repro too.
            var all = new List<KeyValuePair<ProbeDef, string>>(actual);
            var allExpected = new List<KeyValuePair<ProbeDef, string>>(expected);
            var differing = new HashSet<string>(r.Diffs.Select(d => d.Probe));
            if (smokeActual != null && r.SmokeDiffs != null && r.SmokeDiffs.Count > 0) {
                var byName = smokeExpected.GroupBy(x => x.Key.Name).ToDictionary(g => g.Key, g => g.First());
                foreach (var s in smokeActual) {
                    if (byName.TryGetValue(s.Key.Name, out var e)) {
                        all.Add(s);
                        allExpected.Add(e);
                    }
                }
                foreach (var d in r.SmokeDiffs) {
                    differing.Add(d.Probe);
                }
            }
            int last = -1;
            var touched = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++) {
                if (differing.Contains(all[i].Key.Name)) {
                    last = i;
                    touched.UnionWith(Objects(all[i].Key.Code));
                }
            }
            for (int i = 0; i <= last; i++) {
                var name = all[i].Key.Name;
                if (!differing.Contains(name) && !all[i].Key.Mutating) {
                    continue;
                }
                // A mutating probe matters to the repro only through an object a differing probe reads.
                if (!differing.Contains(name) && !Objects(all[i].Key.Code).Overlaps(touched)) {
                    continue;
                }
                mat.ProbeNames.Add(name);
                mat.ProbeCodes.Add(all[i].Key.Code);
                exp.Add(name + "=" + all[i].Value);
                refExp.Add(name + "=" + allExpected[i].Value);
            }
            mat.Expected = string.Join(" | ", exp);
            mat.RefExpected = string.Join(" | ", refExp);
            return mat;
        }
    }
}
