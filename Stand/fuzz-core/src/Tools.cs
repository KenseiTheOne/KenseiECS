// Command-line tools of the worker binary: --shrink (delta debugging of a failing scenario) and --repro
// (NUnit repro of a shrunk scenario). The exploration driver is ../driver.py.
using System;
using System.IO;

namespace KenseiStand.FuzzCore {
    // --shrink case.json [--mode compare|crash] [--out min.json] [--budget-ms N]
    // Runs the scenario, shrinks its failure in-process and writes the minimal scenario; prints the
    // result line of the minimal scenario (same format as a worker).
    public static class ShrinkTool {
        public static int Run(Args a) {
            var scn = Scenario.Load(a.Get("--shrink", null));
            string mode = a.Get("--mode", scn.Uncapped ? "crash" : "compare");
            if (scn.Layout != TypeMap.Layout) {
                Console.Error.WriteLine("layout mismatch: scenario " + scn.Layout + ", process " + TypeMap.Layout);
                return 4;
            }
            var opt = new RunOptions { CrashMode = mode == "crash", Guard = true };
            var first = Executor.Run(scn, opt);
            if (first.Status != "fail") {
                Console.Out.WriteLine(Worker.Line(scn.Seed, mode, scn.Layout, scn, first));
                return 0;
            }
            int budgetMs = int.Parse(a.Get("--budget-ms", "20000"));
            var outcome = Shrinker.Shrink(scn, first, opt, TimeSpan.FromMilliseconds(budgetMs));
            string outPath = a.Get("--out", null);
            if (outPath != null) {
                File.WriteAllText(outPath, outcome.Min.ToJson(true));
            }
            Console.Error.WriteLine("shrink: " + scn.Ops.Count + " -> " + outcome.Min.Ops.Count + " ops, " + outcome.Runs + " runs" + (outcome.OutOfTime ? " (budget exhausted)" : ""));
            Console.Out.WriteLine(Worker.Line(scn.Seed, mode, scn.Layout, outcome.Min, outcome.Result));
            return 0;
        }
    }
}

namespace KenseiStand.FuzzCore {
    // --repro min.json --out repro.cs --id ID --family F --title T --flavor F --cfg rel|dbg --case case.json
    public static class ReproTool {
        public static int Run(Args a) {
            var scn = Scenario.Load(a.Get("--repro", null));
            if (scn.Layout != TypeMap.Layout) {
                Console.Error.WriteLine("layout mismatch: scenario " + scn.Layout + ", process " + TypeMap.Layout);
                return 4;
            }
            var traces = new System.Collections.Generic.List<OpTrace>();
            var res = Executor.Run(scn, new RunOptions { CrashMode = scn.Uncapped, Guard = true, KeepLogs = true, Trace = traces });
            if (res.Status != "fail") {
                Console.Error.WriteLine("the scenario does not fail (" + res.Status + "), no repro written");
                return 5;
            }
            string family = a.Get("--family", res.Family ?? Classifier.NewFamily);
            string fc = Classifier.FamilyContract(family);
            var meta = new ReproWriter.Meta {
                Id = a.Get("--id", "fz"),
                Family = family,
                Title = a.Get("--title", Classifier.Title(family)),
                Flavor = a.Get("--flavor", "?"),
                Config = a.Get("--cfg", Build.Config),
                CaseFile = a.Get("--case", "?"),
                Contract = (fc.Length > 0 ? fc + " Rule " + res.Rule + ": " : "") + Classifier.Contract(res.Rule),
            };
            File.WriteAllText(a.Get("--out", "repro.cs"), ReproWriter.Write(scn, res, meta, traces));
            Console.Out.WriteLine(Worker.Line(scn.Seed, scn.Uncapped ? "crash" : "compare", scn.Layout, scn, res));
            return 0;
        }
    }
}
