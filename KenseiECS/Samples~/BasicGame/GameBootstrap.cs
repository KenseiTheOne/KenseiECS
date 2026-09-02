using UnityEngine;

namespace KenseiECS.Samples.BasicGame {
    /// <summary> Arena bounds shared with the systems through SharedData. </summary>
    public sealed class ArenaConfig {
        public Vector2 HalfSize;
    }

    /// <summary>
    /// Update: MovementSystem, BounceLogSystem. FixedUpdate: BounceSystem. LateUpdate: SyncTransformSystem.
    /// </summary>
    public sealed class GameBootstrap : EcsBootstrap {
        [SerializeField] private Vector2 _arenaHalfSize = new(8f, 4.5f);

        protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
            shared.Add(new ArenaConfig { HalfSize = _arenaHalfSize });

            // Each runner removes its OneFrame components at the end of its own Run.
            // BounceSystem raises the event in FixedUpdate and BounceLogSystem reads it
            // in Update, so the cleanup belongs to the update runner.
            update
                .Add(new MovementSystem())
                .Add(new BounceLogSystem())
                .OneFrame<BounceEvent>();

            fixedUpdate.Add(new BounceSystem());

            lateUpdate.Add(new SyncTransformSystem());
        }
    }
}
