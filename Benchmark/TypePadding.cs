using System.Runtime.CompilerServices;

namespace EcsBenchmark {
    /// <summary>
    /// Registers many distinct component types so that types touched afterwards
    /// get high indices (KenseiECS masks span several 64-bit words) and so that
    /// a world can hold hundreds of pools. Nesting a generic struct produces a
    /// new type per level without a source file per type.
    /// </summary>
    internal static class TypePadding {
        internal struct KenseiPad<T> : KenseiECS.IComponent { public int V; }
        internal struct LitePad<T> { public int V; }

        internal static Type[] KenseiTypes(int count) {
            var types = new Type[count];
            Type arg = typeof(KenseiPad<int>);
            for (int i = 0; i < count; i++) {
                arg = typeof(KenseiPad<>).MakeGenericType(arg);
                RuntimeHelpers.RunClassConstructor(typeof(KenseiECS.ComponentType<>).MakeGenericType(arg).TypeHandle);
                types[i] = arg;
            }
            return types;
        }

        internal static Type[] LiteTypes(int count) {
            var types = new Type[count];
            Type arg = typeof(LitePad<int>);
            for (int i = 0; i < count; i++) {
                arg = typeof(LitePad<>).MakeGenericType(arg);
                types[i] = arg;
            }
            return types;
        }

        internal static void CreateKenseiPools(KenseiECS.World world, Type[] types) {
            var pool = typeof(KenseiECS.World).GetMethod(nameof(KenseiECS.World.Pool))!;
            for (int i = 0; i < types.Length; i++) {
                pool.MakeGenericMethod(types[i]).Invoke(world, null);
            }
        }

        internal static void AddKenseiDefault(KenseiECS.World world, KenseiECS.Entity entity, Type type) {
            var add = typeof(KenseiECS.World).GetMethod(nameof(KenseiECS.World.Add))!.MakeGenericMethod(type);
            add.Invoke(world, new object[] { entity, Activator.CreateInstance(type)! });
        }

        internal static void CreateLitePools(Leopotam.EcsLite.EcsWorld world, Type[] types) {
            var pool = typeof(Leopotam.EcsLite.EcsWorld).GetMethod(nameof(Leopotam.EcsLite.EcsWorld.GetPool))!;
            for (int i = 0; i < types.Length; i++) {
                pool.MakeGenericMethod(types[i]).Invoke(world, null);
            }
        }
    }
}
