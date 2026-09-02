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
        private const int EventsPerPage = 200;
        private const int MaxNavHistory = 50;

        private Vector2 _scrollPos;
        private Vector2 _stackScrollPos;
        private string _searchFilter = "";
        private int _selectedEvent = -1;
        private ProfileEventType? _typeFilter;

        // Entity navigation — back/forward history
        private int? _focusedEntity;
        private readonly List<int> _navHistory = new();
        private int _navIndex = -1;
        private int _currentPage;

        private IReadOnlyList<ProfileEvent> _events;
        private double _lastRepaintTime;
        private double _lastAutoBindTime;

        // Cached GUIStyles
        private static GUIStyle _entityLinkStyle;
        private static GUIStyle _stackTraceStyle;

        private static GUIStyle EntityLinkStyle {
            get {
                if (_entityLinkStyle == null) {
                    _entityLinkStyle = new GUIStyle(EditorStyles.miniButton) {
                        alignment = TextAnchor.MiddleLeft,
                        fontStyle = FontStyle.Bold
                    };
                }
                return _entityLinkStyle;
            }
        }

        private static GUIStyle StackTraceStyle {
            get {
                if (_stackTraceStyle == null) {
                    _stackTraceStyle = new GUIStyle(EditorStyles.label) {
                        wordWrap = true,
                        richText = false
                    };
                }
                return _stackTraceStyle;
            }
        }

        [MenuItem("KenseiECS/Profiler")]
        private static void Open() {
            var window = GetWindow<EcsProfilerWindow>("KenseiECS Profiler");
            window.Show();
        }

        private void OnEnable() {
            EditorApplication.update += ThrottledRepaint;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable() {
            EditorApplication.update -= ThrottledRepaint;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        // Without domain reload the window instance survives into the next play session,
        // where the old selection and navigation would index into a fresh event list.
        private void OnPlayModeChanged(PlayModeStateChange state) {
            if (state != PlayModeStateChange.ExitingEditMode) {
                return;
            }

            _events = null;
            _selectedEvent = -1;
            _currentPage = 0;
            ClearNavigation();
        }

        private void ThrottledRepaint() {
            if (!EditorApplication.isPlaying) {
                return;
            }

            double now = EditorApplication.timeSinceStartup;

            // Debug names come from the inspector's world; bind it from here too so
            // they show up when only the profiler window is open.
            if (!WorldInspectorWindow.IsWorldValid(WorldInspectorWindow.TargetWorld)) {
                WorldInspectorWindow.TargetWorld = null;
                if (now - _lastAutoBindTime > 1.0) {
                    _lastAutoBindTime = now;
                    WorldInspectorWindow.TryAutoBindWorld();
                }
            }

            if (now - _lastRepaintTime > 0.1) {
                _lastRepaintTime = now;
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

            DrawFilterButton(new GUIContent("All"), null);
            DrawFilterButton(new GUIContent("Created", "Show only entity creation events"), ProfileEventType.Created);
            DrawFilterButton(new GUIContent("Destroyed", "Show only entity destruction events"), ProfileEventType.Destroyed);
            DrawFilterButton(new GUIContent("+Comp", "Show only component added events"), ProfileEventType.ComponentAdded);
            DrawFilterButton(new GUIContent("-Comp", "Show only component removed events"), ProfileEventType.ComponentRemoved);

            GUILayout.FlexibleSpace();

            _searchFilter = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(120));

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                _searchFilter = "";
                EcsProfiler.Clear();
                _selectedEvent = -1;
                _currentPage = 0;
                ClearNavigation();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterButton(GUIContent content, ProfileEventType? type) {
            var oldColor = GUI.backgroundColor;
            if (_typeFilter == type) {
                GUI.backgroundColor = Color.cyan;
            }

            if (GUILayout.Button(content, EditorStyles.toolbarButton, GUILayout.Width(65))) {
                _typeFilter = type;
                _currentPage = 0;
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
            if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(30))) {
                GoBack();
            }
            EditorGUI.EndDisabledGroup();

            // Forward button
            EditorGUI.BeginDisabledGroup(!CanGoForward());
            if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(30))) {
                GoForward();
            }
            EditorGUI.EndDisabledGroup();

            // Current focus
            if (_focusedEntity.HasValue) {
                string entityName = DebugName(_focusedEntity.Value);
                EditorGUILayout.LabelField(
                    entityName == null
                        ? $"Entity {_focusedEntity.Value} history"
                        : $"Entity {_focusedEntity.Value} \"{entityName}\" history",
                    EditorStyles.boldLabel, GUILayout.Width(220));

                if (GUILayout.Button("Show All", EditorStyles.toolbarButton, GUILayout.Width(70))) {
                    _focusedEntity = null;
                    _selectedEvent = -1;
                    _currentPage = 0;
                }
            } else {
                EditorGUILayout.LabelField("All events", GUILayout.Width(150));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Event list with pagination
        // =================================================================

        private void DrawEventList() {
            // Headers
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Tick", GUILayout.Width(50));
            EditorGUILayout.LabelField("Time (ms)", GUILayout.Width(80));
            EditorGUILayout.LabelField("Type", GUILayout.Width(110));
            EditorGUILayout.LabelField("Entity", GUILayout.Width(160));
            EditorGUILayout.LabelField("Component", GUILayout.MinWidth(100));
            EditorGUILayout.EndHorizontal();

            string filterLower = string.IsNullOrEmpty(_searchFilter)
                ? null
                : _searchFilter.ToLowerInvariant();

            int displayed = 0;
            int skipped = 0;
            int skipTarget = _currentPage * EventsPerPage;

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
                if (filterLower != null) {
                    bool match = evt.EntityIndex.ToString().Contains(filterLower)
                        || (evt.ComponentType != null
                            && evt.ComponentType.IndexOf(filterLower, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) {
                        continue;
                    }
                }

                if (skipped < skipTarget) {
                    skipped++;
                    continue;
                }

                if (displayed >= EventsPerPage) {
                    break;
                }

                DrawEventRow(evt, i);
                displayed++;
            }

            // Pagination
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginDisabledGroup(_currentPage <= 0);
            if (GUILayout.Button("< Prev", EditorStyles.toolbarButton, GUILayout.Width(60))) {
                _currentPage--;
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.LabelField($"Page {_currentPage + 1}", EditorStyles.miniLabel, GUILayout.Width(60));

            if (GUILayout.Button("Next >", EditorStyles.toolbarButton, GUILayout.Width(60))) {
                _currentPage++;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
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
            if (GUILayout.Button(EntityLabel(evt.EntityIndex, evt.Generation), EntityLinkStyle, GUILayout.Width(160))) {
                NavigateTo(evt.EntityIndex);
            }

            EditorGUILayout.LabelField(evt.ComponentType ?? "-", GUILayout.MinWidth(100));

            EditorGUILayout.EndHorizontal();
            GUI.backgroundColor = bgColor;

            // Click row to select (show stack trace)
            var rect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown
                && rect.Contains(Event.current.mousePosition)
                && Event.current.button == 0) {
                _selectedEvent = index;
                Event.current.Use();
            }
        }

        private static Color GetEventColor(ProfileEventType type) {
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
                $"Tick {evt.Tick} - {evt.Type} - {EntityLabel(evt.EntityIndex, evt.Generation)} at {evt.TimestampMs:F1}ms",
                EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(evt.CallStack)) {
                EditorGUILayout.LabelField(evt.CallStack, StackTraceStyle);
            } else {
                EditorGUILayout.LabelField(
                    "No call stack captured. Set EcsProfiler.CaptureStacks = true to enable.");
            }
        }

        // =================================================================
        // Entity labels — debug names come from the world the inspector is bound to
        // =================================================================

        private static string EntityLabel(int entityIndex, int generation) {
            string entityName = DebugName(entityIndex);
            return entityName == null
                ? $"E({entityIndex}v{generation})"
                : $"E({entityIndex}v{generation}) \"{entityName}\"";
        }

        private static string DebugName(int entityIndex) {
            var world = WorldInspectorWindow.TargetWorld;
            if (!WorldInspectorWindow.IsWorldValid(world)) {
                return null;
            }
            return world.TryGetEntity(entityIndex, out var entity) ? world.GetName(entity) : null;
        }

        // =================================================================
        // Navigation — back/forward history
        // =================================================================

        private void NavigateTo(int entityIndex) {
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
            _currentPage = 0;

            // Cap history size
            if (_navHistory.Count > MaxNavHistory) {
                int excess = _navHistory.Count - MaxNavHistory;
                _navHistory.RemoveRange(0, excess);
                _navIndex -= excess;
            }
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
            _currentPage = 0;
        }

        private void GoForward() {
            if (!CanGoForward()) {
                return;
            }
            _navIndex++;
            _focusedEntity = _navHistory[_navIndex];
            _selectedEvent = -1;
            _currentPage = 0;
        }

        private void ClearNavigation() {
            _focusedEntity = null;
            _navHistory.Clear();
            _navIndex = -1;
        }
    }
}
#endif
