namespace KenseiECS {
    /// <summary> Called once during SystemsRunner.Init(). </summary>
    public interface IInitSystem {
        void Init(World world, SharedData shared);
    }

    /// <summary> Called every frame during SystemsRunner.Run(). </summary>
    public interface IRunSystem {
        void Run(World world);
    }

    /// <summary> Called once during SystemsRunner.Destroy(). </summary>
    public interface IDestroySystem {
        void Destroy(World world);
    }
}
