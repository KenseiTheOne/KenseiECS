using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KenseiStand.Alloc.Probe {
    /// <summary>
    /// The runtime keeps the static fields of classes in pinned object[] buckets (2 040 slots, then 4 080, ...) and
    /// allocates the next bucket on the thread whose class initialization needs a slot: 16 344 B (32 664 B for the
    /// next one), counted by GC.GetAllocatedBytesForCurrentThread in whatever window initializes that class. Which
    /// first pass pays depends on how many classes with statics the process initialized before it, so unrelated
    /// changes of the probe moved exactly 16 344 B between scenarios (.NET 8: reserve.first_entities_of_process of
    /// 2.2.0, the runner and invoke first passes under KENSEI_DEBUG; .NET 10 has the same buckets at other counts).
    ///
    /// <see cref="Prime"/> initializes classes of its own (a generic class with one static field, instantiated over
    /// pairs of BCL types) until such a bucket appears, once per process, before any scenario and outside every
    /// window: the scenarios then start with a fresh bucket and no window pays for the runtime's static storage.
    /// KenseiECS types are not touched, so a first pass is still the first use of the package in the process.
    /// </summary>
    public static class StaticsPrimer {
        private static class Slot<T> {
            internal static object Value = null;
        }

        /// <summary> The smallest bucket seen is object[2040]: 16 344 B on a 64-bit runtime. </summary>
        public const long MinBucketBytes = 16000;

        /// <summary> Classes initialized and the bytes of the bucket that appeared (0: none within the limit). </summary>
        public static (int Classes, long Bucket) Prime(int maxClasses = 6400) {
            var basics = new List<Type>();
            foreach (var t in typeof(object).Assembly.GetExportedTypes()) {
                if (t.IsClass && t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition && !t.ContainsGenericParameters) {
                    basics.Add(t);
                    if (basics.Count == 80) {
                        break;
                    }
                }
            }
            int n = 0;
            foreach (var a in basics) {
                foreach (var b in basics) {
                    if (n >= maxClasses) {
                        return (n, 0);
                    }
                    n++;
                    var slot = typeof(Slot<>).MakeGenericType(typeof(Tuple<,>).MakeGenericType(a, b));
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    RuntimeHelpers.RunClassConstructor(slot.TypeHandle);
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (bytes >= MinBucketBytes) {
                        return (n, bytes);
                    }
                }
            }
            return (n, 0);
        }
    }
}
