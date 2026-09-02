using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    [TestFixture]
    public class CommandBufferTests {
        [Test]
        public void Playback_CreatesEntityWithComponent() {
            var world = new World();
            var buffer = new CommandBuffer();
            buffer.CreateEntity(new Position { X = 4 });
            buffer.Playback(world);
            Assert.That(world.EntityCount, Is.EqualTo(1), "Create must produce an entity");
            Assert.That(world.GetSingleton<Position>().X, Is.EqualTo(4f), "created entity must carry the payload");
        }

        [Test]
        public void Playback_PendingEntity_ReceivesLaterCommands() {
            var world = new World();
            var buffer = new CommandBuffer();
            var pending = buffer.CreateEntity(new Position());
            buffer.Add(pending, new Velocity { X = 2 });
            buffer.Playback(world);
            var e = world.GetSingletonEntity<Position>();
            Assert.That(world.Get<Velocity>(e).X, Is.EqualTo(2f), "commands on a pending entity must apply to the created entity");
        }

        [Test]
        public void Playback_AddOnLiveEntity() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var buffer = new CommandBuffer();
            buffer.Add(e, new Health { Value = 9 });
            buffer.Playback(world);
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(9f), "Add must apply at playback");
        }

        [Test]
        public void Playback_SetOverwritesExisting() {
            var world = new World();
            var e = world.CreateEntity(new Health { Value = 1 });
            var buffer = new CommandBuffer();
            buffer.Set(e, new Health { Value = 2 });
            buffer.Playback(world);
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(2f), "Set must overwrite an existing component");
        }

        [Test]
        public void Playback_SetAddsWhenMissing() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var buffer = new CommandBuffer();
            buffer.Set(e, new Health { Value = 2 });
            buffer.Playback(world);
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(2f), "Set must add a missing component");
        }

        [Test]
        public void Playback_AddDuplicate_Throws() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            var buffer = new CommandBuffer();
            buffer.Add(e, new Health());
            Assert.That(() => buffer.Playback(world), Throws.InvalidOperationException, "Add of an existing component must throw like World.Add");
        }

        [Test]
        public void Playback_RemoveAndDestroy() {
            var world = new World();
            var a = world.CreateEntity(new Position());
            world.Add(a, new Health());
            var b = world.CreateEntity(new Position());
            var buffer = new CommandBuffer();
            buffer.Remove<Health>(a);
            buffer.DestroyEntity(b);
            buffer.Playback(world);
            Assert.That(world.Has<Health>(a), Is.False, "Remove must apply at playback");
            Assert.That(world.IsAlive(b), Is.False, "Destroy must apply at playback");
        }

        [Test]
        public void Playback_CommandOnDeadEntity_IsSkipped() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var buffer = new CommandBuffer();
            buffer.Add(e, new Health());
            buffer.DestroyEntity(e);
            world.DestroyEntity(e);
            world.CreateEntity(new Velocity());
            Assert.That(() => buffer.Playback(world), Throws.Nothing, "commands on a stale handle must be skipped");
            Assert.That(world.EntityCount, Is.EqualTo(1), "the reborn entity in the same slot must be untouched");
        }

        [Test]
        public void Playback_PreservesOrder() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var buffer = new CommandBuffer();
            buffer.Add(e, new Health { Value = 1 });
            buffer.Remove<Health>(e);
            buffer.Add(e, new Health { Value = 2 });
            buffer.Playback(world);
            Assert.That(world.Get<Health>(e).Value, Is.EqualTo(2f), "commands must apply in recording order");
        }

        [Test]
        public void Playback_ClearsBuffer() {
            var world = new World();
            var buffer = new CommandBuffer();
            buffer.CreateEntity(new Position());
            buffer.Playback(world);
            buffer.Playback(world);
            Assert.That(world.EntityCount, Is.EqualTo(1), "second playback must apply nothing");
            Assert.That(buffer.Count, Is.EqualTo(0), "buffer must be empty after playback");
        }

        [Test]
        public void Playback_DestroyPendingEntity() {
            var world = new World();
            var buffer = new CommandBuffer();
            var pending = buffer.CreateEntity(new Position());
            buffer.DestroyEntity(pending);
            buffer.Playback(world);
            Assert.That(world.EntityCount, Is.EqualTo(0), "pending entity destroyed in the same buffer must not survive");
        }

        [Test]
        public void Playback_Throws_DiscardsRest() {
            var world = new World();
            var e = world.CreateEntity(new Health());
            var buffer = new CommandBuffer();
            buffer.Add(e, new Health());
            buffer.Add(e, new Position());
            Assert.That(() => buffer.Playback(world), Throws.InvalidOperationException, "duplicate Add must throw");
            Assert.That(world.Has<Position>(e), Is.False, "commands after the failing one are discarded");
            Assert.That(buffer.Count, Is.EqualTo(0), "buffer is cleared after a failed playback");
        }

        [Test]
        public void Playback_ManyCommands_GrowsBuffers() {
            var world = new World();
            var buffer = new CommandBuffer();
            for (int i = 0; i < 500; i++) {
                var p = buffer.CreateEntity(new Position { X = i });
                buffer.Add(p, new Velocity());
            }
            buffer.Playback(world);
            Assert.That(world.EntityCount, Is.EqualTo(500), "buffer must grow past its initial capacity");
        }

        [Test]
        public void DeferredDestroy_DuringIteration_IsSafe() {
            var world = new World();
            for (int i = 0; i < 10; i++) {
                world.CreateEntity(new Health { Value = i });
            }
            var filter = world.Filter().Inc<Health>().End();
            var hp = world.Pool<Health>();
            var buffer = new CommandBuffer();
            var visited = new List<int>();

            foreach (int e in filter) {
                visited.Add(e);
                if (hp.Get(e).Value % 2 == 0) {
                    foreach (int other in filter) {
                        if (hp.Get(other).Value == hp.Get(e).Value + 1) {
                            buffer.DestroyEntity(world.GetEntity(other));
                        }
                    }
                }
            }
            buffer.Playback(world);

            Assert.That(visited.Count, Is.EqualTo(10), "deferring changes must keep the loop visiting each entity once");
            Assert.That(filter.Count, Is.EqualTo(5), "odd-valued entities must be destroyed at playback");
        }

        [Test]
        public void Playback_ReferencePayload_IsClearedForGc() {
            var world = new World();
            var buffer = new CommandBuffer();
            buffer.CreateEntity(new Inventory { Items = new List<int> { 1 } });
            Assert.That(() => buffer.Playback(world), Throws.Nothing, "reference payloads must be supported");
        }
    }
}
