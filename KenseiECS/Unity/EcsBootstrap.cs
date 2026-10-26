#if UNITY_2018_1_OR_NEWER
using UnityEngine;

namespace KenseiECS {
    /// <summary>
    /// Scene entry point that owns a World, its SharedData and three SystemsRunners
    /// driven by Update, FixedUpdate and LateUpdate. Subclass it, register systems in
    /// Configure and put the subclass on a GameObject; the editor windows discover it
    /// automatically.
    ///
    /// Lifecycle: Awake creates the world and the runners, calls Configure, then registers
    /// FixedUpdateSystems and LateUpdateSystems in Systems as the phases "fixed" and "late",
    /// so World and Shared are usable from other scripts' Start. Start calls Warmup (or only
    /// Init when "Warmup On Start" is off). Update, FixedUpdate and LateUpdate each run their
    /// runner only while it is initialized. OnDestroy destroys the systems, then the world.
    ///
    /// The names "fixed" and "late" are reserved in the update runner: see Configure.
    ///
    /// Usage:
    /// <code>
    ///   public sealed class GameBootstrap : EcsBootstrap {
    ///       [SerializeField] private GameConfig _config;
    ///
    ///       protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
    ///           shared.Add(_config);
    ///
    ///           update
    ///               .Add(new InputSystem())
    ///               .Add(new MovementSystem(), "movement")
    ///               .OneFrame&lt;DamageEvent&gt;();
    ///
    ///           fixedUpdate.Add(new PhysicsSystem());
    ///           lateUpdate.Add(new SyncTransformSystem());
    ///       }
    ///   }
    /// </code>
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class EcsBootstrap : MonoBehaviour, IEcsWorldProvider, IEcsSystemsProvider {
        // The names of the FixedUpdateSystems and LateUpdateSystems phases in Systems. Code finds
        // the phases by them (GetRunner, SetActive, IsActive), so they never change.
        private const string FixedPhase = "fixed";
        private const string LatePhase = "late";

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

        /// <summary>
        /// Register systems, one-frame components and shared data. Called once from Awake.
        /// The names "fixed" and "late" are reserved in update: after Configure, Awake registers
        /// fixedUpdate and lateUpdate in update under them as phases. Do not register a system,
        /// group or phase in update under either name: under KENSEI_DEBUG Awake then throws
        /// InvalidOperationException naming that system, and the phases are not registered (so
        /// they never run); in release nothing checks it, and the name refers to both
        /// registrations, so SetActive, IsActive and IndexOf with it may reach your system
        /// instead of the phase. Runners nested in update have names of their own and may use
        /// these. Do not register fixedUpdate or lateUpdate in another runner here: a runner can
        /// be registered only once, so registering it as a phase would then throw.
        /// </summary>
        protected abstract void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared);

        private void Awake() {
            World = new World(CreateConfig());
            Shared = new SharedData();
            Systems = new SystemsRunner(World, Shared);
            FixedUpdateSystems = new SystemsRunner(World);
            LateUpdateSystems = new SystemsRunner(World);

            Configure(Systems, FixedUpdateSystems, LateUpdateSystems, Shared);

            // The phases come after Configure, so they are initialized after the update systems
            // and destroyed before them.
#if KENSEI_DEBUG
            ThrowIfPhaseNameTaken(FixedPhase, nameof(FixedUpdateSystems));
            ThrowIfPhaseNameTaken(LatePhase, nameof(LateUpdateSystems));
#endif
            Systems
                .AddPhase(FixedPhase, FixedUpdateSystems)
                .AddPhase(LatePhase, LateUpdateSystems);
        }

#if KENSEI_DEBUG
        // AddPhase throws for a used name too, but its message cannot say that the name belongs to
        // EcsBootstrap, which registers the phase after Configure.
        private void ThrowIfPhaseNameTaken(string phase, string runner) {
            int index = Systems.IndexOfExplicitName(phase);
            if (index < 0) {
                return;
            }
            var info = Systems.GetSystemInfo(index);
            string taken = info.ChildRunner == null ? info.System.GetType().Name
                : info.IsSeparatePhase ? "a phase" : "a group";
            throw new System.InvalidOperationException(
                $"EcsBootstrap: Configure registered {taken} in the update runner under the name \"{phase}\" (index {index}). " +
                $"EcsBootstrap reserves \"{FixedPhase}\" and \"{LatePhase}\" there: after Configure it registers {runner} under \"{phase}\" " +
                "as a phase, and GetRunner, SetActive and IsActive find the phase by that name. Register the system under another name");
        }
#endif

        private void Start() {
            if (_warmupOnStart) {
                Systems.Warmup();
            } else {
                Systems.Init();
            }
        }

        // Each runner runs only while it is initialized. Start can fail part-way through Init
        // (the rollback leaves every runner uninitialized), and when Awake failed before the
        // phases were registered, Init never reaches them: without these guards they would run
        // systems that never initialized (or throw every frame under KENSEI_DEBUG), and a phase
        // that is not registered would advance the world tick on every call, like a root runner.
        private void Update() {
            if (Systems.IsInitialized) {
                Systems.Run();
            }
        }

        private void FixedUpdate() {
            if (FixedUpdateSystems.IsInitialized) {
                FixedUpdateSystems.Run();
            }
        }

        private void LateUpdate() {
            if (LateUpdateSystems.IsInitialized) {
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
