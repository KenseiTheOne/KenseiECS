# KenseiECS documentation

**Read the documentation at https://kenseitheone.github.io/KenseiECS/.** This folder holds the sources of that site (VitePress); pages that include files from elsewhere in the repository or import compiled code samples render properly only there. Links between pages are relative `.md` links, so they work both on the site and here. This index is for browsing on GitHub and is excluded from the site.

- [index.md](index.md): the site's home page.
- [guide/](guide): introduction, installation, quick start.
- [concepts/](concepts): entities, components, filters, systems and `SharedData`, `SystemsRunner`.
- [guides/](guides): groups, change tracking, `CommandBuffer`, singletons, OneFrame components, component listeners, world events, world lifecycle, snapshots, the generated `Init`, threading, `WorldConfig`, Release vs. `KENSEI_DEBUG`.
- [unity/](unity): `EcsBootstrap` and authoring, `EcsEntityView`, the listener bridge, debug tools, the BasicGame sample.
- [architecture.md](architecture.md): how the framework works internally. Entity slots and generations, sparse-set pools and the `_hasHooks` fast path, bitmasks, reactive filters with the per-type table and the reverse enumerator, owning groups and the alignment invariant, change tracking, the structural change flows, event ordering and exception safety, snapshots (file layout and restore), the source generator, the `KENSEI_DEBUG` layer, a complexity table, and the reasoning behind the design limits.
- [migration-from-leoecslite.md](migration-from-leoecslite.md): side-by-side API mapping from LeoEcsLite, the behavior differences that matter, what groups, change tracking, snapshots, the generated `Init` (versus `ecslite-di`) and `EcsBootstrap` add, and a before/after migration of a typical system.
- [faq.md](faq.md): short answers to the recurring questions (archetypes, `CreateEntity` with a component, auto-destroy, threading, entity references, destroying inside loops, type count limits, IL2CPP/Burst, tests, enabling debug validation, groups versus filters, change detection, save/load, generated `Init`, Unity scene setup).
- [contributing.md](contributing.md): repository layout, running the tests, running the docs locally, the compiled-snippet convention.
- [snippets/](snippets): C# projects the site's code samples are imported from; they build as part of `KenseiECS.sln`.

Release notes live in [KenseiECS/CHANGELOG.md](../KenseiECS/CHANGELOG.md); benchmark results in [BENCHMARKS.md](../BENCHMARKS.md). The site's `benchmarks.md` and `changelog.md` pages only include those files.
