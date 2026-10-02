// Stand-ins for types that do not exist in a .NET build, so the snippets that use
// them still compile. Nothing here is shown on the site.
//
// - UnityEngine.MonoBehaviour: the Unity engine is not referenced.
// - KenseiECS.Unity layer (EcsBootstrap, EcsComponentProvider<T>): compiled only
//   inside Unity (UNITY_2018_1_OR_NEWER). The signatures below mirror
//   KenseiECS/Unity/EcsBootstrap.cs and EcsComponentProvider.cs; keep them in sync.
// - ecslite-di (EcsFilterInject, EcsPoolInject, EcsSharedInject, Inject): not on
//   NuGet. The shapes mirror github.com/Leopotam/ecslite-di.

namespace UnityEngine {
    public class MonoBehaviour { }
}

namespace Docs.Snippets.Reference.UnityLayer {
    using KenseiECS;

    public abstract class EcsBootstrap : UnityEngine.MonoBehaviour {
        public World World { get; private set; }
        public SharedData Shared { get; private set; }
        public SystemsRunner Systems { get; private set; }

        protected virtual WorldConfig CreateConfig() => new WorldConfig();

        protected abstract void Configure(SystemsRunner update, SystemsRunner fixedUpdate, SystemsRunner lateUpdate, SharedData shared);
    }

    public abstract class EcsComponentProvider : UnityEngine.MonoBehaviour { }

    public abstract class EcsComponentProvider<T> : EcsComponentProvider where T : struct, IComponent {
        public T Value;
    }
}

namespace Leopotam.EcsLite.Di {
    public interface IEcsInclude { }
    public interface IEcsExclude { }

    public struct Inc<T1, T2> : IEcsInclude where T1 : struct where T2 : struct { }
    public struct Exc<T1> : IEcsExclude where T1 : struct { }

    public struct EcsFilterInject<TInc, TExc> where TInc : struct, IEcsInclude where TExc : struct, IEcsExclude {
        public EcsFilter Value;
    }

    public struct EcsPoolInject<T> where T : struct {
        public EcsPool<T> Value;
    }

    public struct EcsSharedInject<T> where T : class {
        public T Value;
    }

    public static class Extensions {
        public static IEcsSystems Inject(this IEcsSystems systems, params object[] injects) => systems;
    }
}
