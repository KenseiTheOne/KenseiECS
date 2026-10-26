using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KenseiStand.Generator {
    /// <summary> Which compilation a class belongs to: Lib is compiled first (its own generator run) and referenced by Main. </summary>
    public enum Asm { Main, Lib }

    /// <summary> The hand-written Init member of a class (none for a generated class: the generator writes its Init). </summary>
    public enum InitKind {
        None,
        PublicVirtual,          // public virtual void Init(World, SharedData)
        PublicAbstract,         // public abstract void Init(World, SharedData)
        PublicNonVirtual,       // public void Init(World, SharedData)
        PublicNewVirtual,       // public new virtual void Init(World, SharedData)
        PublicNewNonVirtual,    // public new void Init(World, SharedData)
        PublicNewForward,       // public new void Init(World, SharedData) { base.Init(world, shared); }
        PublicOverride,         // public override void Init(...) { base.Init(...); }
        PublicOverrideNoBase,   // public override void Init(...) { }   (does not call base.Init)
        SealedOverride,         // public sealed override void Init(...) { base.Init(...); }
        Explicit,               // void IInitSystem.Init(World, SharedData)
        VirtualThenExplicit,    // public virtual Init, then an explicit IInitSystem.Init (declared in this order)
        ExplicitThenVirtual,    // the same two members in the other order
        ExplicitForward,        // protected virtual void Init(World, SharedData), and void IInitSystem.Init forwarding to it
        OverloadObject,         // public void Init(World, object)
        OverloadParams,         // public void Init(World, params SharedData[])
        OverloadOptional,       // public void Init(World, SharedData, int extra = 0)
        GenericHelper,          // protected void Init<TX>(World, SharedData)
        InParam,                // public void Init(in World, SharedData)
        StaticInit,             // public static void Init(World, SharedData)
        FieldNamedInit,         // public bool Init;
        PropertyNamedInit,      // public bool Init { get; set; }
    }

    /// <summary> A hand-written OnInit(World, SharedData) hook (same name and signature as the generated partial OnInit). </summary>
    public enum HookKind {
        None,
        Virtual,       // protected virtual void OnInit(World, SharedData), called from the class's own Init
        NonVirtual,    // protected void OnInit(World, SharedData), called from the class's own Init
        Override,      // protected override void OnInit(World, SharedData) { base.OnInit(...); }
    }

    public enum OuterKind { Class, Struct, StaticClass, Record, Interface }

    public enum FieldKind { Filter, Pool, Group, Shared }

    public sealed class FieldModel {
        public string Name;
        public FieldKind Kind;
        public string Access = "private";
        public List<string> Inc = new();
        public List<string> Exc = new();
        public List<string> Any = new();
        /// <summary> Pool: the component type; Shared: the shared type; Group: comma-separated component types. </summary>
        public string Type;
        /// <summary> Shared key; null renders [Shared]. </summary>
        public string Key;
        /// <summary> short [Pool], long [PoolAttribute], qualified [KenseiECS.Pool], global [global::KenseiECS.Pool], alias [InjPool]. </summary>
        public string Spelling = "short";
        public bool Readonly;
        public bool Static;
        public bool Nullable;
        /// <summary> Negative layouts: render this field type instead of the matching one (KECS003). </summary>
        public string WrongType;
        /// <summary> Which partial declaration of the class holds the field. </summary>
        public int Part;
        /// <summary> A filter field with several attributes writes them in one attribute list ([Inc(...), Exc(...)]). </summary>
        public bool AttrList;
        /// <summary>
        /// A foreign attribute ([System.NonSerialized]) in front of the injection attribute: "before" as an attribute
        /// list of its own ([System.NonSerialized] [Pool]), "list" in the same list ([System.NonSerialized, Pool]).
        /// </summary>
        public string Foreign;
    }

    /// <summary>
    /// [Shared] field types beyond the plain classes Cfg0-Cfg3 ([Shared] takes any reference type): an interface and
    /// an abstract class, registered with an implementation, a closed generic class and a nested class.
    /// </summary>
    public static class SharedKinds {
        public const string Iface = "global::StandShared.ICfg";
        public const string Abstract = "global::StandShared.CfgBase";
        public const string Generic = "global::StandShared.Box<int>";
        public const string Nested = "global::StandShared.SharedHolder.Inner";
        public static readonly string[] All = { Iface, Abstract, Generic, Nested };

        public static bool IsSpecial(string type) => Array.IndexOf(All, (type ?? "").TrimEnd('?')) >= 0;

        /// <summary> The registration of the clean repro form: the instance, and the type argument of shared.Add when it is not the instance's type. </summary>
        public static (string Value, string TypeArg) Registration(string type) => (type ?? "").TrimEnd('?') switch {
            Iface => ("new CfgImpl()", "ICfg"),
            Abstract => ("new CfgDerived()", "CfgBase"),
            Generic => ("new Box<int>()", null),
            Nested => ("new SharedHolder.Inner()", null),
            _ => (null, null),
        };

        /// <summary> Declarations of the clean form by the short name Renderer.CleanTypes collects for these types. </summary>
        public static readonly Dictionary<string, string> CleanDeclarations = new() {
            ["ICfg"] = "public interface ICfg { }\n    public sealed class CfgImpl : ICfg { }",
            ["CfgBase"] = "public abstract class CfgBase { }\n    public sealed class CfgDerived : CfgBase { }",
            ["Box"] = "public class Box<T> { }",
            ["SharedHolder.Inner"] = "public static class SharedHolder {\n        public class Inner { }\n    }",
        };
    }

    public sealed class OuterModel {
        public string Name;
        public OuterKind Kind;
        public bool Partial = true;
        /// <summary> Type parameter list without brackets ("TTag"), or null. </summary>
        public string TypeParams;
        /// <summary> Extra modifiers before 'partial' (readonly, ref, abstract, sealed, unsafe). </summary>
        public string Modifiers;
    }

    public sealed class ClassModel {
        /// <summary> Stable id in the program ("K1"); the probe tags and ClsId attributes use it. </summary>
        public string Id;
        /// <summary> Identifier as written in C#, '@' included for keywords. </summary>
        public string Name;
        /// <summary> Namespace as written ("" for the global namespace). </summary>
        public string Ns = "";
        public List<OuterModel> Outers = new();
        /// <summary> Id of a model class this one is nested in (rendered inside its body); null otherwise. </summary>
        public string NestedIn;
        public Asm Asm;
        /// <summary> Preprocessor symbol the class is wrapped in (#if), null for always. </summary>
        public string Define;
        public string BaseId;
        /// <summary> Type arguments for a generic base ("global::StandShared.C0"). </summary>
        public string BaseArgs;
        /// <summary> Raw base type expression, used instead of BaseId's name (for bases that need outer type arguments). BaseId is still set. </summary>
        public string BaseRef;
        /// <summary> Type parameter of a generic class ("T", constrained to struct, IComponent), or null. </summary>
        public string TypeParams;
        /// <summary> class, struct or record (the last two only for the KECS010 layouts). </summary>
        public string Kind = "class";
        public bool Partial = true;
        public bool Abstract;
        public bool Sealed;
        public bool ListsInit;
        public bool ListsRun;
        /// <summary> Id of a model interface listed in the base list. </summary>
        public string IfaceId;
        public List<FieldModel> Fields = new();
        public InitKind Init;
        /// <summary> Negative layout: a hand-written Init(World, SharedData) next to injected fields (KECS002). </summary>
        public bool InitNextToFields;
        /// <summary> Negative layout: an explicit IInitSystem.Init next to injected fields (KECS002). </summary>
        public bool ExplicitInitNextToFields;
        public bool OnInit;
        public bool OnInitCallsBase;
        public HookKind Hook;
        public int Parts = 1;
        /// <summary> Free-form note shown in the layout table. </summary>
        public string Note;

        [JsonIgnore] public bool IsGenerated => Fields.Any(f => !f.Static);
        [JsonIgnore] public bool IsGeneric => !string.IsNullOrEmpty(TypeParams) || Outers.Any(o => !string.IsNullOrEmpty(o.TypeParams));
        [JsonIgnore] public string Plain => Name.TrimStart('@');
    }

    public sealed class InterfaceModel {
        public string Id;
        public string Name;
        public string Ns = "";
        public Asm Asm;
        public bool IncludesInit = true;
        public bool IncludesRun = true;
    }

    /// <summary>
    /// A stand program: the classes of up to two compilations (Lib, referenced by Main). Everything
    /// the oracle needs is derived from this model, never from the generator under test.
    /// </summary>
    public sealed class ProgramModel {
        public string Title = "";
        public List<ClassModel> Classes = new();
        public List<InterfaceModel> Interfaces = new();
        public List<string> LibDefines = new();
        public List<string> MainDefines = new();
        /// <summary> #nullable enable in every file (fields get '?' when FieldModel.Nullable). </summary>
        public bool NullableEnable;

        public ClassModel Cls(string id) => id == null ? null : Classes.FirstOrDefault(c => c.Id == id);

        public InterfaceModel Iface(string id) => id == null ? null : Interfaces.FirstOrDefault(i => i.Id == id);

        public ClassModel BaseOf(ClassModel c) => Cls(c.BaseId);

        public IEnumerable<ClassModel> Ancestors(ClassModel c) {
            var seen = new HashSet<string>();
            for (var a = BaseOf(c); a != null && seen.Add(a.Id); a = BaseOf(a)) {
                yield return a;
            }
        }

        public IEnumerable<ClassModel> Descendants(ClassModel c) => Classes.Where(x => Ancestors(x).Any(a => a.Id == c.Id));

        public bool IsActive(ClassModel c) {
            if (c.Define == null) {
                return true;
            }
            return (c.Asm == Asm.Lib ? LibDefines : MainDefines).Contains(c.Define);
        }

        public IEnumerable<ClassModel> ActiveClasses(Asm asm) => Classes.Where(c => c.Asm == asm && IsActive(c));

        public bool HasLib => Classes.Any(c => c.Asm == Asm.Lib && IsActive(c));

        public string NextId(string prefix) {
            int n = 1;
            while (Classes.Any(c => c.Id == prefix + n) || Interfaces.Any(i => i.Id == prefix + n)) {
                n++;
            }
            return prefix + n;
        }

        public ProgramModel Clone() => Json.Clone(this);
    }

    public static class Json {
        public static readonly JsonSerializerOptions Options = new() {
            IncludeFields = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary> Reports keep null members (a finding without a known issue says "known": null). </summary>
        public static readonly JsonSerializerOptions ReportOptions = new() {
            IncludeFields = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string WriteReport<T>(T value) => JsonSerializer.Serialize(value, ReportOptions);

        public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options);

        public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

        private static readonly JsonSerializerOptions CompactOptions = new() {
            IncludeFields = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string WriteCompact<T>(T value) => JsonSerializer.Serialize(value, CompactOptions);

        public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
    }
}
