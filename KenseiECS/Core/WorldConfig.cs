namespace KenseiECS {
    public struct WorldConfig {
        /// <summary> Initial capacity for entity slot arrays. </summary>
        public int InitialEntityCapacity;

        /// <summary> Initial sparse array capacity for component pools and filters. </summary>
        public int InitialPoolSparseCapacity;

        /// <summary> Initial dense array capacity for component pools and filters. </summary>
        public int InitialPoolDenseCapacity;

        /// <summary> Initial number of component type slots in the pool registry. </summary>
        public int InitialPoolCount;

        public static WorldConfig Default() {
            return new WorldConfig {
                InitialEntityCapacity = 256,
                InitialPoolSparseCapacity = 256,
                InitialPoolDenseCapacity = 64,
                InitialPoolCount = 32
            };
        }
    }
}
