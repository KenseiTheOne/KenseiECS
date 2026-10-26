using UnityEngine;

namespace KenseiStand {
    /// <summary>Content asset of the stand: the asset that AssetBundle, SBP and Addressables builds pack.</summary>
    public sealed class StandData : ScriptableObject {
        public int value = 42;
        public string label = "kensei-stand";
    }
}
