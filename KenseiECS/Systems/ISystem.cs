namespace KenseiECS {
    /// <summary> Marker interface for all ECS systems. </summary>
    public interface ISystem { }

    /// <summary> Called once during SystemsRunner.Init(). </summary>
    public interface IInitSystem : ISystem {
        /// <summary> Called once before the first Run; acquire pools, filters and shared data here. </summary>
        void Init(World world, SharedData shared);
    }

    /// <summary> Called every frame during SystemsRunner.Run(). </summary>
    public interface IRunSystem : ISystem {
        /// <summary> Called on every Run of the owning runner while the system is enabled. </summary>
        void Run(World world);
    }

    /// <summary> Called once during SystemsRunner.Destroy(). </summary>
    public interface IDestroySystem : ISystem {
        /// <summary> Called once on shutdown, in reverse registration order. </summary>
        void Destroy(World world);
    }
}
