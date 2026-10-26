using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    public enum ExpKind {
        /// <summary> Documented as supported: no diagnostic, clean generated code, every field injected. </summary>
        Works,
        /// <summary> Documented as an error: one of <see cref="Expectation.Ids"/> on this class. </summary>
        Reports,
        /// <summary> Not covered by the contract: a KECS error or working code are both fine, broken code or silent null fields are not. </summary>
        Either,
        /// <summary> A base of this class has its own error; the documented behaviour is no second error here. </summary>
        Blocked,
        /// <summary> Hand-written class: the generator writes nothing for it. </summary>
        Hand,
    }

    public sealed class Expectation {
        public ExpKind Kind;
        public List<string> Ids = new();
        public string Layout = "";
        /// <summary> Name-free form of the layout, used to group findings. </summary>
        public string Shape = "";
        public string Doc = "";
        /// <summary>
        /// The generated Init overrides the base Init (<see cref="ChainTarget"/>) and calls it: before its own fields on
        /// 2.2.0 and 2.2.1, after them and before the derived OnInit on 2.2.2 (Contract.FieldsBeforeBase).
        /// </summary>
        public bool Chained;
        public string ChainTarget;
        /// <summary>
        /// KECS006: the reasons the message may name, each a word of it ("is sealed", "is not virtual", ...) and the class
        /// whose name the reason carries (the base whose Init cannot be chained, or the class with the overload). Several
        /// when the layout has more than one; empty when the text is not checked.
        /// </summary>
        public List<(string Word, string Of)> Reasons = new();
        /// <summary> 2.1 layout without base.Init: the base chain runs only through the user's call (documented). </summary>
        public bool SkipAllowed;
        /// <summary> ... and injected fields above stay null although no rule of the contract asks for an error: a contract question. </summary>
        public bool SkipIsQuestion;
        /// <summary> ... and the documents say so for these fields (2.2.2: private injected fields of a base in another assembly are not seen): documented. </summary>
        public bool SkipIsDocumented;
        /// <summary>
        /// Whether the class still gets its generated Init (derived classes then chain to it): false after
        /// a class-level error, KECS006 or a failed base; true when it works, after KECS012 (the class is
        /// still emitted) or a field error with another valid field; null when the documents leave it open.
        /// </summary>
        public bool? Emitted;
        /// <summary> Claim (Claims.cs) the expectation rests on; null for the universal rules. </summary>
        public string Claim;

        public override string ToString() => Kind switch {
            ExpKind.Reports => $"error {string.Join("/", Ids)} ({Layout})",
            ExpKind.Blocked => $"no second error ({Layout})",
            _ => $"{Kind.ToString().ToLowerInvariant()} ({Layout})",
        };
    }

    /// <summary>
    /// The documented generator contract as an executable model, independent of the generator under
    /// test. Profile "2.2" transcribes docs/guides/source-generator.md and the CHANGELOG of the 2.2 line;
    /// profile "2.1" the 2.1.1 changelog. Inside 2.2 the rules that 2.2.2 changed are switched by the
    /// claims of the tree (Claims.cs): a tree whose documents carry the 2.2.2 sentence is held to the 2.2.2
    /// rule (the derived fields before the base Init, a generated Init virtual unless sealed, KECS006 for the
    /// 2.1 layout without base.Init, KECS013, a generic Init helper that takes no call), otherwise to the
    /// 2.2.0 one ("the base runs only through your call" for the 2.1 layout). Whatever the documents do not
    /// settle is <see cref="ExpKind.Either"/>, where only the universal oracles apply, so an undocumented
    /// choice of the generator is never a finding.
    /// </summary>
    public sealed class Contract {
        public const string DocInheritance = "docs/guides/source-generator.md, Inheritance";
        public const string DocDiagnostics = "docs/guides/source-generator.md, Diagnostics";
        public const string Log220 = "CHANGELOG 2.2.0, Generator";
        public const string Log211 = "CHANGELOG 2.1.1, Generator";

        public readonly string Profile;
        /// <summary> The claims of the tree under test (null: no gating, e.g. when judging by another tree's contract). </summary>
        public readonly ClaimSet Claims;
        private readonly ProgramModel _p;
        private readonly Dictionary<string, Expectation> _memo = new();
        private readonly HashSet<string> _busy = new();

        public Contract(ProgramModel p, string profile, ClaimSet claims = null) {
            _p = p;
            Profile = profile;
            Claims = claims;
        }

        public bool Is22 => Profile != "2.1";

        private bool Has(string claim) => Is22 && Claims != null && Claims.Has(claim);

        /// <summary> 2.2.2: "The generated Init is public virtual in every class that is not sealed" (a generated base in another assembly is chained). </summary>
        public bool VirtualEverywhere => Has("virtual-init-222");

        /// <summary> 2.2.2: "A class in this layout without a base.Init call is KECS006" (except over an explicit base IInitSystem.Init). </summary>
        public bool Layout21NeedsCall => Has("layout21-kecs006-222");

        /// <summary> 2.2.2: a generic Init&lt;T&gt;(World, SharedData) helper "does not take the call and is allowed". </summary>
        public bool GenericHelperAllowed => Has("generic-helper-allowed");

        /// <summary> 2.2.2: "The derived fields are filled first, then the base Init runs (...), then the derived OnInit". 2.2.0/2.2.1: base.Init before the derived fields. </summary>
        public bool FieldsBeforeBase => Has("inheritance-order-222");

        /// <summary> 2.2.2: the warning KECS013 on a hand-written override of a generated Init without base.Init. </summary>
        public bool Kecs013 => Has("kecs013");

        /// <summary> 2.2.2: an override of a base OnInit hook "runs once, after the derived fields are filled" (no CS0111/CS0108/CS0114 over the hook). </summary>
        public bool HookSupported => Has("hook-222");

        /// <summary> 2.2.2: "a derived class that lists IInitSystem itself is still chained over them" (hand-written overrides of a public Init next to an explicit IInitSystem.Init that does not call it). </summary>
        public bool ChainOverOverrides => Has("chain-over-overrides-222");

        /// <summary> 2.2.2: KECS006 of the 2.1 layout sees only the protected and public injected fields of a base in another assembly. </summary>
        public bool Kecs006LibVisible => Has("kecs006-lib-visible-222");

        /// <summary> Whether an Init overload in a class between takes the generated base.Init call (a KECS006 reason). </summary>
        public bool Captures(InitKind k) => IsCaptureOverload(k) && !(k == InitKind.GenericHelper && GenericHelperAllowed);

        public static bool IsExactInitMethod(InitKind k) => k switch {
            InitKind.PublicVirtual or InitKind.PublicAbstract or InitKind.PublicNonVirtual or InitKind.PublicNewVirtual
                or InitKind.PublicNewNonVirtual or InitKind.PublicNewForward or InitKind.PublicOverride or InitKind.PublicOverrideNoBase
                or InitKind.SealedOverride or InitKind.Explicit or InitKind.VirtualThenExplicit or InitKind.ExplicitThenVirtual
                or InitKind.ExplicitForward => true,
            _ => false,
        };

        public static bool IsOverrideKind(InitKind k) => k is InitKind.PublicOverride or InitKind.PublicOverrideNoBase or InitKind.SealedOverride;

        public static bool IsCaptureOverload(InitKind k) => k is InitKind.OverloadObject or InitKind.OverloadOptional or InitKind.GenericHelper
            or InitKind.InParam or InitKind.OverloadParams;

        public static bool IsNonMethodInit(InitKind k) => k is InitKind.StaticInit or InitKind.FieldNamedInit or InitKind.PropertyNamedInit;

        public static bool IsExplicitKind(InitKind k) => k is InitKind.Explicit or InitKind.VirtualThenExplicit or InitKind.ExplicitThenVirtual
            or InitKind.ExplicitForward;

        /// <summary> Whether the rendered hand-written Init of <paramref name="c"/> starts with base.Init(world, shared). </summary>
        public static bool HandCallsBase(ProgramModel p, ClassModel c) {
            switch (c.Init) {
                case InitKind.PublicOverride:
                case InitKind.SealedOverride:
                    return Renderer.NearestInit(p, c).Kind != InitKind.PublicAbstract;
                case InitKind.PublicNewForward:
                    return CanCallBase(p, c);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether `base.Init(world, shared)` in <paramref name="d"/> compiles once generated members
        /// exist: the stand writes such a call only then, so a leftover error is never its own.
        /// </summary>
        public static bool CanCallBase(ProgramModel p, ClassModel d) {
            foreach (var a in p.Ancestors(d)) {
                if (a.IsGenerated) {
                    return true;
                }
                switch (a.Init) {
                    case InitKind.None:
                        continue;
                    case InitKind.Explicit:
                    case InitKind.PublicAbstract:
                    case InitKind.GenericHelper:
                    case InitKind.StaticInit:
                    case InitKind.FieldNamedInit:
                    case InitKind.PropertyNamedInit:
                        return false;
                    default:
                        return true;
                }
            }
            return false;
        }

        public bool ListsInitIface(ClassModel c) => c.ListsInit || (c.IfaceId != null && _p.Iface(c.IfaceId)?.IncludesInit == true);

        public Expectation For(ClassModel c) {
            if (_memo.TryGetValue(c.Id, out var e)) {
                return e;
            }
            if (!_busy.Add(c.Id)) {
                return new Expectation { Kind = ExpKind.Either, Layout = "cyclic hierarchy", Shape = "either:cycle" };
            }
            e = Compute(c);
            _busy.Remove(c.Id);
            _memo[c.Id] = e;
            return e;
        }

        /// <summary>
        /// Every injected field is marked through a using alias ([InjPool] for KenseiECS.PoolAttribute).
        /// docs/architecture.md: the syntax receiver flags a class by the short attribute names Inc, Exc,
        /// Any, Pool, Group, Shared, so such a class is documented to get nothing.
        /// </summary>
        public static bool IsAliasOnly(ClassModel c) => c.IsGenerated && c.Fields.Where(f => !f.Static).All(f => f.Spelling == "alias");

        private static readonly string[] ClassLevelErrors = { "KECS001", "KECS002", "KECS005", "KECS010", "KECS011" };

        private Expectation Compute(ClassModel c) {
            if (!c.IsGenerated) {
                return new Expectation { Kind = ExpKind.Hand, Layout = "hand-written", Shape = "hand" };
            }
            if (IsAliasOnly(c)) {
                return new Expectation { Kind = ExpKind.Either, Layout = "every field marked through a using alias (the receiver looks for the short names, docs/architecture.md)", Shape = "either:alias-only", Doc = "docs/architecture.md, The source generator, step 1", Emitted = false };
            }
            var ids = PerClassErrors(c);
            // The generator emits a class with a bad field as long as another field is valid; only then
            // does its inheritance count.
            bool emittable = !ids.Any(ClassLevelErrors.Contains) && c.Fields.Any(f => !f.Static && FieldValid(f));
            var inherited = emittable ? (Is22 ? Inherit22(c) : Inherit21(c)) : null;
            if (inherited != null && inherited.Kind == ExpKind.Either && c.ListsInit && !c.OnInitCallsBase) {
                // Whatever the generator decides here, a class that lists IInitSystem itself and does not
                // call base.Init is the 2.1 layout, where the base runs only through that call.
                inherited.SkipAllowed = true;
                inherited.SkipIsQuestion = AnyFieldsAbove(c);
            }
            if (inherited != null && inherited.Emitted == null) {
                inherited.Emitted = inherited.Kind switch {
                    ExpKind.Works => true,
                    ExpKind.Reports => inherited.Ids.All(id => id == "KECS012"),
                    ExpKind.Blocked => false,
                    _ => null,
                };
            }
            if (ids.Count > 0) {
                return new Expectation { Kind = ExpKind.Reports, Ids = ids, Layout = "per-class error " + string.Join("/", ids), Shape = "perclass:" + string.Join("/", ids), Doc = DocDiagnostics, Emitted = inherited?.Emitted ?? false };
            }
            return inherited;
        }

        private static bool FieldValid(FieldModel f) {
            if (f.Readonly || f.WrongType != null) {
                return false;
            }
            if (f.Kind == FieldKind.Filter) {
                bool emptyInc = f.Inc.Count == 1 && f.Inc[0] == "";
                if (f.Inc.Count == 0 && f.Any.Count == 0) {
                    return false;
                }
                if (emptyInc && f.Any.Count == 0) {
                    return false;
                }
                if (f.Inc.Concat(f.Exc).Concat(f.Any).Any(t => t.Contains("NotComponent"))) {
                    return false;
                }
            }
            return true;
        }

        private static Expectation Works(string layout, string shape, string doc, string claim = null) => new() { Kind = ExpKind.Works, Layout = layout, Shape = shape, Doc = doc, Claim = claim };

        private static Expectation Either(string layout, string shape) => new() { Kind = ExpKind.Either, Layout = layout, Shape = "either:" + shape, Doc = "not covered by the documents" };

        private static Expectation Reports(string id, string layout, string shape, string doc, string claim = null) => new() { Kind = ExpKind.Reports, Ids = { id }, Layout = layout, Shape = shape, Doc = doc, Claim = claim };

        private List<string> PerClassErrors(ClassModel c) {
            var ids = new List<string>();
            if (c.Kind != "class") {
                ids.Add("KECS010");
                return ids;
            }
            foreach (var f in c.Fields.Where(f => !f.Static)) {
                if (f.Readonly) {
                    ids.Add("KECS009");
                    continue;
                }
                if (f.WrongType != null) {
                    ids.Add("KECS003");
                    continue;
                }
                if (f.Kind == FieldKind.Filter) {
                    bool emptyInc = f.Inc.Count == 1 && f.Inc[0] == "";
                    if (f.Inc.Count == 0 && f.Any.Count == 0) {
                        ids.Add("KECS004");
                    } else if (emptyInc && f.Any.Count == 0) {
                        ids.Add("KECS007");
                    } else if (f.Inc.Concat(f.Exc).Concat(f.Any).Any(t => t.Contains("NotComponent"))) {
                        ids.Add("KECS008");
                    }
                }
            }
            if (!c.Partial) {
                ids.Add("KECS001");
            }
            if (c.InitNextToFields || c.ExplicitInitNextToFields) {
                ids.Add("KECS002");
            }
            foreach (var o in c.Outers) {
                if (o.Kind == OuterKind.Record || o.Kind == OuterKind.Interface) {
                    ids.Add("KECS011");
                } else if (!o.Partial) {
                    ids.Add("KECS005");
                }
            }
            return ids.Distinct().ToList();
        }

        private List<ClassModel> Chain(ClassModel c) => _p.Ancestors(c).ToList();

        /// <summary> Whether the hand-written Init of <paramref name="a"/> is its IInitSystem.Init implementation (C# interface mapping). </summary>
        public bool IsImplementation(ClassModel a) {
            switch (a.Init) {
                case InitKind.PublicVirtual:
                case InitKind.PublicAbstract:
                case InitKind.PublicNewVirtual:
                case InitKind.PublicNonVirtual:
                case InitKind.PublicNewNonVirtual:
                case InitKind.PublicNewForward:
                    // A new slot implements the interface only where the interface is (re)listed.
                    return ListsInitIface(a);
                case InitKind.PublicOverride:
                case InitKind.PublicOverrideNoBase:
                case InitKind.SealedOverride:
                    var (owner, _) = Renderer.NearestInit(_p, a);
                    if (owner == null) {
                        return false;
                    }
                    return owner.IsGenerated || IsImplementation(owner);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 2.2.0/2.2.1: a generated base in Lib gets a virtual Init when Lib has a generated or hand-written overriding
        /// subclass of it (docs: Inheritance). 2.2.2: the generated Init is virtual in every class that is not sealed.
        /// </summary>
        public bool LibGeneratedIsVirtual(ClassModel a) {
            if (VirtualEverywhere) {
                return !a.Sealed;
            }
            foreach (var s in _p.ActiveClasses(Asm.Lib)) {
                if (s.Id == a.Id) {
                    continue;
                }
                var (owner, _) = Renderer.NearestInit(_p, s);
                // A subclass whose fields are all marked through an alias gets nothing (docs/architecture.md).
                if (owner?.Id == a.Id && ((s.IsGenerated && !IsAliasOnly(s)) || IsOverrideKind(s.Init))) {
                    return true;
                }
            }
            // A generated Init that itself overrides (the class chains to its own base in Lib) is virtual too.
            var own = For(a);
            return own.Kind == ExpKind.Works && own.Shape.StartsWith("chain:");
        }

        /// <summary> Injected fields above <paramref name="d"/> that the 2.1.1 KECS006 rule sees (in source, or public/protected in a referenced assembly). </summary>
        private bool VisibleFieldsAbove(ClassModel d) {
            foreach (var a in Chain(d)) {
                foreach (var f in a.Fields.Where(f => !f.Static)) {
                    if (a.Asm == d.Asm || IsMetadataVisible(f.Access)) {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool AnyFieldsAbove(ClassModel d) => Chain(d).Any(a => a.IsGenerated);

        public static bool IsMetadataVisible(string access) => access is "public" or "protected" or "protected internal";

        private Expectation Inherit22(ClassModel d) {
            var chain = Chain(d);
            if (chain.Count == 0) {
                return Works("standalone", "standalone", DocInheritance, "generated-init");
            }
            if (!chain.Any(a => a.IsGenerated || ListsInitIface(a))) {
                var member = chain.FirstOrDefault(a => a.Init != InitKind.None);
                return member != null
                    ? Works($"over a non-system base with an Init member ({member.Init}, hidden)", "nonsystem:" + member.Init, Log220 + ": the generated Init is declared new where it hides one (no CS0108/CS0114)", "generated-init")
                    : Works("over a non-system base", "nonsystem", DocInheritance, "generated-init");
            }
            ClassModel captureBy = null;
            foreach (var a in chain) {
                if (a.IsGenerated) {
                    // A generated base that gets no Init of its own (its error; in Lib that fails the Lib
                    // build) blocks the chain, in this assembly or another.
                    var ea = For(a);
                    if (ea.Emitted == null) {
                        var capture = captureBy ?? CaptureAbove(a);
                        return Either($"base {a.Plain}: its own layout is not settled by the documents ({ea.Layout})" + (capture != null ? $"; an Init overload in {capture.Plain} ({capture.Init}) would take this class's generated base.Init" : ""),
                            "unsettled-base:" + ea.Shape + (capture != null ? ":capture-above-" + capture.Init : ""));
                    }
                    if (ea.Emitted == false) {
                        string root = ea.Kind == ExpKind.Blocked ? ea.Shape : "blocked:" + ea.Shape;
                        if (captureBy != null) {
                            // An Init overload in between is a KECS006 reason of its own (CHANGELOG 2.2.0).
                            return Either($"base {a.Plain} has its own error, and {captureBy.Plain} declares an Init overload ({captureBy.Init})", root + ":capture-" + captureBy.Init);
                        }
                        return new Expectation { Kind = ExpKind.Blocked, Layout = $"base {a.Plain} has its own error", Shape = root, Doc = Log220 + ": no second error", Claim = "no-second-error" };
                    }
                    var above = captureBy == null ? CaptureAbove(a) : null;
                    if (a.Asm == d.Asm) {
                        return ChainTo(d, a, false, captureBy ?? above, "chained to a generated base", "gen", above != null);
                    }
                    // The Lib base's own layout decides whether its generated Init is virtual (and carries
                    // the mechanism when it is not, a composite interface for example).
                    return LibGeneratedIsVirtual(a)
                        ? ChainTo(d, a, false, captureBy ?? above, "chained to a generated base in Lib (virtual there)", "gen-lib-virtual(" + ea.Shape + ")", above != null)
                        : NotOverridable(d, a, "generated base in another assembly with a non-virtual Init", "gen-lib-nonvirtual", captureBy);
                }
                if (a.Init == InitKind.None) {
                    continue;
                }
                if (IsCaptureOverload(a.Init)) {
                    // 2.2.2: a generic helper the call cannot infer takes nothing and is passed over.
                    if (Captures(a.Init)) {
                        captureBy ??= a;
                    }
                    continue;
                }
                if (IsNonMethodInit(a.Init)) {
                    return Either($"a non-method member named Init ({a.Init}) in {a.Plain}", "init-member:" + a.Init);
                }
                string via = a.IfaceId != null ? "-via-" + (_p.Iface(a.IfaceId)?.IncludesInit == true ? "composite" : "iface") : "";
                if (IsOverrideKind(a.Init) && Renderer.NearestInit(_p, a).Owner is ClassModel owner && owner.IsGenerated) {
                    // The override sits on a generated Init: if that class gets no Init (its own error),
                    // the chain is blocked there.
                    var eo = For(owner);
                    if (eo.Emitted == false) {
                        string root = eo.Kind == ExpKind.Blocked ? eo.Shape : "blocked:" + eo.Shape;
                        return captureBy != null
                            ? Either($"base {owner.Plain} has its own error, and {captureBy.Plain} declares an Init overload ({captureBy.Init})", root + ":capture-" + captureBy.Init)
                            : new Expectation { Kind = ExpKind.Blocked, Layout = $"base {owner.Plain} (below the hand-written {a.Plain}) has its own error", Shape = root, Doc = Log220 + ": no second error", Claim = "no-second-error" };
                    }
                    if (eo.Emitted == null) {
                        return Either($"base {owner.Plain}: its own layout is not settled by the documents ({eo.Layout})", "unsettled-base:" + eo.Shape);
                    }
                }
                var overloadAbove = captureBy == null ? CaptureAbove(a) : null;
                switch (a.Init) {
                    case InitKind.PublicVirtual:
                    case InitKind.PublicAbstract:
                    case InitKind.PublicNewVirtual:
                    case InitKind.PublicOverride:
                    case InitKind.PublicOverrideNoBase:
                        if (IsImplementation(a)) {
                            return ChainTo(d, a, a.Init == InitKind.PublicAbstract, captureBy ?? overloadAbove, a.IfaceId != null
                                ? $"chained to a hand-written {a.Init} Init (IInitSystem through {_p.Iface(a.IfaceId)?.Name})"
                                : $"chained to a hand-written {a.Init} Init", "hand-" + a.Init + via + (a.Asm != d.Asm ? "-lib" : ""), overloadAbove != null);
                        }
                        if (ChainOverOverrides && IsOverrideKind(a.Init) && ListsInitIface(d) && OverridesPublicNextToExplicit(a)) {
                            // 2.2.2: hand-written overrides of a public Init that an explicit IInitSystem.Init next to it does not
                            // call: "a derived class that lists IInitSystem itself is still chained over them, since the interface
                            // then maps to its generated Init" (with base.Init in OnInit that is KECS012, as for any chained class).
                            var chained = ChainTo(d, a, false, captureBy ?? overloadAbove,
                                $"chained over the hand-written {a.Init} Init of {a.Plain}, which overrides a public Init next to an explicit IInitSystem.Init that does not call it (the derived class lists IInitSystem itself)",
                                "over-explicit-" + a.Init + via + (a.Asm != d.Asm ? "-lib" : ""), overloadAbove != null);
                            chained.Doc = DocInheritance + " (2.2.2: 'a derived class that lists IInitSystem itself is still chained over them, since the interface then maps to its generated Init')"
                                          + (chained.Kind == ExpKind.Reports ? "; " + chained.Doc : "");
                            if (chained.Kind == ExpKind.Works) {
                                chained.Claim = "chain-over-overrides-222";
                            }
                            return chained;
                        }
                        return NotOverridable(d, a, $"{a.Plain}.Init does not implement IInitSystem.Init", "not-impl-" + a.Init + (a.Asm != d.Asm ? "-lib" : ""), captureBy);
                    case InitKind.SealedOverride:
                        return NotOverridable(d, a, $"{a.Plain}.Init is sealed", "sealed", captureBy);
                    case InitKind.PublicNonVirtual:
                    case InitKind.PublicNewNonVirtual:
                    case InitKind.PublicNewForward:
                        return NotOverridable(d, a, $"{a.Plain}.Init is not virtual", "nonvirtual-" + a.Init, captureBy);
                    default:
                        return NotOverridable(d, a, $"{a.Plain} implements IInitSystem.Init explicitly", "explicit-" + a.Init, captureBy);
                }
            }
            return Either("no Init found above", "no-init");
        }

        /// <summary>
        /// C# binds the generated base.Init(world, shared) to the most derived applicable Init
        /// <em>declaration</em>; an override (hand-written, or a generated Init that itself overrides) is
        /// not one, so an applicable Init overload between the chained override and the class that
        /// declares the virtual Init takes the generated call as well (the KECS006 overload case of the
        /// documents: "C# would bind the generated base.Init(world, shared) to it").
        /// </summary>
        private ClassModel CaptureAbove(ClassModel target) {
            var seen = new HashSet<string>();
            for (var t = target; t != null && seen.Add(t.Id);) {
                bool isOverride = t.IsGenerated ? IsGeneratedOverride(t) : IsOverrideKind(t.Init);
                if (!isOverride) {
                    return null;
                }
                ClassModel next = null;
                foreach (var x in _p.Ancestors(t)) {
                    if (x.IsGenerated && _p.IsActive(x)) {
                        next = x;
                        break;
                    }
                    // Applicable overloads; a generic helper cannot infer its type argument.
                    if (x.Init is InitKind.OverloadObject or InitKind.OverloadOptional or InitKind.OverloadParams or InitKind.InParam) {
                        return x;
                    }
                    if (IsExactInitMethod(x.Init) || IsNonMethodInit(x.Init)) {
                        next = x;
                        break;
                    }
                }
                t = next;
            }
            return null;
        }

        // A generated Init that overrides a base Init (it chains); the 2.1 layout and a standalone
        // system declare their own.
        private bool IsGeneratedOverride(ClassModel g) {
            string s = For(g).Shape;
            return !(s == "standalone" || s.StartsWith("nonsystem") || s.StartsWith("layout21:") || s.StartsWith("either:layout21")
                || s.StartsWith("either:alias") || s.StartsWith("v21:"));
        }

        private Expectation ChainTo(ClassModel d, ClassModel target, bool abstractTarget, ClassModel captureBy, string layout, string shape, bool captureAbove = false) {
            // Over an abstract base Init the generated override has no base.Init call, so an overload in
            // between cannot take it; the documents explain the overload rule by that call, but the
            // Inheritance section also lists overloads among "its own non-override Init" in between.
            if (captureBy != null && abstractTarget) {
                return Either($"{layout}, with an Init overload in {captureBy.Plain} ({captureBy.Init}) over an abstract base Init", "chain:" + shape + ":abstract-capture-" + captureBy.Init);
            }
            if (captureBy != null) {
                // An overload above the chained override: the generator chains (it stops at the override)
                // and its own base.Init is taken, whether the class lists IInitSystem or not.
                if (!d.ListsInit || captureAbove) {
                    return WithReason(Reports("KECS006", $"{layout}, but {captureBy.Plain} declares another Init overload ({captureBy.Init}){(captureAbove ? " above the chained override, which C# binds the generated base.Init to" : "")}",
                        "chain:" + shape + (captureAbove ? ":capture-above-" : ":capture-") + captureBy.Init,
                        DocDiagnostics + " (KECS006: another Init overload)", "kecs006-2.2"), CaptureReasons(d, target, captureBy));
                }
                if (!d.OnInitCallsBase && VisibleFieldsAbove(d)) {
                    return WithReason(Reports("KECS006", $"{layout}, Init overload in {captureBy.Plain}, class lists IInitSystem without base.Init, base fields visible",
                        "layout21:capture-" + captureBy.Init + ":nocall:visible-fields",
                        DocDiagnostics + " (KECS006: another Init overload); " + Log211 + ": KECS006 when the base has injected fields and OnInit does not call base.Init", "kecs006-2.2"),
                        CaptureReasons(d, target, captureBy));
                }
                return Either($"{layout}, Init overload in {captureBy.Plain}, class lists IInitSystem", "capture-" + captureBy.Init + "-list" + (d.OnInitCallsBase ? "-calls" : ""));
            }
            if (d.OnInitCallsBase && !abstractTarget) {
                return Reports("KECS012", $"{layout}, OnInit calls base.Init", "chain:" + shape + ":calls-base", DocDiagnostics + " (KECS012)", "kecs012");
            }
            return new Expectation {
                Kind = ExpKind.Works, Layout = layout, Shape = "chain:" + shape + (d.ListsInit ? ":lists" : ""), Doc = DocInheritance,
                Chained = !abstractTarget, ChainTarget = target.Id, Claim = "inheritance",
            };
        }

        /// <summary>
        /// Whether the hand-written override <paramref name="a"/> (through hand-written overrides above it) overrides a
        /// public virtual Init that has an explicit IInitSystem.Init next to it which does not call it.
        /// </summary>
        private bool OverridesPublicNextToExplicit(ClassModel a) {
            var seen = new HashSet<string>();
            var cur = a;
            while (cur != null && IsOverrideKind(cur.Init) && seen.Add(cur.Id)) {
                cur = Renderer.NearestInit(_p, cur).Owner;
            }
            return cur != null && !cur.IsGenerated && cur.Init is InitKind.VirtualThenExplicit or InitKind.ExplicitThenVirtual;
        }

        private Expectation NotOverridable(ClassModel d, ClassModel a, string why, string shape, ClassModel captureBy) {
            var reasons = ReasonsOf(shape, a);
            if (shape.StartsWith("not-impl-")) {
                // The Init of a does not implement IInitSystem.Init because the interface maps to an explicit
                // implementation further up: the message may name that one instead.
                reasons.AddRange(_p.Ancestors(a).Where(x => IsExplicitKind(x.Init)).Select(x => ("explicitly", x.Id)));
            }
            if (captureBy != null) {
                // An overload in between is a KECS006 reason of its own, and the message may name it instead.
                reasons.AddRange(CaptureReasons(d, a, captureBy));
            }
            if (d.ListsInit) {
                bool nocall = !d.OnInitCallsBase;
                bool visible = VisibleFieldsAbove(d);
                bool fields = AnyFieldsAbove(d);
                string fieldsTag = visible ? ":visible-fields" : fields ? ":invisible-fields" : "";
                if (Layout21NeedsCall && nocall) {
                    // 2.2.2: "A class in this layout without a base.Init call is KECS006 when the base Init would never run
                    // (...). Not reported: an explicit base IInitSystem.Init, which base.Init cannot reach anyway, and a new
                    // Init in between that does not implement IInitSystem.Init, with no injected fields above it. Such an Init
                    // is taken for an unrelated method: the generated Init replaces the interface's Init without a diagnostic,
                    // and the Init it hides does not run."
                    var unrelated = UnrelatedNewInit(d);
                    if (unrelated != null && !fields && Has("layout21-new-unrelated-222")) {
                        return new Expectation {
                            Kind = ExpKind.Works,
                            Layout = $"2.1 layout ({why}) without base.Init over a new Init in {unrelated.Plain} that does not implement IInitSystem.Init, no injected fields above (documented: an unrelated method, the Init it hides does not run)",
                            Shape = "layout21:" + shape + ":nocall:new-unrelated",
                            Doc = DocInheritance + " (2.2.2: 'Not reported: ... a new Init in between that does not implement IInitSystem.Init, with no injected fields above it')",
                            SkipAllowed = true, SkipIsQuestion = false, Claim = "layout21-new-unrelated-222",
                        };
                    }
                    if (unrelated != null && fields && !visible && Kecs006LibVisible && Has("layout21-new-unrelated-222")) {
                        // 2.2.2: the injected fields above are all in another assembly and private (or internal): "of a base in
                        // another assembly only the protected and public ones are seen, as for KECS013, so a base there with only
                        // private injected fields is not reported". No KECS006, and those fields stay null: documented.
                        return new Expectation {
                            Kind = ExpKind.Works,
                            Layout = $"2.1 layout ({why}) without base.Init over a new Init in {unrelated.Plain} that does not implement IInitSystem.Init; the injected fields above are private fields of a base in another assembly (documented: not seen, not reported, they stay null)",
                            Shape = "layout21:" + shape + ":nocall:new-unrelated:invisible-fields",
                            Doc = DocInheritance + " (2.2.2: 'of a base in another assembly only the protected and public ones are seen, as for KECS013, so a base there with only private injected fields is not reported')",
                            SkipAllowed = true, SkipIsQuestion = false, SkipIsDocumented = true, Claim = "kecs006-lib-visible-222",
                        };
                    }
                    if (!BaseInitIsExplicit(d)) {
                        return WithReason(Reports("KECS006", $"2.1 layout ({why}) without base.Init", "layout21:" + shape + (captureBy != null ? ":capture-" + captureBy.Init : "") + ":nocall" + fieldsTag,
                            DocInheritance + " (2.2.2: a class in the 2.1 layout without a base.Init call is KECS006)", "layout21-kecs006-222"), reasons);
                    }
                    return new Expectation {
                        Kind = ExpKind.Works, Layout = $"2.1 layout ({why}) without base.Init over an explicit base IInitSystem.Init (the documented exception)",
                        Shape = "layout21:" + shape + ":nocall:explicit-exception", Doc = DocInheritance + " (2.2.2: the exception is an explicit base IInitSystem.Init, which base.Init cannot reach)",
                        SkipAllowed = true, SkipIsQuestion = false, Claim = "layout21-kecs006-222",
                    };
                }
                if (Layout21NeedsCall && HookSupported && d.Hook == HookKind.Override && HookCallableBy(a)) {
                    // 2.2.2: "In the 2.1 layout (...) an override of a hook that this base Init can call is KECS006".
                    return WithReason(Reports("KECS006", $"2.1 layout ({why}) that overrides an OnInit hook the base Init calls", "layout21:" + shape + ":calls:hook-override",
                        DocInheritance + ", A base with its own OnInit (2.2.2)", "hook-layout21-222"), reasons);
                }
                if (captureBy != null) {
                    // The 2.1 layout has no generated base.Init call for the overload to take (and the
                    // program's own call binds to the overload); whether the overload rule of KECS006
                    // applies here is not settled by the documents.
                    return Either($"2.1 layout ({why}) with an Init overload in {captureBy.Plain} ({captureBy.Init})", "layout21:" + shape + ":capture-" + captureBy.Init + (d.OnInitCallsBase ? ":calls" : ""));
                }
                if (d.OnInitCallsBase) {
                    return Works($"2.1 layout over a base that cannot be chained ({why}), OnInit calls base.Init", "layout21:" + shape + ":calls",
                        DocInheritance + " (2.1 layout still works)", "layout21-works");
                }
                // 2.2.0 and 2.2.1: "the base runs only through your call" (and the CHANGELOG 2.2.0 reports KECS006 for
                // these bases "unless the class lists IInitSystem itself"). 2.1.1 reported KECS006 where the base fields
                // are visible: its loss is the cross-version rule R-LOST-SIGNAL (GN1), not a defect of this tree.
                return new Expectation {
                    Kind = ExpKind.Works, Layout = $"2.1 layout ({why}) without base.Init", Shape = "layout21:" + shape + ":nocall" + fieldsTag,
                    Doc = DocInheritance + " (the base runs only through your call)",
                    SkipAllowed = true, SkipIsQuestion = fields, Claim = "layout21-works",
                };
            }
            return WithReason(Reports("KECS006", why, "nochain:" + shape, DocDiagnostics + " (KECS006)", "kecs006-2.2"), reasons);
        }

        private static Expectation WithReason(Expectation e, List<(string Word, string Of)> reasons) {
            e.Reasons = reasons;
            return e;
        }

        private static Expectation WithOverloadReason(Expectation e, ClassModel captureBy) => WithReason(e, OverloadReasons(captureBy));

        /// <summary> The reasons of every Init overload between <paramref name="d"/> and <paramref name="upTo"/> (and of <paramref name="captureBy"/>): the message may name any of them. </summary>
        private List<(string Word, string Of)> CaptureReasons(ClassModel d, ClassModel upTo, ClassModel captureBy) {
            var list = new List<(string, string)>();
            foreach (var x in _p.Ancestors(d)) {
                if (x.Id == upTo?.Id) {
                    break;
                }
                if (IsCaptureOverload(x.Init)) {
                    list.AddRange(OverloadReasons(x));
                }
            }
            if (captureBy != null && list.All(r => r.Item2 != captureBy.Id)) {
                list.AddRange(OverloadReasons(captureBy));
            }
            return list;
        }

        /// <summary>
        /// The reasons of an Init overload in <paramref name="by"/>: "another Init overload". An Init(in World, SharedData)
        /// or Init&lt;T&gt;(World, SharedData) has the parameter types of Init, and its message may call it "not virtual" instead.
        /// </summary>
        private static List<(string Word, string Of)> OverloadReasons(ClassModel by) {
            var list = new List<(string, string)> { ("another Init overload", by.Id) };
            if (by.Init is InitKind.InParam or InitKind.GenericHelper) {
                list.Add(("is not virtual", by.Id));
            }
            return list;
        }

        /// <summary> The reasons the KECS006 message may name for a layout shape of <see cref="Inherit22"/>, with the class each names. </summary>
        private static List<(string Word, string Of)> ReasonsOf(string shape, ClassModel a) {
            var list = new List<(string, string)>();
            if (shape == "sealed") {
                list.Add(("is sealed", a.Id));
            } else if (shape.StartsWith("nonvirtual-") || shape.StartsWith("gen-lib-nonvirtual")) {
                list.Add(("is not virtual", a.Id));
            } else if (shape.StartsWith("explicit-")) {
                list.Add(("explicitly", a.Id));
                if (a.Init == InitKind.ExplicitForward) {
                    // The explicit implementation forwards to a protected virtual Init: "not public" names that one.
                    list.Add(("is not public", a.Id));
                }
            } else if (shape.StartsWith("not-impl-")) {
                list.Add(("does not implement IInitSystem.Init", a.Id));
            }
            return list;
        }

        /// <summary>
        /// Whether IInitSystem.Init of the base of <paramref name="d"/> maps to an explicit implementation (C# interface
        /// mapping: from the most derived class that lists the interface, or a generated one, upward, the first class with
        /// an explicit IInitSystem.Init or a public Init(World, SharedData) decides).
        /// </summary>
        public bool BaseInitIsExplicit(ClassModel d) {
            var chain = Chain(d);
            int listing = chain.FindIndex(a => a.IsGenerated || ListsInitIface(a));
            if (listing < 0) {
                return false;
            }
            for (int i = listing; i < chain.Count; i++) {
                var s = chain[i];
                if (IsExplicitKind(s.Init)) {
                    return true;
                }
                if (s.IsGenerated || IsPublicInstanceInit(s.Init)) {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 2.2.2: the nearest hand-written Init above <paramref name="d"/> when it is a `new Init` whose class does not list
        /// IInitSystem (or an interface that extends it) itself, so it does not implement IInitSystem.Init; null otherwise.
        /// </summary>
        private ClassModel UnrelatedNewInit(ClassModel d) {
            foreach (var a in Chain(d)) {
                if (a.IsGenerated) {
                    return null;
                }
                if (a.Init == InitKind.None) {
                    continue;
                }
                return a.Init is InitKind.PublicNewVirtual or InitKind.PublicNewNonVirtual or InitKind.PublicNewForward && !ListsInitIface(a) ? a : null;
            }
            return null;
        }

        public static bool IsPublicInstanceInit(InitKind k) => k is InitKind.PublicVirtual or InitKind.PublicAbstract or InitKind.PublicNonVirtual
            or InitKind.PublicNewVirtual or InitKind.PublicNewNonVirtual or InitKind.PublicNewForward or InitKind.PublicOverride
            or InitKind.PublicOverrideNoBase or InitKind.SealedOverride;

        /// <summary> Whether the Init of <paramref name="a"/> can call a virtual OnInit hook: the hook is declared in that class or one of its bases. </summary>
        private bool HookCallableBy(ClassModel a) => new[] { a }.Concat(_p.Ancestors(a)).Any(x => x.Hook == HookKind.Virtual);

        /// <summary>
        /// KECS013 (2.2.2) on a hand-written class: "expected" for an override of a generated Init without base.Init
        /// (directly or through hand-written overriding classes); "documented-miss" when that generated base is in
        /// another assembly and has no protected or public injected field ("recognised only by injected fields visible
        /// from the derived assembly"); "either" when only a base further up has visible fields; null otherwise.
        /// </summary>
        public string Kecs013Site(ClassModel c) {
            if (!Kecs013 || c.IsGenerated || c.Init != InitKind.PublicOverrideNoBase) {
                return null;
            }
            var cur = Renderer.NearestInit(_p, c).Owner;
            var seen = new HashSet<string>();
            while (cur != null && !cur.IsGenerated && IsOverrideKind(cur.Init) && seen.Add(cur.Id)) {
                cur = Renderer.NearestInit(_p, cur).Owner;
            }
            if (cur == null || !cur.IsGenerated || IsAliasOnly(cur) || For(cur).Emitted == false) {
                // No generated Init to override (the class gets none): nothing for KECS013 to say.
                return null;
            }
            if (cur.Asm == c.Asm || cur.Fields.Any(f => !f.Static && IsMetadataVisible(f.Access))) {
                return "expected";
            }
            return _p.Ancestors(cur).Any(x => x.IsGenerated && x.Fields.Any(f => !f.Static && IsMetadataVisible(f.Access))) ? "either" : "documented-miss";
        }

        /// <summary> The generated class whose Init a hand-written override chain ends at (see <see cref="Kecs013Site"/>). </summary>
        public ClassModel OverriddenGenerated(ClassModel c) {
            var cur = Renderer.NearestInit(_p, c).Owner;
            var seen = new HashSet<string>();
            while (cur != null && !cur.IsGenerated && IsOverrideKind(cur.Init) && seen.Add(cur.Id)) {
                cur = Renderer.NearestInit(_p, cur).Owner;
            }
            return cur != null && cur.IsGenerated ? cur : null;
        }

        private Expectation Inherit21(ClassModel d) {
            var chain = Chain(d);
            if (chain.Count == 0) {
                return Works("standalone", "standalone", Log211, "generated-init");
            }
            // 2.1.1: IsInitSystem(baseType) sees interfaces of source types and the generated
            // IInitSystem of referenced ones; InheritsInjectedFields sees every visible injected field.
            bool baseIsInitSystem = chain.Any(a => ListsInitIface(a) || IsExplicitKind(a.Init) || (a.IsGenerated && a.Asm != d.Asm));
            bool baseFields = VisibleFieldsAbove(d);
            if (baseIsInitSystem && !d.ListsInit) {
                return Reports("KECS006", "derives from an IInitSystem base without listing IInitSystem", "v21:interface-stays-with-base", Log211 + " (KECS006)", "kecs006-2.1");
            }
            if (baseFields && !d.OnInitCallsBase) {
                return Reports("KECS006", "base has injected fields, OnInit does not call base.Init", "v21:base-fields-skipped", Log211 + " (KECS006)", "kecs006-2.1");
            }
            bool fields = AnyFieldsAbove(d);
            return new Expectation {
                Kind = ExpKind.Works, Layout = d.OnInitCallsBase ? "2.1 layout, OnInit calls base.Init" : "2.1 layout without base.Init",
                Shape = "v21:" + (d.OnInitCallsBase ? "calls" : "nocall"),
                Doc = Log211, SkipAllowed = !d.OnInitCallsBase, SkipIsQuestion = !d.OnInitCallsBase && fields, Claim = "kecs006-2.1",
            };
        }
    }
}
