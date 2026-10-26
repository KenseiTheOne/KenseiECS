using System;
using System.Linq;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // The exception of a failed Init: the original one as is when the rollback succeeds, otherwise
    // one flat AggregateException with the original first and the rollback errors after it, in the
    // order they were thrown, however deep the failing system is nested.
    [TestFixture]
    public class RunnerInitExceptionTests {
        private sealed class Sys : IInitSystem, IDestroySystem {
            private readonly string _name;
            public bool ThrowOnInit;
            public bool ThrowOnDestroy;
            public Exception InitError;

            public Sys(string name) =>
                _name = name;

            public void Init(World world, SharedData shared) {
                if (InitError != null) {
                    var error = InitError;
                    InitError = null;
                    throw error;
                }
                if (ThrowOnInit) {
                    ThrowOnInit = false;
                    throw new InvalidOperationException("init failed: " + _name);
                }
            }

            public void Destroy(World world) {
                if (ThrowOnDestroy) {
                    throw new InvalidOperationException("destroy failed: " + _name);
                }
            }
        }

        private static string[] Messages(AggregateException error) =>
            error.InnerExceptions.Select(e => e.Message).ToArray();

        private static SystemsRunner Nest(SystemsRunner parent, string name, SystemsRunner child, bool phase) =>
            phase ? parent.AddPhase(name, child) : parent.AddGroup(name, child);

        [Test]
        public void TwoLevelsOfRollbackErrors_OneFlatAggregate([Values(false, true)] bool phase) {
            var world = new World();
            var group = new SystemsRunner(world)
                .Add(new Sys("g1") { ThrowOnDestroy = true })
                .Add(new Sys("g2") { ThrowOnInit = true });
            var root = new SystemsRunner(world).Add(new Sys("r1") { ThrowOnDestroy = true });
            Nest(root, "inner", group, phase).Add(new Sys("r2"));

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(Messages(thrown), Is.EqualTo(new[] { "init failed: g2", "destroy failed: g1", "destroy failed: r1" }),
                "the original first, then the group's rollback error, then the root's (2.2.0-2.2.1 nested the group's AggregateException)");
            Assert.That(thrown.InnerExceptions, Has.None.InstanceOf<AggregateException>(), "no AggregateException inside another");
            Assert.That(thrown.Flatten().InnerExceptions[0].Message, Is.EqualTo("init failed: g2"), "Flatten() starts with the original too");

            Assert.That(() => root.Init(), Throws.Nothing, "the retry starts over and succeeds");
        }

        [Test]
        public void ThreeLevelsOfRollbackErrors_InTheOrderTheyWereThrown() {
            var world = new World();
            var inner = new SystemsRunner(world)
                .Add(new Sys("i1") { ThrowOnDestroy = true })
                .Add(new Sys("i2") { ThrowOnInit = true });
            var mid = new SystemsRunner(world)
                .Add(new Sys("m1") { ThrowOnDestroy = true })
                .Add(inner);
            var root = new SystemsRunner(world)
                .Add(new Sys("r1") { ThrowOnDestroy = true })
                .AddPhase("mid", mid);

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(Messages(thrown), Is.EqualTo(new[] { "init failed: i2", "destroy failed: i1", "destroy failed: m1", "destroy failed: r1" }));
            Assert.That(thrown.InnerExceptions, Has.None.InstanceOf<AggregateException>());
        }

        [Test]
        public void OnlyTheNestedRollbackThrows_ItsAggregateReachesTheTopAsIs() {
            var world = new World();
            var group = new SystemsRunner(world)
                .Add(new Sys("g1") { ThrowOnDestroy = true })
                .Add(new Sys("g2") { ThrowOnInit = true });
            var root = new SystemsRunner(world).Add(new Sys("r1")).AddGroup("inner", group);

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(Messages(thrown), Is.EqualTo(new[] { "init failed: g2", "destroy failed: g1" }));
        }

        [Test]
        public void OnlyTheOuterRollbackThrows_OriginalStillFirst() {
            var world = new World();
            var group = new SystemsRunner(world)
                .Add(new Sys("g1"))
                .Add(new Sys("g2") { ThrowOnInit = true });
            var root = new SystemsRunner(world).Add(new Sys("r1") { ThrowOnDestroy = true }).AddGroup("inner", group);

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(Messages(thrown), Is.EqualTo(new[] { "init failed: g2", "destroy failed: r1" }));
        }

        [Test]
        public void RollbackWithoutErrors_RethrowsTheOriginalInstance_FromAnyDepth() {
            var world = new World();
            var original = new InvalidOperationException("init failed");
            var inner = new SystemsRunner(world).Add(new Sys("i1")).Add(new Sys("i2") { InitError = original });
            var root = new SystemsRunner(world)
                .Add(new Sys("r1"))
                .AddGroup("mid", new SystemsRunner(world).Add(new Sys("m1")).AddPhase("inner", inner));

            var thrown = Assert.Throws<InvalidOperationException>(() => root.Init());

            Assert.That(thrown, Is.SameAs(original), "no wrapper when nothing else failed");
        }

        [Test]
        public void AggregateThrownByASystem_IsKeptWholeAsTheOriginal([Values(false, true)] bool groupRollbackThrows) {
            var world = new World();
            var userError = new AggregateException("thrown by the system", new FormatException("a"), new FormatException("b"));
            var group = new SystemsRunner(world)
                .Add(new Sys("g1") { ThrowOnDestroy = groupRollbackThrows })
                .Add(new Sys("g2") { InitError = userError });
            var root = new SystemsRunner(world).Add(new Sys("r1") { ThrowOnDestroy = true }).AddGroup("inner", group);

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(thrown, Is.Not.SameAs(userError));
            Assert.That(thrown.InnerExceptions[0], Is.SameAs(userError), "a system's own AggregateException is the original exception, not unwrapped");
            var expectedRest = groupRollbackThrows ? new[] { "destroy failed: g1", "destroy failed: r1" } : new[] { "destroy failed: r1" };
            Assert.That(Messages(thrown).Skip(1), Is.EqualTo(expectedRest));
        }

        [Test]
        public void RollbackErrorsOfEarlierNestedRunners_AreInTheSameFlatList() {
            var world = new World();
            var earlier = new SystemsRunner(world)
                .Add(new Sys("e1") { ThrowOnDestroy = true })
                .Add(new Sys("e2") { ThrowOnDestroy = true });
            var failing = new SystemsRunner(world)
                .Add(new Sys("f1") { ThrowOnDestroy = true })
                .Add(new Sys("f2") { ThrowOnInit = true });
            var root = new SystemsRunner(world).AddPhase("earlier", earlier).AddGroup("failing", failing);

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(Messages(thrown), Is.EqualTo(new[] { "init failed: f2", "destroy failed: f1", "destroy failed: e2", "destroy failed: e1" }));
        }
    }
}
