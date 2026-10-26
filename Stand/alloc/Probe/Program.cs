using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace KenseiStand.Alloc.Probe {
    /// <summary> Sizes of one run: --mode quick or full. </summary>
    public sealed class Settings {
        public string Mode = "quick";
        public int SteadyWindows = 5;
        public int PerWindow = 2000;
        public int Warmup = 4000;
        public double TimeWarmMs = 150;
        public double TimeWindowMs = 5;
        public int TimeWindows = 7;
        public bool Timing = true;

        public static Settings For(string mode) {
            var s = new Settings { Mode = mode };
            if (mode == "full") {
                s.SteadyWindows = 9;
                s.PerWindow = 10000;
                s.Warmup = 20000;
                s.TimeWarmMs = 600;
                s.TimeWindowMs = 10;
                s.TimeWindows = 15;
            }
            return s;
        }
    }

    /// <summary> What one scenario measured. Serialized as one RESULT line of JSON. </summary>
    public sealed class Result {
        public string Id;
        public bool Available = true;
        public string Reason;
        public long? FirstBytes;
        public long[] SteadyWindows;
        public int PerWindow;
        public int Warmup;
        public double[] Ns;
        public int NsBatch;
        // Long run in geometric spans (AllocMeter.Spans): bytes and runs per span.
        public long[] SpanBytes;
        public int[] SpanRuns;
        public int SpanWarmup;
        // "thread" (GC.GetAllocatedBytesForCurrentThread) or "all_threads" (GC.GetTotalAllocatedBytes(true)).
        public string Counter = "thread";
        public readonly List<KeyValuePair<string, object>> Extra = new();
        public string Error;

        public void Set(string key, object value) {
            Extra.Add(new KeyValuePair<string, object>(key, value));
        }
    }

    public abstract class Scenario {
        public abstract string Id { get; }
        public abstract void Run(Settings s, Result r);

        /// <summary> Steady state of <paramref name="op"/>: warm-up, then the configured windows. </summary>
        protected static void Steady<T>(Settings s, Result r, ref T op, int perWindowDivisor = 1) where T : struct, IAllocOp {
            int perWindow = Math.Max(1, s.PerWindow / perWindowDivisor);
            int warm = Math.Max(1, s.Warmup / perWindowDivisor);
            var windows = new long[s.SteadyWindows];
            AllocMeter.Steady(ref op, warm, perWindow, windows);
            r.SteadyWindows = windows;
            r.PerWindow = perWindow;
            r.Warmup = warm;
        }

        /// <summary> Steady state counted over every thread (work handed to other threads). </summary>
        protected static void SteadyAllThreads<T>(Settings s, Result r, ref T op, int perWindowDivisor = 1) where T : struct, IAllocOp {
            int perWindow = Math.Max(1, s.PerWindow / perWindowDivisor);
            int warm = Math.Max(1, s.Warmup / perWindowDivisor);
            var windows = new long[s.SteadyWindows];
            AllocMeter.SteadyAllThreads(ref op, warm, perWindow, windows);
            r.SteadyWindows = windows;
            r.PerWindow = perWindow;
            r.Warmup = warm;
            r.Counter = "all_threads";
        }

        /// <summary>
        /// Long run in three geometric spans after the warm-up (see AllocMeter.Spans); <paramref name="done"/>
        /// runs happened before. Full mode warms up four times longer, so every span is four times longer.
        /// </summary>
        protected static void Spans<T>(Settings s, Result r, ref T op, int done, int warmup) where T : struct, IAllocOp {
            if (s.Mode == "full") {
                warmup *= 4;
            }
            var bytes = new long[3];
            var runs = new int[3];
            AllocMeter.Spans(ref op, done, warmup, bytes, runs);
            r.SpanBytes = bytes;
            r.SpanRuns = runs;
            r.SpanWarmup = done + warmup;
        }

        protected static void Time<T>(Settings s, Result r, ref T op) where T : struct, IAllocOp {
            if (!s.Timing) {
                return;
            }
            var ns = new double[s.TimeWindows];
            r.NsBatch = AllocMeter.Time(ref op, s.TimeWarmMs, s.TimeWindowMs, ns);
            r.Ns = ns;
        }
    }

    public static class Program {
#if KENSEI_DEBUG
        public const bool KenseiDebug = true;
#else
        public const bool KenseiDebug = false;
#endif

        public static int Main(string[] args) {
            string mode = "quick";
            string run = null;
            bool list = false;
            bool timing = true;
            bool pretouchNames = false, listStatic = false;
            string cctorOf = null;
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--mode": mode = args[++i]; break;
                    case "--run": run = args[++i]; break;
                    case "--list": list = true; break;
                    case "--no-timing": timing = false; break;
                    // Attribution knobs: one-time work done before any scenario, outside every window.
                    case "--pretouch-type-names": pretouchNames = true; break;
                    // Comma list of full type names (from --list-static-types of another process): looked up by
                    // name, so no other type of the assembly is touched by reflection in this process.
                    case "--run-cctors-of": cctorOf = args[++i]; break;
                    case "--list-static-types": listStatic = true; break;
                    case "--info": break;
                    default:
                        Console.Error.WriteLine("unknown argument " + args[i]);
                        return 2;
                }
            }

            if (listStatic) {
                foreach (var name in Attribution.StaticTypes()) {
                    Console.WriteLine(name);
                }
                return 0;
            }

            var all = Registry.All();
            if (list) {
                foreach (var sc in all) {
                    Console.WriteLine(sc.Id);
                }
                return 0;
            }

            // Self-test first: a meter that is not exact on this runtime produces no findings.
            var self = AllocMeter.SelfTest(1000);
            long artifact = KnownArtifacts.BoxedExpectedAfterWindow();
            artifact = Math.Max(artifact, KnownArtifacts.BoxedExpectedAfterWindow());
            // The runtime's static-field storage grows in pinned buckets allocated by whichever window needs the next
            // slot; a fresh bucket now keeps them out of every window (StaticsPrimer).
            var primed = StaticsPrimer.Prime();
            var info = new Json()
                .Str("runtime", RuntimeInformation.FrameworkDescription)
                .Str("runtime_version", Environment.Version.ToString())
                .Str("arch", RuntimeInformation.ProcessArchitecture.ToString())
                .Bool("server_gc", GCSettings.IsServerGC)
                .Bool("kensei_debug", KenseiDebug)
                .Str("mode", mode)
                .Str("tiered_compilation", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default")
                .StrArray("features", Compat.Features())
                .Bool("selftest_ok", self.Ok)
                .Num("selftest_empty_window", self.EmptyBytes)
                .Num("selftest_object_bytes", self.ObjectBytes / self.Count)
                .Num("selftest_byte100_bytes", self.ByteArray100Bytes / self.Count)
                .Num("artifact_boxed_expected_after_window", artifact)
                .Num("statics_primer_classes", primed.Classes)
                .Num("statics_primer_bucket_bytes", primed.Bucket)
                .Bool("pretouch_type_names", pretouchNames)
                .Str("run_cctors_of", cctorOf)
                .ToString();
            Console.WriteLine("INFO " + info);

            if (pretouchNames) {
                Attribution.PretouchTypeNames();
            }
            if (cctorOf != null) {
                foreach (var name in cctorOf.Split(',')) {
                    if (name.Length > 0 && !Attribution.RunClassConstructorOf(name)) {
                        Console.Error.WriteLine("unknown type " + name);
                        return 2;
                    }
                }
            }

            var settings = Settings.For(mode);
            settings.Timing = timing;
            IEnumerable<Scenario> selected = all;
            if (run != null && run != "all") {
                var ids = new HashSet<string>(run.Split(','));
                selected = all.Where(sc => ids.Contains(sc.Id)).ToList();
                foreach (var id in ids) {
                    if (all.All(sc => sc.Id != id)) {
                        Console.Error.WriteLine("unknown scenario " + id);
                        return 2;
                    }
                }
            }

            int failed = 0;
            foreach (var sc in selected) {
                var r = new Result { Id = sc.Id };
                try {
                    sc.Run(settings, r);
                } catch (Exception ex) {
                    r.Error = ex.GetType().Name + ": " + ex.Message;
                    failed++;
                }
                Console.WriteLine("RESULT " + Serialize(r));
                Console.Out.Flush();
            }
            return failed == 0 ? 0 : 1;
        }

        private static string Serialize(Result r) {
            var j = new Json().Str("id", r.Id).Bool("available", r.Available);
            if (r.Reason != null) j.Str("reason", r.Reason);
            if (r.Error != null) j.Str("error", r.Error);
            if (r.FirstBytes.HasValue) j.Num("first_bytes", r.FirstBytes.Value);
            if (r.SteadyWindows != null) {
                j.NumArray("steady_windows", r.SteadyWindows.Select(x => (double)x));
                j.Num("per_window", r.PerWindow);
                j.Num("warmup", r.Warmup);
            }
            if (r.Ns != null) {
                j.NumArray("ns", r.Ns);
                j.Num("ns_batch", r.NsBatch);
            }
            if (r.SpanBytes != null) {
                j.NumArray("span_bytes", r.SpanBytes.Select(x => (double)x));
                j.NumArray("span_runs", r.SpanRuns.Select(x => (double)x));
                j.Num("span_warmup", r.SpanWarmup);
            }
            j.Str("counter", r.Counter);
            var extra = new Json();
            foreach (var kv in r.Extra) {
                extra.Any(kv.Key, kv.Value);
            }
            j.Raw("extra", extra.ToString());
            return j.ToString();
        }
    }

    /// <summary> Minimal JSON object writer (invariant culture). Used only outside windows. </summary>
    public sealed class Json {
        private readonly StringBuilder _sb = new StringBuilder("{");
        private bool _first = true;

        private Json Key(string key) {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append('"').Append(Escape(key)).Append("\":");
            return this;
        }

        public Json Str(string key, string value) {
            Key(key);
            if (value == null) _sb.Append("null");
            else _sb.Append('"').Append(Escape(value)).Append('"');
            return this;
        }

        public Json Bool(string key, bool value) {
            Key(key).Append(value ? "true" : "false");
            return this;
        }

        public Json Num(string key, double value) {
            Key(key).Append(Format(value));
            return this;
        }

        public Json NumArray(string key, IEnumerable<double> values) {
            Key(key).Append('[').Append(string.Join(",", values.Select(Format))).Append(']');
            return this;
        }

        public Json StrArray(string key, IEnumerable<string> values) {
            Key(key).Append('[').Append(string.Join(",", values.Select(v => "\"" + Escape(v) + "\""))).Append(']');
            return this;
        }

        public Json Raw(string key, string json) {
            Key(key).Append(json);
            return this;
        }

        public Json Any(string key, object value) {
            switch (value) {
                case null: return Str(key, null);
                case string s: return Str(key, s);
                case bool b: return Bool(key, b);
                case int i: return Num(key, i);
                case long l: return Num(key, l);
                case double d: return Num(key, d);
                case float f: return Num(key, f);
                default: return Str(key, value.ToString());
            }
        }

        private Json Append(string s) {
            _sb.Append(s);
            return this;
        }

        private Json Append(char c) {
            _sb.Append(c);
            return this;
        }

        public override string ToString() => _sb.ToString() + "}";

        private static string Format(double v) {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 9e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Escape(string s) {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
