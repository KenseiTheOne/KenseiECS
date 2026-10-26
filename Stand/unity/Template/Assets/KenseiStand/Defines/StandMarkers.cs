// KenseiECS test stand, module unity. An assembly with no reference to KenseiECS: its compiled DLL
// shows which scripting defines a compile had, also when KenseiECS itself fails to compile. The stand
// searches the DLL metadata for the class names below ("StandMark_<DEFINE>").
namespace KenseiStand {
#if DEVELOPMENT_BUILD
    public static class StandMark_DEVELOPMENT_BUILD { }
#endif
#if KENSEI_DEBUG
    public static class StandMark_KENSEI_DEBUG { }
#endif
#if KENSEI_ALLOW_DEBUG_RELEASE
    public static class StandMark_KENSEI_ALLOW_DEBUG_RELEASE { }
#endif
#if UNITY_EDITOR
    public static class StandMark_UNITY_EDITOR { }
#endif
#if ENABLE_IL2CPP
    public static class StandMark_ENABLE_IL2CPP { }
#endif
#if ADDRESSABLES_ADD_DEFINES
    public static class StandMark_ADDRESSABLES_ADD_DEFINES { }
#endif
    public static class StandMark_Always { }
}
