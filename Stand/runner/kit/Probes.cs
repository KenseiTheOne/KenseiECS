// Stand module "runner" — probe kit shared by the stand and by the generated repro snippets.
//
// Only the 2.1.1 API is used here, so the kit compiles against every source tree the stand
// targets. A probe system writes "init:<label>", "run:<label>", "see:<label>:<c0>,<c1>,<c2>,<c3>"
// (pool counts of Ev0..Ev3 at its Run) and "destroy:<label>" into a Journal; failures are injected
// with FailInit / FailRun / ThrowOnDestroy.
using System;
using System.Collections.Generic;
using System.Text;
using KenseiECS;

namespace Stand.Runner.Kit {
    public struct Ev0 : IComponent { }
    public struct Ev1 : IComponent { }
    public struct Ev2 : IComponent { }
    public struct Ev3 : IComponent { }

    public sealed class ProbeInitException : Exception {
        public ProbeInitException(string message) : base(message) { }
    }

    public sealed class ProbeRunException : Exception {
        public ProbeRunException(string message) : base(message) { }
    }

    public sealed class ProbeDestroyException : Exception {
        public ProbeDestroyException(string message) : base(message) { }
    }

    /// <summary> Event log of one case plus the exceptions the probes threw. </summary>
    public sealed class Journal {
        public readonly List<string> Log = new List<string>();
        public readonly List<Exception> DestroyErrors = new List<Exception>();

        /// <summary> ThrowOnDestroy only fires while this is set (the stand arms it around Init/Warmup, i.e. during a rollback). </summary>
        public bool DestroyThrowsArmed;

        private int _mark;
        private int _destroyMark;

        /// <summary> Start of the step under test: Slice() and StepDestroyErrors() return what happens after it. </summary>
        public void Begin() {
            _mark = Log.Count;
            _destroyMark = DestroyErrors.Count;
        }

        public List<string> Slice() {
            return Log.GetRange(_mark, Log.Count - _mark);
        }

        public List<Exception> StepDestroyErrors() {
            return DestroyErrors.GetRange(_destroyMark, DestroyErrors.Count - _destroyMark);
        }

        public static Exception Catch(Action action) {
            try {
                action();
                return null;
            } catch (Exception e) {
                return e;
            }
        }

        /// <summary>
        /// Compare the step's log with a pattern: tokens separated by spaces, "?token" is optional,
        /// "see:x:1,?,0,0" has wildcard counts. Returns null on a match, else a message.
        /// </summary>
        public string ExpectLog(string pattern) {
            var expected = LogPattern.Parse(pattern);
            var actual = Slice();
            var mismatch = LogPattern.Compare(expected, actual);
            return mismatch == null ? null : "log mismatch: expected [" + pattern + "], actual [" + string.Join(" ", actual) + "]";
        }

        /// <summary> The step must rethrow exactly <paramref name="original"/>. </summary>
        public string ExpectRethrown(Exception thrown, Exception original) {
            if (ReferenceEquals(thrown, original)) {
                return null;
            }
            return "expected the original exception (" + Describe(original) + ") to be rethrown, got " + Describe(thrown);
        }

        /// <summary>
        /// The step must throw an AggregateException whose first inner exception is <paramref name="original"/>,
        /// followed by exactly the Destroy errors thrown during the step (flat, no nested AggregateException).
        /// </summary>
        public string ExpectAggregate(Exception thrown, Exception original) {
            var rollback = StepDestroyErrors();
            var aggregate = thrown as AggregateException;
            if (aggregate == null) {
                return "expected AggregateException(original, rollback errors), got " + Describe(thrown);
            }
            var inner = aggregate.InnerExceptions;
            if (inner.Count == 0 || !ReferenceEquals(inner[0], original)) {
                return "InnerExceptions[0] is " + (inner.Count == 0 ? "missing" : Describe(inner[0])) +
                       ", not the original " + Describe(original) + "; structure: " + Structure(thrown);
            }
            var rest = new List<Exception>();
            for (int i = 1; i < inner.Count; i++) {
                rest.Add(inner[i]);
            }
            if (!SameSet(rest, rollback)) {
                return "the inner exceptions after the original are not exactly the rollback errors " +
                       "[" + string.Join(", ", rollback.ConvertAll(Describe)) + "]; structure: " + Structure(thrown);
            }
            return null;
        }

        private static bool SameSet(List<Exception> a, List<Exception> b) {
            if (a.Count != b.Count) {
                return false;
            }
            var left = new List<Exception>(b);
            foreach (var x in a) {
                int at = left.FindIndex(y => ReferenceEquals(x, y));
                if (at < 0) {
                    return false;
                }
                left.RemoveAt(at);
            }
            return true;
        }

        /// <summary> The messages of an exception and of every exception inside it (inner, aggregated), joined. </summary>
        public static string Messages(Exception e) {
            var parts = new List<string>();
            void Walk(Exception x, int depth) {
                if (x == null || depth > 8) {
                    return;
                }
                parts.Add(x.Message);
                if (x is AggregateException aggregate) {
                    foreach (var inner in aggregate.InnerExceptions) {
                        Walk(inner, depth + 1);
                    }
                } else {
                    Walk(x.InnerException, depth + 1);
                }
            }
            Walk(e, 0);
            return string.Join(" | ", parts);
        }

        public static string Describe(Exception e) {
            if (e == null) {
                return "no exception";
            }
            if (e is AggregateException) {
                return "AggregateException";
            }
            return e.GetType().Name + "(\"" + e.Message + "\")";
        }

        /// <summary> Compact tree of an exception: Agg[Agg[init failed: a, destroy failed: b], destroy failed: c]. </summary>
        public static string Structure(Exception e) {
            if (e == null) {
                return "none";
            }
            if (e is AggregateException aggregate) {
                var sb = new StringBuilder("Agg[");
                for (int i = 0; i < aggregate.InnerExceptions.Count; i++) {
                    if (i > 0) {
                        sb.Append(", ");
                    }
                    sb.Append(Structure(aggregate.InnerExceptions[i]));
                }
                return sb.Append(']').ToString();
            }
            return e.Message;
        }

        public static string ExpectEqual<T>(T actual, T expected, string what) {
            return EqualityComparer<T>.Default.Equals(actual, expected)
                ? null
                : what + ": expected " + Show(expected) + ", actual " + Show(actual);
        }

        public static string ExpectSame(object actual, object expected, string what) {
            return ReferenceEquals(actual, expected) ? null : what + ": expected the same instance, got another one";
        }

        public static string ExpectThrows<TException>(Action action, string what) where TException : Exception {
            var e = Catch(action);
            if (e is TException) {
                return null;
            }
            return what + ": expected " + typeof(TException).Name + ", got " + Describe(e);
        }

        public static string ExpectNoThrow(Action action, string what) {
            var e = Catch(action);
            return e == null ? null : what + ": expected no exception, got " + Describe(e);
        }

        private static string Show<T>(T value) {
            return value == null ? "null" : value is bool b ? (b ? "true" : "false") : value.ToString();
        }
    }

    /// <summary> One expected log token. </summary>
    public sealed class LogToken {
        public string Text;
        public bool Optional;

        public override string ToString() {
            return (Optional ? "?" : "") + Text;
        }
    }

    /// <summary> Where the expected and actual logs part ways. </summary>
    public sealed class LogMismatch {
        public int ExpectedIndex;
        public int ActualIndex;
        public LogToken Expected;   // null: the actual log has extra tokens
        public string Actual;       // null: the actual log ended early
    }

    public static class LogPattern {
        public static List<LogToken> Parse(string pattern) {
            var list = new List<LogToken>();
            foreach (var raw in pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                bool optional = raw.StartsWith("?", StringComparison.Ordinal);
                list.Add(new LogToken { Text = optional ? raw.Substring(1) : raw, Optional = optional });
            }
            return list;
        }

        public static string Format(List<LogToken> tokens) {
            return string.Join(" ", tokens.ConvertAll(t => t.ToString()));
        }

        /// <summary> Greedy comparison; optional tokens may be skipped. Null when the logs match. </summary>
        public static LogMismatch Compare(List<LogToken> expected, List<string> actual) {
            int i = 0;
            int k = 0;
            while (i < expected.Count || k < actual.Count) {
                if (i < expected.Count && k < actual.Count && Matches(expected[i].Text, actual[k])) {
                    i++;
                    k++;
                    continue;
                }
                if (i < expected.Count && expected[i].Optional) {
                    i++;
                    continue;
                }
                return new LogMismatch {
                    ExpectedIndex = i,
                    ActualIndex = k,
                    Expected = i < expected.Count ? expected[i] : null,
                    Actual = k < actual.Count ? actual[k] : null
                };
            }
            return null;
        }

        /// <summary> Exact match, except that "?" stands for any count inside a "see:" token. </summary>
        public static bool Matches(string pattern, string actual) {
            if (pattern == actual) {
                return true;
            }
            if (!pattern.StartsWith("see:", StringComparison.Ordinal) || !actual.StartsWith("see:", StringComparison.Ordinal)) {
                return false;
            }
            int pc = pattern.LastIndexOf(':');
            int ac = actual.LastIndexOf(':');
            if (pc < 0 || ac < 0 || pattern.Substring(0, pc) != actual.Substring(0, ac)) {
                return false;
            }
            var p = pattern.Substring(pc + 1).Split(',');
            var a = actual.Substring(ac + 1).Split(',');
            if (p.Length != a.Length) {
                return false;
            }
            for (int x = 0; x < p.Length; x++) {
                if (p[x] != "?" && p[x] != a[x]) {
                    return false;
                }
            }
            return true;
        }
    }

    public static class Events {
        public const int TypeCount = 4;

        public static void Create(World world, int type) {
            switch (type) {
                case 0: world.CreateEntity(new Ev0()); break;
                case 1: world.CreateEntity(new Ev1()); break;
                case 2: world.CreateEntity(new Ev2()); break;
                case 3: world.CreateEntity(new Ev3()); break;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        public static int Count(World world, int type) {
            switch (type) {
                case 0: return world.Pool<Ev0>().Count;
                case 1: return world.Pool<Ev1>().Count;
                case 2: return world.Pool<Ev2>().Count;
                case 3: return world.Pool<Ev3>().Count;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        public static string Counts(World world) {
            return Count(world, 0) + "," + Count(world, 1) + "," + Count(world, 2) + "," + Count(world, 3);
        }

        public static SystemsRunner OneFrame(SystemsRunner runner, int type) {
            switch (type) {
                case 0: return runner.OneFrame<Ev0>();
                case 1: return runner.OneFrame<Ev1>();
                case 2: return runner.OneFrame<Ev2>();
                case 3: return runner.OneFrame<Ev3>();
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        public static SystemsRunner DelHere(SystemsRunner runner, int type) {
            switch (type) {
                case 0: return runner.DelHere<Ev0>();
                case 1: return runner.DelHere<Ev1>();
                case 2: return runner.DelHere<Ev2>();
                case 3: return runner.DelHere<Ev3>();
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        public static Type TypeOf(int type) {
            switch (type) {
                case 0: return typeof(Ev0);
                case 1: return typeof(Ev1);
                case 2: return typeof(Ev2);
                case 3: return typeof(Ev3);
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }

    /// <summary> Base of the probe systems; the concrete classes pick the interfaces. </summary>
    public abstract class Probe : ISystem {
        public readonly string Label;
        protected readonly Journal J;

        /// <summary> Init throws on its next FailInit calls. </summary>
        public int FailInit;
        /// <summary> Run throws on its next FailRun calls. </summary>
        public int FailRun;
        /// <summary> Destroy throws while Journal.DestroyThrowsArmed is set. </summary>
        public bool ThrowOnDestroy;
        /// <summary> Ev type index created on every Run, or -1. </summary>
        public int Produce = -1;
        /// <summary> Log the Ev pool counts on every Run (before producing). </summary>
        public bool Observe;

        /// <summary> Exception thrown by the last failing Init / Run. </summary>
        public Exception InitError;
        public Exception RunError;

        /// <summary> Arguments of the last Init call. </summary>
        public World InitWorld;
        public SharedData InitShared;

        protected Probe(string label, Journal journal) {
            Label = label;
            J = journal;
        }

        protected void DoInit(World world, SharedData shared) {
            InitWorld = world;
            InitShared = shared;
            J.Log.Add("init:" + Label);
            if (FailInit > 0) {
                FailInit--;
                InitError = new ProbeInitException("init failed: " + Label);
                throw InitError;
            }
        }

        protected void DoRun(World world) {
            J.Log.Add("run:" + Label);
            if (FailRun > 0) {
                FailRun--;
                RunError = new ProbeRunException("run failed: " + Label);
                throw RunError;
            }
            if (Observe) {
                J.Log.Add("see:" + Label + ":" + Events.Counts(world));
            }
            if (Produce >= 0) {
                Events.Create(world, Produce);
            }
        }

        protected void DoDestroy(World world) {
            J.Log.Add("destroy:" + Label);
            if (ThrowOnDestroy && J.DestroyThrowsArmed) {
                var e = new ProbeDestroyException("destroy failed: " + Label);
                J.DestroyErrors.Add(e);
                throw e;
            }
        }
    }

    public sealed class InitOnly : Probe, IInitSystem {
        public InitOnly(string label, Journal journal) : base(label, journal) { }
        public void Init(World world, SharedData shared) => DoInit(world, shared);
    }

    public sealed class RunOnly : Probe, IRunSystem {
        public RunOnly(string label, Journal journal) : base(label, journal) { }
        public void Run(World world) => DoRun(world);
    }

    public sealed class DestroyOnly : Probe, IDestroySystem {
        public DestroyOnly(string label, Journal journal) : base(label, journal) { }
        public void Destroy(World world) => DoDestroy(world);
    }

    public sealed class InitRun : Probe, IInitSystem, IRunSystem {
        public InitRun(string label, Journal journal) : base(label, journal) { }
        public void Init(World world, SharedData shared) => DoInit(world, shared);
        public void Run(World world) => DoRun(world);
    }

    public sealed class InitDestroy : Probe, IInitSystem, IDestroySystem {
        public InitDestroy(string label, Journal journal) : base(label, journal) { }
        public void Init(World world, SharedData shared) => DoInit(world, shared);
        public void Destroy(World world) => DoDestroy(world);
    }

    public sealed class RunDestroy : Probe, IRunSystem, IDestroySystem {
        public RunDestroy(string label, Journal journal) : base(label, journal) { }
        public void Run(World world) => DoRun(world);
        public void Destroy(World world) => DoDestroy(world);
    }

    public sealed class InitRunDestroy : Probe, IInitSystem, IRunSystem, IDestroySystem {
        public InitRunDestroy(string label, Journal journal) : base(label, journal) { }
        public void Init(World world, SharedData shared) => DoInit(world, shared);
        public void Run(World world) => DoRun(world);
        public void Destroy(World world) => DoDestroy(world);
    }

    public static class Probes {
        public const int I = 1;
        public const int R = 2;
        public const int D = 4;

        public static string ClassName(int caps) {
            switch (caps) {
                case I: return nameof(InitOnly);
                case R: return nameof(RunOnly);
                case D: return nameof(DestroyOnly);
                case I | R: return nameof(InitRun);
                case I | D: return nameof(InitDestroy);
                case R | D: return nameof(RunDestroy);
                case I | R | D: return nameof(InitRunDestroy);
                default: throw new ArgumentOutOfRangeException(nameof(caps), caps, "a probe needs at least one interface");
            }
        }

        public static Probe Create(int caps, string label, Journal journal) {
            switch (caps) {
                case I: return new InitOnly(label, journal);
                case R: return new RunOnly(label, journal);
                case D: return new DestroyOnly(label, journal);
                case I | R: return new InitRun(label, journal);
                case I | D: return new InitDestroy(label, journal);
                case R | D: return new RunDestroy(label, journal);
                case I | R | D: return new InitRunDestroy(label, journal);
                default: throw new ArgumentOutOfRangeException(nameof(caps), caps, "a probe needs at least one interface");
            }
        }
    }
}
