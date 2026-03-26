namespace KenseiECS {
    public struct WorldConfig {
        /// <summary> Initial capacity for entity slot arrays. </summary>
        public int InitialEntityCapacity = 256;

        /// <summary> Initial sparse array capacity for component pools and filters. </summary>
        public int InitialPoolSparseCapacity = 256;

        /// <summary> Initial dense array capacity for component pools and filters. </summary>
        public int InitialPoolDenseCapacity = 64;

        /// <summary> Initial number of component type slots in the pool registry. </summary>
        public int InitialPoolCount = 32;

        public WorldConfig() { }
    }
}
