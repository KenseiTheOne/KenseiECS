// KenseiECS test stand, module unity: EcsEntityView.Spawn while a world listener spawns another view
// (case E5). Lives in Assembly-CSharp and uses the package like game code does; the editor side
// (StandCases.EditorSpawn) calls Run() through reflection and judges the JSON it returns.
//
// docs/unity/entity-view.md: "Spawn creates the entity from the providers in component order, binds the
// view to it and returns it"; docs/guides/world-events.md: "Listeners may modify the world". The outer
// view has three providers (A, B, C). The first provider creates the entity, which raises
// OnEntityCreated; the listener spawns another view there (one provider, Inner). After both Spawn calls
// the outer entity must have A, B and C, and the inner one Inner only. The control is the same outer
// Spawn without the listener. Uses only API that exists since 2.1.1.
using System;
using System.Collections.Generic;
using KenseiECS;
using UnityEngine;

namespace KenseiStand {
    [Serializable]
    public struct StandSpawnA : IComponent {
        public int Value;
    }

    [Serializable]
    public struct StandSpawnB : IComponent {
        public int Value;
    }

    [Serializable]
    public struct StandSpawnC : IComponent {
        public int Value;
    }

    [Serializable]
    public struct StandSpawnInner : IComponent {
        public int Value;
    }

    /// <summary>What the check saw; the editor reads it as KenseiStand.Editor.SpawnReport (same fields).</summary>
    [Serializable]
    public sealed class StandSpawnReport {
        public List<string> plain = new List<string>();
        public List<string> outer = new List<string>();
        public List<string> inner = new List<string>();
        public List<string> expected = new List<string> { "StandSpawnA", "StandSpawnB", "StandSpawnC" };
        public bool listenerFired;
        public bool innerAlive;
        public string error = "";
    }

    // Spawns the other view from the first OnEntityCreated it sees: the creation of the outer entity, which
    // the first provider makes before the other providers add their components.
    internal sealed class StandSpawnOnCreate : IWorldEventListener {
        private readonly World _world;
        private readonly EcsEntityView _inner;
        public bool Fired;
        public Entity Inner;
        public string Error = "";

        public StandSpawnOnCreate(World world, EcsEntityView inner) {
            _world = world;
            _inner = inner;
        }

        public void OnEntityCreated(int entityIndex) {
            if (Fired) {
                return;
            }
            Fired = true;
            try {
                Inner = _inner.Spawn(_world);
            } catch (Exception e) {
                Error = "the inner Spawn threw " + e.GetType().Name + ": " + e.Message;
            }
        }

        public void OnEntityDestroyed(int entityIndex) { }

        public void OnComponentAdded(int entityIndex, int typeIndex) { }

        public void OnComponentRemoved(int entityIndex, int typeIndex) { }
    }

    public static class StandSpawnCheck {
        /// <summary>Runs the control and the nested Spawn; returns a StandSpawnReport as JSON.</summary>
        public static string Run() {
            var report = new StandSpawnReport();
            var created = new List<GameObject>();
            try {
                var plainWorld = new World();
                try {
                    var plainView = MakeOuter(created, "StandSpawnPlain");
                    report.plain = Components(plainWorld, plainView.Spawn(plainWorld));
                } finally {
                    plainWorld.Destroy();
                }

                var world = new World();
                try {
                    var outerView = MakeOuter(created, "StandSpawnOuter");
                    var innerObject = NewObject(created, "StandSpawnInnerView");
                    var innerView = innerObject.AddComponent<EcsEntityView>();
                    innerObject.AddComponent<StandSpawnInnerProvider>().Value = new StandSpawnInner { Value = 4 };
                    var listener = new StandSpawnOnCreate(world, innerView);
                    world.AddEventListener(listener);
                    Entity outer;
                    try {
                        outer = outerView.Spawn(world);
                    } catch (Exception e) {
                        report.listenerFired = listener.Fired;
                        report.error = "the outer Spawn threw " + e.GetType().Name + ": " + e.Message;
                        return JsonUtility.ToJson(report);
                    }
                    world.RemoveEventListener(listener);
                    report.listenerFired = listener.Fired;
                    report.error = listener.Error;
                    report.outer = Components(world, outer);
                    report.innerAlive = listener.Fired && world.IsAlive(listener.Inner);
                    if (report.innerAlive) {
                        report.inner = Components(world, listener.Inner);
                    }
                } finally {
                    world.Destroy();
                }
            } finally {
                foreach (var go in created) {
                    if (go != null) {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                }
            }
            return JsonUtility.ToJson(report);
        }

        // Not saved with any scene: the editor process of the stand never writes these objects.
        private static GameObject NewObject(List<GameObject> created, string name) {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            created.Add(go);
            return go;
        }

        private static EcsEntityView MakeOuter(List<GameObject> created, string name) {
            var go = NewObject(created, name);
            var view = go.AddComponent<EcsEntityView>();
            go.AddComponent<StandSpawnAProvider>().Value = new StandSpawnA { Value = 1 };
            go.AddComponent<StandSpawnBProvider>().Value = new StandSpawnB { Value = 2 };
            go.AddComponent<StandSpawnCProvider>().Value = new StandSpawnC { Value = 3 };
            return view;
        }

        private static List<string> Components(World world, Entity entity) {
            var list = new List<string>();
            if (!world.IsAlive(entity)) {
                return list;
            }
            if (world.Has<StandSpawnA>(entity)) {
                list.Add("StandSpawnA");
            }
            if (world.Has<StandSpawnB>(entity)) {
                list.Add("StandSpawnB");
            }
            if (world.Has<StandSpawnC>(entity)) {
                list.Add("StandSpawnC");
            }
            if (world.Has<StandSpawnInner>(entity)) {
                list.Add("StandSpawnInner");
            }
            return list;
        }
    }
}
