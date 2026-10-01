---
layout: home

hero:
  name: KenseiECS
  text: Sparse-set ECS for Unity and .NET
  tagline: Lightweight Entity Component System with O(1) component access, reactive filters and zero-allocation iteration.
  image:
    src: /favicon.svg
    alt: KenseiECS
  actions:
    - theme: brand
      text: Get Started
      link: /guide/introduction
    - theme: alt
      text: Quick Start
      link: /guide/quick-start
    - theme: alt
      text: Live Demo
      link: /guide/demo
    - theme: alt
      text: GitHub
      link: https://github.com/KenseiTheOne/KenseiECS

features:
  - title: Sparse-set storage
    details: O(1) component access and dense arrays for cache-friendly iteration. 8-byte generational entities protect against stale handles.
    link: /concepts/components
    linkText: Components
  - title: Reactive filters
    details: Cached query results, updated automatically on component changes. Struct enumerator, zero allocations, reverse iteration safe for structural changes.
    link: /concepts/filters
    linkText: Filters
  - title: Owning groups and change tracking
    details: Owning groups keep chosen pools aligned for contiguous iteration. Opt-in change tracking shows what changed since a system's last run, whatever the system order.
    link: /guides/groups
    linkText: Groups
  - title: Systems that stay organized
    details: Nested runners for Update / FixedUpdate / LateUpdate, named systems and phases you can toggle at runtime, SharedData without reflection, OneFrame events.
    link: /concepts/systems
    linkText: Systems
  - title: Exception-safe, validated
    details: A throwing listener or system never leaves the world inconsistent. Dead and stale handle misuse throws under KENSEI_DEBUG, zero cost in release.
    link: /guides/debug-mode
    linkText: Release vs. KENSEI_DEBUG
  - title: Unity integration
    details: EcsBootstrap, a listener bridge to MonoBehaviours without delegates, and editor tools — World Inspector, Profiler, EcsEntityView.
    link: /unity/bootstrap
    linkText: Unity
---

## At a glance

<<< @/snippets/Concepts/QuickStart.cs#quickstart{csharp}

Walk through it step by step in the [Quick Start](./guide/quick-start.md), or see how it compares in the [benchmarks](./benchmarks.md).

## See it run

[**Horde**](./guide/demo.md) is a survivors-like game built on KenseiECS that runs in the browser on .NET WebAssembly: thousands of enemies after a few minutes, 20,000+ with the stress slider, and an overlay with every system's time per frame. Each of its 22 systems shows one feature of the library — [play it and see how it's built](./guide/demo.md).
