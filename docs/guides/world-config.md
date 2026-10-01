# WorldConfig

`WorldConfig` sets the starting sizes of a world's internal arrays.

<<< @/snippets/Guides/WorldConfig.cs#config{csharp}

Every array grows on demand; the config only sets starting sizes. Raise them to avoid growth during gameplay when you know the scale up front.

| Field | Sizes | Default |
|---|---|---|
| `InitialEntityCapacity` | entity slots, mask words, filter sparse arrays | 256 |
| `InitialPoolSparseCapacity` | per-pool sparse array | 256 |
| `InitialPoolDenseCapacity` | per-pool and per-filter dense arrays | 64 |
| `InitialPoolCount` | pool registry size | 32 |

`new World()` uses `WorldConfig.Default()`. A field left at zero (or set negative) falls back to its default, so a config can set only what it needs:

<<< @/snippets/Guides/WorldConfig.cs#partial{csharp}
