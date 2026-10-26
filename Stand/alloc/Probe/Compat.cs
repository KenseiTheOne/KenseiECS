using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KenseiECS;

namespace KenseiStand.Alloc.Probe {
    // Open-instance delegates over Listeners<T> (a struct): the receiver is passed by ref.
    public delegate void InvokeArgFn<TL, TArg>(ref Listeners<TL> self, TArg arg, Action<TL, TArg> call, Action<TL, TArg, Exception> onError) where TL : class;
    public delegate void InvokeFn<TL>(ref Listeners<TL> self, Action<TL> call, Action<TL, Exception> onError) where TL : class;

    /// <summary>
    /// API added after 2.1.1, reached through reflection with an existence check, so the probe
    /// compiles against 2.1.1, 2.2.0 and the working copy alike. Every member is resolved into a
    /// typed delegate before any measured window; a window only calls the delegate (one indirect
    /// call, no reflection, no allocation). A null delegate means "not in this version".
    /// </summary>
    public static class Compat {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static;

        public static readonly Action<World, int, int> WorldReserve =
            Bind<Action<World, int, int>>(typeof(World).GetMethod("Reserve", Inst, null, new[] { typeof(int), typeof(int) }, null));

        public static readonly Func<World, int> GrowthsAfterReserve =
            Bind<Func<World, int>>(typeof(World).GetProperty("GrowthsAfterReserve", Inst)?.GetGetMethod());

        public static readonly PropertyInfo FirstGrowthAfterReserveProperty =
            typeof(World).GetProperty("FirstGrowthAfterReserve", Inst);

        public static readonly Action<Filter, int> FilterReserve =
            Bind<Action<Filter, int>>(typeof(Filter).GetMethod("Reserve", Inst, null, new[] { typeof(int) }, null));

        public static readonly Func<SystemsRunner, string, SystemsRunner, SystemsRunner> AddGroup =
            Bind<Func<SystemsRunner, string, SystemsRunner, SystemsRunner>>(
                typeof(SystemsRunner).GetMethod("AddGroup", Inst, null, new[] { typeof(string), typeof(SystemsRunner) }, null));

        public static readonly Action<SystemsRunner, bool> RunnerSetActive =
            Bind<Action<SystemsRunner, bool>>(typeof(SystemsRunner).GetMethod("SetActive", Inst, null, new[] { typeof(bool) }, null));

        public static Action<ComponentPool<T>, int> PoolReserve<T>() where T : struct, IComponent =>
            Bind<Action<ComponentPool<T>, int>>(typeof(ComponentPool<T>).GetMethod("Reserve", Inst, null, new[] { typeof(int) }, null));

        public static Action<int, int> Prewarm<T>() where T : struct =>
            Bind<Action<int, int>>(typeof(EventBuffer<T>).GetMethod("Prewarm", Stat, null, new[] { typeof(int), typeof(int) }, null));

        public static Func<SystemsRunner, bool, SystemsRunner> OneFrameClean<T>() where T : struct, IComponent {
            var generic = typeof(SystemsRunner).GetMethods(Inst).FirstOrDefault(m =>
                m.Name == "OneFrame" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
            return Bind<Func<SystemsRunner, bool, SystemsRunner>>(generic?.MakeGenericMethod(typeof(T)));
        }

        public static InvokeArgFn<TL, TArg> InvokeArg<TL, TArg>() where TL : class {
            var generic = typeof(Listeners<TL>).GetMethods(Inst).FirstOrDefault(m =>
                m.Name == "Invoke" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
            return Bind<InvokeArgFn<TL, TArg>>(generic?.MakeGenericMethod(typeof(TArg)));
        }

        public static InvokeFn<TL> Invoke<TL>() where TL : class =>
            Bind<InvokeFn<TL>>(typeof(Listeners<TL>).GetMethod("Invoke", Inst, null,
                new[] { typeof(Action<TL>), typeof(Action<TL, Exception>) }, null));

        /// <summary> FirstGrowthAfterReserve as text (boxes; call outside windows only). </summary>
        public static string FirstGrowthText(World world) {
            if (FirstGrowthAfterReserveProperty == null) {
                return null;
            }
            return FirstGrowthAfterReserveProperty.GetValue(world)?.ToString();
        }

        /// <summary> Names of the post-2.1.1 members found in the compiled KenseiECS. </summary>
        public static List<string> Features() {
            var list = new List<string>();
            if (WorldReserve != null) list.Add("World.Reserve");
            if (GrowthsAfterReserve != null) list.Add("World.GrowthsAfterReserve");
            if (FirstGrowthAfterReserveProperty != null) list.Add("World.FirstGrowthAfterReserve");
            if (FilterReserve != null) list.Add("Filter.Reserve");
            if (PoolReserve<Pos>() != null) list.Add("ComponentPool.Reserve");
            if (Prewarm<EvPrewarm>() != null) list.Add("EventBuffer.Prewarm");
            if (InvokeArg<IView, int>() != null) list.Add("Listeners.Invoke<TArg>");
            if (Invoke<IView>() != null) list.Add("Listeners.Invoke");
            if (AddGroup != null) list.Add("SystemsRunner.AddGroup");
            if (RunnerSetActive != null) list.Add("SystemsRunner.SetActive(bool)");
            if (OneFrameClean<Mark2>() != null) list.Add("SystemsRunner.OneFrame(cleanWhenDisabled)");
            return list;
        }

        public static string Missing(string api) => "not in this version: " + api;

        private static TDelegate Bind<TDelegate>(MethodInfo method) where TDelegate : Delegate {
            if (method == null) {
                return null;
            }
            return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method, false);
        }
    }
}
