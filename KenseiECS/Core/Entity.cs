using System;
using System.Diagnostics;

namespace KenseiECS {
    /// <summary>
    /// Lightweight entity identifier (4 bytes).
    /// Contains slot index (20 bits) and generation (12 bits).
    /// Generation is used to detect stale references:
    /// when a slot is reused, generation increments,
    /// making all old Entity values with the previous generation invalid.
    /// </summary>
    [DebuggerDisplay("E({Index}v{Generation})")]
    public readonly struct Entity : IEquatable<Entity> {
        internal readonly uint Data;

        private const int GenBits = 12;
        private const uint GenMask = (1u << GenBits) - 1;   // 0xFFF

        /// <summary> Slot index in World entity arrays (0..1_048_575). </summary>
        public int Index => (int)(Data >> GenBits);

        /// <summary> Slot generation (0..4095). </summary>
        public int Generation => (int)(Data & GenMask);

        internal Entity(int index, int generation) {
            Data = ((uint)index << GenBits) | ((uint)generation & GenMask);
        }

        /// <summary> Invalid entity (Data = 0). </summary>
        public static readonly Entity Null = default;

        public bool Equals(Entity other) => Data == other.Data;
        public override bool Equals(object obj) => obj is Entity e && Equals(e);
        public override int GetHashCode() => (int)Data;
        public override string ToString() => $"E({Index}v{Generation})";

        public static bool operator ==(Entity a, Entity b) => a.Data == b.Data;
        public static bool operator !=(Entity a, Entity b) => a.Data != b.Data;
    }
}
