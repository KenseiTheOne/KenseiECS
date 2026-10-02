using System;

namespace KenseiECS.Demo {
    /// <summary>
    /// Adds up this frame's XpGained events; on level up pauses the run and rolls
    /// the upgrade offers into the GameState singleton.
    /// Shows: event entities — each XpGained lives on its own entity, and the
    /// OneFrame cleanup at the end of the sim phase destroys both event and entity.
    /// </summary>
    public partial class LevelUpSystem : IRunSystem {
        [Inc(typeof(XpGained))] private Filter _gains;
        [Pool] private ComponentPool<XpGained> _amounts;
        [Shared] private GameConfig _config;
        [Shared] private GameRandom _rng;

        public void Run(World world) {
            if (_gains.IsEmpty) {
                return;
            }
            ref var state = ref world.GetSingleton<GameState>();
            // Stress is a benchmark knob: XP is scaled down so a 20x horde does
            // not mean a level-up screen every second.
            float scale = 1f / MathF.Sqrt(MathF.Max(1f, _config.StressMultiplier));
            foreach (int e in _gains) {
                state.Xp += _amounts.Get(e).Amount * scale;
            }
            while (state.Xp >= state.XpToNext) {
                state.Xp -= state.XpToNext;
                state.Level++;
                state.PendingLevels++;
                state.XpToNext = _config.XpForLevel(state.Level);
            }
            if (state.PendingLevels > 0 && state.Status == GameStatus.Playing) {
                Upgrades.OfferNext(world, _config, _rng, ref state);
            }
        }
    }
}
