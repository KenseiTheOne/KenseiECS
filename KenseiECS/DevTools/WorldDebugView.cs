using System.Collections.Generic;
using System.Diagnostics;

namespace KenseiECS
{
    /// <summary>
    /// DebuggerTypeProxy for World.
    /// When you hover over a World instance in IDE (Rider, VS),
    /// it shows a list of alive entities with all their components.
    /// No runtime cost — only invoked by the debugger.
    /// </summary>
    sealed class WorldDebugView
    {
        readonly World _world;

        public WorldDebugView(World world) => _world = world;

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public EntityDebugInfo[] Entities
        {
            get
            {
                var list = new List<EntityDebugInfo>();

                for (int i = 0; i < _world._nextIndex; i++)
                {
                    if (!_world._alive[i]) continue;

                    var entity = new Entity(i, _world._generations[i]);
                    var components = new List<object>();

                    for (int p = 0; p < _world._pools.Length; p++)
                    {
                        var pool = _world._pools[p];
                        if (pool != null && pool.Has(i))
                            components.Add(pool.GetRaw(i));
                    }

                    list.Add(new EntityDebugInfo(entity, components.ToArray()));
                }

                return list.ToArray();
            }
        }
    }

    /// <summary> Entity info for debugger display. </summary>
    [DebuggerDisplay("{Entity} — {Components.Length} components")]
    sealed class EntityDebugInfo
    {
        public readonly Entity Entity;
        public readonly object[] Components;

        public EntityDebugInfo(Entity entity, object[] components)
        {
            Entity = entity;
            Components = components;
        }
    }
}
