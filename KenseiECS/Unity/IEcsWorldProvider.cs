namespace KenseiECS {
    /// <summary>
    /// Implement on a MonoBehaviour to expose your World to framework editor tools.
    /// WorldInspectorWindow auto-discovers providers in play mode — no manual wiring needed.
    ///
    /// Usage:
    ///   public class MyBootstrap : MonoBehaviour, IEcsWorldProvider {
    ///       public World World { get; private set; }
    ///   }
    /// </summary>
    public interface IEcsWorldProvider {
        World World { get; }
    }
}
