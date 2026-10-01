# Singletons

For a component that lives on exactly one entity (game state, camera, player):

<<< @/snippets/Guides/Singletons.cs#access{csharp}

A singleton is an ordinary component; there is no separate registration. It exists while exactly one entity holds it:

<<< @/snippets/Guides/Singletons.cs#setup{csharp}

- `GetSingleton<T>()` returns a `ref` to the component and throws unless exactly one entity has it.
- `GetSingletonEntity<T>()` returns the holder's `Entity`, with the same check.
- `HasSingleton<T>()` is `true` only when exactly one entity has the component — `false` for zero and for two or more.
