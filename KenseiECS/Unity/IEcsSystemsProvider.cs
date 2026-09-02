#if UNITY_2018_1_OR_NEWER
namespace KenseiECS {
    /// <summary>
    /// Implement on a MonoBehaviour to expose your root SystemsRunner to framework editor tools.
    /// EcsSystemsWindow auto-discovers providers in play mode — no manual wiring needed.
    /// EcsBootstrap already implements it.
    ///
    /// Usage:
    ///   public class MyBootstrap : MonoBehaviour, IEcsSystemsProvider {
    ///       public SystemsRunner Systems { get; private set; }
    ///   }
    /// </summary>
    public interface IEcsSystemsProvider {
        SystemsRunner Systems { get; }
    }
}
#endif
