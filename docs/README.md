# KenseiECS documentation

- [README](../README.md) at the repository root: install, API reference, contracts, debug tools. Start here.
- [architecture.md](architecture.md): how the framework works internally. Entity slots and generations, sparse-set pools and the `_hasHooks` fast path, bitmasks, reactive filters with the per-type table and the reverse enumerator, owning groups and the alignment invariant, change tracking, the structural change flows, event ordering and exception safety, snapshots (file layout and restore), the source generator, the `KENSEI_DEBUG` layer, a complexity table, and the reasoning behind the design limits.
- [migration-from-leoecslite.md](migration-from-leoecslite.md): side-by-side API mapping from LeoEcsLite, the behavior differences that matter, what groups, change tracking, snapshots, the generated `Init` (versus `ecslite-di`) and `EcsBootstrap` add, and a before/after migration of a typical system.
- [faq.md](faq.md): short answers to the recurring questions (archetypes, `CreateEntity` with a component, auto-destroy, threading, entity references, destroying inside loops, type count limits, IL2CPP/Burst, tests, enabling debug validation, groups versus filters, change detection, save/load, generated `Init`, Unity scene setup).

Release notes live in [KenseiECS/CHANGELOG.md](../KenseiECS/CHANGELOG.md); benchmark results in [BENCHMARKS.md](../BENCHMARKS.md).
