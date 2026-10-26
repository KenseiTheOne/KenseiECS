// KenseiECS test stand, module unity: the minimal runtime script of the scratch project.
// Lives in Assembly-CSharp (no asmdef) and references KenseiECS like game code does, so every player
// and content compile of the stand compiles KenseiECS together with a user of it. Uses only API that
// exists since 2.1.1, plus the source generator (partial system with injected fields) and EcsBootstrap
// (StandBootstrap.cs: a MonoBehaviour needs a file of its own name, or the scene cannot reference it).
using System.Collections;
using KenseiECS;
using UnityEngine;

namespace KenseiStand {
    public struct StandPosition : IComponent {
        public float X;
    }

    public struct StandVelocity : IComponent {
        public float X;
    }

    // No hand-written Init: the generator in Plugins/KenseiECS.Generators.dll writes it (and adds
    // IInitSystem). Without the generator the fields stay null and Injected is false.
    public partial class StandMoveSystem : IRunSystem {
        [Inc(typeof(StandPosition), typeof(StandVelocity))] private Filter _moving;
        [Pool] private ComponentPool<StandPosition> _positions;
        [Pool] private ComponentPool<StandVelocity> _velocities;

        public bool Injected => _moving != null && _positions != null && _velocities != null;

        public void Run(World world) {
            foreach (int e in _moving) {
                _positions.Get(e).X += _velocities.Get(e).X;
            }
        }
    }

    public sealed class StandGame : MonoBehaviour {
        private IEnumerator Start() {
            if (Application.isEditor) {
                yield break;
            }
            string result;
            try {
                result = Smoke();
            } catch (System.Exception e) {
                result = "FAIL exception " + e.GetType().Name + ": " + e.Message;
            }
            var boot = GetComponent<StandBootstrap>();
            if (result.StartsWith("OK") && boot != null) {
                Entity moving = default;
                string error = null;
                try {
                    moving = boot.World.CreateEntity(new StandPosition { X = 0f });
                    boot.World.Add(moving, new StandVelocity { X = 1f });
                } catch (System.Exception e) {
                    error = "FAIL EcsBootstrap world: " + e.GetType().Name + ": " + e.Message;
                }
                if (error == null) {
                    for (int i = 0; i < 10; i++) {
                        yield return null;
                    }
                    yield return new WaitForSeconds(0.25f);
                    float x = boot.World.Get<StandPosition>(moving).X;
                    if (x < 5f) {
                        error = "FAIL EcsBootstrap: the Update runner did not move the entity (x=" + x + ")";
                    } else if (StandBootstrap.FixedRuns == 0) {
                        error = "FAIL EcsBootstrap: the FixedUpdate runner never ran";
                    } else if (StandBootstrap.LateRuns == 0) {
                        error = "FAIL EcsBootstrap: the LateUpdate runner never ran";
                    } else {
                        result += "; bootstrap x=" + x + " fixed=" + StandBootstrap.FixedRuns + " late=" + StandBootstrap.LateRuns;
                    }
                }
                if (error != null) {
                    result = error;
                }
            } else if (boot == null) {
                result += "; no StandBootstrap in the scene";
            }
            Debug.Log("[KENSEI-STAND-PLAYER] " + result);
            Application.Quit(result.StartsWith("OK") ? 0 : 3);
        }

        /// <summary>
        /// Creates a world, runs the generated system and checks the result. Returns "OK ..." or
        /// "FAIL reason". Called by the stand in the editor (StandRunner, case E3) and by the player.
        /// </summary>
        public static string Smoke() {
            var world = new World();
            var systems = new SystemsRunner(world);
            var move = new StandMoveSystem();
            systems.Add(move);
            systems.Init();
            if (!(move is IInitSystem)) {
                return "FAIL StandMoveSystem has no generated Init (not an IInitSystem)";
            }
            if (!move.Injected) {
                return "FAIL the generated Init did not inject the [Inc]/[Pool] fields";
            }

            var a = world.CreateEntity(new StandPosition { X = 1f });
            world.Add(a, new StandVelocity { X = 2f });
            var b = world.CreateEntity(new StandPosition { X = 10f });
            systems.Run();
            systems.Run();
            float ax = world.Get<StandPosition>(a).X;
            float bx = world.Get<StandPosition>(b).X;
            if (ax != 5f || bx != 10f) {
                return "FAIL positions after two Run: a=" + ax + " (expected 5), b=" + bx + " (expected 10)";
            }

            string mode = "release";
#if KENSEI_DEBUG
            mode = "debug";
            // docs/guides/debug-mode.md: under KENSEI_DEBUG, Get with a dead entity throws.
            world.DestroyEntity(b);
            bool threw = false;
            try {
                world.Get<StandPosition>(b);
            } catch (System.Exception) {
                threw = true;
            }
            if (!threw) {
                return "FAIL KENSEI_DEBUG: Get on a destroyed entity did not throw";
            }
#endif
            systems.Destroy();
            world.Destroy();
            return "OK " + mode + " a.X=" + ax;
        }
    }
}
