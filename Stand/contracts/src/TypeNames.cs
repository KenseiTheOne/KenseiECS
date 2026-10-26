using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Stand.Contracts {
    /// <summary> C# spelling of types and members (for repros and the matrix) and XML documentation ids. </summary>
    public static class TypeNames {
        public static string CSharp(Type t) {
            if (t.IsByRef) {
                return CSharp(t.GetElementType());
            }
            if (t.IsArray) {
                return CSharp(t.GetElementType()) + "[]";
            }
            if (t.IsGenericParameter) {
                return t.Name;
            }
            switch (Type.GetTypeCode(t)) {
                case TypeCode.Int32: return "int";
                case TypeCode.Int64: return "long";
                case TypeCode.Boolean: return "bool";
                case TypeCode.String: return "string";
                case TypeCode.Double: return "double";
                case TypeCode.Single: return "float";
                case TypeCode.Byte: return "byte";
                case TypeCode.UInt32: return "uint";
            }
            if (t == typeof(object)) {
                return "object";
            }
            if (t == typeof(void)) {
                return "void";
            }
            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) {
                name = name.Substring(0, tick);
            }
            if (t.IsNested && !t.IsGenericParameter) {
                // World.AliveEntityEnumerable, SystemsRunner.SystemInfo
                var outer = t.DeclaringType;
                string outerName = outer.IsGenericType ? CSharp(outer) : outer.Name;
                name = outerName + "." + name;
            }
            if (!t.IsGenericType) {
                return name;
            }
            var args = t.GetGenericArguments();
            // A nested type of a generic type repeats the outer arguments.
            if (t.IsNested && t.DeclaringType.IsGenericType) {
                int outerCount = t.DeclaringType.GetGenericArguments().Length;
                args = args.Skip(outerCount).ToArray();
                if (args.Length == 0) {
                    return name;
                }
            }
            return name + "<" + string.Join(", ", args.Select(CSharp)) + ">";
        }

        /// <summary> Short display form: World.Add&lt;CA&gt;(Entity, CA), Filter.Count {get}, new World(WorldConfig). </summary>
        public static string Display(MethodBase m, Type targetType) {
            var sb = new StringBuilder();
            string owner = CSharp(targetType ?? m.DeclaringType);
            if (m is ConstructorInfo) {
                sb.Append("new ").Append(owner);
            } else {
                var mi = (MethodInfo)m;
                if (mi.IsSpecialName && mi.Name.StartsWith("get_", StringComparison.Ordinal)) {
                    return owner + "." + mi.Name.Substring(4) + " {get}";
                }
                if (IsExtension(mi)) {
                    sb.Append(CSharp(mi.GetParameters()[0].ParameterType)).Append('.').Append(mi.Name);
                    AppendGeneric(sb, mi);
                    sb.Append('(').Append(string.Join(", ", mi.GetParameters().Skip(1).Select(ParamDisplay))).Append(")*");
                    return sb.ToString();
                }
                sb.Append(owner).Append('.').Append(mi.Name);
                AppendGeneric(sb, mi);
            }
            sb.Append('(').Append(string.Join(", ", m.GetParameters().Select(ParamDisplay))).Append(')');
            if (m.IsStatic && !(m is ConstructorInfo)) {
                sb.Append(" static");
            }
            return sb.ToString();
        }

        private static void AppendGeneric(StringBuilder sb, MethodInfo mi) {
            if (mi.IsGenericMethod) {
                sb.Append('<').Append(string.Join(", ", mi.GetGenericArguments().Select(CSharp))).Append('>');
            }
        }

        private static string ParamDisplay(ParameterInfo p) {
            string prefix = "";
            if (p.ParameterType.IsByRef) {
                prefix = p.IsOut ? "out " : (p.IsIn ? "in " : "ref ");
            }
            return prefix + CSharp(p.ParameterType);
        }

        public static bool IsExtension(MethodInfo mi) {
            return mi.IsStatic && mi.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false);
        }

        // =====================================================================
        // XML documentation ids (ECMA-334 annex D), from the open generic definition
        // =====================================================================

        public static string DocId(MethodBase m) {
            var open = OpenDefinition(m);
            var sb = new StringBuilder("M:");
            sb.Append(TypeId(open.DeclaringType)).Append('.');
            if (open is ConstructorInfo) {
                sb.Append("#ctor");
            } else {
                var mi = (MethodInfo)open;
                if (mi.IsSpecialName && mi.Name.StartsWith("get_", StringComparison.Ordinal)) {
                    return "P:" + TypeId(open.DeclaringType) + "." + mi.Name.Substring(4);
                }
                sb.Append(mi.Name);
                if (mi.IsGenericMethodDefinition) {
                    sb.Append("``").Append(mi.GetGenericArguments().Length);
                }
            }
            var ps = open.GetParameters();
            if (ps.Length > 0) {
                sb.Append('(').Append(string.Join(",", ps.Select(p => ParamId(p.ParameterType)))).Append(')');
            }
            return sb.ToString();
        }

        public static MethodBase OpenDefinition(MethodBase m) {
            var declaring = m.DeclaringType;
            MethodBase open = m;
            if (declaring != null && declaring.IsGenericType && !declaring.IsGenericTypeDefinition) {
                var def = declaring.GetGenericTypeDefinition();
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                open = (MethodBase)def.GetMethods(all).Cast<MethodBase>().Concat(def.GetConstructors(all))
                    .FirstOrDefault(x => x.MetadataToken == m.MetadataToken && x.Module == m.Module) ?? m;
            }
            if (open is MethodInfo mi && mi.IsGenericMethod && !mi.IsGenericMethodDefinition) {
                open = mi.GetGenericMethodDefinition();
            }
            return open;
        }

        private static string TypeId(Type t) {
            if (t.IsGenericType && !t.IsGenericTypeDefinition) {
                t = t.GetGenericTypeDefinition();
            }
            return (t.FullName ?? t.Name).Replace('+', '.');
        }

        private static string ParamId(Type t) {
            if (t.IsByRef) {
                return ParamId(t.GetElementType()) + "@";
            }
            if (t.IsArray) {
                return ParamId(t.GetElementType()) + "[]";
            }
            if (t.IsGenericParameter) {
                return (t.DeclaringMethod != null ? "``" : "`") + t.GenericParameterPosition;
            }
            if (t.IsGenericType) {
                var def = t.GetGenericTypeDefinition();
                string name = (def.FullName ?? def.Name).Replace('+', '.');
                int tick = name.IndexOf('`');
                if (tick >= 0) {
                    name = name.Substring(0, tick);
                }
                return name + "{" + string.Join(",", t.GetGenericArguments().Select(ParamId)) + "}";
            }
            return (t.FullName ?? t.Name).Replace('+', '.');
        }
    }
}
