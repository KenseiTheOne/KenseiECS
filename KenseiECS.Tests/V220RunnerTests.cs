using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Tests for the 2.2.0 runner API: AddPhase/AddGroup, IndexOf, the inline group's
    // IsEnabled kept in sync with its slot, OneFrame(cleanWhenDisabled) and the Init rollback.

    [TestFixture]
    public class RunnerPhaseAndGroupTests {
        private sealed class CountingRun : IRunSystem {
            public int Runs;

            public void Run(World world) =>
                Runs++;
        }

        [Test]
        public void DirectRunOfGroup_WithSlotOff_RunsNothing() {
            var world = new World();
            var inner = new CountingRun();
            var group = new SystemsRunner(world).Add(inner);
            var root = new SystemsRunner(world).Add(group);
            root.Init();

            root.SetActive(0, false);
            group.Run();
            Assert.That(inner.Runs, Is.EqualTo(0), "the slot also turns the group off, so a direct Run does nothing (2.1.x ran it)");

            root.SetActive(0, true);
            group.Run();
            Assert.That(inner.Runs, Is.EqualTo(1));
        }

        [Test]
        public void AddGroup_RunsInsideParentRun() {
            var world = new World();
            var system = new CountingRun();
            var group = new SystemsRunner(world).Add(system);
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();

            root.Run();

            Assert.That(system.Runs, Is.EqualTo(1), "a named group must run as part of the parent's Run");
            var info = root.GetSystemInfo(0);
            Assert.That(info.Name, Is.EqualTo("sim"), "SystemInfo.Name must be the group's name");
            Assert.That(info.IsRunnable, Is.True, "a group is part of the parent's Run pipeline");
            Assert.That(info.IsSeparatePhase, Is.False, "a group is not a separate phase");
            Assert.That(info.ChildRunner, Is.SameAs(group), "ChildRunner must expose the group");
            Assert.That(root.GetRunner("sim"), Is.SameAs(group), "GetRunner must find a named group");
        }

        [Test]
        public void AddGroup_SetActiveByName_SkipsGroupAndSyncsIsEnabled() {
            var world = new World();
            var system = new CountingRun();
            var group = new SystemsRunner(world).Add(system);
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();

            root.SetActive("sim", false);
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(0), "a disabled group must be skipped");
            Assert.That(root.IsActive("sim"), Is.False, "IsActive must report the group as disabled");
            Assert.That(root.GetSystemInfo(0).IsEnabled, Is.False, "the slot must be disabled");
            Assert.That(group.IsEnabled, Is.False, "the group's own IsEnabled must follow its slot");

            root.SetActive("sim", true);
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(1), "re-enabling must run the group again");
            Assert.That(root.IsActive("sim") && root.GetSystemInfo(0).IsEnabled && group.IsEnabled, Is.True,
                "slot, IsActive and IsEnabled must all report enabled");
        }

        [Test]
        public void UnnamedGroup_SetActiveByIndex_SyncsIsEnabled() {
            var world = new World();
            var group = new SystemsRunner(world).Add(new CountingRun());
            var root = new SystemsRunner(world).Add(group);
            root.Init();

            root.SetActive(0, false);
            Assert.That(group.IsEnabled, Is.False, "SetActive(int) must switch the inline group's IsEnabled");

            root.SetActive(0, true);
            Assert.That(group.IsEnabled, Is.True, "SetActive(int) must switch it back");
        }

        [Test]
        public void AddGroup_SetActiveByIndex_SyncsNameLookup() {
            var world = new World();
            var group = new SystemsRunner(world).Add(new CountingRun());
            var root = new SystemsRunner(world).Add(new CountingRun()).AddGroup("sim", group);
            root.Init();

            root.SetActive(root.IndexOf("sim"), false);

            Assert.That(root.IsActive("sim"), Is.False, "IsActive(name) must see a change made by index");
            Assert.That(group.IsEnabled, Is.False, "the group's IsEnabled must see it too");
        }

        [Test]
        public void AddPhase_IsNotRunByParent() {
            var world = new World();
            var system = new CountingRun();
            var phase = new SystemsRunner(world).Add(system);
            var root = new SystemsRunner(world).AddPhase("fixed", phase);
            root.Init();

            root.Run();
            Assert.That(system.Runs, Is.EqualTo(0), "a phase is not part of the parent's Run");

            root.GetRunner("fixed").Run();
            Assert.That(system.Runs, Is.EqualTo(1), "a phase runs through GetRunner(name).Run()");

            var info = root.GetSystemInfo(0);
            Assert.That(info.IsSeparatePhase, Is.True, "AddPhase must register a separate phase");
            Assert.That(info.IsRunnable, Is.False, "a phase is not in the parent's Run pipeline");
            Assert.That(info.Name, Is.EqualTo("fixed"), "SystemInfo.Name must be the phase's name");
        }

        [Test]
        public void AddPhase_InitAndDestroyCascade() {
            var world = new World();
            var log = new List<string>();
            var phase = new SystemsRunner(world).Add(new OrderTrackingSystem("phase", log));
            var group = new SystemsRunner(world).Add(new OrderTrackingSystem("group", log));
            var root = new SystemsRunner(world).AddPhase("fixed", phase).AddGroup("sim", group);

            root.Init();
            root.Destroy();

            Assert.That(log, Is.EqualTo(new[] { "init:phase", "init:group", "destroy:group", "destroy:phase" }),
                "Init and Destroy must cascade to phases and groups in registration order");
        }

        [Test]
        public void AddWithName_OnRunner_StillRegistersPhase() {
            var world = new World();
            var system = new CountingRun();
            var child = new SystemsRunner(world).Add(system);
            // Compatibility check of the obsolete overload: the warning is the point of it.
#pragma warning disable CS0618
            var root = new SystemsRunner(world).Add(child, "fixed");
#pragma warning restore CS0618
            root.Init();

            root.Run();

            Assert.That(system.Runs, Is.EqualTo(0), "Add(runner, name) keeps its 2.1 meaning: a separate phase");
            Assert.That(root.GetSystemInfo(0).IsSeparatePhase, Is.True, "Add(runner, name) must be a phase");
        }

        [Test]
        public void AddPhaseAndAddGroup_RequireRunnerAndName() {
            var world = new World();
            var root = new SystemsRunner(world);

            Assert.That(() => root.AddGroup("sim", null), Throws.TypeOf<ArgumentNullException>(), "a null group must throw");
            Assert.That(() => root.AddPhase("fixed", null), Throws.TypeOf<ArgumentNullException>(), "a null phase must throw");
            Assert.That(() => root.AddGroup(null, new SystemsRunner(world)), Throws.ArgumentException, "a group needs a name");
            Assert.That(() => root.AddPhase("", new SystemsRunner(world)), Throws.ArgumentException, "a phase needs a name");
            Assert.That(root.SystemCount, Is.EqualTo(0), "rejected registrations must not be added");
        }

        [Test]
        public void GroupName_IsAProfilerAndInfoName_NotTheTypeName() {
            var world = new World();
            var root = new SystemsRunner(world)
                .Add(new SystemsRunner(world))
                .AddGroup("sim", new SystemsRunner(world));

            Assert.That(root.GetSystemInfo(0).Name, Is.EqualTo(nameof(SystemsRunner)), "an unnamed group keeps the type name");
            Assert.That(root.GetSystemInfo(1).Name, Is.EqualTo("sim"), "a named group reports its name");
        }

        [Test]
        public void SetActive_DisabledGroupInsideDisabledParentSlot_StaysSkipped() {
            var world = new World();
            var system = new CountingRun();
            var inner = new SystemsRunner(world).Add(system);
            var outer = new SystemsRunner(world).AddGroup("inner", inner);
            var root = new SystemsRunner(world).AddGroup("outer", outer);
            root.Init();

            outer.SetActive("inner", false);
            root.Run();
            Assert.That(system.Runs, Is.EqualTo(0), "a disabled nested group is skipped");

            outer.SetActive("inner", true);
            root.SetActive("outer", false);
            root.Run();
            Assert.That(system.Runs, Is.EqualTo(0), "a disabled outer group skips everything inside it");
            Assert.That(inner.IsEnabled, Is.True, "disabling the outer group does not touch the inner group's own flag");
        }

#if KENSEI_DEBUG
        [Test]
        public void DuplicateName_ThrowsInDebug() {
            var world = new World();
            var root = new SystemsRunner(world).Add(new CountingRun(), "a");

            Assert.That(() => root.Add(new CountingRun(), "a"), Throws.InvalidOperationException, "a repeated system name must throw");
            Assert.That(() => root.AddGroup("a", new SystemsRunner(world)), Throws.InvalidOperationException, "a group may not reuse a system name");
            Assert.That(() => root.AddPhase("a", new SystemsRunner(world)), Throws.InvalidOperationException, "a phase may not reuse a system name");
        }
#else
        // The release lookup the runner page describes: the name reaches the last system or group
        // registered under it, a phase only when no system or group has it, GetRunner the last
        // group or phase. EcsBootstrap's "fixed" and "late" are the system-then-phase case.
        [TestCase("system", "phase")]
        [TestCase("phase", "system")]
        [TestCase("group", "phase")]
        [TestCase("phase", "group")]
        [TestCase("system", "system")]
        [TestCase("system", "group")]
        public void DuplicateName_InRelease_LookupFollowsTheRunnerPage(string first, string second) {
            var world = new World();
            var root = new SystemsRunner(world);
            ISystem firstSystem = Register(root, first, world);
            ISystem secondSystem = Register(root, second, world);
            root.Init();

            bool firstIsPhase = first == "phase", secondIsPhase = second == "phase";
            var byName = firstIsPhase ? secondSystem : secondIsPhase ? firstSystem : secondSystem;
            var runner = secondSystem is SystemsRunner && (second != "system") ? secondSystem : firstSystem is SystemsRunner && first != "system" ? firstSystem : null;
            Assert.That(root.IndexOf("a"), Is.EqualTo(root.IndexOf(byName)), "IndexOf(name)");
            Assert.That(root.GetRunner("a"), Is.SameAs(runner), "GetRunner(name)");
            root.SetActive("a", false);
            Assert.That(root.IsActive("a"), Is.False);
            Assert.That(root.GetSystemInfo(root.IndexOf(byName)).IsEnabled, Is.False, "SetActive(name) reaches the system or group");
            var other = ReferenceEquals(byName, firstSystem) ? secondSystem : firstSystem;
            Assert.That(root.GetSystemInfo(root.IndexOf(other)).IsEnabled, Is.True, "the other registration keeps running");
        }

        private static ISystem Register(SystemsRunner root, string how, World world) {
            switch (how) {
                case "system":
                    var system = new CountingRun();
                    root.Add(system, "a");
                    return system;
                case "group":
                    var group = new SystemsRunner(world).Add(new CountingRun());
                    root.AddGroup("a", group);
                    return group;
                default:
                    var phase = new SystemsRunner(world).Add(new CountingRun());
                    root.AddPhase("a", phase);
                    return phase;
            }
        }
#endif

        [Test]
        public void DelHereOfSameType_Twice_IsAllowed() {
            var world = new World();
            var root = new SystemsRunner(world);

            Assert.That(() => root.DelHere<Damage>().Add(new CountingRun()).DelHere<Damage>(), Throws.Nothing,
                "DelHere names are generated by the runner and may repeat");
            Assert.That(root.SystemCount, Is.EqualTo(3), "all three entries must be registered");
        }
    }

    [TestFixture]
    public class RunnerIndexOfTests {
        private sealed class RunA : IRunSystem {
            public void Run(World world) {
            }
        }

        [Test]
        public void IndexOf_System_ReturnsRegistrationPosition() {
            var world = new World();
            var a = new RunA();
            var b = new TestDestroySystem();
            var group = new SystemsRunner(world);
            var root = new SystemsRunner(world).Add(a).Add(b).AddGroup("sim", group);

            Assert.That(root.IndexOf(a), Is.EqualTo(0), "first system");
            Assert.That(root.IndexOf(b), Is.EqualTo(1), "a non-run system has a position too");
            Assert.That(root.IndexOf(group), Is.EqualTo(2), "a nested runner has a position");
            Assert.That(root.IndexOf(new RunA()), Is.EqualTo(-1), "an unregistered system returns -1");
            Assert.That(root.IndexOf((ISystem)null), Is.EqualTo(-1), "null returns -1");
            Assert.That(root.GetSystemInfo(root.IndexOf(b)).System, Is.SameAs(b), "IndexOf must match GetSystemInfo");
        }

        [Test]
        public void IndexOf_Name_MatchesSystemInfoName() {
            var world = new World();
            var root = new SystemsRunner(world)
                .Add(new RunA())
                .Add(new RunA(), "named")
                .AddPhase("fixed", new SystemsRunner(world));

            Assert.That(root.IndexOf("named"), Is.EqualTo(1), "a registration name");
            Assert.That(root.IndexOf("fixed"), Is.EqualTo(2), "a phase name");
            Assert.That(root.IndexOf(nameof(RunA)), Is.EqualTo(0), "an unnamed system is found by its type name, first match");
            Assert.That(root.IndexOf("missing"), Is.EqualTo(-1), "an unknown name returns -1 in every build");
            Assert.That(root.IndexOf((string)null), Is.EqualTo(-1), "null returns -1");
        }

        [Test]
        public void IndexOf_Name_ResolvesLikeSetActive_WhenExplicitNameEqualsTypeName() {
            var world = new World();
            var unnamed = new RunA();
            var other = new CountingRun();
            // The unnamed system is listed as "RunA"; the explicit "RunA" is a different system.
            var root = new SystemsRunner(world).Add(unnamed).Add(other, nameof(RunA));

            Assert.That(root.IndexOf(nameof(RunA)), Is.EqualTo(1), "the explicit name wins, as in SetActive(string)");

            root.SetActive(root.IndexOf(nameof(RunA)), false);
            Assert.That(root.IsActive(nameof(RunA)), Is.False, "SetActive(IndexOf(name)) must toggle what IsActive(name) reads");
            Assert.That(root.GetSystemInfo(0).IsEnabled, Is.True, "the unnamed system with the same type name is untouched");

            root.Init();
            root.Run();
            Assert.That(other.Runs, Is.EqualTo(0));
        }

        private sealed class CountingRun : IRunSystem {
            public int Runs;

            public void Run(World world) =>
                Runs++;
        }

        [Test]
        public void IndexOf_DoesNotSearchNestedRunners() {
            var world = new World();
            var inner = new RunA();
            var root = new SystemsRunner(world).AddGroup("sim", new SystemsRunner(world).Add(inner, "inner"));

            Assert.That(root.IndexOf(inner), Is.EqualTo(-1), "a system of a nested runner is not this runner's");
            Assert.That(root.IndexOf("inner"), Is.EqualTo(-1), "nor is its name");
        }

        private sealed class InitOnly : IInitSystem {
            public void Init(World world, SharedData shared) { }
        }

        [Test]
        public void InitOnlyNamedSystem_IndexOfFindsIt_SetActiveCannotToggleIt() {
            var world = new World();
            var root = new SystemsRunner(world).Add(new InitOnly(), "setup").Add(new RunA(), "run");

            int index = root.IndexOf("setup");
            Assert.That(index, Is.EqualTo(0), "IndexOf finds any registered system");
            Assert.That(root.GetSystemInfo(index).IsRunnable, Is.False);
#if KENSEI_DEBUG
            var byIndex = Assert.Throws<InvalidOperationException>(() => root.SetActive(index, false));
            Assert.That(byIndex.Message, Does.Contain("no Run"));
            var byName = Assert.Throws<InvalidOperationException>(() => root.SetActive("setup", false));
            Assert.That(byName.Message, Does.Contain("no Run"), "a known name without Run is not reported as unknown");
            Assert.Throws<InvalidOperationException>(() => root.IsActive("setup"));
            var getRunner = Assert.Throws<InvalidOperationException>(() => root.GetRunner("setup"));
            Assert.That(getRunner.Message, Does.Contain("not a nested runner"));
#else
            Assert.DoesNotThrow(() => root.SetActive(index, false), "release ignores it");
            Assert.DoesNotThrow(() => root.SetActive("setup", false));
            Assert.That(root.IsActive("setup"), Is.False);
#endif
            Assert.That(root.IsActive("run"), Is.True, "the other system is untouched");
        }

        [Test]
        public void Names_AreCaseSensitive() {
            var world = new World();
            var root = new SystemsRunner(world).AddGroup("sim", new SystemsRunner(world));

            Assert.That(root.IndexOf("Sim"), Is.EqualTo(-1));
#if KENSEI_DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() => root.IsActive("Sim"));
            Assert.That(ex.Message, Does.Contain("case-sensitive"));
#else
            Assert.That(root.IsActive("Sim"), Is.False, "release: an unknown name reads as disabled");
            root.SetActive("Sim", false);
            Assert.That(root.IsActive("sim"), Is.True, "release: SetActive with an unknown name does nothing");
#endif
        }

        [Test]
        public void AddRunnerWithName_IsObsolete_OtherAddOverloadsAreNot() {
            var obsolete = typeof(SystemsRunner).GetMethod(nameof(SystemsRunner.Add), new[] { typeof(SystemsRunner), typeof(string) });
            var plain = typeof(SystemsRunner).GetMethod(nameof(SystemsRunner.Add), new[] { typeof(ISystem), typeof(string) });

            var attribute = (ObsoleteAttribute)Attribute.GetCustomAttribute(obsolete, typeof(ObsoleteAttribute));
            Assert.That(attribute, Is.Not.Null, "Add(runner, name) warns at compile time");
            Assert.That(attribute.IsError, Is.False, "a warning, not an error: existing code keeps compiling");
            Assert.That(attribute.Message, Does.Contain("AddPhase(name, runner)").And.Contain("AddGroup(name, runner)"));
            // Add(runner, null) binds to the same overload and makes an unnamed group, so the
            // warning must not claim that every call creates a phase.
            Assert.That(attribute.Message, Does.StartWith("With a name,").And.Contain("Add(runner) for an unnamed one"));
            Assert.That(Attribute.IsDefined(plain, typeof(ObsoleteAttribute)), Is.False, "Add(system, name) and Add(runner) do not warn");
        }

        [Test]
        public void AddRunnerWithNullName_IsUnnamedInlineGroup() {
            var world = new World();
            var system = new CountingRun();
            var child = new SystemsRunner(world).Add(system);
#pragma warning disable CS0618
            var root = new SystemsRunner(world).Add(child, null);
#pragma warning restore CS0618
            root.Init();

            root.Run();

            Assert.That(system.Runs, Is.EqualTo(1));
            Assert.That(root.GetSystemInfo(0).IsSeparatePhase, Is.False);
        }

        [Test]
        public void GroupSetActive_FromItsOwnHandle_SyncsParentSlot([Values(false, true)] bool named) {
            var world = new World();
            var system = new CountingRun();
            var group = new SystemsRunner(world).Add(system);
            var root = named
                ? new SystemsRunner(world).Add(new RunA()).AddGroup("sim", group)
                : new SystemsRunner(world).Add(new RunA()).Add(group);
            root.Init();

            group.SetActive(false);
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(0), "the parent skips the group's slot");
            Assert.That(group.IsEnabled, Is.False);
            Assert.That(root.GetSystemInfo(1).IsEnabled, Is.False, "the slot follows the group's handle");
            if (named) {
                Assert.That(root.IsActive("sim"), Is.False);
            }

            group.SetActive(true);
            root.Run();

            Assert.That(system.Runs, Is.EqualTo(1));
            Assert.That(root.GetSystemInfo(1).IsEnabled && group.IsEnabled, Is.True);

            root.SetActive(1, false);
            Assert.That(group.IsEnabled, Is.False, "and the other way round, as before");
        }

        [Test]
        public void PhaseAndRootSetActive_ToggleTheRunnerItself() {
            var world = new World();
            var inPhase = new CountingRun();
            var inRoot = new CountingRun();
            var phase = new SystemsRunner(world).Add(inPhase);
            var root = new SystemsRunner(world).Add(inRoot).AddPhase("fixed", phase);
            root.Init();

            phase.SetActive(false);
            phase.Run();
            Assert.That(inPhase.Runs, Is.EqualTo(0));
            Assert.That(root.IsActive("fixed"), Is.False, "the parent reads the phase's own flag");

            root.SetActive(false);
            int tick = world.Tick;
            root.Run();
            Assert.That(inRoot.Runs, Is.EqualTo(0), "a disabled root runs no systems");
            Assert.That(world.Tick, Is.EqualTo(tick + 1), "the tick still advances");

            root.SetActive(true);
            phase.SetActive(true);
            root.Run();
            phase.Run();
            Assert.That(inRoot.Runs, Is.EqualTo(1));
            Assert.That(inPhase.Runs, Is.EqualTo(1));
        }
    }

    [TestFixture]
    public class RunnerOneFrameWhenDisabledTests {
        private sealed class DamageProducer : IRunSystem {
            public Entity Target;

            public void Run(World world) =>
                world.Add(Target, new Damage { Value = 1 });
        }

        [Test]
        public void DefaultOneFrame_DisabledGroup_KeepsComponents() {
            var world = new World();
            var group = new SystemsRunner(world).OneFrame<Damage>();
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();
            var e = world.CreateEntity(new Position());

            root.SetActive("sim", false);
            world.Add(e, new Damage());
            root.Run();

            Assert.That(world.Has<Damage>(e), Is.True, "OneFrame<T>() keeps the 2.1 behavior: no cleanup while skipped");
        }

        [Test]
        public void CleanWhenDisabled_DisabledGroupSlot_RemovesComponents([Values(false, true)] bool named) {
            var world = new World();
            var group = new SystemsRunner(world).OneFrame<Damage>(cleanWhenDisabled: true);
            var root = named
                ? new SystemsRunner(world).AddGroup("sim", group)
                : new SystemsRunner(world).Add(group);
            root.Init();
            var e = world.CreateEntity(new Position());

            root.SetActive(0, false);
            world.Add(e, new Damage());
            root.Run();

            Assert.That(world.Has<Damage>(e), Is.False, "a skipped group must still remove its cleanWhenDisabled types");
        }

        [Test]
        public void CleanWhenDisabled_EnabledGroup_RemovesAtEndOfItsRun() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var group = new SystemsRunner(world)
                .Add(new DamageProducer { Target = e })
                .OneFrame<Damage>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();

            root.Run();

            Assert.That(world.Has<Damage>(e), Is.False, "an enabled group cleans as usual");
        }

        [Test]
        public void CleanWhenDisabled_RemovesAtSlotPosition() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var before = new DamageCountingSystem();
            var after = new DamageCountingSystem();
            var group = new SystemsRunner(world).OneFrame<Damage>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).Add(before).AddGroup("sim", group).Add(after);
            root.Init();

            root.SetActive("sim", false);
            world.Add(e, new Damage());
            root.Run();

            Assert.That(before.SeenLastRun, Is.EqualTo(1), "a system before the skipped slot still sees the component");
            Assert.That(after.SeenLastRun, Is.EqualTo(0), "a system after the skipped slot no longer does");
        }

        [Test]
        public void CleanWhenDisabled_DisabledPhaseRun_RemovesComponents() {
            var world = new World();
            var phase = new SystemsRunner(world).OneFrame<Damage>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).AddPhase("fixed", phase);
            root.Init();
            var e = world.CreateEntity(new Position());

            root.SetActive("fixed", false);
            world.Add(e, new Damage());
            root.Run();
            Assert.That(world.Has<Damage>(e), Is.True, "the parent does not clean a phase: it does not drive it");

            root.GetRunner("fixed").Run();
            Assert.That(world.Has<Damage>(e), Is.False, "Run() of a disabled phase removes its cleanWhenDisabled types");
        }

        [Test]
        public void CleanWhenDisabled_CascadesIntoGroupsOfSkippedRunner() {
            var world = new World();
            var inner = new SystemsRunner(world).OneFrame<Damage>(cleanWhenDisabled: true);
            var outer = new SystemsRunner(world).Add(inner);
            var root = new SystemsRunner(world).AddGroup("outer", outer);
            root.Init();
            var e = world.CreateEntity(new Position());

            root.SetActive("outer", false);
            world.Add(e, new Damage());
            root.Run();

            Assert.That(world.Has<Damage>(e), Is.False, "an inline group inside a skipped group is skipped too, so it cleans");
        }

        [Test]
        public void CleanWhenDisabled_DoesNotRunSystemsOrDelHere() {
            var world = new World();
            var e = world.CreateEntity(new Position());
            var counter = new DamageCountingSystem();
            var group = new SystemsRunner(world)
                .Add(counter)
                .DelHere<Health>()
                .OneFrame<Damage>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();

            root.SetActive("sim", false);
            world.Add(e, new Damage());
            world.Add(e, new Health());
            root.Run();

            Assert.That(counter.SeenLastRun, Is.EqualTo(0), "systems of a skipped group must not run");
            Assert.That(world.Has<Health>(e), Is.True, "DelHere is a system of the group and is skipped with it");
            Assert.That(world.Has<Damage>(e), Is.False, "only the cleanWhenDisabled OneFrame cleanup runs");
        }

        // Round-2: Run passes skipped slots without a lookup unless a cleanWhenDisabled type exists
        // below. A type registered after the group was added must still reach every ancestor.
        [Test]
        public void CleanWhenDisabled_RegisteredAfterAdd_PropagatesToAncestors() {
            var world = new World();
            var inner = new SystemsRunner(world);
            var outer = new SystemsRunner(world).Add(inner);
            var root = new SystemsRunner(world).AddGroup("outer", outer);
            var host = new SystemsRunner(world).AddPhase("root", root);
            inner.OneFrame<Damage>(cleanWhenDisabled: true);
            host.Init();
            var e = world.CreateEntity(new Position());

            root.SetActive("outer", false);
            world.Add(e, new Damage());
            root.Run();
            Assert.That(world.Has<Damage>(e), Is.False, "a skipped slot cleans a cleanWhenDisabled type registered after the group was added");

            root.SetActive("outer", true);
            outer.SetActive(0, false);
            world.Add(e, new Damage());
            root.Run();
            Assert.That(world.Has<Damage>(e), Is.False, "the middle runner's skipped slot cleans it too");

            outer.SetActive(0, true);
            host.SetActive("root", false);
            world.Add(e, new Damage());
            root.Run();
            Assert.That(world.Has<Damage>(e), Is.False, "Run() of a disabled phase cascades into its groups");
        }

        // A runner is registered in one parent only (2.2.0 accepted a second one), so a cleanup
        // registered later reaches that parent and nothing is left in the rejected one.
        [Test]
        public void CleanWhenDisabled_GroupRejectedBySecondParent_PropagatesToItsParentOnly() {
            var world = new World();
            var group = new SystemsRunner(world);
            var a = new SystemsRunner(world).AddGroup("g", group);
            var b = new SystemsRunner(world);

            Assert.That(() => b.AddGroup("g", group), Throws.InvalidOperationException, "a runner is registered only once");
            group.OneFrame<Damage>(cleanWhenDisabled: true);

            Assert.That(HasSkipCleanup(a), Is.True, "the parent must learn about the cleanup");
            Assert.That(HasSkipCleanup(b), Is.False, "the rejected registration must leave nothing behind");
            Assert.That(b.SystemCount, Is.EqualTo(0));
        }

        [Test]
        public void NoCleanWhenDisabled_RunnerTreeHasNoSkipCleanup() {
            var world = new World();
            var group = new SystemsRunner(world).OneFrame<Damage>();
            var phase = new SystemsRunner(world).OneFrame<Health>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).AddGroup("sim", group).AddPhase("fixed", phase).OneFrame<Damage>();

            Assert.That(HasSkipCleanup(root), Is.False, "without cleanWhenDisabled in inline groups the skipped slots are passed without a lookup");
            Assert.That(HasSkipCleanup(phase), Is.True, "a phase keeps its own flag for a direct Run() while disabled");
        }

        private static bool HasSkipCleanup(SystemsRunner runner) {
            return (bool)typeof(SystemsRunner)
                .GetField("_hasSkipCleanup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(runner)!;
        }

        [Test]
        public void CleanWhenDisabled_SkippedFrames_DoNotAllocate() {
            var world = new World();
            var group = new SystemsRunner(world).OneFrame<Damage>(cleanWhenDisabled: true);
            var root = new SystemsRunner(world).AddGroup("sim", group);
            root.Init();
            var entities = new Entity[32];
            for (int i = 0; i < entities.Length; i++) {
                entities[i] = world.CreateEntity(new Position());
            }
            root.SetActive("sim", false);
            for (int frame = 0; frame < 4; frame++) {
                foreach (var e in entities) {
                    world.Add(e, new Damage());
                }
                root.Run();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 100; frame++) {
                foreach (var e in entities) {
                    world.Add(e, new Damage());
                }
                root.Run();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the skip cleanup must not allocate");
        }
    }

    [TestFixture]
    public class RunnerInitRollbackTests {
        private sealed class InitDestroyLog : IInitSystem, IDestroySystem {
            private readonly string _name;
            private readonly List<string> _log;
            public bool ThrowOnInit;
            public bool ThrowOnDestroy;

            public InitDestroyLog(string name, List<string> log) {
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

            public void Destroy(World world) {
                _log.Add("destroy:" + _name);
                if (ThrowOnDestroy) {
                    throw new InvalidOperationException("destroy failed: " + _name);
                }
            }
        }

        private sealed class DestroyOnly : IDestroySystem {
            private readonly List<string> _log;

            public DestroyOnly(List<string> log) =>
                _log = log;

            public void Destroy(World world) =>
                _log.Add("destroy:only");
        }

        [Test]
        public void FailedInit_DestroysEarlierSystemsInReverse_AndRethrowsOriginal() {
            var world = new World();
            var log = new List<string>();
            var failing = new InitDestroyLog("c", log) { ThrowOnInit = true };
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log))
                .Add(new DestroyOnly(log))
                .Add(new InitDestroyLog("b", log))
                .Add(failing)
                .Add(new InitDestroyLog("d", log));

            var thrown = Assert.Throws<InvalidOperationException>(() => systems.Init(), "the original exception must propagate");

            Assert.That(thrown.Message, Is.EqualTo("init failed: c"), "it must be the failing system's own exception");
            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:b", "init:c", "destroy:b", "destroy:a" }),
                "systems initialized before the failed one are destroyed in reverse order; the failed and later ones are not, " +
                "and neither is the Destroy-only system (2.2.0-2.2.1 destroyed it)");
            Assert.That(systems.IsInitialized, Is.False, "the runner stays uninitialized");
        }

        [Test]
        public void FailedInit_ThenDestroy_IsNoOp() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log))
                .Add(new InitDestroyLog("b", log) { ThrowOnInit = true });

            Assert.That(() => systems.Init(), Throws.InvalidOperationException);
            log.Clear();
            systems.Destroy();

            Assert.That(log, Is.Empty, "the rollback already destroyed them; Destroy must not destroy twice");
        }

        [Test]
        public void FailedInit_Retry_StartsFromFirstSystem() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log))
                .Add(new InitDestroyLog("b", log) { ThrowOnInit = true });

            Assert.That(() => systems.Init(), Throws.InvalidOperationException);
            log.Clear();
            systems.Init();

            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:b" }), "the retry must initialize every system again, from the start");
            Assert.That(systems.IsInitialized, Is.True, "a successful retry initializes the runner");
        }

        [Test]
        public void FailedInit_FirstSystem_DestroysNothing() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log) { ThrowOnInit = true })
                .Add(new InitDestroyLog("b", log));

            Assert.That(() => systems.Init(), Throws.InvalidOperationException);

            Assert.That(log, Is.EqualTo(new[] { "init:a" }), "nothing was initialized before the failure, so nothing is destroyed");
        }

        [Test]
        public void FailedInit_InNestedRunner_RollsBackChildThenParent() {
            var world = new World();
            var log = new List<string>();
            var child = new SystemsRunner(world)
                .Add(new InitDestroyLog("child-a", log))
                .Add(new InitDestroyLog("child-b", log) { ThrowOnInit = true });
            var earlierChild = new SystemsRunner(world).Add(new InitDestroyLog("phase-a", log));
            var root = new SystemsRunner(world)
                .Add(new InitDestroyLog("root-a", log))
                .AddPhase("fixed", earlierChild)
                .AddGroup("sim", child)
                .Add(new InitDestroyLog("root-z", log));

            Assert.That(() => root.Init(), Throws.InvalidOperationException, "the nested failure must propagate");

            Assert.That(log, Is.EqualTo(new[] {
                "init:root-a", "init:phase-a", "init:child-a", "init:child-b",
                "destroy:child-a",
                "destroy:phase-a", "destroy:root-a"
            }), "the failing child rolls itself back, then the parent destroys what came before the child");
            Assert.That(child.IsInitialized || earlierChild.IsInitialized || root.IsInitialized, Is.False,
                "no runner stays initialized");
        }

        [Test]
        public void FailedInit_RollbackThrows_AggregatesOriginalFirst() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log) { ThrowOnDestroy = true })
                .Add(new InitDestroyLog("b", log) { ThrowOnDestroy = true })
                .Add(new InitDestroyLog("c", log) { ThrowOnInit = true });

            var thrown = Assert.Throws<AggregateException>(() => systems.Init(), "a failed rollback must surface every error");

            Assert.That(thrown.InnerExceptions.Count, Is.EqualTo(3), "original plus two rollback errors");
            Assert.That(thrown.InnerExceptions[0].Message, Is.EqualTo("init failed: c"), "the original exception comes first");
            Assert.That(thrown.InnerExceptions[1].Message, Is.EqualTo("destroy failed: b"), "then rollback errors, in rollback order");
            Assert.That(thrown.InnerExceptions[2].Message, Is.EqualTo("destroy failed: a"), "a throwing Destroy does not stop the rollback");
            Assert.That(systems.IsInitialized, Is.False, "the runner stays uninitialized");
        }

        [Test]
        public void FailedInit_RollbackThrowsInsideEarlierGroup_StillDestroysRestOfGroup() {
            var world = new World();
            var log = new List<string>();
            var group = new SystemsRunner(world)
                .Add(new InitDestroyLog("g1", log))
                .Add(new InitDestroyLog("g2", log) { ThrowOnDestroy = true });
            var root = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log))
                .AddGroup("grp", group)
                .Add(new InitDestroyLog("f", log) { ThrowOnInit = true });

            var thrown = Assert.Throws<AggregateException>(() => root.Init(), "the rollback error must surface with the original");

            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:g1", "init:g2", "init:f", "destroy:g2", "destroy:g1", "destroy:a" }),
                "a throwing Destroy inside a group must not stop the rollback of the rest of that group (2.2.0-rc skipped g1)");
            Assert.That(thrown.InnerExceptions.Count, Is.EqualTo(2), "original plus the one rollback error");
            Assert.That(thrown.InnerExceptions[0].Message, Is.EqualTo("init failed: f"));
            Assert.That(thrown.InnerExceptions[1].Message, Is.EqualTo("destroy failed: g2"));
            Assert.That(group.IsInitialized || root.IsInitialized, Is.False, "no runner stays initialized");
        }

        [Test]
        public void FailedInit_RollbackThrowsInsideDeeplyNestedRunner_StillDestroysEverything() {
            var world = new World();
            var log = new List<string>();
            var inner = new SystemsRunner(world)
                .Add(new InitDestroyLog("i1", log))
                .Add(new InitDestroyLog("i2", log) { ThrowOnDestroy = true });
            var outer = new SystemsRunner(world)
                .Add(new InitDestroyLog("o1", log))
                .Add(inner)
                .Add(new InitDestroyLog("o2", log) { ThrowOnDestroy = true });
            var root = new SystemsRunner(world)
                .AddPhase("outer", outer)
                .Add(new InitDestroyLog("f", log) { ThrowOnInit = true });

            var thrown = Assert.Throws<AggregateException>(() => root.Init());

            Assert.That(log, Is.EqualTo(new[] {
                "init:o1", "init:i1", "init:i2", "init:o2", "init:f",
                "destroy:o2", "destroy:i2", "destroy:i1", "destroy:o1"
            }), "every level is walked to the end despite throwing Destroys");
            Assert.That(thrown.InnerExceptions.Count, Is.EqualTo(3));
            Assert.That(inner.IsInitialized || outer.IsInitialized, Is.False);

            log.Clear();
            root.Init();
            Assert.That(log, Is.EqualTo(new[] { "init:o1", "init:i1", "init:i2", "init:o2", "init:f" }),
                "nested runners were reset by the rollback, so a retry initializes them again");
        }

        [Test]
        public void Warmup_FailedInit_RollsBack() {
            var world = new World();
            var log = new List<string>();
            var systems = new SystemsRunner(world)
                .Add(new InitDestroyLog("a", log))
                .Add(new InitDestroyLog("b", log) { ThrowOnInit = true });

            Assert.That(() => systems.Warmup(), Throws.InvalidOperationException, "Warmup runs Init, so it fails the same way");

            Assert.That(log, Is.EqualTo(new[] { "init:a", "init:b", "destroy:a" }), "and rolls back the same way");
        }
    }
}
