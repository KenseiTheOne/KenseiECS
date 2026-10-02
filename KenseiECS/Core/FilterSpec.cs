namespace KenseiECS {
    /// <summary>
    /// Static filter description for world.Filter&lt;Inc&lt;A, B&gt;, Exc&lt;C&gt;&gt;().
    /// Specs are empty structs; Apply adds their constraints to a builder.
    /// </summary>
    public interface IFilterSpec {
        /// <summary> Adds this spec's constraints to <paramref name="builder"/>. </summary>
        /// <param name="builder">Builder that receives the constraints.</param>
        void Apply(FilterBuilder builder);
    }

    /// <summary> Spec with no constraints — placeholder for an unused slot. </summary>
    public struct None : IFilterSpec {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) {
        }
    }

    /// <summary> Spec requiring a component: adds an Inc constraint for <typeparamref name="T1"/>. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    public struct Inc<T1> : IFilterSpec
        where T1 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>();
    }

    /// <summary> Spec requiring every listed component: adds an Inc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    /// <typeparam name="T2">Required component type.</typeparam>
    public struct Inc<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>();
    }

    /// <summary> Spec requiring every listed component: adds an Inc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    /// <typeparam name="T2">Required component type.</typeparam>
    /// <typeparam name="T3">Required component type.</typeparam>
    public struct Inc<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>();
    }

    /// <summary> Spec requiring every listed component: adds an Inc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    /// <typeparam name="T2">Required component type.</typeparam>
    /// <typeparam name="T3">Required component type.</typeparam>
    /// <typeparam name="T4">Required component type.</typeparam>
    public struct Inc<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>();
    }

    /// <summary> Spec requiring every listed component: adds an Inc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    /// <typeparam name="T2">Required component type.</typeparam>
    /// <typeparam name="T3">Required component type.</typeparam>
    /// <typeparam name="T4">Required component type.</typeparam>
    /// <typeparam name="T5">Required component type.</typeparam>
    public struct Inc<T1, T2, T3, T4, T5> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>().Inc<T5>();
    }

    /// <summary> Spec requiring every listed component: adds an Inc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Required component type.</typeparam>
    /// <typeparam name="T2">Required component type.</typeparam>
    /// <typeparam name="T3">Required component type.</typeparam>
    /// <typeparam name="T4">Required component type.</typeparam>
    /// <typeparam name="T5">Required component type.</typeparam>
    /// <typeparam name="T6">Required component type.</typeparam>
    public struct Inc<T1, T2, T3, T4, T5, T6> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>().Inc<T5>().Inc<T6>();
    }

    /// <summary> Spec excluding entities that have a component: adds an Exc constraint for <typeparamref name="T1"/>. </summary>
    /// <typeparam name="T1">Excluded component type.</typeparam>
    public struct Exc<T1> : IFilterSpec
        where T1 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>();
    }

    /// <summary> Spec excluding entities that have any listed component: adds an Exc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Excluded component type.</typeparam>
    /// <typeparam name="T2">Excluded component type.</typeparam>
    public struct Exc<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>();
    }

    /// <summary> Spec excluding entities that have any listed component: adds an Exc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Excluded component type.</typeparam>
    /// <typeparam name="T2">Excluded component type.</typeparam>
    /// <typeparam name="T3">Excluded component type.</typeparam>
    public struct Exc<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>().Exc<T3>();
    }

    /// <summary> Spec excluding entities that have any listed component: adds an Exc constraint for each type parameter. </summary>
    /// <typeparam name="T1">Excluded component type.</typeparam>
    /// <typeparam name="T2">Excluded component type.</typeparam>
    /// <typeparam name="T3">Excluded component type.</typeparam>
    /// <typeparam name="T4">Excluded component type.</typeparam>
    public struct Exc<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>().Exc<T3>().Exc<T4>();
    }

    /// <summary> Spec requiring at least one of the listed components: adds an Any constraint for each type parameter. </summary>
    /// <typeparam name="T1">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T2">Component type, at least one of which is required.</typeparam>
    public struct Any<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>();
    }

    /// <summary> Spec requiring at least one of the listed components: adds an Any constraint for each type parameter. </summary>
    /// <typeparam name="T1">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T2">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T3">Component type, at least one of which is required.</typeparam>
    public struct Any<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>().Any<T3>();
    }

    /// <summary> Spec requiring at least one of the listed components: adds an Any constraint for each type parameter. </summary>
    /// <typeparam name="T1">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T2">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T3">Component type, at least one of which is required.</typeparam>
    /// <typeparam name="T4">Component type, at least one of which is required.</typeparam>
    public struct Any<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        /// <inheritdoc/>
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>().Any<T3>().Any<T4>();
    }
}
