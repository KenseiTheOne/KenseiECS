// Minimal UnityEngine surface for compiling KenseiECS/Unity/EcsBootstrap.cs outside Unity.
// EcsBootstrap uses only MonoBehaviour, [DisallowMultipleComponent] and [SerializeField]; Unity
// sends Awake/Start/Update/FixedUpdate/LateUpdate/OnDestroy, which UnityLoop below does through
// reflection, logging and swallowing an exception per message the way the engine does.
using System;
using System.Reflection;

namespace UnityEngine {
    public class Object { }

    public class Component : Object { }

    public class Behaviour : Component {
        public bool enabled = true;
    }

    public class MonoBehaviour : Behaviour { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }
}

namespace Stand.Runner.Kit {
    /// <summary> Sends Unity messages to a MonoBehaviour stub; an exception is returned, not thrown (Unity logs it and goes on). </summary>
    public static class UnityLoop {
        public static Exception Send(object behaviour, string message) {
            MethodInfo method = null;
            for (var type = behaviour.GetType(); type != null && method == null; type = type.BaseType) {
                method = type.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            if (method == null) {
                return null;
            }
            try {
                method.Invoke(behaviour, null);
                return null;
            } catch (TargetInvocationException e) {
                return e.InnerException;
            }
        }

        /// <summary> Set a private [SerializeField] the way the inspector would. </summary>
        public static void SetField(object behaviour, string field, object value) {
            for (var type = behaviour.GetType(); type != null; type = type.BaseType) {
                var f = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) {
                    f.SetValue(behaviour, value);
                    return;
                }
            }
            throw new MissingFieldException(behaviour.GetType().Name, field);
        }
    }
}
