namespace Docs.Snippets.Singletons {
    using KenseiECS;

    struct GameState : IComponent { public int Score; }

    class Example {
        void Use(World world) {
            // #region access
            ref var state = ref world.GetSingleton<GameState>();   // throws unless exactly one holder
            Entity holder = world.GetSingletonEntity<GameState>();
            bool exists = world.HasSingleton<GameState>();
            // #endregion access
        }

        void Setup(World world) {
            // #region setup
            world.CreateEntity(new GameState());          // one holder: the singleton exists

            world.GetSingleton<GameState>().Score += 10;  // ref access, writes go straight to the pool
            // #endregion setup
        }
    }
}
