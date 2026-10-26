using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KenseiStand.Generator {
    public sealed class Violation {
        /// <summary> Oracle rule, R-... </summary>
        public string Rule;
        /// <summary> Defect class: silent-non-injection, generator-crash, false-diagnostic, ... </summary>
        public string Class;
        /// <summary> high / medium / low (an estimate), or question for a contract question. </summary>
        public string Severity;
        public string ClassId;
        public string Message;
        /// <summary> Name-free signature used to group the same defect across programs and for minimization. </summary>
        public string Key;
        public string Doc;
        /// <summary> Leaf class of the run that showed a runtime violation (null for compile-time ones). </summary>
        public string RunId;
        /// <summary> Known issue id (GN1...) the violation matches, or null. </summary>
        public string Known;
        /// <summary> The key before a downgrade to a contract question (alias-only classes). </summary>
        public string OriginalKey;
        /// <summary> Claim (Claims.cs) the rule rests on; null for the universal rules. A claim the tree does not make turns the violation into a question. </summary>
        public string Claim;
        /// <summary> Why the violation was turned into a question (inactive claim), or null. </summary>
        public string Downgrade;
        /// <summary> A change against another tree that no promise forbids: status "regression", neither defect nor question. </summary>
        public bool Regression;
        /// <summary> Behaviour the documents of this tree describe (a documented limitation): status "documented". </summary>
        public bool Documented;
        /// <summary> The tree a cross-version violation compares with ("v2.1.1-rel"), or null. </summary>
        public string Against;
        /// <summary> Name-free place: the contract shape of the class the violation is about ("-" for a unit). Rule, key and place make the signature. </summary>
        public string Place;
        /// <summary> The program a cross-version violation is about when it is not the sample's own (the upgraded form of a migration pair). </summary>
        [System.Text.Json.Serialization.JsonIgnore] public ProgramModel Subject;
        /// <summary> Evidence from the other tree for a cross-version violation (what it showed there). </summary>
        public string Evidence;

        public bool Question => Severity == "question";
        public bool IsNote => Severity == "note";
        public bool IsDefect => !Question && !IsNote && !Regression && !Documented;
        public string Status => IsNote ? "note" : Question ? "question" : Documented ? "documented" : Regression ? "regression" : "defect";

        public override string ToString() => $"[{(Documented ? "documented/" + Severity : Regression ? "regression/" + Severity : Severity)}] {Rule} {Key} @{Place}: {Message}";
    }

    /// <summary>
    /// Checks an observation against the universal oracles (no crash, only KECS diagnostics, generated
    /// code that compiles cleanly, every injected field assigned, each Init/OnInit once and base first)
    /// and against the contract model (documented layouts must work, documented errors must be
    /// reported). Anything the documents leave open is at most a contract question.
    /// </summary>
    public static class Oracle {
        public static List<Violation> Check(ProgramModel p, Contract contract, Observation obs) {
            var result = new List<Violation>();
            if (obs.Invalid) {
                return result;
            }
            foreach (var unit in obs.Units) {
                CheckUnit(p, contract, unit, result);
            }
            if (obs.RuntimeError != null) {
                result.Add(new Violation {
                    Rule = "R-HARNESS", Class = "inconclusive", Severity = "note", Key = "HARNESS",
                    Message = "loading or running the program failed: " + obs.RuntimeError,
                });
            }
            foreach (var run in obs.Runs) {
                CheckRun(p, contract, obs, run, result);
            }
            ApplyClaims(contract.Claims, result);
            // A missing documented error is only worth a question when nothing worse happened to the class.
            var serious = new HashSet<string>(result.Where(v => !v.Question && v.Severity != "note" && !v.Documented && v.Rule != "R-KECS-TEXT").Select(v => v.ClassId ?? ""));
            result.RemoveAll(v => v.Rule == "R-MISSING-KECS" && serious.Contains(v.ClassId ?? ""));
            // A class whose injected fields are all marked through a using alias is documented to get
            // nothing (docs/architecture.md: the receiver looks for the short attribute names); whatever
            // follows from that is a contract question, not a finding.
            foreach (var v in result) {
                if (v.Severity == "note" || v.Key.StartsWith("ALIAS|") || v.Key == "MISSING|alias") {
                    continue;
                }
                var involved = new[] { p.Cls(v.ClassId), p.Cls(v.RunId) }.Where(c => c != null).SelectMany(c => new[] { c }.Concat(p.Ancestors(c)));
                var alias = involved.FirstOrDefault(Contract.IsAliasOnly);
                if (alias != null) {
                    v.Severity = "question";
                    v.Class = "alias-attribute";
                    v.OriginalKey = v.Key;
                    v.Key = "ALIAS|" + v.Rule;
                    v.Message = $"{alias.Plain} marks every injected field through a using alias, which the generator does not see (docs/architecture.md: the receiver matches the short names Inc/Exc/Any/Pool/Group/Shared): " + v.Message;
                    v.Doc = "docs/architecture.md, The source generator, step 1";
                }
            }
            foreach (var v in result) {
                v.Place ??= PlaceOf(p, contract, v);
            }
            return Dedup(result);
        }

        /// <summary> Name-free place of a violation: the contract shape of its class (or of the run's leaf), "-" for a whole unit. </summary>
        public static string PlaceOf(ProgramModel p, Contract contract, Violation v) {
            var c = p.Cls(v.ClassId) ?? p.Cls(v.RunId);
            return c == null ? "-" : contract.For(c).Shape;
        }

        /// <summary>
        /// The versioned contract profile: a violation whose claim the tree under test does not make
        /// (the promise is newer than the tree, or its documents dropped the sentence) is a question.
        /// </summary>
        public static void ApplyClaims(ClaimSet claims, List<Violation> list) {
            if (claims == null) {
                return;
            }
            foreach (var v in list) {
                if (v.Claim == null || v.Question || v.IsNote || v.Regression || v.Documented) {
                    continue;
                }
                // "a|b": the same promise in the words of two versions; one active sentence keeps the defect.
                if (claims.IsActive(v.Claim)) {
                    continue;
                }
                var inactive = ClaimSet.Alternatives(v.Claim).Select(claims.Get).Where(c => c != null).ToList();
                v.Severity = "question";
                v.Downgrade = string.Join("; ", inactive.Select(c => $"claim {c.Id}: {c.Note}"));
                v.Message += $" [a question, not a defect: {inactive.FirstOrDefault()?.Note}]";
            }
        }

        private static List<Violation> Dedup(List<Violation> list) {
            var seen = new HashSet<string>();
            var result = new List<Violation>();
            foreach (var v in list) {
                if (seen.Add(v.Rule + "|" + v.Key + "|" + v.ClassId)) {
                    result.Add(v);
                }
            }
            return result;
        }

        private static string Name(ProgramModel p, string id) => p.Cls(id)?.Plain ?? id ?? "?";

        // Program names (class, field, namespace identifiers) vary between programs; keys keep
        // only the shape: a name-like token becomes <name> unless it is Init or OnInit.
        private static string NormToken(string token) {
            if (string.IsNullOrEmpty(token)) {
                return "-";
            }
            if (token == "Init" || token == "OnInit" || token == "IInitSystem") {
                return token;
            }
            if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(token) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) {
                return "keyword";
            }
            if (token.Length == 1 && !char.IsLetterOrDigit(token[0])) {
                return "punct";
            }
            return "<name>";
        }

        private static string ExceptionType(string text) {
            if (text == null) {
                return "";
            }
            int colon = text.IndexOf(':');
            return colon < 0 ? text : text.Substring(0, colon);
        }

        private static string NormException(string text) {
            if (text == null) {
                return "";
            }
            string s = Regex.Replace(text, "'[^']*'", "'*'");
            s = Regex.Replace(s, "\"[^\"]*\"", "\"*\"");
            s = Regex.Replace(s, @"\(Parameter[^)]*\)", "");
            s = Regex.Replace(s, @"\d+", "N");
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        private static void CheckUnit(ProgramModel p, Contract contract, UnitResult unit, List<Violation> result) {
            bool crashed = unit.GeneratorException != null || unit.GeneratorDiagnostics.Any(d => d.Id == "CS8785");
            if (crashed) {
                string text = unit.GeneratorException ?? unit.GeneratorDiagnostics.First(d => d.Id == "CS8785").Message;
                string mechanism = CrashMechanism(p, unit, text);
                result.Add(new Violation {
                    Rule = "R-CRASH", Class = "generator-crash", Severity = mechanism == "case-collision" ? "high" : "medium",
                    // The exception text differs between Roslyn versions ("The hintName 'X' ..." in 4.x,
                    // "The hintName ..." in 3.8); the key keeps the type and the model-derived mechanism.
                    Key = "CRASH|" + ExceptionType(text) + "|" + mechanism,
                    Message = $"{unit.Asm}: the generator threw ({text}); CS8785 is the only trace and no class of the assembly gets its Init",
                    Doc = "universal: the generator must not fail (CS8785 drops all of its output)",
                });
            }
            foreach (var d in unit.GeneratorDiagnostics.Where(d => !d.Id.StartsWith("KECS") && d.Id != "CS8785")) {
                result.Add(new Violation {
                    Rule = "R-FOREIGN-DIAG", Class = "foreign-diagnostic", Severity = "medium", ClassId = d.ClassId,
                    Key = "FOREIGN|" + d.Id, Message = $"{unit.Asm}: the generator reported {d.Id}, not a KECS diagnostic: {d.Message}",
                    Doc = DocDiagnosticsTable, Claim = ClaimDiagnostics,
                });
            }

            var reportedBy = unit.GeneratorDiagnostics.Where(d => d.Id.StartsWith("KECS"))
                .GroupBy(d => d.ClassId ?? "").ToDictionary(g => g.Key, g => g.ToList());
            foreach (var c in p.ActiveClasses(unit.Asm)) {
                var e = contract.For(c);
                reportedBy.TryGetValue(c.Id, out var mine);
                var ids = mine?.Select(d => d.Id).Distinct().OrderBy(x => x).ToList() ?? new List<string>();
                switch (e.Kind) {
                    case ExpKind.Works:
                        if (ids.Count > 0) {
                            result.Add(new Violation {
                                Rule = "R-FALSE-KECS", Class = "false-diagnostic", Severity = "medium", ClassId = c.Id,
                                Key = $"FALSE|{string.Join("/", ids)}|{e.Shape}",
                                Message = $"{string.Join(", ", ids)} on {c.Plain}, a layout the documents support ({e.Layout}): {mine[0].Message}",
                                Doc = e.Doc, Claim = e.Claim,
                            });
                        }
                        break;
                    case ExpKind.Reports:
                        if (ids.Count > 0 && !ids.Any(e.Ids.Contains)) {
                            // Another KECS error than the documented one: the build fails either way, but the
                            // message sends the user the wrong way (KECS012 "remove the call" where the base
                            // cannot be chained leads to fields that silently stay null).
                            result.Add(new Violation {
                                Rule = "R-OTHER-KECS", Class = "misleading-diagnostic", Severity = "low", ClassId = c.Id,
                                Key = $"OTHER|{string.Join("/", ids)}|{string.Join("/", e.Ids)}|{e.Shape}",
                                Message = $"{c.Plain}: the contract names {string.Join("/", e.Ids)} for this layout ({e.Layout}), the generator reported {string.Join(", ", ids)}",
                                Doc = e.Doc, Claim = e.Claim,
                            });
                        }
                        break;
                    case ExpKind.Blocked:
                        // KECS012 for the class's own base.Init call does not follow from the base's error.
                        ids = ids.Where(id => !(id == "KECS012" && c.OnInitCallsBase)).ToList();
                        if (ids.Count > 0) {
                            // A cascade only when the base did get its error; otherwise the generator put an
                            // error on the derived class instead of the documented one on the base.
                            bool baseReported = p.Ancestors(c).Any(a => HardReported(reportedBy, a.Id));
                            result.Add(baseReported
                                ? new Violation {
                                    Rule = "R-CASCADE", Class = "cascade-diagnostic", Severity = "low", ClassId = c.Id,
                                    Key = $"CASCADE|{string.Join("/", ids)}",
                                    Message = $"{c.Plain}: a second error ({string.Join(", ", ids)}) although its base has its own error ({e.Layout})",
                                    Doc = e.Doc, Claim = "no-second-error",
                                }
                                : new Violation {
                                    Rule = "R-OTHER-KECS", Class = "other-diagnostic", Severity = "question", ClassId = c.Id,
                                    Key = $"OTHER|{string.Join("/", ids)}|base-error-missing|{e.Shape}",
                                    Message = $"{c.Plain}: the generator reported {string.Join(", ", ids)} here, but not the error the contract names for its base ({e.Layout})",
                                    Doc = e.Doc,
                                });
                        }
                        break;
                    case ExpKind.Hand: {
                        // 2.2.2: KECS013 belongs on a hand-written override of a generated Init without base.Init.
                        string site = contract.Kecs013Site(c);
                        var unexpected = ids.Where(id => !(id == "KECS013" && site != null)).ToList();
                        if (unexpected.Count > 0) {
                            bool only13 = unexpected.All(id => id == "KECS013");
                            result.Add(new Violation {
                                Rule = "R-FALSE-KECS", Class = "false-diagnostic", Severity = "medium", ClassId = c.Id,
                                Key = $"FALSE|{string.Join("/", unexpected)}|hand",
                                Message = $"{string.Join(", ", unexpected)} on the hand-written class {c.Plain}, which has no injected fields" +
                                          (only13 ? " and does not override a generated Init without base.Init" : "") + $": {mine.First(d => unexpected.Contains(d.Id)).Message}",
                                Doc = only13 ? DocKecs013 : DocDiagnosticsTable, Claim = only13 ? "kecs013" : ClaimDiagnostics,
                            });
                        }
                        break;
                    }
                }
                if (mine != null) {
                    // The text of the diagnostics: the KECS006 reason, the names in the other messages.
                    foreach (var d in mine.Where(d => d.Id.StartsWith("KECS")).GroupBy(d => d.Id).Select(g => g.First())) {
                        var problem = MessageProblem(p, contract, c, e, d);
                        if (problem == null) {
                            continue;
                        }
                        result.Add(new Violation {
                            Rule = "R-KECS-TEXT", Class = "misleading-diagnostic", Severity = "low", ClassId = c.Id,
                            Key = $"TEXT|{d.Id}|{problem.Value.Kind}",
                            Message = $"{d.Id} on {c.Plain}: {problem.Value.Text}: \"{d.Message}\"",
                            Doc = problem.Value.Kind.StartsWith("reason")
                                ? "docs/guides/source-generator.md, Diagnostics, KECS006: 'The message names the reason.'"
                                : "universal: a diagnostic names the class, field or type it is about (docs/guides/source-generator.md, Diagnostics: the cause of each id)",
                            Claim = problem.Value.Kind.StartsWith("reason") ? "kecs006-reason" : null,
                        });
                    }
                }
            }

            bool anyKecsError = unit.GeneratorDiagnostics.Any(d => d.Id.StartsWith("KECS") && d.Severity == "Error");
            // A diagnostic outside the class body (a namespace brace after broken code) belongs to the
            // class of its generated file.
            foreach (var d in unit.Compile.Where(d => d.InGenerated && d.ClassId == null)) {
                d.ClassId = p.ActiveClasses(unit.Asm).FirstOrDefault(c => Renderer.HintOf(p, c) == d.File)?.Id;
            }
            // Errors in one generated file usually cascade (a syntax error yields a dozen); the first one
            // of each class names the defect, the others are listed with it.
            foreach (var group in unit.Compile.Where(d => d.InGenerated && d.Severity == "Error").GroupBy(d => d.ClassId ?? d.File)) {
                var first = group.OrderBy(d => d.File).ThenBy(d => d.Line).First();
                var codes = group.Select(d => d.Id).Distinct().ToList();
                string token = NormToken(first.Token);
                bool keyword = token == "keyword" || KeywordInvolved(p, p.Cls(first.ClassId));
                string mechanism = MemberMechanism(p, first.ClassId, token);
                // The generated partial OnInit over a hand-written OnInit(World, SharedData) hook of a base (GN4): no document
                // covers it before 2.2.2, which promises the hook override (claim hook-222); a question until then.
                bool hook = !keyword && token == "OnInit" && mechanism == "|hook";
                result.Add(new Violation {
                    Rule = "R-GEN-ERROR", Class = "generated-code-error", Severity = hook ? (contract.HookSupported ? "medium" : "question") : keyword ? "low" : "medium", ClassId = first.ClassId,
                    Key = keyword ? "GENERR|keyword-ident" : $"GENERR|{first.Id}|{token}{mechanism}",
                    Message = $"{unit.Asm}: {first.Id} in the generated {first.File}:{first.Line} ({Name(p, first.ClassId)}): {first.Message}" +
                              (codes.Count > 1 ? $" (also {string.Join(", ", codes.Where(x => x != first.Id))})" : ""),
                    Doc = hook
                        ? (contract.HookSupported ? DocHook222 : "not covered by the documents before 2.2.2: a generated partial OnInit next to a hand-written OnInit(World, SharedData) hook of the base (the hiding warnings CS0108/CS0114 of the same clash are questions too)")
                        : "universal: the generator reports KECS errors instead of emitting code that does not compile (" + Contract.Log211 + ": 'failed inside the generated file')",
                    Claim = hook && contract.HookSupported ? "hook-222" : null,
                });
            }
            var brokenClasses = new HashSet<string>(unit.Compile.Where(d => d.InGenerated && d.Severity == "Error").Select(d => d.ClassId ?? d.File ?? ""));
            foreach (var d in unit.Compile) {
                var c = p.Cls(d.ClassId);
                if (d.InGenerated) {
                    if (d.Severity == "Error") {
                        continue;
                    } else if (!brokenClasses.Contains(d.ClassId ?? d.File ?? "")) {
                        // CHANGELOG 2.2.0 promises `new` where the generated Init hides an inherited one,
                        // so a hiding warning on Init breaks a promise. CS0108/CS0114 on OnInit and CS0109
                        // (a `new` that hides nothing) are in no document: contract questions.
                        string token = NormToken(d.Token);
                        string mechanism = MemberMechanism(p, d.ClassId, token);
                        bool hidesInit = (d.Id == "CS0108" || d.Id == "CS0114") && token == "Init";
                        // 2.2.2 declares no partial OnInit over a base hook and silences the hiding warning (claim hook-222).
                        bool hook = token == "OnInit" && mechanism == "|hook" && contract.HookSupported;
                        result.Add(new Violation {
                            Rule = "R-GEN-WARNING", Class = "generated-code-warning", Severity = hidesInit || hook ? "low" : "question", ClassId = d.ClassId,
                            Claim = hidesInit ? "init-hiding-new" : hook ? "hook-222" : null,
                            Key = $"GENWARN|{d.Id}|{token}{mechanism}",
                            Message = $"{unit.Asm}: warning {d.Id} in the generated {d.File}:{d.Line} ({Name(p, d.ClassId)}): {d.Message}",
                            Doc = hidesInit
                                ? Contract.Log220 + ": 'Where the generated Init hides an inherited one, it is declared new (no more CS0108/CS0114 in generated code)'"
                                : hook ? DocHook222
                                : "not covered by the documents: the CHANGELOG 2.2.0 promise is about a generated Init that hides an inherited Init; " +
                                  (d.Id == "CS0109" ? "CS0109 (a new modifier that hides nothing)" : $"{d.Id} on {token}") +
                                  " is not mentioned (KenseiECS.Generators.Tests expects no warnings, but a test is not a document)",
                        });
                    }
                    continue;
                }
                if (d.Severity != "Error") {
                    continue;
                }
                if (c != null && new[] { c }.Concat(p.Ancestors(c)).Any(x => HardReported(reportedBy, x.Id))) {
                    // The class or a base got a KECS error that leaves it without its generated Init:
                    // whatever fails in this class follows from that error.
                    continue;
                }
                // CS0506 on a hand-written override: the generated base Init did not become virtual. The
                // generator walks up from the override and stops at the first method it takes for Init;
                // docs/architecture.md, step 5: only overriding intermediates pass the "virtual" on, so an
                // intermediate with another Init overload is not covered (a contract question).
                bool overloadTakenAsInit = d.Id == "CS0506" && c != null
                    && p.Ancestors(c).TakeWhile(a => !a.IsGenerated).Any(a => a.Init is InitKind.InParam or InitKind.GenericHelper);
                if (overloadTakenAsInit && contract.Is22) {
                    result.Add(new Violation {
                        Rule = "R-GEN-ERROR", Class = "contract-question", Severity = "question", ClassId = d.ClassId,
                        Key = "GENERR-USER|CS0506|Init|overload-taken-as-init",
                        Message = $"{unit.Asm}: CS0506 in {Name(p, d.ClassId)}: the hand-written override of Init does not make the generated base Init virtual, because a class in between declares an Init overload the generator takes for Init: {d.Message}",
                        Doc = "docs/architecture.md, The source generator, step 5 (only overriding intermediates are named)",
                    });
                    continue;
                }
                if (!contract.Is22 && d.Id == "CS0506" && c != null && !c.IsGenerated && Contract.IsOverrideKind(c.Init)
                    && Pipeline.ResolvedByGeneratedInit(p, c)) {
                    // 2.1 generated a plain `public void Init`; overriding it failed with CS0506, which the
                    // 2.2.0 changelog lists as fixed. Not a 2.1 finding.
                    continue;
                }
                if (contract.Is22 && d.Id == "CS0506" && c != null && !c.IsGenerated && Contract.IsOverrideKind(c.Init)
                    && contract.OverriddenGenerated(c) is ClassModel target && target.Asm != c.Asm && !contract.LibGeneratedIsVirtual(target)) {
                    // 2.2.0/2.2.1: the generated Init of a base in another assembly is virtual only when that assembly has
                    // generated subclasses of it ("keep such hierarchies in one assembly"): CS0506 is the documented result.
                    continue;
                }
                if (!d.Unresolved && c != null && new[] { c }.Concat(p.Ancestors(c)).Any(x => brokenClasses.Contains(x.Id))) {
                    // A follow-up of broken generated code in this class or its base (a garbage
                    // constructor there hides the default one): the generated-code error is the finding.
                    continue;
                }
                if (!d.Unresolved) {
                    string userToken = NormToken(d.Token);
                    result.Add(new Violation {
                        Rule = "R-GEN-ERROR", Class = "generated-code-error", Severity = "medium", ClassId = d.ClassId,
                        Key = $"GENERR-USER|{d.Id}|{userToken}{MemberMechanism(p, d.ClassId, userToken)}",
                        Message = $"{unit.Asm}: {d.Id} in the program's own {d.File}:{d.Line} ({Name(p, d.ClassId)}), caused by the generated code (absent without the generator): {d.Message}",
                        Doc = "universal: generated members must not conflict with valid user code",
                    });
                    continue;
                }
                // Unresolved: user code still needs a generated member.
                if (crashed || c == null) {
                    continue;
                }
                var e = contract.For(c);
                bool explained = (reportedBy.ContainsKey(c.Id) && anyKecsError) || e.Kind == ExpKind.Reports || e.Kind == ExpKind.Blocked
                    || (e.Kind == ExpKind.Hand && anyKecsError) || (anyKecsError && p.Ancestors(c).Any(a => reportedBy.ContainsKey(a.Id)));
                if (explained) {
                    continue;
                }
                bool alias = c.IsGenerated && c.Fields.Where(f => !f.Static).All(f => f.Spelling == "alias");
                bool abstract21 = !contract.Is22 && d.Id == "CS0534";
                result.Add(new Violation {
                    Rule = "R-GEN-MISSING", Class = alias ? "missed-alias-attribute" : "missing-generated-member", Severity = alias || abstract21 ? "question" : "medium", ClassId = c.Id,
                    Key = alias ? "MISSING|alias" : $"MISSING|{d.Id}|{e.Shape}",
                    Message = alias
                        ? $"{c.Plain}: every injected field uses a using-alias of the attribute ([InjPool] for KenseiECS.PoolAttribute); the generator does not see them and writes no Init ({d.Id}: {d.Message})"
                        : abstract21
                            ? $"{c.Plain}: an abstract base Init stays unimplemented ({d.Id}); 2.1 generates a plain `public void Init`, and the 2.1 documents do not cover abstract bases"
                            : $"{unit.Asm}: {d.Id} stays after the generator ran ({c.Plain}): {d.Message}",
                    Doc = alias ? "the documents name [Inc]/[Pool]/... only; aliases are not mentioned" : abstract21 ? Contract.Log211 : "universal",
                });
            }

            // A documented error the generator did not report, while the unit still built: judged at
            // run time (null fields make it a finding); a quiet success is a contract question.
            if (unit.Image != null) {
                foreach (var c in p.ActiveClasses(unit.Asm)) {
                    var e = contract.For(c);
                    if (e.Kind == ExpKind.Reports && !reportedBy.ContainsKey(c.Id)) {
                        result.Add(new Violation {
                            Rule = "R-MISSING-KECS", Class = "missing-diagnostic", Severity = "question", ClassId = c.Id,
                            Key = $"MISSINGKECS|{string.Join("/", e.Ids)}|{e.Shape}",
                            Message = $"{c.Plain}: the contract asks for {string.Join("/", e.Ids)} ({e.Layout}), the generator reported nothing and the assembly built",
                            Doc = e.Doc,
                        });
                    }
                    // KECS013 is a warning: the build goes through without it, so a missing one is the lost signal itself.
                    if (e.Kind == ExpKind.Hand && contract.Kecs013Site(c) == "expected"
                        && !(reportedBy.TryGetValue(c.Id, out var mine) && mine.Any(d => d.Id == "KECS013"))) {
                        var target = contract.OverriddenGenerated(c);
                        result.Add(new Violation {
                            Rule = "R-MISSING-WARNING", Class = "missing-diagnostic", Severity = "medium", ClassId = c.Id,
                            Key = "MISSINGWARN|KECS013|" + (target != null && target.Asm != c.Asm ? "other-assembly" : "same-assembly"),
                            Message = $"{c.Plain} overrides the generated Init of {target?.Plain} without base.Init and the build has no KECS013: the base fields stay null and the base OnInit is skipped without a word",
                            Doc = DocKecs013, Claim = "kecs013",
                        });
                    }
                }
            }
        }

        private const string DocDiagnosticsTable = "docs/guides/source-generator.md, Diagnostics: all diagnostics are KECS errors";

        private const string DocKecs013 = "docs/guides/source-generator.md 2.2.2, Inheritance: 'an override of a generated Init without that call leaves the base fields null and skips the base OnInit, which is the warning KECS013'";

        private const string DocHook222 = "docs/guides/source-generator.md 2.2.2, A base with its own OnInit: 'The generated part then declares no partial void OnInit over it (that would hide the hook). Override the hook, and it runs once, after the derived fields are filled'";

        /// <summary> "All diagnostics are errors." (2.1.0-2.2.1), "All diagnostics are errors, except the warning KECS013." (2.2.2). </summary>
        private const string ClaimDiagnostics = "diagnostics-are-errors|diagnostics-are-errors-222";

        /// <summary>
        /// What is wrong with the text of a diagnostic the contract expects on <paramref name="c"/>: the KECS006 reason
        /// ("The message names the reason") and the class it names, and the names every other message carries (the class,
        /// the field, the containing type, the offending component type). Null when the text is right or not checked.
        /// </summary>
        private static (string Kind, string Text)? MessageProblem(ProgramModel p, Contract contract, ClassModel c, Expectation e, Diag d) {
            string msg = d.Message ?? "";
            bool Names(string name) => msg.Contains("'" + (name ?? "").TrimStart('@') + "'");
            bool NamesPrefix(string name) => msg.Contains("'" + (name ?? "").TrimStart('@'));
            if (d.Id == "KECS013") {
                if (contract.Kecs013Site(c) == null) {
                    return null;
                }
                var target = contract.OverriddenGenerated(c);
                if (!Names(c.Plain)) {
                    return ("class", $"the message does not name the class '{c.Plain}'");
                }
                return target != null && !Names(target.Plain) ? ("arg-base", $"the message does not name the generated base '{target.Plain}' whose Init never runs") : null;
            }
            if (e.Kind != ExpKind.Reports || !e.Ids.Contains(d.Id)) {
                return null;
            }
            var fields = c.Fields.Where(f => !f.Static).ToList();
            switch (d.Id) {
                case "KECS001":
                case "KECS002":
                case "KECS012":
                    return Names(c.Plain) ? null : ("class", $"the message does not name the class '{c.Plain}'");
                case "KECS006":
                    if (!Names(c.Plain)) {
                        return ("class", $"the message does not name the class '{c.Plain}'");
                    }
                    if (e.Reasons.Count == 0) {
                        return null;
                    }
                    if (!e.Reasons.Any(r => msg.Contains(r.Word))) {
                        string words = string.Join(" or ", e.Reasons.Select(r => "\"" + r.Word + "\"").Distinct());
                        return ("reason:" + e.Reasons[0].Word.Replace(' ', '-'), $"the contract's reason is {words} ({e.Layout}), the message names another one");
                    }
                    var named = e.Reasons.Where(r => msg.Contains(r.Word)).ToList();
                    if (named.Any(r => p.Cls(r.Of) is ClassModel of && NamesPrefix(of.Plain))) {
                        return null;
                    }
                    var of0 = p.Cls(named[0].Of);
                    return of0 == null ? null : ("reason-class", $"the reason does not name '{of0.Plain}', the class it is about ({e.Layout})");
                case "KECS010":
                    if (!Names(c.Plain)) {
                        return ("class", $"the message does not name the type '{c.Plain}'");
                    }
                    return msg.Contains(c.Kind) ? null : ("arg-kind", $"the message does not say the type is a {c.Kind}");
                case "KECS003": {
                    var wrong = fields.Where(f => f.WrongType != null).ToList();
                    if (wrong.Count == 0) {
                        return null;
                    }
                    if (!wrong.Any(f => Names(f.Name))) {
                        return ("arg-field", $"the message does not name the field '{wrong[0].Name.TrimStart('@')}'");
                    }
                    return wrong.Any(f => msg.Contains(AttributeWord(f.Kind))) ? null : ("arg-attribute", $"the message does not name the attribute {AttributeWord(wrong[0].Kind)} of the field");
                }
                case "KECS004":
                case "KECS007": {
                    var bad = fields.Where(f => f.Kind == FieldKind.Filter && (f.Inc.Count == 0 || (f.Inc.Count == 1 && f.Inc[0] == "")) && f.Any.Count == 0).ToList();
                    return bad.Count == 0 || bad.Any(f => Names(f.Name)) ? null : ("arg-field", $"the message does not name the filter field '{bad[0].Name.TrimStart('@')}'");
                }
                case "KECS008": {
                    var bad = fields.Where(f => f.Kind == FieldKind.Filter && f.Inc.Concat(f.Exc).Concat(f.Any).Any(t => t.Contains("NotComponent"))).ToList();
                    if (bad.Count == 0) {
                        return null;
                    }
                    if (!bad.Any(f => Names(f.Name))) {
                        return ("arg-field", $"the message does not name the field '{bad[0].Name.TrimStart('@')}'");
                    }
                    if (!msg.Contains("NotComponent")) {
                        return ("arg-type", "the message does not name the type that is not a component (NotComponent)");
                    }
                    var f0 = bad[0];
                    string list = f0.Inc.Any(t => t.Contains("NotComponent")) ? "Inc" : f0.Exc.Any(t => t.Contains("NotComponent")) ? "Exc" : "Any";
                    return msg.Contains("[" + list + "]") ? null : ("arg-attribute", $"the message does not name [{list}], the attribute that lists the type");
                }
                case "KECS009": {
                    var bad = fields.Where(f => f.Readonly).ToList();
                    return bad.Count == 0 || bad.Any(f => Names(f.Name)) ? null : ("arg-field", $"the message does not name the readonly field '{bad[0].Name.TrimStart('@')}'");
                }
                case "KECS005": {
                    var outer = c.Outers.FirstOrDefault(o => !o.Partial && o.Kind is OuterKind.Class or OuterKind.Struct or OuterKind.StaticClass);
                    if (!Names(c.Plain)) {
                        return ("class", $"the message does not name the class '{c.Plain}'");
                    }
                    return outer == null || Names(outer.Name) ? null : ("arg-outer", $"the message does not name the containing type '{outer.Name.TrimStart('@')}' that is not partial");
                }
                case "KECS011": {
                    var outer = c.Outers.FirstOrDefault(o => o.Kind is OuterKind.Record or OuterKind.Interface);
                    if (outer == null) {
                        return null;
                    }
                    if (!Names(outer.Name)) {
                        return ("arg-outer", $"the message does not name the containing {outer.Kind.ToString().ToLowerInvariant()} '{outer.Name.TrimStart('@')}'");
                    }
                    return msg.Contains(outer.Kind.ToString().ToLowerInvariant()) ? null : ("arg-kind", $"the message does not say the containing type is a {outer.Kind.ToString().ToLowerInvariant()}");
                }
                default:
                    return null;
            }
        }

        private static string AttributeWord(FieldKind kind) => kind switch {
            FieldKind.Filter => "Inc",
            FieldKind.Pool => "[Pool]",
            FieldKind.Group => "[Group]",
            _ => "[Shared]",
        };

        private static bool IsInitTag(string tag, string id) {
            int colon = tag.IndexOf(':');
            if (colon < 0 || tag.Substring(colon + 1) != id) {
                return false;
            }
            string what = tag.Substring(0, colon);
            return what == "Init" || what == "InitV" || what == "InitX";
        }

        private static string What(string tag) => tag.Substring(0, tag.IndexOf(':'));

        private static string Who(string tag) => tag.Substring(tag.IndexOf(':') + 1);

        private static void CheckRun(ProgramModel p, Contract contract, Observation obs, RunObservation run, List<Violation> result) {
            int before = result.Count;
            CheckRunCore(p, contract, obs, run, result);
            for (int i = before; i < result.Count; i++) {
                result[i].RunId ??= run.ClassId;
            }
        }

        /// <summary>
        /// Why the generator threw, from the program model: two classes whose hint names
        /// (<see cref="Renderer.HintOf"/>) are equal (exact-collision) or equal ignoring case only
        /// (case-collision; the generator driver compares hint names without case), or a hint name
        /// with a character the driver rejects ('@' of a keyword identifier).
        /// </summary>
        public static string CrashMechanism(ProgramModel p, UnitResult unit, string text) {
            if (text == null) {
                return "other";
            }
            if (text.Contains("invalid character")) {
                return "invalid-hint-char";
            }
            if (text.Contains("must be unique")) {
                var hints = p.ActiveClasses(unit?.Asm ?? Asm.Main).Where(c => c.IsGenerated).Select(c => Renderer.HintOf(p, c)).ToList();
                bool exact = hints.GroupBy(h => h, StringComparer.Ordinal).Any(g => g.Count() > 1);
                if (exact) {
                    return "exact-collision";
                }
                bool caseOnly = hints.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
                return caseOnly ? "case-collision" : "unique-other";
            }
            return "other";
        }

        // A KECS error that leaves the class without its generated Init (all but KECS012, after which the
        // class is still emitted).
        private static bool HardReported(Dictionary<string, List<Diag>> reportedBy, string id) =>
            reportedBy.TryGetValue(id, out var list) && list.Any(d => d.Id.StartsWith("KECS") && d.Id != "KECS012");

        /// <summary> Whether an identifier of the class, its namespace, containers, type parameters or fields is a keyword written with '@'. </summary>
        public static bool KeywordInvolved(ProgramModel p, ClassModel c) {
            for (var x = c; x != null; x = p.Cls(x.NestedIn)) {
                if (x.Name.Contains('@') || (x.Ns ?? "").Contains('@') || (x.TypeParams ?? "").Contains('@')
                    || x.Outers.Any(o => o.Name.Contains('@') || (o.TypeParams ?? "").Contains('@')) || x.Fields.Any(f => f.Name.StartsWith("@"))) {
                    return true;
                }
            }
            return false;
        }

        /// <summary> For a diagnostic on OnInit: whether a hand-written OnInit hook of the class or a base, or nesting in the base, is involved. </summary>
        private static string MemberMechanism(ProgramModel p, string classId, string token) {
            if (token != "OnInit") {
                return "";
            }
            var c = p.Cls(classId);
            if (c == null) {
                return "|other";
            }
            if (new[] { c }.Concat(p.Ancestors(c)).Any(x => x.Hook != HookKind.None)) {
                return "|hook";
            }
            var ancestors = new HashSet<string>(p.Ancestors(c).Select(a => a.Id));
            for (var outer = p.Cls(c.NestedIn); outer != null; outer = p.Cls(outer.NestedIn)) {
                if (ancestors.Contains(outer.Id)) {
                    return "|nested-in-base";
                }
            }
            return "|other";
        }

        private static void CheckRunCore(ProgramModel p, Contract contract, Observation obs, RunObservation run, List<Violation> result) {
            var leaf = p.Cls(run.ClassId);
            if (leaf == null) {
                return;
            }
            var chain = new List<ClassModel> { leaf };
            chain.AddRange(p.Ancestors(leaf));
            if (run.Exception != null) {
                // Every [Shared] (type, key) of the program is registered, so a failed lookup means the
                // generated Init asked for another key or type. Other exceptions come from the runtime
                // (a stand program it rejects) and stay inconclusive.
                bool lookup = run.Exception.StartsWith("KeyNotFoundException") && run.Exception.Contains("SharedData");
                result.Add(new Violation {
                    Rule = "R-RUNTIME-EXCEPTION", Class = lookup ? "wrong-injection" : "inconclusive", Severity = lookup ? "medium" : "note", ClassId = leaf.Id,
                    Key = "EXC|" + NormException(run.Exception),
                    Message = $"{leaf.Plain}: SystemsRunner.Init threw {run.Exception}",
                    Doc = lookup ? "docs/guides/source-generator.md: [Shared(\"key\")] is injected with shared.Get<T>(\"key\")" : null,
                    Claim = lookup ? "inject-table" : null,
                });
                return;
            }
            var events = run.Events;
            bool Executed(ClassModel c) {
                if (c.IsGenerated) {
                    bool anySet = c.Fields.Where(f => !f.Static).Any(f => !run.NullAfter.Contains(c.Id + "." + f.Name.TrimStart('@')));
                    return anySet || events.Any(e => e.Tag == "OnInit:" + c.Id);
                }
                return events.Any(e => IsInitTag(e.Tag, c.Id));
            }
            bool Relevant(ClassModel c) => c.IsGenerated || Contract.IsExactInitMethod(c.Init);
            var unitOf = new Func<ClassModel, UnitResult>(c => c.Asm == Asm.Lib ? obs.Lib : obs.Main);
            bool reported(ClassModel c) => unitOf(c)?.GeneratorDiagnostics.Any(d => d.ClassId == c.Id && d.Id.StartsWith("KECS")) == true;

            // 1. The chain from the runner's call upward: the first class that should run but did not.
            ClassModel breaker = null;
            ClassModel missing = null;
            foreach (var c in chain) {
                if (!Relevant(c)) {
                    continue;
                }
                if (Executed(c)) {
                    breaker = c;
                    continue;
                }
                missing = c;
                break;
            }
            if (missing != null) {
                var nulls = run.NullAfter.Where(n => chain.SkipWhile(c => c.Id != missing.Id).Any(c => n.StartsWith(c.Id + "."))).ToList();
                string nullText = nulls.Count == 0 ? "" : " Null after Init: " + string.Join(", ", nulls.Select(n => Name(p, n.Split('.')[0]) + "." + n.Split('.')[1])) + ".";
                string missingWhat = missing.IsGenerated ? $"the generated Init of {missing.Plain}" : $"the hand-written {missing.Init} Init of {missing.Plain}";
                // A hand-written Init at or below the class that did not run decides the dispatch itself
                // (a `new` Init that does not re-implement IInitSystem never runs, by C# rules): the
                // program's business. The generated classes of the chain show their own defects in
                // their own runs.
                bool userDispatch = !missing.IsGenerated || chain.TakeWhile(c => c.Id != missing.Id).Any(c => !c.IsGenerated && Contract.IsExactInitMethod(c.Init));
                if (breaker == null && userDispatch) {
                    // nothing to say
                } else if (breaker == null) {
                    if (nulls.Count > 0 || missing.IsGenerated) {
                        string why = !run.IsInitSystem
                            ? $"{leaf.Plain} does not implement IInitSystem, so SystemsRunner.Init never calls it"
                            : $"SystemsRunner.Init calls IInitSystem.Init of {leaf.Plain}, which does not reach {missingWhat}";
                        var eLeaf = contract.For(leaf);
                        var crashUnit = obs.Units.FirstOrDefault(u => u.GeneratorException != null);
                        string crash = crashUnit?.GeneratorException;
                        // The layout of the class whose generated Init does not run names the mechanism
                        // (a hand-written or blocked leaf below it only inherits the problem).
                        var firstGenerated = chain.FirstOrDefault(c => c.IsGenerated);
                        var eMissing = missing.IsGenerated ? contract.For(missing) : firstGenerated != null ? contract.For(firstGenerated) : eLeaf;
                        result.Add(new Violation {
                            Rule = "R-FIELDS-NULL", Class = "silent-non-injection", Severity = "high", ClassId = missing.IsGenerated ? missing.Id : leaf.Id, RunId = leaf.Id,
                            Key = crash != null ? "NULL|after-crash|" + ExceptionType(crash) + "|" + CrashMechanism(p, crashUnit, crash) : $"NULL|no-dispatch|{eMissing.Shape}",
                            Message = $"{why}; the build had no KECS error.{nullText}" + (eLeaf.Kind == ExpKind.Reports ? $" The contract asks for {string.Join("/", eLeaf.Ids)} here ({eLeaf.Layout})." : $" Contract: {eLeaf}."),
                            Doc = eLeaf.Doc, Claim = eMissing.Kind is ExpKind.Works or ExpKind.Reports ? eMissing.Claim : null,
                        });
                    }
                } else if (!breaker.IsGenerated) {
                    // The program's own code stopped the chain (an override without base.Init, a
                    // re-implementation): not the generator's business, unless a base call was lost.
                    bool callsBase = Contract.HandCallsBase(p, breaker);
                    bool captured = events.Any(e => e.Tag.StartsWith("InitO:"));
                    if (callsBase && !captured) {
                        result.Add(new Violation {
                            Rule = "R-CHAIN-BROKEN", Class = "silent-non-injection", Severity = "high", ClassId = breaker.Id, RunId = leaf.Id,
                            Key = "CHAIN|hand-override-base-call-lost",
                            Message = $"{breaker.Plain}.Init calls base.Init, but {missingWhat} did not run.{nullText}",
                            Doc = Contract.DocInheritance, Claim = "inheritance",
                        });
                    }
                } else {
                    var e = contract.For(breaker);
                    // The program's own base.Init call landed in its own Init overload (C# binding
                    // the stand reproduces on purpose): the generator could have warned, nothing more.
                    var between = chain.SkipWhile(c => c.Id != breaker.Id).Skip(1).TakeWhile(c => c.Id != missing.Id).ToList();
                    var diverted = between.FirstOrDefault(c => Contract.IsCaptureOverload(c.Init) && events.Any(ev => ev.Tag == "InitO:" + c.Id));
                    if (diverted != null && e.Kind != ExpKind.Reports) {
                        result.Add(new Violation {
                            Rule = "R-FIELDS-NULL", Class = "silent-non-injection", Severity = "question", ClassId = breaker.Id,
                            Key = $"NULLQ|diverted|{e.Shape}",
                            Message = $"{breaker.Plain}: base.Init(world, shared) in OnInit binds to {diverted.Plain}'s {diverted.Init} overload, so {missingWhat} never runs.{nullText} " +
                                      "C# binding of the program's own call; the documents report such overloads (KECS006) only for the generated base.Init.",
                            Doc = e.Doc,
                        });
                    } else if (e.SkipAllowed) {
                        if (nulls.Count > 0 && e.SkipIsDocumented) {
                            // The documents of this tree describe exactly this: the fields stay null without a diagnostic.
                            result.Add(new Violation {
                                Rule = "R-FIELDS-NULL", Class = "silent-non-injection", Severity = "low", Documented = true, ClassId = breaker.Id,
                                Key = $"NULLD|{e.Shape}",
                                Message = $"{breaker.Plain} ({e.Layout}): its Init does not call the base and OnInit does not either, so {missingWhat} never runs.{nullText} " +
                                          "The documents of this tree describe it: KECS006 does not see private injected fields of a base in another assembly.",
                                Doc = e.Doc, Claim = e.Claim,
                            });
                        } else if (nulls.Count > 0 && e.SkipIsQuestion) {
                            result.Add(new Violation {
                                Rule = "R-FIELDS-NULL", Class = "silent-non-injection", Severity = "question", ClassId = breaker.Id,
                                Key = $"NULLQ|{e.Shape}",
                                Message = $"{breaker.Plain} ({e.Layout}): its Init does not call the base and OnInit does not either, so {missingWhat} never runs.{nullText} " +
                                          "The documents allow skipping the base in this layout; the fields above are not visible to the KECS006 rule (private or internal in another assembly).",
                                Doc = e.Doc,
                            });
                        }
                    } else if (nulls.Count > 0 || missing.IsGenerated || e.Chained || (e.Kind == ExpKind.Reports && !reported(breaker))) {
                        // The last case: the documents ask for an error here because this base Init would
                        // never run, the generator reported nothing, and it indeed did not run.
                        bool missingKecs = e.Kind == ExpKind.Reports && !reported(breaker);
                        result.Add(new Violation {
                            Rule = nulls.Count > 0 ? "R-FIELDS-NULL" : "R-BASE-INIT-SKIPPED", Class = "silent-non-injection", Severity = "high", ClassId = breaker.Id,
                            Key = $"{(nulls.Count > 0 ? "NULL" : "SKIP")}|{e.Shape}",
                            Message = $"The generated Init of {breaker.Plain} runs but {missingWhat} does not; no KECS error.{nullText}" +
                                      (missingKecs ? $" The contract asks for {string.Join("/", e.Ids)} here ({e.Layout})." : $" Contract: {e}."),
                            Doc = e.Doc, Claim = e.Kind is ExpKind.Works or ExpKind.Reports ? e.Claim : null,
                        });
                    }
                }
            }

            // 2. A generated Init that ran must fill every field of its class and call OnInit once.
            foreach (var c in chain.Where(c => c.IsGenerated && Executed(c))) {
                var own = c.Fields.Where(f => !f.Static).Select(f => c.Id + "." + f.Name.TrimStart('@')).ToList();
                var dropped = own.Where(run.NullAfter.Contains).ToList();
                if (dropped.Count > 0) {
                    result.Add(new Violation {
                        Rule = "R-FIELD-DROPPED", Class = "silent-non-injection", Severity = "high", ClassId = c.Id,
                        Key = "DROPPED|" + string.Join("/", c.Fields.Where(f => dropped.Contains(c.Id + "." + f.Name.TrimStart('@'))).Select(f => f.Kind + ":" + f.Spelling).Distinct()),
                        Message = $"{c.Plain}: the generated Init ran but left {string.Join(", ", dropped.Select(d => d.Split('.')[1]))} null",
                        Doc = "docs/guides/source-generator.md: the generated Init fills the marked fields", Claim = "generated-init",
                    });
                }
                // The value of every injected field: the documents name the call that fills it.
                foreach (var w in run.Wrong.Where(w => w.Field.StartsWith(c.Id + "."))) {
                    string field = w.Field.Substring(c.Id.Length + 1);
                    result.Add(new Violation {
                        Rule = "R-WRONG-INJECTION", Class = "wrong-injection", Severity = "high", ClassId = c.Id, Claim = "inject-table",
                        Key = $"WRONG|{w.Kind}|{w.Mechanism}",
                        Message = $"{c.Plain}.{field}: the generated Init injected {w.Actual}; the documents say {w.Expected}",
                        Doc = "docs/guides/source-generator.md, What gets generated: " + w.Kind switch {
                            "Filter" => "[Inc]/[Exc]/[Any] -> world.Filter().Inc<...>().Exc<...>().Any<...>().End()",
                            "Pool" => "[Pool] -> world.Pool<T>()",
                            "Group" => "[Group] -> world.Group<...>()",
                            _ => "[Shared] / [Shared(\"key\")] -> shared.Get<T>() / shared.Get<T>(\"key\")",
                        },
                    });
                }
                int onInits = events.Count(e => e.Tag == "OnInit:" + c.Id);
                if (onInits > 1) {
                    result.Add(new Violation {
                        Rule = "R-ONINIT-TWICE", Class = "init-count", Severity = "high", ClassId = c.Id, Key = "ONINIT-TWICE|" + contract.For(c).Shape,
                        Message = $"{c.Plain}.OnInit ran {onInits} times in one SystemsRunner.Init", Doc = Contract.DocInheritance, Claim = "generated-init",
                    });
                } else if (onInits == 0 && c.OnInit) {
                    result.Add(new Violation {
                        Rule = "R-ONINIT-MISSING", Class = "init-count", Severity = "high", ClassId = c.Id, Key = "ONINIT-MISSING|" + contract.For(c).Shape,
                        Message = $"{c.Plain}: fields were injected but OnInit did not run", Doc = "docs/guides/source-generator.md: the generated Init fills the fields, then calls OnInit", Claim = "generated-init",
                    });
                }
            }
            foreach (var c in chain.Where(c => !c.IsGenerated)) {
                foreach (var what in new[] { "Init", "InitV", "InitX", "Hook" }) {
                    int n = events.Count(e => e.Tag == what + ":" + c.Id);
                    if (n > 1) {
                        result.Add(new Violation {
                            Rule = "R-BASE-INIT-TWICE", Class = "init-count", Severity = "high", ClassId = c.Id, Key = $"TWICE|{what}|{c.Init}",
                            Message = $"{c.Plain}: {what} ran {n} times in one SystemsRunner.Init", Doc = Contract.DocInheritance + " (each Init once, base first)",
                        });
                    }
                }
            }

            // 3. Order: whatever ran of a base before the derived OnInit, with the base fields set; a
            // chained base before the derived fields.
            for (int i = 0; i < events.Count; i++) {
                var ev = events[i];
                if (What(ev.Tag) != "OnInit") {
                    continue;
                }
                var c = p.Cls(Who(ev.Tag));
                if (c == null || !chain.Contains(c)) {
                    continue;
                }
                var ownNull = ev.NullFields.Where(n => n.StartsWith(c.Id + ".")).ToList();
                if (ownNull.Count > 0) {
                    result.Add(new Violation {
                        Rule = "R-ONINIT-BEFORE-FIELDS", Class = "init-order", Severity = "high", ClassId = c.Id, Key = "ONINIT-EARLY|" + contract.For(c).Shape,
                        Message = $"{c.Plain}.OnInit ran while its own field(s) {string.Join(", ", ownNull)} were null",
                        Doc = "docs/guides/source-generator.md: the generated Init fills the marked fields, then calls OnInit", Claim = "generated-init",
                    });
                }
                foreach (var a in p.Ancestors(c).Where(Executed)) {
                    var baseNull = ev.NullFields.Where(n => n.StartsWith(a.Id + ".")).ToList();
                    int first = events.FindIndex(x => Who(x.Tag) == a.Id && (What(x.Tag) is "Init" or "InitV" or "InitX" or "OnInit"));
                    if (baseNull.Count > 0 || (first > i)) {
                        // The base runs before the derived OnInit in every 2.2 version: "the base first, then the derived
                        // class" (2.2.0/2.2.1), "then the base Init runs (...), then the derived OnInit" (2.2.2).
                        result.Add(new Violation {
                            Rule = "R-ORDER", Class = "init-order", Severity = "high", ClassId = c.Id, Key = "ORDER|" + contract.For(c).Shape,
                            Message = $"{c.Plain}.OnInit ran before the base {a.Plain} was initialised" + (baseNull.Count > 0 ? $" ({string.Join(", ", baseNull)} null)" : ""),
                            Doc = Contract.DocInheritance + ": base fields and base OnInit first, then the derived OnInit", Claim = "inheritance-order|inheritance-order-222",
                        });
                    }
                }
            }
            // The chain target against the derived fields. 2.2.0/2.2.1: "the derived one overrides it and calls base.Init
            // before its own fields" (the base sees them null). 2.2.2: "The derived fields are filled first, then the base
            // Init runs (...): the base OnInit, and any virtual method it calls, already sees the derived fields".
            foreach (var c in chain.Where(c => c.IsGenerated)) {
                var e = contract.For(c);
                if (!e.Chained || e.ChainTarget == null) {
                    continue;
                }
                var target = p.Cls(e.ChainTarget);
                var ev = events.FirstOrDefault(x => Who(x.Tag) == target.Id && (What(x.Tag) is "Init" or "InitV" or "InitX" or "OnInit" or "Hook"));
                if (ev == null) {
                    continue;
                }
                var own = c.Fields.Where(f => !f.Static).Select(f => c.Id + "." + f.Name.TrimStart('@')).ToList();
                if (contract.FieldsBeforeBase) {
                    var stillNull = own.Where(ev.NullFields.Contains).ToList();
                    if (stillNull.Count > 0) {
                        result.Add(new Violation {
                            Rule = "R-ORDER", Class = "init-order", Severity = "high", ClassId = c.Id, Key = "ORDER-FIELDS-FIRST|" + e.Shape,
                            Message = $"the base {target.Plain} ran before the fields of {c.Plain} were filled ({string.Join(", ", stillNull)} null)",
                            Doc = Contract.DocInheritance + " (2.2.2): 'The derived fields are filled first, then the base Init runs (the base fields and the base OnInit), then the derived OnInit'",
                            Claim = "inheritance-order-222",
                        });
                    }
                    continue;
                }
                var derivedSet = own.Where(n => !ev.NullFields.Contains(n)).ToList();
                if (derivedSet.Count > 0) {
                    result.Add(new Violation {
                        Rule = "R-ORDER", Class = "init-order", Severity = "high", ClassId = c.Id, Key = "ORDER-CHAIN|" + e.Shape,
                        Message = $"the base {target.Plain} ran after the fields of {c.Plain} were already filled ({string.Join(", ", derivedSet)})",
                        Doc = Contract.DocInheritance + " (2.2.0/2.2.1): 'the derived one overrides it and calls base.Init before its own fields'", Claim = "order-chain-220",
                    });
                }
            }
            // 2.2.2: an override of a base OnInit hook "runs once, after the derived fields are filled".
            if (contract.HookSupported) {
                foreach (var c in chain.Where(c => c.IsGenerated && c.Hook == HookKind.Override && Executed(c))) {
                    var hits = events.Where(x => x.Tag == "HookOv:" + c.Id).ToList();
                    var own = c.Fields.Where(f => !f.Static).Select(f => c.Id + "." + f.Name.TrimStart('@')).ToList();
                    if (hits.Count != 1) {
                        result.Add(new Violation {
                            Rule = "R-HOOK-ONCE", Class = "init-count", Severity = "high", ClassId = c.Id, Key = "HOOK-RUNS|" + (hits.Count == 0 ? "never" : "twice") + "|" + contract.For(c).Shape,
                            Message = $"{c.Plain}'s override of the base OnInit hook ran {hits.Count} times in one SystemsRunner.Init",
                            Doc = DocHook222, Claim = "hook-222",
                        });
                    } else if (own.Any(hits[0].NullFields.Contains)) {
                        result.Add(new Violation {
                            Rule = "R-HOOK-ONCE", Class = "init-order", Severity = "high", ClassId = c.Id, Key = "HOOK-BEFORE-FIELDS|" + contract.For(c).Shape,
                            Message = $"{c.Plain}'s override of the base OnInit hook ran while its own fields were null ({string.Join(", ", own.Where(hits[0].NullFields.Contains))})",
                            Doc = DocHook222, Claim = "hook-222",
                        });
                    }
                }
            }
        }
    }
}
