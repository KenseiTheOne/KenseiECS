using System;
using UnityEngine;

namespace KenseiECS.Samples.BasicGame {
    [Serializable]
    public struct Position : IComponent {
        public Vector2 Value;
    }

    [Serializable]
    public struct Velocity : IComponent {
        public Vector2 Value;
    }

    /// <summary> One-frame event: the entity hit an arena wall. </summary>
    public struct BounceEvent : IComponent {
        public Vector2 Normal;
    }

    /// <summary> Links an entity to the Transform that displays it. </summary>
    public struct TransformRef : IComponent {
        public Transform Value;
    }
}
