namespace KenseiECS.Demo {
    /// <summary>
    /// Render phase: bolts, blades and nova rings in one pass.
    /// Shows: an [Any] filter — Position plus at least one of Bolt, Blade, NovaRing.
    /// </summary>
    public partial class RenderProjectilesSystem : IRunSystem {
        [Inc(typeof(Position))] [Any(typeof(Bolt), typeof(Blade), typeof(NovaRing))] private Filter _projectiles;
        [Pool] private ComponentPool<Position> _positions;
        [Pool] private ComponentPool<Radius> _radii;
        [Pool] private ComponentPool<Bolt> _bolts;
        [Pool] private ComponentPool<NovaRing> _rings;
        [Shared] private SpriteBuffer _out;

        public void Run(World world) {
            foreach (int e in _projectiles) {
                var p = _positions.Get(e);
                if (_rings.Has(e)) {
                    ref var ring = ref _rings.Get(e);
                    _out.Push(p.X, p.Y, ring.Radius, SpriteKind.Nova, 1f - ring.Radius / ring.MaxRadius);
                } else {
                    _out.Push(p.X, p.Y, _radii.Get(e).Value, _bolts.Has(e) ? SpriteKind.Bolt : SpriteKind.Blade, 0f);
                }
            }
        }
    }
}
