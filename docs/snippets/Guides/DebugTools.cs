using System;
using KenseiECS;

namespace Docs.Snippets.DebugTools {
    public static class Usage {
        public static void Profile(World world) {
            // #region profiler
            #if KENSEI_DEBUG
            EcsProfiler.Enable(world);           // record this world only; restarts if already on
            EcsProfiler.CaptureStacks = true;    // opt-in, expensive
            #endif
            // #endregion profiler
        }

        public static void Query(World world, Entity entity) {
            // #region query
            #if KENSEI_DEBUG
            EcsProfiler.MaxEvents = 50_000;      // ring buffer size, 10 000 by default

            // ... gameplay ...

            var all = EcsProfiler.GetEvents();                              // chronological
            var history = EcsProfiler.GetEntityHistory(entity.Index);
            var destroyed = EcsProfiler.GetEventsByType(ProfileEventType.Destroyed);
            var thisTick = EcsProfiler.GetEventsByTick(world.Tick);

            foreach (var evt in history) {
                Console.WriteLine($"tick {evt.Tick} {evt.TimestampMs:F1} ms: {evt.Type} {evt.ComponentType}");
            }

            EcsProfiler.Clear();
            EcsProfiler.Disable();
            #endif
            // #endregion query
        }
    }
}
