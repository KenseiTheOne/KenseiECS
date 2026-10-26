using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KenseiStand.Generator {
    /// <summary>
    /// Repro artifacts of a finding: the clean C# of the minimized program with a header (finding,
    /// seed and parameters, expectation, observation, the expected ReproMain.Run() output), the model
    /// JSON (replayable on another tree), the generated code and the observation. The clean form is
    /// compiled again and must give the same diagnostics and the same ReproMain.Run() lines that the
    /// instrumented run predicts; otherwise the repro is marked as differing.
    /// </summary>
    public static class Repro {
        public static void Write(Finding f, ProgramModel p, Observation obs, List<Violation> violations, Settings settings, Options o, string reproDir) {
            string name = f.Id;
            Observation clean;
            try {
                clean = Pipeline.ExecuteClean(p, settings);
            } catch (Exception e) {
                clean = new Observation { RuntimeError = "clean form failed: " + e.GetType().Name + ": " + e.Message };
            }
            var expectedLines = obs.Runs.Select(r => Renderer.ExpectedReproLine(p, r)).ToList();
            var (status, details) = Verify(obs, clean, expectedLines);
            f.ReproStatus = status;

            var sb = new StringBuilder();
            sb.AppendLine($"// Repro {f.Id}{(f.Known != null ? " (" + f.Known + ")" : "")}, Stand/generator, tree {o.Flavor}-{o.Config} (package {Driver.PackageVersion(o.Src)}, contract profile {settings.Profile})");
            sb.AppendLine("// " + f.Title);
            sb.AppendLine($"// Status: {f.Status}; class: {f.Class}; rule: {f.Rule}; severity: {f.Severity} (estimate); key: {f.Key}{(f.Claim != null ? "; claim: " + f.Claim : "")}");
            sb.AppendLine($"// Found by: {f.Exemplar} ({f.CatalogHits} catalog layouts, {f.FuzzHits} random programs); seed: {(f.Seed?.ToString() ?? "-")}");
            sb.AppendLine("// Params: " + f.Params);
            sb.AppendLine($"// Minimized on the model: {f.MinimizeEvaluations} pipeline runs.");
            sb.AppendLine("// Expected (contract): " + f.Expected + " — " + f.Doc);
            sb.AppendLine("// Observed: " + f.Message);
            if (f.Downgrade != null) {
                sb.AppendLine("// Why a question: " + f.Downgrade);
            }
            if (f.Evidence != null) {
                sb.AppendLine($"// On {f.Against}: {f.Evidence}");
            }
            if (f.Rule == "R-MIGRATION-ORDER") {
                sb.AppendLine($"// Migration pair: the program below is the documented 2.2 upgrade; with `base.Init(world, shared);` back at the start of OnInit");
                sb.AppendLine($"// of the derived class it is the 2.1 layout that ran on {f.Against}.");
            }
            sb.AppendLine("// How to run: compile each '===== assembly' part below as its own assembly with the KenseiECS source generator");
            sb.AppendLine("// (Lib first, then Main referencing Lib), both referencing KenseiECS; then call ReproMain.Run().");
            bool built = obs.Units.Any() && obs.Units.All(u => u.Image != null);
            if (expectedLines.Count > 0 && built) {
                sb.AppendLine("// ReproMain.Run() on this tree prints:");
                foreach (var line in expectedLines) {
                    sb.AppendLine("//   " + line);
                }
            } else if (expectedLines.Count > 0) {
                sb.AppendLine("// Main does not build (see the generated code); the systems of Lib, run on their own, show:");
                foreach (var line in expectedLines) {
                    sb.AppendLine("//   " + line);
                }
            } else {
                sb.AppendLine("// The build itself shows the problem (see the generator diagnostics and the generated code next to this file).");
            }
            sb.AppendLine("// Repro check: " + status);
            sb.AppendLine();
            File.WriteAllText(Path.Combine(reproDir, name + ".cs"), sb + Renderer.RenderClean(p));
            File.WriteAllText(Path.Combine(reproDir, name + ".model.json"), Json.Write(p));

            var gen = new StringBuilder();
            foreach (var u in clean.Units) {
                if (u.Generated.Count == 0) {
                    gen.AppendLine($"// {u.Asm}: the generator added no source" + (u.GeneratorException != null ? $" (it threw {u.GeneratorException})" : ""));
                }
                foreach (var g in u.Generated) {
                    gen.AppendLine($"// ===== {u.Asm}: {g.Path}");
                    gen.AppendLine(g.Text);
                }
            }
            File.WriteAllText(Path.Combine(reproDir, name + ".generated.cs"), gen.ToString());

            var text = new StringBuilder();
            text.AppendLine($"{f.Id}: {f.Title}");
            text.AppendLine();
            text.AppendLine("== Instrumented run (what the oracle judged)");
            Describe(text, obs);
            foreach (var r in obs.Runs) {
                text.AppendLine("run  " + Renderer.ExpectedReproLine(p, r));
            }
            text.AppendLine();
            text.AppendLine("== Violations of the minimized program");
            foreach (var v in violations) {
                text.AppendLine($"{v}{(v.Known != null ? " (" + v.Known + ")" : "")}");
            }
            text.AppendLine();
            text.AppendLine("== Clean repro form");
            Describe(text, clean);
            text.AppendLine("ReproMain.Run():");
            text.AppendLine(clean.ReproOutput ?? "(not run: " + (clean.RuntimeError ?? "the build fails") + ")");
            text.AppendLine();
            text.AppendLine("== Repro check: " + status);
            foreach (var d in details) {
                text.AppendLine("  " + d);
            }
            File.WriteAllText(Path.Combine(reproDir, name + ".observed.txt"), text.ToString());

            f.Repro = "repro/" + name + ".cs";
            f.ReproModel = "repro/" + name + ".model.json";
            f.ReproGenerated = "repro/" + name + ".generated.cs";
            f.ReproObserved = "repro/" + name + ".observed.txt";
        }

        private static void Describe(StringBuilder text, Observation obs) {
            foreach (var u in obs.Units) {
                foreach (var d in u.InvalidErrors) {
                    text.AppendLine($"{u.Asm} invalid program: {d}");
                }
                if (u.GeneratorException != null) {
                    text.AppendLine($"{u.Asm} generator exception: {u.GeneratorException}");
                }
                foreach (var d in u.GeneratorDiagnostics) {
                    text.AppendLine($"{u.Asm} generator: {d.Id} {d.Severity} [{d.ClassName}] {d.Message}");
                }
                foreach (var d in u.Compile) {
                    text.AppendLine($"{u.Asm} compiler: {d.Id} {d.Severity} in {(d.InGenerated ? "generated" : "program")} {d.File}:{d.Line} [{d.ClassName}] {d.Message}");
                }
                text.AppendLine($"{u.Asm} build: {(u.Image != null ? "ok" : "fails")}");
            }
            if (obs.RuntimeError != null) {
                text.AppendLine("runtime: " + obs.RuntimeError);
            }
        }

        /// <summary> Diagnostics that make up the outcome of a build, independent of the form (instrumented or clean). </summary>
        public static List<string> StaticSignature(Observation obs) {
            var list = new List<string>();
            foreach (var u in obs.Units) {
                if (u.Invalid) {
                    list.Add($"{u.Asm}: invalid");
                }
                if (u.GeneratorException != null) {
                    list.Add($"{u.Asm}: generator threw {u.GeneratorException.Split(':')[0]}");
                }
                foreach (var d in u.GeneratorDiagnostics) {
                    list.Add($"{u.Asm}: {d.Id} on {d.ClassName}");
                }
                foreach (var d in u.Compile) {
                    list.Add($"{u.Asm}: {d.Severity} {d.Id} in {(d.InGenerated ? "generated" : "program")} code of {d.ClassName}");
                }
                list.Add($"{u.Asm}: build {(u.Image != null ? "ok" : "fails")}");
            }
            return list.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static (string Status, List<string> Details) Verify(Observation obs, Observation clean, List<string> expectedLines) {
            var details = new List<string>();
            var a = StaticSignature(obs);
            var b = StaticSignature(clean);
            bool bothFail = obs.Units.Any(u => u.Image == null) && clean.Units.Any(u => u.Image == null);
            if (bothFail) {
                // Broken generated code can break the repro harness too (a garbage constructor hides the
                // default one ReproMain uses); that is not a difference of the program.
                b = b.Where(x => !x.EndsWith(" of ReproMain") && !x.EndsWith(" of ReproLog")).ToList();
            }
            foreach (var x in a.Except(b)) {
                details.Add("only in the instrumented run: " + x);
            }
            foreach (var x in b.Except(a)) {
                details.Add("only in the clean form: " + x);
            }
            bool built = clean.Units.Any() && clean.Units.All(u => u.Image != null);
            if (built) {
                if (clean.ReproOutput == null) {
                    details.Add("ReproMain.Run() did not run: " + clean.RuntimeError);
                } else {
                    var actual = clean.ReproOutput.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                    for (int i = 0; i < Math.Max(actual.Count, expectedLines.Count); i++) {
                        string e = i < expectedLines.Count ? expectedLines[i] : "(none)";
                        string g = i < actual.Count ? actual[i] : "(none)";
                        if (e != g) {
                            details.Add($"ReproMain line {i + 1}: expected '{e}', got '{g}'");
                        }
                    }
                }
            }
            return details.Count == 0
                ? ("verified (the clean form gives the same diagnostics" + (built ? " and the same ReproMain.Run() output" : "") + ")", details)
                : ("differs: " + details[0] + (details.Count > 1 ? $" (+{details.Count - 1})" : ""), details);
        }
    }
}
