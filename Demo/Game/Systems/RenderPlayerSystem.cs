namespace KenseiECS.Demo {
    /// <summary>
    /// Render phase: the player, drawn last; flash is the hit flash.
    /// Shows: GetSingletonEntity — the entity holding the only Player component.
    /// </summary>
    public partial class RenderPlayerSystem : IRunSystem {
        [Shared] private SpriteBuffer _out;
        [Shared] private GameConfig _config;

        public void Run(World world) {
            var e = world.GetSingletonEntity<Player>();
            var p = world.Get<Position>(e);
            _out.Push(p.X, p.Y, _config.PlayerRadius, SpriteKind.Player, world.Get<Player>(e).Flash);
        }
    }
}
