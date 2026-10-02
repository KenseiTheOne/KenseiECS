using System;
using System.Diagnostics;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Lightweight entity identifier (8 bytes).
    /// Contains slot index and generation as separate ints.
    /// Generation is used to detect stale references:
    /// when a slot is reused, generation increments,
    /// making all old Entity values with the previous generation invalid.
    /// </summary>
    [DebuggerDisplay("E({Index}v{Generation})")]
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
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

        /// <summary> True when both index and generation match. </summary>
        public bool Equals(Entity other) => Index == other.Index && Generation == other.Generation;
        /// <summary> True when <paramref name="obj"/> is an <see cref="Entity"/> with the same index and generation. </summary>
        public override bool Equals(object obj) => obj is Entity e && Equals(e);
        /// <summary> Hash combining index and generation. </summary>
        public override int GetHashCode() => Index ^ (Generation * 397);
        /// <summary> Formats as index and generation, e.g. <c>E(5v2)</c>. </summary>
        public override string ToString() => $"E({Index}v{Generation})";

        /// <summary> True when both index and generation match. </summary>
        public static bool operator ==(Entity a, Entity b) => a.Index == b.Index && a.Generation == b.Generation;
        /// <summary> True when index or generation differ. </summary>
        public static bool operator !=(Entity a, Entity b) => a.Index != b.Index || a.Generation != b.Generation;
    }
}
