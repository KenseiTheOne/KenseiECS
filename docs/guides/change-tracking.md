# Change Tracking

Opt in per pool. `Get` stays untracked; write through `Modify` instead:

<<< @/snippets/Guides/ChangeTracking.cs#sync{csharp}

Elsewhere, writers mark what they touched:

<<< @/snippets/Guides/ChangeTracking.cs#write{csharp}

Versions come from a world-wide counter that `Add`, `Modify` and `MarkChanged` advance, so a consumer sees exactly the changes made since its own last run, whatever the system order.

## API

| Member | Meaning |
|---|---|
| `pool.TrackChanges()` | Start recording change versions. Components already in the pool count as changed now. Calling it again does nothing. |
| `pool.TracksChanges` | Whether the pool records versions. |
| `pool.Modify(e)` | `ref` to the component, like `Get`, and marks it changed. |
| `pool.MarkChanged(e)` | Marks the component changed without reading it. |
| `pool.ChangedVersion(e)` | Version recorded by the last `Add`, `Modify` or `MarkChanged`. |
| `pool.ChangedSince(e, version)` | `true` when the component was added or modified after `version` (a value of `world.ChangeVersion`). |
| `world.ChangeVersion` | Current value of the world-wide counter; store it as a bookmark at the end of `Run`. |

::: warning
`ChangedVersion` and `ChangedSince` throw on a pool that does not track changes — call `TrackChanges()` once (e.g. in `Init`). `Modify` and `MarkChanged` work on any pool; without tracking they record nothing.
:::

Tracking costs one `int` per component and one store per `Add`, `Remove` and `Modify`. Writes through `Get` or `RawData` are invisible to it.
