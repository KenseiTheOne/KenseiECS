namespace Docs.Snippets.WorldEvents {
    using System;
    using KenseiECS;

    // #region listener
    class MyListener : IWorldEventListener {
        public void OnEntityCreated(int entityIndex) { }
        public void OnEntityDestroyed(int entityIndex) { }
        public void OnComponentAdded(int entityIndex, int typeIndex) { }
        public void OnComponentRemoved(int entityIndex, int typeIndex) { }
    }
    // #endregion listener

    class Registration {
        void Register(World world) {
            // #region register
            var listener = new MyListener();
            world.AddEventListener(listener);
            world.RemoveEventListener(listener);
            // #endregion register
        }
    }

    // #region type-names
    class ComponentLog : IWorldEventListener {
        public void OnEntityCreated(int entityIndex) { }
        public void OnEntityDestroyed(int entityIndex) { }

        public void OnComponentAdded(int entityIndex, int typeIndex) {
            Console.WriteLine($"+{ComponentType.NameOf(typeIndex)} on {entityIndex}");
        }

        public void OnComponentRemoved(int entityIndex, int typeIndex) {
            Type type = ComponentType.TypeOf(typeIndex);
            Console.WriteLine($"-{type.Name} on {entityIndex}");
        }
    }
    // #endregion type-names
}
