namespace KenseiECS.Demo {
    /// <summary>
    /// Advances the run clock in the GameState singleton.
    /// Shows: the smallest possible system — a hand-written IRunSystem with no
    /// Init at all, reading a singleton by ref.
    /// </summary>
    public sealed class ClockSystem : IRunSystem {
        public void Run(World world) {
            ref var state = ref world.GetSingleton<GameState>();
            state.Time += state.Dt;
        }
    }
}
