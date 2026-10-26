// KenseiECS test stand, module unity: a provider of the EcsEntityView.Spawn case (StandSpawnCheck.cs).
// A MonoBehaviour in a file of its own name, so Unity has a MonoScript for it.
using KenseiECS;

namespace KenseiStand {
    public sealed class StandSpawnCProvider : EcsComponentProvider<StandSpawnC> { }
}
