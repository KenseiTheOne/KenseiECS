using System;
using System.Collections.Generic;
using KenseiECS;

namespace KenseiStand.FuzzCore {
    // Component kinds the fuzzer uses. The order is the registration order in the "narrow" layout.
    public enum CT { A, B, C, D, Tag, R, K, Ev, EB, LS }

    // A shared counter object referenced by the R and K components: AutoReset and AutoCopy
    // leave their marks here, so their calls are observable from outside the pool.
    public sealed class Probe {
        public readonly int Id;
        public readonly int Gen;
        public int Resets;
        public int Copies;
        public readonly List<Probe> Registry;

        public Probe(int id, int gen, List<Probe> registry) {
            Id = id;
            Gen = gen;
            Registry = registry;
            registry?.Add(this);
        }

        public Probe Fork() => new Probe(Id, Gen + 1, Registry);

        public string Digest => "b" + Id + "." + Gen + "r" + Resets + "c" + Copies;
    }

    public struct A : IComponent { public int Id; public int P; }
    public struct B : IComponent { public int Id; public int P; }
    public struct C : IComponent { public int Id; public int P; }
    // D: its pool tracks changes (TrackChanges at setup).
    public struct D : IComponent { public int Id; public int P; }
    public struct Tag : IComponent { }

    // IAutoReset only: CopyEntity copies it by value (the Probe is shared, as documented for a shallow copy).
    public struct R : IComponent, IAutoReset<R> {
        public int Id;
        public int P;
        public Probe Box;

        public void AutoReset(ref R c) {
            if (c.Box != null) {
                c.Box.Resets++;
            }
            c.Box = null;
            c.P = 0;
        }
    }

    // IAutoReset + IAutoCopy: CopyEntity gives the copy its own Probe.
    public struct K : IComponent, IAutoReset<K>, IAutoCopy<K> {
        public int Id;
        public int P;
        public Probe Box;

        public void AutoReset(ref K c) {
            if (c.Box != null) {
                c.Box.Resets++;
            }
            c.Box = null;
            c.P = 0;
        }

        public void AutoCopy(ref K c) {
            if (c.Box != null) {
                c.Box.Copies++;
                c.Box = c.Box.Fork();
            }
        }
    }

    // One-frame event component (OneFrame<Ev> on the runner).
    public struct Ev : IComponent { public int Id; public int P; }

    // Event payload of EventBuffer<Hit> (OneFrame<EventBuffer<Hit>> on the runner).
    public struct Hit { public int V; }

    // Listener interface stored in Listeners<IFoo>.
    public interface IFoo { int Id { get; } }

    public sealed class Foo : IFoo {
        private static readonly Dictionary<int, Foo> Cache = new();
        public int Id { get; }
        private Foo(int id) { Id = id; }

        public static Foo Get(int id) {
            lock (Cache) {
                if (!Cache.TryGetValue(id, out var f)) {
                    f = new Foo(id);
                    Cache[id] = f;
                }
                return f;
            }
        }

        public override string ToString() => "foo" + Id;
    }

    // Filler component types: the "wide" layouts register 60 of them so that some fuzzer
    // types land in the second 64-type mask word (multi-word masks and filters).
    public struct Dm<T> : IComponent where T : struct { }

    public static class TypeMap {
        public const int Count = 10;
        public static readonly int[] Index = new int[Count];
        public static readonly CT[] All = { CT.A, CT.B, CT.C, CT.D, CT.Tag, CT.R, CT.K, CT.Ev, CT.EB, CT.LS };
        public static CT[] ByTypeIndex;   // CT sorted by ascending type index
        public static string Layout = "narrow";
        private static bool _initialized;

        public static int Of(CT t) => Index[(int)t];

        public static CT? FromIndex(int typeIndex) {
            for (int i = 0; i < Count; i++) {
                if (Index[i] == typeIndex) {
                    return (CT)i;
                }
            }
            return null;
        }

        public static CT Parse(string s) {
            switch (s) {
                case "A": return CT.A;
                case "B": return CT.B;
                case "C": return CT.C;
                case "D": return CT.D;
                case "Tag": return CT.Tag;
                case "R": return CT.R;
                case "K": return CT.K;
                case "Ev": return CT.Ev;
                case "EB": return CT.EB;
                case "LS": return CT.LS;
            }
            throw new ArgumentException("unknown component kind " + s);
        }

        public static bool TryParse(string s, out CT t) {
            try {
                t = Parse(s);
                return true;
            } catch (ArgumentException) {
                t = CT.A;
                return false;
            }
        }

        public static string Name(CT t) => t.ToString();

        public static bool HasPayload(CT t) => t != CT.Tag && t != CT.EB && t != CT.LS;

        // Must run before any other code touches ComponentType<T>: indices are process-wide and
        // assigned on first touch.
        public static void Init(string layout) {
            if (_initialized) {
                if (layout != Layout) {
                    throw new InvalidOperationException("TypeMap already initialised with layout " + Layout);
                }
                return;
            }
            _initialized = true;
            Layout = layout ?? "narrow";
            switch (Layout) {
                case "narrow":
                    TouchAll(All);
                    break;
                case "wideA":
                    // A, B, Tag, R in word 0; the rest in word 1.
                    TouchAll(new[] { CT.A, CT.B, CT.Tag, CT.R });
                    TouchFillers(60);
                    TouchAll(new[] { CT.C, CT.D, CT.K, CT.Ev, CT.EB, CT.LS });
                    break;
                case "wideB":
                    // C, D, K, Ev, EB, LS in word 0; A, B, Tag, R in word 1.
                    TouchAll(new[] { CT.C, CT.D, CT.K, CT.Ev, CT.EB, CT.LS });
                    TouchFillers(58);
                    TouchAll(new[] { CT.A, CT.B, CT.Tag, CT.R });
                    break;
                default:
                    throw new ArgumentException("unknown layout " + Layout);
            }
            var sorted = new List<CT>(All);
            sorted.Sort((x, y) => Of(x).CompareTo(Of(y)));
            ByTypeIndex = sorted.ToArray();
        }

        private static void TouchAll(CT[] kinds) {
            foreach (var t in kinds) {
                Index[(int)t] = Touch(t);
            }
        }

        private static int Touch(CT t) {
            switch (t) {
                case CT.A: return ComponentType<A>.Index;
                case CT.B: return ComponentType<B>.Index;
                case CT.C: return ComponentType<C>.Index;
                case CT.D: return ComponentType<D>.Index;
                case CT.Tag: return ComponentType<Tag>.Index;
                case CT.R: return ComponentType<R>.Index;
                case CT.K: return ComponentType<K>.Index;
                case CT.Ev: return ComponentType<Ev>.Index;
                case CT.EB: return ComponentType<EventBuffer<Hit>>.Index;
                case CT.LS: return ComponentType<Listeners<IFoo>>.Index;
            }
            throw new ArgumentOutOfRangeException(nameof(t));
        }

        // Registers n distinct closed generic filler types: Dm<byte>, Dm<Dm<byte>>, ...
        private static void TouchFillers(int n) {
            Type inner = typeof(byte);
            for (int i = 0; i < n; i++) {
                var t = typeof(Dm<>).MakeGenericType(inner);
                var ct = typeof(ComponentType<>).MakeGenericType(t);
                ct.GetField("Index").GetValue(null);
                inner = t;
            }
        }

        public static string Describe() {
            var parts = new List<string>();
            foreach (var t in All) {
                parts.Add(t + "=" + Of(t));
            }
            return Layout + " (" + string.Join(", ", parts) + ")";
        }
    }

    public static class Build {
#if KENSEI_DEBUG
        public const bool Debug = true;
#else
        public const bool Debug = false;
#endif
        public static string Config => Debug ? "dbg" : "rel";
    }
}
