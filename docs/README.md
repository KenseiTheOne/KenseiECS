# KenseiECS documentation

- [README](../README.md) at the repository root: install, API reference, contracts, debug tools. Start here.
- [architecture.md](architecture.md): how the framework works internally. Entity slots and generations, sparse-set pools, bitmasks, reactive filters and the reverse enumerator, the structural change flows, event ordering and exception safety, the `KENSEI_DEBUG` layer, a complexity table, and the reasoning behind the design limits.
- [migration-from-leoecslite.md](migration-from-leoecslite.md): side-by-side API mapping from LeoEcsLite, the behavior differences that matter, and a before/after migration of a typical system.
- [faq.md](faq.md): short answers to the recurring questions (archetypes, `CreateEntity` with a component, auto-destroy, threading, entity references, destroying inside loops, type count limits, IL2CPP/Burst, tests, enabling debug validation).

Release notes live in [KenseiECS/CHANGELOG.md](../KenseiECS/CHANGELOG.md); benchmark results in [BENCHMARKS.md](../BENCHMARKS.md).
