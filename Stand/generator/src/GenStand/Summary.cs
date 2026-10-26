using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    /// <summary> Short human-readable outcome of a program (layout table, replay). </summary>
    public static class Summary {
        public static string Outcome(ProgramModel p, Observation obs, string focusId) {
            var parts = new List<string>();
            if (obs.Invalid) {
                return "the stand program does not compile even without the generator";
            }
            bool twoUnits = obs.Lib != null;
            foreach (var u in obs.Units) {
                string asm = twoUnits ? u.Asm + ": " : "";
                if (u.GeneratorException != null || u.GeneratorDiagnostics.Any(d => d.Id == "CS8785")) {
                    parts.Add(asm + "the generator threw (only CS8785)");
                }
                foreach (var g in u.GeneratorDiagnostics.Where(d => d.Id.StartsWith("KECS")).GroupBy(d => d.Id)) {
                    var names = g.Select(d => p.Cls(d.ClassId)?.Plain ?? d.ClassName ?? "?").Distinct();
                    parts.Add($"{asm}{g.Key} on {string.Join(", ", names)}");
                }
                var genErrors = u.Compile.Where(d => d.InGenerated && d.Severity == "Error").Select(d => d.Id).Distinct().ToList();
                if (genErrors.Count > 0) {
                    parts.Add($"{asm}{string.Join("/", genErrors.Take(3))}{(genErrors.Count > 3 ? "/…" : "")} in the generated code");
                }
                var genWarnings = u.Compile.Where(d => d.InGenerated && d.Severity == "Warning").Select(d => d.Id).Distinct().ToList();
                if (genWarnings.Count > 0) {
                    parts.Add($"{asm}warning {string.Join("/", genWarnings)} in the generated code");
                }
                var userErrors = u.Compile.Where(d => !d.InGenerated && d.Severity == "Error").Select(d => d.Id).Distinct().ToList();
                if (userErrors.Count > 0) {
                    parts.Add($"{asm}{string.Join("/", userErrors)} in the program's own code");
                }
            }
            bool built = obs.Units.Any() && obs.Units.All(u => u.Image != null);
            if (!built) {
                parts.Add("the build fails");
                return string.Join("; ", parts);
            }
            if (obs.RuntimeError != null) {
                parts.Add("the run failed: " + obs.RuntimeError);
                return string.Join("; ", parts);
            }
            var focus = p.Cls(focusId);
            var runs = obs.Runs.Where(r => r.ClassId == focusId).ToList();
            if (runs.Count == 0 && focus != null) {
                runs = obs.Runs.Where(r => r.Chain.Contains(focusId)).Take(1).ToList();
            }
            if (runs.Count == 0) {
                runs = obs.Runs.Take(3).ToList();
            }
            if (runs.Count == 0) {
                parts.Add("builds; nothing to run");
            }
            foreach (var r in runs) {
                parts.Add(Run(p, r, runs.Count > 1 || r.ClassId != focusId));
            }
            return string.Join("; ", parts);
        }

        public static string Run(ProgramModel p, RunObservation r, bool named) {
            var c = p.Cls(r.ClassId);
            string who = named ? (c?.Plain ?? r.ClassId) + ": " : "";
            if (r.Exception != null) {
                return $"{who}Init threw {r.Exception.Split(':')[0]}";
            }
            var calls = r.Events.Select(e => {
                int colon = e.Tag.IndexOf(':');
                return (p.Cls(e.Tag.Substring(colon + 1))?.Plain ?? "?") + "." + e.Tag.Substring(0, colon);
            }).ToList();
            string trace = calls.Count == 0 ? "no hand-written call" : string.Join(" → ", calls);
            if (!r.IsInitSystem) {
                trace = "not an IInitSystem, Init not called";
            }
            string nulls = r.NullAfter.Count == 0 ? "every field filled" : "null after Init: " + string.Join(", ", r.NullAfter.Select(n => (p.Cls(n.Split('.')[0])?.Plain ?? "?") + "." + n.Split('.')[1]));
            string wrong = r.Wrong.Count == 0 ? "" : "; wrong injection: " + string.Join(", ", r.Wrong.Select(w => (p.Cls(w.Field.Split('.')[0])?.Plain ?? "?") + "." + w.Field.Split('.')[1] + " (" + w.Mechanism + ")"));
            return $"{who}{trace}; {nulls}{wrong}";
        }
    }

    /// <summary> One-line title of a finding, from its violation and the minimized program. </summary>
    public static class Titles {
        public static string For(Violation v, ProgramModel p, Contract contract) {
            var c = p.Cls(v.ClassId) ?? p.Cls(v.RunId);
            string name = c?.Plain ?? "?";
            string layout = c == null ? "" : contract.For(c).Layout;
            var key = v.Key.Split('|');
            if (v.Key.StartsWith("ALIAS|")) {
                var inner = Titles.For(new Violation { Rule = v.Rule, Key = v.OriginalKey ?? "", Message = v.Message, ClassId = v.ClassId, RunId = v.RunId }, p, contract);
                return "A class whose injected fields are all marked through a using alias of the attribute is not seen by the generator (docs/architecture.md: the receiver looks for the short names Inc/Exc/Any/Pool/Group/Shared); the consequence: " + char.ToLowerInvariant(inner[0]) + inner.Substring(1);
            }
            if (v.Key.Contains("capture-above-") && !v.Question) {
                return "An Init overload above the chained override (" + v.Key.Substring(v.Key.IndexOf("capture-above-") + "capture-above-".Length).Split(':', '|')[0]
                    + ") takes the generated base.Init: C# binds the call to it, the base Init silently does not run, the documented KECS006 is missing; layout: " + layout;
            }
            switch (v.Rule) {
                case "R-CRASH":
                    return "The generator throws: " + CrashCause(v.Key) + "; no system of the assembly gets its Init, only the CS8785 warning is left";
                case "R-FIELDS-NULL" when v.Key.StartsWith("NULL|no-dispatch"):
                    return $"The generated Init of {name} is never called (SystemsRunner calls another IInitSystem.Init): fields null without a diagnostic; layout: {layout}";
                case "R-FIELDS-NULL" when v.Question:
                    return $"Base fields stay null without a diagnostic, which the documents do not forbid: {layout}";
                case "R-FIELDS-NULL":
                    return $"The base Init is not called, the base fields silently stay null: {layout}";
                case "R-BASE-INIT-SKIPPED":
                    return $"A hand-written base Init is skipped without a diagnostic (the documents promise the chain): {layout}";
                case "R-FIELD-DROPPED":
                    return $"The generated Init of {name} ran but left fields null ({key.ElementAtOrDefault(1)})";
                case "R-WRONG-INJECTION":
                    return $"The generated Init of {name} fills a {key.ElementAtOrDefault(1)} field with the wrong object ({WrongCause(key.ElementAtOrDefault(2))})";
                case "R-CHAIN-BROKEN":
                    return $"base.Init in the hand-written Init of {name} does not reach the generated Init of the base";
                case "R-FALSE-KECS":
                    return $"A false {key.ElementAtOrDefault(1)} on a layout the documents support: {layout}";
                case "R-OTHER-KECS" when key.ElementAtOrDefault(2) == "base-error-missing":
                    return $"The generator reports {key.ElementAtOrDefault(1)} on the derived class, and the documented error of its base is missing: {layout}";
                case "R-OTHER-KECS":
                    return $"A misleading diagnostic: the generator reports {key.ElementAtOrDefault(1)} instead of the documented {key.ElementAtOrDefault(2)}: {layout}";
                case "R-MISSING-KECS":
                    return $"The documented {key.ElementAtOrDefault(1)} is missing and the build passes: {layout}";
                case "R-CASCADE":
                    return $"A second error {key.ElementAtOrDefault(1)} on the derived class of a base that already got its own";
                case "R-GEN-ERROR":
                    return $"The generated code does not compile: {key.ElementAtOrDefault(1)} ({GenErrorCause(v.Key)})" + (v.Key.StartsWith("GENERR-USER") ? " — an error in the program's own code that does not exist without the generator" : "");
                case "R-GEN-WARNING":
                    return $"Warning {key.ElementAtOrDefault(1)} in the generated code ({WarningCause(v.Key)})";
                case "R-GEN-MISSING":
                    return v.Key == "MISSING|alias"
                        ? "Attributes spelled through a using alias ([InjPool] = KenseiECS.PoolAttribute) are not seen by the generator: no Init is written"
                        : $"After generation the program's own code still has {key.ElementAtOrDefault(1)}: the generator did not write a member it needs; layout: {layout}";
                case "R-ONINIT-TWICE":
                    return $"OnInit of {name} runs twice in one SystemsRunner.Init";
                case "R-ONINIT-MISSING":
                    return $"OnInit of {name} is not called although the fields are filled";
                case "R-BASE-INIT-TWICE":
                    return $"Init/hook of {name} runs twice in one SystemsRunner.Init";
                case "R-ONINIT-BEFORE-FIELDS":
                    return $"OnInit of {name} runs before its own fields are filled";
                case "R-ORDER" when v.Key.StartsWith("ORDER-FIELDS-FIRST|"):
                    return $"Wrong order (2.2.2): the base runs before the fields of {name} are filled and sees them null; layout: {layout}";
                case "R-ORDER" when v.Key.StartsWith("ORDER-CHAIN|"):
                    return $"Wrong order (2.2.0/2.2.1): the base runs after the fields of {name} are filled, not before them; layout: {layout}";
                case "R-ORDER":
                    return $"Wrong order: OnInit/fields of {name} before the base";
                case "R-HOOK-ONCE":
                    return v.Key.StartsWith("HOOK-BEFORE-FIELDS")
                        ? $"The override of a base OnInit hook in {name} runs before the fields of {name} are filled"
                        : $"The override of a base OnInit hook in {name} does not run exactly once ({key.ElementAtOrDefault(1)})";
                case "R-KECS-TEXT":
                    return $"{key.ElementAtOrDefault(1)} on {name} does not say what it is about ({key.ElementAtOrDefault(2)}); layout: {layout}";
                case "R-MISSING-WARNING":
                    return $"No KECS013 on {name}, a hand-written override of a generated Init without base.Init ({key.ElementAtOrDefault(2)}): the base fields stay null without a word";
                case "R-RUNTIME-EXCEPTION":
                    return $"SystemsRunner.Init throws: {v.Message}";
                case "R-FOREIGN-DIAG":
                    return $"The generator reports a non-KECS diagnostic {key.ElementAtOrDefault(1)}";
                case "R-MIGRATION-ORDER" when v.Key.EndsWith("|base-no-longer-runs"):
                    return $"After the documented upgrade from 2.1 (base.Init deleted from OnInit of {name}) a base Init/OnInit no longer runs (compared with {v.Against})";
                case "R-MIGRATION-ORDER":
                    return $"After the documented upgrade from 2.1 (base.Init deleted from OnInit of {name}) the base runs before the derived fields are filled and sees them null; in the 2.1 layout on {v.Against} they were set";
                case "R-LOST-SIGNAL" when v.Documented:
                    return $"Documented lost signal: {v.Against} rejected the program at build time, this tree builds it without KECS013, which does not see a generated base in another assembly with only private or internal injected fields ({LostCause(key.ElementAtOrDefault(1))})";
                case "R-LOST-SIGNAL":
                    return $"Lost compiler signal: {v.Against} rejected the program at build time, this tree builds it and injected fields stay null without a diagnostic ({LostCause(key.ElementAtOrDefault(1))})";
                case "R-REGRESSION-NULL":
                    return $"Regression against {v.Against}: injected fields of {name} stay null here, there SystemsRunner.Init filled them (no document forbids it)";
                default:
                    return v.Message;
            }
        }

        private static string LostCause(string cause) => cause switch {
            "drop-PublicOverrideNoBase" => "a hand-written override without base.Init drops the generated base Init",
            "drop-PublicOverride" => "the base.Init of a hand-written override does not reach the generated base Init",
            "skip" => "a generated Init below does not reach the base",
            "unexplained" => "nothing in the chain visibly stops it",
            string s when s != null && s.StartsWith("drop-") => $"a hand-written {s.Substring(5)} Init does not reach the generated base Init",
            _ => cause ?? "",
        };

        private static string WrongCause(string mechanism) => mechanism switch {
            "exc-dropped" => "the [Exc] constraints are missing from the filter",
            "inc-dropped" => "[Inc] constraints are missing from the filter",
            "any-dropped" => "[Any] constraints are missing from the filter",
            "any-as-inc" => "the [Any] types became required [Inc] types",
            "constraints-differ" => "the filter has other constraints than the attributes",
            "key-ignored" => "the [Shared(\"key\")] key is ignored, the object registered without a key is injected",
            "other-object" => "not the object registered for the field's type and key",
            "other-instance" => "an object that is not the one the documented call returns",
            "other-type" => "an object of another type",
            _ => mechanism ?? "",
        };

        private static string CrashCause(string key) {
            if (key.EndsWith("|unique-other")) {
                return "the hint names of two systems are equal";
            }
            if (key.EndsWith("|case-collision")) {
                return "the hint names of two systems differ only in case (Game.AI / Game.Ai), and the driver compares them ignoring case";
            }
            if (key.EndsWith("|exact-collision")) {
                return "two systems get the same hint name once <, >, commas and spaces become '_' (Sys<T> and Sys_T_)";
            }
            if (key.EndsWith("|invalid-hint-char")) {
                return "the '@' of a keyword identifier goes into the hint name (@class, Game.@internal)";
            }
            return "an exception in the generator";
        }

        private static string GenErrorCause(string key) {
            if (key.Contains("|keyword")) {
                return "a keyword identifier is written without '@'";
            }
            if (key.Contains("|CS0505|")) {
                return "a class in between has a field or property named Init, and the generator writes an override";
            }
            if (key.Contains("|CS0111|") && key.Contains("|hook")) {
                return "the partial OnInit duplicates the override of the base's OnInit hook";
            }
            if (key.Contains("|CS1010|") || key.Contains("|CS1039|")) {
                return "a line break in a [Shared] key is not escaped";
            }
            if (key.Contains("|OnInit|hook")) {
                return "a clash with the hand-written OnInit hook of the base";
            }
            if (key.StartsWith("GENERR-USER|CS0506") && key.EndsWith("|overload-taken-as-init")) {
                return "a hand-written override of Init does not make the generated base Init virtual: the generator took an Init(in World, SharedData) / Init<T>(World, SharedData) overload in between for Init";
            }
            if (key.StartsWith("GENERR-USER|CS0506")) {
                return "the generated base Init did not become virtual for a hand-written override";
            }
            return key.Split('|').ElementAtOrDefault(2) ?? "";
        }

        private static string WarningCause(string key) {
            if (key.Contains("|OnInit|hook")) {
                return "the partial OnInit hides the hand-written OnInit(World, SharedData) hook of the base";
            }
            if (key.Contains("|OnInit|nested-in-base")) {
                return "the derived class is nested in its base: its partial OnInit hides the private OnInit of the base it can see";
            }
            if (key == "GENWARN|CS0114|Init") {
                return "an Init without override/new hides the public virtual Init of the base: the generator took the explicit implementation declared first";
            }
            if (key == "GENWARN|CS0108|Init") {
                return "an Init without new hides a base member named Init that the generator does not take for an Init method (a field, property or static method)";
            }
            if (key == "GENWARN|CS0109|Init") {
                return "a needless new: the generator took an Init<T>/Init(in ...) overload of the base for an Init it hides";
            }
            return key.Split('|').ElementAtOrDefault(2) ?? "";
        }
    }
}
