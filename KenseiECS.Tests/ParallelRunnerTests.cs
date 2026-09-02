using System;
using System.Threading;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class ParallelRunnerTests {
        private struct MoveJob : IRangeJob {
            public Filter Filter;
            public ComponentPool<Position> Positions;
            public ComponentPool<Velocity> Velocities;

            public void Execute(int start, int end) {
                var entities = Filter.Entities;
                for (int i = start; i < end; i++) {
                    int e = entities[i];
                    Positions.Get(e).X += Velocities.Get(e).X;
                }
            }
        }

        private struct GroupJob : IRangeJob {
            public Group<Position, Velocity> Group;

            public void Execute(int start, int end) {
                var pos = Group.Data1;
                var vel = Group.Data2;
                for (int i = start; i < end; i++) {
                    pos[i].X += vel[i].X;
                }
            }
        }

        private struct CountingJob : IRangeJob {
            public int[] Hits;
            public int[] ThreadIds;

            public void Execute(int start, int end) {
                for (int i = start; i < end; i++) {
                    Interlocked.Increment(ref Hits[i]);
                    ThreadIds[i] = Thread.CurrentThread.ManagedThreadId;
                }
            }
        }

        private struct ThrowingJob : IRangeJob {
            public void Execute(int start, int end) {
                if (start >= 2048) {
                    throw new InvalidOperationException("job failed at " + start);
                }
            }
        }

        private static World CreateWorld(int n) {
            var world = new World();
            for (int i = 0; i < n; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity { X = 1 });
            }
            return world;
        }

        [Test]
        public void Run_FilterJob_MatchesSequential() {
            var world = CreateWorld(10000);
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            using var runner = new ParallelRunner(3);
            runner.Run(new MoveJob { Filter = filter, Positions = world.Pool<Position>(), Velocities = world.Pool<Velocity>() }, filter.Count, 256);

            var positions = world.Pool<Position>();
            foreach (int e in filter) {
                Assert.That(positions.Get(e).X, Is.EqualTo((float)e + 1), $"entity {e} must be moved exactly once");
            }
        }

        [Test]
        public void Run_GroupJob_MatchesSequential() {
            var world = CreateWorld(5000);
            var group = world.Group<Position, Velocity>();
            using var runner = new ParallelRunner(2);
            runner.Run(new GroupJob { Group = group }, group.Count, 100);

            var pos = group.Data1;
            var entities = group.Entities;
            for (int i = 0; i < pos.Length; i++) {
                Assert.That(pos[i].X, Is.EqualTo((float)entities[i] + 1), "every member must be processed once");
            }
        }

        [Test]
        public void Run_EveryIndexExactlyOnce_AcrossThreads() {
            const int count = 100000;
            var hits = new int[count];
            var threads = new int[count];
            using var runner = new ParallelRunner(4);
            runner.Run(new CountingJob { Hits = hits, ThreadIds = threads }, count, 512);

            for (int i = 0; i < count; i++) {
                Assert.That(hits[i], Is.EqualTo(1), $"index {i} must be visited exactly once");
            }
            int distinct = 0;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < count; i++) {
                if (seen.Add(threads[i])) {
                    distinct++;
                }
            }
            Assert.That(distinct, Is.GreaterThan(1), "work must be spread over more than one thread");
        }

        [Test]
        public void Run_SmallRange_RunsInline() {
            var hits = new int[10];
            var threads = new int[10];
            using var runner = new ParallelRunner(2);
            runner.Run(new CountingJob { Hits = hits, ThreadIds = threads }, 10, 1024);
            for (int i = 0; i < 10; i++) {
                Assert.That(threads[i], Is.EqualTo(Thread.CurrentThread.ManagedThreadId), "a range smaller than one chunk runs on the caller");
            }
        }

        [Test]
        public void Run_ZeroWorkers_RunsInline() {
            var hits = new int[5000];
            var threads = new int[5000];
            using var runner = new ParallelRunner(0);
            runner.Run(new CountingJob { Hits = hits, ThreadIds = threads }, 5000, 100);
            for (int i = 0; i < 5000; i++) {
                Assert.That(hits[i], Is.EqualTo(1), "inline run must visit every index");
                Assert.That(threads[i], Is.EqualTo(Thread.CurrentThread.ManagedThreadId), "inline run stays on the caller");
            }
        }

        [Test]
        public void Run_JobThrows_RethrowsOnCaller() {
            using var runner = new ParallelRunner(2);
            Assert.That(() => runner.Run(new ThrowingJob(), 10000, 1024), Throws.InvalidOperationException, "worker exception must surface on the calling thread");
            var hits = new int[3000];
            runner.Run(new CountingJob { Hits = hits, ThreadIds = new int[3000] }, 3000, 100);
            Assert.That(Array.TrueForAll(hits, h => h == 1), Is.True, "runner must be usable after a failed job");
        }

        [Test]
        public void Run_Repeated_DoesNotAllocate() {
            var hits = new int[20000];
            var threads = new int[20000];
            using var runner = new ParallelRunner(2);
            var job = new CountingJob { Hits = hits, ThreadIds = threads };
            var perRun = new long[40];
            for (int i = 0; i < perRun.Length; i++) {
                long before = GC.GetAllocatedBytesForCurrentThread();
                runner.Run(job, hits.Length, 1000);
                perRun[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            long allocated = 0;
            for (int i = 5; i < perRun.Length; i++) {
                allocated += perRun[i];
            }
            Assert.That(allocated, Is.EqualTo(0), "Run must not allocate on the calling thread after warmup; per run: " + string.Join(",", perRun));
        }

        [Test]
        public void Dispose_StopsWorkers_AndRejectsRun() {
            var runner = new ParallelRunner(2);
            runner.Dispose();
            Assert.That(() => runner.Run(new CountingJob { Hits = new int[10], ThreadIds = new int[10] }, 10), Throws.TypeOf<ObjectDisposedException>(), "disposed runner must reject Run");
        }
    }
}
