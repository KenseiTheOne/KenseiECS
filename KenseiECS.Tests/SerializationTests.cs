using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class SerializationTests {
        private struct Target : IComponent { public Entity Other; }

        private sealed class InventoryFormatter : IComponentFormatter<Inventory> {
            public void Write(BinaryWriter writer, ref Inventory c) {
                int count = c.Items?.Count ?? 0;
                writer.Write(count);
                for (int i = 0; i < count; i++) {
                    writer.Write(c.Items[i]);
                }
            }

            public void Read(BinaryReader reader, out Inventory c) {
                int count = reader.ReadInt32();
                c = new Inventory { Items = new List<int>(count) };
                for (int i = 0; i < count; i++) {
                    c.Items.Add(reader.ReadInt32());
                }
            }
        }

        private static MemoryStream SaveToStream(World world, WorldSerializer serializer) {
            var stream = new MemoryStream();
            serializer.Save(world, stream);
            stream.Position = 0;
            return stream;
        }

        [Test]
        public void RoundTrip_RestoresComponentData() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 1.5f, Y = -2 });
            world.Add(e, new Health { Value = 42 });
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            serializer.Load(loaded, stream);

            var restored = loaded.GetEntity(e.Index);
            Assert.That(loaded.IsAlive(restored), Is.True, "entity must be alive after load");
            Assert.That(loaded.Get<Position>(restored).X, Is.EqualTo(1.5f), "Position.X must round-trip");
            Assert.That(loaded.Get<Position>(restored).Y, Is.EqualTo(-2f), "Position.Y must round-trip");
            Assert.That(loaded.Get<Health>(restored).Value, Is.EqualTo(42f), "Health must round-trip");
        }

        [Test]
        public void RoundTrip_PreservesIndexAndGeneration() {
            var world = new World();
            var first = world.CreateEntity(new Position());
            world.DestroyEntity(first);
            var reused = world.CreateEntity(new Position());
            world.CreateEntity(new Health());
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            serializer.Load(loaded, stream);

            Assert.That(loaded.IsAlive(reused), Is.True, "handle with generation 2 must be valid after load");
            Assert.That(loaded.IsAlive(first), Is.False, "stale handle must stay stale after load");
            Assert.That(loaded.EntityCount, Is.EqualTo(2), "entity count must match");
        }

        [Test]
        public void RoundTrip_EntityReferencesStayValid() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Target { Other = a });
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            serializer.Load(loaded, stream);

            var target = loaded.Get<Target>(loaded.GetEntity(b.Index)).Other;
            Assert.That(target, Is.EqualTo(a), "Entity field must reference the same handle after load");
            Assert.That(loaded.IsAlive(target), Is.True, "referenced entity must be alive after load");
        }

        [Test]
        public void RoundTrip_ManagedComponent_WithFormatter() {
            var world = new World();
            var e = world.CreateEntity(new Inventory { Items = new List<int> { 3, 4, 5 } });
            var serializer = new WorldSerializer();
            serializer.Register(new InventoryFormatter());
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            serializer.Load(loaded, stream);

            Assert.That(loaded.Get<Inventory>(loaded.GetEntity(e.Index)).Items, Is.EqualTo(new[] { 3, 4, 5 }), "formatter must round-trip the list");
        }

        [Test]
        public void Save_ManagedComponent_WithoutFormatter_Throws() {
            var world = new World();
            world.CreateEntity(new Inventory { Items = new List<int> { 1 } });
            var serializer = new WorldSerializer();
            Assert.That(() => serializer.Save(world, new MemoryStream()), Throws.InvalidOperationException,
                "managed component without a formatter must be rejected");
        }

        [Test]
        public void Load_NonEmptyWorld_Throws() {
            var world = new World();
            world.CreateEntity(new Position());
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);
            Assert.That(() => serializer.Load(world, stream), Throws.InvalidOperationException, "Load into a non-empty world must throw");
        }

        [Test]
        public void Load_GarbageStream_Throws() {
            var serializer = new WorldSerializer();
            var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.That(() => serializer.Load(new World(), stream), Throws.TypeOf<InvalidDataException>(), "wrong magic must be rejected");
        }

        [Test]
        public void Load_PopulatesFiltersAndGroups() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.CreateEntity(new Position());
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            var filter = loaded.Filter().Inc<Position>().Inc<Velocity>().End();
            var group = loaded.Group<Position, Velocity>();
            serializer.Load(loaded, stream);

            Assert.That(filter.Count, Is.EqualTo(1), "filters created before Load must be populated");
            Assert.That(group.Count, Is.EqualTo(1), "groups created before Load must be populated");
        }

        [Test]
        public void Load_FiresNoEvents() {
            var world = new World();
            world.CreateEntity(new Position());
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            var listener = new CountingWorldListener();
            loaded.AddEventListener(listener);
            serializer.Load(loaded, stream);
            Assert.That(listener.CreatedCount + listener.AddedCount, Is.EqualTo(0), "Load must not fire world events");
        }

        [Test]
        public void Load_FreeSlotsAreReused() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var c = world.CreateEntity(new Position());
            world.DestroyEntity(a);
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);

            var loaded = new World();
            serializer.Load(loaded, stream);
            var fresh = loaded.CreateEntity(new Health());
            Assert.That(fresh.Index, Is.EqualTo(a.Index), "the gap left by a destroyed entity must be reused after load");
            Assert.That(loaded.IsAlive(c), Is.True, "existing entities must be untouched by the new one");
        }

        [Test]
        public void RoundTrip_RestoresTick() {
            var world = new World();
            world.CreateEntity(new Position());
            world.NextTick();
            world.NextTick();
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);
            var loaded = new World();
            serializer.Load(loaded, stream);
            Assert.That(loaded.Tick, Is.EqualTo(2), "tick must round-trip");
        }

        [Test]
        public void RoundTrip_ClearThenLoadSameWorld() {
            var world = new World();
            var e = world.CreateEntity(new Position { X = 9 });
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);
            world.Clear();
            serializer.Load(world, stream);
            Assert.That(world.Get<Position>(world.GetEntity(e.Index)).X, Is.EqualTo(9f), "a cleared world must accept its own snapshot");
        }

        [Test]
        public void RoundTrip_ManyEntities_AcrossGrowth() {
            var config = WorldConfig.Default();
            config.InitialEntityCapacity = 4;
            var world = new World(config);
            for (int i = 0; i < 1000; i++) {
                var e = world.CreateEntity(new Position { X = i });
                if (i % 3 == 0) {
                    world.Add(e, new Health { Value = i });
                }
            }
            var serializer = new WorldSerializer();
            var stream = SaveToStream(world, serializer);
            var loaded = new World(config);
            serializer.Load(loaded, stream);
            Assert.That(loaded.EntityCount, Is.EqualTo(1000), "all entities must be restored");
            Assert.That(loaded.Pool<Health>().Count, Is.EqualTo(334), "all Health components must be restored");
        }
    }
}
