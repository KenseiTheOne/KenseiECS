// Catalogue of the rules the module checks: id, class, estimated severity, the documented
// contract it rests on, and the mapping to the findings known from the 2.2.0 review (R1–R4).
// Whether a violation of a rule is a defect or a contract question for the tree under test is
// decided by the contract profile (Profile.cs), not here.
using System.Collections.Generic;

namespace Stand.Runner {
    public sealed class RuleInfo {
        public string Id;
        public string Class;
        public string Severity;
        public string Title;
        public string Contract;
        public string Known;
        /// <summary> What the reader must know before acting on the finding (documents that disagree, a debug check behind it). </summary>
        public string Caveat;
    }

    public static class Rules {
        public const string RollbackDestroysNoInit = "RUNNER-ROLLBACK-DESTROYS-INITLESS";
        public const string RollbackAggregate = "RUNNER-ROLLBACK-AGGREGATE-STRUCTURE";
        public const string RollbackRethrow = "RUNNER-ROLLBACK-NOT-RETHROWN";
        public const string RollbackMissingDestroy = "RUNNER-ROLLBACK-MISSING-DESTROY";
        public const string RollbackExtraDestroy = "RUNNER-ROLLBACK-EXTRA-DESTROY";
        public const string RollbackOrder = "RUNNER-ROLLBACK-ORDER";
        public const string InitSequence = "RUNNER-INIT-SEQUENCE";
        public const string InitSwallowed = "RUNNER-INIT-EXCEPTION-SWALLOWED";
        public const string InitException21 = "RUNNER-INIT-EXCEPTION-21";
        public const string InitState = "RUNNER-INITIALIZED-STATE";
        public const string DestroySequence = "RUNNER-DESTROY-SEQUENCE";
        public const string DestroyNotNoop = "RUNNER-DESTROY-NOT-NOOP";
        public const string SharedSlots = "RUNNER-SHARED-RUNNER-SLOTS";
        public const string SharedRunnerAccepted = "RUNNER-SHARED-RUNNER-ACCEPTED";
        public const string SharedRunnerRejectState = "RUNNER-SHARED-RUNNER-REJECT-STATE";
        public const string GroupStartState = "RUNNER-GROUP-START-STATE";
        public const string ConsistInfoEnabled = "RUNNER-CONSIST-INFO-ENABLED";
        public const string ConsistIsActive = "RUNNER-CONSIST-ISACTIVE";
        public const string ConsistChildEnabled = "RUNNER-CONSIST-CHILD-ENABLED";
        public const string ConsistIndexOfName = "RUNNER-CONSIST-INDEXOF-NAME";
        public const string ConsistIndexOfSystem = "RUNNER-CONSIST-INDEXOF-SYSTEM";
        public const string ConsistGetRunner = "RUNNER-CONSIST-GETRUNNER";
        public const string InfoStatic = "RUNNER-SYSTEMINFO-STATIC";
        public const string RunDisabledRan = "RUNNER-RUN-DISABLED-RAN";
        public const string RunEnabledSkipped = "RUNNER-RUN-ENABLED-SKIPPED";
        public const string RunOrder = "RUNNER-RUN-ORDER";
        public const string RunException = "RUNNER-RUN-EXCEPTION";
        public const string Tick = "RUNNER-TICK";
        public const string OneFramePrefix = "RUNNER-ONEFRAME-";
        public const string DebugCheckPrefix = "RUNNER-DEBUG-CHECK-";
        public const string UnexpectedPrefix = "RUNNER-UNEXPECTED-EXCEPTION-";
        public const string UnexpectedCall = "RUNNER-UNEXPECTED-SYSTEM-CALL";
        public const string ReleaseResult = "RUNNER-RELEASE-UNKNOWN-NAME-RESULT";
        public const string BuildThrew = "RUNNER-REGISTRATION-THREW";
        public const string DuplicateNameRelease = "RUNNER-DUPLICATE-NAME-RELEASE-LOOKUP";
        public const string DuplicateNameDebug = "RUNNER-DUPLICATE-NAME-DEBUG-NOT-THROWN";
        public const string AddAfterInitDebug = "RUNNER-ADD-AFTER-INIT-DEBUG";
        public const string InitArgs = "RUNNER-INIT-ARGS";
        public const string SharedProperty = "RUNNER-SHARED-PROPERTY-STALE";
        public const string SharedPropertyTwoSlots = "RUNNER-SHARED-PROPERTY-TWO-SLOTS";
        public const string NestedArgs = "RUNNER-NESTED-ARGS-NOT-CHECKED";
        public const string BootstrapNameCollision = "BOOTSTRAP-PHASE-NAME-COLLISION-DEBUG";
        public const string BootstrapReservedName = "BOOTSTRAP-RESERVED-NAME-ERROR";
        public const string BootstrapLifecycle = "BOOTSTRAP-LIFECYCLE";
        public const string BootstrapGuard = "BOOTSTRAP-ISINITIALIZED-GUARD";

        private static readonly Dictionary<string, RuleInfo> Catalogue = new Dictionary<string, RuleInfo>();

        static Rules() {
            Add(RollbackDestroysNoInit, "contract-violation", "medium", known: "R1",
                title: "The Init rollback calls Destroy on a system without IInitSystem (IDestroySystem only, or IRunSystem + IDestroySystem)",
                contract: "ISystem.cs: IDestroySystem is \"Called once during SystemsRunner.Destroy()\", Destroy is \"Called once on shutdown\". runner.md: the rollback exists so that " +
                          "\"systems that subscribed to events or allocated in Init can undo it\"; the second Destroy of \"Changed in 2.2.0\" is described for systems \"destroyed by the " +
                          "rollback and initialized again by the retry\", and such a system has no Init. Over the cycle \"failed Init, Init, Destroy\" it gets Destroy twice and Init never. " +
                          "2.2.2, runner.md: \"the systems whose Init completed before the failure get Destroy, if they implement IDestroySystem, in reverse order ... neither is a system without " +
                          "IInitSystem: it initialized nothing, and gets its Destroy once, from Destroy() after a completed Init\"; ISystem.cs: \"A system without IInitSystem is never destroyed by a rollback\".",
                caveat: "Up to 2.2.1 the documents disagree: the general sentence of the rollback (\"every IDestroySystem registered before the failed system gets Destroy\", runner.md " +
                        "and the XML of Init) literally includes such systems, and V220RunnerTests.FailedInit_DestroysEarlierSystemsInReverse_AndRethrowsOriginal pins the call. " +
                        "The code follows that sentence and the test. Hence a contract question up to 2.2.1, a defect from 2.2.2 on.");
            Add(RollbackAggregate, "contract-violation", "low", known: "R2",
                title: "In a nested rollback the AggregateExceptions nest: InnerExceptions[0] is not the original exception",
                contract: "runner.md (Lifecycle contract): \"Init throws an AggregateException whose first inner exception is the original one, followed by the rollback errors\"; " +
                          "XML Init(World, SharedData): \"an AggregateException holds the original exception first, then the rollback ones\"; CHANGELOG 2.2.0 says the same. " +
                          "2.2.2: \"one flat AggregateException holds the original exception first, then every rollback exception, at any nesting depth\" (XML), \"never an AggregateException nested in another\" (runner.md).");
            Add(RollbackRethrow, "contract-violation", "medium",
                title: "A rollback without Destroy errors does not rethrow the original exception as is",
                contract: "runner.md: \"The runner stays uninitialized and the original exception is rethrown\".");
            Add(RollbackMissingDestroy, "contract-violation", "high",
                title: "The Init rollback did not destroy a system whose Init completed before the failed one",
                contract: "runner.md: \"every IDestroySystem registered before the failed system gets Destroy, in reverse order\"; CHANGELOG 2.2.0: \"each of their systems is destroyed even when another one throws\".");
            Add(RollbackExtraDestroy, "contract-violation", "high",
                title: "The Init rollback destroyed the failed system or a system after it",
                contract: "runner.md: \"The failed system and the ones after it are not destroyed\".");
            Add(RollbackOrder, "contract-violation", "medium",
                title: "The Init rollback does not destroy in reverse registration order",
                contract: "runner.md: \"every IDestroySystem registered before the failed system gets Destroy, in reverse order\".");
            Add(InitSequence, "contract-violation", "high",
                title: "Init does not initialize the systems in registration order, does not start over after a rollback, or initializes one twice",
                contract: "runner.md: \"Init runs each IInitSystem once, in registration order\"; 2.2: \"The next Init starts over from the first system\"; 2.1: \"the next Init resumes with the system that failed\".");
            Add(InitSwallowed, "contract-violation", "high",
                title: "The exception of a system's Init did not reach the caller",
                contract: "runner.md: \"the original exception is rethrown\".");
            Add(InitException21, "contract-violation", "medium",
                title: "2.1: Init did not rethrow the original exception",
                contract: "runner.md 2.1.1: \"If one throws, the runner stays uninitialized and the next Init resumes with the system that failed\".");
            Add(InitState, "contract-violation", "high",
                title: "IsInitialized disagrees with the contract (after Init, a rollback or Destroy)",
                contract: "XML IsInitialized: \"True after Init completed for every IInitSystem\"; runner.md: \"The runner stays uninitialized\"; Destroy \"resets the runner so Init can run again\".");
            Add(DestroySequence, "contract-violation", "high",
                title: "Destroy does not call Destroy of every system exactly once, in reverse order",
                contract: "runner.md: \"Destroy runs IDestroySystem in reverse registration order\"; ISystem.cs: \"Called once on shutdown, in reverse registration order\".");
            Add(DestroyNotNoop, "contract-violation", "high",
                title: "Destroy of an uninitialized runner (also after a failed Init) destroyed something",
                contract: "runner.md: \"Destroy ... is a no-op before Init (and after a failed one, which already rolled back)\".");
            Add(SharedSlots, "contract-violation", "medium", known: "R3",
                title: "A runner in two slots: SetActive by index or name of one slot switches it off everywhere, while the other slots report \"enabled\"",
                contract: "runner.md (IsEnabled of a nested runner): for a group IsEnabled \"always equals the group's slot in the parent ... GetSystemInfo(index).IsEnabled, IsActive(name) " +
                          "and child.IsEnabled report the same value\"; XML SystemInfo.IsEnabled: \"a group's own IsEnabled follows the slot\".",
                caveat: "Up to 2.2.1 the documents describe registering one runner in two slots neither as allowed nor as forbidden, and the package accepts it in every build. " +
                        "The consistency invariant is broken: the slot that reports \"enabled\" does not run its group (whose IsEnabled is already false). 2.2.2 forbids the double " +
                        "registration (" + SharedRunnerAccepted + ").");
            Add(SharedRunnerAccepted, "contract-violation", "medium", known: "R3",
                title: "2.2.2: registering a runner that already sits in a slot did not throw",
                contract: "2.2.2, XML SystemsRunner: \"A runner can be registered only once: in one slot of one parent. A second registration, in the same parent or another one, and adding a " +
                          "runner to itself or to a runner nested in it throw InvalidOperationException in every build\"; runner.md: \"a second registration of a runner (or one inside itself), throw in every build\".");
            Add(SharedRunnerRejectState, "contract-violation", "low",
                title: "2.2.2: the rejected second registration of a runner left an entry in the parent",
                contract: "2.2.2 forbids the registration; the CHANGELOG says \"A rejected call registers nothing\", the XML and runner.md do not say what a rejected call leaves.");
            Add(GroupStartState, "contract-violation", "medium",
                title: "A group switched off with SetActive(false) before its registration starts enabled (group and slot both switched back on)",
                contract: "2.2.1 XML AddGroup: \"The slot starts as the group is at registration: a group switched off with SetActive(false) before AddGroup is added disabled (so is an " +
                          "unnamed Add(runner))\"; runner.md 2.2.1 and CHANGELOG 2.2.1 say the same. 2.2.0 only promises that the slot and the group's IsEnabled agree.");
            Add(ConsistInfoEnabled, "contract-violation", "medium",
                title: "GetSystemInfo(i).IsEnabled disagrees with the state of the slot or phase",
                contract: "XML SystemInfo.IsEnabled: \"its Run slot for runnable systems and inline groups ..., the phase's own IsEnabled for a phase; false otherwise\".");
            Add(ConsistIsActive, "contract-violation", "medium",
                title: "IsActive(name) disagrees with the state of the slot or phase",
                contract: "XML IsActive: \"Check if a named run system, inline group or phase is enabled\"; runner.md: IsActive(name) and GetSystemInfo(index).IsEnabled \"report the same value\".");
            Add(ConsistChildEnabled, "contract-violation", "medium",
                title: "child.IsEnabled disagrees with the contract",
                contract: "2.2 runner.md: for a group \"it always equals the group's slot\"; 2.1 runner.md: for an unnamed group \"child.IsEnabled keeps returning true\".");
            Add(ConsistIndexOfName, "contract-violation", "low",
                title: "IndexOf(name) returns the wrong position",
                contract: "XML IndexOf(string): the position of the system with that name; an explicit name resolves as in SetActive/IsActive; otherwise the first unnamed system with that SystemInfo.Name; -1 when there is none.");
            Add(ConsistIndexOfSystem, "contract-violation", "low",
                title: "IndexOf(system) does not return the registration position of that instance",
                contract: "XML IndexOf(ISystem): \"Position of the system in registration order ... Compares by reference\".");
            Add(ConsistGetRunner, "contract-violation", "medium",
                title: "GetRunner(name) returns the wrong runner",
                contract: "XML GetRunner: \"Get a named nested SystemsRunner: a phase ... or a named inline group\".");
            Add(InfoStatic, "contract-violation", "low",
                title: "SystemInfo (Name, IsRunnable, ChildRunner, IsSeparatePhase, System) or SystemCount does not match the registrations",
                contract: "XML SystemInfo.*: Name is the registration name or the type name; IsRunnable: part of Run (not a phase); IsSeparatePhase: a phase.");
            Add(RunDisabledRan, "contract-violation", "high",
                title: "Run executed a disabled system",
                contract: "runner.md: \"Run executes enabled systems in order\"; XML SetActive: \"Disabled systems and groups are skipped during Run()\".");
            Add(RunEnabledSkipped, "contract-violation", "high",
                title: "Run skipped an enabled system",
                contract: "runner.md: \"Run executes enabled systems in order\".");
            Add(RunOrder, "contract-violation", "high",
                title: "Run does not execute the systems in registration order",
                contract: "XML SystemsRunner: \"executes them in declared order\".");
            Add(RunException, "contract-violation", "medium",
                title: "The exception of a system's Run did not reach the caller as is",
                contract: "runner.md: \"Cleanup runs even if a system throws (systems after the failing one are skipped that frame)\".");
            Add(Tick, "contract-violation", "medium",
                title: "The world tick advances other than by Run() of the root runner",
                contract: "XML Run(): \"On the root runner increments the tick counter ... Child runners do not advance the tick\"; runner.md: \"a disabled root's Run still advances the tick\".");
            Add(UnexpectedCall, "contract-violation", "high",
                title: "An operation unrelated to the lifecycle called Init, Run or Destroy of a system",
                contract: "SetActive, IsActive and GetRunner only switch and read flags.");
            Add(ReleaseResult, "contract-violation", "low",
                title: "Release, unknown name: IsActive is not false or GetRunner is not null",
                contract: "XML IsActive/GetRunner: \"An unknown name ... returns false in release\", \"Unknown names return null (throw under KENSEI_DEBUG)\".");
            Add(BuildThrew, "contract-violation", "high",
                title: "A valid registration threw",
                contract: "Registering systems, groups and phases with unique names is valid in every build.");
            Add(DuplicateNameRelease, "contract-violation", "medium",
                title: "Release: a registration under a name already used in the runner is not the one the name lookup reaches (SetActive/IsActive/IndexOf/GetRunner)",
                contract: "runner.md 2.2.0/2.2.1: \"in release the second one silently replaces the first in the name lookup (the first keeps running, but only SetActive(int, bool) can reach it)\"; " +
                          "runner.md 2.1: \"registering a second system under the same name silently replaces the first in the name lookup\". In EcsBootstrap (release) a user system " +
                          "\"fixed\" in update makes Systems.SetActive(\"fixed\", false) switch that system off instead of FixedUpdateSystems, while GetRunner(\"fixed\") returns the phase. " +
                          "2.2.2 documents that split: \"SetActive, IsActive and IndexOf(name) reach the last run system or inline group registered under the name, and a phase only when no " +
                          "system or group has it; GetRunner reaches the last group or phase\" (runner.md), \"The name then refers to two registrations\" (bootstrap.md).");
            Add(DuplicateNameDebug, "contract-violation", "medium",
                title: "KENSEI_DEBUG: a repeated name in one runner does not throw",
                contract: "CHANGELOG 2.2.0 / runner.md: \"under KENSEI_DEBUG registering a second system, group or phase under a name already used in that runner throws ... DelHere entries are exempt\".");
            Add(AddAfterInitDebug, "contract-violation", "low",
                title: "KENSEI_DEBUG: Add after Init does not throw",
                contract: "runner.md: \"Add after Init throws under KENSEI_DEBUG\".");
            Add(InitArgs, "contract-violation", "high",
                title: "A system got another World or SharedData in Init than the root's",
                contract: "XML of the constructor: \"a nested runner created this way inherits its parent's SharedData when added\"; runner.md: \"a child constructed without SharedData " +
                          "inherits the parent's\"; bootstrap.md: \"they are constructed without their own SharedData and inherit the root's — every system gets the same shared\".");
            Add(SharedProperty, "contract-violation", "low",
                title: "The Shared property of a nested runner is not the SharedData its systems get in Init (the runner joined its group before the group joined its parent)",
                contract: "XML SystemsRunner.Shared: \"Shared data passed to systems in Init\". Constructor: \"a nested runner created this way inherits its parent's SharedData when added\" — " +
                          "a grandchild added to a group before the group joins the root inherits the group's own SharedData, and the group gets the root's later.",
                caveat: "The systems are not affected: Init passes the root's SharedData down the chain of Init parameters. Only the Shared property of the nested runner differs (read by " +
                        "tools and by code that takes GetRunner(name).Shared): it is a separate object whose content the systems never see. Every fluent tree of depth 2+ is built " +
                        "this way, and so is every group added to fixedUpdate/lateUpdate inside EcsBootstrap.Configure (the phases join the root after Configure).");
            Add(SharedPropertyTwoSlots, "contract-violation", "low", known: "R3",
                title: "A runner registered in two slots: its Shared property is not the SharedData its systems get in Init",
                contract: "XML SystemsRunner.Shared: \"Shared data passed to systems in Init\". Up to 2.2.1 no document says whether one runner may sit in two slots; 2.2.2 forbids it " +
                          "(\"A runner can be registered only once\").",
                caveat: "The same mechanism as " + SharedProperty + " in a setup the documents of 2.2.0 and 2.2.1 neither allow nor forbid: a contract question there, impossible from 2.2.2 on.");
            Add(NestedArgs, "contract-violation", "low",
                title: "AddGroup/AddPhase with a null runner or an empty name does not throw",
                contract: "CHANGELOG 2.2.0 / runner.md: \"AddPhase/AddGroup throw for a null runner or an empty name in every build\".");
            Add(BootstrapNameCollision, "crash", "high", known: "R4",
                title: "EcsBootstrap: a user system or group named \"fixed\" or \"late\" in update makes Awake throw under KENSEI_DEBUG; the phases are not attached and FixedUpdate/LateUpdate throw every frame",
                contract: "bootstrap.md: Awake \"calls Configure, then attaches the fixed and late runners to the update runner as the named phases \"fixed\" and \"late\"\"; the names " +
                          "\"fixed\" and \"late\" are not declared reserved anywhere; tip \"Why the IsInitialized guard\": without it the phases \"throw every frame under KENSEI_DEBUG\" — here the guard does not help.",
                caveat: "What throws is the documented KENSEI_DEBUG check of repeated names: the user's name and the bootstrap's phase land in one runner. Up to 2.2.1 no document says " +
                        "the names are taken, so this is a contract question; 2.2.2 turns it into a clear error (" + BootstrapReservedName + ").");
            Add(BootstrapReservedName, "contract-violation", "medium", known: "R4",
                title: "2.2.2: a user system or group named \"fixed\" or \"late\" in EcsBootstrap's update runner does not get a clear error naming the reserved names",
                contract: "2.2.2, bootstrap.md (Reserved names): \"both are reserved in the update runner: do not register a system, group or phase there under either of them\"; " +
                          "\"Under KENSEI_DEBUG, Awake throws InvalidOperationException right after Configure, naming the system: ... EcsBootstrap reserves \"fixed\" and \"late\" there\"; \"In release nothing checks it\".");
            Add(BootstrapLifecycle, "contract-violation", "high",
                title: "EcsBootstrap does not follow its documented lifecycle",
                contract: "bootstrap.md (Lifecycle): Awake, Start, Update, FixedUpdate, LateUpdate, OnDestroy.");
            Add(BootstrapGuard, "contract-violation", "high",
                title: "EcsBootstrap: the phases still run after a failed Start",
                contract: "bootstrap.md: Update/FixedUpdate/LateUpdate \"Runs the matching runner, but only once Systems.IsInitialized is true\".");
        }

        private static void Add(string id, string cls, string severity, string title, string contract, string known = null, string caveat = null) {
            Catalogue[id] = new RuleInfo { Id = id, Class = cls, Severity = severity, Title = title, Contract = contract, Known = known, Caveat = caveat };
        }

        public static RuleInfo Get(string id) {
            if (Catalogue.TryGetValue(id, out var info)) {
                return info;
            }
            if (id.StartsWith(OneFramePrefix)) {
                return new RuleInfo {
                    Id = id, Class = "contract-violation", Severity = "medium",
                    Title = "OneFrame cleanup breaks the contract (" + id.Substring(OneFramePrefix.Length) + ")",
                    Contract = "XML OneFrame<T>() / OneFrame<T>(cleanWhenDisabled), runner.md \"Disabled runners and their OneFrame components\", DelHere."
                };
            }
            if (id.StartsWith(DebugCheckPrefix)) {
                return new RuleInfo {
                    Id = id, Class = "contract-violation", Severity = "low",
                    Title = "A KENSEI_DEBUG check did not fire (" + id.Substring(DebugCheckPrefix.Length) + ")",
                    Contract = "XML SetActive/IsActive/GetRunner: an unknown name and a system without Run throw under KENSEI_DEBUG; runner.md: \"Run before Init throws under KENSEI_DEBUG\", " +
                               "\"adding, initializing or running a child constructed with a different World (or an explicitly passed different SharedData) throws\"."
                };
            }
            if (id.StartsWith(UnexpectedPrefix)) {
                return new RuleInfo {
                    Id = id, Class = "crash", Severity = "high",
                    Title = "Unexpected exception (" + id.Substring(UnexpectedPrefix.Length) + ")",
                    Contract = "The operation is valid under the contract and must not throw."
                };
            }
            return new RuleInfo { Id = id, Class = "contract-violation", Severity = "medium", Title = id, Contract = "" };
        }
    }
}
