using System;
using System.IO;
using KenseiECS;

// Component, listener and system types shared by the contracts stand and by the repro programs it
// writes (Stand/contracts/repro compiles both this file and the generated repros), so a repro uses
// exactly the types the stand used. Only API that exists in 2.1.1 is referenced here.
namespace Stand.Contracts.Probe {
    public struct CA : IComponent { public int V; }
    public struct CB : IComponent { public int V; }
    public struct CC : IComponent { public int V; }
    public struct CD : IComponent { public int V; }
    public struct CE : IComponent { public int V; }
    public struct CF : IComponent { public int V; }

    // Never added in any scenario: "does not create the pool" checks look for its pool.
    public struct CZ : IComponent { public int V; }

    // Touched only by the extended state probe (a pool and a filter created after the call).
    public struct ProbeFresh : IComponent { public int V; }

    // A component with a reference field: needs a formatter to be saved.
    public struct CRef : IComponent { public string S; }

    // Growth-owner checks: two pools / two filters of the same kind.
    public struct GA : IComponent { public int V; }
    public struct GB : IComponent { public int V; }

    // Event payload for EventBuffer<Ev>.
    public struct Ev { public int V; }

    public interface IProbeListener {
        void Ping(int value);
    }

    public sealed class ProbeListener : IProbeListener, IFilterListener, IComponentListener<CA>, IWorldEventListener {
        public int Pings;
        public int Events;

        public void Ping(int value) { Pings++; }
        public void OnEntityAdded(Filter filter, int entityIndex) { Events++; }
        public void OnEntityRemoved(Filter filter, int entityIndex) { Events++; }
        public void OnAdded(int entityIndex, ref CA component) { Events++; }
        public void OnRemoved(int entityIndex, ref CA component) { Events++; }
        public void OnEntityCreated(int entityIndex) { Events++; }
        public void OnEntityDestroyed(int entityIndex) { Events++; }
        public void OnComponentAdded(int entityIndex, int typeIndex) { Events++; }
        public void OnComponentRemoved(int entityIndex, int typeIndex) { Events++; }
    }

    public sealed class ProbeConfig {
        public int V;
    }

    // Counts its lifecycle calls; the runner probes read the counts.
    public sealed class ProbeSystem : IInitSystem, IRunSystem, IDestroySystem {
        public int Inits;
        public int Runs;
        public int Destroys;

        public void Init(World world, SharedData shared) { Inits++; }
        public void Run(World world) { Runs++; }
        public void Destroy(World world) { Destroys++; }
    }

    public sealed class InitOnlySystem : IInitSystem {
        public int Inits;
        public void Init(World world, SharedData shared) { Inits++; }
    }

    // Owning-group checks of the curated Group<...>() cases (Stand/contracts/src/Matrix.cs): what a group promises
    // (groups.md: membership is exact, the dense arrays of its pools stay aligned; a pool belongs to one group) asked
    // through the public API only, so the repros can call them too.
    public static class GroupChecks {
        // The members of Group<CC, CD> sit at the same dense index of both pools, and Data1/Data2 are their components.
        public static string Aligned(World w, Group<CC, CD> g) {
            var entities = g.Entities;
            var d1 = g.Data1;
            var d2 = g.Data2;
            var p1 = w.Pool<CC>();
            var p2 = w.Pool<CD>();
            for (int i = 0; i < g.Count; i++) {
                int e = entities[i];
                if (p1.RawEntities[i] != e || p2.RawEntities[i] != e) {
                    return "misaligned at " + i + ": CC holds " + p1.RawEntities[i] + ", CD holds " + p2.RawEntities[i] + ", the member is " + e;
                }
                if (d1[i].V != p1.Get(e).V || d2[i].V != p2.Get(e).V) {
                    return "Data at " + i + " is not the component of member " + e;
                }
            }
            return "aligned, " + g.Count + " members";
        }

        // A new member of Group<CC, CD> that also gets CE (a type no registered group owns): the group must stay aligned.
        public static string AlignedAfterNewMember(World w, Group<CC, CD> g) {
            var e = w.CreateEntity(new CC { V = 40 });
            w.Add(e, new CD { V = 41 });
            w.Add(e, new CE { V = 42 });
            return Aligned(w, g);
        }

        // CE and CF: types no registered group owns unless a group lists them; a group over them must be creatable then.
        public static string FreeTypesGroupable(World w) {
            try {
                var g = w.Group<CE, CF>();
                return "registered, " + g.Count + " members";
            } catch (InvalidOperationException ex) {
                for (int i = 0; i < w.GroupCount; i++) {
                    var group = w.GetGroup(i);
                    for (int k = 0; k < group.PoolCount; k++) {
                        var t = group.GetPool(k).ComponentType;
                        if (t == typeof(CE) || t == typeof(CF)) {
                            return "owned by the registered group " + i;
                        }
                    }
                }
                return "throws although no registered group owns CE or CF: " + ex.Message;
            }
        }
    }

    public sealed class CRefFormatter : IComponentFormatter<CRef> {
        public void Write(BinaryWriter writer, ref CRef component) {
            writer.Write(component.S ?? string.Empty);
        }

        public void Read(BinaryReader reader, out CRef component) {
            component = new CRef { S = reader.ReadString() };
        }
    }
}
