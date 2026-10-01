namespace KenseiECS.Demo {
    /// <summary>
    /// Render phase: death sparks, fading out with their Lifetime.
    /// Shows: a filter over a tag component (Particle) plus data components.
    /// </summary>
    public partial class RenderParticlesSystem : IRunSystem {
        [Inc(typeof(Particle), typeof(Position), typeof(Lifetime))] private Filter _sparks;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Lifetime> _lifetimes;
        [Shared] private SpriteBuffer _out;

        public void Run(World world) {
            foreach (int e in _sparks) {
                var p = _positions.Get(e);
                ref var life = ref _lifetimes.Get(e);
                _out.Push(p.X, p.Y, 0.18f, SpriteKind.Spark, life.Left / life.Total);
            }
        }
    }
}
