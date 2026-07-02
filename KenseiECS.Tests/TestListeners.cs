namespace KenseiECS.Tests {
    internal class ReentrantDestroyListener : IWorldEventListener {
        public World World;
        public bool WasDeadInsideListener;

        public void OnEntityCreated(int entityIndex) {
        }

        public void OnEntityDestroyed(int entityIndex) {
            var entity = World.GetEntity(entityIndex);
            WasDeadInsideListener = !World.IsAlive(entity);
            World.DestroyEntity(entity);
        }

        public void OnComponentAdded(int entityIndex, int typeIndex) {
        }

        public void OnComponentRemoved(int entityIndex, int typeIndex) {
        }
    }

    internal class ReAddOnRemoveListener : IWorldEventListener {
        public World World;
        public bool Armed;

        public void OnEntityCreated(int entityIndex) {
        }

        public void OnEntityDestroyed(int entityIndex) {
        }

        public void OnComponentAdded(int entityIndex, int typeIndex) {
        }

        public void OnComponentRemoved(int entityIndex, int typeIndex) {
            if (Armed) {
                Armed = false;
                World.Add(World.GetEntity(entityIndex), new Position { X = 1 });
            }
        }
    }

    internal class CountingWorldListener : IWorldEventListener {
        public int CreatedCount;
        public int DestroyedCount;
        public int AddedCount;
        public int RemovedCount;

        public void OnEntityCreated(int entityIndex) =>
            CreatedCount++;

        public void OnEntityDestroyed(int entityIndex) =>
            DestroyedCount++;

        public void OnComponentAdded(int entityIndex, int typeIndex) =>
            AddedCount++;

        public void OnComponentRemoved(int entityIndex, int typeIndex) =>
            RemovedCount++;
    }
}
