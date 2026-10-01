namespace KenseiECS.Demo {
    /// <summary>
    /// Turns the PlayerInput singleton into the player's velocity.
    /// Shows: singletons (GetSingleton) and a generated Init — the [Inc] filter
    /// and [Pool] fields are filled by the KenseiECS source generator.
    /// </summary>
    public partial class PlayerMoveSystem : IRunSystem {
        [Inc(typeof(Player), typeof(Velocity))] private Filter _player;
        [Pool] private ComponentPool<Player> _players;
        [Pool] private ComponentPool<Velocity> _velocities;

        public void Run(World world) {
            ref var input = ref world.GetSingleton<PlayerInput>();
            foreach (int e in _player) {
                float speed = _players.Get(e).Speed;
                ref var v = ref _velocities.Get(e);
                v.X = input.X * speed;
                v.Y = input.Y * speed;
            }
        }
    }
}
