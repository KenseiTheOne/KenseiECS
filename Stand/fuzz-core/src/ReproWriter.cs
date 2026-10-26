using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KenseiStand.FuzzCore {
    // Turns a (shrunk) failing scenario into an NUnit test that drives KenseiECS through its public API
    // exactly as the stand's real side did, and asserts what the reference model (the documented
    // contract) expects at the failing operation. The test compiles with kit/ReproKit.cs and fails on
    // the tree it was generated from; it passes once the defect is fixed.
    public static class ReproWriter {
        // A copy written to another entity than the one it was copied from runs only where the documents describe it
        // (2.2.2: an EventBuffer copy, claim eventbuffer-copy-store; see Side.CrossCopyRuns); otherwise the stand skips it.
        private static bool CrossRuns => Contract.V222 && Contract.Active("eventbuffer-copy-store");

        private static string Foreign(int slot) => CrossRuns
            ? "(!S.ClipSource[" + slot + "].Equals(h) && !(S.Clip[" + slot + "] is EventBuffer<Hit>))"
            : "!S.ClipSource[" + slot + "].Equals(h)";

        public sealed class Meta {
            public string Id;
            public string Family;
            public string Title;
            public string Flavor;
            public string Config;
            public string CaseFile;
            public string Contract;
        }

        private static readonly string[] Kinds = { "A", "B", "C", "D", "Tag", "R", "K", "Ev", "EB", "LS" };

        private static string TypeName(string k) => k switch {
            "EB" => "EventBuffer<Hit>",
            "LS" => "Listeners<IFoo>",
            _ => k,
        };

        private static bool HasPayload(string k) => k != "Tag" && k != "EB" && k != "LS";

        private static string Mk(string k, int id, int p, bool isDefault = false) {
            if (isDefault) return "default(" + TypeName(k) + ")";
            return k switch {
                "Tag" => "new Tag()",
                "R" => "new R { Id = " + id + ", P = " + p + ", Box = new Probe(" + id + ", 0, S.Probes) }",
                "K" => "new K { Id = " + id + ", P = " + p + ", Box = new Probe(" + id + ", 0, S.Probes) }",
                "EB" => "MkEB(" + p + ")",
                "LS" => "MkLS(" + id + ")",
                _ => "new " + k + " { Id = " + id + ", P = " + p + " }",
            };
        }

        private static string Q(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string Layout(string layout) {
            string Join(IEnumerable<string> ks) => string.Join(", ", ks.Select(k => "typeof(" + TypeName(k) + ")"));
            switch (layout) {
                case "wideA":
                    return Join(new[] { "A", "B", "Tag", "R" }) + ", null, " + Join(new[] { "C", "D", "K", "Ev", "EB", "LS" });
                case "wideB":
                    return Join(new[] { "C", "D", "K", "Ev", "EB", "LS" }) + ", null, " + Join(new[] { "A", "B", "Tag", "R" });
                default:
                    return Join(Kinds);
            }
        }

        public static string Write(Scenario scn, RunResult res, Meta meta, List<OpTrace> traces) {
            OpTrace failing = traces != null && res.OpIndex >= 0 ? traces.FirstOrDefault(t => t.Index == res.OpIndex) : null;
            var sb = new StringBuilder();
            string ns = "KenseiStand.FuzzCore.Repros." + Ident(meta.Id);
            string cls = "Repro_" + Ident(meta.Id);
            bool debug = meta.Config == "dbg";
            bool crash = scn.Uncapped;

            // ---------------------------------------------------------------- header
            sb.AppendLine("// " + meta.Id + " — " + meta.Family + ": " + meta.Title);
            sb.AppendLine("// Rule: " + res.Rule + " at operation " + res.OpIndex + " (" + res.OpText + ")");
            sb.AppendLine("// Signature: " + res.Sig);
            string status = res.FindingStatus ?? "defect";
            string statusLine = "Status for package " + (Contract.PackageVersion ?? "?") + (Contract.Unreleased ? " with unreleased changes" : "") + ": " + status;
            if (status == "question" && res.InactiveClaims != null && res.InactiveClaims.Count > 0) {
                statusLine += " (it rests on promises the documents of this version do not make yet: " + string.Join(", ", res.InactiveClaims) + ")";
            } else if (status == "regression" && res.RegressionReason != null) {
                statusLine += " (" + res.RegressionReason + ")";
            }
            foreach (var line in Wrap(statusLine, 110)) {
                sb.AppendLine("// " + line);
            }
            if (res.Readings != null && res.Readings.Count > 0) {
                foreach (var line in Wrap("Checked under the alternative reading of " + string.Join(" and ", res.Readings) +
                                          " (the contract is silent there, and the world follows that reading up to this failure).", 110)) {
                    sb.AppendLine("// " + line);
                }
            }
            foreach (var c in res.Claims ?? new List<string>()) {
                foreach (var line in Wrap("Claim " + Contract.Describe(c), 110)) {
                    sb.AppendLine("// " + line);
                }
            }
            foreach (var line in Wrap("Contract: " + meta.Contract, 110)) {
                sb.AppendLine("// " + line);
            }
            sb.AppendLine("// Found by Stand/fuzz-core on " + meta.Flavor + "-" + meta.Config + ", seed " + scn.Seed + ", layout " + scn.Layout +
                          (crash ? ", uncapped reactions (crash mode)" : "") + "; scenario: " + meta.CaseFile);
            foreach (var line in Wrap("What the stand saw: " + (res.Detail ?? "").Split('\n')[0], 110)) {
                sb.AppendLine("// " + line);
            }
            sb.AppendLine("// Compile with Stand/fuzz-core/kit/ReproKit.cs and the KenseiECS sources" + (debug ? " with KENSEI_DEBUG" : " (release, no KENSEI_DEBUG)") +
                          "; the test fails while the defect is present.");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using KenseiECS;");
            sb.AppendLine("using KenseiStand.FuzzCore.ReproKit;");
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine();
            sb.AppendLine("namespace " + ns + " {");
            sb.AppendLine("    public struct A : IComponent { public int Id; public int P; }");
            sb.AppendLine("    public struct B : IComponent { public int Id; public int P; }");
            sb.AppendLine("    public struct C : IComponent { public int Id; public int P; }");
            sb.AppendLine("    public struct D : IComponent { public int Id; public int P; }   // its pool tracks changes");
            sb.AppendLine("    public struct Tag : IComponent { }");
            sb.AppendLine("    public struct R : IComponent, IAutoReset<R> {");
            sb.AppendLine("        public int Id; public int P; public Probe Box;");
            sb.AppendLine("        public void AutoReset(ref R c) { if (c.Box != null) c.Box.Resets++; c.Box = null; c.P = 0; }");
            sb.AppendLine("    }");
            sb.AppendLine("    public struct K : IComponent, IAutoReset<K>, IAutoCopy<K> {");
            sb.AppendLine("        public int Id; public int P; public Probe Box;");
            sb.AppendLine("        public void AutoReset(ref K c) { if (c.Box != null) c.Box.Resets++; c.Box = null; c.P = 0; }");
            sb.AppendLine("        public void AutoCopy(ref K c) { if (c.Box != null) { c.Box.Copies++; c.Box = c.Box.Fork(); } }");
            sb.AppendLine("    }");
            sb.AppendLine("    public struct Ev : IComponent { public int Id; public int P; }   // OneFrame<Ev>");
            sb.AppendLine("    public struct Hit { public int V; }                              // OneFrame<EventBuffer<Hit>>");
            sb.AppendLine("    public interface IFoo { int Id { get; } }");
            sb.AppendLine("    public sealed class Foo : IFoo {");
            sb.AppendLine("        private static readonly Dictionary<int, Foo> Cache = new();");
            sb.AppendLine("        public int Id { get; }");
            sb.AppendLine("        private Foo(int id) { Id = id; }");
            sb.AppendLine("        public static Foo Get(int id) { if (!Cache.TryGetValue(id, out var f)) Cache[id] = f = new Foo(id); return f; }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // ---------------------------------------------------------------- test
            sb.AppendLine("    [TestFixture]");
            sb.AppendLine("    public sealed class " + cls + " {");
            sb.AppendLine("        private Ctx S;");
            sb.AppendLine("        private World w;");
            foreach (var l in scn.Listeners) {
                sb.AppendLine("        private " + ListenerClass(l) + " L" + l.Lid + ";");
            }
            sb.AppendLine();
            sb.AppendLine("        private static EventBuffer<Hit> MkEB(int v) { var b = new EventBuffer<Hit>(); b.Add(new Hit { V = v }); return b; }");
            sb.AppendLine("        private static Listeners<IFoo> MkLS(int id) { var l = new Listeners<IFoo>(); l.Add(Foo.Get(id)); return l; }");
            sb.AppendLine();
            sb.AppendLine("        [Test]");
            sb.AppendLine("        public void " + Ident(meta.Family + "_" + res.Rule + "_at_" + Shrinker.OpKind(scn, res)) + "() {");
            sb.AppendLine("#if " + (debug ? "!KENSEI_DEBUG" : "KENSEI_DEBUG"));
            sb.AppendLine("            Assert.Ignore(\"This repro was found " + (debug ? "with" : "without") + " KENSEI_DEBUG; build it the same way.\");");
            sb.AppendLine("#endif");
            sb.AppendLine("            Layout.Register(" + Layout(scn.Layout) + ");");
            sb.AppendLine("            S = new Ctx { Kinds = new[] { " + string.Join(", ", Kinds.Select(k => "typeof(" + TypeName(k) + ")")) + " }, Debug = " +
                          (debug ? "true" : "false") + (crash ? ", Uncapped = true" : "") + " };");
            sb.AppendLine("            S.W = w = new World();");
            if (Contract.V222) {
                sb.AppendLine("            S.CheckPayloadLists = false;   // 2.2.2: lists of pending EventBuffer values have no recorded holder (EventBuffer XML doc)");
            }
            sb.AppendLine("            w.Pool<A>(); w.Pool<B>(); w.Pool<C>(); w.Pool<D>().TrackChanges(); w.Pool<Tag>(); w.Pool<R>(); w.Pool<K>(); w.Pool<Ev>();");
            sb.AppendLine("            w.Pool<EventBuffer<Hit>>(); w.Pool<Listeners<IFoo>>();");
            if (scn.Group) {
                sb.AppendLine("            w.Group<A, B>();");
            }
            sb.AppendLine("            S.Cb = new CommandBuffer();");
            sb.AppendLine("            S.Runner = new SystemsRunner(w).OneFrame<Ev>().OneFrame<EventBuffer<Hit>>();");
            sb.AppendLine("            S.Runner.Init();");
            if (scn.RecWorld) {
                sb.AppendLine("            w.AddEventListener(new WorldRec(S, 3000));      // recorder: logs every world event");
            }
            if (scn.RecTyped) {
                for (int k = 0; k < Kinds.Length; k++) {
                    sb.AppendLine("            w.Pool<" + TypeName(Kinds[k]) + ">().AddListener(new TypedRec<" + TypeName(Kinds[k]) + ">(S, " + (1000 + k) + ", \"" + Kinds[k] + "\"));");
                }
            }
            foreach (var l in scn.Listeners) {
                sb.AppendLine("            L" + l.Lid + " = new " + ListenerClass(l) + "(S, this);");
            }
            sb.AppendLine();

            int failAt = res.OpIndex;
            int filterOrdinal = 0;
            var filterCreated = new Dictionary<string, bool>();
            for (int i = 0; i < scn.Ops.Count && i <= failAt; i++) {
                var op = scn.Ops[i];
                string text = i + ": " + op;
                bool last = i == failAt;
                if (last) {
                    sb.AppendLine();
                    sb.AppendLine("            // ---- the failing operation");
                    sb.AppendLine("            S.Log.Clear();");
                    sb.AppendLine("            S.Violations.Clear();");
                }
                var opTrace = traces?.FirstOrDefault(t => t.Index == i);
                string body = OpBody(scn, op, i, opTrace, ref filterOrdinal, filterCreated);
                if (last) {
                    sb.AppendLine("            string outcome = S.Do(" + Q(text) + ", () => {");
                } else {
                    sb.AppendLine("            S.Do(" + Q(text) + ", () => {");
                }
                foreach (var bl in body.Split('\n')) {
                    if (bl.Length > 0) sb.AppendLine("                " + bl);
                }
                sb.AppendLine("            });");
            }
            sb.AppendLine();
            sb.AppendLine(Assertion(scn, res, failing));
            sb.AppendLine("        }");
            sb.AppendLine();

            // iteration steps
            foreach (var (op, i) in scn.Ops.Select((o, i) => (o, i)).Where(x => x.o.K == "iterate" && x.i <= failAt)) {
                sb.AppendLine(IterMethod(op, i));
            }

            // listener classes
            foreach (var l in scn.Listeners) {
                sb.AppendLine(ListenerCode(scn, l, cls));
            }
            sb.AppendLine(RecorderCode());
            sb.AppendLine(AttachCode(scn));
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static IEnumerable<string> Wrap(string s, int width) {
            var words = s.Replace("\n", " ").Split(' ');
            var line = new StringBuilder();
            foreach (var w in words) {
                if (line.Length + w.Length + 1 > width && line.Length > 0) {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0) yield return line.ToString();
        }

        public static string Ident(string s) {
            var sb = new StringBuilder();
            foreach (char c in s) {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            var r = sb.ToString();
            return char.IsDigit(r[0]) ? "_" + r : r;
        }

        private static string ListenerClass(ListenerDef l) => "Listener" + l.Lid;

        // ---------------------------------------------------------------- top-level operations

        private static string Named(string n) => "S.Named(" + Q(n) + ")";

        private static string OpBody(Scenario scn, Op op, int index, OpTrace trace, ref int filterOrdinal, Dictionary<string, bool> filterCreated) {
            string t = op.T;
            string live = "w.IsAlive(h)" + (op.Stale ? " || S.Debug" : "");
            switch (op.K) {
                case "create":
                    return "var e = w.CreateEntity(" + Mk(t, op.Id, op.P) + ");\nS.Names[" + Q(op.Name) + "] = e;";
                case "destroy":
                    return "if (S.Names.ContainsKey(" + Q(op.E) + ")) w.DestroyEntity(" + Named(op.E) + ");";
                case "copy":
                    return "if (!S.Names.ContainsKey(" + Q(op.E) + ")) return;\nvar c = w.CopyEntity(" + Named(op.E) + ");" +
                           (op.Name != null ? "\nif (!c.Equals(Entity.Null)) S.Names[" + Q(op.Name) + "] = c;" : "");
                case "add": {
                    string add = op.M >= 0 && HasPayload(t)
                        ? "ref var r = ref w.Add(h, " + Mk(t, op.Id, op.P) + ");\nr.P = " + op.M + ";   // write through the returned ref"
                        : "w.Add(h, " + Mk(t, op.Id, op.P) + ");";
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\n" + add;
                }
                case "remove":
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\nw.Remove<" + TypeName(t) + ">(h);";
                case "has":
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\nw.Has<" + TypeName(t) + ">(h);";
                case "poolAdd":
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h)) return;\nw.Pool<" + TypeName(t) + ">().Add(h.Index, " + Mk(t, op.Id, op.P) + ");";
                case "poolRemove":
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h)) return;\nw.Pool<" + TypeName(t) + ">().Remove(h.Index);";
                case "writeGet":
                    if (!HasPayload(t)) return "// no payload";
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h) || !w.Pool<" + t + ">().Has(h.Index)) return;\nw.Get<" + t + ">(h).P = " + op.M + ";";
                case "modify":
                    if (!HasPayload(t)) return "// no payload";
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h) || !w.Pool<" + t + ">().Has(h.Index)) return;\nw.Pool<" + t + ">().Modify(h.Index).P = " + op.M + ";";
                case "mark":
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h) || !w.Pool<D>().Has(h.Index)) return;\nw.Pool<D>().MarkChanged(h.Index);";
                case "addEvent":
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\nw.AddEvent(h, new Hit { V = " + op.P + " });";
                case "subscribe":
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\nw.Subscribe<IFoo>(h, Foo.Get(" + op.Id + "));";
                case "unsubscribe":
                    return "var h = " + Named(op.E) + ";\nif (!S.Names.ContainsKey(" + Q(op.E) + ") || !(" + live + ")) return;\nw.Unsubscribe<IFoo>(h, Foo.Get(" + op.Id + "));";
                case "clip":
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h) || !w.Pool<" + TypeName(t) + ">().Has(h.Index)) return;\n" +
                           "S.Clip[" + op.Slot + "] = w.Get<" + TypeName(t) + ">(h);   // a struct copy of the component\nS.ClipType[" + op.Slot + "] = typeof(" +
                           TypeName(t) + ");\nS.ClipSource[" + op.Slot + "] = h;";
                case "clipEvent":
                    if (trace?.RealOutcome == "stale") return "// skipped by the stand: the copy is stale (its component was removed), the contract does not cover modifying it";
                    return "if (!(S.Clip[" + op.Slot + "] is EventBuffer<Hit> b)) return;\nb.Add(new Hit { V = " + op.P + " });\nS.Clip[" + op.Slot + "] = b;";
                case "writeBack":
                    return "var h = " + Named(op.E) + ";\nif (!w.IsAlive(h) || S.Clip[" + op.Slot + "] == null || " + Foreign(op.Slot) + ") return;\n" +
                           "if (S.PoolHas(S.ClipType[" + op.Slot + "], h.Index)) return;\nWriteBack(h, " + op.Slot + ");   // world.Add(h, copy)";
                case "cbSetClip":
                    if (op.E != null && op.E.StartsWith("p:")) {
                        if (!CrossRuns) return "// skipped by the stand: a copy written to another entity";
                        string pn = op.E.Substring(2);
                        return "if (!S.Pending.TryGetValue(" + Q(pn) + ", out var p) || !(S.Clip[" + op.Slot + "] is EventBuffer<Hit> eb)) return;\n" +
                               "S.Cb.Set(p, eb);   // cb.Set(p, copy of another entity's EventBuffer): 2.2.2 documents the outcome";
                    }
                    return "var h = " + Named(op.E) + ";\nif (S.Clip[" + op.Slot + "] == null || " + Foreign(op.Slot) + ") return;\n" +
                           "CbSetBack(h, " + op.Slot + ");   // cb.Set(h, copy): the documented write-back of a modified copy";
                case "cbCreate":
                    return "S.Pending[" + Q(op.Name) + "] = S.Cb.CreateEntity(S.Note(" + Mk(t, op.Id, op.P) + "));";
                case "cbAdd":
                case "cbSet":
                case "cbRemove":
                case "cbDestroy": {
                    string call = op.K switch {
                        "cbAdd" => "Add(TARGET, S.Note(" + Mk(t, op.Id, op.P) + "))",
                        "cbSet" => "Set(TARGET, S.Note(" + Mk(t, op.Id, op.P) + "))",
                        "cbRemove" => "Remove<" + TypeName(t) + ">(TARGET)",
                        _ => "DestroyEntity(TARGET)",
                    };
                    if (op.E != null && op.E.StartsWith("p:")) {
                        string p = op.E.Substring(2);
                        return "if (!S.Pending.TryGetValue(" + Q(p) + ", out var p)) return;\nS.Cb." + call.Replace("TARGET", "p") + ";";
                    }
                    return "if (!S.Names.ContainsKey(" + Q(op.E) + ")) return;\nS.Cb." + call.Replace("TARGET", Named(op.E)) + ";";
                }
                case "cbPlay":
                    return "try { S.Cb.Playback(w); } finally { S.Pending.Clear(); S.PayloadLists.Clear(); }";
                case "link":
                    return "if (!S.Names.ContainsKey(" + Q(op.E) + ") || !S.Names.ContainsKey(" + Q(op.E2) + ")) return;\nS.Links[" + Named(op.E) + "] = " + Named(op.E2) +
                           ";\nS.Links[" + Named(op.E2) + "] = " + Named(op.E) + ";";
                case "filter": {
                    var sb = new StringBuilder("int before = w.FilterCount;\nvar b = w.Filter()");
                    foreach (var x in op.Inc ?? Array.Empty<string>()) sb.Append(".Inc<" + TypeName(x) + ">()");
                    foreach (var x in op.Exc ?? Array.Empty<string>()) sb.Append(".Exc<" + TypeName(x) + ">()");
                    foreach (var x in op.Any ?? Array.Empty<string>()) sb.Append(".Any<" + TypeName(x) + ">()");
                    sb.Append(";\nvar f = b.End();\nS.Filters[" + Q(op.F) + "] = f;\nif (w.FilterCount > before) {\n    S.FilterNames[f] = " + Q(op.F) + ";");
                    if (scn.RecFilters) {
                        sb.Append("\n    f.AddListener(new FilterRec(S, 2000 + w.FilterCount - 1));   // recorder");
                    }
                    sb.Append("\n}");
                    return sb.ToString();
                }
                case "listen":
                    return "Listen(" + op.Lid + ");";
                case "unlisten":
                    return "Unlisten(" + op.Lid + ");";
                case "group":
                    return "w.Group<" + string.Join(", ", (op.Inc ?? Array.Empty<string>()).Select(TypeName)) + ">();";
                case "warmup":
                    return "w.Warmup();";
                case "clear":
                    return "w.Clear();";
                case "reserve":
                    return "typeof(World).GetMethod(\"Reserve\", new[] { typeof(int), typeof(int) })?.Invoke(w, new object[] { " + op.N1 + ", " + op.N2 + " });   // World.Reserve exists from 2.2.0";
                case "frame":
                    return "S.Suppress = true;\ntry { S.Runner.Run(); } finally { S.Suppress = false; }   // OneFrame<Ev>, OneFrame<EventBuffer<Hit>>";
                case "iterate":
                    return "if (!S.Filters.ContainsKey(" + Q(op.F) + ")) return;\nIterate_" + index + "();";
            }
            return "// unsupported operation " + op.K;
        }

        // Inside foreach over a filter: the stand's per-visit actions, cycled.
        private static string IterMethod(Op op, int i) {
            var sb = new StringBuilder();
            sb.AppendLine("        private void Iterate_" + i + "() {   // foreach over " + op.F);
            sb.AppendLine("            S.Suppress = true;");
            sb.AppendLine("            try {");
            sb.AppendLine("                int k = 0;");
            sb.AppendLine("                foreach (int idx in S.Filters[" + Q(op.F) + "]) {");
            sb.AppendLine("                    S.Log.Add(\"visit|\" + idx);");
            sb.AppendLine("                    var cur = w.GetEntity(idx);");
            sb.AppendLine("                    string action = null, outcome = null;");
            sb.AppendLine("                    try {");
            sb.AppendLine("                        switch (k++ % " + Math.Max(1, op.Plan?.Length ?? 0) + ") {");
            var plan = op.Plan == null || op.Plan.Length == 0 ? new[] { "none" } : op.Plan;
            for (int p = 0; p < plan.Length; p++) {
                var parts = plan[p].Split(':');
                sb.Append("                            case " + p + ": action = " + Q(plan[p]) + "; ");
                switch (parts[0]) {
                    case "none": sb.Append("outcome = \"ok\";"); break;
                    case "destroy": sb.Append("if (!w.IsAlive(cur)) { outcome = \"dead\"; break; } w.DestroyEntity(cur); outcome = \"ok\";"); break;
                    case "remove": sb.Append("if (!w.IsAlive(cur) || !w.Has<" + TypeName(parts[1]) + ">(cur)) { outcome = \"absent\"; break; } w.Remove<" + TypeName(parts[1]) + ">(cur); outcome = \"ok\";"); break;
                    case "add": sb.Append("if (!w.IsAlive(cur) || w.Has<" + TypeName(parts[1]) + ">(cur)) { outcome = \"has\"; break; } w.Add(cur, " + Mk(parts[1], int.Parse(parts[2]), int.Parse(parts[3])) + "); outcome = \"ok\";"); break;
                    case "create": sb.Append("outcome = \"ok\" + Ctx.H(w.CreateEntity(" + Mk(parts[1], int.Parse(parts[2]), int.Parse(parts[3])) + "));"); break;
                    case "write":
                        if (HasPayload(parts[1])) {
                            sb.Append("if (!w.IsAlive(cur) || !w.Pool<" + parts[1] + ">().Has(idx)) { outcome = \"absent\"; break; } w.Get<" + parts[1] + ">(cur).P = " + parts[2] + "; outcome = \"ok\";");
                        } else {
                            sb.Append("outcome = \"absent\";");
                        }
                        break;
                    default: sb.Append("outcome = \"unknown\";"); break;
                }
                sb.AppendLine(" break;");
            }
            sb.AppendLine("                        }");
            sb.AppendLine("                    } catch (Exception ex) { outcome = \"!\" + ex.GetType().Name; }");
            sb.AppendLine("                    S.Log.Add(\"it|\" + action + \"|\" + outcome);");
            sb.AppendLine("                }");
            sb.AppendLine("            } finally { S.Suppress = false; }");
            sb.AppendLine("        }");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- assertion at the failing operation

        private static string Assertion(Scenario scn, RunResult res, OpTrace tr) {
            var sb = new StringBuilder();
            string rule = res.Rule ?? "";
            string msg = Q(rule + " at " + res.OpText + ": " + (res.Detail ?? "").Split('\n')[0]);
            if (rule.StartsWith("C-")) {
                sb.AppendLine("            // Under the contract the scripted reactions end (the reference model finished this operation);");
                sb.AppendLine("            // here the listener recursion passed " + 256 + " nested callbacks. Without the guard the process dies with a stack overflow.");
                sb.AppendLine("            Assert.That(S.Runaway, Is.False, " + msg + ");");
                return sb.ToString();
            }
            if (rule.StartsWith("R-cb-") || rule == "R-filter-event") {
                sb.AppendLine("            // Checked by the recorders at the moment of each callback (they are the first listener of their pool or filter).");
                sb.AppendLine("            Ctx.AssertNoRule(S.Violations, " + Q(rule) + ", " + msg + ");");
                return sb.ToString();
            }
            if (rule.StartsWith("X-") && !rule.StartsWith("X-nested")) {
                string expected = tr?.ModelOutcome ?? "ok";
                if (expected.StartsWith("!")) {
                    sb.AppendLine("            Assert.That(outcome, Is.EqualTo(" + Q(expected) + "), \"the documented outcome is an exception: \" + " + msg + ");");
                } else {
                    sb.AppendLine("            Assert.That(outcome, Does.Not.StartWith(\"!\"), \"the operation is documented to succeed: \" + " + msg + ");");
                }
                return sb.ToString();
            }
            if (rule.StartsWith("N-") && rule != "N-dying-enter" || rule.StartsWith("X-nested") || rule.StartsWith("I-")) {
                var expected = tr?.ModelLog ?? new List<string>();
                sb.AppendLine("            // What the documented semantics produce for this operation (callbacks, nested step outcomes):");
                sb.AppendLine("            var expected = new List<string> {");
                foreach (var l in expected) {
                    sb.AppendLine("                " + Q(l) + ",");
                }
                sb.AppendLine("            };");
                sb.AppendLine("            Ctx.AssertLog(expected, S.Log, " + msg + ");");
                return sb.ToString();
            }
            if (rule == "N-dying-enter") {
                sb.AppendLine("            Ctx.AssertNoRule(S.Violations, \"N-dying-enter\", " + msg + ");");
                return sb.ToString();
            }
            if (rule.StartsWith("R-") || rule.StartsWith("L-")) {
                sb.AppendLine("            Ctx.AssertNoRule(S.Invariants(), " + Q(rule) + ", " + msg + ");");
                return sb.ToString();
            }
            if (rule.StartsWith("S-")) {
                var (present, absent) = SnapshotExpectation(tr);
                sb.AppendLine("            // The state the documented semantics produce (only the lines that differ here):");
                sb.AppendLine("            var snap = S.Snapshot();");
                if (present.Count > 0) {
                    sb.AppendLine("            Ctx.AssertLines(new List<string> { " + string.Join(", ", present.Select(Q)) + " }, snap, " + msg + ");");
                }
                foreach (var a in absent) {
                    sb.AppendLine("            Assert.That(snap, Does.Not.Contain(" + Q(a) + "), " + msg + ");");
                }
                return sb.ToString();
            }
            sb.AppendLine("            Assert.Fail(\"unclassified rule \" + " + msg + ");");
            return sb.ToString();
        }

        private static (List<string> present, List<string> absent) SnapshotExpectation(OpTrace tr) {
            var present = new List<string>();
            var absent = new List<string>();
            if (tr?.RealSnapshot == null || tr.ModelSnapshot == null) {
                return (present, absent);
            }
            var real = new HashSet<string>(tr.RealSnapshot);
            var model = new HashSet<string>(tr.ModelSnapshot);
            string Key(string l) {
                if (l.StartsWith("e")) return l.Substring(0, l.IndexOf(':'));
                if (l.StartsWith("pool ") || l.StartsWith("filter ")) return l.Substring(0, l.IndexOf(' ', l.IndexOf(' ') + 1));
                if (l.StartsWith("ver ")) return l.Substring(0, l.IndexOf('='));
                if (l.StartsWith("tick")) return "world";
                if (l.StartsWith("probes")) return "probes";
                return l;
            }
            var modelKeys = new HashSet<string>(tr.ModelSnapshot.Select(Key));
            foreach (var l in tr.ModelSnapshot) {
                if (!real.Contains(l) && !l.Contains("EB(?)")) present.Add(l);
            }
            foreach (var l in tr.RealSnapshot) {
                if (!model.Contains(l) && !modelKeys.Contains(Key(l))) absent.Add(l);
            }
            return (present, absent);
        }

        // ---------------------------------------------------------------- listeners

        private static string Iface(ListenerDef l) => l.Kind switch {
            "typed" => "IComponentListener<" + TypeName(l.T) + ">",
            "filter" => "IFilterListener",
            _ => "IWorldEventListener",
        };

        private static string ListenerCode(Scenario scn, ListenerDef l, string testCls) {
            var sb = new StringBuilder();
            string name = ListenerClass(l);
            sb.AppendLine("        // L" + l.Lid + ": " + l.Kind + (l.T != null ? " listener of " + TypeName(l.T) : "") + (l.F != null ? " listener of filter " + l.F : "") +
                          "; scripted reactions (each rule fires at most Max times, at most 3 nested reactions deep)");
            sb.AppendLine("        private sealed class " + name + " : " + Iface(l) + " {");
            sb.AppendLine("            private readonly Ctx S;");
            sb.AppendLine("            private readonly " + testCls + " T;");
            sb.AppendLine("            private World w => S.W;");
            sb.AppendLine("            public " + name + "(Ctx s, " + testCls + " t) { S = s; T = t; }");
            switch (l.Kind) {
                case "typed": {
                    string tn = TypeName(l.T);
                    sb.AppendLine("            public void OnAdded(int e, ref " + tn + " c) => On(\"added\", \"+\", e, ref c);");
                    sb.AppendLine("            public void OnRemoved(int e, ref " + tn + " c) => On(\"removed\", \"-\", e, ref c);");
                    sb.AppendLine("            private void On(string on, string ch, int e, ref " + tn + " c) {");
                    sb.AppendLine("                S.Callback(\"" + l.Lid + "|\" + ch + \"|\" + e + \"|" + l.T + "|\" + Ctx.Digest(c), e, false, ch);");
                    EmitPlan(sb, l, "on", null, true);
                    sb.AppendLine("            }");
                    break;
                }
                case "filter":
                    sb.AppendLine("            public void OnEntityAdded(Filter f, int e) => On(\"enter\", \">\", f, e);");
                    sb.AppendLine("            public void OnEntityRemoved(Filter f, int e) => On(\"leave\", \"<\", f, e);");
                    sb.AppendLine("            private void On(string on, string ch, Filter f, int e) {");
                    sb.AppendLine("                S.Callback(\"" + l.Lid + "|\" + ch + \"|\" + e + \"|\" + (S.FilterNames.TryGetValue(f, out var n) ? n : \"?\"), e, false, ch, f);");
                    EmitPlan(sb, l, "on", null, false);
                    sb.AppendLine("            }");
                    break;
                default:
                    sb.AppendLine("            public void OnEntityCreated(int e) => On(\"created\", \"C\", e, -1);");
                    sb.AppendLine("            public void OnEntityDestroyed(int e) => On(\"destroyed\", \"D\", e, -1);");
                    sb.AppendLine("            public void OnComponentAdded(int e, int t) => On(\"compAdded\", \"a\", e, t);");
                    sb.AppendLine("            public void OnComponentRemoved(int e, int t) => On(\"compRemoved\", \"r\", e, t);");
                    sb.AppendLine("            private void On(string on, string ch, int e, int t) {");
                    sb.AppendLine("                string tname = t < 0 ? \"\" : S.KindOf(t);");
                    sb.AppendLine("                S.Callback(\"" + l.Lid + "|\" + ch + \"|\" + e + \"|\" + tname, e, false, ch);");
                    EmitPlan(sb, l, "on", "tname", false);
                    sb.AppendLine("            }");
                    break;
            }
            for (int r = 0; r < l.Rules.Count; r++) {
                sb.AppendLine(RuleMethod(scn, l, r));
            }
            sb.AppendLine("        }");
            return sb.ToString();
        }

        // The stand decides all rules of a callback first (caps), applies a "write" through the ref, then
        // runs the rules in order.
        private static void EmitPlan(StringBuilder sb, ListenerDef l, string onVar, string typeVar, bool typed) {
            if (l.Rules.Count == 0) {
                return;
            }
            sb.AppendLine("                if (S.Suppress) return;   // reactions are off during OneFrame cleanup and filter iteration");
            for (int r = 0; r < l.Rules.Count; r++) {
                var rule = l.Rules[r];
                string cond = onVar + " == " + Q(rule.On);
                if (rule.T != null && typeVar != null) {
                    cond += " && (" + typeVar + " == \"\" || " + typeVar + " == " + Q(rule.T) + ")";
                }
                sb.AppendLine("                bool r" + r + " = " + cond + " && S.Fire(" + l.Lid + ", " + r + ", " + rule.Max + ");");
            }
            if (typed && HasPayload(l.T)) {
                // first fired rule whose first step is "write"
                var writers = l.Rules.Select((ru, i) => (ru, i)).Where(x => x.ru.Do.Count > 0 && x.ru.Do[0].A == "write").ToList();
                if (writers.Count > 0) {
                    string chain = "";
                    foreach (var (ru, i) in writers) {
                        chain += (chain.Length > 0 ? " else " : "") + "if (r" + i + ") { c.P = " + ru.Do[0].M + "; S.Log.Add(\"" + l.Lid + "|w|" + ru.Do[0].M + "\"); }";
                    }
                    sb.AppendLine("                " + chain + "   // write through the callback's ref");
                }
            }
            for (int r = 0; r < l.Rules.Count; r++) {
                sb.AppendLine("                if (r" + r + ") Rule" + r + "(e);");
            }
        }

        private static string Who(string who) => who switch {
            null => "S.Self(e)",
            "self" => "S.Self(e)",
            "partner" => "S.Partner(e)",
            "last" => "last",
            _ => "S.Named(" + Q(who) + ")",
        };

        private static string RuleMethod(Scenario scn, ListenerDef l, int r) {
            var rule = l.Rules[r];
            var sb = new StringBuilder();
            sb.AppendLine("            private void Rule" + r + "(int e) {   // on " + rule.On + (rule.T != null ? " " + rule.T : "") + ", max " + rule.Max);
            sb.AppendLine("                S.Depth++;");
            sb.AppendLine("                try {");
            sb.AppendLine("                    var last = Entity.Null;");
            for (int s = 0; s < rule.Do.Count; s++) {
                var st = rule.Do[s];
                if (st.A == "write") {
                    continue;
                }
                string tag = l.Lid + "#" + r + "." + s;
                sb.AppendLine("                    S.Step(" + Q(tag) + ", " + Q(st.A) + ", () => {");
                foreach (var line in StepBody(st, l).Split('\n')) {
                    sb.AppendLine("                        " + line);
                }
                sb.AppendLine("                    });");
            }
            sb.AppendLine("                } finally { S.Depth--; }");
            sb.AppendLine("            }");
            return sb.ToString();
        }

        private static string StepBody(Step s, ListenerDef l) {
            string t = s.T;
            string h = "var h = " + Who(s.Who) + ";";
            switch (s.A) {
                case "destroy":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nw.DestroyEntity(h);\nreturn \"ok\";";
                case "create":
                    return "var h = w.CreateEntity(" + Mk(t, s.Id, s.P) + ");\nlast = h;" + (s.Name != null ? "\nS.Names[" + Q(s.Name) + "] = h;" : "") + "\nreturn \"ok\" + Ctx.H(h);";
                case "add":
                    return h + "\nif (!S.Usable(h)) return \"skip\";" + (s.G ? "\nif (w.Has<" + TypeName(t) + ">(h)) return \"has\";" : "") +
                           "\nw.Add(h, " + Mk(t, s.Id, s.P) + ");\nreturn \"ok\";";
                case "remove":
                    return h + "\nif (!S.Usable(h)) return \"skip\";" + (s.G ? "\nif (!w.Has<" + TypeName(t) + ">(h)) return \"absent\";" : "") +
                           "\nw.Remove<" + TypeName(t) + ">(h);\nreturn \"ok\";";
                case "poolAdd":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (w.Pool<" + TypeName(t) + ">().Has(h.Index)) return \"has\";\nw.Pool<" + TypeName(t) + ">().Add(h.Index, " +
                           Mk(t, s.Id, s.P) + ");\nreturn \"ok\";";
                case "poolRemove":
                    return h + "\nif (!S.Usable(h)) return \"skip\";" + (s.G ? "\nif (!w.Pool<" + TypeName(t) + ">().Has(h.Index)) return \"absent\";" : "") +
                           "\nw.Pool<" + TypeName(t) + ">().Remove(h.Index);\nreturn \"ok\";";
                case "addEvent":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nw.AddEvent(h, new Hit { V = " + s.P + " });\nreturn \"ok\";";
                case "staleAddEvent":
                case "staleAdd":
                case "staleRemove":
                case "staleHas": {
                    // KENSEI_DEBUG: a dead (not dying) handle must throw.
                    string tt = TypeName(t ?? "A");
                    string call = s.A switch {
                        "staleAddEvent" => "w.AddEvent(h, new Hit { V = " + s.P + " });",
                        "staleAdd" => "w.Add(h, " + Mk(t ?? "A", s.Id, s.P) + ");",
                        "staleRemove" => "w.Remove<" + tt + ">(h);",
                        _ => "return \"ok:\" + (w.Has<" + tt + ">(h) ? \"True\" : \"False\");",
                    };
                    return "if (!S.Debug) return \"skip\";\n" + h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nif (S.Usable(h)) return \"live\";   // only a dead handle is stale\n" +
                           call + (s.A == "staleHas" ? "" : "\nreturn \"ok\";   // KENSEI_DEBUG documents a throw here");
                }
                case "clip":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (!w.Pool<" + TypeName(t) + ">().Has(h.Index)) return \"absent\";\nS.Clip[" + s.Slot + "] = w.Get<" + TypeName(t) +
                           ">(h);\nS.ClipType[" + s.Slot + "] = typeof(" + TypeName(t) + ");\nS.ClipSource[" + s.Slot + "] = h;\nreturn \"ok\";";
                case "writeBack":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (S.Clip[" + s.Slot + "] == null) return \"empty\";\nif (" + Foreign(s.Slot) + ") return \"cross\";\n" +
                           "if (S.PoolHas(S.ClipType[" + s.Slot + "], h.Index)) return \"has\";\nT.WriteBack(h, " + s.Slot + ");\nreturn \"ok\";";
                case "cbSetClip":
                    return h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nif (S.Clip[" + s.Slot + "] == null) return \"empty\";\nif (" + Foreign(s.Slot) + ") return \"cross\";\n" +
                           "T.CbSetBack(h, " + s.Slot + ");\nreturn \"ok\";";
                case "cbDestroy":
                    return h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nS.Cb.DestroyEntity(h);\nreturn \"ok\";";
                case "cbRemove":
                    return h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nS.Cb.Remove<" + TypeName(t) + ">(h);\nreturn \"ok\";";
                case "cbAdd":
                    return h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nS.Cb.Add(h, S.Note(" + Mk(t, s.Id, s.P) + "));\nreturn \"ok\";";
                case "cbSet":
                    return h + "\nif (h.Equals(Entity.Null)) return \"skip\";\nS.Cb.Set(h, S.Note(" + Mk(t, s.Id, s.P) + "));\nreturn \"ok\";";
                case "unlisten":
                    return "return T.Unlisten(" + (s.Lid == 0 ? l.Lid : s.Lid) + ") ? \"ok\" : \"no\";";
                case "listen":
                    return "return T.Listen(" + (s.Lid == 0 ? l.Lid : s.Lid) + ") ? \"ok\" : \"no\";";
                case "copy":
                    return h + "\nif (h.Equals(Entity.Null) || !w.IsAlive(h)) return \"skip\";\nlast = w.CopyEntity(h);" + (s.Name != null ? "\nS.Names[" + Q(s.Name) + "] = last;" : "") +
                           "\nreturn \"ok\" + Ctx.H(last);";
                case "subscribe":
                    return h + "\nif (h.Equals(Entity.Null) || !w.IsAlive(h)) return \"skip\";\nw.Subscribe<IFoo>(h, Foo.Get(" + s.Id + "));\nreturn \"ok\";";
                case "unsubscribe":
                    return h + "\nif (h.Equals(Entity.Null) || !w.IsAlive(h)) return \"skip\";\nw.Unsubscribe<IFoo>(h, Foo.Get(" + s.Id + "));\nreturn \"ok\";";
                case "modify":
                    if (!HasPayload(t)) return h + "\nif (!S.Usable(h)) return \"skip\";\nreturn \"absent\";";
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (!w.Pool<" + t + ">().Has(h.Index)) return \"absent\";\nw.Pool<" + t + ">().Modify(h.Index).P = " + s.M + ";\nreturn \"ok\";";
                case "writeGet":
                    if (!HasPayload(t)) return h + "\nif (!S.Usable(h)) return \"skip\";\nreturn \"absent\";";
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (!w.Pool<" + t + ">().Has(h.Index)) return \"absent\";\nw.Get<" + t + ">(h).P = " + s.M + ";\nreturn \"ok\";";
                case "mark":
                    return h + "\nif (!S.Usable(h)) return \"skip\";\nif (!w.Pool<D>().Has(h.Index)) return \"absent\";\nw.Pool<D>().MarkChanged(h.Index);\nreturn \"ok\";";
                case "link":
                    return "var a = " + Who(s.Who) + ";\nvar b = " + Who(s.Name) + ";\nif (a.Equals(Entity.Null) || b.Equals(Entity.Null)) return \"skip\";\nS.Links[a] = b;\nS.Links[b] = a;\nreturn \"ok\";";
            }
            return "return \"unknown\";";
        }

        // Recorders (when the scenario keeps them) and the shared write-back helpers.
        private static string RecorderCode() {
            return @"        // Recorders: the stand's first listener of every pool, filter and the world. They only log and check.
        private sealed class TypedRec<TC> : IComponentListener<TC> where TC : struct, IComponent {
            private readonly Ctx S; private readonly int Lid; private readonly string Name;
            public TypedRec(Ctx s, int lid, string name) { S = s; Lid = lid; Name = name; }
            public void OnAdded(int e, ref TC c) => S.Callback(Lid + ""|+|"" + e + ""|"" + Name + ""|"" + Ctx.Digest(c), e, true, ""+"");
            public void OnRemoved(int e, ref TC c) => S.Callback(Lid + ""|-|"" + e + ""|"" + Name + ""|"" + Ctx.Digest(c), e, true, ""-"");
        }

        private sealed class FilterRec : IFilterListener {
            private readonly Ctx S; private readonly int Lid;
            public FilterRec(Ctx s, int lid) { S = s; Lid = lid; }
            public void OnEntityAdded(Filter f, int e) => S.Callback(Lid + ""|>|"" + e + ""|"" + S.FilterNames[f], e, true, "">"", f);
            public void OnEntityRemoved(Filter f, int e) => S.Callback(Lid + ""|<|"" + e + ""|"" + S.FilterNames[f], e, true, ""<"", f);
        }

        private sealed class WorldRec : IWorldEventListener {
            private readonly Ctx S; private readonly int Lid;
            public WorldRec(Ctx s, int lid) { S = s; Lid = lid; }
            public void OnEntityCreated(int e) => S.Callback(Lid + ""|C|"" + e + ""|"", e, true, ""C"");
            public void OnEntityDestroyed(int e) => S.Callback(Lid + ""|D|"" + e + ""|"", e, true, ""D"");
            public void OnComponentAdded(int e, int t) => S.Callback(Lid + ""|a|"" + e + ""|"" + S.KindOf(t), e, true, ""a"");
            public void OnComponentRemoved(int e, int t) => S.Callback(Lid + ""|r|"" + e + ""|"" + S.KindOf(t), e, true, ""r"");
        }

        private readonly HashSet<int> _active = new();

        public bool Listen(int lid) {
            if (_active.Contains(lid)) return false;
            if (!Attach(lid, true)) return false;
            _active.Add(lid);
            return true;
        }

        public bool Unlisten(int lid) {
            if (!_active.Contains(lid)) return false;
            Attach(lid, false);
            _active.Remove(lid);
            return true;
        }

        // world.Add(h, copy) of a clip slot: the copy goes back to its own entity.
        public void WriteBack(Entity h, int slot) {
            switch (S.Clip[slot]) {
                case A v: w.Add(h, v); break;
                case B v: w.Add(h, v); break;
                case C v: w.Add(h, v); break;
                case D v: w.Add(h, v); break;
                case Tag v: w.Add(h, v); break;
                case R v: w.Add(h, v); break;
                case K v: w.Add(h, v); break;
                case Ev v: w.Add(h, v); break;
                case EventBuffer<Hit> v: w.Add(h, v); break;
                case Listeners<IFoo> v: w.Add(h, v); break;
            }
        }

        // cb.Set(h, copy) of a clip slot, played back by a later cbPlay.
        public void CbSetBack(Entity h, int slot) {
            switch (S.Clip[slot]) {
                case A v: S.Cb.Set(h, v); break;
                case B v: S.Cb.Set(h, v); break;
                case C v: S.Cb.Set(h, v); break;
                case D v: S.Cb.Set(h, v); break;
                case Tag v: S.Cb.Set(h, v); break;
                case R v: S.Cb.Set(h, v); break;
                case K v: S.Cb.Set(h, v); break;
                case Ev v: S.Cb.Set(h, v); break;
                case EventBuffer<Hit> v: S.Cb.Set(h, v); break;
                case Listeners<IFoo> v: S.Cb.Set(h, v); break;
            }
        }";
        }

        // Attach/detach code per listener (added to the test class).
        public static string AttachCode(Scenario scn) {
            var sb = new StringBuilder();
            sb.AppendLine("        private bool Attach(int lid, bool on) {");
            sb.AppendLine("            switch (lid) {");
            foreach (var l in scn.Listeners) {
                string obj = "L" + l.Lid;
                switch (l.Kind) {
                    case "typed":
                        sb.AppendLine("                case " + l.Lid + ": if (on) w.Pool<" + TypeName(l.T) + ">().AddListener(" + obj + "); else w.Pool<" + TypeName(l.T) + ">().RemoveListener(" + obj + "); return true;");
                        break;
                    case "filter":
                        sb.AppendLine("                case " + l.Lid + ": if (!S.Filters.TryGetValue(" + Q(l.F) + ", out var f" + l.Lid + ")) return false; if (on) f" + l.Lid + ".AddListener(" + obj +
                                      "); else f" + l.Lid + ".RemoveListener(" + obj + "); return true;");
                        break;
                    default:
                        sb.AppendLine("                case " + l.Lid + ": if (on) w.AddEventListener(" + obj + "); else w.RemoveEventListener(" + obj + "); return true;");
                        break;
                }
            }
            sb.AppendLine("            }");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            return sb.ToString();
        }
    }
}
