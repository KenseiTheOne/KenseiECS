using System;
using System.Runtime.CompilerServices;

namespace KenseiECS {
    /// <summary>
    /// Handle to an entity that a CommandBuffer will create on Playback.
    /// Usable as a target for further commands in the same buffer.
    /// </summary>
    public readonly struct PendingEntity {
        internal readonly int Id;

        internal PendingEntity(int id) {
            Id = id;
        }
    }

    /// <summary>
    /// Records structural changes and applies them later with Playback(world), in order.
    /// Use it to defer changes from inside a filter iteration or a nested loop,
    /// where destroying or modifying a not-yet-visited entity is unsafe.
    ///
    /// Commands targeting an Entity that is dead at Playback time are skipped.
    /// Payloads are stored per component type without boxing; after the first
    /// frame the buffer allocates nothing.
    ///
    /// Usage:
    ///   foreach (int e in filter) {
    ///       if (hp.Get(e).Value <= 0) {
    ///           buffer.DestroyEntity(world.GetEntity(e));
    ///       }
    ///   }
    ///   buffer.Playback(world);
    /// </summary>
    public sealed class CommandBuffer {
        private enum Op : byte {
            Create,
            Add,
            Set,
            Remove,
            Destroy
        }

        private struct Command {
            public Op Op;
            public int PendingId;     // -1 when Entity is the target
            public Entity Entity;
            public int TypeIndex;
            public int PayloadIndex;
        }

        private Command[] _commands = new Command[64];
        private int _count;
        private PayloadStore[] _stores = new PayloadStore[64];
        private int _pendingCount;
        private Entity[] _resolved = new Entity[16];

        /// <summary> Number of recorded commands. </summary>
        public int Count => _count;

        /// <summary> Record CreateEntity with one initial component. Returns a handle for further commands. </summary>
        public PendingEntity CreateEntity<T>(T component) where T : struct, IComponent {
            int id = _pendingCount++;
            Push(Op.Create, id, default, ComponentType<T>.Index, Store<T>().Push(component));
            return new PendingEntity(id);
        }

        /// <summary> Record Add. At Playback throws if the entity already has the component, like World.Add. </summary>
        public void Add<T>(Entity entity, T component) where T : struct, IComponent {
            Push(Op.Add, -1, entity, ComponentType<T>.Index, Store<T>().Push(component));
        }

        public void Add<T>(PendingEntity entity, T component) where T : struct, IComponent {
            Push(Op.Add, entity.Id, default, ComponentType<T>.Index, Store<T>().Push(component));
        }

        /// <summary> Record Add-or-overwrite. </summary>
        public void Set<T>(Entity entity, T component) where T : struct, IComponent {
            Push(Op.Set, -1, entity, ComponentType<T>.Index, Store<T>().Push(component));
        }

        public void Set<T>(PendingEntity entity, T component) where T : struct, IComponent {
            Push(Op.Set, entity.Id, default, ComponentType<T>.Index, Store<T>().Push(component));
        }

        /// <summary> Record Remove. No-op at Playback if the component is absent. </summary>
        public void Remove<T>(Entity entity) where T : struct, IComponent {
            Push(Op.Remove, -1, entity, ComponentType<T>.Index, -1);
        }

        public void Remove<T>(PendingEntity entity) where T : struct, IComponent {
            Push(Op.Remove, entity.Id, default, ComponentType<T>.Index, -1);
        }

        /// <summary> Record DestroyEntity. No-op at Playback if the entity is already dead. </summary>
        public void DestroyEntity(Entity entity) {
            Push(Op.Destroy, -1, entity, -1, -1);
        }

        public void DestroyEntity(PendingEntity entity) {
            Push(Op.Destroy, entity.Id, default, -1, -1);
        }

        /// <summary>
        /// Apply all recorded commands to the world in order, then clear the buffer.
        /// If a command throws, the remaining commands are discarded.
        /// </summary>
        public void Playback(World world) {
            if (_resolved.Length < _pendingCount) {
                _resolved = new Entity[Math.Max(_pendingCount, _resolved.Length * 2)];
            }

            try {
                for (int i = 0; i < _count; i++) {
                    ref var cmd = ref _commands[i];

                    Entity target;
                    if (cmd.Op == Op.Create) {
                        target = _stores[cmd.TypeIndex].Create(world, cmd.PayloadIndex);
                        _resolved[cmd.PendingId] = target;
                        continue;
                    }

                    target = cmd.PendingId >= 0 ? _resolved[cmd.PendingId] : cmd.Entity;
                    if (!world.IsAlive(target)) {
                        continue;
                    }

                    switch (cmd.Op) {
                        case Op.Add:
                            _stores[cmd.TypeIndex].Apply(world, target, cmd.PayloadIndex, false);
                            break;
                        case Op.Set:
                            _stores[cmd.TypeIndex].Apply(world, target, cmd.PayloadIndex, true);
                            break;
                        case Op.Remove:
                            world.GetPool(cmd.TypeIndex)?.Remove(target.Index);
                            break;
                        case Op.Destroy:
                            world.DestroyEntity(target);
                            break;
                    }
                }
            } finally {
                Clear();
            }
        }

        /// <summary> Discard all recorded commands. </summary>
        public void Clear() {
            _count = 0;
            _pendingCount = 0;
            for (int i = 0; i < _stores.Length; i++) {
                _stores[i]?.Clear();
            }
        }

        private void Push(Op op, int pendingId, Entity entity, int typeIndex, int payloadIndex) {
            if (_count == _commands.Length) {
                Array.Resize(ref _commands, _commands.Length * 2);
            }
            ref var cmd = ref _commands[_count++];
            cmd.Op = op;
            cmd.PendingId = pendingId;
            cmd.Entity = entity;
            cmd.TypeIndex = typeIndex;
            cmd.PayloadIndex = payloadIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PayloadStore<T> Store<T>() where T : struct, IComponent {
            int typeIdx = ComponentType<T>.Index;
            var stores = _stores;
            if ((uint)typeIdx < (uint)stores.Length && stores[typeIdx] is PayloadStore<T> store) {
                return store;
            }
            return CreateStore<T>(typeIdx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private PayloadStore<T> CreateStore<T>(int typeIdx) where T : struct, IComponent {
            if (typeIdx >= _stores.Length) {
                Array.Resize(ref _stores, Math.Max(_stores.Length * 2, typeIdx + 1));
            }
            var store = new PayloadStore<T>();
            _stores[typeIdx] = store;
            return store;
        }

        private abstract class PayloadStore {
            public abstract Entity Create(World world, int payloadIndex);
            public abstract void Apply(World world, Entity entity, int payloadIndex, bool overwrite);
            public abstract void Clear();
        }

        private sealed class PayloadStore<T> : PayloadStore where T : struct, IComponent {
            private T[] _items = new T[16];
            private int _count;

            public int Push(T value) {
                if (_count == _items.Length) {
                    Array.Resize(ref _items, _items.Length * 2);
                }
                _items[_count] = value;
                return _count++;
            }

            public override Entity Create(World world, int payloadIndex) {
                return world.CreateEntity(_items[payloadIndex]);
            }

            public override void Apply(World world, Entity entity, int payloadIndex, bool overwrite) {
                var pool = world.Pool<T>();
                int idx = entity.Index;
                if (overwrite && pool.Has(idx)) {
                    pool.Get(idx) = _items[payloadIndex];
                } else {
                    pool.Add(idx, _items[payloadIndex]);
                }
            }

            public override void Clear() {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
                    Array.Clear(_items, 0, _count);
                }
                _count = 0;
            }
        }
    }
}
