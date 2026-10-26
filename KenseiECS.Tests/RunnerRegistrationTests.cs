using System;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // A runner is registered once: in one slot of one parent, never inside itself or below itself.
    // Checked in every build. A second slot shared the runner's IsEnabled with the first, so the
    // slots, IsActive and IsEnabled disagreed after SetActive; a cycle recursed without end.
    [TestFixture]
    public class RunnerRegistrationTests {
        private sealed class CountingRun : IRunSystem {
            public int Runs;

            public void Run(World world) =>
                Runs++;
        }

        // Every way a runner can be registered: a named group, a phase, an unnamed group, a runner
        // passed as an ISystem with a name, and the obsolete Add(runner, name) (both make a phase).
        private static void Register(SystemsRunner parent, SystemsRunner child, string how, string name) {
            switch (how) {
                case "group":
                    parent.AddGroup(name, child);
                    break;
                case "phase":
                    parent.AddPhase(name, child);
                    break;
                case "unnamed":
                    parent.Add(child);
                    break;
                case "system":
                    parent.Add((ISystem)child, name);
                    break;
                case "obsolete":
#pragma warning disable CS0618
                    parent.Add(child, name);
#pragma warning restore CS0618
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(how));
            }
        }

        private static readonly string[] Ways = { "group", "phase", "unnamed", "system", "obsolete" };

        private static string Slot(string how, string name, int index) =>
            (how == "group" ? $"the group \"{name}\"" : how == "unnamed" ? "an unnamed group" : $"the phase \"{name}\"") + $" (index {index})";

        [Test]
        public void SecondSlotInTheSameParent_Throws_AndAddsNothing(
            [ValueSource(nameof(Ways))] string first,
            [ValueSource(nameof(Ways))] string second) {
            var world = new World();
            var group = new SystemsRunner(world).Add(new CountingRun());
            var root = new SystemsRunner(world).Add(new CountingRun(), "before");
            Register(root, group, first, "a");

            var error = Assert.Throws<InvalidOperationException>(() => Register(root, group, second, "b"));

            Assert.That(error.Message, Does.Contain("already registered, as " + Slot(first, "a", 1) + " of this runner"));
            Assert.That(root.SystemCount, Is.EqualTo(2), "the rejected registration adds nothing");
            Assert.That(root.IndexOf("b"), Is.EqualTo(-1));
        }

        [Test]
        public void SecondRegistration_MessageNamesTheCallAndTheSlot() {
            var world = new World();
            var group = new SystemsRunner(world);
            var root = new SystemsRunner(world).Add(new CountingRun()).AddGroup("sim", group);

            var error = Assert.Throws<InvalidOperationException>(() => root.AddPhase("fixed", group));

            Assert.That(error.Message, Does.StartWith(
                "SystemsRunner.AddPhase(\"fixed\", SystemsRunner): the runner is already registered, as the group \"sim\" (index 1) of this runner. " +
                "A runner can be registered only once"));
        }

        [Test]
        public void SlotInAnotherParent_Throws_AndTheFirstSlotKeepsWorking([ValueSource(nameof(Ways))] string how) {
            var world = new World();
            var system = new CountingRun();
            var group = new SystemsRunner(world).Add(system);
            var first = new SystemsRunner(world).AddGroup("sim", group);
            var second = new SystemsRunner(world).Add(new CountingRun());

            var error = Assert.Throws<InvalidOperationException>(() => Register(second, group, how, "sim"));

            Assert.That(error.Message, Does.Contain("already registered, as the group \"sim\" (index 0) of a root runner"));
            Assert.That(second.SystemCount, Is.EqualTo(1), "the rejected registration adds nothing");

            first.Init();
            first.Run();
            Assert.That(system.Runs, Is.EqualTo(1), "the group runs once per frame, in its one slot");

            first.SetActive("sim", false);
            first.Run();
            Assert.That(system.Runs, Is.EqualTo(1));
            Assert.That(first.IsActive("sim") || first.GetSystemInfo(0).IsEnabled || group.IsEnabled, Is.False,
                "the slot, IsActive and IsEnabled agree");
        }

        [Test]
        public void SlotInAnotherParent_MessageDescribesTheWholePath() {
            var world = new World();
            var group = new SystemsRunner(world);
            var phase = new SystemsRunner(world).Add(new CountingRun()).AddGroup("combat", group);
            new SystemsRunner(world).AddPhase("fixed", phase);

            var error = Assert.Throws<InvalidOperationException>(() => new SystemsRunner(world).Add(group));

            Assert.That(error.Message, Does.Contain(
                "the group \"combat\" (index 1) of the phase \"fixed\" (index 0) of a root runner"));
        }

        [Test]
        public void RejectedRegistration_DoesNotTakeOverTheRunner() {
            var world = new World();
            var shared = new SharedData();
            var group = new SystemsRunner(world);
            var first = new SystemsRunner(world, shared).AddGroup("sim", group);
            var second = new SystemsRunner(world, new SharedData());

            Assert.Throws<InvalidOperationException>(() => second.AddGroup("sim", group));

            Assert.That(group.Shared, Is.SameAs(shared), "the group keeps the SharedData of its parent");
            Assert.That(second.SystemCount, Is.EqualTo(0));
            Assert.That(second.IndexOf("sim"), Is.EqualTo(-1));
            first.Init();
            Assert.That(() => first.Run(), Throws.Nothing);
        }

        [Test]
        public void RunnerAddedToItself_Throws([ValueSource(nameof(Ways))] string how) {
            var world = new World();
            var runner = new SystemsRunner(world).Add(new CountingRun());

            var error = Assert.Throws<InvalidOperationException>(() => Register(runner, runner, how, "self"));

            Assert.That(error.Message, Does.Contain("a runner cannot be added to itself"));
            Assert.That(runner.SystemCount, Is.EqualTo(1), "the rejected registration adds nothing");
        }

        [Test]
        public void RunnerAddedBelowItself_Throws([ValueSource(nameof(Ways))] string how) {
            var world = new World();
            var root = new SystemsRunner(world);
            var mid = new SystemsRunner(world);
            var leaf = new SystemsRunner(world);
            root.AddGroup("mid", mid);
            mid.AddPhase("leaf", leaf);

            var deep = Assert.Throws<InvalidOperationException>(() => Register(leaf, root, how, "root"));
            var direct = Assert.Throws<InvalidOperationException>(() => Register(mid, root, how, "root"));

            Assert.That(deep.Message, Does.Contain(
                "this runner is nested in the runner being added (it is the phase \"leaf\" (index 0) of the group \"mid\" (index 0) of a root runner), so adding it would make a cycle"));
            Assert.That(direct.Message, Does.Contain("cycle"));
            Assert.That(leaf.SystemCount + mid.SystemCount, Is.EqualTo(1), "the rejected registrations add nothing");
        }

        [Test]
        public void RunnerFromARejectedTree_CanBeRegisteredElsewhere() {
            var world = new World();
            var system = new CountingRun();
            var group = new SystemsRunner(world).Add(system);
            var root = new SystemsRunner(world);

            Assert.Throws<InvalidOperationException>(() => group.Add(group));
            root.AddGroup("sim", group);
            root.Init();
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(1), "a rejected call leaves the runner free to be registered once");
        }

        [Test]
        public void IndexOfExplicitName_FindsRegistrationNamesOnly() {
            var world = new World();
            var root = new SystemsRunner(world)
                .Add(new CountingRun())
                .Add(new CountingRun(), "run")
                .DelHere<Damage>()
                .AddPhase("fixed", new SystemsRunner(world))
                .AddGroup("sim", new SystemsRunner(world));

            Assert.That(root.IndexOfExplicitName("run"), Is.EqualTo(1));
            Assert.That(root.IndexOfExplicitName("fixed"), Is.EqualTo(3));
            Assert.That(root.IndexOfExplicitName("sim"), Is.EqualTo(4));
            Assert.That(root.IndexOfExplicitName(nameof(CountingRun)), Is.EqualTo(-1), "the type name of an unnamed system is not a registration name");
            Assert.That(root.IndexOfExplicitName("DelHere<Damage>"), Is.EqualTo(-1), "DelHere names may repeat and are not reserved");
            Assert.That(root.IndexOfExplicitName("late"), Is.EqualTo(-1));
        }

#if KENSEI_DEBUG
        [Test]
        public void DifferentWorld_IsRejectedBeforeAnythingIsRegistered() {
            var root = new SystemsRunner(new World());
            var child = new SystemsRunner(new World());

            Assert.Throws<InvalidOperationException>(() => root.Add(child));

            Assert.That(root.SystemCount, Is.EqualTo(0), "2.2.0-2.2.1 added the entry before the check threw");
            Assert.That(() => new SystemsRunner(child.World).Add(child), Throws.Nothing, "the child is still free to be registered");
        }

        [Test]
        public void DuplicateName_MessageNamesTheMethodCalled() {
            var world = new World();
            var root = new SystemsRunner(world).Add(new CountingRun(), "fixed");

            var phase = Assert.Throws<InvalidOperationException>(() => root.AddPhase("fixed", new SystemsRunner(world)));
            var group = Assert.Throws<InvalidOperationException>(() => root.AddGroup("fixed", new SystemsRunner(world)));
            var system = Assert.Throws<InvalidOperationException>(() => root.Add(new CountingRun(), "fixed"));

            Assert.That(phase.Message, Does.StartWith("SystemsRunner.AddPhase(\"fixed\", SystemsRunner): the name is already used by CountingRun in this runner"));
            Assert.That(group.Message, Does.StartWith("SystemsRunner.AddGroup(\"fixed\", SystemsRunner): the name is already used by CountingRun"));
            Assert.That(system.Message, Does.StartWith("SystemsRunner.Add(CountingRun, \"fixed\"): the name is already used by CountingRun"));
            Assert.That(root.SystemCount, Is.EqualTo(1));
        }
#endif
    }
}
