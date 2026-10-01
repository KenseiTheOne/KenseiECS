# Threading

`World`, pools and filters are single-threaded. Reading `RawData`/`RawEntities` of pools from several threads is safe only while no structural change (Add/Remove/Create/Destroy) happens on any thread. Component type registration (`ComponentType<T>.Index`) is thread-safe.

## ParallelRunner

Data-parallel work over a filter or group without delegates or per-call allocations. A job is a struct implementing `IRangeJob`; the runner copies it to each worker without boxing.

<<< @/snippets/Guides/Threading.cs#job{csharp}

Create the runner once, run the job every frame, dispose the runner on shutdown:

<<< @/snippets/Guides/Threading.cs#system{csharp}

The range `[0, count)` is split into chunks of at most `chunkSize` indices (default 1024), handed out dynamically to the workers and the calling thread, so uneven work balances itself. After the first `Run` for a job type, `Run` allocates nothing.

### Workers

`ParallelRunner` starts its own dedicated `System.Threading.Thread` workers (background threads named `KenseiECS worker N`) in the constructor; it does not use the .NET thread pool or the Unity job system. `new ParallelRunner()` creates `Environment.ProcessorCount - 1` workers; `new ParallelRunner(n)` creates `n`. `WorkerCount` reports the number. The calling thread always works too.

::: tip When Run executes inline
`Run` calls `job.Execute(0, count)` directly on the calling thread, without waking any worker, when:

- the runner has 0 workers — `new ParallelRunner(0)`, or the default constructor on a single-core machine (use 0 workers on platforms without threads, such as WebGL), or
- `count <= chunkSize`.

`count <= 0` returns immediately.
:::

### Groups

For a group, index `Data1`/`Data2` from `start` to `end`:

<<< @/snippets/Guides/Threading.cs#group-job{csharp}

<<< @/snippets/Guides/Threading.cs#group-run{csharp}

### Rules for jobs

- Jobs may read and write component data of the entities in their range and nothing else: no structural changes, no listeners, no filter or group registration.
- Two jobs must not run at the same time on one runner.
- An exception in a worker is rethrown by `Run` on the calling thread once every worker stopped; the chunks not yet started are skipped.
- Dispose the runner on shutdown: `Dispose()` stops the worker threads, and `Run` on a disposed runner throws `ObjectDisposedException`.
