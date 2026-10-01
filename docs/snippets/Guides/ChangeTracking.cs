namespace Docs.Snippets.ChangeTracking {
    using System;
    using KenseiECS;

    struct Position : IComponent { public float X, Y; }
    struct View : IComponent { public int Id; }

    // #region sync
    class SyncTransformSystem : IInitSystem, IRunSystem {
        Filter _views;
        ComponentPool<Position> _positions;
        int _lastSeen;

        public void Init(World world, SharedData shared) {
            _views = world.Filter().Inc<Position>().Inc<View>().End();
            _positions = world.Pool<Position>();
            _positions.TrackChanges();
        }

        public void Run(World world) {
            foreach (int e in _views) {
                if (!_positions.ChangedSince(e, _lastSeen)) {
                    continue;
                }
                SyncTransform(e);
            }
            _lastSeen = world.ChangeVersion;     // bookmark: "everything up to here is handled"
        }

        void SyncTransform(int e) {
            ref var pos = ref _positions.Get(e);
            Console.WriteLine($"entity {e} moved to ({pos.X}, {pos.Y})");
        }
    }
    // #endregion sync

    class Writer {
        ComponentPool<Position> _positions;

        void Write(int e) {
            // #region write
            _positions.Modify(e).X += 1;             // marks changed
            _positions.MarkChanged(e);               // without reading
            // #endregion write
        }
    }
}
