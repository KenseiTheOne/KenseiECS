namespace KenseiECS {
    /// <summary> Marker interface for all ECS systems. </summary>
    public interface ISystem { }

    /// <summary> Called once during SystemsRunner.Init(). </summary>
    public interface IInitSystem : ISystem {
        void Init(World world, SharedData shared);
    }

    /// <summary> Called every frame during SystemsRunner.Run(). </summary>
    public interface IRunSystem : ISystem {
        void Run(World world);
    }

    /// <summary> Called once during SystemsRunner.Destroy(). </summary>
    public interface IDestroySystem : ISystem {
        void Destroy(World world);
    }
}
