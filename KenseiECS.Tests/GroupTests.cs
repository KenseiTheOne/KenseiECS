using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class GroupTests {
        private static bool IsAligned(World world, Group<Position, Velocity> group) {
            var entities = group.Entities;
            var pos = world.Pool<Position>();
            var vel = world.Pool<Velocity>();
            for (int i = 0; i < entities.Length; i++) {
                int e = entities[i];
                if (pos.RawEntities[i] != e || vel.RawEntities[i] != e) {
                    return false;
                }
                if (!world.Has<Position>(world.GetEntity(e)) || !world.Has<Velocity>(world.GetEntity(e))) {
                    return false;
                }
            }
            return true;
        }

        [Test]
        public void Group_PopulatesExistingMembers() {
            var world = new World();
            var a = world.CreateEntity(new Position { X = 1 });
            world.Add(a, new Velocity { X = 10 });
            world.CreateEntity(new Position { X = 2 });
            var b = world.CreateEntity(new Velocity { X = 30 });
            world.Add(b, new Position { X = 3 });

            var group = world.Group<Position, Velocity>();

            Assert.That(group.Count, Is.EqualTo(2), "entities with both components must be members");
            Assert.That(IsAligned(world, group), Is.True, "member dense slots must line up across pools");
        }

        [Test]
        public void Group_DataSpans_MatchEntities() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            for (int i = 0; i < 10; i++) {
                var e = world.CreateEntity(new Position { X = i });
                if (i % 2 == 0) {
                    world.Add(e, new Velocity { X = i * 10 });
                }
            }
            var pos = group.Data1;
            var vel = group.Data2;
            Assert.That(pos.Length, Is.EqualTo(5), "Data1 length equals Count");
            for (int i = 0; i < pos.Length; i++) {
                Assert.That(vel[i].X, Is.EqualTo(pos[i].X * 10), "Data1 and Data2 must refer to the same entity at each index");
            }
        }

        [Test]
        public void Group_Iteration_WritesThrough() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var e = world.CreateEntity(new Position { X = 1 });
            world.Add(e, new Velocity { X = 2 });
            var pos = group.Data1;
            var vel = group.Data2;
            for (int i = 0; i < pos.Length; i++) {
                pos[i].X += vel[i].X;
            }
            Assert.That(world.Get<Position>(e).X, Is.EqualTo(3f), "writes through Data spans must reach the component");
        }

        [Test]
        public void Group_RemoveOwnedComponent_DropsMember() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var entities = new Entity[6];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position { X = i });
                world.Add(entities[i], new Velocity());
            }
            world.Remove<Velocity>(entities[2]);
            Assert.That(group.Count, Is.EqualTo(5), "member losing an owned component leaves the group");
            Assert.That(IsAligned(world, group), Is.True, "alignment must hold after removal");
            Assert.That(world.Has<Position>(entities[2]), Is.True, "non-owned membership loss keeps the other component");
        }

        [Test]
        public void Group_DestroyEntity_DropsMember() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var entities = new Entity[6];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position { X = i });
                world.Add(entities[i], new Velocity());
            }
            world.DestroyEntity(entities[0]);
            world.DestroyEntity(entities[5]);
            Assert.That(group.Count, Is.EqualTo(4), "destroyed entities leave the group");
            Assert.That(IsAligned(world, group), Is.True, "alignment must hold after destroy");
        }

        [Test]
        public void Group_NonMembersStayBehindMembers() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            world.CreateEntity(new Position());
            world.CreateEntity(new Position());
            var member = world.CreateEntity(new Position());
            world.Add(member, new Velocity());
            var pos = world.Pool<Position>();
            Assert.That(pos.RawEntities[0], Is.EqualTo(member.Index), "member must be packed at the front of the pool");
            Assert.That(pos.Count, Is.EqualTo(3), "non-members stay in the pool");
        }

        [Test]
        public void Group_ReverseIteration_DestroyCurrent_IsSafe() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            for (int i = 0; i < 8; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity());
            }
            var visited = new List<float>();
            for (int i = group.Count - 1; i >= 0; i--) {
                visited.Add(group.Data1[i].X);
                if ((int)group.Data1[i].X % 2 == 0) {
                    world.DestroyEntity(world.GetEntity(group.Entities[i]));
                }
            }
            Assert.That(visited.Count, Is.EqualTo(8), "reverse iteration visits every member once while destroying");
            Assert.That(group.Count, Is.EqualTo(4), "even members must be destroyed");
            Assert.That(IsAligned(world, group), Is.True, "alignment must hold after mixed destroys");
        }

        [Test]
        public void Group_SameTypes_ReturnsSameInstance() {
            var world = new World();
            var a = world.Group<Position, Velocity>();
            var b = world.Group<Position, Velocity>();
            Assert.That(b, Is.SameAs(a), "group with identical pools must be reused");
        }

        [Test]
        public void Group_PoolOwnedTwice_Throws() {
            var world = new World();
            world.Group<Position, Velocity>();
            Assert.That(() => world.Group<Position, Health>(), Throws.InvalidOperationException, "a pool cannot belong to two groups");
        }

        [Test]
        public void Group_ThreeTypes_Aligned() {
            var world = new World();
            var group = world.Group<Position, Velocity, Health>();
            for (int i = 0; i < 5; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity { X = i });
                if (i != 2) {
                    world.Add(e, new Health { Value = i });
                }
            }
            Assert.That(group.Count, Is.EqualTo(4), "only entities with all three components are members");
            var pos = group.Data1;
            var hp = group.Data3;
            for (int i = 0; i < pos.Length; i++) {
                Assert.That(hp[i].Value, Is.EqualTo(pos[i].X), "all three spans must line up");
            }
        }

        [Test]
        public void Group_FilterAndGroup_Coexist() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var filter = world.Filter().Inc<Position>().Inc<Velocity>().End();
            for (int i = 0; i < 20; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity());
            }
            foreach (int e in filter) {
                if (world.Pool<Position>().Get(e).X < 10) {
                    world.DestroyEntity(world.GetEntity(e));
                }
            }
            Assert.That(group.Count, Is.EqualTo(10), "group must track destroys done through a filter loop");
            Assert.That(filter.Count, Is.EqualTo(10), "filter must agree with the group");
            Assert.That(IsAligned(world, group), Is.True, "alignment must hold");
        }

        [Test]
        public void Group_Clear_ResetsCount() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            var e = world.CreateEntity(new Position());
            world.Add(e, new Velocity());
            world.Clear();
            Assert.That(group.Count, Is.EqualTo(0), "Clear must empty the group");
            var reborn = world.CreateEntity(new Position());
            world.Add(reborn, new Velocity());
            Assert.That(group.Count, Is.EqualTo(1), "group must work after Clear");
        }

        [Test]
        public void Group_ChangeTracking_FollowsSwaps() {
            var world = new World();
            var pos = world.Pool<Position>();
            pos.TrackChanges();
            var group = world.Group<Position, Velocity>();
            var a = world.CreateEntity(new Position());
            var b = world.CreateEntity(new Position());
            int seen = world.ChangeVersion;
            pos.Modify(b.Index);
            world.Add(b, new Velocity());
            Assert.That(pos.ChangedSince(b.Index, seen), Is.True, "version must move with the component when the group swaps it");
            Assert.That(pos.ChangedSince(a.Index, seen), Is.False, "untouched component keeps its version after the swap");
            Assert.That(group.Count, Is.EqualTo(1), "b is the only member");
        }

        [Test]
        public void Group_Warmup_LeavesNoMember() {
            var world = new World();
            var group = world.Group<Position, Velocity>();
            world.Warmup();
            Assert.That(group.Count, Is.EqualTo(0), "the warmup entity must not remain a member");
        }

        [Test]
        public void Group_ManyMembers_Growth() {
            var config = WorldConfig.Default();
            config.InitialPoolDenseCapacity = 2;
            var world = new World(config);
            var group = world.Group<Position, Velocity>();
            for (int i = 0; i < 500; i++) {
                var e = world.CreateEntity(new Position { X = i });
                world.Add(e, new Velocity { X = i });
            }
            Assert.That(group.Count, Is.EqualTo(500), "all entities are members after growth");
            Assert.That(IsAligned(world, group), Is.True, "alignment must hold after growth");
        }
    }
}
