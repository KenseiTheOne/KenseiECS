using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KenseiStand.FuzzCore {
    // Worker process: generates scenarios from seeds, runs them and prints one JSON line per seed.
    // A "begin" line is flushed before each run, so the driver knows which seed killed the process.
    public static class Worker {
        private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

        public static int Run(Args a) {
            long seed = long.Parse(a.Get("--seed", "1"));
            int count = int.Parse(a.Get("--count", "1"));
            int ops = int.Parse(a.Get("--ops", "60"));
            string mode = a.Get("--mode", "compare");
            string motif = a.Get("--motif", null);
            var exclude = Exclusions.Parse(a.Get("--exclude", null));
            string layout = TypeMap.Layout;
            bool crash = mode == "crash";
            bool guard = !a.Has("--no-guard");
            var stdout = Console.Out;
            for (long s = seed; s < seed + count; s++) {
                stdout.WriteLine(JsonSerializer.Serialize(new { t = "begin", seed = s, mode, layout }, Json));
                stdout.Flush();
                Scenario scn = null;
                RunResult r;
                try {
                    scn = Generator.Make(s, ops, Build.Debug, crash, layout, motif, exclude);
                    r = Executor.Run(scn, new RunOptions { CrashMode = crash, Guard = guard });
                } catch (Exception ex) {
                    r = new RunResult { Status = "harness-error", Detail = ex.ToString() };
                }
                stdout.WriteLine(Line(s, mode, layout, scn, r));
                stdout.Flush();
            }
            return 0;
        }

        public static string Line(long seed, string mode, string layout, Scenario scn, RunResult r) {
            return JsonSerializer.Serialize(new {
                t = "result",
                seed,
                mode,
                layout,
                status = r.Status,
                sig = r.Sig,
                rule = r.Rule,
                cls = r.Class,
                op = r.OpIndex,
                opText = r.OpText,
                detail = r.Detail,
                origin = scn?.Origin,
                ops = r.OpsRun,
                events = r.Events,
                questions = r.Questions,
                notes = r.Notes,
                family = r.Status == "fail" ? (r.Family ?? Classifier.NewFamily) : null,
                fstatus = r.Status == "fail" ? (r.FindingStatus ?? "defect") : null,
                regression = r.RegressionReason,
                facts = r.Facts,
                mirrors = r.Mirrors,
                readings = r.Readings,
                claims = r.Claims,
                inactive = r.InactiveClaims,
                ctx = r.Ctx,
                debug = Build.Debug,
                scenario = r.Status == "fail" ? scn?.ToJson() : null,
            }, Json);
        }

        // Replays a scenario file: --replay file.json [--mode compare|crash] [--uncapped]
        public static int Replay(Args a) {
            var scn = Scenario.Load(a.Get("--replay", null));
            string mode = a.Get("--mode", "compare");
            if (a.Has("--uncapped")) {
                scn.Uncapped = true;
            }
            if (scn.Layout != TypeMap.Layout) {
                Console.Error.WriteLine("layout mismatch: scenario " + scn.Layout + ", process " + TypeMap.Layout + " (pass --layout " + scn.Layout + ")");
                return 4;
            }
            Console.Out.WriteLine(JsonSerializer.Serialize(new { t = "begin", seed = scn.Seed, mode }, Json));
            Console.Out.Flush();
            if (a.Has("--capped")) {
                scn.Uncapped = false;
            }
            var readings = a.All("--reading");
            var r = Executor.Run(scn, new RunOptions { CrashMode = mode == "crash" && scn.Uncapped, Guard = !a.Has("--no-guard"),
                                                       Readings = readings.Count > 0 ? readings : null });
            Console.Out.WriteLine(Line(scn.Seed, mode, scn.Layout, scn, r));
            if (a.Has("--verbose") && r.Status != "fail" && r.Questions != null && r.Questions.Count > 0) {
                Console.Error.WriteLine(r.Status.ToUpperInvariant() + "; contract questions met:");
                foreach (var q in r.Questions) Console.Error.WriteLine("  " + q);
            }
            if (a.Has("--verbose") && r.Status == "fail") {
                Console.Error.WriteLine("FAIL " + r.Sig + " at op " + r.OpText);
                Console.Error.WriteLine("  family " + r.Family + ", status " + r.FindingStatus + (r.RegressionReason != null ? " (" + r.RegressionReason + ")" : ""));
                Console.Error.WriteLine("  claims: " + string.Join("; ", (r.Claims ?? new List<string>()).Select(c => c + (Contract.Active(c) ? "" : " [not in the documents of " + Contract.DocsVersion + "]"))));
                if (r.Mirrors != null && r.Mirrors.Count > 0) Console.Error.WriteLine("  mirrored questions before it: " + string.Join(", ", r.Mirrors));
                Console.Error.WriteLine(r.Detail);
                if (r.RealLog != null) {
                    Console.Error.WriteLine("--- real log");
                    foreach (var l in r.RealLog) Console.Error.WriteLine("  " + l);
                    Console.Error.WriteLine("--- model log");
                    foreach (var l in r.ModelLog) Console.Error.WriteLine("  " + l);
                }
            }
            return 0;
        }
    }
}
