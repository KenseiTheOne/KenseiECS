using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
#if KENSEI_DEBUG
using System.Diagnostics;
#endif
#if UNITY_2019_1_OR_NEWER
using Unity.Profiling;
#endif
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace KenseiECS {
    /// <summary>
    /// Manages a group of systems and executes them in declared order.
    /// Systems can implement any combination of IInitSystem, IRunSystem, IDestroySystem.
    ///
    /// Supports nesting — a SystemsRunner can be added to another SystemsRunner.
    /// An inline group (AddGroup(name, runner), or Add(runner) without a name) runs as part
    /// of the parent's Run(). A phase (AddPhase(name, runner); the obsolete Add(runner, name)
    /// does the same) is run separately (FixedUpdate/LateUpdate): the parent Init()/Destroy()
    /// cascade to it, but it is run only via GetRunner(name).Run().
    /// A runner can be registered only once: in one slot of one parent. A second registration,
    /// in the same parent or another one, and adding a runner to itself or to a runner nested in
    /// it throw InvalidOperationException in every build.
    /// Only the root runner's Run() advances the world tick; each runner cleans
    /// its own OneFrame components at the end of its own Run().
    ///
    /// Supports named systems — enable/disable at runtime by name. Names are compared
    /// ordinally (case-sensitive: "sim" and "Sim" are different names). An unknown name throws
    /// under KENSEI_DEBUG and is silently ignored in release (SetActive does nothing, IsActive
    /// returns false, GetRunner returns null), so keep names in constants.
    ///
    /// Lifecycle contract:
    ///   Init runs each IInitSystem once, in registration order. If one throws, Init
    ///   rolls back: the systems whose Init completed before the failure and that implement
    ///   IDestroySystem are destroyed in reverse order (a nested runner that completed its Init
    ///   is rolled back system by system the same way; one that failed has rolled itself back),
    ///   the runner stays uninitialized, and the original exception is rethrown — or, if the
    ///   rollback throws too, one flat AggregateException holds the original exception first,
    ///   then every rollback exception, at any nesting depth, in the order they were thrown.
    ///   Systems without IInitSystem are not destroyed by the rollback. The next Init starts
    ///   over from the first system.
    ///   Run always cleans OneFrame components, even when a system throws.
    ///   Destroy runs IDestroySystem in reverse registration order, is a no-op
    ///   on an uninitialized runner, and makes the runner re-initializable.
    ///
    /// Every run system gets a Unity ProfilerMarker; under KENSEI_DEBUG per-system
    /// timings are also recorded and exposed through GetSystemInfo.
    ///
    /// Usage:
    /// <code>
    ///   var shared = new SharedData();
    ///   shared.Add(new GameConfig());
    ///
    ///   var root = new SystemsRunner(world, shared)
    ///       .Add(new MovementSystem(), "movement")
    ///       .Add(new DamageSystem())
    ///       .AddGroup("combat", new SystemsRunner(world).Add(new HitSystem()))
    ///       .AddPhase("fixed", new SystemsRunner(world).Add(new PhysicsSystem()))
    ///       .OneFrame&lt;DamageEvent&gt;();
    ///
    ///   root.Init();
    ///   // Update:      root.Run();
    ///   // FixedUpdate: root.GetRunner("fixed").Run();
    ///   // on shutdown: root.Destroy();
    /// </code>
    /// </summary>
#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public class SystemsRunner : IInitSystem, IRunSystem, IDestroySystem {
        /// <summary> Read-only view of one registered system, for tooling. </summary>
        public readonly struct SystemInfo {
            /// <summary> The registered system instance. </summary>
            public readonly ISystem System;
            /// <summary> Name given at registration, or the system's type name when none was given. </summary>
            public readonly string Name;
            /// <summary> True when the system is part of this runner's Run pipeline: any IRunSystem except a phase (inline groups, named or not, are runnable). </summary>
            public readonly bool IsRunnable;
            /// <summary> Whether the system is enabled: its Run slot for runnable systems and inline groups (a group's own IsEnabled follows the slot), the phase's own IsEnabled for a phase; false otherwise. </summary>
            public readonly bool IsEnabled;
            /// <summary> Non-null when the system is a nested runner. </summary>
            public readonly SystemsRunner ChildRunner;
            /// <summary> True for a phase (AddPhase, or the obsolete Add(runner, name)), which is driven separately from the parent's Run; false for an inline group, including one added with AddGroup. </summary>
            public readonly bool IsSeparatePhase;
#if KENSEI_DEBUG
            /// <summary> Duration of the system's last Run, in milliseconds (KENSEI_DEBUG only). </summary>
            public readonly double LastRunMs;
            /// <summary> Longest recorded Run of the system, in milliseconds, since creation or ResetTimings (KENSEI_DEBUG only). </summary>
            public readonly double PeakRunMs;
#endif

            internal SystemInfo(Entry entry, bool enabled) {
                System = entry.System;
                Name = entry.Name;
                IsRunnable = entry.RunIndex >= 0;
                IsEnabled = enabled;
                ChildRunner = entry.Child;
                IsSeparatePhase = entry.IsSeparatePhase;
#if KENSEI_DEBUG
                LastRunMs = entry.LastRunMs;
                PeakRunMs = entry.PeakRunMs;
#endif
            }
        }

        internal sealed class Entry {
            public ISystem System;
            public string Name;
            public int RunIndex = -1;
            public SystemsRunner Child;
            public bool IsSeparatePhase;
            // False for names generated by the runner (DelHere), which may repeat.
            public bool HasUniqueName;
#if KENSEI_DEBUG
            public double LastRunMs;
            public double PeakRunMs;
#endif
        }

        private readonly World _world;
        // The SharedData passed to the constructor, or a new empty one. A runner without its own
        // uses this one only while it is a root: once added, Shared reads its parent's.
        private readonly SharedData _shared;
        private readonly bool _hasExplicitShared;

        private readonly List<Entry> _entries = new();

        // Separate lists avoid type-checking every frame in Run()
        private readonly List<IInitSystem> _initSystems = new();
        private readonly List<IRunSystem> _runSystems = new();
        private readonly List<IDestroySystem> _destroySystems = new();

        // Run system enable/disable state — parallel to _runSystems
        private readonly List<bool> _runSystemEnabled = new();

        // Entry per run system — parallel to _runSystems
        private readonly List<Entry> _runEntries = new();

#if UNITY_2019_1_OR_NEWER
        private readonly List<ProfilerMarker> _runMarkers = new();
#endif

        // Named systems — name → index in _runSystems
        private readonly Dictionary<string, int> _namedRunSystems = new();

        // Named nested runners — for retrieval via GetRunner()
        private readonly Dictionary<string, SystemsRunner> _namedRunners = new();

        // OneFrame cleanup — each removes all components of a registered type
        private readonly List<IOneFrameCleanup> _oneFrameCleanups = new();

        // Subset of _oneFrameCleanups registered with cleanWhenDisabled — run when this runner is skipped.
        private readonly List<IOneFrameCleanup> _skippedCleanups = new();

        // Inline nested runners — skipped together with this runner, so their skip cleanup cascades.
        private readonly List<SystemsRunner> _inlineChildren = new();

        // The runner this one is registered in, and its entry there; both null for a root runner.
        // AddCore registers a runner only once, so there is at most one parent and one slot.
        private SystemsRunner _parent;
        private Entry _registration;

        // Whether this runner or an inline group below it has a cleanWhenDisabled type. Without one
        // a skipped runner or slot has nothing to clean, and Run passes it without a lookup, so
        // runners that do not use cleanWhenDisabled keep the 2.1 per-frame cost. Never reset:
        // cleanups cannot be unregistered.
        private bool _hasSkipCleanup;

        // Number of IInitSystems whose Init completed in the current Init call — the ones
        // RollbackInit destroys.
        private int _initProgress;
        private bool _initialized;
        private bool _enabled = true;

        /// <summary> True after Init completed for every IInitSystem. </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Shared data passed to systems in Init: the one this runner was constructed with, otherwise
        /// its parent's (read through the parent at every call, so a runner added to a group before the
        /// group joined its own parent reports the root's), or, for a root runner constructed without
        /// one, the empty SharedData it created.
        /// </summary>
        public SharedData Shared => _hasExplicitShared || _parent == null ? _shared : _parent.Shared;

        /// <summary> World this runner drives. </summary>
        public World World => _world;

        /// <summary>
        /// Whether this runner executes its systems in Run. For a nested runner it is set by the
        /// parent's SetActive(name) or SetActive(index), or by the runner's own SetActive(bool):
        /// for a phase it is the phase's own flag, for an inline group it always equals the parent's
        /// run slot (GetSystemInfo(index).IsEnabled and IsActive(name)). A root runner is enabled
        /// unless SetActive(false) is called on it.
        /// A disabled runner does not run its systems; its OneFrame components are kept until it
        /// runs again, except those registered with OneFrame&lt;T&gt;(cleanWhenDisabled: true).
        /// </summary>
        public bool IsEnabled => _enabled;

        /// <summary> Number of registered systems, including nested runners. </summary>
        public int SystemCount => _entries.Count;

        /// <summary>
        /// Create a runner for the given world.
        /// Without SharedData a new empty one is created; a nested runner created
        /// this way uses its parent's SharedData once added (the root's when nested deeper,
        /// whatever order the runners were added in).
        /// </summary>
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
        /// Optional name for runtime enable/disable (SetActive/IsActive) and for the Profiler.
        /// A nested runner added without a name becomes an unnamed inline group. A nested runner
        /// passed here with a name as an ISystem (not statically typed as SystemsRunner) becomes a
        /// separate phase, like AddPhase; use AddPhase or AddGroup to say which one is meant.
        /// A nested runner constructed without SharedData inherits the parent's. A nested runner
        /// that is already registered (here or in another runner), this runner itself, or a runner
        /// this one is nested in throws InvalidOperationException in every build.
        /// Under KENSEI_DEBUG a name already used in this runner throws.
        /// Returns this for fluent chaining.
        /// </summary>
        public SystemsRunner Add(ISystem system, string name = null) {
            return AddCore(system, name, system is SystemsRunner && name != null, true, nameof(Add));
        }

        /// <summary>
        /// Register a nested runner under a name as a separate phase — same as AddPhase(name, runner).
        /// Kept for compatibility: next to AddGroup(name, runner) it reads like an inline group, but
        /// its systems do not run in this runner's Run, only via GetRunner(name).Run().
        /// With a null name it adds an unnamed inline group, like Add(runner).
        /// </summary>
        [Obsolete("With a name, Add(runner, name) creates a separate phase that this runner's Run does not run; use AddPhase(name, runner) for a phase, AddGroup(name, runner) for a named inline group, or Add(runner) for an unnamed one")]
        public SystemsRunner Add(SystemsRunner runner, string name) {
            return AddCore(runner, name, name != null, true, nameof(Add));
        }

        /// <summary>
        /// Register a nested runner as a separate phase (FixedUpdate/LateUpdate). The phase is
        /// not part of this runner's Run: drive it with GetRunner(name).Run(), which runs its
        /// systems and cleans its OneFrame components without advancing the tick.
        /// Init()/Destroy() cascade to it. SetActive(name)/IsActive(name) toggle and read the
        /// phase's own IsEnabled. The obsolete Add(runner, name) does the same.
        /// A runner can be registered only once: one that is already registered (here or in
        /// another runner), this runner itself, or a runner this one is nested in throws
        /// InvalidOperationException in every build.
        /// Under KENSEI_DEBUG a name already used in this runner throws.
        /// </summary>
        public SystemsRunner AddPhase(string name, SystemsRunner runner) {
            ValidateNestedArgs(name, runner);
            return AddCore(runner, name, true, true, nameof(AddPhase));
        }

        /// <summary>
        /// Register a nested runner as a named inline group: it runs as part of this runner's Run,
        /// at its registration position, like an unnamed Add(runner). The name is used by
        /// SetActive(name)/IsActive(name)/IndexOf(name)/GetRunner(name), by the group's
        /// ProfilerMarker and by SystemInfo.Name. Disabling the group disables its slot here
        /// and the group's IsEnabled together. The slot starts as the group is at registration:
        /// a group switched off with SetActive(false) before AddGroup is added disabled (so is an
        /// unnamed Add(runner)). Do not Run() the group separately — it already runs inside this
        /// runner. A runner can be registered only once: to run the same systems at two points of
        /// the pipeline, register two runners. One that is already registered (here or in another
        /// runner), this runner itself, or a runner this one is nested in throws
        /// InvalidOperationException in every build.
        /// Under KENSEI_DEBUG a name already used in this runner throws.
        /// </summary>
        public SystemsRunner AddGroup(string name, SystemsRunner runner) {
            ValidateNestedArgs(name, runner);
            return AddCore(runner, name, false, true, nameof(AddGroup));
        }

        private static void ValidateNestedArgs(string name, SystemsRunner runner) {
            if (runner == null) {
                throw new ArgumentNullException(nameof(runner));
            }
            if (string.IsNullOrEmpty(name)) {
                throw new ArgumentException("A phase or a named group needs a non-empty name", nameof(name));
            }
        }

        // method is the public method that was called, for error messages; separatePhase is only
        // meaningful for nested runners; uniqueName is false for the runner's own generated names
        // (DelHere), which may legitimately repeat. Every check comes before anything is registered,
        // so a rejected call leaves this runner and the nested one as they were.
        private SystemsRunner AddCore(ISystem system, string name, bool separatePhase, bool uniqueName, string method) {
            if (system == null) {
                throw new ArgumentNullException(nameof(system));
            }
            var childRunner = system as SystemsRunner;
            if (childRunner != null) {
                ValidateNewChild(childRunner, name, method);
            }
#if KENSEI_DEBUG
            if (_initialized) {
                throw new InvalidOperationException(
                    $"SystemsRunner.{CallText(method, system, name)} after Init — the system would never be initialized. Register all systems before calling Init");
            }
            if (name != null && uniqueName) {
                int used = IndexOfExplicitName(name);
                if (used >= 0) {
                    throw new InvalidOperationException(
                        $"SystemsRunner.{CallText(method, system, name)}: the name is already used by {_entries[used].System.GetType().Name} in this runner. Names must be unique within one runner");
                }
            }
            if (childRunner != null) {
                if (childRunner._world != _world) {
                    throw new InvalidOperationException(
                        "Nested SystemsRunner was constructed with a different World than its parent — the child's World would be silently ignored");
                }
                // Systems get the SharedData of the root runner. It is known here once this runner or
                // one it is nested in was constructed with SharedData; until then this runner may still
                // be added to such a runner, and Init checks the explicit ones against what it passes.
                var settled = SettledShared();
                if (settled != null && childRunner.HasExplicitSharedOtherThan(settled)) {
                    throw new InvalidOperationException(
                        "Nested SystemsRunner (or a runner nested in it) was constructed with a different SharedData than its parent — the child's SharedData would be silently ignored");
                }
            }
#endif
            var entry = new Entry {
                System = system,
                Name = name ?? system.GetType().Name,
                HasUniqueName = name != null && uniqueName
            };
            _entries.Add(entry);

            if (childRunner != null) {
                // A child without SharedData of its own reads it through _parent (Shared).
                childRunner._parent = this;
                childRunner._registration = entry;
                entry.Child = childRunner;
                entry.IsSeparatePhase = separatePhase;
                if (name != null) {
                    _namedRunners[name] = childRunner;
                }
                if (!separatePhase) {
                    _inlineChildren.Add(childRunner);
                    if (childRunner._hasSkipCleanup) {
                        MarkSkipCleanup();
                    }
                }
            }

            if (system is IInitSystem init) {
                _initSystems.Add(init);
            }

            // Phases are run via GetRunner(name).Run(), so they are excluded from this runner's Run() pipeline.
            if (system is IRunSystem run && !(childRunner != null && separatePhase)) {
                int idx = _runSystems.Count;
                entry.RunIndex = idx;
                _runSystems.Add(run);
                // An inline group's slot mirrors the group's own flag, so both stay in sync.
                _runSystemEnabled.Add(childRunner == null || childRunner._enabled);
                _runEntries.Add(entry);
#if UNITY_2019_1_OR_NEWER
                _runMarkers.Add(new ProfilerMarker(entry.Name));
#endif

                if (name != null) {
                    _namedRunSystems[name] = idx;
                }
            }

            if (system is IDestroySystem destroy) {
                _destroySystems.Add(destroy);
            }

            return this;
        }

        // A runner is registered once, in one slot of one parent: a second slot would share the
        // runner's IsEnabled with the first, so the slots, IsActive and IsEnabled would disagree,
        // and a runner inside itself would recurse without end in Init and Run. Checked in every
        // build: registration is not a hot path.
        private void ValidateNewChild(SystemsRunner child, string name, string method) {
            if (child._parent != null) {
                throw new InvalidOperationException(
                    $"SystemsRunner.{CallText(method, child, name)}: the runner is already registered, as {DescribeRegistration(child)}. " +
                    "A runner can be registered only once, in one slot of one parent; to run the same systems twice, register two runners");
            }
            for (var runner = this; runner != null; runner = runner._parent) {
                if (runner == child) {
                    throw new InvalidOperationException(runner == this
                        ? $"SystemsRunner.{CallText(method, child, name)}: a runner cannot be added to itself"
                        : $"SystemsRunner.{CallText(method, child, name)}: this runner is nested in the runner being added (it is {DescribeRegistration(this)}), so adding it would make a cycle");
                }
            }
        }

#if KENSEI_DEBUG
        // The SharedData the systems of this runner will get, when it is already known: that of the
        // nearest runner from this one up that was constructed with SharedData, or null when none was
        // (the root of this chain may still be added to another runner).
        private SharedData SettledShared() {
            for (var runner = this; runner != null; runner = runner._parent) {
                if (runner._hasExplicitShared) {
                    return runner._shared;
                }
            }
            return null;
        }

        // Whether this runner or a runner nested in it was constructed with SharedData other than shared.
        private bool HasExplicitSharedOtherThan(SharedData shared) {
            if (_hasExplicitShared && _shared != shared) {
                return true;
            }
            for (int i = 0; i < _entries.Count; i++) {
                var child = _entries[i].Child;
                if (child != null && child.HasExplicitSharedOtherThan(shared)) {
                    return true;
                }
            }
            return false;
        }
#endif

        // Where a registered runner sits, slot by slot up to this runner or the root, for error
        // messages: the group "combat" (index 0) of the phase "fixed" (index 2) of a root runner.
        private string DescribeRegistration(SystemsRunner runner) {
            var text = new StringBuilder();
            for (var r = runner; ; r = r._parent) {
                var entry = r._registration;
                var parent = r._parent;
                if (text.Length > 0) {
                    text.Append(" of ");
                }
                if (entry.IsSeparatePhase) {
                    text.Append("the phase \"").Append(entry.Name).Append('"');
                } else if (entry.HasUniqueName) {
                    text.Append("the group \"").Append(entry.Name).Append('"');
                } else {
                    text.Append("an unnamed group");
                }
                text.Append(" (index ").Append(parent._entries.IndexOf(entry)).Append(')');
                if (parent == this) {
                    return text.Append(" of this runner").ToString();
                }
                if (parent._parent == null) {
                    return text.Append(" of a root runner").ToString();
                }
            }
        }

        // The registration call as written, for error messages: Add(MovementSystem, "move"),
        // AddGroup("sim", SystemsRunner), DelHere<Damage>().
        private static string CallText(string method, ISystem system, string name) {
            if (method == nameof(DelHere)) {
                return name + "()";
            }
            string type = system.GetType().Name;
            if (method == nameof(Add)) {
                return name == null ? $"{method}({type})" : $"{method}({type}, \"{name}\")";
            }
            return $"{method}(\"{name}\", {type})";
        }

        // Position of the system registered under this explicit name, or -1: the names that must
        // be unique within a runner (not type names of unnamed systems, not DelHere names).
        // EcsBootstrap uses it to report its reserved phase names.
        internal int IndexOfExplicitName(string name) {
            for (int i = 0; i < _entries.Count; i++) {
                if (_entries[i].HasUniqueName && _entries[i].Name == name) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Register a one-frame component type.
        /// All components of this type are removed at the end of each Run() call,
        /// after every system. Producers must therefore run before consumers within
        /// the frame; use DelHere for a cleanup point in the middle of the pipeline.
        /// Cleanup belongs to this runner's Run: while the runner is disabled (or its
        /// slot in the parent is), components of this type are kept until it runs again.
        /// Same as OneFrame&lt;T&gt;(cleanWhenDisabled: false).
        /// </summary>
        public SystemsRunner OneFrame<T>() where T : struct, IComponent {
            return OneFrame<T>(false);
        }

        /// <summary>
        /// Register a one-frame component type, choosing what happens while the runner is skipped.
        /// The components are always removed at the end of each Run() of the enabled runner.
        /// With cleanWhenDisabled false they are kept while the runner is skipped and removed by
        /// its next enabled Run. With cleanWhenDisabled true they are also removed in every frame
        /// the runner is skipped: when the parent's Run passes the runner's disabled slot (at the
        /// slot's position in the parent's pipeline; this cascades into inline groups nested in
        /// the skipped runner), and when Run() is called on a disabled phase. A phase nobody
        /// calls Run() on is not cleaned.
        /// </summary>
        public SystemsRunner OneFrame<T>(bool cleanWhenDisabled) where T : struct, IComponent {
            var cleanup = new OneFrameCleanup<T>();
            _oneFrameCleanups.Add(cleanup);
            if (cleanWhenDisabled) {
                _skippedCleanups.Add(cleanup);
                MarkSkipCleanup();
            }
            return this;
        }

        /// <summary>
        /// Remove all components of type T at this point of the pipeline.
        /// Systems registered before DelHere see the components; systems after it do not.
        /// </summary>
        public SystemsRunner DelHere<T>() where T : struct, IComponent {
            return AddCore(new OneFrameCleanup<T>(), "DelHere<" + typeof(T).Name + ">", false, false, nameof(DelHere));
        }

        // =================================================================
        // Introspection
        // =================================================================

        /// <summary> Information about the system registered at the given position. </summary>
        public SystemInfo GetSystemInfo(int index) {
            var entry = _entries[index];
            bool enabled = entry.RunIndex >= 0
                ? _runSystemEnabled[entry.RunIndex]
                : entry.Child != null && entry.Child._enabled;
            return new SystemInfo(entry, enabled);
        }

        /// <summary>
        /// Position of the system in registration order (the index used by GetSystemInfo and
        /// SetActive(int)), or -1 when it is not registered in this runner (nested runners are
        /// not searched). Compares by reference.
        /// </summary>
        public int IndexOf(ISystem system) {
            for (int i = 0; i < _entries.Count; i++) {
                if (ReferenceEquals(_entries[i].System, system)) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Position of the system with the given name, in registration order; -1 when there is none.
        /// Finds any registered system, including one without Run (Init/Destroy only), which
        /// SetActive and IsActive cannot toggle. For a run system, an inline group or a phase, an
        /// explicit registration name is resolved exactly as SetActive(string)/IsActive(string)
        /// resolve it, so SetActive(IndexOf(name), x) and SetActive(name, x) act on the same system.
        /// Only when no system was registered under that name does it fall back to the first
        /// unnamed system whose type name (SystemInfo.Name) matches. Names are case-sensitive
        /// (ordinal). Nested runners are not searched.
        /// </summary>
        public int IndexOf(string name) {
            if (name == null) {
                return -1;
            }
            // Same precedence as SetActive(string): named run slot, then named runner.
            if (_namedRunSystems.TryGetValue(name, out int runIndex)) {
                return IndexOfEntry(_runEntries[runIndex]);
            }
            if (_namedRunners.TryGetValue(name, out var runner)) {
                return IndexOf(runner);
            }
            // Explicitly named systems outside the Run pipeline (Init/Destroy-only); the last
            // registration wins, as it does in the name dictionaries.
            for (int i = _entries.Count - 1; i >= 0; i--) {
                if (_entries[i].HasUniqueName && string.Equals(_entries[i].Name, name, StringComparison.Ordinal)) {
                    return i;
                }
            }
            for (int i = 0; i < _entries.Count; i++) {
                if (string.Equals(_entries[i].Name, name, StringComparison.Ordinal)) {
                    return i;
                }
            }
            return -1;
        }

        private int IndexOfEntry(Entry entry) {
            for (int i = 0; i < _entries.Count; i++) {
                if (ReferenceEquals(_entries[i], entry)) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Enable or disable the system registered at the given position (run systems and nested runners).
        /// For an inline group this sets both its run slot here and the group's IsEnabled;
        /// for a phase it sets the phase's IsEnabled. A system without Run (Init/Destroy only)
        /// cannot be toggled: under KENSEI_DEBUG it throws, in release the call does nothing.
        /// </summary>
        public void SetActive(int index, bool active) {
            var entry = _entries[index];
            if (entry.RunIndex >= 0) {
                SetRunSlot(entry.RunIndex, active);
            } else if (entry.Child != null) {
                entry.Child._enabled = active;
            }
#if KENSEI_DEBUG
            else {
                ThrowNotRunnable(entry.Name, nameof(SetActive));
            }
#endif
        }

        /// <summary>
        /// Enable or disable this runner, from a reference to it (a handle on a group or phase,
        /// without its name or the parent). For an inline group it sets the group's slot in the
        /// parent together with its IsEnabled, exactly like the parent's SetActive(name, active);
        /// for a phase or a root runner it sets the runner's own IsEnabled (a disabled root runs
        /// no systems and only removes its cleanWhenDisabled OneFrame types).
        /// </summary>
        public void SetActive(bool active) {
            _enabled = active;
            // An inline group's slot in its parent follows it; a phase has no run slot.
            if (_registration != null && _registration.RunIndex >= 0) {
                _parent._runSystemEnabled[_registration.RunIndex] = active;
            }
        }

        // An inline group's slot and its own IsEnabled always change together.
        private void SetRunSlot(int runIndex, bool active) {
            _runSystemEnabled[runIndex] = active;
            var child = _runEntries[runIndex].Child;
            if (child != null) {
                child._enabled = active;
            }
        }

#if KENSEI_DEBUG
        /// <summary> Reset the peak timings recorded for every system. </summary>
        public void ResetTimings() {
            for (int i = 0; i < _entries.Count; i++) {
                _entries[i].PeakRunMs = 0;
                _entries[i].LastRunMs = 0;
            }
        }
#endif

        // =================================================================
        // Named systems — enable/disable at runtime
        // =================================================================

        /// <summary>
        /// Enable or disable a named run system, a named inline group or a phase.
        /// Disabled systems and groups are skipped during Run(); a disabled phase's Run() runs no systems.
        /// For a group both its slot here and its IsEnabled change.
        /// The name is compared ordinally (case-sensitive). Under KENSEI_DEBUG an unknown name, or the
        /// name of a system without Run (Init/Destroy only), throws; in release the call does nothing,
        /// silently — a misspelt name is not reported.
        /// </summary>
        public void SetActive(string name, bool active) {
            if (_namedRunSystems.TryGetValue(name, out int idx)) {
                SetRunSlot(idx, active);
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

        /// <summary>
        /// Check if a named run system, inline group or phase is enabled. The name is compared ordinally
        /// (case-sensitive). An unknown name, or the name of a system without Run, returns false in
        /// release and throws under KENSEI_DEBUG.
        /// </summary>
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
        /// Get a named nested SystemsRunner: a phase (to drive it from FixedUpdate/LateUpdate)
        /// or a named inline group (for inspection or SetActive(bool); it already runs inside this
        /// runner's Run). The name is compared ordinally (case-sensitive). Unknown names return null
        /// (throw under KENSEI_DEBUG).
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

        /// <summary>
        /// Call once at startup. Invokes Init() on all IInitSystem in registration order; if one
        /// throws, rolls back as described for Init(World, SharedData).
        /// </summary>
        public void Init() {
            Init(_world, Shared);
        }

        /// <summary>
        /// IInitSystem entry point, used when this runner is nested in another.
        /// Invokes Init() on all IInitSystem in registration order; no-op if already initialized.
        /// If an Init throws, Init rolls back: the systems whose Init completed before the failure
        /// and that implement IDestroySystem are destroyed in reverse order (a nested runner that
        /// completed its Init is rolled back system by system; one that failed has already rolled
        /// itself back). The failed system, the systems after it and systems without IInitSystem
        /// are not destroyed. The runner stays uninitialized and the exception is rethrown as is;
        /// if a rollback Destroy throws too, one AggregateException is thrown instead, with the
        /// original exception first, then the rollback exceptions in the order they were thrown,
        /// including those of nested runners (never an AggregateException inside another).
        /// The next Init starts from the first system.
        /// Under KENSEI_DEBUG throws if world or an explicit SharedData differ from the runner's own.
        /// </summary>
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
            try {
                for (; _initProgress < _initSystems.Count; _initProgress++) {
                    _initSystems[_initProgress].Init(world, shared);
                }
            } catch (Exception error) {
                var rollbackErrors = RollbackInit(world);
                if (rollbackErrors != null) {
                    throw InitFailure(error, rollbackErrors);
                }
                // The original exception, or the flat InitFailure of a nested runner, unchanged.
                throw;
            }
            _initialized = true;
        }

        // Marks, in Exception.Data, the AggregateException a failed Init throws, so that a parent
        // runner merges it into its own instead of nesting it. A string key and a bool value:
        // Mono's Exception.Data (Unity, IL2CPP) accepts only serializable keys and values.
        private const string InitFailureKey = "KenseiECS.SystemsRunner.InitFailure";

        // One flat AggregateException: the original exception, then the rollback errors. When the
        // failed system is a nested runner whose own rollback threw, its InitFailure already holds
        // the original exception and its rollback errors, so they come first, unwrapped. Any other
        // AggregateException (one a system threw) is kept whole as the original exception.
        private static AggregateException InitFailure(Exception error, List<Exception> rollbackErrors) {
            List<Exception> all;
            if (error is AggregateException nested && nested.Data.Contains(InitFailureKey)) {
                all = new List<Exception>(nested.InnerExceptions);
                all.AddRange(rollbackErrors);
            } else {
                rollbackErrors.Insert(0, error);
                all = rollbackErrors;
            }
            var failure = new AggregateException(
                "SystemsRunner.Init failed, and destroying the systems initialized before the failed one threw as well", all);
            failure.Data[InitFailureKey] = true;
            return failure;
        }

        // Undo a partial Init: destroy, in reverse order, the systems whose Init completed in this
        // call and that implement IDestroySystem, so what they subscribed or allocated in Init is
        // released. The failed system and the ones after it never completed Init; a system without
        // IInitSystem initialized nothing, and gets its Destroy from Destroy() after a completed
        // Init. Returns the exceptions thrown by those Destroy calls, or null.
        private List<Exception> RollbackInit(World world) {
            int completed = _initProgress;
            _initProgress = 0;
            _initialized = false;

            List<Exception> errors = null;
            for (int i = completed - 1; i >= 0; i--) {
                if (_initSystems[i] is IDestroySystem destroy) {
                    DestroyCollecting(destroy, world, ref errors);
                }
            }
            return errors;
        }

        // Rollback Destroy of one system whose Init completed. A nested runner is walked system by
        // system rather than through its Destroy, which stops at the first throwing system: it has
        // already marked itself uninitialized by then, so the systems after it would never be
        // destroyed. Its Init completed, so all its IInitSystems did, and the same rule applies.
        private static void DestroyCollecting(IDestroySystem system, World world, ref List<Exception> errors) {
            if (system is SystemsRunner nested) {
                if (!nested._initialized) {
                    return;
                }
                nested._initialized = false;
                nested._initProgress = 0;
                for (int i = nested._initSystems.Count - 1; i >= 0; i--) {
                    if (nested._initSystems[i] is IDestroySystem destroy) {
                        DestroyCollecting(destroy, world, ref errors);
                    }
                }
                return;
            }
            try {
                system.Destroy(world);
            } catch (Exception e) {
                (errors ??= new List<Exception>()).Add(e);
            }
        }

        /// <summary>
        /// Pre-warm the ECS: Init all systems (registers pools and filters),
        /// then exercise World internals (JIT + memory allocation) with World.Warmup.
        /// Existing entities and their data are not touched.
        /// World event listeners do not observe the temporary warmup entity, but typed pool
        /// listeners (IComponentListener) and filter listeners (IFilterListener) do — including
        /// those systems subscribe in Init: they get OnAdded/OnEntityAdded and then
        /// OnRemoved/OnEntityRemoved for it, with default component values.
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
            if (_parent == null) {
                _world.NextTick();
            }
            Run(_world);
        }

        /// <summary>
        /// IRunSystem entry point, used when this runner is nested in another.
        /// Runs all enabled systems and removes this runner's OneFrame components,
        /// without advancing the tick. While the runner is disabled it runs no systems and
        /// only removes the OneFrame types registered with cleanWhenDisabled. The outermost Run on
        /// the world (not one called from inside another runner's Run) then applies the listener
        /// removals queued by <see cref="WorldListenerExtensions.UnsubscribeLater{T}"/>, also when a
        /// system threw.
        /// Under KENSEI_DEBUG throws if world differs from the runner's own or Init has not completed.
        /// </summary>
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
            // The outermost Run applies the listener removals queued by UnsubscribeLater on its way
            // out, after the OneFrame cleanup, also when a system or a cleanup listener threw; then a
            // failure while applying them does not replace that exception.
            world.EnterRun();
            try {
                RunSystems(world);
            } catch {
                world.ExitRunAfterFailure();
                throw;
            }
            world.ExitRun();
        }

        private void RunSystems(World world) {
            if (!_enabled) {
                if (_hasSkipCleanup) {
                    CleanSkipped(world);
                }
                return;
            }

            try {
                for (int i = 0; i < _runSystems.Count; i++) {
                    if (!_runSystemEnabled[i]) {
                        // A skipped inline group still drops its cleanWhenDisabled one-frame types.
                        if (_hasSkipCleanup) {
                            CleanSkippedSlot(i, world);
                        }
                        continue;
                    }
#if KENSEI_DEBUG
                    long start = Stopwatch.GetTimestamp();
#endif
#if UNITY_2019_1_OR_NEWER
                    using (_runMarkers[i].Auto()) {
                        _runSystems[i].Run(world);
                    }
#else
                    _runSystems[i].Run(world);
#endif
#if KENSEI_DEBUG
                    double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    var entry = _runEntries[i];
                    entry.LastRunMs = ms;
                    if (ms > entry.PeakRunMs) {
                        entry.PeakRunMs = ms;
                    }
#endif
                }
            } finally {
                for (int i = 0; i < _oneFrameCleanups.Count; i++) {
                    _oneFrameCleanups[i].Cleanup(world);
                }
            }
        }

        // Out of line: Run only gets here when a cleanWhenDisabled type exists below this runner,
        // and keeping the slot lookup out of the loop keeps the loop as tight as in 2.1.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CleanSkippedSlot(int runIndex, World world) {
            var skipped = _runEntries[runIndex].Child;
            if (skipped != null && skipped._hasSkipCleanup) {
                skipped.CleanSkipped(world);
            }
        }

        // Sets _hasSkipCleanup here and in every runner above this one through inline groups
        // (a phase is not skipped with its parent, so the parent is not told).
        private void MarkSkipCleanup() {
            if (_hasSkipCleanup) {
                return;
            }
            _hasSkipCleanup = true;
            if (_registration != null && !_registration.IsSeparatePhase) {
                _parent.MarkSkipCleanup();
            }
        }

        // Cleanup for a frame in which this runner is skipped: its cleanWhenDisabled one-frame
        // types, then the same for its inline groups, which are skipped with it. Phases are
        // driven separately and are not touched.
        private void CleanSkipped(World world) {
            for (int i = 0; i < _skippedCleanups.Count; i++) {
                _skippedCleanups[i].Cleanup(world);
            }
            for (int i = 0; i < _inlineChildren.Count; i++) {
                if (_inlineChildren[i]._hasSkipCleanup) {
                    _inlineChildren[i].CleanSkipped(world);
                }
            }
        }

        /// <summary>
        /// Call once on shutdown. Invokes Destroy() on all IDestroySystem in reverse registration
        /// order. No-op if Init has not completed: a failed Init has already destroyed the systems
        /// it initialized, and systems without IInitSystem are destroyed only here, after a
        /// completed Init.
        /// </summary>
        public void Destroy() {
            Destroy(_world);
        }

        /// <summary>
        /// IDestroySystem entry point, used when this runner is nested in another.
        /// Invokes Destroy() on all IDestroySystem in reverse registration order, including
        /// systems without IInitSystem. No-op if Init has not completed (after a failed Init too:
        /// its rollback has destroyed what it initialized); afterwards the runner can be
        /// initialized again.
        /// </summary>
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
        private void ThrowUnknownName(string name, string operation) {
            for (int i = 0; i < _entries.Count; i++) {
                if (_entries[i].HasUniqueName && _entries[i].Name == name) {
                    if (operation == nameof(GetRunner)) {
                        throw new InvalidOperationException(
                            $"SystemsRunner.GetRunner(\"{name}\"): the system registered under that name is not a nested runner");
                    }
                    ThrowNotRunnable(name, operation);
                }
            }
            throw new InvalidOperationException(
                $"SystemsRunner.{operation}(\"{name}\"): no run system, group or phase was registered under that name (names are case-sensitive)");
        }

        private static void ThrowNotRunnable(string name, string operation) {
            throw new InvalidOperationException(
                $"SystemsRunner.{operation}(\"{name}\"): the system has no Run (Init/Destroy only), so it cannot be enabled or disabled");
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
