namespace KenseiECS {
    /// <summary>
    /// One growth of a storage array past what <see cref="World.Reserve"/> reserved: which array of
    /// which pool, filter or world grew, from what size to what size, and on which tick.
    /// <see cref="World.FirstGrowthAfterReserve"/> returns the first one; it is empty
    /// (<see cref="IsEmpty"/>) while nothing outgrew the reserve, and always without KENSEI_DEBUG.
    /// </summary>
    public readonly struct StorageGrowth {
        /// <summary>
        /// What owns the array: the component type name for a pool, the filter's description
        /// (<c>Filter Inc&lt;A, B&gt;</c>) for a filter, or <c>World</c>. Null when empty.
        /// </summary>
        public readonly string Owner;

        /// <summary>
        /// Which array grew: <c>entity slots</c>, <c>free-slot list</c>, <c>pool sparse</c>,
        /// <c>pool dense</c>, <c>filter dense</c> or <c>filter sparse page</c>. Null when empty.
        /// </summary>
        public readonly string Storage;

        /// <summary>
        /// Size before the growth. For a filter's dense array it is the capacity in entities
        /// (the array keeps one more slot); for a filter sparse page, the first entity index the
        /// new page covers.
        /// </summary>
        public readonly int OldSize;

        /// <summary>
        /// Size after the growth, counted like <see cref="OldSize"/>; for a filter sparse page,
        /// one past the last entity index the new page covers.
        /// </summary>
        public readonly int NewSize;

        /// <summary>
        /// The reserved size of that dimension when it grew: the <c>entities</c> or
        /// <c>components</c> value passed to <see cref="World.Reserve"/>.
        /// </summary>
        public readonly int Reserved;

        /// <summary> <see cref="World.Tick"/> when the array grew. </summary>
        public readonly int Tick;

        internal StorageGrowth(string owner, string storage, int oldSize, int newSize, int reserved, int tick) {
            Owner = owner;
            Storage = storage;
            OldSize = oldSize;
            NewSize = newSize;
            Reserved = reserved;
            Tick = tick;
        }

        /// <summary> True while nothing has outgrown the reserve (and always without KENSEI_DEBUG). </summary>
        public bool IsEmpty => Storage == null;

        /// <summary> For example <c>Position pool dense 512 -> 1024 (reserved 512) at tick 182</c>, or <c>none</c>. </summary>
        public override string ToString() {
            return IsEmpty
                ? "none"
                : $"{Owner} {Storage} {OldSize} -> {NewSize} (reserved {Reserved}) at tick {Tick}";
        }
    }
}
