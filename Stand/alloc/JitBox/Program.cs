using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace KenseiStand.Alloc.JitBox {
    /// <summary>
    /// Every window below is empty: nothing runs between the two reads of
    /// GC.GetAllocatedBytesForCurrentThread(). A non-zero result is therefore an artifact of how the
    /// result is checked. Prints one JITBOX line of JSON.
    /// </summary>
    public static class Program {
        public static int Main() {
            var d = (DebuggableAttribute)Attribute.GetCustomAttribute(typeof(Program).Assembly, typeof(DebuggableAttribute));
            bool optimized = d == null || !d.IsJITOptimizerDisabled;
            // Each variant twice: the second call runs warm code.
            long f0 = 0, f1 = 0, f2 = 0, f3 = 0, f4 = 0;
            for (int i = 0; i < 2; i++) {
                f0 = Math.Max(f0, Run(IsEqualToZero));
                f1 = Math.Max(f1, Run(IsZero));
                f2 = Math.Max(f2, Run(IsEqualToZeroLong));
                f3 = Math.Max(f3, NoNUnitReplica());
                var empty = new Empty();
                f4 = Math.Max(f4, AllocMeter.Bytes(ref empty, 1));
            }
            Console.WriteLine("JITBOX {\"runtime\":\"" + RuntimeInformation.FrameworkDescription + "\",\"optimized\":" + (optimized ? "true" : "false")
                + ",\"is_equal_to_0\":" + f0 + ",\"is_zero\":" + f1 + ",\"is_equal_to_0L\":" + f2
                + ",\"replica_object_param\":" + f3 + ",\"meter_empty\":" + f4 + "}");
            return 0;
        }

        private struct Empty : IAllocOp {
            public void Invoke() { }
        }

        // The value NUnit saw: "But was: 24" in the failure message, 0 when the assert passed.
        private static long Run(Action test) {
            try {
                test();
                return 0;
            } catch (AssertionException ex) {
                var m = Regex.Match(ex.Message, @"But was:\s+(-?\d+)");
                return m.Success ? long.Parse(m.Groups[1].Value) : -1;
            }
        }

        // The pattern of the failing KenseiECS tests.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void IsEqualToZero() {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0), "empty window");
        }

        // The fix used by the working copy's V220RunnerTests: Is.Zero boxes nothing at the call site.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void IsZero() {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, "empty window");
        }

        // A long constant boxes too.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void IsEqualToZeroLong() {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), "empty window");
        }

        // Same shape without NUnit, compiled in this assembly (so in its configuration): the result
        // is used once, next to a boxed constant.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long NoNUnitReplica() {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return Report(allocated, 0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Report(long allocated, object expected) {
            Sink.Value = expected;
            return allocated;
        }
    }
}
