namespace KenseiECS.Demo {
    /// <summary>
    /// The hot loop: position += velocity * dt for everything that moves —
    /// player, enemies, bolts, gems in flight, sparks.
    /// Shows: an owning Group&lt;Position, Velocity&gt;. The group keeps both pools'
    /// dense arrays aligned, so the loop walks two plain spans with no lookups.
    /// </summary>
    public partial class IntegrateSystem : IRunSystem {
        // #region integrate
        [Group] private Group<Position, Velocity> _moving;

        public void Run(World world) {
            float dt = world.GetSingleton<GameState>().Dt;
            var positions = _moving.Data1;
            var velocities = _moving.Data2;
            for (int i = 0; i < positions.Length; i++) {
                ref var p = ref positions[i];
                ref readonly var v = ref velocities[i];
                p.X += v.X * dt;
                p.Y += v.Y * dt;
            }
        }
        // #endregion integrate
    }
}
