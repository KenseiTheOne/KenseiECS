using System;
using System.Diagnostics;

namespace KenseiECS {
    /// <summary>
    /// Lightweight entity identifier (8 bytes).
    /// Contains slot index and generation as separate ints.
    /// Generation is used to detect stale references:
    /// when a slot is reused, generation increments,
    /// making all old Entity values with the previous generation invalid.
    /// </summary>
    [DebuggerDisplay("E({Index}v{Generation})")]
    public readonly struct Entity : IEquatable<Entity> {
        /// <summary> Slot index in World entity arrays. </summary>
        public readonly int Index;

        /// <summary> Slot generation — increments each time a slot is reused. </summary>
        public readonly int Generation;

        internal Entity(int index, int generation) {
            Index = index;
            Generation = generation;
        }

        /// <summary> Invalid entity — default(Entity) = (0, 0), never matches a real entity (generations start at 1). </summary>
        public static readonly Entity Null = default;

        public bool Equals(Entity other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is Entity e && Equals(e);
        public override int GetHashCode() => Index ^ (Generation * 397);
        public override string ToString() => $"E({Index}v{Generation})";

        public static bool operator ==(Entity a, Entity b) => a.Index == b.Index && a.Generation == b.Generation;
        public static bool operator !=(Entity a, Entity b) => a.Index != b.Index || a.Generation != b.Generation;
    }
}
