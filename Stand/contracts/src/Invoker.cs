using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Stand.Contracts {
    /// <summary> Compiled call of one member: target (boxed for a struct), arguments, boxed result. </summary>
    public delegate object Thunk(object target, object[] args);

    /// <summary>
    /// Calls any public method, property getter or constructor through a DynamicMethod instead of
    /// MethodInfo.Invoke. Reflection cannot box a ref return (World.Get, ComponentPool.Add) or a
    /// by-ref-like return (Filter.Entities, Filter.GetEnumerator), and it wraps every exception in
    /// TargetInvocationException. The thunk lets the original exception through with its own stack.
    /// A by-ref-like result is consumed in place: its Length is returned, or an enumerator is drained
    /// (MoveNext, Current, Dispose) and the number of steps returned.
    /// </summary>
    public static class Invoker {
        public const int DrainLimit = 4096;

        private static readonly Dictionary<MethodBase, Thunk> Cache = new();

        /// <summary> Null when the member cannot be called from boxed arguments (a by-ref-like parameter). </summary>
        public static Thunk For(MethodBase method, out string whyNot) {
            whyNot = null;
            if (Cache.TryGetValue(method, out var cached)) {
                return cached;
            }
            foreach (var p in method.GetParameters()) {
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (t.IsByRefLike) {
                    whyNot = "by-ref-like parameter " + p.Name;
                    return null;
                }
                if (t.IsPointer) {
                    whyNot = "pointer parameter " + p.Name;
                    return null;
                }
            }
            if (method is MethodInfo mi && mi.ReturnType.IsByRef && mi.ReturnType.GetElementType().IsByRefLike) {
                whyNot = "ref return of a by-ref-like type";
                return null;
            }
            var thunk = Build(method);
            Cache[method] = thunk;
            return thunk;
        }

        private static Thunk Build(MethodBase m) {
            var dm = new DynamicMethod("call_" + m.Name, typeof(object), new[] { typeof(object), typeof(object[]) },
                typeof(Invoker).Module, true);
            var il = dm.GetILGenerator();
            var ps = m.GetParameters();
            var locals = new LocalBuilder[ps.Length];

            // By-ref parameters go through locals, initialised from args[i] when it is not null
            // (an out parameter is passed null) and written back after the call.
            for (int i = 0; i < ps.Length; i++) {
                var pt = ps[i].ParameterType;
                if (!pt.IsByRef) {
                    continue;
                }
                var et = pt.GetElementType();
                locals[i] = il.DeclareLocal(et);
                var skip = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_1);
                EmitInt(il, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Brfalse, skip);
                il.Emit(OpCodes.Ldarg_1);
                EmitInt(il, i);
                il.Emit(OpCodes.Ldelem_Ref);
                EmitFromObject(il, et);
                il.Emit(OpCodes.Stloc, locals[i]);
                il.MarkLabel(skip);
            }

            bool isCtor = m is ConstructorInfo;
            var declaring = m.DeclaringType;
            if (!m.IsStatic && !isCtor) {
                il.Emit(OpCodes.Ldarg_0);
                if (declaring.IsValueType) {
                    // A pointer into the box: the call mutates the boxed instance the caller keeps.
                    il.Emit(OpCodes.Unbox, declaring);
                } else {
                    il.Emit(OpCodes.Castclass, declaring);
                }
            }

            for (int i = 0; i < ps.Length; i++) {
                if (locals[i] != null) {
                    il.Emit(OpCodes.Ldloca, locals[i]);
                    continue;
                }
                il.Emit(OpCodes.Ldarg_1);
                EmitInt(il, i);
                il.Emit(OpCodes.Ldelem_Ref);
                EmitFromObject(il, ps[i].ParameterType);
            }

            Type ret;
            if (isCtor) {
                il.Emit(OpCodes.Newobj, (ConstructorInfo)m);
                ret = declaring;
            } else {
                var mi = (MethodInfo)m;
                il.Emit(m.IsStatic || declaring.IsValueType ? OpCodes.Call : OpCodes.Callvirt, mi);
                ret = mi.ReturnType;
            }

            EmitBoxResult(il, ret);
            var result = il.DeclareLocal(typeof(object));
            il.Emit(OpCodes.Stloc, result);

            for (int i = 0; i < ps.Length; i++) {
                if (locals[i] == null) {
                    continue;
                }
                il.Emit(OpCodes.Ldarg_1);
                EmitInt(il, i);
                il.Emit(OpCodes.Ldloc, locals[i]);
                if (locals[i].LocalType.IsValueType) {
                    il.Emit(OpCodes.Box, locals[i].LocalType);
                }
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            return (Thunk)dm.CreateDelegate(typeof(Thunk));
        }

        private static void EmitFromObject(ILGenerator il, Type t) {
            if (t.IsValueType) {
                il.Emit(OpCodes.Unbox_Any, t);
            } else if (t != typeof(object)) {
                il.Emit(OpCodes.Castclass, t);
            }
        }

        private static void EmitBoxResult(ILGenerator il, Type ret) {
            if (ret == typeof(void)) {
                il.Emit(OpCodes.Ldnull);
                return;
            }
            if (ret.IsByRef) {
                var et = ret.GetElementType();
                il.Emit(OpCodes.Ldobj, et);
                if (et.IsValueType) {
                    il.Emit(OpCodes.Box, et);
                }
                return;
            }
            if (ret.IsByRefLike) {
                EmitConsumeByRefLike(il, ret);
                return;
            }
            if (ret.IsValueType) {
                il.Emit(OpCodes.Box, ret);
            }
        }

        // Span/ReadOnlySpan: their Length. A ref struct enumerator: MoveNext until false (bounded),
        // reading Current each step, then Dispose when it has one; the step count. Anything else: null.
        private static void EmitConsumeByRefLike(ILGenerator il, Type t) {
            var local = il.DeclareLocal(t);
            il.Emit(OpCodes.Stloc, local);
            var length = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
            if (length != null && length.PropertyType == typeof(int)) {
                il.Emit(OpCodes.Ldloca, local);
                il.Emit(OpCodes.Call, length.GetGetMethod());
                il.Emit(OpCodes.Box, typeof(int));
                return;
            }
            var moveNext = t.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (moveNext != null && moveNext.ReturnType == typeof(bool)) {
                var current = t.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                var dispose = t.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var count = il.DeclareLocal(typeof(int));
                var loop = il.DefineLabel();
                var end = il.DefineLabel();
                il.MarkLabel(loop);
                il.Emit(OpCodes.Ldloc, count);
                EmitInt(il, DrainLimit);
                il.Emit(OpCodes.Bge, end);
                il.Emit(OpCodes.Ldloca, local);
                il.Emit(OpCodes.Call, moveNext);
                il.Emit(OpCodes.Brfalse, end);
                if (current != null && current.GetGetMethod() != null) {
                    il.Emit(OpCodes.Ldloca, local);
                    il.Emit(OpCodes.Call, current.GetGetMethod());
                    if (current.PropertyType.IsByRef) {
                        // ref T Current: nothing to keep.
                    }
                    il.Emit(OpCodes.Pop);
                }
                il.Emit(OpCodes.Ldloc, count);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, count);
                il.Emit(OpCodes.Br, loop);
                il.MarkLabel(end);
                if (dispose != null) {
                    il.Emit(OpCodes.Ldloca, local);
                    il.Emit(OpCodes.Call, dispose);
                }
                il.Emit(OpCodes.Ldloc, count);
                il.Emit(OpCodes.Box, typeof(int));
                return;
            }
            il.Emit(OpCodes.Ldnull);
        }

        private static void EmitInt(ILGenerator il, int value) {
            il.Emit(OpCodes.Ldc_I4, value);
        }

        /// <summary>
        /// Walk a returned KenseiECS enumerable (World.AliveEntities, World.ActivePools, a Filter)
        /// so that iteration is exercised too. Returns the number of elements, or -1 when the value
        /// is not such an enumerable. Exceptions propagate: they belong to the call.
        /// </summary>
        public static int Drain(object value) {
            if (value == null) {
                return -1;
            }
            var t = value.GetType();
            if (t.Namespace == null || !t.Namespace.StartsWith("KenseiECS", StringComparison.Ordinal)) {
                return -1;
            }
            var getEnumerator = t.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getEnumerator == null) {
                return -1;
            }
            var thunk = For(getEnumerator, out _);
            if (thunk == null) {
                return -1;
            }
            if (getEnumerator.ReturnType.IsByRefLike) {
                // The thunk already drained it in place.
                return thunk(value, Array.Empty<object>()) is int n ? n : -1;
            }
            object enumerator = thunk(value, Array.Empty<object>());
            if (enumerator == null) {
                return -1;
            }
            var et = enumerator.GetType();
            var moveNext = et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var current = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
            if (moveNext == null) {
                return -1;
            }
            var mn = For(moveNext, out _);
            var cur = current != null ? For(current, out _) : null;
            int count = 0;
            while (count < DrainLimit && (bool)mn(enumerator, Array.Empty<object>())) {
                cur?.Invoke(enumerator, Array.Empty<object>());
                count++;
            }
            return count;
        }
    }
}
