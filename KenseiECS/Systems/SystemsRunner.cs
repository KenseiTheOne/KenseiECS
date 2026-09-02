#if KENSEI_DEBUG
using System;
#endif
using System.Collections.Generic;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Manages a group of systems and executes them in declared order.
    /// Systems can implement any combination of IInitSystem, IRunSystem, IDestroySystem.
    ///
    /// Supports nesting — a SystemsRunner can be added to another SystemsRunner.
    /// An unnamed nested runner is an inline group: it runs as part of the parent's Run().
    /// A named nested runner is a separate phase (FixedUpdate/LateUpdate): the parent
    /// Init()/Destroy() cascade to it, but it is run only via GetRunner(name).Run().
    /// Only the root runner's Run() advances the world tick; each runner cleans
    /// its own OneFrame components at the end of its own Run().
    ///
    /// Supports named systems — enable/disable at runtime by name.
    ///
    /// Lifecycle contract:
    ///   Init runs each IInitSystem once. If one throws, the runner stays
    ///   uninitialized and the next Init resumes with the system that failed.
    ///   Run always cleans OneFrame components, even when a system throws.
    ///   Destroy runs IDestroySystem in reverse registration order, is a no-op
    ///   on an uninitialized runner, and makes the runner re-initializable.
    ///
    /// Usage:
    ///   var shared = new SharedData();
    ///   shared.Add(new GameConfig());
    ///
    ///   var root = new SystemsRunner(world, shared)
    ///       .Add(new MovementSystem(), "movement")
    ///       .Add(new DamageSystem())
    ///       .Add(new SystemsRunner(world).Add(new PhysicsSystem()), "fixed")
    ///       .OneFrame<DamageEvent>();
    ///
    ///   root.Init();
    ///   // Update:      root.Run();
    ///   // FixedUpdate: root.GetRunner("fixed").Run();
    ///   // on shutdown: root.Destroy();
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public class SystemsRunner : IInitSystem, IRunSystem, IDestroySystem {
        private readonly World _world;
        private SharedData _shared;
        private readonly bool _hasExplicitShared;

        // Separate lists avoid type-checking every frame in Run()
        private readonly List<IInitSystem> _initSystems = new();
        private readonly List<IRunSystem> _runSystems = new();
        private readonly List<IDestroySystem> _destroySystems = new();

        // Run system enable/disable state — parallel to _runSystems
        private readonly List<bool> _runSystemEnabled = new();

        // Named systems — name → index in _runSystems
        private readonly Dictionary<string, int> _namedRunSystems = new();

        // Named nested runners — for retrieval via GetRunner()
        private readonly Dictionary<string, SystemsRunner> _namedRunners = new();

        // OneFrame cleanup — each removes all components of a registered type
        private readonly List<IOneFrameCleanup> _oneFrameCleanups = new();

        // Number of IInitSystems whose Init completed — lets a failed Init resume.
        private int _initProgress;
        private bool _initialized;
        private bool _isChild;
        private bool _enabled = true;

        /// <summary> True after Init completed for every IInitSystem. </summary>
        public bool IsInitialized => _initialized;

        /// <summary> Shared data passed to systems in Init. </summary>
        public SharedData Shared => _shared;

        public SystemsRunner(World world, SharedData shared = null) {
            _world = world;
            _hasExplicitShared = shared != null;
            _shared = shared ?? new SharedData();
        }

        // =================================================================
        // Registration
        // =================================================================

        /// <summary>
        /// Register a system. Automatically detects which interfaces it implements.
        /// Optional name for runtime enable/disable.
        /// A nested runner constructed without SharedData inherits the parent's.
        /// Returns this for fluent chaining.
        /// </summary>
        public SystemsRunner Add(ISystem system, string name = null) {
#if KENSEI_DEBUG
            if (_initialized) {
                throw new InvalidOperationException(
                    $"SystemsRunner.Add({system.GetType().Name}) after Init — the system would never be initialized. Register all systems before calling Init");
            }
#endif
            var childRunner = system as SystemsRunner;
            if (childRunner != null) {
                childRunner._isChild = true;
#if KENSEI_DEBUG
                if (childRunner._world != _world) {
                    throw new InvalidOperationException(
                        "Nested SystemsRunner was constructed with a different World than its parent — the child's World would be silently ignored");
                }
                if (childRunner._hasExplicitShared && childRunner._shared != _shared) {
                    throw new InvalidOperationException(
                        "Nested SystemsRunner was constructed with a different SharedData than its parent — the child's SharedData would be silently ignored");
                }
#endif
                if (!childRunner._hasExplicitShared) {
                    childRunner._shared = _shared;
                }
                if (name != null) {
                    _namedRunners[name] = childRunner;
                }
            }

            if (system is IInitSystem init) {
                _initSystems.Add(init);
            }

            // Named child runners are separate phases run via GetRunner(name).Run(),
            // so they are excluded from this runner's Run() pipeline.
            if (system is IRunSystem run && (childRunner == null || name == null)) {
                int idx = _runSystems.Count;
                _runSystems.Add(run);
                _runSystemEnabled.Add(true);

                if (name != null) {
                    _namedRunSystems[name] = idx;
                }
            }

            if (system is IDestroySystem destroy) {
                _destroySystems.Add(destroy);
            }

            return this;
        }

        /// <summary>
        /// Register a one-frame component type.
        /// All components of this type are removed at the end of each Run() call,
        /// after every system. Producers must therefore run before consumers within
        /// the frame; use DelHere for a cleanup point in the middle of the pipeline.
        /// </summary>
        public SystemsRunner OneFrame<T>() where T : struct, IComponent {
            _oneFrameCleanups.Add(new OneFrameCleanup<T>());
            return this;
        }

        /// <summary>
        /// Remove all components of type T at this point of the pipeline.
        /// Systems registered before DelHere see the components; systems after it do not.
        /// </summary>
        public SystemsRunner DelHere<T>() where T : struct, IComponent {
            return Add(new OneFrameCleanup<T>());
        }

        // =================================================================
        // Named systems — enable/disable at runtime
        // =================================================================

        /// <summary>
        /// Enable or disable a named run system or a named nested runner.
        /// Disabled systems are skipped during Run(); a disabled runner's Run() is a no-op.
        /// Under KENSEI_DEBUG an unknown name throws; in release it is ignored.
        /// </summary>
        public void SetActive(string name, bool active) {
            if (_namedRunSystems.TryGetValue(name, out int idx)) {
                _runSystemEnabled[idx] = active;
                return;
            }
            if (_namedRunners.TryGetValue(name, out var runner)) {
                runner._enabled = active;
                return;
            }
#if KENSEI_DEBUG
            ThrowUnknownName(name, nameof(SetActive));
#endif
        }

        /// <summary> Check if a named run system or nested runner is enabled. Unknown names return false (throw under KENSEI_DEBUG). </summary>
        public bool IsActive(string name) {
            if (_namedRunSystems.TryGetValue(name, out int idx)) {
                return _runSystemEnabled[idx];
            }
            if (_namedRunners.TryGetValue(name, out var runner)) {
                return runner._enabled;
            }
#if KENSEI_DEBUG
            ThrowUnknownName(name, nameof(IsActive));
#endif
            return false;
        }

        /// <summary>
        /// Get a named nested SystemsRunner.
        /// Useful for separate Update/FixedUpdate/LateUpdate groups.
        /// Unknown names return null (throw under KENSEI_DEBUG).
        /// </summary>
        public SystemsRunner GetRunner(string name) {
            if (_namedRunners.TryGetValue(name, out var runner)) {
                return runner;
            }
#if KENSEI_DEBUG
            ThrowUnknownName(name, nameof(GetRunner));
#endif
            return null;
        }

        // =================================================================
        // Lifecycle
        // =================================================================

        /// <summary> Call once at startup. Invokes Init() on all IInitSystem. </summary>
        public void Init() {
            Init(_world, _shared);
        }

        public void Init(World world, SharedData shared) {
#if KENSEI_DEBUG
            if (world != _world) {
                throw new InvalidOperationException(
                    "SystemsRunner.Init called with a different World than the runner was constructed with");
            }
            if (_hasExplicitShared && shared != _shared) {
                throw new InvalidOperationException(
                    "SystemsRunner.Init called with a different SharedData than the runner was constructed with");
            }
#endif
            if (_initialized) {
                return;
            }
            for (; _initProgress < _initSystems.Count; _initProgress++) {
                _initSystems[_initProgress].Init(world, shared);
            }
            _initialized = true;
        }

        /// <summary>
        /// Pre-warm the ECS: Init all systems (registers pools and filters),
        /// then exercise World internals (JIT + memory allocation).
        /// Existing entities and their data are not touched.
        /// After warmup, World is ready — Init() has already been called.
        /// </summary>
        public void Warmup() {
            Init();
            _world.Warmup();
        }

        /// <summary>
        /// Call every frame. On the root runner increments the tick counter,
        /// then runs all enabled systems and removes this runner's OneFrame components.
        /// Child runners do not advance the tick — only the root does.
        /// </summary>
        public void Run() {
            if (!_isChild) {
                _world.NextTick();
            }
            Run(_world);
        }

        public void Run(World world) {
#if KENSEI_DEBUG
            if (world != _world) {
                throw new InvalidOperationException(
                    "SystemsRunner.Run called with a different World than the runner was constructed with");
            }
            if (!_initialized) {
                throw new InvalidOperationException(
                    "SystemsRunner.Run before Init — systems have no pools or filters yet. Call Init (or Warmup) first");
            }
#endif
            if (!_enabled) {
                return;
            }

            try {
                for (int i = 0; i < _runSystems.Count; i++) {
                    if (_runSystemEnabled[i]) {
                        _runSystems[i].Run(world);
                    }
                }
            } finally {
                for (int i = 0; i < _oneFrameCleanups.Count; i++) {
                    _oneFrameCleanups[i].Cleanup(world);
                }
            }
        }

        /// <summary> Call once on shutdown. No-op if Init has not completed. </summary>
        public void Destroy() {
            Destroy(_world);
        }

        public void Destroy(World world) {
            if (!_initialized) {
                return;
            }
            _initialized = false;
            _initProgress = 0;

            for (int i = _destroySystems.Count - 1; i >= 0; i--) {
                _destroySystems[i].Destroy(world);
            }
        }

#if KENSEI_DEBUG
        private static void ThrowUnknownName(string name, string operation) {
            throw new InvalidOperationException(
                $"SystemsRunner.{operation}(\"{name}\"): no run system or nested runner was registered under that name");
        }
#endif
    }

    /// <summary> Non-generic interface for one-frame cleanup. </summary>
    internal interface IOneFrameCleanup {
        void Cleanup(World world);
    }

    /// <summary>
    /// Typed one-frame cleanup. Iterates the pool and removes all components.
    /// Uses reverse iteration — safe with swap-remove.
    /// Also usable as a run system for DelHere.
    /// </summary>
    internal sealed class OneFrameCleanup<T> : IOneFrameCleanup, IRunSystem where T : struct, IComponent {
        public void Cleanup(World world) {
            var pool = world.Pool<T>();
            var entities = pool.RawEntities;
            int count = pool.Count;

            for (int i = count - 1; i >= 0; i--) {
                pool.Remove(entities[i]);
            }
        }

        public void Run(World world) {
            Cleanup(world);
        }
    }
}
