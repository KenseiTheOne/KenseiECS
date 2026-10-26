using System;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    // ------------------------------------------------------------------
    // Components, one kind per add/remove scenario.
    // ------------------------------------------------------------------
    public struct Pos : IComponent { public float X, Y; }
    public struct Vel : IComponent { public float X, Y; }
    public struct Carrier : IComponent { public int V; }
    public struct Tag : IComponent { }
    public struct Tag2 : IComponent { }
    public struct Frozen : IComponent { }
    public struct Big : IComponent { public long A, B, C, D, E, F, G, H; }
    public struct Managed : IComponent { public object Ref; public int V; }
    public struct Tracked : IComponent { public int V; }
    public struct Listened : IComponent { public int V; }
    public struct GroupA : IComponent { public int V; }
    public struct GroupB : IComponent { public int V; }
    public struct Filtered : IComponent { public int V; }
    public struct DenseProbe : IComponent { public int V; }
    // access.*: reads and writes in a steady frame.
    public struct AccessTracked : IComponent { public int V; }
    public struct AccessConfig : IComponent { public int V; }
    public struct AccessGA : IComponent { public int V; }
    public struct AccessGB : IComponent { public int V; }

    /// <summary> IAutoReset that clears a reference, allocates nothing. </summary>
    public struct Resettable : IComponent, IAutoReset<Resettable> {
        public int V;
        public object Ref;
        public void AutoReset(ref Resettable c) {
            c.V = 0;
            c.Ref = null;
        }
    }

    /// <summary> IAutoCopy that allocates nothing: the copy only counts its generation. </summary>
    public struct Copyable : IComponent, IAutoCopy<Copyable> {
        public int V;
        public int Copies;
        public void AutoCopy(ref Copyable c) {
            c.Copies++;
        }
    }

    // A pool first created by an Add in a frame, and one created at load (Reserve scenarios).
    // reserve.unreserved_type_grows: a type nothing reserved, in a world that reserved its hot types.
    public struct Cold : IComponent { public int V; }
    public struct Late : IComponent { public int V; public float W; }
    public struct LateLoad : IComponent { public int V; public float W; }

    // OneFrame markers of the runner scenarios.
    public struct Mark : IComponent { public int V; }
    public struct Mark2 : IComponent { public int V; }
    public struct Mark3 : IComponent { public int V; }

    // Event payloads: one type per scenario, so the static list pool of one scenario never feeds another.
    public struct EvAddRemove { public int V; }
    public struct EvPrewarm { public int V; }
    public struct EvNoPrewarm { public int V; }
    public struct EvCopy { public int V; }
    public struct EvWriteBack { public int V; }
    public struct EvWriteBackControl { public int V; }

    // ------------------------------------------------------------------
    // Listener bridge.
    // ------------------------------------------------------------------
    public interface IView {
        void OnHit(int damage);
    }

    public sealed class View : IView {
        public long Sum;
        public void OnHit(int damage) {
            Sum += damage;
        }
    }

    public sealed class CountingListener<T> : IComponentListener<T> where T : struct, IComponent {
        public long Count;
        public void OnAdded(int entityIndex, ref T component) { Count++; }
        public void OnRemoved(int entityIndex, ref T component) { Count--; }
    }

    // ------------------------------------------------------------------
    // Systems of the runner scenarios.
    // ------------------------------------------------------------------
    public sealed class MoveSystem : IInitSystem, IRunSystem {
        private Filter _filter;
        private ComponentPool<Pos> _positions;
        private ComponentPool<Vel> _velocities;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<Pos>().Inc<Vel>().End();
            _positions = world.Pool<Pos>();
            _velocities = world.Pool<Vel>();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                ref var p = ref _positions.Get(e);
                ref var v = ref _velocities.Get(e);
                p.X += v.X;
                p.Y += v.Y;
            }
        }
    }

    /// <summary> Adds a one-frame marker to the first <c>count</c> targets every frame. </summary>
    public sealed class MarkProducer<T> : IRunSystem where T : struct, IComponent {
        private readonly Entity[] _targets;
        private readonly int _count;

        public MarkProducer(Entity[] targets, int count) {
            _targets = targets;
            _count = count;
        }

        public void Run(World world) {
            for (int i = 0; i < _count; i++) {
                world.Add(_targets[i], default(T));
            }
        }
    }

    public sealed class CountSystem : IRunSystem {
        public long Runs;
        public void Run(World world) {
            Runs++;
        }
    }

    // ------------------------------------------------------------------
    // EventBuffer write-back (docs/guides/command-buffer.md, CHANGELOG 2.1.1):
    //   var b = world.Get<EventBuffer<Hit>>(e); b.Add(h); cb.Set(e, b);
    // ------------------------------------------------------------------

    /// <summary> AddEvent to each target every frame; targets past <c>active</c> start later (SetActive). </summary>
    public sealed class EventProducer<T> : IRunSystem where T : struct {
        private readonly Entity[] _targets;
        public int Active;

        public EventProducer(Entity[] targets, int active) {
            _targets = targets;
            Active = active;
        }

        public void Run(World world) {
            for (int i = 0; i < Active; i++) {
                world.AddEvent(_targets[i], default(T));
            }
        }
    }

    /// <summary> The documented write-back of a modified EventBuffer copy through a CommandBuffer. </summary>
    public sealed class AppendViaSet<T> : IInitSystem, IRunSystem where T : struct {
        private readonly CommandBuffer _cb;
        private Filter _filter;

        public AppendViaSet(CommandBuffer cb) {
            _cb = cb;
        }

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<EventBuffer<T>>().End();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                var entity = world.GetEntity(e);
                var b = world.Get<EventBuffer<T>>(entity);
                b.Add(default(T));
                _cb.Set(entity, b);
            }
        }
    }

    /// <summary>
    /// The control of the write-back: the same filter walk and the same world.Get of every buffer, read only (no Add, no
    /// Set), so what Get itself may allocate is in the control too and the long run of the write-back keeps only its own bytes.
    /// </summary>
    public sealed class ReadViaGet<T> : IInitSystem, IRunSystem where T : struct {
        private Filter _filter;

        public void Init(World world, SharedData shared) {
            _filter = world.Filter().Inc<EventBuffer<T>>().End();
        }

        public void Run(World world) {
            foreach (int e in _filter) {
                var entity = world.GetEntity(e);
                var b = world.Get<EventBuffer<T>>(entity);
                Sink.Number += b.Count;
            }
        }
    }

    public sealed class PlaybackSystem : IRunSystem {
        private readonly CommandBuffer _cb;

        public PlaybackSystem(CommandBuffer cb) {
            _cb = cb;
        }

        public void Run(World world) {
            _cb.Playback(world);
        }
    }

    // ------------------------------------------------------------------
    // ParallelRunner job.
    // ------------------------------------------------------------------
    public struct AddOneJob : IRangeJob {
        public float[] Data;

        public void Execute(int start, int end) {
            var d = Data;
            for (int i = start; i < end; i++) {
                d[i] += 1f;
            }
        }
    }
}
