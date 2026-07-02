#if KENSEI_DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KenseiECS {
    /// <summary>
    /// Profiler event types for entity lifecycle tracking.
    /// </summary>
    public enum ProfileEventType {
        Created,
        Destroyed,
        ComponentAdded,
        ComponentRemoved
    }

    /// <summary>
    /// A single profiler event — captures what happened, when, and where.
    /// </summary>
    public struct ProfileEvent {
        public int Tick;
        public ProfileEventType Type;
        public int EntityIndex;
        public int Generation;
        public string ComponentType;
        public double TimestampMs;
        public string CallStack;
    }

    /// <summary>
    /// Lightweight profiler for KenseiECS.
    /// Tracks entity creation, destruction, and component add/remove
    /// with timestamps and optional call stacks.
    ///
    /// Disabled by default — enable with EcsProfiler.Enable(world).
    /// Has runtime cost — use for debugging only.
    ///
    /// Usage:
    ///   EcsProfiler.Enable(world);
    ///   EcsProfiler.CaptureStacks = true; // opt-in, expensive
    ///   // ... gameplay ...
    ///   var events = EcsProfiler.GetEvents();
    ///   EcsProfiler.Disable();
    /// </summary>
    public static class EcsProfiler {
        private static ProfileEvent[] _ringBuffer = new ProfileEvent[10000];
        private static int _head;
        private static int _count;
        private static readonly Stopwatch _stopwatch = new();
        private static readonly object _lock = new();
        private static World _world;
        private static bool _enabled;

        /// <summary> Maximum number of events to store. Must be positive. Oldest are discarded via ring buffer (O(1)). </summary>
        public static int MaxEvents {
            get => _ringBuffer.Length;
            set {
                if (value <= 0) {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxEvents must be positive");
                }

                if (value == _ringBuffer.Length) {
                    return;
                }

                lock (_lock) {
                    var oldEvents = GetEventsInternal();
                    _ringBuffer = new ProfileEvent[value];
                    _head = 0;
                    _count = 0;

                    int start = Math.Max(0, oldEvents.Count - value);
                    for (int i = start; i < oldEvents.Count; i++) {
                        _ringBuffer[_count++] = oldEvents[i];
                    }
                }
            }
        }

        /// <summary> Whether the profiler is currently recording. </summary>
        public static bool IsEnabled => _enabled;

        /// <summary> Whether to capture call stacks (expensive — default false). </summary>
        public static bool CaptureStacks { get; set; }

        /// <summary>
        /// Start recording events for the given world.
        /// Events from other worlds are ignored.
        /// </summary>
        public static void Enable(World world) {
            if (_enabled) {
                Disable();
            }

            _world = world;
            _enabled = true;
            Clear();
            _stopwatch.Restart();
        }

        /// <summary> Stop recording and unhook from world. </summary>
        public static void Disable() {
            _enabled = false;
            _world = null;
            _stopwatch.Stop();
        }

        /// <summary> Clear all recorded events. </summary>
        public static void Clear() {
            lock (_lock) {
                Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
                _head = 0;
                _count = 0;
            }
        }

        /// <summary> Get all recorded events in chronological order. </summary>
        public static IReadOnlyList<ProfileEvent> GetEvents() {
            lock (_lock) {
                return GetEventsInternal();
            }
        }

        /// <summary> Get events for a specific entity index. </summary>
        public static List<ProfileEvent> GetEntityHistory(int entityIndex) {
            lock (_lock) {
                var result = new List<ProfileEvent>();
                var events = GetEventsInternal();
                for (int i = 0; i < events.Count; i++) {
                    if (events[i].EntityIndex == entityIndex) {
                        result.Add(events[i]);
                    }
                }
                return result;
            }
        }

        /// <summary> Get events filtered by type. </summary>
        public static List<ProfileEvent> GetEventsByType(ProfileEventType type) {
            lock (_lock) {
                var result = new List<ProfileEvent>();
                var events = GetEventsInternal();
                for (int i = 0; i < events.Count; i++) {
                    if (events[i].Type == type) {
                        result.Add(events[i]);
                    }
                }
                return result;
            }
        }

        /// <summary> Get events for a specific tick. </summary>
        public static List<ProfileEvent> GetEventsByTick(int tick) {
            lock (_lock) {
                var result = new List<ProfileEvent>();
                var events = GetEventsInternal();
                for (int i = 0; i < events.Count; i++) {
                    if (events[i].Tick == tick) {
                        result.Add(events[i]);
                    }
                }
                return result;
            }
        }

        // =================================================================
        // Internal — called by World / ComponentPool
        // =================================================================

        internal static void OnEntityCreated(World world, int tick, int entityIndex, int generation) {
            if (!_enabled || world != _world) {
                return;
            }

            Record(new ProfileEvent {
                Tick = tick,
                Type = ProfileEventType.Created,
                EntityIndex = entityIndex,
                Generation = generation,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStacks ? CaptureStack() : null
            });
        }

        internal static void OnEntityDestroyed(World world, int tick, int entityIndex, int generation) {
            if (!_enabled || world != _world) {
                return;
            }

            Record(new ProfileEvent {
                Tick = tick,
                Type = ProfileEventType.Destroyed,
                EntityIndex = entityIndex,
                Generation = generation,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStacks ? CaptureStack() : null
            });
        }

        internal static void OnComponentAdded(World world, int tick, int entityIndex, string componentType) {
            if (!_enabled || world != _world) {
                return;
            }

            Record(new ProfileEvent {
                Tick = tick,
                Type = ProfileEventType.ComponentAdded,
                EntityIndex = entityIndex,
                ComponentType = componentType,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStacks ? CaptureStack() : null
            });
        }

        internal static void OnComponentRemoved(World world, int tick, int entityIndex, string componentType) {
            if (!_enabled || world != _world) {
                return;
            }

            Record(new ProfileEvent {
                Tick = tick,
                Type = ProfileEventType.ComponentRemoved,
                EntityIndex = entityIndex,
                ComponentType = componentType,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStacks ? CaptureStack() : null
            });
        }

        // =================================================================
        // Private
        // =================================================================

        private static void Record(ProfileEvent evt) {
            lock (_lock) {
                if (_count < _ringBuffer.Length) {
                    _ringBuffer[(_head + _count) % _ringBuffer.Length] = evt;
                    _count++;
                } else {
                    _ringBuffer[_head] = evt;
                    _head = (_head + 1) % _ringBuffer.Length;
                }
            }
        }

        private static List<ProfileEvent> GetEventsInternal() {
            var result = new List<ProfileEvent>(_count);
            for (int i = 0; i < _count; i++) {
                result.Add(_ringBuffer[(_head + i) % _ringBuffer.Length]);
            }
            return result;
        }

        private static string CaptureStack() {
            var trace = new StackTrace(3, true);
            return trace.ToString();
        }
    }
}
#endif
