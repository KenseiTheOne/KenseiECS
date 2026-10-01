namespace KenseiECS.Demo {
    /// <summary>
    /// Counts down Lifetime and destroys expired bolts and sparks.
    /// Shows: destroying the current entity inside foreach without a
    /// CommandBuffer — filters iterate in reverse, so the swap-remove only moves
    /// an already visited entity into the freed slot.
    /// </summary>
    public partial class LifetimeSystem : IRunSystem {
        [Inc(typeof(Lifetime))] private Filter _mortal;
        [Pool] private ComponentPool<Lifetime> _lifetimes;

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;
            foreach (int e in _mortal) {
                ref var life = ref _lifetimes.Get(e);
                life.Left -= dt;
                if (life.Left <= 0f) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }
        }
    }
}
