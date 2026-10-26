// KenseiECS test stand, module unity: the control of a player run. The control scene
// (Assets/KenseiStand/StandControl.unity) holds only this component, so no KenseiECS code runs in it.
// When a stand player fails without reporting a result (a native crash, a hang), the stand builds and
// runs this scene with the same options: if the control fails too, the environment or Unity is at
// fault, not KenseiECS, and the case is inconclusive instead of a finding.
using UnityEngine;

namespace KenseiStand {
    public sealed class StandControl : MonoBehaviour {
        private void Start() {
            if (Application.isEditor) {
                return;
            }
            Debug.Log("[KENSEI-STAND-PLAYER] CONTROL OK");
            Application.Quit(0);
        }
    }
}
