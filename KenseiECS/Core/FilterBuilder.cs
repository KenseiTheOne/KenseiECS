using System;
using System.Collections.Generic;

namespace KenseiECS {
    /// <summary>
    /// Fluent builder for creating filters.
    ///
    /// Usage:
    ///   var filter = world.Filter()
    ///       .Inc<Position>()
    ///       .Inc<Velocity>()
    ///       .Exc<Frozen>()
    ///       .End();
    /// </summary>
    public class FilterBuilder {
        private readonly World _world;
        private readonly List<int> _includes = new();
        private readonly List<int> _excludes = new();

        internal FilterBuilder(World world) {
            _world = world;
        }

        /// <summary> Require component of type T. </summary>
        public FilterBuilder Inc<T>() where T : struct, IComponent {
            int idx = ComponentType<T>.Index;
            if (!_includes.Contains(idx)) {
                _includes.Add(idx);
            }
            return this;
        }

        /// <summary> Exclude entities that have component of type T. </summary>
        public FilterBuilder Exc<T>() where T : struct, IComponent {
            int idx = ComponentType<T>.Index;
            if (!_excludes.Contains(idx)) {
                _excludes.Add(idx);
            }
            return this;
        }

        /// <summary>
        /// Finalize and register the filter with the World.
        /// If a filter with identical constraints already exists, returns the existing one.
        /// </summary>
        public Filter End() {
            if (_includes.Count == 0) {
                throw new InvalidOperationException(
                    "Filter requires at least one Inc<T> constraint. Exclude-only and empty filters are not supported — add Inc<T> for a component the target entities always have.");
            }

            for (int i = 0; i < _excludes.Count; i++) {
                if (_includes.Contains(_excludes[i])) {
                    throw new InvalidOperationException(
                        $"Filter has the same component type (index {_excludes[i]}) in both Inc and Exc — it can never match anything. Remove one of the constraints.");
                }
            }

            _includes.Sort();
            _excludes.Sort();

            return _world.RegisterFilter(
                _includes.ToArray(),
                _excludes.ToArray());
        }
    }
}
