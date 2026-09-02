using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class ChangeTrackingTests {
        [Test]
        public void ChangedSince_WithoutTracking_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            Assert.That(() => world.Pool<Health>().ChangedSince(e.Index, 0), Throws.InvalidOperationException,
                "ChangedSince without TrackChanges must throw");
        }

        [Test]
        public void Add_MarksChanged() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            int seen = world.ChangeVersion;
            var e = world.CreateEntity(new Health());
            Assert.That(pool.ChangedSince(e.Index, seen), Is.True, "a component added after the captured version is changed");
        }

        [Test]
        public void Get_DoesNotMarkChanged() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            pool.Get(e.Index).Value = 5;
            Assert.That(pool.ChangedSince(e.Index, seen), Is.False, "Get must not mark the component changed");
        }

        [Test]
        public void Modify_MarksChanged() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            pool.Modify(e.Index).Value = 5;
            Assert.That(pool.ChangedSince(e.Index, seen), Is.True, "Modify must mark the component changed");
            Assert.That(pool.Get(e.Index).Value, Is.EqualTo(5f), "Modify must return a live ref");
        }

        [Test]
        public void MarkChanged_WithoutRead() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            pool.MarkChanged(e.Index);
            Assert.That(pool.ChangedSince(e.Index, seen), Is.True, "MarkChanged must bump the version");
        }

        [Test]
        public void ChangedSince_IsExactAcrossSystemOrder() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health());

            int consumerSeen = world.ChangeVersion;
            pool.Modify(e.Index).Value = 1;
            Assert.That(pool.ChangedSince(e.Index, consumerSeen), Is.True, "change after the consumer's snapshot is visible");
            consumerSeen = world.ChangeVersion;
            Assert.That(pool.ChangedSince(e.Index, consumerSeen), Is.False, "nothing changed after the new snapshot");
        }

        [Test]
        public void TrackChanges_ExistingComponentsCountAsChanged() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            Assert.That(pool.ChangedSince(e.Index, seen), Is.True, "components present when tracking starts are changed once");
        }

        [Test]
        public void SwapRemove_KeepsVersionOfMovedComponent() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var removed = world.CreateEntity(new Health());
            world.Add(removed, new Position());
            var survivor = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            world.Remove<Health>(removed);
            Assert.That(pool.ChangedSince(survivor.Index, seen), Is.False, "moving a component in the dense array must not mark it changed");
            pool.Modify(survivor.Index);
            Assert.That(pool.ChangedSince(survivor.Index, seen), Is.True, "moved component must keep tracking correctly");
        }

        [Test]
        public void Tracking_SurvivesGrowth() {
            var config = WorldConfig.Default();
            config.InitialPoolDenseCapacity = 2;
            var world = new World(config);
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var entities = new Entity[20];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Health());
            }
            int seen = world.ChangeVersion;
            pool.Modify(entities[17].Index);
            Assert.That(pool.ChangedSince(entities[17].Index, seen), Is.True, "versions array must grow with the dense array");
            Assert.That(pool.ChangedSince(entities[3].Index, seen), Is.False, "untouched components stay unchanged after growth");
        }

        [Test]
        public void Tracking_Idempotent() {
            var world = new World();
            var pool = world.Pool<Health>();
            pool.TrackChanges();
            var e = world.CreateEntity(new Health());
            int seen = world.ChangeVersion;
            pool.TrackChanges();
            Assert.That(pool.ChangedSince(e.Index, seen), Is.False, "second TrackChanges must not reset versions");
        }
    }
}
