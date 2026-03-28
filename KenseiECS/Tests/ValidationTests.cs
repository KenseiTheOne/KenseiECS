using System;
using System.Collections.Generic;

namespace KenseiECS.Tests {
    // Test components
    struct Position : IComponent { public float X, Y; }
    struct Velocity : IComponent { public float X, Y; }
    struct Health : IComponent { public float Value; }
    struct Frozen : IComponent { }
    struct Damage : IComponent { public float Value; }

    struct Inventory : IComponent, IAutoReset<Inventory> {
        public List<int> Items;
        public void AutoReset(ref Inventory c) {
            c.Items?.Clear();
            c.Items = null;
        }
    }

    static class ValidationTests {
        private static int _passed;
        private static int _failed;

        public static void RunAll() {
            _passed = 0;
            _failed = 0;

            Console.WriteLine("=== KenseiECS Validation ===\n");

            // Entity lifecycle
            Test_CreateEntity();
            Test_DestroyEntity();
            Test_EntityAliasing();
            Test_DoubleDestroy();
            Test_SlotReuse();
            Test_AutoDestroyOnLastComponentRemoved();

            // Component operations
            Test_AddGetComponent();
            Test_RemoveComponent();
            Test_RefModification();
            Test_MultipleComponents();
            Test_AutoResetOnRemove();
            Test_DefaultResetOnRemove();

            // Filter
            Test_FilterBasic();
            Test_FilterExclude();
            Test_FilterReactive_Add();
            Test_FilterReactive_Remove();
            Test_FilterDuplicateReuse();
            Test_FilterPopulateExisting();

            // Structural changes during iteration
            Test_DestroyDuringIteration();
            Test_AddComponentDuringIteration();
            Test_RemoveComponentDuringIteration();

            // CopyEntity
            Test_CopyEntity();

            // Systems
            Test_SystemRunner();
            Test_NestedSystemRunner();
            Test_NamedSystemEnableDisable();
            Test_OneFrame();
            Test_SharedData();

            // World lifecycle
            Test_WorldClear();
            Test_WorldDestroy();

            // World events
            Test_WorldEvents();

            Console.WriteLine($"\n=== Results: {_passed} passed, {_failed} failed ===");
        }

        // =================================================================
        // Entity lifecycle
        // =================================================================

        private static void Test_CreateEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert("CreateEntity — alive", world.IsAlive(e));
            Assert("CreateEntity — count", world.EntityCount == 1);
        }

        private static void Test_DestroyEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            Assert("DestroyEntity — not alive", !world.IsAlive(e));
            Assert("DestroyEntity — count 0", world.EntityCount == 0);
        }

        private static void Test_EntityAliasing() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            var stale = e1;
            world.DestroyEntity(e1);
            var e2 = world.CreateEntity(new Position());
            Assert("Aliasing — same index", e2.Index == stale.Index);
            Assert("Aliasing — different gen", e2.Generation != stale.Generation);
            Assert("Aliasing — stale is dead", !world.IsAlive(stale));
            Assert("Aliasing — new is alive", world.IsAlive(e2));
        }

        private static void Test_DoubleDestroy() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.DestroyEntity(e);
            world.DestroyEntity(e);
            Assert("DoubleDestroy — count 0", world.EntityCount == 0);
        }

        private static void Test_SlotReuse() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            int idx = e1.Index;
            world.DestroyEntity(e1);
            var e2 = world.CreateEntity(new Position());
            Assert("SlotReuse — same index", e2.Index == idx);
            Assert("SlotReuse — gen incremented", e2.Generation == 2);
        }

        private static void Test_AutoDestroyOnLastComponentRemoved() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Remove<Velocity>(e);
            Assert("AutoDestroy — still alive with 1 comp", world.IsAlive(e));
            world.Remove<Position>(e);
            Assert("AutoDestroy — dead after last remove", !world.IsAlive(e));
            Assert("AutoDestroy — count 0", world.EntityCount == 0);
        }

        // =================================================================
        // Component operations
        // =================================================================

        private static void Test_AddGetComponent() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1, Y = 2 });
            Assert("AddGet — has", world.Has<Position>(e));
            Assert("AddGet — X", world.Get<Position>(e).X == 1f);
            Assert("AddGet — Y", world.Get<Position>(e).Y == 2f);
        }

        private static void Test_RemoveComponent() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Health());
            world.Remove<Position>(e);
            Assert("Remove — not has", !world.Has<Position>(e));
        }

        private static void Test_RefModification() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            ref var pos = ref world.Get<Position>(e);
            pos.X = 42;
            Assert("RefMod — modified in place", world.Get<Position>(e).X == 42f);
        }

        private static void Test_MultipleComponents() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Velocity { Y = 4 });
            world.Add(e, new Health { Value = 100 });
            Assert("Multi — has Position", world.Has<Position>(e));
            Assert("Multi — has Velocity", world.Has<Velocity>(e));
            Assert("Multi — has Health", world.Has<Health>(e));
        }

        private static void Test_AutoResetOnRemove() {
            var world = new World();
            var e = world.CreateEntity(new Inventory { Items = new List<int> { 1, 2, 3 } });
            world.Add(e, new Health()); // keep entity alive after removing Inventory
            world.Remove<Inventory>(e);
            world.Add(e, new Inventory());
            ref var inv = ref world.Get<Inventory>(e);
            Assert("AutoReset — Items is null after reset", inv.Items == null);
        }

        private static void Test_DefaultResetOnRemove() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 99, Y = 88 });
            world.Add(e, new Health()); // keep alive
            world.Remove<Position>(e);
            world.Add(e, new Position());
            ref var pos = ref world.Get<Position>(e);
            Assert("DefaultReset — X is 0", pos.X == 0f);
            Assert("DefaultReset — Y is 0", pos.Y == 0f);
        }

        // =================================================================
        // Filter
        // =================================================================

        private static void Test_FilterBasic() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Velocity());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert("FilterBasic — count 1", filter.Count == 1);
        }

        private static void Test_FilterExclude() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Frozen());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Exc<Frozen>().End();
            Assert("FilterExclude — count 1", filter.Count == 1);
        }

        private static void Test_FilterReactive_Add() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert("FilterReactiveAdd — empty", filter.Count == 0);
            var e = world.CreateEntity(new Position());
            Assert("FilterReactiveAdd — after pos", filter.Count == 0);
            world.Add(e, new Velocity());
            Assert("FilterReactiveAdd — after vel", filter.Count == 1);
        }

        private static void Test_FilterReactive_Remove() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert("FilterReactiveRemove — before", filter.Count == 1);
            world.Remove<Velocity>(e);
            Assert("FilterReactiveRemove — after", filter.Count == 0);
        }

        private static void Test_FilterDuplicateReuse() {
            var world = new World();
            var f1 = world.Filter().Inc<Position>().Inc<Velocity>().End();
            var f2 = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert("FilterDuplicate — same instance", ReferenceEquals(f1, f2));
        }

        private static void Test_FilterPopulateExisting() {
            var world = new World();
            var e1 = world.CreateEntity(new Position());
            world.Add(e1, new Velocity());
            world.CreateEntity(new Position());
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            Assert("FilterPopulate — count 1", filter.Count == 1);
        }

        // =================================================================
        // Structural changes during iteration
        // =================================================================

        private static void Test_DestroyDuringIteration() {
            var world = new World();
            for (int i = 0; i < 10; i++) {
                world.CreateEntity(new Health { Value = i });
            }

            var filter = world.Filter().Inc<Health>().End();
            foreach (int e in filter) {
                ref var hp = ref world.Pool<Health>().Get(e);
                if (hp.Value < 5) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }
            Assert("DestroyIter — after", filter.Count == 5);
        }

        private static void Test_AddComponentDuringIteration() {
            var world = new World();
            for (int i = 0; i < 5; i++) {
                world.CreateEntity(new Position { X = i });
            }

            var posFilter = world.Filter().Inc<Position>().End();
            var bothFilter = world.Filter().Inc<Position>().Inc<Velocity>().End();

            foreach (int e in posFilter) {
                if (!world.Pool<Velocity>().Has(e)) {
                    world.Pool<Velocity>().Add(e, new Velocity { X = 1 });
                }
            }
            Assert("AddDuringIter — all have vel", bothFilter.Count == 5);
        }

        private static void Test_RemoveComponentDuringIteration() {
            var world = new World();
            for (int i = 0; i < 10; i++) {
                var ent = world.CreateEntity(new Position { X = i });
                world.Add(ent, new Health { Value = i });
            }

            var filter = world.Filter().Inc<Position>().Inc<Health>().End();
            foreach (int e in filter) {
                ref var hp = ref world.Pool<Health>().Get(e);
                if (hp.Value < 5) {
                    world.Remove<Health>(world.GetEntity(e));
                }
            }
            Assert("RemoveDuringIter — after", filter.Count == 5);
        }

        // =================================================================
        // CopyEntity
        // =================================================================

        private static void Test_CopyEntity() {
            var world = new World();
            var src = world.CreateEntity(new Position { X = 10, Y = 20 });
            world.Add(src, new Health { Value = 100 });

            var copy = world.CopyEntity(src);

            Assert("Copy — alive", world.IsAlive(copy));
            Assert("Copy — different entity", copy != src);
            Assert("Copy — has Position", world.Has<Position>(copy));
            Assert("Copy — has Health", world.Has<Health>(copy));
            Assert("Copy — Position.X", world.Get<Position>(copy).X == 10f);
            Assert("Copy — Health", world.Get<Health>(copy).Value == 100f);

            ref var srcPos = ref world.Get<Position>(src);
            srcPos.X = 999;
            Assert("Copy — independent", world.Get<Position>(copy).X == 10f);
        }

        // =================================================================
        // Systems
        // =================================================================

        private static void Test_SystemRunner() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1, Y = 2 });

            var systems = new SystemsRunner(world)
                .Add(new TestMovementSystem());
            systems.Init();
            systems.Run();

            Assert("SystemRunner — X moved", world.Get<Position>(e).X == 1f);
            Assert("SystemRunner — Y moved", world.Get<Position>(e).Y == 2f);
            systems.Destroy();
        }

        private static void Test_NestedSystemRunner() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1, Y = 2 });

            var inner = new SystemsRunner(world).Add(new TestMovementSystem());
            var root = new SystemsRunner(world).Add(inner, "update");
            root.Init();

            root.Run();
            Assert("Nested — X moved", world.Get<Position>(e).X == 1f);

            var retrieved = root.GetRunner("update");
            Assert("Nested — GetRunner works", retrieved != null);
        }

        private static void Test_NamedSystemEnableDisable() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity { X = 1 });

            var systems = new SystemsRunner(world)
                .Add(new TestMovementSystem(), "movement");
            systems.Init();

            systems.SetActive("movement", false);
            systems.Run();
            Assert("Disable — not moved", world.Get<Position>(e).X == 0f);

            systems.SetActive("movement", true);
            systems.Run();
            Assert("Enable — moved", world.Get<Position>(e).X == 1f);
        }

        private static void Test_OneFrame() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Damage { Value = 10 });

            var systems = new SystemsRunner(world)
                .OneFrame<Damage>();
            systems.Init();

            Assert("OneFrame — before run", world.Has<Damage>(e));
            systems.Run();
            Assert("OneFrame — after run removed", !world.Has<Damage>(e));
            Assert("OneFrame — entity still alive", world.IsAlive(e));
        }

        private static void Test_SharedData() {
            var world = new World();
            var service = new TestService { Value = 42 };
            var shared = new SharedData();
            shared.Add(service);

            var system = new TestSharedDataSystem();
            var systems = new SystemsRunner(world, shared)
                .Add(system);
            systems.Init();

            Assert("SharedData — service received", system.ReceivedValue == 42);
        }

        // =================================================================
        // World lifecycle
        // =================================================================

        private static void Test_WorldClear() {
            var world = new World();
            world.CreateEntity(new Position());
            var e2 = world.CreateEntity(new Position());
            world.Add(e2, new Velocity());
            Assert("Clear — before", world.EntityCount == 2);

            world.Clear();
            Assert("Clear — after", world.EntityCount == 0);
            Assert("Clear — pool empty", world.Pool<Position>().Count == 0);

            var e = world.CreateEntity(new Position { X = 1 });
            Assert("Clear — reuse works", world.IsAlive(e));
        }

        private static void Test_WorldDestroy() {
            var world = new World();
            world.CreateEntity(new Position());
            world.Destroy();
            Assert("Destroy — completed", true);
        }

        // =================================================================
        // World events
        // =================================================================

        private static void Test_WorldEvents() {
            var world = new World();
            var listener = new TestWorldListener();
            world.AddEventListener(listener);

            var e = world.CreateEntity(new Position());
            Assert("Events — created fired", listener.CreatedCount == 1);
            Assert("Events — comp added fired", listener.AddedCount == 1);

            world.Add(e, new Velocity());
            Assert("Events — comp added 2", listener.AddedCount == 2);

            world.Remove<Velocity>(e);
            Assert("Events — comp removed fired", listener.RemovedCount == 1);

            world.DestroyEntity(e);
            Assert("Events — destroyed fired", listener.DestroyedCount == 1);
        }

        // =================================================================
        // Helpers
        // =================================================================

        private static void Assert(string name, bool condition) {
            if (condition) {
                _passed++;
                Console.WriteLine($"  PASS  {name}");
            } else {
                _failed++;
                Console.WriteLine($"  FAIL  {name}");
            }
        }
    }

    // --- Test systems ---

    class TestMovementSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Position> _positions;
        private ComponentPool<Velocity> _velocities;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            _positions = world.Pool<Position>();
            _velocities = world.Pool<Velocity>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var pos = ref _positions.Get(e);
                ref var vel = ref _velocities.Get(e);
                pos.X += vel.X;
                pos.Y += vel.Y;
            }
        }
    }

    class TestService {
        public int Value;
    }

    class TestSharedDataSystem : IInitSystem {
        public int ReceivedValue;

        public void Init(World world, SharedData shared) {
            var service = shared.Get<TestService>();
            ReceivedValue = service.Value;
        }
    }

    class TestWorldListener : IWorldEventListener {
        public int CreatedCount;
        public int DestroyedCount;
        public int AddedCount;
        public int RemovedCount;

        public void OnEntityCreated(int entityIndex) => CreatedCount++;
        public void OnEntityDestroyed(int entityIndex) => DestroyedCount++;
        public void OnComponentAdded(int entityIndex, int typeIndex) => AddedCount++;
        public void OnComponentRemoved(int entityIndex, int typeIndex) => RemovedCount++;
    }

    static class Program {
        static void Main() => ValidationTests.RunAll();
    }
}
