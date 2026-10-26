using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KenseiECS;

namespace KenseiStand.FuzzCore {
    // Internal state of the tree under test, read by reflection so the stand keeps compiling when
    // internals change: a missing field disables the check that needs it.
    public static class Internals {
        private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        public static readonly FieldInfo Alive = typeof(World).GetField("_alive", NP);
        public static readonly FieldInfo Generations = typeof(World).GetField("_generations", NP);
        public static readonly FieldInfo NextIndex = typeof(World).GetField("_nextIndex", NP);
        public static readonly FieldInfo FreeIndices = typeof(World).GetField("_freeIndices", NP);
        public static readonly FieldInfo FreeCount = typeof(World).GetField("_freeCount", NP);
        public static readonly FieldInfo Masks = typeof(World).GetField("_componentMasks", NP);
        public static readonly FieldInfo Counts = typeof(World).GetField("_componentCounts", NP);
        // KENSEI_DEBUG builds only: number of DestroyEntity calls in progress.
        public static readonly FieldInfo DestroyDepth = typeof(World).GetField("_destroyDepth", NP);

        public static readonly FieldInfo ListPoolFree = typeof(EventBuffer<Hit>).Assembly
            .GetType("KenseiECS.ListPool`1")?.MakeGenericType(typeof(Hit))
            .GetField("_free", BindingFlags.NonPublic | BindingFlags.Static);

        public static bool CanDetectDying =>
            Alive != null && Generations != null && NextIndex != null && FreeIndices != null && FreeCount != null;

        // Empty the static EventBuffer<Hit> list pool, so runs in one process do not leak lists into each other.
        public static void ResetListPool() {
            var free = ListPoolFree?.GetValue(null);
            free?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(free, null);
        }

        public static List<object> FreeLists() {
            var result = new List<object>();
            if (ListPoolFree?.GetValue(null) is IEnumerable e) {
                foreach (var x in e) {
                    result.Add(x);
                }
            }
            return result;
        }
    }

    // Typed operations on one component type of the real world.
    public abstract class RealOps {
        public CT Ct;
        public abstract void Init(World w);
        public abstract Entity Create(RealSide s, CompSpec c);
        public abstract void Add(RealSide s, Entity e, CompSpec c, int marker);
        public abstract void AddBoxed(World w, Entity e, object boxed);
        public abstract void Remove(World w, Entity e);
        public abstract bool WorldHas(World w, Entity e);
        public abstract bool PoolHas(World w, int idx);
        public abstract void PoolAdd(RealSide s, int idx, CompSpec c);
        public abstract void PoolRemove(World w, int idx);
        public abstract void WriteGet(World w, Entity e, int m);
        public abstract void Modify(World w, int idx, int m);
        public abstract void MarkChanged(World w, int idx);
        public abstract string Digest(World w, int idx);
        public abstract object CopyOut(World w, Entity e);
        public abstract PendingEntity CbCreate(CommandBuffer cb, RealSide s, CompSpec c);
        public abstract void CbAdd(CommandBuffer cb, RealSide s, Entity e, PendingEntity? p, CompSpec c);
        public abstract void CbSet(CommandBuffer cb, RealSide s, Entity e, PendingEntity? p, CompSpec c);
        public abstract void CbSetBoxed(CommandBuffer cb, Entity e, PendingEntity? p, object boxed);
        public abstract void CbRemove(CommandBuffer cb, Entity e, PendingEntity? p);
        public abstract object NewListener(RealSide s, int lid);
        public abstract void AddListener(World w, object l);
        public abstract void RemoveListener(World w, object l);
        public abstract void Inc(FilterBuilder b);
        public abstract void Exc(FilterBuilder b);
        public abstract void Any(FilterBuilder b);
        public abstract int Count(World w);
        public abstract int[] Dense(World w);
        public abstract int Version(World w, int idx);
        public abstract object ListOf(World w, int idx);
    }

    public sealed class RealOps<T> : RealOps where T : struct, IComponent {
        public RealOps(CT ct) {
            Ct = ct;
        }

        public static T Make(RealSide s, CompSpec c) {
            if (c.IsDefault) {
                return default;
            }
            object v;
            switch (c.T) {
                case CT.A: v = new A { Id = c.Id, P = c.P }; break;
                case CT.B: v = new B { Id = c.Id, P = c.P }; break;
                case CT.C: v = new C { Id = c.Id, P = c.P }; break;
                case CT.D: v = new D { Id = c.Id, P = c.P }; break;
                case CT.Tag: v = new Tag(); break;
                case CT.R: v = new R { Id = c.Id, P = c.P, Box = new Probe(c.Id, 0, s.Probes) }; break;
                case CT.K: v = new K { Id = c.Id, P = c.P, Box = new Probe(c.Id, 0, s.Probes) }; break;
                case CT.Ev: v = new Ev { Id = c.Id, P = c.P }; break;
                case CT.EB: {
                    var b = new EventBuffer<Hit>();
                    b.Add(new Hit { V = c.P });
                    v = b;
                    break;
                }
                case CT.LS: {
                    var l = new Listeners<IFoo>();
                    l.Add(Foo.Get(c.Id));
                    v = l;
                    break;
                }
                default: throw new ArgumentOutOfRangeException();
            }
            return (T)v;
        }

        public static string DigestOf(ref T v) {
            object o = v;
            switch (o) {
                case A a: return Side.DigestPlain(CT.A, a.Id, a.P);
                case B b: return Side.DigestPlain(CT.B, b.Id, b.P);
                case C c: return Side.DigestPlain(CT.C, c.Id, c.P);
                case D d: return Side.DigestPlain(CT.D, d.Id, d.P);
                case Ev ev: return Side.DigestPlain(CT.Ev, ev.Id, ev.P);
                case Tag _: return "Tag";
                case R r: return Side.DigestBoxed(CT.R, r.Id, r.P, r.Box);
                case K k: return Side.DigestBoxed(CT.K, k.Id, k.P, k.Box);
                case EventBuffer<Hit> eb: return Side.DigestEvents(eb.Values?.Select(h => h.V).ToList());
                case Listeners<IFoo> ls: return Side.DigestFoos(ls.Values?.Select(f => f.Id).ToList());
            }
            return "?";
        }

        public static void SetP(ref T v, int m) {
            if (typeof(T) == typeof(A)) Unsafe.As<T, A>(ref v).P = m;
            else if (typeof(T) == typeof(B)) Unsafe.As<T, B>(ref v).P = m;
            else if (typeof(T) == typeof(C)) Unsafe.As<T, C>(ref v).P = m;
            else if (typeof(T) == typeof(D)) Unsafe.As<T, D>(ref v).P = m;
            else if (typeof(T) == typeof(Ev)) Unsafe.As<T, Ev>(ref v).P = m;
            else if (typeof(T) == typeof(R)) Unsafe.As<T, R>(ref v).P = m;
            else if (typeof(T) == typeof(K)) Unsafe.As<T, K>(ref v).P = m;
        }

        public override void Init(World w) {
            var pool = w.Pool<T>();
            if (Ct == CT.D) {
                pool.TrackChanges();
            }
        }

        public override Entity Create(RealSide s, CompSpec c) => s.W.CreateEntity(Make(s, c));

        public override void Add(RealSide s, Entity e, CompSpec c, int marker) {
            ref T r = ref s.W.Add(e, Make(s, c));
            if (marker >= 0) {
                SetP(ref r, marker);
            }
        }

        public override void AddBoxed(World w, Entity e, object boxed) => w.Add(e, (T)boxed);
        public override void Remove(World w, Entity e) => w.Remove<T>(e);
        public override bool WorldHas(World w, Entity e) => w.Has<T>(e);
        public override bool PoolHas(World w, int idx) => w.Pool<T>().Has(idx);
        public override void PoolAdd(RealSide s, int idx, CompSpec c) => s.W.Pool<T>().Add(idx, Make(s, c));
        public override void PoolRemove(World w, int idx) => w.Pool<T>().Remove(idx);
        public override void WriteGet(World w, Entity e, int m) => SetP(ref w.Get<T>(e), m);
        public override void Modify(World w, int idx, int m) => SetP(ref w.Pool<T>().Modify(idx), m);
        public override void MarkChanged(World w, int idx) => w.Pool<T>().MarkChanged(idx);
        public override string Digest(World w, int idx) => DigestOf(ref w.Pool<T>().Get(idx));
        public override object CopyOut(World w, Entity e) => w.Get<T>(e);

        public override PendingEntity CbCreate(CommandBuffer cb, RealSide s, CompSpec c) => cb.CreateEntity(s.NotePayload(Make(s, c)));

        public override void CbAdd(CommandBuffer cb, RealSide s, Entity e, PendingEntity? p, CompSpec c) {
            var v = s.NotePayload(Make(s, c));
            if (p.HasValue) cb.Add(p.Value, v); else cb.Add(e, v);
        }

        public override void CbSet(CommandBuffer cb, RealSide s, Entity e, PendingEntity? p, CompSpec c) {
            var v = s.NotePayload(Make(s, c));
            if (p.HasValue) cb.Set(p.Value, v); else cb.Set(e, v);
        }

        public override void CbSetBoxed(CommandBuffer cb, Entity e, PendingEntity? p, object boxed) {
            if (p.HasValue) cb.Set(p.Value, (T)boxed); else cb.Set(e, (T)boxed);
        }

        public override void CbRemove(CommandBuffer cb, Entity e, PendingEntity? p) {
            if (p.HasValue) cb.Remove<T>(p.Value); else cb.Remove<T>(e);
        }

        public override object NewListener(RealSide s, int lid) => new RealTypedListener<T>(s, lid, Ct);
        public override void AddListener(World w, object l) => w.Pool<T>().AddListener((IComponentListener<T>)l);
        public override void RemoveListener(World w, object l) => w.Pool<T>().RemoveListener((IComponentListener<T>)l);
        public override void Inc(FilterBuilder b) => b.Inc<T>();
        public override void Exc(FilterBuilder b) => b.Exc<T>();
        public override void Any(FilterBuilder b) => b.Any<T>();
        public override int Count(World w) => w.Pool<T>().Count;

        public override int[] Dense(World w) {
            var pool = w.Pool<T>();
            var arr = new int[pool.Count];
            Array.Copy(pool.RawEntities, arr, pool.Count);
            return arr;
        }

        public override int Version(World w, int idx) => w.Pool<T>().ChangedVersion(idx);

        public override object ListOf(World w, int idx) {
            object o = w.Pool<T>().Get(idx);
            switch (o) {
                case EventBuffer<Hit> eb: return eb.Values;
                case Listeners<IFoo> ls: return ls.Values;
            }
            return null;
        }
    }

    public sealed class RealTypedListener<T> : IComponentListener<T> where T : struct, IComponent {
        private readonly RealSide _s;
        private readonly int _lid;
        private readonly CT _ct;

        public RealTypedListener(RealSide s, int lid, CT ct) {
            _s = s;
            _lid = lid;
            _ct = ct;
        }

        public void OnAdded(int entityIndex, ref T component) => _s.OnTyped(_lid, Cb.Added, entityIndex, _ct, ref component);
        public void OnRemoved(int entityIndex, ref T component) => _s.OnTyped(_lid, Cb.Removed, entityIndex, _ct, ref component);
    }

    public sealed class RealFilterListener : IFilterListener {
        private readonly RealSide _s;
        private readonly int _lid;

        public RealFilterListener(RealSide s, int lid) {
            _s = s;
            _lid = lid;
        }

        public void OnEntityAdded(Filter filter, int entityIndex) => _s.OnFilter(_lid, Cb.Enter, filter, entityIndex);
        public void OnEntityRemoved(Filter filter, int entityIndex) => _s.OnFilter(_lid, Cb.Leave, filter, entityIndex);
    }

    public sealed class RealWorldListener : IWorldEventListener {
        private readonly RealSide _s;
        private readonly int _lid;

        public RealWorldListener(RealSide s, int lid) {
            _s = s;
            _lid = lid;
        }

        public void OnEntityCreated(int entityIndex) => _s.OnWorld(_lid, Cb.Created, entityIndex, -1);
        public void OnEntityDestroyed(int entityIndex) => _s.OnWorld(_lid, Cb.Destroyed, entityIndex, -1);
        public void OnComponentAdded(int entityIndex, int typeIndex) => _s.OnWorld(_lid, Cb.CompAdded, entityIndex, typeIndex);
        public void OnComponentRemoved(int entityIndex, int typeIndex) => _s.OnWorld(_lid, Cb.CompRemoved, entityIndex, typeIndex);
    }

    // The world under test, driven through the same primitives as the model.
    public sealed class RealSide : Side {
        public World W;
        private CommandBuffer _cb;
        private SystemsRunner _runner;
        private readonly Dictionary<string, Filter> _filters = new();
        private readonly Dictionary<Filter, string> _filterNames = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, object> _listenerObjs = new();
        private readonly HashSet<int> _activeLids = new();
        private readonly Dictionary<string, PendingEntity> _pending = new();
        private readonly Dictionary<CT, object> _recorders = new();
        // Lists of the fresh EventBuffer values handed to pending CommandBuffer commands (not copies of a component):
        // until Playback each one has a single owner, the command.
        private readonly List<object> _payloadLists = new();

        public T NotePayload<T>(T value) {
            if (value is EventBuffer<Hit> eb && eb.Values != null) {
                _payloadLists.Add(eb.Values);
            }
            return value;
        }
        public static readonly RealOps[] Ops = {
            new RealOps<A>(CT.A), new RealOps<B>(CT.B), new RealOps<C>(CT.C), new RealOps<D>(CT.D),
            new RealOps<Tag>(CT.Tag), new RealOps<R>(CT.R), new RealOps<K>(CT.K), new RealOps<Ev>(CT.Ev),
            new RealOps<EventBuffer<Hit>>(CT.EB), new RealOps<Listeners<IFoo>>(CT.LS),
        };

        public RealSide(Scenario scn) : base(scn, Build.Debug) {
            DyingOpsAllowed = Internals.CanDetectDying;
        }

        private static RealOps O(CT t) => Ops[(int)t];

        public static Entity ToEntity(EH h) => Unsafe.As<EH, Entity>(ref h);
        public static EH ToEH(Entity e) => new EH(e.Index, e.Generation);

        public override void Setup() {
            Internals.ResetListPool();
            W = new World();
            foreach (var t in TypeMap.All) {
                O(t).Init(W);
            }
            if (Scn.Group) {
                W.Group<A, B>();
            }
            _cb = new CommandBuffer();
            _runner = new SystemsRunner(W).OneFrame<Ev>().OneFrame<EventBuffer<Hit>>();
            _runner.Init();
            if (Scn.RecWorld) {
                var l = new RealWorldListener(this, Recorders.World);
                W.AddEventListener(l);
                _activeLids.Add(Recorders.World);
            }
            if (Scn.RecTyped) {
                foreach (var t in TypeMap.All) {
                    var l = O(t).NewListener(this, Recorders.Typed(t));
                    O(t).AddListener(W, l);
                    _activeLids.Add(Recorders.Typed(t));
                }
            }
        }

        // ------------------------------------------------------------------ callbacks

        // ------------------------------------------------------------------ immediate callback checks
        //
        // The recorders are the first listener of their pool, filter and world (registered before any
        // scripted listener), so when a recorder is called nothing else has reacted to this event yet.
        // What each callback promises at that moment is checked here, independent of the model and of
        // any order: a callback is about an entity (alive, or being destroyed), and a filter event agrees
        // with the filter's predicate for an alive entity. Each entry: log index, rule, detail.
        public readonly List<(int at, string rule, string detail)> CbViolations = new();

        // Slot state and handle at each callback of the current operation (by log index), and the entities
        // that this operation created (top-level or from listener reactions).
        public readonly Dictionary<int, (string state, EH handle)> CbInfo = new();
        public readonly HashSet<EH> CreatedThisOp = new();

        public void ResetOpTracking() {
            CbInfo.Clear();
            CreatedThisOp.Clear();
        }

        private void NoteCallback(int idx) {
            if (Internals.CanDetectDying) {
                CbInfo[Log.Count] = (SlotState(idx), ToEH(W.GetEntity(idx)));
            }
        }

        // "alive", "dying" (DestroyEntity in progress: dead flag set, slot not yet released) or "free".
        public string SlotState(int idx) {
            var alive = Internals.Alive?.GetValue(W) as bool[];
            if (alive == null || idx < 0) {
                return "free";
            }
            int next = (int)Internals.NextIndex.GetValue(W);
            if (idx >= next) {
                return "free";
            }
            if (alive[idx]) {
                return "alive";
            }
            var free = (int[])Internals.FreeIndices.GetValue(W);
            int freeCount = (int)Internals.FreeCount.GetValue(W);
            for (int i = 0; i < freeCount; i++) {
                if (free[i] == idx) {
                    return "free";
                }
            }
            return "dying";
        }

        private void Violation(string rule, string detail) {
            CbViolations.Add((Log.Count, rule, detail + " (during " + CurrentOp + ")"));
        }

        private void CheckTyped(int lid, Cb kind, int idx, CT ct) {
            if (!Recorders.IsRecorder(lid) || !Internals.CanDetectDying) {
                return;
            }
            string state = SlotState(idx);
            if (state == "free") {
                Violation("R-cb-free-slot", "IComponentListener<" + ct + ">." + (kind == Cb.Added ? "OnAdded" : "OnRemoved") + "(" + idx +
                          ") for slot " + idx + ", which holds no entity (not alive, not being destroyed)");
            }
        }

        private void CheckFilter(int lid, Cb kind, Filter f, int idx) {
            if (!Recorders.IsRecorder(lid) || !Internals.CanDetectDying) {
                return;
            }
            string state = SlotState(idx);
            string name = _filterNames.TryGetValue(f, out var n) ? n : "?";
            if (state == "free") {
                if (kind == Cb.Leave && Holdings(idx) == "[]") {
                    // A listener destroyed the entity while the filters of its last component's removal were still
                    // being updated; the remaining filters report the leave after the slot was released. Nothing is
                    // left on the slot (no phantom component), and the contract says nothing about such a late event.
                    Questions.Add("Q-stale-filter-event: IFilterListener.OnEntityRemoved arrives after a listener destroyed the entity during " +
                                  "the same structural change (the slot is already free); the contract does not say whether it should");
                    return;
                }
                Violation("R-cb-free-slot", "IFilterListener." + (kind == Cb.Enter ? "OnEntityAdded" : "OnEntityRemoved") + "(" + name + ", " + idx +
                          ") for slot " + idx + ", which holds no entity");
                return;
            }
            if (state != "alive") {
                return;   // a dying entity: masks and pools differ during the drain (questions Q-has-during-drain, Q-dying-enter)
            }
            bool matches = MatchesReal(f, idx);
            if (kind == Cb.Enter && !matches) {
                Violation("R-filter-event", "OnEntityAdded(" + name + " = " + f + ", " + idx + ") but the entity does not match the filter now: " + Holdings(idx));
            } else if (kind == Cb.Leave && matches) {
                Violation("R-filter-event", "OnEntityRemoved(" + name + " = " + f + ", " + idx + ") but the entity still matches the filter: " + Holdings(idx));
            }
        }

        private void CheckWorld(int lid, Cb kind, int idx, int typeIndex) {
            if (!Recorders.IsRecorder(lid) || !Internals.CanDetectDying) {
                return;
            }
            string state = SlotState(idx);
            switch (kind) {
                case Cb.CompAdded:
                case Cb.CompRemoved:
                    // World events are dispatched after the filter loops; a filter listener may have destroyed
                    // the entity in between. Unlike typed OnAdded (2.1.1: no further OnAdded once the component
                    // is gone), the contract says nothing about such a stale world event.
                    if (state == "free") {
                        Questions.Add("Q-stale-world-event: IWorldEventListener.OnComponentAdded/OnComponentRemoved arrives after a filter listener " +
                                      "destroyed the entity (the listener already got OnEntityDestroyed for it); the contract does not say whether it should");
                    }
                    break;
                case Cb.Destroyed:
                    if (state != "dying") {
                        Violation("R-cb-destroyed", "IWorldEventListener.OnEntityDestroyed(" + idx + ") while the slot is " + state +
                                  " (documented: the entity is already dead, its components are not yet removed)");
                    }
                    break;
            }
        }

        private string Holdings(int idx) {
            var list = new List<string>();
            foreach (var t in TypeMap.All) {
                if (O(t).PoolHas(W, idx)) {
                    list.Add(t.ToString());
                }
            }
            return "[" + string.Join(",", list) + "]";
        }

        public void OnTyped<T>(int lid, Cb kind, int idx, CT ct, ref T c) where T : struct, IComponent {
            CheckTyped(lid, kind, idx, ct);
            NoteCallback(idx);
            var plan = PlanFire(lid, kind, idx, ct.ToString(), RealOps<T>.DigestOf(ref c), ct);
            if (plan != null && plan.WriteMarker >= 0 && TypeMap.HasPayload(ct)) {
                RealOps<T>.SetP(ref c, plan.WriteMarker);
                LogWrite(lid, plan.WriteMarker);
            }
            RunPlan(plan, kind, idx);
        }

        public void OnFilter(int lid, Cb kind, Filter f, int idx) {
            CheckFilter(lid, kind, f, idx);
            NoteCallback(idx);
            string name = _filterNames.TryGetValue(f, out var n) ? n : "?";
            var plan = PlanFire(lid, kind, idx, name, null, null);
            RunPlan(plan, kind, idx);
        }

        public void OnWorld(int lid, Cb kind, int idx, int typeIndex) {
            CheckWorld(lid, kind, idx, typeIndex);
            NoteCallback(idx);
            CT? ct = typeIndex >= 0 ? TypeMap.FromIndex(typeIndex) : null;
            string x = typeIndex < 0 ? "" : (ct.HasValue ? ct.Value.ToString() : "t" + typeIndex);
            var plan = PlanFire(lid, kind, idx, x, null, ct);
            RunPlan(plan, kind, idx);
        }

        // ------------------------------------------------------------------ entity primitives

        public override EH CreateEntity(CompSpec c) {
            var e = ToEH(O(c.T).Create(this, c));
            CreatedThisOp.Add(e);
            return e;
        }

        public override void DestroyEntity(EH h) => W.DestroyEntity(ToEntity(h));

        public override EH CopyEntity(EH h) {
            var e = ToEH(W.CopyEntity(ToEntity(h)));
            CreatedThisOp.Add(e);
            return e;
        }
        public override bool IsAlive(EH h) => W.IsAlive(ToEntity(h));

        public override bool IsDying(EH h) {
            if (!Internals.CanDetectDying || h.IsNull) {
                return false;
            }
            var alive = (bool[])Internals.Alive.GetValue(W);
            var gens = (int[])Internals.Generations.GetValue(W);
            int next = (int)Internals.NextIndex.GetValue(W);
            if (alive == null || h.I < 0 || h.I >= next || alive[h.I] || gens[h.I] != h.G) {
                return false;
            }
            var free = (int[])Internals.FreeIndices.GetValue(W);
            int freeCount = (int)Internals.FreeCount.GetValue(W);
            for (int i = 0; i < freeCount; i++) {
                if (free[i] == h.I) {
                    return false;
                }
            }
            return true;
        }

        public override EH GetEntity(int idx) => ToEH(W.GetEntity(idx));

        // What a dead or stale handle points at: "free" (its own dead entity's slot, not reused), "reused" (another
        // entity holds the slot now, alive or being destroyed) or "free-reused" (the slot was reused and freed
        // again); "+destroying" when a DestroyEntity is in progress anywhere in the world, "+idle" otherwise.
        public override string StaleState(EH h) {
            if (!Internals.CanDetectDying || h.IsNull) {
                return "?";
            }
            var gens = (int[])Internals.Generations.GetValue(W);
            string state = SlotState(h.I);
            bool sameGen = h.I >= 0 && h.I < gens.Length && gens[h.I] == h.G;
            string target = state == "free" ? (sameGen ? "free" : "free-reused") : (sameGen ? "self-" + state : "reused");
            bool destroying = Internals.DestroyDepth != null && (int)Internals.DestroyDepth.GetValue(W) > 0;
            return target + (destroying ? "+destroying" : "+idle");
        }

        public override void StaleAccepted(EH h, string state, int pos, string action) {
            bool addEvent = action.IndexOf("ddEvent", StringComparison.Ordinal) >= 0;
            AddFact("stale-accepted:" + state, h.I, addEvent ? "addevent-stale" : "debug-handles", null, pos, decision: false);
        }
        public override bool WorldHas(EH h, CT t) => O(t).WorldHas(W, ToEntity(h));
        public override bool PoolHas(int idx, CT t) => O(t).PoolHas(W, idx);
        public override void Add(EH h, CompSpec c, int marker) => O(c.T).Add(this, ToEntity(h), c, marker);
        public override void Remove(EH h, CT t) => O(t).Remove(W, ToEntity(h));
        public override void PoolAdd(int idx, CompSpec c) => O(c.T).PoolAdd(this, idx, c);
        public override void PoolRemove(int idx, CT t) => O(t).PoolRemove(W, idx);
        public override void WriteGet(EH h, CT t, int marker) => O(t).WriteGet(W, ToEntity(h), marker);
        public override void Modify(int idx, CT t, int marker) => O(t).Modify(W, idx, marker);
        public override void MarkChanged(int idx, CT t) => O(t).MarkChanged(W, idx);
        public override void AddEvent(EH h, int v) => W.AddEvent(ToEntity(h), new Hit { V = v });
        public override void Subscribe(EH h, int fooId) => W.Subscribe<IFoo>(ToEntity(h), Foo.Get(fooId));
        public override void Unsubscribe(EH h, int fooId) => W.Unsubscribe<IFoo>(ToEntity(h), Foo.Get(fooId));

        public override void CopyClip(EH h, CT t, int slot) {
            Clip[slot] = O(t).CopyOut(W, ToEntity(h));
            ClipType[slot] = t;
            ClipSource[slot] = h;
        }

        public override void ClipAddEvent(int slot, int v) {
            var b = (EventBuffer<Hit>)Clip[slot];
            b.Add(new Hit { V = v });
            Clip[slot] = b;
        }

        public override void WriteBackAdd(EH h, int slot) => O(ClipType[slot]).AddBoxed(W, ToEntity(h), Clip[slot]);

        // ------------------------------------------------------------------ command buffer

        private PendingEntity? PendingOf(string pending) =>
            pending != null && _pending.TryGetValue(pending, out var p) ? p : (PendingEntity?)null;

        public override void CbSetClip(string pending, EH h, int slot) =>
            O(ClipType[slot]).CbSetBoxed(_cb, ToEntity(h), PendingOf(pending), Clip[slot]);

        public override void CbCreate(string pending, CompSpec c) {
            _pending[pending] = O(c.T).CbCreate(_cb, this, c);
            PendingIds[pending] = PendingIds.Count;
        }

        public override void CbAdd(string pending, EH h, CompSpec c) => O(c.T).CbAdd(_cb, this, ToEntity(h), PendingOf(pending), c);
        public override void CbSet(string pending, EH h, CompSpec c) => O(c.T).CbSet(_cb, this, ToEntity(h), PendingOf(pending), c);
        public override void CbRemove(string pending, EH h, CT t) => O(t).CbRemove(_cb, ToEntity(h), PendingOf(pending));

        public override void CbDestroy(string pending, EH h) {
            var p = PendingOf(pending);
            if (p.HasValue) _cb.DestroyEntity(p.Value); else _cb.DestroyEntity(ToEntity(h));
        }

        public override void CbPlayback() {
            try {
                _cb.Playback(W);
            } finally {
                _pending.Clear();
                PendingIds.Clear();
                _payloadLists.Clear();
            }
        }

        // ------------------------------------------------------------------ world operations

        public override void Clear() => W.Clear();
        public override void Warmup() => W.Warmup();

        public override bool Reserve(int entities, int components) {
            if (Compat.ReserveMethod == null) {
                return false;
            }
            try {
                Compat.ReserveMethod.Invoke(W, new object[] { entities, components });
            } catch (TargetInvocationException tie) when (tie.InnerException != null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            return true;
        }

        public override bool HasFilter(string name) => _filters.ContainsKey(name);

        private static readonly Dictionary<int, MethodInfo> GroupMethods = typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Group" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .GroupBy(m => m.GetGenericArguments().Length).ToDictionary(g => g.Key, g => g.First());

        private static Type ClrType(CT t) => Ops[(int)t].GetType().GetGenericArguments()[0];

        public override string RegisterGroup(CT[] types) {
            if (!GroupMethods.TryGetValue(types.Length, out var open)) {
                throw new NotSupportedException("world.Group with " + types.Length + " types is not in this version");
            }
            int before = W.GroupCount;
            try {
                open.MakeGenericMethod(types.Select(ClrType).ToArray()).Invoke(W, null);
            } catch (TargetInvocationException tie) when (tie.InnerException != null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            return W.GroupCount > before ? "new" : "existing";
        }

        public override void CreateFilter(string name, CT[] inc, CT[] exc, CT[] any) {
            int before = W.FilterCount;
            var b = W.Filter();
            foreach (var t in inc) O(t).Inc(b);
            foreach (var t in exc) O(t).Exc(b);
            foreach (var t in any) O(t).Any(b);
            var f = b.End();
            _filters[name] = f;
            if (W.FilterCount > before) {
                _filterNames[f] = name;
                if (Scn.RecFilters) {
                    int lid = Recorders.Filter(W.FilterCount - 1);
                    f.AddListener(new RealFilterListener(this, lid));
                    _activeLids.Add(lid);
                }
            }
        }

        public override void IterateFilter(string name, string[] plan) {
            var f = _filters[name];
            var visits = new List<int>();
            SuppressReactions = true;
            try {
                int k = 0;
                foreach (int idx in f) {
                    visits.Add(idx);
                    AddLog("visit|" + idx);
                    string action = plan == null || plan.Length == 0 ? "none" : plan[k % plan.Length];
                    k++;
                    AddLog("it|" + action + "|" + IterAction(action, idx));
                    if (visits.Count > 100000) {
                        AddLog("visit|runaway");
                        break;
                    }
                }
            } finally {
                SuppressReactions = false;
                Hints.Visits = visits;
            }
        }

        public override void RunFrame() {
            Hints.Dense[CT.Ev] = O(CT.Ev).Dense(W);
            Hints.Dense[CT.EB] = O(CT.EB).Dense(W);
            SuppressReactions = true;
            try {
                _runner.Run();
            } finally {
                SuppressReactions = false;
            }
        }

        // ------------------------------------------------------------------ listeners

        private object ListenerObj(ListenerDef def) {
            if (_listenerObjs.TryGetValue(def.Lid, out var o)) {
                return o;
            }
            switch (def.Kind) {
                case "typed": o = O(TypeMap.Parse(def.T)).NewListener(this, def.Lid); break;
                case "filter": o = new RealFilterListener(this, def.Lid); break;
                case "world": o = new RealWorldListener(this, def.Lid); break;
            }
            _listenerObjs[def.Lid] = o;
            return o;
        }

        public override bool Listen(int lid) {
            var def = Scn.FindListener(lid);
            if (def == null || _activeLids.Contains(lid)) {
                return false;
            }
            var obj = ListenerObj(def);
            switch (def.Kind) {
                case "typed":
                    O(TypeMap.Parse(def.T)).AddListener(W, obj);
                    break;
                case "filter":
                    if (!_filters.TryGetValue(def.F, out var f)) {
                        return false;
                    }
                    f.AddListener((IFilterListener)obj);
                    break;
                case "world":
                    W.AddEventListener((IWorldEventListener)obj);
                    break;
                default:
                    return false;
            }
            _activeLids.Add(lid);
            return true;
        }

        public override bool Unlisten(int lid) {
            var def = Scn.FindListener(lid);
            if (def == null || !_activeLids.Contains(lid)) {
                return false;
            }
            var obj = ListenerObj(def);
            switch (def.Kind) {
                case "typed":
                    O(TypeMap.Parse(def.T)).RemoveListener(W, obj);
                    break;
                case "filter":
                    if (_filters.TryGetValue(def.F, out var f)) {
                        f.RemoveListener((IFilterListener)obj);
                    }
                    break;
                case "world":
                    W.RemoveEventListener((IWorldEventListener)obj);
                    break;
            }
            _activeLids.Remove(lid);
            return true;
        }

        // ------------------------------------------------------------------ snapshot and checks

        public override List<string> Snapshot() {
            var lines = new List<string>();
            lines.Add("tick=" + W.Tick + " count=" + W.EntityCount + " cv=" + W.ChangeVersion);
            foreach (var e in W.AliveEntities) {
                var parts = new List<string>();
                foreach (var t in TypeMap.All) {
                    if (O(t).PoolHas(W, e.Index)) {
                        parts.Add(O(t).Digest(W, e.Index));
                    }
                }
                lines.Add("e" + e.Index + "g" + e.Generation + ": " + string.Join(" ", parts));
            }
            foreach (var t in TypeMap.All) {
                lines.Add("pool " + t + " n=" + O(t).Count(W));
            }
            foreach (var name in _filters.Keys.OrderBy(x => x, StringComparer.Ordinal)) {
                var f = _filters[name];
                lines.Add("filter " + name + " " + Members(f.Entities.ToArray()) + " n=" + f.Count);
            }
            foreach (var e in W.AliveEntities) {
                if (O(CT.D).PoolHas(W, e.Index)) {
                    lines.Add("ver e" + e.Index + "=" + O(CT.D).Version(W, e.Index));
                }
            }
            for (int g = 0; g < W.GroupCount; g++) {
                var grp = W.GetGroup(g);
                var names = new List<string>();
                for (int k = 0; k < grp.PoolCount; k++) {
                    var ct = TypeMap.FromIndex(grp.GetPool(k).TypeIndex);
                    names.Add(ct.HasValue ? ct.Value.ToString() : "?");
                }
                lines.Add("group " + g + " <" + string.Join(",", names) + "> n=" + grp.Count);
            }
            lines.Add(ProbeTotals());
            return lines;
        }

        // Consistency of the real world on its own, independent of the model. Each entry is
        // "<rule>|<detail>". Called between top-level operations only.
        public List<string> CheckInternal(List<string> notes) {
            var errs = new List<string>();
            var aliveArr = Internals.Alive?.GetValue(W) as bool[];
            int next = Internals.NextIndex != null ? (int)Internals.NextIndex.GetValue(W) : 0;
            var masks = Internals.Masks?.GetValue(W) as ulong[][];
            var alive = new List<Entity>();
            foreach (var e in W.AliveEntities) {
                alive.Add(e);
            }
            var aliveSet = new HashSet<int>(alive.Select(e => e.Index));

            foreach (var t in TypeMap.All) {
                var ops = O(t);
                int count = ops.Count(W);
                var dense = ops.Dense(W);
                var seen = new HashSet<int>();
                foreach (int idx in dense) {
                    if (!seen.Add(idx)) {
                        errs.Add("R-pool-dense|pool " + t + " lists entity " + idx + " twice");
                    }
                    if (!ops.PoolHas(W, idx)) {
                        errs.Add("R-pool-dense|pool " + t + " dense entry " + idx + " but Has(" + idx + ") is false");
                    }
                }
                int aliveWith = 0;
                for (int idx = 0; idx < next; idx++) {
                    bool has = ops.PoolHas(W, idx);
                    if (aliveSet.Contains(idx)) {
                        if (has) {
                            aliveWith++;
                        }
                        bool worldHas = ops.WorldHas(W, W.GetEntity(idx));
                        if (worldHas != has) {
                            errs.Add("R-has-mismatch|entity " + idx + ": world.Has<" + t + ">=" + worldHas + " but pool.Has=" + has);
                        }
                    } else if (has) {
                        errs.Add("R-dead-slot|dead slot " + idx + " still holds " + t + " (" + ops.Digest(W, idx) + ")");
                    }
                    if (!aliveSet.Contains(idx) && masks != null) {
                        int ti = TypeMap.Of(t);
                        int w = ti >> 6;
                        if (w < masks.Length && masks[w] != null && idx < masks[w].Length && (masks[w][idx] & (1UL << (ti & 63))) != 0) {
                            errs.Add("R-dead-mask|dead slot " + idx + " still has the mask bit of " + t);
                        }
                    }
                }
                if (count != aliveWith) {
                    errs.Add("R-pool-count|pool " + t + " Count=" + count + " but " + aliveWith + " alive entities have it");
                }
            }

            foreach (var e in alive) {
                int n = 0;
                foreach (var t in TypeMap.All) {
                    if (O(t).PoolHas(W, e.Index)) {
                        n++;
                    }
                }
                if (n == 0) {
                    errs.Add("R-alive-empty|entity " + e + " is alive with no components");
                }
                int cc = W.GetComponentCount(e);
                if (cc != n) {
                    errs.Add("R-count|entity " + e + ": GetComponentCount=" + cc + " but it has " + n + " components");
                }
            }

            foreach (var kv in _filterNames) {
                var f = kv.Key;
                string name = kv.Value;
                var expected = new HashSet<int>();
                foreach (var e in alive) {
                    if (MatchesReal(f, e.Index)) {
                        expected.Add(e.Index);
                    }
                }
                var actual = f.Entities.ToArray();
                var actualSet = new HashSet<int>(actual);
                if (actual.Length != actualSet.Count) {
                    errs.Add("R-filter|filter " + name + " lists an entity twice: " + Members(actual));
                }
                if (!actualSet.SetEquals(expected) || f.Count != expected.Count) {
                    var extra = actualSet.Except(expected).OrderBy(x => x).ToList();
                    var missing = expected.Except(actualSet).OrderBy(x => x).ToList();
                    errs.Add("R-filter|filter " + name + " (" + f + ") has " + Members(actual) + " n=" + f.Count + ", the predicate gives " +
                             Members(expected) + (extra.Count > 0 ? "; extra " + Members(extra) : "") + (missing.Count > 0 ? "; missing " + Members(missing) : ""));
                }
                var iterated = new List<int>();
                foreach (int idx in f) {
                    iterated.Add(idx);
                    if (iterated.Count > actual.Length + 4) {
                        break;
                    }
                }
                if (iterated.Count != actual.Length || !new HashSet<int>(iterated).SetEquals(actualSet)) {
                    errs.Add("R-filter-iter|filter " + name + " iteration gives " + Members(iterated) + " but Entities is " + Members(actual));
                }
                for (int idx = 0; idx < next; idx++) {
                    if (f.Contains(idx) != actualSet.Contains(idx)) {
                        errs.Add("R-filter-contains|filter " + name + ": Contains(" + idx + ")=" + f.Contains(idx));
                    }
                }
            }

            // Every owning group: exact membership (the alive entities with all its types) and aligned dense arrays.
            for (int gi = 0; gi < W.GroupCount; gi++) {
                var g = W.GetGroup(gi);
                var types = new List<CT>();
                for (int k = 0; k < g.PoolCount; k++) {
                    var ct = TypeMap.FromIndex(g.GetPool(k).TypeIndex);
                    if (ct.HasValue) {
                        types.Add(ct.Value);
                    }
                }
                int expected = alive.Count(e => types.All(t => O(t).PoolHas(W, e.Index)));
                if (g.Count != expected) {
                    errs.Add("R-group|group " + gi + " <" + string.Join(",", types) + "> Count=" + g.Count + " but " + expected + " entities have all its types");
                }
                var ents = g.Entities.ToArray();
                for (int i = 0; i < ents.Length; i++) {
                    for (int k = 0; k < g.PoolCount; k++) {
                        if (g.GetPool(k).RawEntities[i] != ents[i]) {
                            errs.Add("R-group|group " + gi + " pool " + k + " dense[" + i + "]=" + g.GetPool(k).RawEntities[i] + " != member " + ents[i]);
                        }
                    }
                }
            }

            // Invariant 6: a pooled or per-entity list belongs to one component at a time.
            var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (var t in new[] { CT.EB, CT.LS }) {
                foreach (var e in alive) {
                    if (!O(t).PoolHas(W, e.Index)) {
                        continue;
                    }
                    var list = O(t).ListOf(W, e.Index);
                    if (list == null) {
                        continue;
                    }
                    if (owners.TryGetValue(list, out var other)) {
                        errs.Add("L-shared-list|" + t + " of entity " + e + " and " + other + " hold the same List instance");
                    } else {
                        owners[list] = t + " of " + e;
                    }
                }
            }
            var free = Internals.FreeLists();
            var freeSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var l in free) {
                if (!freeSet.Add(l)) {
                    errs.Add("L-free-dup|the EventBuffer<Hit> list pool holds the same List twice (it was returned twice)");
                    break;
                }
            }
            foreach (var kv in owners) {
                if (freeSet.Contains(kv.Key)) {
                    errs.Add("L-owned-and-free|" + kv.Value + " holds a List that is also in the free list pool (the next AddEvent anywhere gets it too)");
                }
            }
            // The EventBuffer values handed to pending commands are built with EventBuffer.Add on a buffer without a list.
            // Up to 2.2.1 a pooled list has one owner, theirs included. 2.2.2 documents such a list as one without a
            // recorded holder (EventBuffer XML doc: "the list it rents has no recorded holder, and a copy of that buffer can
            // give it to a second entity"): its ownership is not checked by the package, so not here either.
            foreach (var list in Contract.V222 ? new List<object>() : _payloadLists) {
                if (owners.TryGetValue(list, out var other)) {
                    errs.Add("L-shared-list|the EventBuffer of a pending CommandBuffer command and " + other + " hold the same List instance");
                } else if (freeSet.Contains(list)) {
                    errs.Add("L-owned-and-free|the EventBuffer of a pending CommandBuffer command holds a List that is also in the free list pool");
                }
            }
            return errs;
        }

        // Filter predicate over the real world, through pool.Has.
        private bool MatchesReal(Filter f, int idx) {
            foreach (int ti in f.IncludedTypes) {
                var ct = TypeMap.FromIndex(ti);
                if (!ct.HasValue || !O(ct.Value).PoolHas(W, idx)) {
                    return false;
                }
            }
            foreach (int ti in f.ExcludedTypes) {
                var ct = TypeMap.FromIndex(ti);
                if (ct.HasValue && O(ct.Value).PoolHas(W, idx)) {
                    return false;
                }
            }
            if (f.AnyTypes.Length == 0) {
                return true;
            }
            foreach (int ti in f.AnyTypes) {
                var ct = TypeMap.FromIndex(ti);
                if (ct.HasValue && O(ct.Value).PoolHas(W, idx)) {
                    return true;
                }
            }
            return false;
        }
    }
}
