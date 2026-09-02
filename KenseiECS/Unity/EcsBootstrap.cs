#if UNITY_2018_1_OR_NEWER
using UnityEngine;

namespace KenseiECS {
    /// <summary>
    /// Scene entry point that owns a World, its SharedData and three SystemsRunners
    /// driven by Update, FixedUpdate and LateUpdate. Subclass it, register systems in
    /// Configure and put the subclass on a GameObject; the editor windows discover it
    /// automatically.
    ///
    /// Lifecycle: Awake creates the world and the runners and calls Configure, so World
    /// and Shared are usable from other scripts' Start. Start calls Warmup (or only Init
    /// when "Warmup On Start" is off). OnDestroy destroys the systems, then the world.
    ///
    /// Usage:
    ///   public sealed class GameBootstrap : EcsBootstrap {
    ///       [SerializeField] private GameConfig _config;
    ///
    ///       protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
    ///           shared.Add(_config);
    ///
    ///           update
    ///               .Add(new InputSystem())
    ///               .Add(new MovementSystem(), "movement")
    ///               .OneFrame<DamageEvent>();
    ///
    ///           fixedUpdate.Add(new PhysicsSystem());
    ///           lateUpdate.Add(new SyncTransformSystem());
    ///       }
    ///   }
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class EcsBootstrap : MonoBehaviour, IEcsWorldProvider, IEcsSystemsProvider {
        [SerializeField] private bool _warmupOnStart = true;

        /// <summary> The world this bootstrap owns. Created in Awake. </summary>
        public World World { get; private set; }

        /// <summary> Root runner, driven by Update. FixedUpdateSystems and LateUpdateSystems are its "fixed" and "late" phases. </summary>
        public SystemsRunner Systems { get; private set; }

        /// <summary> Shared data passed to every system's Init. </summary>
        public SharedData Shared { get; private set; }

        /// <summary> Runner driven by FixedUpdate. </summary>
        public SystemsRunner FixedUpdateSystems { get; private set; }

        /// <summary> Runner driven by LateUpdate. </summary>
        public SystemsRunner LateUpdateSystems { get; private set; }

        /// <summary> World configuration. Override to tune initial capacities. </summary>
        protected virtual WorldConfig CreateConfig() =>
            WorldConfig.Default();

        /// <summary> Register systems, one-frame components and shared data. Called once from Awake. </summary>
        protected abstract void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared);

        private void Awake() {
            World = new World(CreateConfig());
            Shared = new SharedData();
            Systems = new SystemsRunner(World, Shared);
            FixedUpdateSystems = new SystemsRunner(World);
            LateUpdateSystems = new SystemsRunner(World);

            Configure(Systems, FixedUpdateSystems, LateUpdateSystems, Shared);

            Systems
                .Add(FixedUpdateSystems, "fixed")
                .Add(LateUpdateSystems, "late");
        }

        private void Start() {
            if (_warmupOnStart) {
                Systems.Warmup();
            } else {
                Systems.Init();
            }
        }

        // Start can fail part-way through Init; without this guard the phases would
        // run systems that never initialized (or throw every frame under KENSEI_DEBUG).
        private void Update() {
            if (Systems.IsInitialized) {
                Systems.Run();
            }
        }

        private void FixedUpdate() {
            if (Systems.IsInitialized) {
                FixedUpdateSystems.Run();
            }
        }

        private void LateUpdate() {
            if (Systems.IsInitialized) {
                LateUpdateSystems.Run();
            }
        }

        private void OnDestroy() {
            Systems.Destroy();
            World.Destroy();
        }
    }
}
#endif
