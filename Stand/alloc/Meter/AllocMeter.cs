using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace KenseiStand.Alloc {
    /// <summary>
    /// One measured operation. Implemented by structs, so <see cref="AllocMeter"/> calls it through a
    /// constrained generic call: no delegate, no boxing, nothing allocated by the call itself.
    /// </summary>
    public interface IAllocOp {
        void Invoke();
    }

    /// <summary>
    /// Allocation meter. The measured window is always a method of this class that contains nothing
    /// but the two counter reads and the loop over the operation:
    /// <list type="bullet">
    /// <item>no Assert, no boxing, no string interpolation, no delegate, no params array inside the
    /// window, so nothing of the meter's own can be counted;</item>
    /// <item>[NoInlining] window methods: the caller's code (for example the boxed expected value of an
    /// <c>Assert.That(allocated, Is.EqualTo(0))</c>) cannot be scheduled between the two reads. That
    /// is the known artifact: with an optimized test assembly the C# compiler keeps
    /// <c>allocated</c> on the evaluation stack and the JIT allocates the 24-byte box of the
    /// expected <c>0</c> before the second read, on .NET 8 and .NET 10 alike;</item>
    /// <item>results are returned as <c>long</c> or written into arrays the caller allocated
    /// beforehand.</item>
    /// </list>
    /// Warm-up is separate (<see cref="Warm{T}"/>): a window never pays for JIT or first-call work
    /// unless the caller measures the first pass on purpose (<see cref="Once{T}"/>).
    /// Counts the current thread only (<see cref="GC.GetAllocatedBytesForCurrentThread"/>), which
    /// is exact per object on CoreCLR; <see cref="SelfTest"/> checks that on the running runtime.
    /// </summary>
    public static class AllocMeter {
        // Window methods are never inlined into the caller (nothing of the caller can be scheduled
        // between the two counter reads) and are compiled fully optimized at once: a tiered window
        // method with a loop goes through on-stack replacement in the middle of a window, and with
        // Dynamic PGO that transition was seen to allocate 24 bytes on the measuring thread
        // (.NET 10, a window of Listeners.Invoke; gone with TieredPGO=0 or QuickJitForLoops=0).
        // AggressiveOptimization (512) is spelled as a number: netstandard2.1 does not name it,
        // CoreCLR honours it, Mono ignores it.
        private const MethodImplOptions Window = (MethodImplOptions)((int)MethodImplOptions.NoInlining | 512);

        /// <summary> Bytes allocated by this thread while <paramref name="op"/> ran <paramref name="count"/> times. </summary>
        [MethodImpl(Window)]
        public static long Bytes<T>(ref T op, int count) where T : struct, IAllocOp {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++) {
                op.Invoke();
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }

        /// <summary> Bytes of a single run: the first pass when nothing ran before it. </summary>
        [MethodImpl(Window)]
        public static long Once<T>(ref T op) where T : struct, IAllocOp {
            long before = GC.GetAllocatedBytesForCurrentThread();
            op.Invoke();
            long after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }

#if NETCOREAPP3_0_OR_GREATER
        /// <summary>
        /// Bytes allocated by every thread of the process while <paramref name="op"/> ran
        /// <paramref name="count"/> times (<see cref="GC.GetTotalAllocatedBytes(bool)"/>, precise).
        /// For operations that hand work to other threads; runtime threads count too, so a single
        /// window can catch an unrelated allocation (the analysis treats such windows as sporadic).
        /// </summary>
        [MethodImpl(Window)]
        public static long BytesAllThreads<T>(ref T op, int count) where T : struct, IAllocOp {
            long before = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < count; i++) {
                op.Invoke();
            }
            long after = GC.GetTotalAllocatedBytes(true);
            return after - before;
        }

        /// <summary> <see cref="Steady{T}"/> counted over all threads (<see cref="BytesAllThreads{T}"/>). </summary>
        public static void SteadyAllThreads<T>(ref T op, int warmup, int perWindow, long[] windowBytes) where T : struct, IAllocOp {
            Warm(ref op, warmup);
            for (int w = 0; w < windowBytes.Length; w++) {
                windowBytes[w] = BytesAllThreads(ref op, perWindow);
            }
        }
#endif

        /// <summary>
        /// Long run in geometric spans. After <paramref name="warmup"/> more runs (<paramref name="done"/>
        /// ran before the call), each span starts at run P, counted from the first run, and covers
        /// [P, 2.25 P). A state that settles allocates nothing in any span. Storage that grows with every
        /// run and doubles its backing array (an unbounded pool) allocates in each span: its size is
        /// about a * run + c, and [P, 2.25 P) always contains one doubling point once a * P / 4 &gt; c.
        /// Bytes per span go to <paramref name="spanBytes"/>, runs per span to <paramref name="spanRuns"/>.
        /// </summary>
        public static void Spans<T>(ref T op, int done, int warmup, long[] spanBytes, int[] spanRuns) where T : struct, IAllocOp {
            Warm(ref op, warmup);
            int position = done + warmup;
            for (int s = 0; s < spanBytes.Length; s++) {
                int length = position + position / 4;
                spanBytes[s] = Bytes(ref op, length);
                spanRuns[s] = length;
                position += length;
            }
        }

        /// <summary> Stopwatch ticks of <paramref name="count"/> runs. </summary>
        [MethodImpl(Window)]
        public static long Ticks<T>(ref T op, int count) where T : struct, IAllocOp {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) {
                op.Invoke();
            }
            return Stopwatch.GetTimestamp() - start;
        }

        /// <summary> Run the operation outside any window (JIT, tier-up, lazy one-time work). </summary>
        [MethodImpl(Window)]
        public static void Warm<T>(ref T op, int count) where T : struct, IAllocOp {
            for (int i = 0; i < count; i++) {
                op.Invoke();
            }
        }

        /// <summary>
        /// Steady state: <paramref name="warmup"/> runs outside any window, then one window of
        /// <paramref name="perWindow"/> runs per element of <paramref name="windowBytes"/>.
        /// </summary>
        public static void Steady<T>(ref T op, int warmup, int perWindow, long[] windowBytes) where T : struct, IAllocOp {
            Warm(ref op, warmup);
            for (int w = 0; w < windowBytes.Length; w++) {
                windowBytes[w] = Bytes(ref op, perWindow);
            }
        }

        /// <summary>
        /// Nanoseconds per run, one value per element of <paramref name="nsPerOp"/>. Warms up for at
        /// least <paramref name="warmMs"/> (long enough for tiered compilation to install optimized
        /// code) and sizes each window to about <paramref name="windowMs"/>.
        /// </summary>
        public static int Time<T>(ref T op, double warmMs, double windowMs, double[] nsPerOp) where T : struct, IAllocOp {
            double tickNs = 1e9 / Stopwatch.Frequency;
            int count = 16;
            var clock = Stopwatch.StartNew();
            // Calibrate and warm up together: grow the batch until one batch takes windowMs.
            while (true) {
                long ticks = Ticks(ref op, count);
                double ms = ticks * tickNs / 1e6;
                if (ms < windowMs && count < (1 << 26)) {
                    count *= 2;
                    continue;
                }
                if (clock.Elapsed.TotalMilliseconds >= warmMs) {
                    break;
                }
                // Let the background tier-1 compilation finish before the next batch.
                Thread.Sleep(1);
            }
            for (int w = 0; w < nsPerOp.Length; w++) {
                long ticks = Ticks(ref op, count);
                nsPerOp[w] = ticks * tickNs / count;
            }
            return count;
        }

        /// <summary>
        /// Checks that the counter is exact on this runtime: an empty window is 0, one
        /// <c>new object()</c> per run is <see cref="ObjectSize"/> bytes, one <c>new byte[100]</c> is
        /// <see cref="ByteArray100Size"/>. A meter that fails this is not used for findings.
        /// </summary>
        public static SelfTestResult SelfTest(int count) {
            var empty = new EmptyOp();
            var obj = new ObjectOp();
            var arr = new ByteArrayOp();
            Warm(ref empty, 64);
            Warm(ref obj, 64);
            Warm(ref arr, 64);
            var result = new SelfTestResult {
                Count = count,
                EmptyBytes = Bytes(ref empty, count),
                ObjectBytes = Bytes(ref obj, count),
                ByteArray100Bytes = Bytes(ref arr, count),
            };
            result.Ok = result.EmptyBytes == 0
                && result.ObjectBytes == (long)ObjectSize * count
                && result.ByteArray100Bytes == (long)ByteArray100Size * count;
            return result;
        }

        /// <summary> Size of an empty object: header + method table, 24 bytes on 64-bit (12 + padding on 32-bit). </summary>
        public static int ObjectSize => IntPtr.Size == 8 ? 24 : 12;

        /// <summary> Size of <c>new byte[100]</c>: header, method table, length and 100 bytes, rounded up to the pointer size. </summary>
        public static int ByteArray100Size => IntPtr.Size == 8 ? 128 : 112;

        private struct EmptyOp : IAllocOp {
            public void Invoke() { }
        }

        private struct ObjectOp : IAllocOp {
            public void Invoke() {
                Sink.Value = new object();
            }
        }

        private struct ByteArrayOp : IAllocOp {
            public void Invoke() {
                Sink.Value = new byte[100];
            }
        }
    }

    /// <summary> Result of <see cref="AllocMeter.SelfTest"/>. </summary>
    public struct SelfTestResult {
        public bool Ok;
        public int Count;
        public long EmptyBytes;
        public long ObjectBytes;
        public long ByteArray100Bytes;
    }

    /// <summary> Keeps allocated objects reachable so the JIT cannot drop the allocation. </summary>
    public static class Sink {
        public static object Value;
        public static long Number;
    }

    /// <summary>
    /// Deliberately wrong measurements, kept to show what the meter avoids. Not used for findings.
    /// </summary>
    public static class KnownArtifacts {
        /// <summary>
        /// The pattern of the KenseiECS allocation tests without NUnit: the window is empty, the
        /// result is used once and passed next to a boxed expected value, like
        /// <c>Assert.That(allocated, Is.EqualTo(0))</c>. In an optimized assembly this reports the
        /// 24-byte box of <c>0</c>; in a Debug assembly it reports 0. The meter's windows never
        /// contain such code.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long BoxedExpectedAfterWindow() {
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
