using System;
using KenseiECS;

// Code for docs/architecture.md.
//
// The excerpts on that page are copied from the framework's internals
// (KenseiECS/Core/World.cs, Filter.cs) and touch private fields, so they cannot be
// compiled against the public API. The classes below are stand-ins that declare those
// fields with the same names and types, which makes each excerpt type-check exactly as
// written. When the source of an excerpt changes, update the excerpt here; the comment
// above each region names the method it comes from.

namespace Docs.Snippets.Reference.Architecture {
    sealed class WorldInternals {
        private int[] _generations;
        private bool[] _alive;
        private int[] _componentCounts;
        private ulong[][] _componentMasks;
        private int _maskWordCount;
        private int[] _freeIndices;
        private int _freeCount;
        private ComponentPoolBase[] _pools;

        private int CreateEntityInternal() {
            int index;
            // World.CreateEntityInternal
            // #region generation-on-reuse
            // CreateEntityInternal, free-list branch
            index = _freeIndices[--_freeCount];
            int generation = _generations[index] + 1;
            if (generation == 0) {
                generation = 1;
            }
            _generations[index] = generation;
            // #endregion generation-on-reuse
            return index;
        }

        // World.Has<T>
        public bool Has<T>(Entity entity) where T : struct, IComponent {
            // #region has
            int typeIdx = ComponentType<T>.Index;
            int word = typeIdx >> 6;
            if (word >= _maskWordCount) {
                return false;
            }
            ulong[] maskWord = _componentMasks[word];
            if ((uint)entity.Index >= (uint)maskWord.Length) {
                return false;
            }
            return (maskWord[entity.Index] & (1UL << (typeIdx & 63))) != 0;
            // #endregion has
        }

        // World.CopyEntity / World.DrainComponents
        private void WalkMask(int idx) {
            // #region mask-walk
            for (int w = 0; w < _maskWordCount; w++) {
                ulong mask = _componentMasks[w][idx];
                while (mask != 0) {
                    int bit = TrailingZeroCount(mask);
                    int typeIdx = (w << 6) | bit;
                    _pools[typeIdx].Remove(idx);      // or .CopyTo(srcIdx, dstIdx)
                    mask &= mask - 1;
                }
            }
            // #endregion mask-walk
        }

        // World.EntityMatchesFilter, single-word branch (mask fields renamed to locals)
        private bool MatchesSingleWord(int w, int entityIndex, ulong include, ulong exclude, ulong any) {
            // #region single-word-test
            ulong entityWord = _componentMasks[w][entityIndex];
            return (entityWord & include) == include
                && (entityWord & exclude) == 0
                && (entityWord & any) != 0;
            // #endregion single-word-test
        }

        // #region destroy-entity
        public void DestroyEntity(Entity entity) {
            if (!IsAlive(entity)) {
                return;
            }
            DestroyEntityInternal(entity.Index);
        }
        // #endregion destroy-entity

        // World.DestroyEntityInternal, without the KENSEI_DEBUG pass counter
        private void DrainComponents(int idx) {
            // #region drain
            do {
                _componentCounts[idx] = 0;
                for (int w = 0; w < _maskWordCount; w++) {
                    ulong mask = _componentMasks[w][idx];
                    if (mask == 0) {
                        continue;
                    }
                    _componentMasks[w][idx] = 0;
                    while (mask != 0) {
                        int bit = TrailingZeroCount(mask);
                        _pools[(w << 6) | bit].Remove(idx);
                        mask &= mask - 1;
                    }
                }
            } while (_componentCounts[idx] != 0);
            // #endregion drain
        }

        private bool IsAlive(Entity entity) => _alive[entity.Index] && _generations[entity.Index] == entity.Generation;

        private void DestroyEntityInternal(int idx) => DrainComponents(idx);

        private static int TrailingZeroCount(ulong value) => System.Numerics.BitOperations.TrailingZeroCount(value);
    }
}

namespace Docs.Snippets.Reference.Architecture.FilterInternals {
    sealed class Filter {
        private const int FreeSlot = -1;

        internal int[] _denseEntities;
        internal int _count;
        private int[] _debugCursors;
        private int _debugIterators;

        // Filter.GrowDense
        private void GrowDense(int needed) {
            int[] old = _denseEntities;
            int newSize = Math.Max(old.Length * 2, needed);
            int[] grown = new int[newSize];
            Array.Copy(old, grown, old.Length);
            Array.Fill(grown, FreeSlot, old.Length, newSize - old.Length);
            // #region poison
            _denseEntities = grown;
            Array.Fill(old, FreeSlot, 0, old.Length);
            // #endregion poison
        }

        // Filter.CheckRemovalDuringIteration (KENSEI_DEBUG)
        private void CheckRemovalDuringIteration(int entityIndex, int denseIdx, int lastIdx) {
            // #region iteration-guard
            for (int d = 0; d < _debugIterators; d++) {
                int cursor = _debugCursors[d];
                if (denseIdx < cursor && lastIdx >= cursor) {
                    throw new InvalidOperationException(
                        $"Entity {entityIndex} left a filter that is being iterated before the loop reached it; " +
                        "an already visited entity would be visited twice");
                }
            }
            // #endregion iteration-guard
        }

        public ref struct Enumerator {
            private readonly Filter _filter;
            private Span<int> _entities;
            private int _index;
            private int _current;

            internal Enumerator(Filter filter) {
                _filter = filter;
                _entities = filter._denseEntities;
                _index = filter._count + 1;
                _current = 0;
            }

            public int Current => _current;

            // Filter.Enumerator.MoveNext, without the KENSEI_DEBUG cursor update
            // #region move-next
            public bool MoveNext() {
                int i = _index - 1;
                int entity = _entities[i];
                if (entity == FreeSlot) {
                    Filter filter = _filter;
                    _entities = filter._denseEntities;
                    int count = filter._count;
                    if (i > count) {
                        i = count;
                    }
                    if (i == 0) {
                        _index = 1;
                        return false;
                    }
                    entity = _entities[i];
                }

                _index = i;
                _current = entity;
                return true;
            }
            // #endregion move-next
        }
    }
}

// The code the source generator emits, for the example on the page. It is written by
// hand here (no injection attributes, so the generator stays out of it); compare with
// KenseiECS.Generators.Tests for the real output.
namespace Game {
    using KenseiECS;

    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }

    partial class MovementSystem {
        private Filter _moving;
        private ComponentPool<Position> _positions;
    }

    // #region generated
    partial class MovementSystem : global::KenseiECS.IInitSystem {
        partial void OnInit(global::KenseiECS.World world, global::KenseiECS.SharedData shared);

        public void Init(global::KenseiECS.World world, global::KenseiECS.SharedData shared) {
            this._moving = world.Filter().Inc<global::Game.Position>().Inc<global::Game.Velocity>().End();
            this._positions = world.Pool<global::Game.Position>();
            OnInit(world, shared);
        }
    }
    // #endregion generated
}
