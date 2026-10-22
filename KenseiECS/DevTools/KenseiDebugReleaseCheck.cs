// Compile-time backstop for KenseiDebugBuildGuard (Editor). The guard reads the defines of the
// active Build Profile and Player Settings only; a player build can also get KENSEI_DEBUG from
// BuildPlayerOptions.extraScriptingDefines or an Assets/csc.rsp, which the guard cannot see. The
// player compile sees every define source, so a release player with the define fails here.
// UNITY_5_3_OR_NEWER keeps the check to Unity compiles: the .NET test build defines KENSEI_DEBUG
// without UNITY_EDITOR or DEVELOPMENT_BUILD on purpose.
#if UNITY_5_3_OR_NEWER && KENSEI_DEBUG && !UNITY_EDITOR && !DEVELOPMENT_BUILD && !KENSEI_ALLOW_DEBUG_RELEASE
#error KenseiECS: KENSEI_DEBUG is defined in a release (non-development) player build; it compiles validation and debug tooling into the player. Remove it from every define source (Build Profile, Player Settings, BuildPlayerOptions.extraScriptingDefines, csc.rsp), tick Development Build, or add KENSEI_ALLOW_DEBUG_RELEASE to ship it on purpose.
#endif
