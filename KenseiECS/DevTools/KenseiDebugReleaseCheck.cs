// Marker for the release check after the player scripts compile (Editor/KenseiDebugPlayerScriptsCheck).
// The preprocess guard (KenseiDebugBuildGuard) reads the Scripting Define Symbols of the active Build
// Profile and Player Settings only; a player build can also get KENSEI_DEBUG from
// BuildPlayerOptions.extraScriptingDefines, Player Settings -> Additional Compiler Arguments or an
// Assets/csc.rsp. The player compile sees every define source, so a compile with the define contains
// this type, and the editor check fails a non-development player build whose KenseiECS.dll has it.
// Whether the build is a development one is read from the build options there, like the guard does,
// not from DEVELOPMENT_BUILD (deprecated in Unity 6.6).
// Content builds (Addressables, the Scriptable Build Pipeline, AssetBundles) compile the same player
// scripts, but Unity never runs that check for them, so Debug Mode does not stop them. 2.2.0 had an
// #error here, which failed every content build while Debug Mode was on.
// UNITY_5_3_OR_NEWER keeps the marker to Unity compiles: the .NET test build defines KENSEI_DEBUG
// without UNITY_EDITOR on purpose.
#if UNITY_5_3_OR_NEWER && KENSEI_DEBUG && !UNITY_EDITOR && !KENSEI_ALLOW_DEBUG_RELEASE
namespace KenseiECS.Internal {
    // The name is what the editor check looks for (KenseiDebugDefines.RELEASE_MARKER); it must not
    // appear anywhere else in the runtime assembly.
    internal static class KenseiDebugInReleasePlayer { }
}
#endif
