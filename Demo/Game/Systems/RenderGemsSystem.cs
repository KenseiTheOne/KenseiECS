namespace KenseiECS.Demo {
    /// <summary>
    /// Render phase: writes XP gems (small/big by value) to the sprite buffer.
    /// Shows: the "render" phase is a separate named SystemsRunner that keeps
    /// running while the "sim" phase is paused for a level-up or game over.
    /// </summary>
    public partial class RenderGemsSystem : IRunSystem {
        [Inc(typeof(XpGem), typeof(Position))] private Filter _gems;
        [Pool] private ComponentPool<XpGem> _values;
        [Pool] private ComponentPool<Position> _positions;
        [Shared] private SpriteBuffer _out;

        public void Run(World world) {
            foreach (int e in _gems) {
                var p = _positions.Get(e);
                bool big = _values.Get(e).Value >= 10f;
                _out.Push(p.X, p.Y, big ? 0.55f : 0.3f, big ? SpriteKind.GemBig : SpriteKind.GemSmall, 0f);
            }
        }
    }
}
