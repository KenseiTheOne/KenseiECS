using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class SystemIntrospectionTests {
        [Test]
        public void GetSystemInfo_ReportsNameAndEnabled() {
            var world = new World();
            var systems = new SystemsRunner(world).Add(new TestMovementSystem(), "movement").Add(new CountingRunSystem());
            Assert.That(systems.SystemCount, Is.EqualTo(2), "every Add must register an entry");
            var first = systems.GetSystemInfo(0);
            Assert.That(first.Name, Is.EqualTo("movement"), "explicit name must be reported");
            Assert.That(first.IsRunnable && first.IsEnabled, Is.True, "run system must be runnable and enabled");
            Assert.That(systems.GetSystemInfo(1).Name, Is.EqualTo(nameof(CountingRunSystem)), "unnamed system reports its type name");
        }

        [Test]
        public void SetActiveByIndex_DisablesSystem() {
            var world = new World();
            var counting = new CountingRunSystem();
            var systems = new SystemsRunner(world).Add(counting);
            systems.Init();
            systems.SetActive(0, false);
            systems.Run();
            Assert.That(counting.Runs, Is.EqualTo(0), "system disabled by index must not run");
            Assert.That(systems.GetSystemInfo(0).IsEnabled, Is.False, "info must reflect the disabled state");
        }

        [Test]
        public void GetSystemInfo_NestedRunner_ReportsChildAndPhase() {
            var world = new World();
            var fixedRunner = new SystemsRunner(world);
            var inline = new SystemsRunner(world);
            var root = new SystemsRunner(world).Add(fixedRunner, "fixed").Add(inline);
            var phase = root.GetSystemInfo(0);
            Assert.That(phase.ChildRunner, Is.SameAs(fixedRunner), "child runner must be exposed");
            Assert.That(phase.IsSeparatePhase, Is.True, "named child is a separate phase");
            Assert.That(phase.IsRunnable, Is.False, "separate phase is not part of the parent's run pipeline");
            Assert.That(root.GetSystemInfo(1).IsSeparatePhase, Is.False, "unnamed child runs inline");
            Assert.That(root.GetSystemInfo(1).IsRunnable, Is.True, "unnamed child is runnable");
        }

        [Test]
        public void SetActiveByIndex_OnPhase_DisablesChild() {
            var world = new World();
            var counting = new CountingRunSystem();
            var fixedRunner = new SystemsRunner(world).Add(counting);
            var root = new SystemsRunner(world).Add(fixedRunner, "fixed");
            root.Init();
            root.SetActive(0, false);
            fixedRunner.Run();
            Assert.That(counting.Runs, Is.EqualTo(0), "disabled phase must not run");
            Assert.That(fixedRunner.IsEnabled, Is.False, "child reports disabled");
        }

        [Test]
        public void DelHere_HasDescriptiveName() {
            var world = new World();
            var systems = new SystemsRunner(world).DelHere<Damage>();
            Assert.That(systems.GetSystemInfo(0).Name, Is.EqualTo("DelHere<Damage>"), "DelHere entry must be identifiable");
        }

#if KENSEI_DEBUG
        [Test]
        public void Timings_RecordedAfterRun() {
            var world = new World();
            var systems = new SystemsRunner(world).Add(new CountingRunSystem());
            systems.Init();
            systems.Run();
            Assert.That(systems.GetSystemInfo(0).LastRunMs, Is.GreaterThanOrEqualTo(0), "last run time must be recorded");
            Assert.That(systems.GetSystemInfo(0).PeakRunMs, Is.GreaterThanOrEqualTo(systems.GetSystemInfo(0).LastRunMs), "peak must be at least the last run");
            systems.ResetTimings();
            Assert.That(systems.GetSystemInfo(0).PeakRunMs, Is.EqualTo(0), "ResetTimings must clear the peak");
        }
#endif
    }

    [TestFixture]
    public class WorldIntrospectionTests {
        [Test]
        public void GetFilter_ListsRegisteredFilters() {
            var world = new World();
            var a = world.Filter().Inc<Position>().End();
            var b = world.Filter().Inc<Velocity>().Exc<Frozen>().End();
            Assert.That(world.FilterCount, Is.EqualTo(2), "both filters must be registered");
            Assert.That(world.GetFilter(0), Is.SameAs(a), "filters are listed in registration order");
            Assert.That(world.GetFilter(1), Is.SameAs(b), "filters are listed in registration order");
        }

        [Test]
        public void Filter_ExposesConstraintTypes() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Exc<Frozen>().Any<Health>().Any<Damage>().End();
            Assert.That(filter.IncludedTypes.ToArray(), Is.EqualTo(new[] { ComponentType<Position>.Index }), "included types must be exposed");
            Assert.That(filter.ExcludedTypes.ToArray(), Is.EqualTo(new[] { ComponentType<Frozen>.Index }), "excluded types must be exposed");
            Assert.That(filter.AnyTypes.Length, Is.EqualTo(2), "any types must be exposed");
        }

        [Test]
        public void Filter_ToString_NamesConstraints() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().Exc<Frozen>().End();
            Assert.That(filter.ToString(), Is.EqualTo("Filter Inc<Position, Velocity> Exc<Frozen>"), "ToString must describe the filter");
        }

        [Test]
        public void Pool_ReportsCapacitiesAndSize() {
            var world = new World();
            var pool = world.Pool<Position>();
            Assert.That(pool.ComponentSize, Is.EqualTo(8), "Position is two floats");
            Assert.That(pool.SparseCapacity, Is.GreaterThan(0), "sparse capacity must be reported");
            Assert.That(pool.DenseCapacity, Is.GreaterThan(0), "dense capacity must be reported");
            Assert.That(pool.AllocatedBytes, Is.GreaterThan(0), "allocated bytes must be reported");
        }

        [Test]
        public void Pool_ManagedComponent_ReportsZeroSize() {
            var world = new World();
            Assert.That(world.Pool<Inventory>().ComponentSize, Is.EqualTo(0), "components with references cannot be measured");
        }

        [Test]
        public void Filter_AllocatedBytes_GrowsWithPages() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            long before = filter.AllocatedBytes;
            world.CreateEntity(new Position());
            Assert.That(filter.AllocatedBytes, Is.GreaterThan(before), "first entity must allocate a sparse page");
        }
    }

    [TestFixture]
    public class FilterPagedSparseTests {
        [Test]
        public void Filter_TracksEntitiesAcrossPages() {
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 16;
            var world = new World(config);
            var filter = world.Filter().Inc<Position>().End();
            var entities = new Entity[3000];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position());
            }
            Assert.That(filter.Count, Is.EqualTo(3000), "every entity across several sparse pages must be tracked");
            for (int i = 0; i < entities.Length; i += 7) {
                world.DestroyEntity(entities[i]);
            }
            for (int i = 0; i < entities.Length; i++) {
                Assert.That(filter.Contains(entities[i].Index), Is.EqualTo(i % 7 != 0), $"entity {i} membership must be correct across pages");
            }
        }

        [Test]
        public void Filter_Contains_UnallocatedPage_IsFalse() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            Assert.That(filter.Contains(50000), Is.False, "index on a page that was never allocated is not contained");
        }

        [Test]
        public void Filter_Clear_ThenReuse_AcrossPages() {
            var world = new World();
            var filter = world.Filter().Inc<Position>().End();
            for (int i = 0; i < 2500; i++) {
                world.CreateEntity(new Position());
            }
            world.Clear();
            Assert.That(filter.Count, Is.EqualTo(0), "Clear must empty the filter");
            var e = world.CreateEntity(new Position());
            Assert.That(filter.Count, Is.EqualTo(1), "filter must work again after Clear");
            Assert.That(filter.Contains(e.Index), Is.True, "reborn entity must be tracked");
        }
    }

    [TestFixture]
    public class EntityNameTests {
        [Test]
        public void GetName_WithoutName_IsNull() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            Assert.That(world.GetName(e), Is.Null, "unnamed entity has no name");
        }

#if KENSEI_DEBUG
        [Test]
        public void SetName_IsReadable() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.SetName(e, "Player");
            Assert.That(world.GetName(e), Is.EqualTo("Player"), "name must round-trip in debug mode");
        }

        [Test]
        public void SetName_IsClearedOnDestroy() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.SetName(e, "Player");
            world.DestroyEntity(e);
            var reborn = world.CreateEntity(new Position());
            Assert.That(world.GetName(reborn), Is.Null, "name must not leak to the entity reusing the slot");
        }

        [Test]
        public void SetName_Null_RemovesName() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.SetName(e, "Player");
            world.SetName(e, null);
            Assert.That(world.GetName(e), Is.Null, "null clears the name");
        }
#else
        [Test]
        public void SetName_IsNoOpInRelease() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.SetName(e, "Player");
            Assert.That(world.GetName(e), Is.Null, "names are compiled out in release");
        }
#endif
    }
}
