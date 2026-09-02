using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace KenseiECS {
    /// <summary>
    /// Work over an index range [start, end). Implement on a struct so the
    /// runner copies it to each worker without boxing.
    ///
    /// Usage with a filter:
    ///   struct MoveJob : IRangeJob {
    ///       public Filter Filter;
    ///       public ComponentPool<Position> Positions;
    ///       public ComponentPool<Velocity> Velocities;
    ///       public void Execute(int start, int end) {
    ///           var entities = Filter.Entities;
    ///           for (int i = start; i < end; i++) {
    ///               int e = entities[i];
    ///               ref var pos = ref Positions.Get(e);
    ///               ref var vel = ref Velocities.Get(e);
    ///               pos.X += vel.X;
    ///           }
    ///       }
    ///   }
    ///   _runner.Run(new MoveJob { ... }, _filter.Count);
    ///
    /// Usage with a group: index Data1/Data2 spans from start to end.
    /// </summary>
    public interface IRangeJob {
        void Execute(int start, int end);
    }

    /// <summary>
    /// Runs an IRangeJob over a range on a fixed set of worker threads plus the
    /// calling thread. No allocations per Run after the first call for a job type.
    ///
    /// Rules for jobs: read and write component data only for the entities in the
    /// range; no Add/Remove/Create/Destroy, no filter or group registration, no
    /// world event listeners — those are single-threaded. Two jobs must not run
    /// at the same time on one runner. Exceptions thrown by a worker are
    /// rethrown on the calling thread after every chunk finished.
    ///
    /// With zero workers (single-core machines, WebGL) Run executes inline.
    /// </summary>
    public sealed class ParallelRunner : IDisposable {
        private interface IExecutor {
            void Execute(int start, int end);
        }

        // One instance per job type, reused: the job struct is copied in before each Run.
        private sealed class Executor<TJob> : IExecutor where TJob : struct, IRangeJob {
            public TJob Job;

            public void Execute(int start, int end) {
                Job.Execute(start, end);
            }
        }

        // Executors are cached per runner: the last one used is a type check away,
        // the rest live in a small list searched by type.
        private IExecutor _lastExecutor;
        private readonly System.Collections.Generic.List<IExecutor> _executors = new();

        private readonly Thread[] _workers;
        private readonly SemaphoreSlim _start;
        // Monitor on a preallocated object instead of ManualResetEventSlim: the
        // latter allocates its lock lazily on the first blocking wait.
        private readonly object _finishedLock = new();
        private bool _finished;
        private volatile IExecutor _executor;
        private int _chunkSize;
        private int _count;
        private int _nextChunk;
        private int _pendingWorkers;
        private ExceptionDispatchInfo _error;
        private volatile bool _disposed;

        /// <summary> Number of worker threads (the calling thread also works). </summary>
        public int WorkerCount => _workers.Length;

        public ParallelRunner() : this(Math.Max(0, Environment.ProcessorCount - 1)) {
        }

        public ParallelRunner(int workerCount) {
            if (workerCount < 0) {
                throw new ArgumentOutOfRangeException(nameof(workerCount));
            }
            _workers = new Thread[workerCount];
            _start = new SemaphoreSlim(0);
            for (int i = 0; i < workerCount; i++) {
                var thread = new Thread(WorkerLoop) {
                    IsBackground = true,
                    Name = "KenseiECS worker " + i
                };
                _workers[i] = thread;
                thread.Start();
            }
        }

        /// <summary>
        /// Execute job over [0, count) in chunks of at most chunkSize indices.
        /// Chunks are handed out dynamically, so uneven work balances itself.
        /// </summary>
        public void Run<TJob>(TJob job, int count, int chunkSize = 1024) where TJob : struct, IRangeJob {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(ParallelRunner));
            }
            if (chunkSize <= 0) {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }
            if (count <= 0) {
                return;
            }

            if (_workers.Length == 0 || count <= chunkSize) {
                job.Execute(0, count);
                return;
            }

            var executor = _lastExecutor as Executor<TJob> ?? FindOrCreateExecutor<TJob>();
            executor.Job = job;

            _executor = executor;
            _chunkSize = chunkSize;
            _count = count;
            _nextChunk = 0;
            _error = null;
            _pendingWorkers = _workers.Length;
            _finished = false;
            _start.Release(_workers.Length);

            DrainChunks(executor);

            var spin = new SpinWait();
            while (Volatile.Read(ref _pendingWorkers) != 0 && !spin.NextSpinWillYield) {
                spin.SpinOnce();
            }
            lock (_finishedLock) {
                while (!_finished) {
                    Monitor.Wait(_finishedLock);
                }
            }
            _executor = null;
            executor.Job = default;

            var error = _error;
            if (error != null) {
                _error = null;
                error.Throw();
            }
        }

        private Executor<TJob> FindOrCreateExecutor<TJob>() where TJob : struct, IRangeJob {
            for (int i = 0; i < _executors.Count; i++) {
                if (_executors[i] is Executor<TJob> found) {
                    _lastExecutor = found;
                    return found;
                }
            }
            var created = new Executor<TJob>();
            _executors.Add(created);
            _lastExecutor = created;
            return created;
        }

        private void WorkerLoop() {
            while (true) {
                _start.Wait();
                if (_disposed) {
                    return;
                }

                var executor = _executor;
                try {
                    DrainChunks(executor);
                } finally {
                    if (Interlocked.Decrement(ref _pendingWorkers) == 0) {
                        lock (_finishedLock) {
                            _finished = true;
                            Monitor.PulseAll(_finishedLock);
                        }
                    }
                }
            }
        }

        private void DrainChunks(IExecutor executor) {
            int chunkSize = _chunkSize;
            int count = _count;
            while (true) {
                int chunk = Interlocked.Increment(ref _nextChunk) - 1;
                int start = chunk * chunkSize;
                if (start >= count) {
                    return;
                }
                int end = Math.Min(start + chunkSize, count);
                try {
                    executor.Execute(start, end);
                } catch (Exception e) {
                    Interlocked.CompareExchange(ref _error, ExceptionDispatchInfo.Capture(e), null);
                    Interlocked.Exchange(ref _nextChunk, int.MaxValue / 2);
                    return;
                }
            }
        }

        /// <summary> Stop the worker threads. </summary>
        public void Dispose() {
            if (_disposed) {
                return;
            }
            _disposed = true;
            if (_workers.Length > 0) {
                _start.Release(_workers.Length);
            }
        }
    }
}
