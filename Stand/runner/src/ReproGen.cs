// Turns a (shrunk) case and its violation into a self-contained C# repro: the same registrations
// and steps the executor performed, then the check of the violating step. Only the API the case
// uses appears in it, so a repro made on 2.1.1 contains no 2.2 calls. Repros compile against the
// package sources plus kit/Probes.cs (copied next to them as ReproKit.cs); repro-check/ runs them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Stand.Runner.Kit;

namespace Stand.Runner {
    public static class ReproGen {
        public static string ClassName(string id) {
            var sb = new StringBuilder("Repro_");
            foreach (char ch in id) {
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }
            return sb.ToString();
        }

        public static string Generate(CaseSpec c, Violation v, string className, IEnumerable<string> header, Ctx ctx) {
            var sb = new StringBuilder();
            foreach (var line in header) {
                sb.Append("// ").AppendLine(line);
            }
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using KenseiECS;");
            sb.AppendLine("using Stand.Runner.Kit;");
            sb.AppendLine();
            sb.AppendLine("public static class " + className + " {");
            sb.AppendLine("    /// <summary> Null when the contract holds, otherwise what broke. </summary>");
            sb.AppendLine("    public static string Run() {");
            var body = new Body(sb, "        ");
            body.Line("var j = new Journal();");
            body.Line("var world = new World();");

            bool buildCheck = v.Check != null && v.Check.Kind == "build";
            if (v.Check != null && (v.Check.Kind == "reject" || v.Check.Kind == "reject-state")) {
                // 2.2.2: the second registration of a runner must throw; the build stops there, as in Exec.Build.
                EmitBuild(c, body, v.Check);
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }
            if (buildCheck) {
                body.Line("try {");
                body.Indent++;
            }
            EmitBuild(c, body, null);
            if (buildCheck) {
                body.Indent--;
                body.Line("} catch (Exception e) {");
                body.Line("    return \"registration threw \" + Journal.Describe(e);");
                body.Line("}");
                body.Line("return null;");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            // Replay the model to know which frames the driver skipped (root not initialized).
            var model = new Model(c, ctx);
            var phases = c.PhaseOrder();
            int last = v.OpIndex;
            for (int i = 0; i <= last && i < c.Ops.Count; i++) {
                var op = c.Ops[i];
                var exp = model.Apply(op);
                bool violating = i == last;
                if (violating) {
                    body.Line("");
                    body.Line("// step " + (i + 1) + " of " + (last + 1) + " — the one that breaks the contract");
                    body.Line("j.Begin();");
                } else {
                    body.Line("// step " + (i + 1) + ": " + Describe(op, c));
                }
                EmitOp(c, op, exp, phases, body, violating);
            }
            if (last >= 0 && last < c.Ops.Count) {
                body.Line("");
            }
            EmitCheck(v, body);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private sealed class Body {
            private readonly StringBuilder _sb;
            private readonly string _baseIndent;
            public int Indent;

            public Body(StringBuilder sb, string baseIndent) {
                _sb = sb;
                _baseIndent = baseIndent;
            }

            public void Line(string text) {
                if (text.Length == 0) {
                    _sb.AppendLine();
                    return;
                }
                _sb.Append(_baseIndent).Append(new string(' ', Indent * 4)).AppendLine(text);
            }
        }

        // Mirrors Exec.Build: depth-first, a nested runner is filled before its parent adds it. With a "reject"
        // check the first second registration of a runner is wrapped in Journal.Catch and checked, and the build
        // stops there.
        private static void EmitBuild(CaseSpec c, Body b, ReproCheck reject) {
            var emitted = new HashSet<int>();
            var disabledOnce = new HashSet<int>();
            var first = c.FirstRegistrations();
            bool stopped = false;
            void Runner(RunnerSpec spec) {
                emitted.Add(spec.Id);
                string rv = "r" + spec.Id;
                b.Line("var " + rv + " = new SystemsRunner(world);");
                foreach (var e in spec.Entries) {
                    if (stopped) {
                        return;
                    }
                    switch (e.Kind) {
                        case EntryKind.Leaf: {
                            var init = new List<string>();
                            if (e.FailInit > 0) init.Add("FailInit = " + e.FailInit);
                            if (e.FailRun > 0) init.Add("FailRun = " + e.FailRun);
                            if (e.ThrowOnDestroy) init.Add("ThrowOnDestroy = true");
                            if (e.Produce >= 0) init.Add("Produce = " + e.Produce);
                            if (e.Observe) init.Add("Observe = true");
                            b.Line("var " + e.Label + " = new " + Probes.ClassName(e.Caps) + "(\"" + e.Label + "\", j)" +
                                   (init.Count > 0 ? " { " + string.Join(", ", init) + " }" : "") + ";");
                            b.Line(rv + ".Add(" + e.Label + (e.Name != null ? ", \"" + e.Name + "\"" : "") + ");");
                            break;
                        }
                        case EntryKind.Group:
                        case EntryKind.Phase: {
                            var child = c.Runner(e.RunnerId);
                            if (!emitted.Contains(child.Id)) {
                                Runner(child);
                                if (stopped) {
                                    return;
                                }
                            }
                            string cv = "r" + child.Id;
                            if (child.StartDisabled && disabledOnce.Add(child.Id)) {
                                b.Line(cv + ".SetActive(false);");
                            }
                            string call;
                            if (e.Kind == EntryKind.Group) {
                                if (e.Name != null) {
                                    call = rv + ".AddGroup(\"" + e.Name + "\", " + cv + ")";
                                } else if (e.Api == 1) {
                                    call = rv + ".Add(" + cv + ", (string)null)";
                                } else {
                                    call = rv + ".Add(" + cv + ")";
                                }
                            } else if (e.Api == 0 && Api.HasAddPhase) {
                                call = rv + ".AddPhase(\"" + e.Name + "\", " + cv + ")";
                            } else if (e.Api == 2) {
                                call = rv + ".Add(" + cv + ", \"" + e.Name + "\")";
                            } else {
                                call = rv + ".Add((ISystem)" + cv + ", \"" + e.Name + "\")";
                            }
                            bool second = first.TryGetValue(e.RunnerId, out var f) && !ReferenceEquals(f, e);
                            if (reject != null && second) {
                                b.Line("var rejected = Journal.Catch(() => " + call + "); // " + cv + " already sits in a slot: 2.2.2 rejects this in every build");
                                b.Line("if (rejected == null) return \"registering " + cv + " a second time did not throw\";");
                                b.Line(reject.Kind == "reject-state"
                                    ? "return Journal.ExpectEqual(" + reject.Expr + ", " + reject.Expected + ", \"" + Escape(reject.Expr) + " after the rejected registration\");"
                                    : "return null;");
                                stopped = true;
                                return;
                            }
                            b.Line(call + ";" + (e.Kind == EntryKind.Phase && e.Api == 2 && Api.HasObsoleteAddRunnerName ? " // [Obsolete] overload: a phase" : "") +
                                   (second ? " // " + cv + " again: a second slot" : ""));
                            break;
                        }
                        case EntryKind.DelHere:
                            b.Line(rv + ".DelHere<Ev" + e.DelType + ">();");
                            break;
                    }
                }
                foreach (var of in spec.OneFrames.Where(o => !o.AfterAdd)) {
                    b.Line(OneFrame(rv, of));
                }
            }
            Runner(c.Root);
            if (stopped) {
                return;
            }
            if (reject != null) {
                b.Line("return \"no second registration in this tree\";");
                return;
            }
            foreach (var r in c.Reachable()) {
                foreach (var of in r.OneFrames.Where(o => o.AfterAdd)) {
                    b.Line(OneFrame("r" + r.Id, of) + " // registered after the tree is built");
                }
            }
            if (c.Root.StartDisabled) {
                b.Line("r0.SetActive(false);");
            }
        }

        private static string OneFrame(string rv, OneFrameSpec of) {
            return rv + ".OneFrame<Ev" + of.Type + ">(" + (of.CleanWhenDisabled ? "cleanWhenDisabled: true" : "") + ");";
        }

        private static void EmitOp(CaseSpec c, OpSpec op, Expectation exp, List<int> phases, Body b, bool violating) {
            string capture = violating ? "var thrown = " : "";
            switch (op.Kind) {
                case OpKind.Init:
                case OpKind.Warmup:
                    b.Line("j.DestroyThrowsArmed = true; // probes with ThrowOnDestroy throw only during Init (the rollback)");
                    b.Line(capture + "Journal.Catch(() => r0." + op.Kind + "());");
                    b.Line("j.DestroyThrowsArmed = false;");
                    break;
                case OpKind.Frame:
                    if (exp.FrameSkipped) {
                        b.Line("// frame skipped: r0 is not initialized (the EcsBootstrap guard)");
                        break;
                    }
                    b.Line(capture + "Journal.Catch(() => r0.Run());");
                    foreach (var p in phases) {
                        b.Line((violating ? "var thrownR" + p + " = " : "") + "Journal.Catch(() => r" + p + ".Run()); // phase");
                    }
                    break;
                case OpKind.SetActiveIndex: {
                    var (owner, entry) = c.FindEntry(op.EntryId);
                    b.Line(capture + "Journal.Catch(() => r" + owner.Id + ".SetActive(" + owner.Entries.IndexOf(entry) + ", " + Bool(op.Value) + ")); // " +
                           (entry.Kind == EntryKind.Leaf ? entry.Label : entry.Kind == EntryKind.DelHere ? "DelHere" : "r" + entry.RunnerId));
                    break;
                }
                case OpKind.SetActiveName: {
                    var (owner, entry) = c.FindEntry(op.EntryId);
                    b.Line(capture + "Journal.Catch(() => r" + owner.Id + ".SetActive(\"" + entry.Name + "\", " + Bool(op.Value) + "));");
                    break;
                }
                case OpKind.SetActiveSelf:
                    b.Line(capture + "Journal.Catch(() => r" + op.RunnerId + ".SetActive(" + Bool(op.Value) + "));");
                    break;
                case OpKind.Emit:
                    b.Line("Events.Create(world, " + op.Type + "); // an Ev" + op.Type + " from outside the runners");
                    if (violating) {
                        b.Line("Exception thrown = null;");
                    }
                    break;
                case OpKind.Destroy:
                    b.Line(capture + "Journal.Catch(() => r0.Destroy());");
                    break;
                case OpKind.UnknownName: {
                    string call = op.Type == 0 ? "SetActive(\"" + Exec.UnknownName + "\", " + Bool(op.Value) + ")"
                        : op.Type == 1 ? "IsActive(\"" + Exec.UnknownName + "\")" : "GetRunner(\"" + Exec.UnknownName + "\")";
                    b.Line(capture + "Journal.Catch(() => " + (op.Type == 0 ? "" : "{ var _ = ") + "r" + op.RunnerId + "." + call + (op.Type == 0 ? "" : "; }") + ");");
                    break;
                }
            }
        }

        private static void EmitCheck(Violation v, Body b) {
            var k = v.Check;
            switch (k.Kind) {
                case "log":
                    if (k.Note != null) {
                        b.Line("// " + k.Note);
                    }
                    b.Line("return j.ExpectLog(\"" + k.Pattern + "\");");
                    break;
                case "rethrow":
                    b.Line("return j.ExpectRethrown(thrown, s" + k.OrigEntryId + ".InitError);");
                    break;
                case "aggregate":
                    b.Line("return j.ExpectAggregate(thrown, s" + k.OrigEntryId + ".InitError);");
                    break;
                case "same-run":
                    b.Line("return Journal.ExpectSame(" + ThrownVar(k.Note) + ", s" + k.OrigEntryId + ".RunError, \"" + k.Note + " exception\");");
                    break;
                case "equal":
                    b.Line("return Journal.ExpectEqual(" + k.Expr + ", " + k.Expected + ", \"" + Escape(k.Expr) + "\");");
                    break;
                case "throws":
                    if (k.Expr != null) {
                        b.Line("return Journal.ExpectThrows<InvalidOperationException>(() => { var _ = " + k.Expr + "; }, \"" + Escape(k.Expr) + "\");");
                    } else {
                        b.Line("return thrown is InvalidOperationException ? null : \"expected InvalidOperationException under KENSEI_DEBUG, got \" + Journal.Describe(thrown);");
                    }
                    break;
                case "nothrow":
                    if (k.Expr != null) {
                        b.Line("return Journal.ExpectNoThrow(() => { var _ = " + k.Expr + "; }, \"" + Escape(k.Expr) + "\");");
                    } else {
                        string var = ThrownVar(k.Note);
                        b.Line("return " + var + " == null ? null : \"" + Escape(k.Note ?? "the step") + " threw \" + Journal.Describe(" + var + ");");
                    }
                    break;
                default:
                    b.Line("return \"unknown check " + k.Kind + "\";");
                    break;
            }
        }

        // The variable a repro keeps the exception of a step in: "thrownR3" for r3.Run() of a nested runner, "thrown" for
        // the root's Run (also when the step is written "root.Run()") and every other step.
        private static string ThrownVar(string what) {
            var m = what == null ? null : System.Text.RegularExpressions.Regex.Match(what, @"^r(\d+)\.Run\(\)$");
            if (m != null && m.Success && m.Groups[1].Value != "0") {
                return "thrownR" + m.Groups[1].Value;
            }
            return "thrown";
        }

        private static string Bool(bool value) {
            return value ? "true" : "false";
        }

        private static string Escape(string s) {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static string Describe(OpSpec op, CaseSpec c) {
            switch (op.Kind) {
                case OpKind.SetActiveIndex: {
                    var (owner, entry) = c.FindEntry(op.EntryId);
                    return owner == null ? "SetActive(?)" : "r" + owner.Id + ".SetActive(" + owner.Entries.IndexOf(entry) + ", " + Bool(op.Value) + ")";
                }
                case OpKind.SetActiveName: {
                    var (owner, entry) = c.FindEntry(op.EntryId);
                    return owner == null ? "SetActive(name?)" : "r" + owner.Id + ".SetActive(\"" + entry.Name + "\", " + Bool(op.Value) + ")";
                }
                case OpKind.SetActiveSelf:
                    return "r" + op.RunnerId + ".SetActive(" + Bool(op.Value) + ")";
                case OpKind.Emit:
                    return "Events.Create(world, " + op.Type + ")";
                case OpKind.UnknownName:
                    return "r" + op.RunnerId + "." + (op.Type == 0 ? "SetActive" : op.Type == 1 ? "IsActive" : "GetRunner") + "(unknown name)";
                case OpKind.Frame:
                    return "frame: r0.Run() + phases";
                default:
                    return "r0." + op.Kind + "()";
            }
        }
    }
}
