using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KenseiStand.Generator {
    /// <summary> Command line of the module (Stand/generator/run.sh passes these). </summary>
    public sealed class Options {
        public string Out;
        public string Flavor = "wt";
        public bool Debug;
        public string Src = "";
        public string GenSrc = "";
        public string Mode = "quick";
        public string Profile = "auto";
        public int Seed = 20261023;
        public int Iterations = -1;
        public int Jobs = Math.Max(1, Environment.ProcessorCount);
        public int Budget = -1;
        public List<string> Replay = new();
        public List<string> Compare = new();
        public string Show;
        /// <summary> --observe FILE: only write the digests of every program (for a run on another tree). </summary>
        public string Observe;
        /// <summary> --xobs FILE: digests of another tree for the cross-version rules (repeatable). </summary>
        public List<string> XObs = new();
        /// <summary> --baseline-report DIR: a report of the baseline tree to compare signatures with. </summary>
        public string BaselineReport;
        /// <summary> --root DIR: the repository root; paths in the reports are relative to it. </summary>
        public string Root;

        public string Config => Debug ? "dbg" : "rel";

        public static Options Parse(string[] args) {
            var o = new Options();
            for (int i = 0; i < args.Length; i++) {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + args[i]);
                switch (args[i]) {
                    case "--out": o.Out = Next(); break;
                    case "--flavor": o.Flavor = Next(); break;
                    case "--debug": o.Debug = true; break;
                    case "--src": o.Src = Next(); break;
                    case "--gensrc": o.GenSrc = Next(); break;
                    case "--quick": o.Mode = "quick"; break;
                    case "--full": o.Mode = "full"; break;
                    case "--mode": o.Mode = Next(); break;
                    case "--profile": o.Profile = Next(); break;
                    case "--seed": o.Seed = int.Parse(Next()); break;
                    case "--iterations": o.Iterations = int.Parse(Next()); break;
                    case "--jobs": o.Jobs = Math.Max(1, int.Parse(Next())); break;
                    case "--budget": o.Budget = int.Parse(Next()); break;
                    case "--replay": o.Replay.Add(Next()); break;
                    case "--compare": o.Compare.Add(Next()); break;
                    case "--show": o.Show = Next(); break;
                    case "--observe": o.Observe = Next(); break;
                    case "--xobs": o.XObs.Add(Next()); break;
                    case "--baseline-report": o.BaselineReport = Next(); break;
                    case "--root": o.Root = Next(); break;
                    default: throw new ArgumentException("unknown argument " + args[i]);
                }
            }
            if (o.Iterations < 0) {
                o.Iterations = o.Mode == "full" ? 6000 : 600;
            }
            if (o.Budget < 0) {
                o.Budget = o.Mode == "full" ? 400 : 160;
            }
            return o;
        }
    }

    public static class Program {
        public static int Main(string[] args) {
            Options o;
            try {
                o = Options.Parse(args);
            } catch (ArgumentException e) {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            if (o.Compare.Count > 0) {
                return Driver.Compare(o);
            }
            if (o.Show != null) {
                return Driver.Show(o);
            }
            if (o.Observe != null) {
                return Driver.Observe(o);
            }
            if (o.Out == null) {
                Console.Error.WriteLine("--out is required");
                return 2;
            }
            return Driver.Run(o);
        }
    }
}
