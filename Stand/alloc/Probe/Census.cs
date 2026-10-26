using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    /// <summary>
    /// What a first pass created in a world, counted outside the measured window: pool objects, the hook frames a pool
    /// allocates with its first typed listener, the list owner of an EventBuffer pool, the list objects of EventBuffer
    /// pools, the doublings of the entity storage, and the component data arrays of the pools: per component type the
    /// elements of the dense data arrays the pass allocated (the doublings from the length before the pass, or from the
    /// initial capacity of a pool the pass created, to the length after it) and the bytes of one element, so a component
    /// that grew a field shows as elements x the bytes it grew. Each object is sized by allocating one more of its exact
    /// type (RuntimeHelpers.GetUninitializedObject, Array.CreateInstance), so the numbers are the runtime's and the tree's own.
    ///
    /// The analysis (tools/analyze.py, documented costs) compares them with the baseline: a one-time difference that is
    /// exactly the sum of such per-object differences, each backed by a sentence of the target's docs, is a documented
    /// cost of that version, not a regression; any byte it does not account for keeps the regression.
    /// Internals are read by reflection and every field is optional: an older tree simply has fewer of them.
    /// </summary>
    public static class Census {
        private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo Pools = typeof(World).GetField("_pools", NP);
        private static readonly FieldInfo Generations = typeof(World).GetField("_generations", NP);
        private static readonly FieldInfo MaskWords = typeof(World).GetField("_maskWordCount", NP);
        private static readonly FieldInfo Config = typeof(World).GetField("_config", NP);
        private static readonly FieldInfo ReservedComponents = typeof(World).GetField("_reservedComponents", NP);

        public sealed class Snap {
            public readonly List<object> Pools = new();
            public readonly HashSet<object> Frames = new(ReferenceEqualityComparer.Instance);
            public readonly HashSet<object> Owners = new(ReferenceEqualityComparer.Instance);
            public readonly HashSet<object> Lists = new(ReferenceEqualityComparer.Instance);
            // The length of every pool's dense data array (RawData), by pool object.
            public readonly Dictionary<object, int> DataLengths = new(ReferenceEqualityComparer.Instance);
            public int Capacity;
            public int Words;
            // The dense capacity a pool created now starts with: max(InitialPoolDenseCapacity, reserved components); -1 unknown.
            public int NewPoolDense = -1;
        }

        /// <summary> AllocMeter.Once with the census of <paramref name="world"/> around it (outside the window). </summary>
        public static long Once<T>(ref T op, Result r, World world, params Type[] eventTypes) where T : struct, IAllocOp {
            var before = Take(world, eventTypes);
            long bytes = AllocMeter.Once(ref op);
            var after = Take(world, eventTypes);
            Report(r, before, after);
            return bytes;
        }

        public static Snap Take(World w, Type[] eventTypes) {
            var s = new Snap();
            if (w == null) {
                return s;
            }
            if (Pools?.GetValue(w) is Array pools) {
                foreach (var p in pools) {
                    if (p == null) {
                        continue;
                    }
                    s.Pools.Add(p);
                    var frames = p.GetType().GetField("_frames", NP)?.GetValue(p);
                    if (frames != null) {
                        s.Frames.Add(frames);
                    }
                    var owner = p.GetType().GetField("_listOwner", NP)?.GetValue(p);
                    if (owner != null) {
                        s.Owners.Add(owner);
                    }
                    if (p.GetType().GetProperty("RawData")?.GetValue(p) is Array data) {
                        s.DataLengths[p] = data.Length;
                    }
                }
            }
            s.Capacity = (Generations?.GetValue(w) as Array)?.Length ?? 0;
            s.Words = MaskWords?.GetValue(w) is int words ? words : 0;
            var config = Config?.GetValue(w);
            if (config?.GetType().GetField("InitialPoolDenseCapacity")?.GetValue(config) is int dense) {
                int reserved = ReservedComponents?.GetValue(w) is int rc ? rc : 0;
                s.NewPoolDense = Math.Max(dense, reserved);
            }
            foreach (var t in eventTypes ?? Type.EmptyTypes) {
                foreach (var list in ListsOf(w, t)) {
                    s.Lists.Add(list);
                }
            }
            return s;
        }

        // Every list object of EventBuffer<T>: the free ones in the static list pool and those of the world's buffers.
        private static IEnumerable<object> ListsOf(World w, Type eventType) {
            var poolType = typeof(World).Assembly.GetType("KenseiECS.ListPool`1")?.MakeGenericType(eventType);
            if (poolType?.GetField("_free", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IEnumerable free) {
                foreach (var l in free) {
                    if (l != null) {
                        yield return l;
                    }
                }
            }
            var bufferType = typeof(EventBuffer<>).MakeGenericType(eventType);
            int index = (int)typeof(ComponentType<>).MakeGenericType(bufferType).GetField("Index").GetValue(null);
            var pool = w.GetPool(index);
            if (pool == null) {
                yield break;
            }
            var data = pool.GetType().GetProperty("RawData")?.GetValue(pool) as Array;
            var values = bufferType.GetField("Values");
            if (data == null || values == null) {
                yield break;
            }
            for (int i = 0; i < pool.Count && i < data.Length; i++) {
                var l = values.GetValue(data.GetValue(i));
                if (l != null) {
                    yield return l;
                }
            }
        }

        public static void Report(Result r, Snap before, Snap after) {
            var oldPools = new HashSet<object>(before.Pools, ReferenceEqualityComparer.Instance);
            var newPools = after.Pools.Where(p => !oldPools.Contains(p)).ToList();
            var newFrames = after.Frames.Where(f => !before.Frames.Contains(f)).ToList();
            var newOwners = after.Owners.Where(o => !before.Owners.Contains(o)).ToList();
            var newLists = after.Lists.Where(l => !before.Lists.Contains(l)).ToList();
            int growths = 0;
            for (int c = Math.Max(1, before.Capacity); c < after.Capacity; c *= 2) {
                growths++;
            }
            r.Set("cost_pools", newPools.Count);
            r.Set("cost_pool_bytes", newPools.Sum(p => ObjectBytes(p.GetType())));
            r.Set("cost_frames", newFrames.Count);
            r.Set("cost_frame_bytes", newFrames.Sum(f => ArrayBytes((Array)f)));
            r.Set("cost_owners", newOwners.Count);
            r.Set("cost_owner_bytes", newOwners.Sum(o => ObjectBytes(o.GetType())));
            r.Set("cost_lists", newLists.Count);
            r.Set("cost_list_bytes", newLists.Sum(l => ObjectBytes(l.GetType())));
            r.Set("cost_growths", growths);
            r.Set("cost_mask_words", after.Words);
            // A growth that stages its mask rows before replacing them holds them in a ulong[words][].
            r.Set("cost_staging_bytes_per_growth", after.Words > 0 ? ArrayBytes(Array.CreateInstance(typeof(ulong[]), after.Words)) : 0);
            r.Set("cost_data", DataArrays(before, after));
        }

        /// <summary>
        /// "Type:elements:bytes;..." for the component data arrays the pass allocated: per component type, the elements of
        /// the dense data arrays it created (pools double: every length from the one before the pass, or a new pool's
        /// initial capacity, up to the one after it) and the bytes of one element. A type whose arrays this cannot account
        /// for (a length the doublings do not reach, a new pool without a known initial capacity) has elements "?".
        /// </summary>
        private static string DataArrays(Snap before, Snap after) {
            var byType = new SortedDictionary<string, (long Elements, bool Unknown, long Bytes)>(StringComparer.Ordinal);
            foreach (var kv in after.DataLengths) {
                var pool = kv.Key;
                int end = kv.Value;
                var elem = (pool.GetType().GetProperty("RawData")?.GetValue(pool) as Array)?.GetType().GetElementType();
                if (elem == null) {
                    continue;
                }
                long elements = 0;
                bool unknown = false;
                int start;
                if (before.DataLengths.TryGetValue(pool, out int had)) {
                    start = had;
                } else if (after.NewPoolDense >= 0) {
                    // A pool the pass created: its first array, then the doublings.
                    start = after.NewPoolDense;
                    elements += start;
                } else {
                    start = -1;
                    unknown = true;
                }
                if (!unknown && end != start) {
                    long len = Math.Max(start, 1);
                    while (len < end) {
                        len *= 2;
                        elements += len;
                    }
                    unknown = len != end;
                }
                string key = TypeKey(elem);
                byType.TryGetValue(key, out var acc);
                byType[key] = (acc.Elements + elements, acc.Unknown || unknown, ElementBytes(elem));
            }
            return string.Join(";", byType.Where(kv => kv.Value.Elements != 0 || kv.Value.Unknown)
                .Select(kv => kv.Key + ":" + (kv.Value.Unknown ? "?" : kv.Value.Elements.ToString()) + ":" + kv.Value.Bytes));
        }

        // EventBuffer`1[EvAddRemove]: the generic definition's name and its arguments, without namespaces.
        private static string TypeKey(Type t) =>
            t.IsGenericType ? t.Name + "[" + string.Join(",", t.GetGenericArguments().Select(TypeKey)) + "]" : t.Name;

        private static readonly Dictionary<Type, long> ElemSizes = new();

        // The bytes one more element adds to an array of this type (16 elements against none: exact for any element size).
        private static long ElementBytes(Type elem) {
            if (ElemSizes.TryGetValue(elem, out long n)) {
                return n;
            }
            long b16 = ArrayBytes(Array.CreateInstance(elem, 16));
            long b0 = ArrayBytes(Array.CreateInstance(elem, 0));
            ElemSizes[elem] = (b16 - b0) / 16;
            return ElemSizes[elem];
        }

        private static readonly Dictionary<Type, long> Sizes = new();

        private static long ObjectBytes(Type t) {
            if (Sizes.TryGetValue(t, out long n)) {
                return n;
            }
            RuntimeHelpers.GetUninitializedObject(t);   // type loading, outside the measurement
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var o = RuntimeHelpers.GetUninitializedObject(t);
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(o);
            Sizes[t] = b1 - b0;
            return b1 - b0;
        }

        private static long ArrayBytes(Array like) {
            var elem = like.GetType().GetElementType();
            Array.CreateInstance(elem, like.Length);
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var a = Array.CreateInstance(elem, like.Length);
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(a);
            return b1 - b0;
        }
    }
}
