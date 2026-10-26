using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    /// <summary>
    /// Random stand programs from a seed: one to three hierarchies (root kinds from generated
    /// systems to hand-written bases of every Init shape, Lib/Main splits, overloads and hooks in
    /// between), extra standalone systems, random fields, nesting and partial parts, and now and
    /// then one "noise" feature (keyword identifiers, case-only names, special shared keys...).
    /// The same seed always gives the same program; validity is checked by the pipeline (a program
    /// that does not compile without the generator is discarded, never reported).
    /// </summary>
    public sealed class Fuzzer {
        private static readonly string[] Namespaces = { "", "Game", "Game.Systems", "Combat", "Ui", "Game.Combat" };
        private static readonly string[] Stems = { "Move", "Spawn", "Damage", "Heal", "Aim", "Patrol", "Loot", "Shield", "Burn", "Poison", "Wave", "Target", "Drop", "Pulse" };
        private static readonly string[] Keywords = { "@event", "@object", "@base", "@params", "@lock", "@string", "@operator", "@fixed" };
        private static readonly string[] Comps = Enumerable.Range(0, 12).Select(i => "global::StandShared.C" + i).ToArray();

        private readonly Random _r;
        private readonly ProgramModel _p = new();
        private readonly List<string> _features = new();
        private int _n;

        private Fuzzer(int seed) {
            _r = new Random(seed);
        }

        public static ProgramModel Generate(int seed, out List<string> features) {
            var f = new Fuzzer(seed);
            var p = f.Build();
            p.Title = "fuzz seed " + seed;
            features = f._features;
            return p;
        }

        private bool P(double probability) => _r.NextDouble() < probability;

        private T Pick<T>(IReadOnlyList<T> items) => items[_r.Next(items.Count)];

        private T Weighted<T>(params (T Item, int Weight)[] items) {
            int total = items.Sum(i => i.Weight);
            int x = _r.Next(total);
            foreach (var (item, weight) in items) {
                if (x < weight) {
                    return item;
                }
                x -= weight;
            }
            return items[^1].Item;
        }

        private string NextId() => "K" + (++_n);

        private string ClassName() => Pick(Stems) + _n + "System";

        private ProgramModel Build() {
            bool hasLib = P(0.3);
            int hierarchies = _r.Next(1, 4);
            for (int h = 0; h < hierarchies; h++) {
                Hierarchy(hasLib);
            }
            int extra = _r.Next(0, 3);
            for (int i = 0; i < extra; i++) {
                var c = Gen(Asm.Main, Pick(Namespaces));
                c.ListsRun = P(0.5);
                _p.Classes.Add(c);
            }
            if (P(0.22)) {
                Noise();
            }
            FixAbstractness();
            return _p;
        }

        private ClassModel Gen(Asm asm, string ns) {
            var c = new ClassModel { Id = NextId(), Asm = asm, Ns = ns };
            c.Name = ClassName();
            int fields = _r.Next(1, 4);
            for (int i = 0; i < fields; i++) {
                c.Fields.Add(Field(c));
            }
            c.ListsInit = P(0.25);
            c.OnInit = P(0.75);
            if (P(0.14)) {
                c.Parts = P(0.7) ? 2 : 3;
                foreach (var f in c.Fields) {
                    f.Part = _r.Next(c.Parts);
                }
            }
            if (P(0.1)) {
                int levels = P(0.75) ? 1 : 2;
                for (int i = 0; i < levels; i++) {
                    c.Outers.Add(new OuterModel {
                        Name = "Holder" + _n + (i == 0 ? "" : "Inner"),
                        Kind = Weighted((OuterKind.Class, 3), (OuterKind.Struct, 1), (OuterKind.StaticClass, 1)),
                    });
                }
            }
            return c;
        }

        private FieldModel Field(ClassModel owner) {
            string name = "_" + Pick(Stems).ToLowerInvariant() + (++_n);
            var kind = Weighted((FieldKind.Pool, 40), (FieldKind.Filter, 30), (FieldKind.Shared, 20), (FieldKind.Group, 10));
            var f = new FieldModel {
                Name = name, Kind = kind,
                Access = Weighted(("private", 50), ("protected", 25), ("public", 15), ("internal", 10)),
                Spelling = Weighted(("short", 88), ("long", 4), ("qualified", 4), ("global", 4)),
            };
            switch (kind) {
                case FieldKind.Pool:
                    f.Type = !string.IsNullOrEmpty(owner.TypeParams) && P(0.5) ? owner.TypeParams : Pick(Comps);
                    break;
                case FieldKind.Filter:
                    f.Inc = Comps.Take(4).Where(_ => P(0.35)).ToList();
                    f.Any = Comps.Skip(4).Take(4).Where(_ => P(0.2)).ToList();
                    if (f.Inc.Count == 0 && f.Any.Count == 0) {
                        f.Inc.Add(Pick(Comps.Take(4).ToList()));
                    }
                    f.Exc = Comps.Skip(8).Take(4).Where(_ => P(0.2)).ToList();
                    // [Inc(...), Exc(...)] in one attribute list.
                    f.AttrList = P(0.15);
                    break;
                case FieldKind.Shared:
                    f.Type = Weighted(("global::StandShared.Cfg0", 3), ("global::StandShared.Cfg1", 3), ("global::StandShared.Cfg2", 2), ("string", 1), ("object", 1),
                        (SharedKinds.Iface, 1), (SharedKinds.Abstract, 1), (SharedKinds.Generic, 1), (SharedKinds.Nested, 1));
                    f.Key = P(0.4) ? Pick(new[] { "enemies", "pickups", "ui", "" }) : null;
                    break;
                default:
                    f.Type = Weighted((Mk.G01, 3), (Mk.G23, 3), ("global::StandShared.G4, global::StandShared.G5, global::StandShared.G6", 3), (Mk.G4x, 2));
                    break;
            }
            // A foreign attribute ([System.NonSerialized]) in front of the injection attribute, in its own list or the same one.
            if (P(0.06)) {
                f.Foreign = P(0.5) ? "before" : "list";
            }
            return f;
        }

        private ClassModel Add(ClassModel c) {
            _p.Classes.Add(c);
            return c;
        }

        private ClassModel Hand(Asm asm, string ns, InitKind init, bool lists, string name = null) {
            var c = new ClassModel { Id = NextId(), Asm = asm, Ns = ns, Init = init, ListsInit = lists || Contract.IsExplicitKind(init) };
            c.Name = name ?? Pick(Stems) + _n + "Base";
            return c;
        }

        private void Hierarchy(bool hasLib) {
            var asm = hasLib && P(0.55) ? Asm.Lib : Asm.Main;
            string ns = Pick(Namespaces);
            var rootKind = Weighted(
                ("gen", 30), ("hand-virtual", 12), ("hand-abstract", 6), ("hand-nonvirtual", 5), ("hand-explicit", 4),
                ("hand-virtual-explicit", 3), ("hand-explicit-virtual", 2), ("hand-composite", 6), ("hand-hook", 5),
                ("hand-sealed", 3), ("hand-hidden", 3), ("nonsystem-plain", 4), ("nonsystem-member", 4), ("gen-generic", 5), ("hand-generic", 3),
                ("hand-explicit-forward", 3), ("hand-template", 4));
            _features.Add("root:" + rootKind);
            ClassModel current;
            switch (rootKind) {
                case "gen":
                    current = Add(Gen(asm, ns));
                    current.ListsRun = P(0.5);
                    break;
                case "gen-generic":
                    current = new ClassModel { Id = NextId(), Asm = asm, Ns = ns, TypeParams = "T" };
                    current.Name = ClassName();
                    current.Fields.Add(new FieldModel { Name = "_pool" + _n, Kind = FieldKind.Pool, Type = "T", Access = "protected" });
                    if (P(0.5)) {
                        current.Fields.Add(Field(current));
                    }
                    current.OnInit = P(0.75);
                    Add(current);
                    break;
                case "hand-virtual":
                    current = Add(Hand(asm, ns, InitKind.PublicVirtual, true));
                    current.ListsRun = P(0.5);
                    break;
                case "hand-generic":
                    current = Add(Hand(asm, ns, InitKind.PublicVirtual, true));
                    current.TypeParams = "T";
                    break;
                case "hand-abstract":
                    current = Add(Hand(asm, ns, InitKind.PublicAbstract, true));
                    current.Abstract = true;
                    break;
                case "hand-nonvirtual":
                    current = Add(Hand(asm, ns, InitKind.PublicNonVirtual, true));
                    break;
                case "hand-explicit":
                    current = Add(Hand(asm, ns, InitKind.Explicit, true));
                    break;
                case "hand-virtual-explicit":
                    current = Add(Hand(asm, ns, InitKind.VirtualThenExplicit, true));
                    break;
                case "hand-explicit-virtual":
                    current = Add(Hand(asm, ns, InitKind.ExplicitThenVirtual, true));
                    break;
                case "hand-composite": {
                    var iface = _p.Interfaces.FirstOrDefault(i => i.Asm == asm);
                    if (iface == null) {
                        iface = new InterfaceModel { Id = "I" + (_p.Interfaces.Count + 1), Name = "IGameSystem" + (asm == Asm.Lib ? "L" : ""), Ns = ns, Asm = asm };
                        _p.Interfaces.Add(iface);
                    }
                    bool abs = P(0.3);
                    current = Add(Hand(asm, ns, abs ? InitKind.PublicAbstract : InitKind.PublicVirtual, false));
                    current.IfaceId = iface.Id;
                    current.Abstract = abs;
                    break;
                }
                case "hand-hook":
                    current = Add(Hand(asm, ns, InitKind.PublicVirtual, true));
                    current.Hook = P(0.7) ? HookKind.Virtual : HookKind.NonVirtual;
                    break;
                case "hand-explicit-forward":
                    current = Add(Hand(asm, ns, InitKind.ExplicitForward, true));
                    break;
                case "hand-template":
                    current = Add(Hand(asm, ns, InitKind.PublicNonVirtual, true));
                    current.Hook = HookKind.Virtual;
                    break;
                case "hand-sealed": {
                    var r = Add(Hand(asm, ns, InitKind.PublicVirtual, true));
                    current = Add(Hand(asm, ns, InitKind.SealedOverride, false));
                    current.BaseId = r.Id;
                    break;
                }
                case "hand-hidden": {
                    var r = Add(Hand(asm, ns, InitKind.Explicit, true));
                    current = Add(Hand(asm, ns, InitKind.PublicVirtual, false));
                    current.BaseId = r.Id;
                    break;
                }
                case "nonsystem-plain":
                    current = Add(new ClassModel { Id = NextId(), Asm = asm, Ns = ns, Name = "Plain" + _n });
                    break;
                default:
                    current = Add(Hand(asm, ns, Pick(new[] {
                        InitKind.PublicNonVirtual, InitKind.PublicVirtual, InitKind.GenericHelper, InitKind.InParam,
                        InitKind.OverloadObject, InitKind.OverloadParams, InitKind.StaticInit, InitKind.FieldNamedInit, InitKind.PropertyNamedInit,
                    }), false, "Helper" + _n));
                    break;
            }
            string baseArgs = current.TypeParams != null ? Pick(Comps) : null;

            // Lib-side generated subclass: makes a generated Lib base virtual (docs: Inheritance).
            if (current.Asm == Asm.Lib && current.IsGenerated && P(0.4)) {
                var sub = Gen(Asm.Lib, ns);
                sub.BaseId = current.Id;
                sub.BaseArgs = baseArgs;
                sub.OnInitCallsBase = false;
                if (P(0.25)) {
                    sub.Define = "UNITY_EDITOR";
                    if (P(0.5) && !_p.LibDefines.Contains("UNITY_EDITOR")) {
                        _p.LibDefines.Add("UNITY_EDITOR");
                    }
                }
                Add(sub);
                _features.Add("lib-subclass");
            }

            int depth = _r.Next(1, 4);
            for (int level = 0; level < depth; level++) {
                if (asm == Asm.Lib && P(0.5)) {
                    asm = Asm.Main;
                }
                var next = Level(current, asm, ns, baseArgs);
                baseArgs = null;
                Add(next);
                if (P(0.15)) {
                    var sibling = Gen(asm, ns);
                    sibling.BaseId = current.Id;
                    sibling.BaseArgs = next.BaseArgs;
                    SetCall(sibling);
                    Add(sibling);
                }
                current = next;
            }
        }

        private void SetCall(ClassModel c) {
            if (c.IsGenerated && c.OnInit && P(0.2) && Contract.CanCallBase(_p, c)) {
                c.OnInitCallsBase = true;
            } else {
                c.OnInitCallsBase = false;
            }
        }

        /// <summary>
        /// The class at or above <paramref name="start"/> whose Init member an `override Init(World,
        /// SharedData)`, a `new` Init or a base.Init call in a class below would reach: a generated class
        /// or the first hand-written Init member that C# member lookup stops at (overloads with another
        /// signature, a generic helper and an explicit implementation are passed over).
        /// </summary>
        private (ClassModel Owner, InitKind Kind) Decider(ClassModel start) {
            for (var a = start; a != null; a = _p.BaseOf(a)) {
                if (a.IsGenerated && _p.IsActive(a)) {
                    return (a, InitKind.None);
                }
                if (a.Init is InitKind.None or InitKind.Explicit or InitKind.GenericHelper or InitKind.InParam
                    or InitKind.OverloadObject or InitKind.OverloadOptional or InitKind.OverloadParams) {
                    continue;
                }
                return (a, a.Init);
            }
            return (null, InitKind.None);
        }

        // Whether `protected override void OnInit(World, SharedData)` below `start` overrides a virtual hook.
        private bool HookOverridable(ClassModel start) {
            for (var a = start; a != null; a = _p.BaseOf(a)) {
                if (a.Hook is HookKind.Virtual or HookKind.Override) {
                    return true;
                }
                if (a.Hook == HookKind.NonVirtual) {
                    return false;
                }
            }
            return false;
        }

        private ClassModel Level(ClassModel parent, Asm asm, string ns, string baseArgs) {
            var (owner, ownerKind) = Decider(parent);
            bool generatedOwner = owner != null && owner.IsGenerated;
            bool overridable = owner != null && (generatedOwner
                ? owner.Asm == asm
                : ownerKind is InitKind.PublicVirtual or InitKind.PublicAbstract or InitKind.PublicOverride or InitKind.PublicOverrideNoBase
                    or InitKind.PublicNewVirtual or InitKind.VirtualThenExplicit or InitKind.ExplicitThenVirtual);
            // Hiding an abstract Init (new or a member named Init) does not compile (CS0533).
            bool abstractOwner = !generatedOwner && ownerKind == InitKind.PublicAbstract;
            bool hookAbove = HookOverridable(parent);
            var kind = Weighted(
                ("gen", 55), ("plain", 12), ("override", overridable ? 8 : 0), ("override-nobase", overridable ? 2 : 0),
                ("new", owner != null && !abstractOwner ? 3 : 0), ("new-virtual", owner != null && !abstractOwner ? 2 : 0), ("new-forward", owner != null && !abstractOwner ? 3 : 0),
                ("overload", 4), ("member", abstractOwner ? 0 : 3),
                ("hook-override", hookAbove ? 5 : 0));
            _features.Add("level:" + kind);
            ClassModel c;
            switch (kind) {
                case "gen":
                    c = Gen(asm, ns);
                    break;
                case "hook-override":
                    c = Gen(asm, ns);
                    c.Hook = HookKind.Override;
                    c.OnInit = false;
                    break;
                case "plain":
                    c = new ClassModel { Id = NextId(), Asm = asm, Ns = ns, Name = "Plain" + _n };
                    break;
                case "override":
                    c = Hand(asm, ns, InitKind.PublicOverride, false, "Over" + _n);
                    break;
                case "override-nobase":
                    c = Hand(asm, ns, InitKind.PublicOverrideNoBase, false, "Over" + _n);
                    break;
                case "new":
                    c = Hand(asm, ns, InitKind.PublicNewNonVirtual, P(0.3), "Hide" + _n);
                    break;
                case "new-virtual":
                    c = Hand(asm, ns, InitKind.PublicNewVirtual, P(0.3), "Hide" + _n);
                    break;
                case "new-forward":
                    c = Hand(asm, ns, InitKind.PublicNewForward, P(0.3), "Forward" + _n);
                    break;
                case "overload":
                    c = Hand(asm, ns, Pick(new[] { InitKind.OverloadObject, InitKind.OverloadOptional, InitKind.GenericHelper, InitKind.InParam, InitKind.OverloadParams }), false, "Overload" + _n);
                    break;
                default:
                    c = Hand(asm, ns, Pick(new[] { InitKind.FieldNamedInit, InitKind.PropertyNamedInit, InitKind.StaticInit }), false, "Member" + _n);
                    break;
            }
            c.BaseId = parent.Id;
            c.BaseArgs = baseArgs;
            SetCall(c);
            return c;
        }

        // A concrete class under an abstract Init that nothing below implements does not compile;
        // such a class stays abstract (it is then simply not run).
        private void FixAbstractness() {
            foreach (var c in _p.Classes) {
                if (c.IsGenerated || Contract.IsOverrideKind(c.Init) || c.Init == InitKind.PublicAbstract) {
                    continue;
                }
                var (owner, kind) = Renderer.NearestInit(_p, c);
                if (owner != null && kind == InitKind.PublicAbstract) {
                    c.Abstract = true;
                }
            }
        }

        private void Noise() {
            var gens = _p.Classes.Where(c => c.IsGenerated).ToList();
            var feature = Pick(new[] {
                "keyword-field", "keyword-class", "keyword-namespace", "case-namespaces", "case-classes", "hint-generic",
                "key-newline", "key-quote", "alias", "unicode", "field-world", "nullable",
            });
            _features.Add("noise:" + feature);
            switch (feature) {
                case "keyword-field" when gens.Count > 0:
                    Pick(Pick(gens).Fields).Name = Pick(Keywords);
                    break;
                case "keyword-class" when gens.Count > 0:
                    Pick(gens).Name = Pick(Keywords);
                    break;
                case "keyword-namespace" when gens.Count > 0:
                    Pick(gens).Ns = "Game.@internal";
                    break;
                case "case-namespaces": {
                    string name = "Tick" + (++_n) + "System";
                    var a = Gen(Asm.Main, "Game.AI");
                    a.Name = name;
                    var b = Gen(Asm.Main, "Game.Ai");
                    b.Name = name;
                    Add(a);
                    Add(b);
                    break;
                }
                case "case-classes": {
                    var a = Gen(Asm.Main, "Game");
                    a.Name = "Tick" + (++_n) + "System";
                    var b = Gen(Asm.Main, "Game");
                    b.Name = a.Name.Replace("System", "system");
                    Add(a);
                    Add(b);
                    break;
                }
                case "hint-generic": {
                    var a = new ClassModel { Id = NextId(), Name = "Sys" + _n, TypeParams = "T", OnInit = true };
                    a.Fields.Add(new FieldModel { Name = "_p", Kind = FieldKind.Pool, Type = "T" });
                    var b = Gen(Asm.Main, "");
                    b.Name = a.Name + "_T_";
                    Add(a);
                    Add(b);
                    break;
                }
                case "key-newline" when gens.Count > 0:
                    Pick(gens).Fields.Add(new FieldModel { Name = "_key" + (++_n), Kind = FieldKind.Shared, Type = "global::StandShared.Cfg3", Key = "line\nbreak" });
                    break;
                case "key-quote" when gens.Count > 0:
                    Pick(gens).Fields.Add(new FieldModel { Name = "_key" + (++_n), Kind = FieldKind.Shared, Type = "global::StandShared.Cfg3", Key = "say \"hi\" \\ bye" });
                    break;
                case "alias" when gens.Count > 0:
                    Pick(gens).Fields.ForEach(f => f.Spelling = "alias");
                    break;
                case "unicode" when gens.Count > 0:
                    Pick(gens).Name = "Движение" + _n;
                    break;
                case "field-world" when gens.Count > 0:
                    Pick(Pick(gens).Fields).Name = Pick(new[] { "world", "shared" });
                    break;
                case "nullable":
                    _p.NullableEnable = true;
                    foreach (var f in _p.Classes.SelectMany(c => c.Fields)) {
                        f.Nullable = P(0.5);
                    }
                    break;
            }
        }
    }
}
