using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    /// <summary>
    /// Issues known from the 2.2.0 review (GN1-GN8) and found by the stand (NF1-NF5), recognised by
    /// the mechanism and the place of a violation: rule and key (keys carry the mechanism, see
    /// <see cref="Oracle.CrashMechanism"/>) and, where one diagnostic code stands for several mechanisms,
    /// the layout shape of the class (<see cref="Violation.Place"/>), so that the same code in another
    /// layout is a new finding and not a hit of the known issue (a CS0108 on Init over a non-system base
    /// with an ordinary Init is not GN6, a CS0114 on Init outside the explicit layouts is not GN2).
    /// Labels only: the oracle decides what is a defect, a question or a regression, this table says
    /// which known issue a violation belongs to. A known issue is "found" on a tree only through
    /// defects; a label on a question, a regression or a documented item gives it that status instead.
    /// The witnesses are catalog layouts that show the issue; their outcome on a tree explains a "not found".
    /// </summary>
    public sealed class KnownIssue {
        public string Id;
        public string Title;
        public string Severity;
        public string[] Witnesses = Array.Empty<string>();
        public Func<Violation, bool> Match;

        public static readonly List<KnownIssue> All = new() {
            new KnownIssue {
                Id = "GN1", Severity = "high",
                Title = "2.1 layout (the derived class lists IInitSystem itself) without base.Init over a base with injected fields: the KECS006 of 2.1.1 is gone, the base fields silently stay null",
                Witnesses = new[] { "I-gen/lib/list/nocall", "I-gen-new-mid/same/list/nocall", "I-gen-newforward-mid/same/list/nocall", "F-editor-player/player/list" },
                // Defects where a promise of the tree asks for KECS006 (an overload in between on 2.2.0/2.2.1, the 2.2.2
                // rule for the 2.1 layout), and the loss of the 2.1.1 KECS006 against the 2.1 line (a regression).
                Match = v => (!v.Question && v.Rule is "R-FIELDS-NULL" or "R-BASE-INIT-SKIPPED" && v.Key.Contains("layout21:") && v.Key.Contains(":nocall:visible-fields"))
                    || (v.Rule == "R-LOST-SIGNAL" && (v.Place ?? "").StartsWith("layout21:") && (v.Place ?? "").Contains(":nocall")),
            },
            new KnownIssue {
                Id = "GN2", Severity = "high",
                Title = "The base Init is picked by name and member order, not by what implements IInitSystem.Init: silent non-injection (and a false KECS012 / CS0114 where the 2.1 layout is documented)",
                Witnesses = new[] { "I-hand-virtual-then-explicit/same/nolist/nocall", "I-hand-virtual-then-explicit/same/list/call", "I-hand-hidden-virtual/lib/nolist/nocall", "I-hand-explicit-then-virtual/same/list/nocall" },
                Match = v => (v.Rule == "R-FIELDS-NULL" && !v.Question && v.Key.Contains("no-dispatch") && (v.Key.Contains("explicit-") || v.Key.Contains("not-impl-")))
                    || (v.Rule == "R-OTHER-KECS" && !v.Question && v.Key.StartsWith("OTHER|KECS012|KECS006|") && (v.Key.Contains("explicit-") || v.Key.Contains("not-impl-")))
                    || (v.Rule == "R-FALSE-KECS" && v.Key.StartsWith("FALSE|KECS012|layout21:") && (v.Key.Contains("explicit-") || v.Key.Contains("not-impl-")))
                    || (v.Rule == "R-GEN-WARNING" && v.Key == "GENWARN|CS0114|Init" && ExplicitPlace(v.Place)),
            },
            new KnownIssue {
                Id = "GN3", Severity = "medium",
                Title = "Composite interface IGameSystem : IInitSystem: a false KECS006 (and when the derived class lists IInitSystem, the base Init is skipped or CS0534 remains)",
                Witnesses = new[] { "I-hand-composite-virtual/same/nolist/nocall", "I-hand-composite-virtual/same/list/nocall", "I-hand-composite-abstract/same/list/nocall" },
                Match = v => (!v.Question && v.Key.Contains("-via-composite") && v.Rule is "R-FALSE-KECS" or "R-BASE-INIT-SKIPPED" or "R-GEN-MISSING" or "R-FIELDS-NULL" or "R-OTHER-KECS")
                    || (v.Rule == "R-KECS-TEXT" && (v.Place ?? "").Contains("-via-composite")),
            },
            new KnownIssue {
                Id = "GN4", Severity = "medium",
                Title = "The generated partial OnInit clashes with a hand-written OnInit(World, SharedData) hook of the base: CS0111 when the derived class overrides the hook, CS0114/CS0108 in the generated code otherwise (questions up to 2.2.1, whose documents do not cover a hook of the base; defects from 2.2.2, which promises that the override of the hook runs once)",
                Witnesses = new[] { "I-hand-hook-virtual/same/nolist/nocall", "I-hand-hook-nonvirtual/same/nolist/nocall", "F-hook-override/same", "I-hand-template/same/list/call" },
                Match = v => v.Rule is "R-GEN-WARNING" or "R-GEN-ERROR" && v.Key.Contains("|OnInit|hook"),
            },
            new KnownIssue {
                Id = "GN5", Severity = "high",
                Title = "Hint names are compared ignoring case: with Game.AI and Game.Ai the generator throws, every system of the assembly stays without Init, only the CS8785 warning is left",
                Witnesses = new[] { "F-case-namespaces", "F-case-namespaces-silent", "F-case-classes" },
                Match = v => v.Rule is "R-CRASH" or "R-FIELDS-NULL" && v.Key.EndsWith("|case-collision"),
            },
            new KnownIssue {
                Id = "GN6", Severity = "low",
                Title = "Keyword identifiers are not escaped (@event becomes this.event, the '@' in a hint name makes the generator throw); CS0108 where the generated Init hides a member named Init and the CS0505 error remain in the generated code (CS0109 there is a question)",
                Witnesses = new[] { "F-kw-field", "F-kw-class", "I-nonsystem-field-init/same/nolist/nocall", "I-nonsystem-inparam/same/nolist/nocall", "I-gen-field-init-mid/same/nolist/nocall" },
                Match = v => (v.Rule == "R-GEN-ERROR" && (v.Key == "GENERR|keyword-ident" || v.Key.Contains("|CS0505|")))
                    || (v.Rule is "R-CRASH" or "R-FIELDS-NULL" && v.Key.EndsWith("|invalid-hint-char"))
                    || (v.Rule == "R-GEN-WARNING" && v.Key == "GENWARN|CS0108|Init" && MemberPlace(v.Place, "FieldNamedInit", "PropertyNamedInit", "StaticInit"))
                    || (v.Rule == "R-GEN-WARNING" && v.Key == "GENWARN|CS0109|Init" && MemberPlace(v.Place, "InParam", "GenericHelper")),
            },
            new KnownIssue {
                Id = "GN7", Severity = "medium",
                Title = "Windows review G-2: after the documented upgrade from 2.1 (delete base.Init from OnInit) the base Init/OnInit runs before the derived fields are filled and sees them null; in the 2.1 layout they were set. A question on 2.2.0/2.2.1, whose guide says both 'the generated Init makes the call before the derived fields' and 'The order stays the same'; a defect from 2.2.2, which promises that the base still sees the derived fields",
                Witnesses = new[] { "I-gen/same/list/call", "I-hand-virtual/same/list/call", "I-hand-hook-virtual/same/list/call" },
                Match = v => v.Rule == "R-MIGRATION-ORDER",
            },
            new KnownIssue {
                Id = "GN8", Severity = "medium",
                Title = "Windows review G-3: a hand-written override of a generated Init without base.Init compiles silently (2.1.1 failed with CS0506) and the base fields stay null: a lost compiler signal (a regression on 2.2.0/2.2.1; 2.2.2 gives the warning KECS013, whose absence is a defect and, over a base in another assembly with only private injected fields, documented)",
                Witnesses = new[] { "F-hand-leaf-override-nobase", "F-hand-mid-override-nobase", "F-hand-leaf-override-nobase-lib-protected", "F-hand-leaf-override-nobase-lib-private" },
                // 2.2.2 brings the signal back as the warning KECS013: its absence where documented is GN8 again.
                Match = v => (v.Rule == "R-LOST-SIGNAL" && v.Key == "LOSTSIGNAL|drop-PublicOverrideNoBase") || v.Rule == "R-MISSING-WARNING",
            },
        };

        /// <summary> Defects the stand found beyond the 2.2.0 review (stable labels for later trees). </summary>
        public static readonly List<KnownIssue> New = new() {
            new KnownIssue {
                Id = "NF1", Severity = "medium",
                Title = "Exact hint-name collision (Sys<T> and a class Sys_T_ both give Sys_T_.Injection.g.cs): the generator throws, CS8785, no system of the assembly gets its Init",
                Witnesses = new[] { "F-hint-generic" },
                Match = v => v.Rule is "R-CRASH" or "R-FIELDS-NULL" && v.Key.EndsWith("|exact-collision"),
            },
            new KnownIssue {
                Id = "NF2", Severity = "medium",
                Title = "A [Shared(\"...\")] key with a line break (\\n, \\r, U+2028) goes into the generated string literal as is: CS1010 in the generated code",
                Witnesses = new[] { "F-key-newline", "F-key-cr", "F-key-u2028" },
                Match = v => v.Rule == "R-GEN-ERROR" && v.Key.StartsWith("GENERR|CS1010|"),
            },
            new KnownIssue {
                Id = "NF3", Severity = "high",
                Title = "An Init(World, object / params / in ...) overload above a chained override takes the generated base.Init call (C# binds to the most derived declaration, an override is none): the base Init silently does not run, no KECS006",
                Witnesses = new[] { "F-capture-above-hand-override", "F-capture-above-generated-override" },
                Match = v => !v.Question && v.Key.Contains("capture-above-"),
            },
            new KnownIssue {
                Id = "NF4", Severity = "medium",
                Title = "2.1 layout over an abstract Init where an Init<T> / Init(in ...) overload in between is taken for the base Init: the generated new Init does not implement the abstract member, CS0534 without KECS",
                Witnesses = new[] { "F-abstract-helper-list" },
                Match = v => !v.Question && v.Rule == "R-GEN-MISSING" && v.Key.StartsWith("MISSING|CS0534|") && v.Key.Contains("abstract-capture"),
            },
            new KnownIssue {
                Id = "NF5", Severity = "low",
                Title = "A derived class nested in its generated base: its partial OnInit hides the private OnInit of the base it can see, CS0108 in the generated code (a question: no document covers a hidden OnInit)",
                Witnesses = new[] { "F-nested-in-base" },
                Match = v => v.Rule == "R-GEN-WARNING" && v.Key.EndsWith("|OnInit|nested-in-base"),
            },
        };

        // The layouts where GN2's CS0114 comes from: the generator took an explicit IInitSystem.Init (declared first) or a
        // public Init that does not implement the interface for the base Init.
        private static bool ExplicitPlace(string place) => (place ?? "").Contains("explicit-") || (place ?? "").Contains("not-impl-");

        // GN6's places: a base member named Init that is not a method (CS0108), or an Init(in ...) / Init<T> overload the
        // generator took for an Init it hides (CS0109).
        private static bool MemberPlace(string place, params string[] kinds) =>
            kinds.Any(k => (place ?? "").StartsWith("nonsystem:" + k) || (place ?? "").Contains("init-member:" + k));

        public static string Of(Violation v) {
            var match = All.FirstOrDefault(k => k.Match(v)) ?? New.FirstOrDefault(k => k.Match(v));
            return match?.Id;
        }
    }
}
