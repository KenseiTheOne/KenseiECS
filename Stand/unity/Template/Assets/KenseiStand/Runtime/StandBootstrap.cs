// KenseiECS test stand, module unity: the EcsBootstrap of the stand scene (next to StandGame on one
// GameObject). In a file of its own: Unity serializes a reference to a MonoBehaviour through the
// MonoScript of the file named like the class; a second MonoBehaviour in StandGame.cs ended up as a
// MonoScript embedded in the scene, which a player build may not resolve ("missing script").
using KenseiECS;

namespace KenseiStand {
    /// <summary>Counts how often the fixed and late runners of the bootstrap ran.</summary>
    public sealed class StandCountSystem : IRunSystem {
        private readonly bool _fixed;

        public StandCountSystem(bool isFixed) {
            _fixed = isFixed;
        }

        public void Run(World world) {
            if (_fixed) {
                StandBootstrap.FixedRuns++;
            } else {
                StandBootstrap.LateRuns++;
            }
        }
    }

    /// <summary>The package's Unity entry point, driven by Update, FixedUpdate and LateUpdate in the player.</summary>
    public sealed class StandBootstrap : EcsBootstrap {
        public static int FixedRuns;
        public static int LateRuns;

        protected override void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared) {
            update.Add(new StandMoveSystem());
            fixedUpdate.Add(new StandCountSystem(true));
            lateUpdate.Add(new StandCountSystem(false));
        }
    }
}
