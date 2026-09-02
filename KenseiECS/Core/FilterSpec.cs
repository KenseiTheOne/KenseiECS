namespace KenseiECS {
    /// <summary>
    /// Static filter description for world.Filter<Inc<A, B>, Exc<C>>().
    /// Specs are empty structs; Apply adds their constraints to a builder.
    /// </summary>
    public interface IFilterSpec {
        void Apply(FilterBuilder builder);
    }

    /// <summary> Spec with no constraints — placeholder for an unused slot. </summary>
    public struct None : IFilterSpec {
        public void Apply(FilterBuilder builder) {
        }
    }

    public struct Inc<T1> : IFilterSpec
        where T1 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>();
    }

    public struct Inc<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>();
    }

    public struct Inc<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>();
    }

    public struct Inc<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>();
    }

    public struct Inc<T1, T2, T3, T4, T5> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>().Inc<T5>();
    }

    public struct Inc<T1, T2, T3, T4, T5, T6> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Inc<T1>().Inc<T2>().Inc<T3>().Inc<T4>().Inc<T5>().Inc<T6>();
    }

    public struct Exc<T1> : IFilterSpec
        where T1 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>();
    }

    public struct Exc<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>();
    }

    public struct Exc<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>().Exc<T3>();
    }

    public struct Exc<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Exc<T1>().Exc<T2>().Exc<T3>().Exc<T4>();
    }

    public struct Any<T1, T2> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>();
    }

    public struct Any<T1, T2, T3> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>().Any<T3>();
    }

    public struct Any<T1, T2, T3, T4> : IFilterSpec
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent {
        public void Apply(FilterBuilder builder) =>
            builder.Any<T1>().Any<T2>().Any<T3>().Any<T4>();
    }
}
