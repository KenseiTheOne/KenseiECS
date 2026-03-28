#if KENSEI_DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KenseiECS
{
    /// <summary>
    /// Profiler event types for entity lifecycle tracking.
    /// </summary>
    public enum ProfileEventType
    {
        Created,
        Destroyed,
        ComponentAdded,
        ComponentRemoved
    }

    /// <summary>
    /// A single profiler event — captures what happened, when, and where.
    /// </summary>
    public struct ProfileEvent
    {
        public int Tick;              // world tick when event occurred
        public ProfileEventType Type;
        public int EntityIndex;
        public int Generation;
        public string ComponentType;  // null for Create/Destroy
        public double TimestampMs;    // milliseconds since profiler start
        public string CallStack;      // captured stack trace
    }

    /// <summary>
    /// Lightweight profiler for KenseiECS.
    /// Tracks entity creation, destruction, and component add/remove
    /// with timestamps and call stacks.
    /// 
    /// Disabled by default — enable with EcsProfiler.Enable(world).
    /// Has runtime cost — use for debugging only.
    /// 
    /// Usage:
    ///   EcsProfiler.Enable(world);
    ///   // ... gameplay ...
    ///   var events = EcsProfiler.GetEvents();
    ///   var entityHistory = EcsProfiler.GetEntityHistory(entityIndex);
    ///   EcsProfiler.Disable();
    /// </summary>
    public static class EcsProfiler
    {
        static readonly List<ProfileEvent> _events = new();
        static readonly Stopwatch _stopwatch = new();
        static World _world;
        static bool _enabled;

        /// <summary> Maximum number of events to store. Oldest are discarded. </summary>
        public static int MaxEvents { get; set; } = 10000;

        /// <summary> Whether the profiler is currently recording. </summary>
        public static bool IsEnabled => _enabled;

        /// <summary>
        /// Start recording events for the given world.
        /// Hooks into World notifications.
        /// </summary>
        public static void Enable(World world)
        {
            if (_enabled) Disable();

            _world = world;
            _enabled = true;
            _events.Clear();
            _stopwatch.Restart();
        }

        /// <summary> Stop recording and unhook from world. </summary>
        public static void Disable()
        {
            _enabled = false;
            _world = null;
            _stopwatch.Stop();
        }

        /// <summary> Clear all recorded events. </summary>
        public static void Clear()
        {
            _events.Clear();
        }

        /// <summary> Get all recorded events. </summary>
        public static IReadOnlyList<ProfileEvent> GetEvents() => _events;

        /// <summary> Get events for a specific entity index. </summary>
        public static List<ProfileEvent> GetEntityHistory(int entityIndex)
        {
            var result = new List<ProfileEvent>();
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].EntityIndex == entityIndex)
                    result.Add(_events[i]);
            }
            return result;
        }

        /// <summary> Get events filtered by type. </summary>
        public static List<ProfileEvent> GetEventsByType(ProfileEventType type)
        {
            var result = new List<ProfileEvent>();
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].Type == type)
                    result.Add(_events[i]);
            }
            return result;
        }

        /// <summary> Get events for a specific tick. </summary>
        public static List<ProfileEvent> GetEventsByTick(int tick)
        {
            var result = new List<ProfileEvent>();
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].Tick == tick)
                    result.Add(_events[i]);
            }
            return result;
        }

        // =================================================================
        // Internal — called by World
        // =================================================================

        internal static void OnEntityCreated(int tick, int entityIndex, int generation)
        {
            if (!_enabled) return;

            Record(new ProfileEvent
            {
                Tick = tick,
                Type = ProfileEventType.Created,
                EntityIndex = entityIndex,
                Generation = generation,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStack()
            });
        }

        internal static void OnEntityDestroyed(int tick, int entityIndex, int generation)
        {
            if (!_enabled) return;

            Record(new ProfileEvent
            {
                Tick = tick,
                Type = ProfileEventType.Destroyed,
                EntityIndex = entityIndex,
                Generation = generation,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStack()
            });
        }

        internal static void OnComponentAdded(int tick, int entityIndex, string componentType)
        {
            if (!_enabled) return;

            Record(new ProfileEvent
            {
                Tick = tick,
                Type = ProfileEventType.ComponentAdded,
                EntityIndex = entityIndex,
                ComponentType = componentType,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStack()
            });
        }

        internal static void OnComponentRemoved(int tick, int entityIndex, string componentType)
        {
            if (!_enabled) return;

            Record(new ProfileEvent
            {
                Tick = tick,
                Type = ProfileEventType.ComponentRemoved,
                EntityIndex = entityIndex,
                ComponentType = componentType,
                TimestampMs = _stopwatch.Elapsed.TotalMilliseconds,
                CallStack = CaptureStack()
            });
        }

        // =================================================================
        // Private
        // =================================================================

        static void Record(ProfileEvent evt)
        {
            if (_events.Count >= MaxEvents)
                _events.RemoveAt(0);

            _events.Add(evt);
        }

        static string CaptureStack()
        {
            // Skip profiler frames to show caller's stack
            var trace = new StackTrace(3, true);
            return trace.ToString();
        }
    }
}
#endif
