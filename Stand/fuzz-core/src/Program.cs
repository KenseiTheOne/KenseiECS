using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace KenseiStand.FuzzCore {
    public static class Program {
        public static int Main(string[] args) {
            // A dedicated thread with a known stack: deep but finite, so a runaway recursion in the
            // world under test still ends in a stack overflow (crash mode looks for that).
            int code = 0;
            var t = new Thread(() => code = MainCore(args), 64 * 1024 * 1024);
            t.Start();
            t.Join();
            return code;
        }

        private static int MainCore(string[] args) {
            var a = new Args(args);
            string layout = a.Get("--layout", "narrow");
            TypeMap.Init(layout);
            // The contract profile: the package version of the tree under test (package.json), passed by the driver.
            Contract.SetVersion(a.Get("--pkg-version", null), a.Has("--unreleased"), a.Get("--last-release", null));
            try {
                if (a.Has("--worker")) {
                    return Worker.Run(a);
                }
                if (a.Has("--replay")) {
                    return Worker.Replay(a);
                }
                if (a.Has("--gen")) {
                    long seed = long.Parse(a.Get("--seed", "1"));
                    var s = Generator.Make(seed, int.Parse(a.Get("--ops", "60")), Build.Debug, a.Has("--crash"), layout, a.Get("--motif", null),
                        Exclusions.Parse(a.Get("--exclude", null)));
                    Console.WriteLine(s.ToJson(true));
                    return 0;
                }
                if (a.Has("--shrink")) {
                    return ShrinkTool.Run(a);
                }
                if (a.Has("--repro")) {
                    return ReproTool.Run(a);
                }
                Console.Error.WriteLine("usage: FuzzCore --worker --seed N --count C --ops M [--mode compare|crash] [--no-guard] [--layout L] [--exclude W1,W2]\n" +
                                        "       FuzzCore --replay case.json [--mode compare|crash] [--no-guard] [--capped] [--verbose] [--layout L] [--reading Q-id]\n" +
                                        "       FuzzCore --gen --seed N [--ops M] [--crash] [--layout L] [--exclude W1,W2]\n" +
                                        "       FuzzCore --shrink case.json [--mode M] [--out min.json] [--budget-ms N] [--layout L]\n" +
                                        "       FuzzCore --repro min.json --out repro.cs [--id ID --family F --title T --flavor F --cfg C --case P] [--layout L]\n" +
                                        "every command: [--pkg-version X.Y.Z [--unreleased [--last-release X.Y.Z]]] (contract profile of the tree under test)\n" +
                                        "(the driver is ../driver.py, started by ../run.sh)");
                return 2;
            } catch (Exception ex) {
                Console.Error.WriteLine("fatal: " + ex);
                return 3;
            }
        }
    }

    public sealed class Args {
        private readonly string[] _a;
        public Args(string[] a) { _a = a; }
        public bool Has(string k) => Array.IndexOf(_a, k) >= 0;

        public string Get(string k, string def) {
            int i = Array.IndexOf(_a, k);
            return i >= 0 && i + 1 < _a.Length ? _a[i + 1] : def;
        }

        public List<string> All(string k) {
            var r = new List<string>();
            for (int i = 0; i < _a.Length - 1; i++) {
                if (_a[i] == k) r.Add(_a[i + 1]);
            }
            return r;
        }
    }
}
