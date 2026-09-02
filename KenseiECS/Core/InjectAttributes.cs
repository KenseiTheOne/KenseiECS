using System;

namespace KenseiECS {
    /// <summary>
    /// Filter field: required component types. Combine with [Exc] and [Any].
    /// The source generator emits Init for a partial system class:
    ///
    ///   public partial class MoveSystem : IRunSystem {
    ///       [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
    ///       private Filter _moving;
    ///       [Pool] private ComponentPool<Position> _positions;
    ///       [Shared] private GameConfig _config;
    ///
    ///       partial void OnInit(World world, SharedData shared) { }
    ///   }
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class IncAttribute : Attribute {
        public Type[] Types { get; }

        public IncAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> Filter field: excluded component types. </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ExcAttribute : Attribute {
        public Type[] Types { get; }

        public ExcAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> Filter field: at least one of these component types is required. </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class AnyAttribute : Attribute {
        public Type[] Types { get; }

        public AnyAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> ComponentPool<T> field: injected with world.Pool<T>(). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class PoolAttribute : Attribute {
    }

    /// <summary> Group<...> field: injected with world.Group<...>(). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class GroupAttribute : Attribute {
    }

    /// <summary> Field injected with shared.Get<T>(key). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SharedAttribute : Attribute {
        public string Key { get; }

        public SharedAttribute(string key = null) {
            Key = key;
        }
    }
}
