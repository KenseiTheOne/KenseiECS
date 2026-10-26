namespace KenseiECS {
    /// <summary> Marker interface for all ECS systems. </summary>
    public interface ISystem { }

    /// <summary> Initialized by SystemsRunner.Init(), in registration order. </summary>
    public interface IInitSystem : ISystem {
        /// <summary>
        /// Called once by SystemsRunner.Init(), before the first Run; acquire pools, filters and
        /// shared data here. It runs again when the runner is initialized again: after
        /// SystemsRunner.Destroy(), or after an Init that failed in a later system, whose rollback
        /// destroys this system first if it implements IDestroySystem (the retry starts from the
        /// first system).
        /// </summary>
        void Init(World world, SharedData shared);
    }

    /// <summary> Called every frame during SystemsRunner.Run(). </summary>
    public interface IRunSystem : ISystem {
        /// <summary> Called on every Run of the owning runner while the system is enabled. </summary>
        void Run(World world);
    }

    /// <summary> Destroyed by SystemsRunner.Destroy(), in reverse registration order. </summary>
    public interface IDestroySystem : ISystem {
        /// <summary>
        /// Called once on shutdown by SystemsRunner.Destroy() of an initialized runner, in reverse
        /// registration order. A system that also implements IInitSystem is destroyed once per
        /// completed Init: by Destroy(), or by the rollback of a SystemsRunner.Init that failed in
        /// a system registered after it, so Destroy should undo what Init did. A system without
        /// IInitSystem is never destroyed by a rollback, only by Destroy().
        /// </summary>
        void Destroy(World world);
    }
}
