using System;
using System.Collections.Generic;
using System.Linq;

namespace KenseiStand.Generator {
    /// <summary>
    /// Greedy reduction of a failing program on the model: drop or splice classes, drop fields,
    /// interfaces and partial parts, flatten nesting and namespaces, clear flags, simplify field
    /// kinds and names. A step is kept when the reduced program still shows the same violation
    /// (rule and key). A reduced program that does not compile without the generator, or that has an
    /// error there its original did not have (Driver: the errors before the generator must stay a
    /// subset), is never kept: the repro stays valid C# whose only problem is the generated code.
    /// </summary>
    public static class Minimizer {
        public static ProgramModel Run(ProgramModel start, Func<ProgramModel, bool> stillFails, int budget, out int evaluations) {
            var current = start.Clone();
            evaluations = 0;
            bool progress = true;
            while (progress && evaluations < budget) {
                progress = false;
                foreach (var candidate in Candidates(current)) {
                    if (evaluations >= budget) {
                        break;
                    }
                    evaluations++;
                    bool fails;
                    try {
                        fails = stillFails(candidate);
                    } catch (Exception) {
                        fails = false;
                    }
                    if (fails) {
                        current = candidate;
                        progress = true;
                        break;
                    }
                }
            }
            return current;
        }

        private static bool Referenced(ProgramModel p, ClassModel c) =>
            p.Classes.Any(x => x.Id != c.Id && (x.BaseId == c.Id || x.NestedIn == c.Id));

        private static IEnumerable<ProgramModel> Candidates(ProgramModel p) {
            ProgramModel Edit(Action<ProgramModel> change) {
                var copy = p.Clone();
                change(copy);
                return copy;
            }

            // Whole assemblies and classes first: the biggest cuts.
            if (p.Classes.Any(c => c.Asm == Asm.Lib)) {
                yield return Edit(q => {
                    foreach (var c in q.Classes) {
                        c.Asm = Asm.Main;
                    }
                    foreach (var i in q.Interfaces) {
                        i.Asm = Asm.Main;
                    }
                    q.MainDefines = q.LibDefines.ToList();
                    q.LibDefines.Clear();
                });
            }
            foreach (var c in p.Classes.ToList()) {
                if (!Referenced(p, c)) {
                    yield return Edit(q => q.Classes.RemoveAll(x => x.Id == c.Id));
                }
            }
            foreach (var c in p.Classes.ToList()) {
                if (Referenced(p, c) && string.IsNullOrEmpty(c.TypeParams) && !p.Classes.Any(x => x.NestedIn == c.Id || (x.BaseId == c.Id && (x.BaseArgs != null || x.BaseRef != null)))) {
                    yield return Edit(q => {
                        foreach (var x in q.Classes.Where(x => x.BaseId == c.Id)) {
                            x.BaseId = c.BaseId;
                            x.BaseArgs = c.BaseArgs;
                        }
                        q.Classes.RemoveAll(x => x.Id == c.Id);
                    });
                }
            }
            foreach (var i in p.Interfaces.ToList()) {
                if (!p.Classes.Any(c => c.IfaceId == i.Id)) {
                    yield return Edit(q => q.Interfaces.RemoveAll(x => x.Id == i.Id));
                }
            }
            if (p.LibDefines.Count > 0) {
                yield return Edit(q => q.LibDefines.Clear());
            }
            foreach (var c in p.Classes.Where(c => c.Define != null).ToList()) {
                yield return Edit(q => q.Cls(c.Id).Define = null);
            }

            // Members of each class.
            foreach (var c in p.Classes.ToList()) {
                for (int i = 0; i < c.Fields.Count; i++) {
                    int index = i;
                    yield return Edit(q => q.Cls(c.Id).Fields.RemoveAt(index));
                }
            }
            foreach (var c in p.Classes.ToList()) {
                if (c.Parts > 1) {
                    yield return Edit(q => {
                        var x = q.Cls(c.Id);
                        x.Parts = 1;
                        x.Fields.ForEach(f => f.Part = 0);
                    });
                }
                if (c.Outers.Count > 0) {
                    yield return Edit(q => q.Cls(c.Id).Outers.Clear());
                }
                if (c.Ns != "") {
                    yield return Edit(q => q.Cls(c.Id).Ns = "");
                }
                if (c.OnInitCallsBase) {
                    yield return Edit(q => q.Cls(c.Id).OnInitCallsBase = false);
                }
                if (c.OnInit && !c.OnInitCallsBase) {
                    yield return Edit(q => q.Cls(c.Id).OnInit = false);
                }
                if (c.ListsRun) {
                    yield return Edit(q => q.Cls(c.Id).ListsRun = false);
                }
                if (c.ListsInit && !Contract.IsExplicitKind(c.Init)) {
                    yield return Edit(q => q.Cls(c.Id).ListsInit = false);
                }
                if (c.Hook != HookKind.None) {
                    yield return Edit(q => q.Cls(c.Id).Hook = HookKind.None);
                }
                if (c.Abstract) {
                    yield return Edit(q => q.Cls(c.Id).Abstract = false);
                }
                if (c.Init != InitKind.None && c.IsGenerated == false && !Referenced(p, c)) {
                    yield return Edit(q => {
                        var x = q.Cls(c.Id);
                        x.Init = InitKind.None;
                        x.ListsInit = false;
                    });
                }
            }

            // Simpler fields and names.
            foreach (var c in p.Classes.ToList()) {
                for (int i = 0; i < c.Fields.Count; i++) {
                    int index = i;
                    var f = c.Fields[i];
                    bool simple = f.Kind == FieldKind.Pool && f.Type == Mk.C0 && f.Spelling == "short" && f.Access == "private" && !f.Nullable && !f.AttrList && f.Foreign == null;
                    if (!simple && f.WrongType == null && !f.Readonly) {
                        yield return Edit(q => {
                            var x = q.Cls(c.Id).Fields[index];
                            x.Kind = FieldKind.Pool;
                            x.Type = Mk.C0;
                            x.Inc.Clear();
                            x.Exc.Clear();
                            x.Any.Clear();
                            x.Key = null;
                            x.Spelling = "short";
                            x.Access = "private";
                            x.Nullable = false;
                            x.AttrList = false;
                            x.Foreign = null;
                        });
                    }
                    if (f.Foreign != null) {
                        yield return Edit(q => q.Cls(c.Id).Fields[index].Foreign = null);
                    }
                    if (f.AttrList) {
                        yield return Edit(q => q.Cls(c.Id).Fields[index].AttrList = false);
                    }
                    if (f.Access != "private" && f.Access != "protected") {
                        yield return Edit(q => q.Cls(c.Id).Fields[index].Access = "protected");
                    }
                    string plainName = "_f" + i;
                    if (f.Name != plainName && !c.Fields.Any(x => x.Name == plainName)) {
                        yield return Edit(q => q.Cls(c.Id).Fields[index].Name = plainName);
                    }
                }
                string name = c.IsGenerated ? "Sys" + c.Id.TrimStart('K') : "Base" + c.Id.TrimStart('K');
                if (c.Name != name && !p.Classes.Any(x => x.Name == name)) {
                    yield return Edit(q => q.Cls(c.Id).Name = name);
                }
            }
            if (p.NullableEnable) {
                yield return Edit(q => q.NullableEnable = false);
            }
        }
    }
}
