using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    public sealed class Scenario {
        public string Id;
        public string Group;
        public string Title;
        public ProgramModel Program;
        /// <summary> Class whose contract expectation the layout table shows. </summary>
        public string Focus;
    }

    /// <summary> Builders for stand programs. </summary>
    public static class Mk {
        public const string C0 = "global::StandShared.C0", C1 = "global::StandShared.C1", C2 = "global::StandShared.C2", C3 = "global::StandShared.C3";
        public const string C4 = "global::StandShared.C4", C5 = "global::StandShared.C5", C8 = "global::StandShared.C8", C9 = "global::StandShared.C9";
        public const string Cfg0 = "global::StandShared.Cfg0", Cfg1 = "global::StandShared.Cfg1";
        public const string G01 = "global::StandShared.G0, global::StandShared.G1", G23 = "global::StandShared.G2, global::StandShared.G3";
        /// <summary> A group of four component types (World.Group&lt;T1, T2, T3, T4&gt;). </summary>
        public const string G4x = "global::StandShared.G7, global::StandShared.G8, global::StandShared.G9, global::StandShared.G10";

        public static FieldModel Pool(string name, string comp = C0, string access = "private") =>
            new() { Name = name, Kind = FieldKind.Pool, Type = comp, Access = access };

        public static FieldModel Filt(string name, string[] inc, string[] exc = null, string[] any = null, string access = "private") =>
            new() { Name = name, Kind = FieldKind.Filter, Inc = inc?.ToList() ?? new(), Exc = exc?.ToList() ?? new(), Any = any?.ToList() ?? new(), Access = access };

        public static FieldModel Shr(string name, string type = Cfg0, string key = null, string access = "private") =>
            new() { Name = name, Kind = FieldKind.Shared, Type = type, Key = key, Access = access };

        public static FieldModel Grp(string name, string types = G01, string access = "private") =>
            new() { Name = name, Kind = FieldKind.Group, Type = types, Access = access };

        public static ClassModel Gen(string id, string name, params FieldModel[] fields) =>
            new() { Id = id, Name = name, Fields = fields.ToList(), OnInit = true };

        public static ClassModel Hand(string id, string name, InitKind init, bool lists = false) =>
            new() { Id = id, Name = name, Init = init, ListsInit = lists || Contract.IsExplicitKind(init) };

        public static ClassModel Plain(string id, string name) => new() { Id = id, Name = name };

        public static ClassModel Under(this ClassModel c, ClassModel b, string args = null) {
            c.BaseId = b.Id;
            c.BaseArgs = args;
            return c;
        }

        public static ClassModel At(this ClassModel c, Asm asm) {
            c.Asm = asm;
            return c;
        }

        public static ClassModel With(this ClassModel c, Action<ClassModel> change) {
            change(c);
            return c;
        }

        public static FieldModel With(this FieldModel f, Action<FieldModel> change) {
            change(f);
            return f;
        }

        public static ProgramModel Prog(string title, params ClassModel[] classes) => new() { Title = title, Classes = classes.ToList() };
    }

    /// <summary>
    /// The systematic part: every base layout of the inheritance matrix (base kind x assembly x the
    /// derived class listing IInitSystem x OnInit calling base.Init), then feature probes (nesting,
    /// partial parts, keyword identifiers, case-only names, attribute spellings, keys, nullable,
    /// containers, hand-written leaves, Editor/Player builds) and the documented diagnostics.
    /// </summary>
    public static class Catalog {
        private sealed class BaseKind {
            public string Name;
            public string Title;
            public Func<ProgramModel, (ClassModel Base, string Args)> Build;
            public string[] Modes = { "same" };
        }

        private static IEnumerable<BaseKind> BaseKinds() {
            BaseKind K(string name, string title, Func<ProgramModel, (ClassModel, string)> build, params string[] modes) =>
                new() { Name = name, Title = title, Build = build, Modes = modes.Length == 0 ? new[] { "same" } : modes };
            ClassModel add(ProgramModel p, ClassModel c) {
                p.Classes.Add(c);
                return c;
            }
            ClassModel genRoot(ProgramModel p, string access = "protected") =>
                add(p, Mk.Gen("A", "RootSystem", Mk.Pool("_a", Mk.C2, access)).With(c => c.ListsRun = true));

            yield return K("gen", "generated base (protected [Pool])", p => (add(p, Mk.Gen("B", "BaseSystem", Mk.Pool("_b", Mk.C0, "protected")).With(c => c.ListsRun = true)), null), "same", "lib", "lib+sub");
            yield return K("gen-private", "generated base (private [Pool])", p => (add(p, Mk.Gen("B", "BaseSystem", Mk.Pool("_b", Mk.C0, "private")).With(c => c.ListsRun = true)), null), "lib", "lib+sub");
            yield return K("gen-gen", "generated root, generated middle", p => {
                var a = genRoot(p);
                return (add(p, Mk.Gen("B", "MidSystem", Mk.Pool("_b", Mk.C0, "protected")).Under(a)), null);
            }, "same", "lib");
            yield return K("gen-plain-mid", "generated root, plain middle class", p => (add(p, Mk.Plain("M", "Middle").Under(genRoot(p))), null));
            yield return K("gen-override-mid", "generated root, hand-written middle `public override void Init` with base.Init", p => (add(p, Mk.Hand("M", "Middle", InitKind.PublicOverride).Under(genRoot(p))), null));
            yield return K("gen-new-mid", "generated root, middle hides Init with `public new void Init`", p => (add(p, Mk.Hand("M", "Middle", InitKind.PublicNewNonVirtual).Under(genRoot(p))), null), "same", "lib");
            yield return K("gen-newvirtual-mid", "generated root, middle hides Init with `public new virtual void Init`", p => (add(p, Mk.Hand("M", "Middle", InitKind.PublicNewVirtual).Under(genRoot(p))), null));
            yield return K("gen-newforward-mid", "generated root, middle hides Init with `public new void Init` that calls base.Init", p => (add(p, Mk.Hand("M", "Middle", InitKind.PublicNewForward).Under(genRoot(p))), null), "same", "lib");
            yield return K("gen-overload-object-mid", "generated root, middle `Init(World, object)`", p => (add(p, Mk.Hand("M", "Middle", InitKind.OverloadObject).Under(genRoot(p))), null));
            yield return K("gen-overload-optional-mid", "generated root, middle `Init(World, SharedData, int extra = 0)`", p => (add(p, Mk.Hand("M", "Middle", InitKind.OverloadOptional).Under(genRoot(p))), null));
            yield return K("gen-generic-helper-mid", "generated root, middle `protected void Init<TX>(World, SharedData)`", p => (add(p, Mk.Hand("M", "Middle", InitKind.GenericHelper).Under(genRoot(p))), null));
            yield return K("gen-inparam-mid", "generated root, middle `Init(in World, SharedData)`", p => (add(p, Mk.Hand("M", "Middle", InitKind.InParam).Under(genRoot(p))), null));
            yield return K("gen-params-mid", "generated root, middle `Init(World, params SharedData[])`", p => (add(p, Mk.Hand("M", "Middle", InitKind.OverloadParams).Under(genRoot(p))), null));
            yield return K("gen-field-init-mid", "generated root, middle `public bool Init;`", p => (add(p, Mk.Hand("M", "Middle", InitKind.FieldNamedInit).Under(genRoot(p))), null));
            yield return K("gen-property-init-mid", "generated root, middle `public bool Init { get; set; }`", p => (add(p, Mk.Hand("M", "Middle", InitKind.PropertyNamedInit).Under(genRoot(p))), null));
            yield return K("gen-static-init-mid", "generated root, middle `public static void Init(World, SharedData)`", p => (add(p, Mk.Hand("M", "Middle", InitKind.StaticInit).Under(genRoot(p))), null));
            yield return K("gen-generic", "generated generic base PoolSystem<T> ([Pool] ComponentPool<T>)", p => (add(p, Mk.Gen("B", "PoolSystem", Mk.Pool("_p", "T", "protected")).With(c => c.TypeParams = "T")), Mk.C0), "same", "lib+sub");
            yield return K("hand-virtual", "hand-written base : IInitSystem, `public virtual void Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicVirtual, true).With(c => c.ListsRun = true)), null), "same", "lib");
            yield return K("hand-abstract", "hand-written abstract base : IInitSystem, `public abstract void Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicAbstract, true).With(c => c.Abstract = true)), null), "same", "lib");
            yield return K("hand-nonvirtual", "hand-written base : IInitSystem, `public void Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicNonVirtual, true)), null), "same", "lib");
            // A new Init in a class between a hand-written root and the derived class (2.2.2: "hidden by a new Init in
            // between"), also over a composite interface, and over a generated root with only a private field in Lib.
            yield return K("hand-newvirtual-mid", "hand-written root : IInitSystem with `public void Init`, middle `public new virtual void Init`", p => {
                var r = add(p, Mk.Hand("R", "SystemRoot", InitKind.PublicNonVirtual, true));
                return (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicNewVirtual).Under(r)), null);
            });
            yield return K("hand-composite-newforward-mid", "hand-written root : IGameSystem with `public virtual void Init`, middle `public new void Init` that calls base.Init", p => {
                p.Interfaces.Add(new InterfaceModel { Id = "I1", Name = "IGameSystem" });
                var r = add(p, Mk.Hand("R", "GameSystem", InitKind.PublicVirtual).With(c => c.IfaceId = "I1"));
                return (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicNewForward).Under(r)), null);
            });
            yield return K("gen-private-new-mid", "generated root with a private [Pool], middle hides Init with `public new void Init`", p => (add(p, Mk.Hand("M", "Middle", InitKind.PublicNewNonVirtual).Under(genRoot(p, "private"))), null), "same", "lib");
            yield return K("hand-sealed", "hand-written root virtual, middle `public sealed override void Init`", p => {
                var r = add(p, Mk.Hand("R", "SystemRoot", InitKind.PublicVirtual, true));
                return (add(p, Mk.Hand("H", "SystemBase", InitKind.SealedOverride).Under(r)), null);
            });
            yield return K("hand-explicit", "hand-written base, `void IInitSystem.Init` (explicit)", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.Explicit, true)), null), "same", "lib");
            yield return K("hand-virtual-then-explicit", "hand-written base with `public virtual void Init` declared before an explicit `IInitSystem.Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.VirtualThenExplicit, true)), null), "same", "lib");
            yield return K("hand-explicit-then-virtual", "the same two members, explicit one first", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.ExplicitThenVirtual, true)), null), "same", "lib");
            yield return K("hand-explicit-forward", "hand-written base, `void IInitSystem.Init` forwarding to `protected virtual void Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.ExplicitForward, true)), null), "same", "lib");
            yield return K("hand-composite-virtual", "hand-written base : IGameSystem (IGameSystem : IInitSystem, IRunSystem), `public virtual void Init`", p => {
                p.Interfaces.Add(new InterfaceModel { Id = "I1", Name = "IGameSystem" });
                return (add(p, Mk.Hand("H", "GameSystem", InitKind.PublicVirtual).With(c => c.IfaceId = "I1")), null);
            }, "same", "lib");
            yield return K("hand-composite-abstract", "hand-written abstract base : IGameSystem, `public abstract void Init`", p => {
                p.Interfaces.Add(new InterfaceModel { Id = "I1", Name = "IGameSystem" });
                return (add(p, Mk.Hand("H", "GameSystem", InitKind.PublicAbstract).With(c => { c.IfaceId = "I1"; c.Abstract = true; })), null);
            });
            yield return K("hand-hook-virtual", "hand-written base, Init calls a `protected virtual void OnInit(World, SharedData)` hook", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicVirtual, true).With(c => c.Hook = HookKind.Virtual)), null), "same", "lib");
            yield return K("hand-hook-nonvirtual", "hand-written base, Init calls a `protected void OnInit(World, SharedData)` hook", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicVirtual, true).With(c => c.Hook = HookKind.NonVirtual)), null));
            yield return K("hand-template", "template method: hand-written base : IInitSystem, `public void Init` (not virtual) calls a `protected virtual void OnInit(World, SharedData)` hook", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicNonVirtual, true).With(c => c.Hook = HookKind.Virtual)), null), "same", "lib");
            yield return K("hand-hidden-virtual", "root implements IInitSystem.Init explicitly, middle adds `public virtual void Init` (not the interface method)", p => {
                var r = add(p, Mk.Hand("R", "SystemRoot", InitKind.Explicit, true));
                return (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicVirtual).Under(r)), null);
            }, "same", "lib");
            yield return K("hand-override-chain", "hand-written root virtual, middle `public override void Init` with base.Init", p => {
                var r = add(p, Mk.Hand("R", "SystemRoot", InitKind.PublicVirtual, true));
                return (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicOverride).Under(r)), null);
            });
            yield return K("hand-generic-virtual", "hand-written generic base SystemBase<T> : IInitSystem, `public virtual void Init`", p => (add(p, Mk.Hand("H", "SystemBase", InitKind.PublicVirtual, true).With(c => c.TypeParams = "T")), Mk.C0));
            yield return K("nonsystem-plain", "plain base class (no IInitSystem, no Init)", p => (add(p, Mk.Plain("N", "Plain")), null));
            yield return K("nonsystem-init", "non-system base with `public void Init(World, SharedData)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.PublicNonVirtual)), null), "same", "lib");
            yield return K("nonsystem-virtual-init", "non-system base with `public virtual void Init(World, SharedData)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.PublicVirtual)), null));
            yield return K("nonsystem-generic-helper", "non-system base with `protected void Init<TX>(World, SharedData)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.GenericHelper)), null));
            yield return K("nonsystem-inparam", "non-system base with `public void Init(in World, SharedData)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.InParam)), null));
            yield return K("nonsystem-overload-object", "non-system base with `public void Init(World, object)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.OverloadObject)), null));
            yield return K("nonsystem-params", "non-system base with `public void Init(World, params SharedData[])`", p => (add(p, Mk.Hand("N", "Helper", InitKind.OverloadParams)), null));
            yield return K("nonsystem-static-init", "non-system base with `public static void Init(World, SharedData)`", p => (add(p, Mk.Hand("N", "Helper", InitKind.StaticInit)), null));
            yield return K("nonsystem-property-init", "non-system base with `public bool Init { get; set; }`", p => (add(p, Mk.Hand("N", "Helper", InitKind.PropertyNamedInit)), null), "same", "lib");
            yield return K("nonsystem-field-init", "non-system base with `public bool Init;`", p => (add(p, Mk.Hand("N", "Helper", InitKind.FieldNamedInit)), null));
        }

        public static List<Scenario> All() {
            var list = new List<Scenario>();
            list.AddRange(Inheritance());
            list.AddRange(Features());
            list.AddRange(Diagnostics());
            return list;
        }

        private static IEnumerable<Scenario> Inheritance() {
            foreach (var kind in BaseKinds()) {
                foreach (var mode in kind.Modes) {
                    foreach (bool lists in new[] { false, true }) {
                        foreach (bool calls in new[] { false, true }) {
                            var p = new ProgramModel();
                            var (b, args) = kind.Build(p);
                            if (mode != "same") {
                                foreach (var c in p.Classes) {
                                    c.Asm = Asm.Lib;
                                }
                                foreach (var i in p.Interfaces) {
                                    i.Asm = Asm.Lib;
                                }
                            }
                            if (mode == "lib+sub") {
                                p.Classes.Add(Mk.Gen("S", "LibSubSystem", Mk.Pool("_s", Mk.C3)).Under(b, args).At(Asm.Lib));
                            }
                            var d = Mk.Gen("D", "Derived", Mk.Pool("_d", Mk.C1)).Under(b, args).With(c => c.ListsInit = lists);
                            p.Classes.Add(d);
                            if (calls) {
                                if (!Contract.CanCallBase(p, d)) {
                                    continue;
                                }
                                d.OnInitCallsBase = true;
                            }
                            string id = $"I-{kind.Name}/{mode}/{(lists ? "list" : "nolist")}/{(calls ? "call" : "nocall")}";
                            p.Title = id;
                            list(p, id, kind, mode, lists, calls, out var s);
                            yield return s;
                        }
                    }
                }
            }

            static void list(ProgramModel p, string id, BaseKind kind, string mode, bool lists, bool calls, out Scenario s) {
                string where = mode switch { "lib" => "base in Lib", "lib+sub" => "base in Lib with a generated Lib subclass", _ => "one assembly" };
                s = new Scenario {
                    Id = id, Group = "inheritance", Program = p, Focus = "D",
                    Title = $"{kind.Title}; {where}; Derived {(lists ? "lists" : "does not list")} IInitSystem, OnInit {(calls ? "calls" : "does not call")} base.Init",
                };
            }
        }

        private static Scenario S(string id, string group, string title, ProgramModel p, string focus = null) {
            p.Title = id;
            return new Scenario { Id = id, Group = group, Title = title, Program = p, Focus = focus ?? p.Classes.LastOrDefault(c => c.IsGenerated)?.Id };
        }

        private static IEnumerable<Scenario> Features() {
            OuterModel O(string name, OuterKind kind = OuterKind.Class, string mods = null, string tp = null) => new() { Name = name, Kind = kind, Modifiers = mods, TypeParams = tp };
            ClassModel Sys(string id, string name, params FieldModel[] f) => Mk.Gen(id, name, f.Length == 0 ? new[] { Mk.Pool("_a") } : f);

            // Nesting.
            yield return S("F-nested-class", "nesting", "system nested in a partial class", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("Outer")))));
            yield return S("F-nested-struct", "nesting", "system nested in a partial struct", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("OuterS", OuterKind.Struct)))));
            yield return S("F-nested-static", "nesting", "system nested in a static partial class", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("Systems", OuterKind.StaticClass)))));
            yield return S("F-nested-two", "nesting", "system nested two levels (class, struct)", Mk.Prog("", Sys("K1", "Nested").With(c => { c.Outers.Add(O("A")); c.Outers.Add(O("B", OuterKind.Struct)); })));
            yield return S("F-nested-chain", "nesting", "generated base and derived in the same container", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).With(c => c.Outers.Add(O("Outer"))),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.Outers.Add(O("Outer")); c.BaseId = "K1"; })));
            yield return S("F-nested-in-base", "nesting", "derived system nested inside its generated base", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")),
                Sys("K2", "Inner", Mk.Pool("_b", Mk.C1)).With(c => { c.NestedIn = "K1"; c.BaseId = "K1"; })));
            yield return S("F-nested-generic-container", "nesting", "base and derived in a generic container Outer<TTag>, closed by a plain subclass", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).With(c => c.Outers.Add(O("Outer", tp: "TTag"))),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.Outers.Add(O("Outer", tp: "TTag")); c.BaseId = "K1"; }),
                Mk.Plain("K3", "Closed").With(c => { c.BaseId = "K2"; c.BaseRef = "global::Outer<int>.Derived"; })), "K2");
            foreach (var mods in new[] { "readonly", "ref", "unsafe" }) {
                yield return S($"F-container-{mods}-struct", "nesting", $"system nested in a `{mods} partial struct`", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("Holder", OuterKind.Struct, mods)))));
            }
            foreach (var mods in new[] { "abstract", "sealed" }) {
                yield return S($"F-container-{mods}-class", "nesting", $"system nested in a `{mods} partial class`", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("Holder", OuterKind.Class, mods)))));
            }

            // OnInit hooks of a hand-written base (the generated partial OnInit has the same signature).
            foreach (var mode in new[] { "same", "lib" }) {
                var asm = mode == "lib" ? Asm.Lib : Asm.Main;
                yield return S($"F-hook-override/{mode}", "hooks", $"base Init calls a virtual OnInit hook; the generated derived class overrides the hook ({(mode == "lib" ? "base in Lib" : "one assembly")})", Mk.Prog("",
                    Mk.Hand("K1", "SystemBase", InitKind.PublicVirtual, true).With(c => c.Hook = HookKind.Virtual).At(asm),
                    Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K1"; c.Hook = HookKind.Override; c.OnInit = false; })), "K2");
            }
            yield return S("F-hook-generated-base-override", "hooks", "generated base, hand-written middle with a virtual OnInit hook, generated derived", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")),
                Mk.Hand("K2", "Middle", InitKind.PublicOverride).With(c => { c.BaseId = "K1"; c.Hook = HookKind.Virtual; }),
                Sys("K3", "Derived", Mk.Pool("_b", Mk.C1)).With(c => c.BaseId = "K2")), "K3");

            // An Init overload above the chained Init: C# binds the generated base.Init to the most derived
            // applicable declaration, and an override is not one.
            yield return S("F-capture-above-hand-override", "overloads", "hand-written root `public virtual void Init`, middle `Init(World, params SharedData[])`, hand-written `public override void Init` with base.Init, generated leaf", Mk.Prog("",
                Mk.Hand("K1", "SystemRoot", InitKind.PublicVirtual, true),
                Mk.Hand("K2", "ParamsHelper", InitKind.OverloadParams).With(c => c.BaseId = "K1"),
                Mk.Hand("K3", "Middle", InitKind.PublicOverride).With(c => c.BaseId = "K2"),
                Sys("K4", "Derived", Mk.Pool("_b", Mk.C1)).With(c => c.BaseId = "K3")), "K4");
            yield return S("F-capture-above-generated-override", "overloads", "abstract root `public abstract void Init`, abstract middle `Init(World, object)`, generated base (overrides the abstract Init), generated leaf", Mk.Prog("",
                Mk.Hand("K1", "SystemRoot", InitKind.PublicAbstract, true).With(c => c.Abstract = true),
                Mk.Hand("K2", "ObjectHelper", InitKind.OverloadObject).With(c => { c.BaseId = "K1"; c.Abstract = true; }),
                Sys("K3", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).With(c => c.BaseId = "K2"),
                Sys("K4", "Derived", Mk.Pool("_b", Mk.C1)).With(c => c.BaseId = "K3")), "K4");
            yield return S("F-abstract-helper-list", "overloads", "abstract root `public abstract void Init`, abstract middle `protected void Init<TX>(World, SharedData)`, generated leaf that lists IInitSystem", Mk.Prog("",
                Mk.Hand("K1", "SystemRoot", InitKind.PublicAbstract, true).With(c => c.Abstract = true),
                Mk.Hand("K2", "GenericHelper", InitKind.GenericHelper).With(c => { c.BaseId = "K1"; c.Abstract = true; }),
                Sys("K3", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K2"; c.ListsInit = true; })), "K3");

            // Partial parts.
            yield return S("F-parts-3", "parts", "fields in three partial declarations, OnInit in the last", Mk.Prog("",
                Sys("K1", "Split", Mk.Pool("_a"), Mk.Pool("_b", Mk.C1), Mk.Filt("_f", new[] { Mk.C2 })).With(c => { c.Parts = 3; c.Fields[1].Part = 1; c.Fields[2].Part = 2; })));
            yield return S("F-parts-chain", "parts", "base in two parts, derived in three", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected"), Mk.Pool("_c", Mk.C3)).With(c => { c.Parts = 2; c.Fields[1].Part = 1; }),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1), Mk.Shr("_s"), Mk.Filt("_f", new[] { Mk.C2 })).With(c => { c.BaseId = "K1"; c.Parts = 3; c.Fields[1].Part = 1; c.Fields[2].Part = 2; })));
            yield return S("F-parts-fields-not-first", "parts", "injected fields only in the second part (base list in the first)", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).With(c => { c.Parts = 2; c.Fields[0].Part = 1; }),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K1"; c.Parts = 2; c.Fields[0].Part = 1; })));

            // Keyword identifiers (escaped with @ in the source).
            yield return S("F-kw-field", "keywords", "injected field named @event", Mk.Prog("", Sys("K1", "KeywordField", Mk.Pool("@event"))));
            yield return S("F-kw-field-this", "keywords", "injected field named @this", Mk.Prog("", Sys("K1", "KeywordField", Mk.Pool("@this"))));
            yield return S("F-kw-class", "keywords", "system class named @class", Mk.Prog("", Sys("K1", "@class")));
            yield return S("F-kw-namespace", "keywords", "namespace Game.@internal", Mk.Prog("", Sys("K1", "Move").With(c => c.Ns = "Game.@internal")));
            yield return S("F-kw-container", "keywords", "container class named @base", Mk.Prog("", Sys("K1", "Nested").With(c => c.Outers.Add(O("@base")))));
            yield return S("F-kw-typeparam", "keywords", "generic system with a type parameter named @event", Mk.Prog("",
                Sys("K1", "PoolSystem", Mk.Pool("_p", "@event", "protected")).With(c => c.TypeParams = "@event"),
                Mk.Plain("K2", "Closed").Under(new ClassModel { Id = "K1" }, Mk.C0)), "K1");
            yield return S("F-kw-chain", "keywords", "generated base @base with a keyword field, generated derived", Mk.Prog("",
                Sys("K1", "@base", Mk.Pool("@object", Mk.C0, "protected")),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => c.BaseId = "K1")));

            // Names that differ only by case, and other hint-name collisions.
            yield return S("F-case-namespaces", "names", "two systems Game.AI.MoveSystem and Game.Ai.MoveSystem", Mk.Prog("",
                Sys("K1", "MoveSystem").With(c => c.Ns = "Game.AI"), Sys("K2", "MoveSystem", Mk.Pool("_b", Mk.C1)).With(c => c.Ns = "Game.Ai")));
            yield return S("F-case-namespaces-silent", "names", "Game.AI.MoveSystem and Game.Ai.MoveSystem without OnInit, next to an unrelated system: nothing but CS8785", Mk.Prog("",
                Sys("K1", "MoveSystem").With(c => { c.Ns = "Game.AI"; c.OnInit = false; }), Sys("K2", "MoveSystem", Mk.Pool("_b", Mk.C1)).With(c => { c.Ns = "Game.Ai"; c.OnInit = false; }),
                Sys("K3", "SpawnSystem", Mk.Pool("_s", Mk.C2)).With(c => { c.Ns = "Game"; c.OnInit = false; })), "K3");
            yield return S("F-case-classes", "names", "two systems Game.MoveSystem and Game.Movesystem", Mk.Prog("",
                Sys("K1", "MoveSystem").With(c => c.Ns = "Game"), Sys("K2", "Movesystem", Mk.Pool("_b", Mk.C1)).With(c => c.Ns = "Game")));
            yield return S("F-case-nested-vs-namespace", "names", "Game.Ai.Move nested in class Game.Ai, and Game.AI.Move in namespace Game.AI", Mk.Prog("",
                Sys("K1", "Move").With(c => { c.Ns = "Game"; c.Outers.Add(O("Ai")); }), Sys("K2", "Move", Mk.Pool("_b", Mk.C1)).With(c => c.Ns = "Game.AI")));
            yield return S("F-hint-generic", "names", "generic Sys<T> next to a class Sys_T_ (both hint Sys_T_)", Mk.Prog("",
                Sys("K1", "Sys", Mk.Pool("_p", "T")).With(c => c.TypeParams = "T"), Sys("K2", "Sys_T_", Mk.Pool("_b", Mk.C1)),
                Mk.Plain("K3", "Closed").Under(new ClassModel { Id = "K1" }, Mk.C0)), "K2");
            yield return S("F-same-name-namespaces", "names", "control: Game.MoveSystem and Ui.MoveSystem", Mk.Prog("",
                Sys("K1", "MoveSystem").With(c => c.Ns = "Game"), Sys("K2", "MoveSystem", Mk.Pool("_b", Mk.C1)).With(c => c.Ns = "Ui")));
            yield return S("F-unicode", "names", "Unicode identifiers (class Движение, field _скорость, namespace Игра)", Mk.Prog("",
                Sys("K1", "Движение", Mk.Pool("_скорость")).With(c => c.Ns = "Игра")));

            // Attribute spellings.
            foreach (var spelling in new[] { "long", "qualified", "global" }) {
                yield return S($"F-attr-{spelling}", "attributes", $"attribute spelling '{spelling}' on every field kind", Mk.Prog("",
                    Sys("K1", "Spelled", Mk.Pool("_p"), Mk.Filt("_f", new[] { Mk.C1 }, new[] { Mk.C8 }, new[] { Mk.C4 }), Mk.Shr("_s"), Mk.Grp("_g"))
                        .With(c => c.Fields.ForEach(f => f.Spelling = spelling))));
            }
            yield return S("F-attr-alias", "attributes", "every field marked through a using alias ([InjPool] = KenseiECS.PoolAttribute)", Mk.Prog("",
                Sys("K1", "Aliased", Mk.Pool("_p")).With(c => c.Fields.ForEach(f => f.Spelling = "alias"))));
            yield return S("F-attr-alias-mixed", "attributes", "one aliased and one plain [Pool] field", Mk.Prog("",
                Sys("K1", "Aliased", Mk.Pool("_p"), Mk.Pool("_q", Mk.C1)).With(c => c.Fields[0].Spelling = "alias")));
            // Attribute lists and a foreign attribute in front of the injection attribute (the syntax receiver must look at
            // every attribute of every list).
            yield return S("F-attr-list", "attributes", "the [Inc]/[Exc]/[Any] of a filter in one attribute list ([Inc(...), Exc(...), Any(...)]), also spelled long", Mk.Prog("",
                Sys("K1", "Listed", Mk.Filt("_f", new[] { Mk.C1 }, new[] { Mk.C8 }, new[] { Mk.C4 }).With(f => f.AttrList = true),
                    Mk.Filt("_g", new[] { Mk.C2 }, new[] { Mk.C9 }).With(f => { f.AttrList = true; f.Spelling = "long"; }))));
            yield return S("F-attr-foreign-before", "attributes", "[System.NonSerialized] [Pool]: a foreign attribute list before the injection attribute, on the only field", Mk.Prog("",
                Sys("K1", "Foreign", Mk.Pool("_a").With(f => f.Foreign = "before"))));
            yield return S("F-attr-foreign-list", "attributes", "[System.NonSerialized, Pool]: a foreign attribute first in the same list, on the only field", Mk.Prog("",
                Sys("K1", "Foreign", Mk.Pool("_a").With(f => f.Foreign = "list"))));
            yield return S("F-attr-foreign-mixed", "attributes", "[System.NonSerialized, Inc(...), Exc(...)] on a filter and [System.NonSerialized] [Shared] on a shared field", Mk.Prog("",
                Sys("K1", "ForeignMixed", Mk.Filt("_f", new[] { Mk.C0 }, new[] { Mk.C8 }).With(f => f.Foreign = "list"), Mk.Shr("_s").With(f => f.Foreign = "before"))));

            // Shared keys.
            var keys = new (string Id, string Key)[] {
                ("plain", "enemies"), ("quote", "a\"b"), ("backslash", "a\\b"), ("newline", "a\nb"), ("cr", "a\rb"), ("tab", "a\tb"),
                ("unicode", "ключ"), ("empty", ""), ("u2028", "a" + (char)0x2028 + "b"), ("nul", "a\0b"),
            };
            foreach (var (id, key) in keys) {
                yield return S($"F-key-{id}", "shared keys", $"[Shared(key)] with a {id} key", Mk.Prog("", Sys("K1", "Keyed", Mk.Shr("_s", Mk.Cfg0, key))));
            }
            yield return S("F-key-null-and-keyed", "shared keys", "[Shared] and [Shared(\"x\")] of the same type", Mk.Prog("", Sys("K1", "Keyed", Mk.Shr("_a"), Mk.Shr("_b", Mk.Cfg0, "x"))));
            yield return S("F-shared-types", "shared keys", "[Shared] string, object and int[]", Mk.Prog("", Sys("K1", "SharedTypes", Mk.Shr("_s", "string"), Mk.Shr("_o", "object"), Mk.Shr("_i", "int[]"))));
            // [Shared] of other reference types: an interface and an abstract class (registered with an implementation),
            // a closed generic class and a nested class, each also under a key.
            foreach (var (kind, type) in new[] { ("interface", SharedKinds.Iface), ("abstract", SharedKinds.Abstract), ("generic", SharedKinds.Generic), ("nested", SharedKinds.Nested) }) {
                yield return S($"F-shared-{kind}", "shared keys", $"[Shared] and [Shared(\"alt\")] of the {kind} type {type.Replace("global::StandShared.", "")}", Mk.Prog("",
                    Sys("K1", "SharedKind", Mk.Shr("_s", type), Mk.Shr("_k", type, "alt"))));
            }

            // Field names and modifiers.
            yield return S("F-field-world-shared", "fields", "injected fields named world and shared (the Init parameter names)", Mk.Prog("",
                Sys("K1", "Shadow", Mk.Pool("world"), Mk.Shr("shared"))));
            yield return S("F-static-field", "fields", "a static [Pool] field (ignored) next to an instance one", Mk.Prog("",
                Sys("K1", "WithStatic", Mk.Pool("_a"), Mk.Pool("_s", Mk.C1).With(f => f.Static = true))));
            yield return S("F-field-access", "fields", "public, protected, internal and private protected fields", Mk.Prog("",
                Sys("K1", "Access", Mk.Pool("_a", Mk.C0, "public"), Mk.Pool("_b", Mk.C1, "protected"), Mk.Pool("_c", Mk.C2, "internal"), Mk.Pool("_d", Mk.C3, "private protected"))));
            yield return S("F-filters", "fields", "filters: Inc only, Inc+Exc, Inc+Any, Any only, Inc+Exc+Any", Mk.Prog("",
                Sys("K1", "Filters", Mk.Filt("_i", new[] { Mk.C0 }), Mk.Filt("_ie", new[] { Mk.C0, Mk.C1 }, new[] { Mk.C8 }), Mk.Filt("_ia", new[] { Mk.C2 }, null, new[] { Mk.C4, Mk.C5 }),
                    Mk.Filt("_a", null, null, new[] { Mk.C4 }), Mk.Filt("_iea", new[] { Mk.C3 }, new[] { Mk.C9 }, new[] { Mk.C5 }))));
            yield return S("F-groups", "fields", "two groups in a chain (same Group<G0,G1> in base and derived, Group<G2,G3>)", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Grp("_g", Mk.G01, "protected")),
                Sys("K2", "Derived", Mk.Grp("_h", Mk.G01), Mk.Grp("_k", Mk.G23)).With(c => c.BaseId = "K1")));
            // Groups of four component types (docs: "[Group] | Group<...> | world.Group<...>()").
            yield return S("F-group4", "fields", "[Group] Group<G7, G8, G9, G10> (four types) next to a [Pool] field", Mk.Prog("",
                Sys("K1", "Quad", Mk.Grp("_q", Mk.G4x), Mk.Pool("_a"))));
            yield return S("F-group4-only", "fields", "a system whose only injected field is a [Group] of four types", Mk.Prog("",
                Sys("K1", "QuadOnly", Mk.Grp("_q", Mk.G4x))));
            yield return S("F-group4-chain", "fields", "generated base with a protected [Group] of four types, generated derived", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Grp("_q", Mk.G4x, "protected")),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => c.BaseId = "K1")), "K2");
            yield return S("F-nullable", "fields", "#nullable enable with Filter?, ComponentPool<T>? and Cfg0? in a chain", new ProgramModel {
                NullableEnable = true,
                Classes = {
                    Sys("K1", "BaseSystem", Mk.Filt("_f", new[] { Mk.C0 }, access: "protected"), Mk.Shr("_s")).With(c => c.Fields.ForEach(f => f.Nullable = true)),
                    Sys("K2", "Derived", Mk.Pool("_p", Mk.C1)).With(c => { c.BaseId = "K1"; c.Fields.ForEach(f => f.Nullable = true); }),
                },
            });

            // Generic and run systems.
            yield return S("F-generic-system", "generic", "generic system closed by a plain subclass", Mk.Prog("",
                Sys("K1", "PoolSystem", Mk.Pool("_p", "T")).With(c => c.TypeParams = "T"),
                Mk.Plain("K2", "Closed").Under(new ClassModel { Id = "K1" }, Mk.C0)), "K1");
            yield return S("F-generic-chain", "generic", "generic generated base, generated generic middle, generated closed leaf", Mk.Prog("",
                Sys("K1", "PoolSystem", Mk.Pool("_p", "T", "protected")).With(c => c.TypeParams = "T"),
                Sys("K2", "MidSystem", Mk.Pool("_m", "T")).With(c => { c.TypeParams = "T"; c.BaseId = "K1"; c.BaseArgs = "T"; }),
                Sys("K3", "Leaf", Mk.Pool("_l", Mk.C1)).With(c => { c.BaseId = "K2"; c.BaseArgs = Mk.C0; })));

            // Hand-written leaves over generated bases.
            yield return S("F-hand-leaf-override", "hand-written leaves", "hand-written leaf `public override void Init` with base.Init over a generated base", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Hand("K2", "Leaf", InitKind.PublicOverride).With(c => c.BaseId = "K1")), "K1");
            yield return S("F-hand-leaf-override-two", "hand-written leaves", "two hand-written overriding levels over a generated base", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Hand("K2", "Mid", InitKind.PublicOverride).With(c => c.BaseId = "K1"),
                Mk.Hand("K3", "Leaf", InitKind.PublicOverride).With(c => c.BaseId = "K2")), "K1");
            yield return S("F-hand-leaf-override-nobase", "hand-written leaves", "hand-written override that does not call base.Init (user code drops the base; not a finding)", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Hand("K2", "Leaf", InitKind.PublicOverrideNoBase).With(c => c.BaseId = "K1")), "K1");
            yield return S("F-hand-mid-override-nobase", "hand-written leaves", "generated base, hand-written override with base.Init, hand-written leaf override without base.Init (2.2.2: KECS013 also through hand-written overriding classes)", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Hand("K2", "Mid", InitKind.PublicOverride).With(c => c.BaseId = "K1"),
                Mk.Hand("K3", "Leaf", InitKind.PublicOverrideNoBase).With(c => c.BaseId = "K2")), "K1");
            foreach (var access in new[] { "protected", "private" }) {
                // 2.2.0/2.2.1: the Lib Init is not virtual without Lib subclasses, CS0506 (documented); 2.2.2: virtual, KECS013
                // for a protected field, and for a private one the documented limitation ("recognised only by injected fields
                // visible from the derived assembly").
                yield return S($"F-hand-leaf-override-nobase-lib-{access}", "hand-written leaves", $"generated base in Lib with a {access} [Pool], hand-written override in Main without base.Init", Mk.Prog("",
                    Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, access)).At(Asm.Lib), Mk.Hand("K2", "Leaf", InitKind.PublicOverrideNoBase).With(c => c.BaseId = "K1")), "K1");
            }
            yield return S("F-hand-leaf-plain", "hand-written leaves", "plain subclass of a generated system", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Plain("K2", "Leaf").With(c => c.BaseId = "K1")), "K1");
            yield return S("F-hand-leaf-over-generic", "hand-written leaves", "hand-written override over a closed generic generated base", Mk.Prog("",
                Sys("K1", "PoolSystem", Mk.Pool("_p", "T", "protected")).With(c => c.TypeParams = "T"),
                Mk.Hand("K2", "Leaf", InitKind.PublicOverride).With(c => { c.BaseId = "K1"; c.BaseArgs = Mk.C0; })), "K1");
            yield return S("F-hand-leaf-generated-below", "hand-written leaves", "generated base, hand-written override, generated leaf", Mk.Prog("",
                Sys("K1", "RootSystem", Mk.Pool("_a", Mk.C0, "protected")), Mk.Hand("K2", "Mid", InitKind.PublicOverride).With(c => c.BaseId = "K1"),
                Sys("K3", "Leaf", Mk.Pool("_l", Mk.C1)).With(c => c.BaseId = "K2")), "K3");

            // One Lib compiled for the Editor (with an editor-only generated subclass) and for the Player.
            foreach (bool editor in new[] { true, false }) {
                foreach (bool lists in new[] { true, false }) {
                    var p = new ProgramModel {
                        Classes = {
                            Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).At(Asm.Lib),
                            Sys("K2", "EditorDebugSystem", Mk.Pool("_e", Mk.C2)).With(c => { c.BaseId = "K1"; c.Define = "UNITY_EDITOR"; }).At(Asm.Lib),
                            Sys("K3", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K1"; c.ListsInit = lists; }),
                        },
                    };
                    if (editor) {
                        p.LibDefines.Add("UNITY_EDITOR");
                    }
                    yield return S($"F-editor-player/{(editor ? "editor" : "player")}/{(lists ? "list" : "nolist")}", "builds",
                        $"Lib with an editor-only generated subclass, compiled for the {(editor ? "Editor" : "Player")}; Derived in Main {(lists ? "lists" : "does not list")} IInitSystem, no base.Init", p, "K3");
                }
            }
        }

        private static IEnumerable<Scenario> Diagnostics() {
            ClassModel Sys(string id, string name, params FieldModel[] f) => Mk.Gen(id, name, f.Length == 0 ? new[] { Mk.Pool("_a") } : f);
            yield return S("N-KECS001", "diagnostics", "not partial", Mk.Prog("", Sys("K1", "Broken").With(c => { c.Partial = false; c.OnInit = false; })));
            yield return S("N-KECS002", "diagnostics", "Init(World, SharedData) next to injected fields", Mk.Prog("", Sys("K1", "Broken").With(c => { c.InitNextToFields = true; c.OnInit = false; })));
            yield return S("N-KECS002-explicit", "diagnostics", "explicit IInitSystem.Init next to injected fields", Mk.Prog("", Sys("K1", "Broken").With(c => { c.ExplicitInitNextToFields = true; c.ListsInit = true; c.OnInit = false; })));
            yield return S("N-KECS003-pool-on-filter", "diagnostics", "[Pool] on a Filter field", Mk.Prog("", Sys("K1", "Broken", Mk.Pool("_a").With(f => f.WrongType = "global::KenseiECS.Filter"))));
            yield return S("N-KECS003-inc-on-pool", "diagnostics", "[Inc] on a ComponentPool field", Mk.Prog("", Sys("K1", "Broken", Mk.Filt("_a", new[] { Mk.C0 }).With(f => f.WrongType = "global::KenseiECS.ComponentPool<global::StandShared.C0>"))));
            yield return S("N-KECS003-shared-int", "diagnostics", "[Shared] on an int field", Mk.Prog("", Sys("K1", "Broken", Mk.Shr("_a").With(f => f.WrongType = "int"))));
            yield return S("N-KECS004", "diagnostics", "[Exc] without [Inc] or [Any]", Mk.Prog("", Sys("K1", "Broken", Mk.Filt("_a", null, new[] { Mk.C8 })), Mk.Gen("K2", "Fine", Mk.Pool("_b"))), "K1");
            yield return S("N-KECS005", "diagnostics", "nested in a non-partial class", Mk.Prog("", Sys("K1", "Broken").With(c => c.Outers.Add(new OuterModel { Name = "Outer", Partial = false }))));
            yield return S("N-KECS007", "diagnostics", "[Inc] without types", Mk.Prog("", Sys("K1", "Broken", Mk.Filt("_a", new[] { "" }))));
            yield return S("N-KECS008", "diagnostics", "[Inc(typeof(NotComponent))]", Mk.Prog("", Sys("K1", "Broken", Mk.Filt("_a", new[] { "global::StandShared.NotComponent" }))));
            yield return S("N-KECS009", "diagnostics", "readonly injected field", Mk.Prog("", Sys("K1", "Broken", Mk.Pool("_a").With(f => f.Readonly = true))));
            yield return S("N-KECS010-struct", "diagnostics", "partial struct with injected fields", Mk.Prog("", Sys("K1", "Broken").With(c => { c.Kind = "struct"; c.OnInit = false; })));
            yield return S("N-KECS010-record", "diagnostics", "partial record with injected fields", Mk.Prog("", Sys("K1", "Broken").With(c => { c.Kind = "record"; c.OnInit = false; })));
            yield return S("N-KECS011-record", "diagnostics", "nested in a partial record", Mk.Prog("", Sys("K1", "Broken").With(c => c.Outers.Add(new OuterModel { Name = "Outer", Kind = OuterKind.Record }))));
            yield return S("N-KECS011-interface", "diagnostics", "nested in a partial interface", Mk.Prog("", Sys("K1", "Broken").With(c => c.Outers.Add(new OuterModel { Name = "IOuter", Kind = OuterKind.Interface }))));
            yield return S("N-KECS012", "diagnostics", "generated base chained, derived OnInit calls base.Init", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")), Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K1"; c.OnInitCallsBase = true; })));
            yield return S("N-cascade", "diagnostics", "non-partial generated base and a generated derived class (no second error)", Mk.Prog("",
                Sys("K1", "BaseSystem", Mk.Pool("_a", Mk.C0, "protected")).With(c => { c.Partial = false; c.OnInit = false; }),
                Sys("K2", "Derived", Mk.Pool("_b", Mk.C1)).With(c => { c.BaseId = "K1"; c.OnInit = false; })), "K2");
        }
    }
}
