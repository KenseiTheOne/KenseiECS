#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Editor window that shows the system tree of the root SystemsRunner in play mode
    /// and enables or disables systems at runtime. Under KENSEI_DEBUG it also shows
    /// the last and peak run time of every run system.
    ///
    /// Open via menu: KenseiECS → Systems
    /// Auto-discovers any MonoBehaviour implementing IEcsSystemsProvider (EcsBootstrap does).
    /// </summary>
    public class EcsSystemsWindow : EditorWindow {
        private const float IndentWidth = 16f;
        private const float ToggleWidth = 18f;
        private const float PhaseWidth = 44f;
        private const float TypeWidth = 180f;
        private const float TimingWidth = 64f;

        private static SystemsRunner _root;

        private static GUIStyle _typeStyle;
        private static GUIStyle _disabledStyle;
        private static GUIStyle _phaseStyle;
        private static GUIStyle _numberStyle;

        private Vector2 _scrollPos;
        private double _lastRepaintTime;
        private double _lastAutoBindTime;

        private static GUIStyle TypeStyle {
            get {
                if (_typeStyle == null) {
                    _typeStyle = new GUIStyle(EditorStyles.miniLabel);
                    _typeStyle.normal.textColor = Color.gray;
                }
                return _typeStyle;
            }
        }

        private static GUIStyle DisabledStyle {
            get {
                if (_disabledStyle == null) {
                    _disabledStyle = new GUIStyle(EditorStyles.label);
                    _disabledStyle.normal.textColor = Color.gray;
                }
                return _disabledStyle;
            }
        }

        private static GUIStyle PhaseStyle {
            get {
                if (_phaseStyle == null) {
                    _phaseStyle = new GUIStyle(EditorStyles.miniLabel) {
                        fontStyle = FontStyle.Italic
                    };
                    _phaseStyle.normal.textColor = new Color(0.3f, 0.6f, 1f);
                }
                return _phaseStyle;
            }
        }

        private static GUIStyle NumberStyle {
            get {
                if (_numberStyle == null) {
                    _numberStyle = new GUIStyle(EditorStyles.label) {
                        alignment = TextAnchor.MiddleRight
                    };
                }
                return _numberStyle;
            }
        }

        [MenuItem("KenseiECS/Systems")]
        private static void Open() {
            var window = GetWindow<EcsSystemsWindow>("KenseiECS Systems");
            window.Show();
        }

        // Statics survive Enter Play Mode without domain reload, so the hook lives
        // outside any window instance: a runner from a previous session must never
        // be shown in the next one.
        [InitializeOnLoadMethod]
        private static void HookPlayModeChanges() {
            EditorApplication.playModeStateChanged += ClearRoot;
        }

        private static void ClearRoot(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                _root = null;
            }
        }

        private void OnEnable() {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate() {
            if (!EditorApplication.isPlaying) {
                return;
            }

            double now = EditorApplication.timeSinceStartup;

            // Auto-bind at most once per second
            if (!IsRootValid()) {
                _root = null;
                if (now - _lastAutoBindTime > 1.0) {
                    _lastAutoBindTime = now;
                    TryAutoBind();
                }
            }

            // Repaint at most ~10 times per second
            if (now - _lastRepaintTime > 0.1) {
                _lastRepaintTime = now;
                Repaint();
            }
        }

        private static bool IsRootValid() {
            return IsLive(_root);
        }

        // A provider may still hand out the runner of a world it has destroyed. Binding it, the
        // window drops it on the next update and can find it first again on every later bind,
        // so the live runner of another provider would never be shown.
        private static bool IsLive(SystemsRunner runner) {
            return runner != null && runner.World != null && !runner.World.IsDestroyed;
        }

        private static void TryAutoBind() {
#if UNITY_2023_1_OR_NEWER
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
#else
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
            foreach (var mb in behaviours) {
                if (mb is IEcsSystemsProvider provider && IsLive(provider.Systems)) {
                    _root = provider.Systems;
                    return;
                }
            }
        }

        private void OnGUI() {
            if (!EditorApplication.isPlaying) {
                EditorGUILayout.HelpBox("Enter play mode to see the systems.", MessageType.Info);
                return;
            }

            if (!IsRootValid()) {
                _root = null;
                EditorGUILayout.HelpBox(
                    "No SystemsRunner found.\nAdd a MonoBehaviour implementing IEcsSystemsProvider (an EcsBootstrap subclass, for example) to your scene.",
                    MessageType.Info);
                return;
            }

            DrawToolbar();
            DrawHeader();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawRootRow();
            DrawRunner(_root, 1);
            EditorGUILayout.EndScrollView();
        }

        // =================================================================
        // Toolbar and header
        // =================================================================

        private static void DrawToolbar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField($"Tick: {_root.World.Tick}", GUILayout.Width(100));
            EditorGUILayout.LabelField(_root.IsInitialized ? "Initialized" : "Not initialized", GUILayout.Width(100));

            GUILayout.FlexibleSpace();

#if KENSEI_DEBUG
            if (GUILayout.Button("Reset peaks", EditorStyles.toolbarButton, GUILayout.Width(90))) {
                ResetTimings(_root);
            }
#endif

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawHeader() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Space(ToggleWidth);
            GUILayout.Label("System", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("Type", EditorStyles.miniLabel, GUILayout.Width(TypeWidth));
#if KENSEI_DEBUG
            GUILayout.Label("Last ms", NumberStyle, GUILayout.Width(TimingWidth));
            GUILayout.Label("Peak ms", NumberStyle, GUILayout.Width(TimingWidth));
#endif
            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // System tree
        // =================================================================

        private static void DrawRootRow() {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(ToggleWidth);
            GUILayout.Label("Root", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label(EditorTypeNames.Of(_root.GetType()), TypeStyle, GUILayout.Width(TypeWidth));
#if KENSEI_DEBUG
            GUILayout.Space(TimingWidth * 2);
#endif
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawRunner(SystemsRunner runner, int depth) {
            for (int i = 0; i < runner.SystemCount; i++) {
                var info = runner.GetSystemInfo(i);
                DrawSystemRow(runner, i, info, depth);
                if (info.ChildRunner != null) {
                    DrawRunner(info.ChildRunner, depth + 1);
                }
            }
        }

        private static void DrawSystemRow(SystemsRunner runner, int index, SystemsRunner.SystemInfo info, int depth) {
            bool canToggle = info.IsRunnable || info.ChildRunner != null;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(depth * IndentWidth);

            if (canToggle) {
                bool enabled = EditorGUILayout.Toggle(info.IsEnabled, GUILayout.Width(ToggleWidth));
                if (enabled != info.IsEnabled) {
                    runner.SetActive(index, enabled);
                }
            } else {
                GUILayout.Space(ToggleWidth);
            }

            GUIStyle nameStyle;
            if (canToggle && !info.IsEnabled) {
                nameStyle = DisabledStyle;
            } else if (info.ChildRunner != null) {
                nameStyle = EditorStyles.boldLabel;
            } else {
                nameStyle = EditorStyles.label;
            }
            GUILayout.Label(info.Name, nameStyle, GUILayout.ExpandWidth(true));

            if (info.IsSeparatePhase) {
                GUILayout.Label("phase", PhaseStyle, GUILayout.Width(PhaseWidth));
            }

            GUILayout.Label(EditorTypeNames.Of(info.System.GetType()), TypeStyle, GUILayout.Width(TypeWidth));

#if KENSEI_DEBUG
            if (info.IsRunnable) {
                GUILayout.Label(info.LastRunMs.ToString("F2"), NumberStyle, GUILayout.Width(TimingWidth));
                GUILayout.Label(info.PeakRunMs.ToString("F2"), NumberStyle, GUILayout.Width(TimingWidth));
            } else {
                GUILayout.Label("-", NumberStyle, GUILayout.Width(TimingWidth));
                GUILayout.Label("-", NumberStyle, GUILayout.Width(TimingWidth));
            }
#endif

            EditorGUILayout.EndHorizontal();
        }

#if KENSEI_DEBUG
        private static void ResetTimings(SystemsRunner runner) {
            runner.ResetTimings();
            for (int i = 0; i < runner.SystemCount; i++) {
                var child = runner.GetSystemInfo(i).ChildRunner;
                if (child != null) {
                    ResetTimings(child);
                }
            }
        }
#endif
    }
}
#endif
