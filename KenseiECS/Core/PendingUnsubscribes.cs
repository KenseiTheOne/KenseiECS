using System;

namespace KenseiECS {
    // Listener removals that WorldListenerExtensions.UnsubscribeLater queued for the end of the
    // outermost SystemsRunner.Run of a world, where no system is iterating a listener list. One queue
    // per listener type and world; it is kept and reused, so it stops allocating at its peak.
    internal interface IPendingUnsubscribes {
        void Apply(World world);
        void Clear();
    }

    internal sealed class PendingUnsubscribes<T> : IPendingUnsubscribes where T : class {
        private Entity[] _entities = new Entity[4];
        private T[] _listeners = new T[4];
        private int _count;

        internal void Add(Entity entity, T listener) {
            if (_count == _entities.Length) {
                // Both allocated before either is replaced, so an OutOfMemoryException keeps them in step.
                var entities = new Entity[_count * 2];
                var listeners = new T[_count * 2];
                Array.Copy(_entities, entities, _count);
                Array.Copy(_listeners, listeners, _count);
                _entities = entities;
                _listeners = listeners;
            }
            _entities[_count] = entity;
            _listeners[_count] = listener;
            _count++;
        }

        // In queue order. An entity destroyed since (its slot may hold a new entity by now) or one
        // without Listeners<T> is skipped. The queue is emptied also when an Equals of a listener throws.
        // The pool is looked up, not created: without one there is nothing to remove from.
        public void Apply(World world) {
            if (_count == 0) {
                return;
            }
            var pool = world.GetPool(ComponentType<Listeners<T>>.Index) as ComponentPool<Listeners<T>>;
            try {
                for (int i = 0; pool != null && i < _count; i++) {
                    var entity = _entities[i];
                    if (world.IsAlive(entity) && pool.Has(entity.Index)) {
                        pool.Get(entity.Index).Values?.Remove(_listeners[i]);
                    }
                }
            } finally {
                Clear();
            }
        }

        // Cleared so the queue does not keep listeners (views) alive.
        public void Clear() {
            Array.Clear(_listeners, 0, _count);
            _count = 0;
        }
    }
}
