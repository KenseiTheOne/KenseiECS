#if UNITY_EDITOR && KENSEI_DEBUG
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Editor window for viewing ECS profiler events.
    /// Shows entity lifecycle history with timestamps and call stacks.
    /// Supports clicking on entities to view their history,
    /// with back/forward navigation.
    ///
    /// Open via menu: KenseiECS → Profiler
    /// Enable profiling: EcsProfiler.Enable(world);
    /// </summary>
    public class EcsProfilerWindow : EditorWindow {
        private Vector2 _scrollPos;
        private Vector2 _stackScrollPos;
        private string _searchFilter = "";
        private int _selectedEvent = -1;
        private ProfileEventType? _typeFilter;

        // Entity navigation — back/forward history
        private int? _focusedEntity;
        private readonly List<int> _navHistory = new();
        private int _navIndex = -1;

        private IReadOnlyList<ProfileEvent> _events;

        [MenuItem("KenseiECS/Profiler")]
        private static void Open() {
            var window = GetWindow<EcsProfilerWindow>("KenseiECS Profiler");
            window.Show();
        }

        private void OnEnable() {
            EditorApplication.update += RepaintIfPlaying;
        }

        private void OnDisable() {
            EditorApplication.update -= RepaintIfPlaying;
        }

        private void RepaintIfPlaying() {
            if (EditorApplication.isPlaying) {
                Repaint();
            }
        }

        private void OnGUI() {
            if (!EcsProfiler.IsEnabled) {
                EditorGUILayout.HelpBox(
                    "Profiler is not enabled.\nCall EcsProfiler.Enable(world) from your code.",
                    MessageType.Info);
                return;
            }

            _events = EcsProfiler.GetEvents();

            DrawToolbar();
            DrawNavBar();

            EditorGUILayout.BeginVertical();

            // Event list — 60% height
            _scrollPos = EditorGUILayout.BeginScrollView(
                _scrollPos, GUILayout.Height(position.height * 0.55f));
            DrawEventList();
            EditorGUILayout.EndScrollView();

            // Separator
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            // Stack trace — remaining height
            _stackScrollPos = EditorGUILayout.BeginScrollView(_stackScrollPos);
            DrawStackTrace();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        // =================================================================
        // Toolbar
        // =================================================================

        private void DrawToolbar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField($"Events: {_events.Count}", GUILayout.Width(100));

            DrawFilterButton("All", null);
            DrawFilterButton("Created", ProfileEventType.Created);
            DrawFilterButton("Destroyed", ProfileEventType.Destroyed);
            DrawFilterButton("+Comp", ProfileEventType.ComponentAdded);
            DrawFilterButton("-Comp", ProfileEventType.ComponentRemoved);

            GUILayout.FlexibleSpace();

            _searchFilter = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(120));

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                _searchFilter = "";
                EcsProfiler.Clear();
                _selectedEvent = -1;
                ClearNavigation();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterButton(string label, ProfileEventType? type) {
            var oldColor = GUI.backgroundColor;
            if (_typeFilter == type) {
                GUI.backgroundColor = Color.cyan;
            }

            if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(65))) {
                _typeFilter = type;
            }

            GUI.backgroundColor = oldColor;
        }

        // =================================================================
        // Navigation bar — back/forward + focused entity
        // =================================================================

        private void DrawNavBar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Back button
            EditorGUI.BeginDisabledGroup(!CanGoBack());
            if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(30))) {
                GoBack();
            }
            EditorGUI.EndDisabledGroup();

            // Forward button
            EditorGUI.BeginDisabledGroup(!CanGoForward());
            if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(30))) {
                GoForward();
            }
            EditorGUI.EndDisabledGroup();

            // Current focus
            if (_focusedEntity.HasValue) {
                EditorGUILayout.LabelField(
                    $"Entity {_focusedEntity.Value} history",
                    EditorStyles.boldLabel, GUILayout.Width(150));

                if (GUILayout.Button("Show All", EditorStyles.toolbarButton, GUILayout.Width(70))) {
                    _focusedEntity = null;
                    _selectedEvent = -1;
                }
            } else {
                EditorGUILayout.LabelField("All events", GUILayout.Width(150));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Event list
        // =================================================================

        private void DrawEventList() {
            // Headers
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Tick", GUILayout.Width(50));
            EditorGUILayout.LabelField("Time (ms)", GUILayout.Width(80));
            EditorGUILayout.LabelField("Type", GUILayout.Width(110));
            EditorGUILayout.LabelField("Entity", GUILayout.Width(100));
            EditorGUILayout.LabelField("Component", GUILayout.MinWidth(100));
            EditorGUILayout.EndHorizontal();

            for (int i = _events.Count - 1; i >= 0; i--) {
                var evt = _events[i];

                // Type filter
                if (_typeFilter.HasValue && evt.Type != _typeFilter.Value) {
                    continue;
                }

                // Entity focus filter
                if (_focusedEntity.HasValue && evt.EntityIndex != _focusedEntity.Value) {
                    continue;
                }

                // Search filter
                if (!string.IsNullOrEmpty(_searchFilter)) {
                    var filter = _searchFilter.ToLowerInvariant();
                    bool match = evt.EntityIndex.ToString().Contains(filter)
                        || (evt.ComponentType != null && evt.ComponentType.ToLowerInvariant().Contains(filter));
                    if (!match) {
                        continue;
                    }
                }

                DrawEventRow(evt, i);
            }
        }

        private void DrawEventRow(ProfileEvent evt, int index) {
            bool selected = _selectedEvent == index;

            var bgColor = GUI.backgroundColor;
            GUI.backgroundColor = selected
                ? new Color(0.3f, 0.5f, 0.8f)
                : GetEventColor(evt.Type);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.LabelField(evt.Tick.ToString(), GUILayout.Width(50));
            EditorGUILayout.LabelField($"{evt.TimestampMs:F1}", GUILayout.Width(80));
            EditorGUILayout.LabelField(evt.Type.ToString(), GUILayout.Width(110));

            // Clickable entity link
            var entityLabel = $"E({evt.EntityIndex}v{evt.Generation})";
            var linkStyle = new GUIStyle(EditorStyles.miniButton) {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold
            };

            if (GUILayout.Button(entityLabel, linkStyle, GUILayout.Width(100))) {
                NavigateTo(evt.EntityIndex);
            }

            EditorGUILayout.LabelField(evt.ComponentType ?? "—", GUILayout.MinWidth(100));

            EditorGUILayout.EndHorizontal();

            // Click row to select (show stack trace)
            var rect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown
                && rect.Contains(Event.current.mousePosition)
                && Event.current.button == 0) {
                // Left click on row (not on entity button) — select for stack trace
                _selectedEvent = index;
                Event.current.Use();
            }

            GUI.backgroundColor = bgColor;
        }

        private Color GetEventColor(ProfileEventType type) {
            return type switch {
                ProfileEventType.Created          => new Color(0.6f, 0.9f, 0.6f),
                ProfileEventType.Destroyed        => new Color(0.9f, 0.6f, 0.6f),
                ProfileEventType.ComponentAdded   => new Color(0.6f, 0.8f, 0.9f),
                ProfileEventType.ComponentRemoved => new Color(0.9f, 0.8f, 0.6f),
                _                                 => Color.white
            };
        }

        // =================================================================
        // Stack trace panel
        // =================================================================

        private void DrawStackTrace() {
            if (_selectedEvent < 0 || _selectedEvent >= _events.Count) {
                EditorGUILayout.LabelField("Select an event to view its call stack.",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var evt = _events[_selectedEvent];

            EditorGUILayout.LabelField(
                $"Tick {evt.Tick} — {evt.Type} — E({evt.EntityIndex}v{evt.Generation}) at {evt.TimestampMs:F1}ms",
                EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(evt.CallStack)) {
                var style = new GUIStyle(EditorStyles.label) {
                    wordWrap = true,
                    richText = false
                };
                EditorGUILayout.LabelField(evt.CallStack, style);
            } else {
                EditorGUILayout.LabelField("No call stack captured.");
            }
        }

        // =================================================================
        // Navigation — back/forward history
        // =================================================================

        private void NavigateTo(int entityIndex) {
            // If navigating to same entity, do nothing
            if (_focusedEntity.HasValue && _focusedEntity.Value == entityIndex) {
                return;
            }

            // Trim forward history when navigating to new entity
            if (_navIndex < _navHistory.Count - 1) {
                _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);
            }

            _navHistory.Add(entityIndex);
            _navIndex = _navHistory.Count - 1;
            _focusedEntity = entityIndex;
            _selectedEvent = -1;
        }

        private bool CanGoBack() {
            return _navIndex > 0;
        }

        private bool CanGoForward() {
            return _navIndex < _navHistory.Count - 1;
        }

        private void GoBack() {
            if (!CanGoBack()) {
                return;
            }
            _navIndex--;
            _focusedEntity = _navHistory[_navIndex];
            _selectedEvent = -1;
        }

        private void GoForward() {
            if (!CanGoForward()) {
                return;
            }
            _navIndex++;
            _focusedEntity = _navHistory[_navIndex];
            _selectedEvent = -1;
        }

        private void ClearNavigation() {
            _focusedEntity = null;
            _navHistory.Clear();
            _navIndex = -1;
        }
    }
}
#endif
