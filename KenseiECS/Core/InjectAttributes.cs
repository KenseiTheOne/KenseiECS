using System;

namespace KenseiECS {
    /// <summary>
    /// Filter field: required component types. Combine with [Exc] and [Any].
    /// The source generator emits Init for a partial system class:
    ///
    /// <code>
    ///   public partial class MoveSystem : IRunSystem {
    ///       [Inc(typeof(Position), typeof(Velocity))] [Exc(typeof(Frozen))]
    ///       private Filter _moving;
    ///       [Pool] private ComponentPool&lt;Position&gt; _positions;
    ///       [Shared] private GameConfig _config;
    ///
    ///       partial void OnInit(World world, SharedData shared) { }
    ///   }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class IncAttribute : Attribute {
        /// <summary> Component types the filter requires. </summary>
        public Type[] Types { get; }

        /// <summary> Declare the component types the filter requires. </summary>
        public IncAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> Filter field: excluded component types. </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ExcAttribute : Attribute {
        /// <summary> Component types the filter excludes. </summary>
        public Type[] Types { get; }

        /// <summary> Declare the component types the filter excludes. </summary>
        public ExcAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> Filter field: at least one of these component types is required. </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class AnyAttribute : Attribute {
        /// <summary> Component types of which at least one is required. </summary>
        public Type[] Types { get; }

        /// <summary> Declare component types of which at least one is required. </summary>
        public AnyAttribute(params Type[] types) {
            Types = types;
        }
    }

    /// <summary> ComponentPool&lt;T&gt; field: injected with world.Pool&lt;T&gt;(). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class PoolAttribute : Attribute {
    }

    /// <summary> Group&lt;...&gt; field: injected with world.Group&lt;...&gt;(). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class GroupAttribute : Attribute {
    }

    /// <summary> Field injected with shared.Get&lt;T&gt;(key). </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SharedAttribute : Attribute {
        /// <summary> Key passed to shared.Get&lt;T&gt;(key); null selects the instance registered without a key. </summary>
        public string Key { get; }

        /// <summary> Inject the shared instance of the field's type, optionally under the given key. </summary>
        public SharedAttribute(string key = null) {
            Key = key;
        }
    }
}
