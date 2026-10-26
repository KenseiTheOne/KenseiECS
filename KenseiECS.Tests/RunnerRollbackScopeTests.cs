using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // What the rollback of a failed Init destroys: only the systems whose Init completed and that
    // implement IDestroySystem, at every nesting level. A system without IInitSystem is left for
    // Destroy() after a completed Init, so it is destroyed once, at shutdown.
    [TestFixture]
    public class RunnerRollbackScopeTests {
        private sealed class InitDestroy : IInitSystem, IDestroySystem {
            private readonly string _name;
            private readonly List<string> _log;
            public bool ThrowOnInit;

            public InitDestroy(string name, List<string> log) {
                _name = name;
                _log = log;
            }

            public void Init(World world, SharedData shared) {
                _log.Add("init:" + _name);
                if (ThrowOnInit) {
                    ThrowOnInit = false;
                    throw new InvalidOperationException("init failed: " + _name);
                }
            }

            public void Destroy(World world) =>
                _log.Add("destroy:" + _name);
        }

        private sealed class DestroyOnly : IDestroySystem {
            private readonly string _name;
            private readonly List<string> _log;

            public DestroyOnly(string name, List<string> log) {
                _name = name;
                _log = log;
            }

            public void Destroy(World world) =>
                _log.Add("destroy:" + _name);
        }

        // Owns a resource from its constructor and frees it in Destroy, like a system that needs
        // no Init (a native buffer, a pooled object): Run after Destroy is a use after free.
        private sealed class ResourceOwner : IRunSystem, IDestroySystem {
            public bool Freed;
            public int Runs;

            public void Run(World world) {
                if (Freed) {
                    throw new ObjectDisposedException(nameof(ResourceOwner), "Run after the resource was freed");
                }
                Runs++;
            }

            public void Destroy(World world) =>
                Freed = true;
        }

        [Test]
        public void FailedInit_DestroyOnlySystem_IsLeftForDestroyAtShutdown() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new DestroyOnly("d", log))
                .Add(new InitDestroy("a", log))
                .Add(new InitDestroy("f", log) { ThrowOnInit = true });

            Assert.That(() => systems.Init(), Throws.InvalidOperationException);
            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:f", "destroy:a" }),
                "only the system whose Init completed is rolled back; 2.2.0-2.2.1 destroyed the Destroy-only one too");

            systems.Init();
            systems.Destroy();

            Assert.That(log, Is.EqualTo(new[] {
                "init:a", "init:f", "destroy:a",
                "init:a", "init:f",
                "destroy:f", "destroy:a", "destroy:d"
            }), "the retry starts over, and Destroy at shutdown destroys every system, the Destroy-only one included");
            Assert.That(log.FindAll(s => s == "destroy:d").Count, Is.EqualTo(1), "a Destroy-only system is destroyed exactly once");
        }

        [Test]
        public void FailedInit_DestroyOnlySystem_KeepsItsResourceForTheRetry() {
            var world = new World();
            var owner = new ResourceOwner();
            var systems = new SystemsRunner(world).Add(owner).Add(new ThrowOnceInitSystem());

            Assert.That(() => systems.Init(), Throws.InvalidOperationException);
            Assert.That(owner.Freed, Is.False, "a system without Init initialized nothing, so the rollback must not free its resource");

            systems.Init();
            Assert.That(() => systems.Run(), Throws.Nothing, "the retried runner runs with the resource intact");
            Assert.That(owner.Runs, Is.EqualTo(1));

            systems.Destroy();
            Assert.That(owner.Freed, Is.True, "Destroy at shutdown frees it");
        }

        [Test]
        public void FailedInit_InsideGroup_DestroyOnlySystemsBeforeTheFailure_AreNotDestroyed() {
            var world = new World();
            var log = new List<string>();
            var group = new SystemsRunner(world)
                .Add(new DestroyOnly("gd", log))
                .Add(new InitDestroy("g1", log))
                .Add(new InitDestroy("g2", log) { ThrowOnInit = true });
            var root = new SystemsRunner(world)
                .Add(new DestroyOnly("rd", log))
                .Add(new InitDestroy("r1", log))
                .AddGroup("sim", group);

            Assert.That(() => root.Init(), Throws.InvalidOperationException);

            Assert.That(log, Is.EqualTo(new[] { "init:r1", "init:g1", "init:g2", "destroy:g1", "destroy:r1" }),
                "the failing group rolls back its initialized system, then the root its own; no Destroy-only system is touched");
            Assert.That(group.IsInitialized || root.IsInitialized, Is.False);
        }

        [Test]
        public void FailedInit_AfterInitializedRunners_DestroysOnlyTheirInitializedSystems([Values(false, true)] bool phase) {
            var world = new World();
            var log = new List<string>();
            var inner = new SystemsRunner(world)
                .Add(new DestroyOnly("id", log))
                .Add(new InitDestroy("i1", log));
            var group = new SystemsRunner(world)
                .Add(new InitDestroy("g1", log))
                .Add(new DestroyOnly("gd", log))
                .AddPhase("inner", inner);
            var root = new SystemsRunner(world);
            if (phase) {
                root.AddPhase("fixed", group);
            } else {
                root.AddGroup("sim", group);
            }
            root.Add(new InitDestroy("f", log) { ThrowOnInit = true });

            Assert.That(() => root.Init(), Throws.InvalidOperationException);

            Assert.That(log, Is.EqualTo(new[] { "init:g1", "init:i1", "init:f", "destroy:i1", "destroy:g1" }),
                "runners that completed Init are rolled back system by system, without their Destroy-only systems");
            Assert.That(inner.IsInitialized || group.IsInitialized || root.IsInitialized, Is.False, "no runner stays initialized");

            log.Clear();
            root.Init();
            root.Destroy();
            Assert.That(log, Is.EqualTo(new[] {
                "init:g1", "init:i1", "init:f",
                "destroy:f", "destroy:i1", "destroy:id", "destroy:gd", "destroy:g1"
            }), "after a completed Init, Destroy destroys everything once, in reverse order");
        }

        [Test]
        public void FailedInit_SystemsAndRunnersAfterTheFailure_AreNotTouched() {
            var world = new World();
            var log = new List<string>();
            var later = new SystemsRunner(world)
                .Add(new InitDestroy("l1", log))
                .Add(new DestroyOnly("ld", log));
            var root = new SystemsRunner(world)
                .Add(new InitDestroy("a", log))
                .Add(new InitDestroy("f", log) { ThrowOnInit = true })
                .AddGroup("later", later)
                .Add(new DestroyOnly("z", log));

            Assert.That(() => root.Init(), Throws.InvalidOperationException);

            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:f", "destroy:a" }), "nothing after the failed system started Init, so nothing there is destroyed");
            Assert.That(later.IsInitialized, Is.False);
        }
    }
}
