using System;
using System.Collections.Generic;

namespace KenseiECS.Tests {
    internal abstract class WorldListenerBase : IWorldEventListener {
        public virtual void OnEntityCreated(int entityIndex) {
        }

        public virtual void OnEntityDestroyed(int entityIndex) {
        }

        public virtual void OnComponentAdded(int entityIndex, int typeIndex) {
        }

        public virtual void OnComponentRemoved(int entityIndex, int typeIndex) {
        }
    }

    internal class ReentrantDestroyListener : WorldListenerBase {
        public World World;
        public bool WasDeadInsideListener;

        public override void OnEntityDestroyed(int entityIndex) {
            var entity = World.GetEntity(entityIndex);
            WasDeadInsideListener = !World.IsAlive(entity);
            World.DestroyEntity(entity);
        }
    }

    internal class ReAddOnRemoveListener : WorldListenerBase {
        public World World;
        public bool Armed;

        public override void OnComponentRemoved(int entityIndex, int typeIndex) {
            if (Armed) {
                Armed = false;
                World.Add(World.GetEntity(entityIndex), new Position { X = 1 });
            }
        }
    }

    internal class CountingWorldListener : WorldListenerBase {
        public int CreatedCount;
        public int DestroyedCount;
        public int AddedCount;
        public int RemovedCount;

        public override void OnEntityCreated(int entityIndex) =>
            CreatedCount++;

        public override void OnEntityDestroyed(int entityIndex) =>
            DestroyedCount++;

        public override void OnComponentAdded(int entityIndex, int typeIndex) =>
            AddedCount++;

        public override void OnComponentRemoved(int entityIndex, int typeIndex) =>
            RemovedCount++;
    }

    internal class ThrowOnDestroyListener : WorldListenerBase {
        public override void OnEntityDestroyed(int entityIndex) =>
            throw new InvalidOperationException("listener failed");
    }

    internal class ThrowOnCreateListener : WorldListenerBase {
        public override void OnEntityCreated(int entityIndex) =>
            throw new InvalidOperationException("listener failed");
    }

    internal class ThrowOnRemoveListener : WorldListenerBase {
        public override void OnComponentRemoved(int entityIndex, int typeIndex) =>
            throw new InvalidOperationException("listener failed");
    }

    internal class CreatedObserverListener : WorldListenerBase {
        public World World;
        public int ComponentCountAtCreate = -1;
        public bool HadPositionAtCreate;
        public List<int> TypesAtCreate = new();

        public override void OnEntityCreated(int entityIndex) {
            var entity = World.GetEntity(entityIndex);
            ComponentCountAtCreate = World.GetComponentCount(entity);
            HadPositionAtCreate = World.Has<Position>(entity);
            World.GetComponentTypes(entity, TypesAtCreate);
        }
    }

    internal class DestroyOnCreateListener : WorldListenerBase {
        public World World;

        public override void OnEntityCreated(int entityIndex) =>
            World.DestroyEntity(World.GetEntity(entityIndex));
    }

    internal class RemoveOtherListener : WorldListenerBase {
        public World World;
        public IWorldEventListener Other;
        public int Calls;

        public override void OnEntityCreated(int entityIndex) {
            Calls++;
            World.RemoveEventListener(Other);
        }
    }

    internal class RemovedObserverListener : WorldListenerBase {
        public World World;
        public bool WasAliveInsideRemoved;
        public int CountInsideRemoved = -1;

        public override void OnComponentRemoved(int entityIndex, int typeIndex) {
            var entity = World.GetEntity(entityIndex);
            WasAliveInsideRemoved = World.IsAlive(entity);
            if (WasAliveInsideRemoved) {
                CountInsideRemoved = World.GetComponentCount(entity);
            }
        }
    }
}
