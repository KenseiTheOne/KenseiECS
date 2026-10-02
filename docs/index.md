---
layout: home

hero:
  name: KenseiECS
  text: Sparse-set ECS for Unity and .NET
  tagline: Lightweight Entity Component System with O(1) component access, reactive filters and zero-allocation iteration.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/introduction
    - theme: alt
      text: Play the Demo
      link: /guide/demo
    - theme: alt
      text: GitHub
      link: https://github.com/KenseiTheOne/KenseiECS

features:
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><path d="M14 17.5h7M17.5 14v7"/></svg>'
    title: Sparse-set storage
    details: O(1) component access and dense arrays for cache-friendly iteration. 8-byte generational entities protect against stale handles.
    link: /concepts/components
    linkText: Components
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 4h18l-7 8.5V19l-4 2v-8.5z"/></svg>'
    title: Reactive filters
    details: Cached query results, updated automatically on component changes. Struct enumerator, zero allocations, reverse iteration safe for structural changes.
    link: /concepts/filters
    linkText: Filters
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="m12 3 9 5-9 5-9-5z"/><path d="m3 13 9 5 9-5"/></svg>'
    title: Owning groups and change tracking
    details: Owning groups keep chosen pools aligned for contiguous iteration. Opt-in change tracking shows what changed since a system's last run, whatever the system order.
    link: /guides/groups
    linkText: Groups
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="5" rx="1.5"/><rect x="7" y="11" width="14" height="4" rx="1.2"/><rect x="7" y="17" width="14" height="4" rx="1.2"/><path d="M4.5 9v10h2.5M4.5 13h2.5"/></svg>'
    title: Systems that stay organized
    details: Nested runners for Update / FixedUpdate / LateUpdate, named systems and phases you can toggle at runtime, SharedData without reflection, OneFrame events.
    link: /concepts/systems
    linkText: Systems
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 4.5 6v5.5c0 4.6 3.2 8.3 7.5 9.5 4.3-1.2 7.5-4.9 7.5-9.5V6z"/><path d="m8.8 12 2.3 2.3 4.4-4.6"/></svg>'
    title: Exception-safe, validated
    details: A throwing listener or system never leaves the world inconsistent. Dead and stale handle misuse throws under KENSEI_DEBUG, zero cost in release.
    link: /guides/debug-mode
    linkText: Release vs. KENSEI_DEBUG
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="m12 2.8 8 4.6v9.2l-8 4.6-8-4.6V7.4z"/><path d="M12 12 4 7.4M12 12l8-4.6M12 12v9.2"/></svg>'
    title: Unity integration
    details: EcsBootstrap, a listener bridge to MonoBehaviours without delegates, and editor tools — World Inspector, Profiler, EcsEntityView.
    link: /unity/bootstrap
    linkText: Unity
---

## At a glance

Components are plain structs. Systems declare what they need with attributes, and the source generator writes their `Init`.

::: code-group

<<< @/snippets/Concepts/Home.cs#system{csharp} [MoveSystem.cs]

<<< @/snippets/Concepts/Home.cs#components{csharp} [Components.cs]

<<< @/snippets/Concepts/Home.cs#run{csharp} [Game.cs]

:::

Walk through a complete program in the [Quick Start](./guide/quick-start.md), see how it compares in the [benchmarks](./benchmarks.md) and against [other ECS frameworks](./guide/comparison.md), or browse the [API reference](./api/index.md).

## See it run

<HomeDemo />
