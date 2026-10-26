#if UNITY_EDITOR && KENSEI_DEBUG
using System;
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

        // A snapshot copies the whole ring buffer (up to MaxEvents structs), so it is
        // refreshed at most this often while recording instead of on every OnGUI.
        private const double SnapshotInterval = 0.5;

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
        private bool _hasNextPage;

        private IReadOnlyList<ProfileEvent> _events;
        private bool _snapshotWhileEnabled;
        private bool _snapshotStale;
        private double _lastSnapshotTime;
        private double _lastRepaintTime;
        private double _lastAutoBindTime;

        // Generation of the entity each event belongs to, parallel to _events (see ResolveGenerations).
        private int[] _eventGenerations = Array.Empty<int>();
        private readonly Dictionary<int, int> _slotGenerations = new();

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
            // The profiler drops the previous session's events when play mode starts;
            // retake the snapshot so they do not linger in the window.
            if (state == PlayModeStateChange.EnteredPlayMode) {
                _events = null;
                return;
            }

            if (state != PlayModeStateChange.ExitingEditMode) {
                return;
            }

            _events = null;
            _selectedEvent = -1;
            _currentPage = 0;
            ClearNavigation();
        }

        private void ThrottledRepaint() {
            double now = EditorApplication.timeSinceStartup;

            // While stopped the buffer changes only through Enable and Clear, so one
            // snapshot taken after recording stops stays valid.
            bool enabled = EcsProfiler.IsEnabled;
            if (_events == null || _snapshotStale || enabled != _snapshotWhileEnabled
                || (enabled && now - _lastSnapshotTime > SnapshotInterval)) {
                RefreshSnapshot();
                Repaint();
            }

            if (!EditorApplication.isPlaying) {
                return;
            }

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
            // Past the first frame the snapshot is retaken only in ThrottledRepaint, outside
            // OnGUI, so Layout and Repaint of one frame see the same events.
            if (_events == null) {
                RefreshSnapshot();
            }

            if (!EcsProfiler.IsEnabled) {
                if (_events.Count == 0) {
                    EditorGUILayout.HelpBox(
                        "Profiler is not enabled.\nCall EcsProfiler.Enable(world) from your code.",
                        MessageType.Info);
                    return;
                }

                // World.Destroy disables the profiler but keeps its events: show what was recorded.
                EditorGUILayout.HelpBox(
                    "Recording stopped (the world was destroyed or EcsProfiler.Disable was called).\nShowing the events recorded before that.",
                    MessageType.Info);
            }

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
            DrawFilterButton(new GUIContent("Grow", "Show only storage that grew past World.Reserve"), ProfileEventType.StorageGrown);

            GUILayout.FlexibleSpace();

            string newSearch = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(120));
            if (newSearch != _searchFilter) {
                _searchFilter = newSearch;
                _currentPage = 0;
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                _searchFilter = "";
                EcsProfiler.Clear();
                _snapshotStale = true;
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

            // Current focus: an entity index, so its history spans every entity that used the slot.
            if (_focusedEntity.HasValue) {
                string entityName = DebugName(_focusedEntity.Value, SlotOccupant);
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
            _hasNextPage = false;

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
                    if (!evt.MatchesSearch(filterLower)) {
                        continue;
                    }
                }

                if (skipped < skipTarget) {
                    skipped++;
                    continue;
                }

                // One more match past a full page means a next page exists.
                if (displayed >= EventsPerPage) {
                    _hasNextPage = true;
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

            EditorGUI.BeginDisabledGroup(!_hasNextPage);
            if (GUILayout.Button("Next >", EditorStyles.toolbarButton, GUILayout.Width(60))) {
                _currentPage++;
            }
            EditorGUI.EndDisabledGroup();

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

            // Clickable entity link; storage events belong to no entity.
            if (evt.EntityIndex < 0) {
                EditorGUILayout.LabelField("-", GUILayout.Width(160));
            } else if (GUILayout.Button(EntityLabel(evt.EntityIndex, _eventGenerations[index]), EntityLinkStyle, GUILayout.Width(160))) {
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

        private void RefreshSnapshot() {
            var previous = _events;
            _events = EcsProfiler.GetEvents();
            _snapshotWhileEnabled = EcsProfiler.IsEnabled;
            _lastSnapshotTime = EditorApplication.timeSinceStartup;
            _snapshotStale = false;

            ResolveGenerations();
            KeepSelection(previous);

            // Recording restarted or was cleared from code, or MaxEvents shrank: stay on a page
            // that exists. The filters only narrow the list, so this bound holds with them too.
            int lastPage = Math.Max(0, (_events.Count - 1) / EventsPerPage);
            if (_currentPage > lastPage) {
                _currentPage = lastPage;
            }
        }

        // The buffer is a ring: once it is full, every new event drops the oldest one and moves the
        // rest down, so the index of the selected row names another event in the next snapshot.
        // Find the selected event again (it never moves up), or drop the selection when it has left
        // the buffer.
        private void KeepSelection(IReadOnlyList<ProfileEvent> previous) {
            if (_selectedEvent < 0) {
                return;
            }
            if (previous == null || _selectedEvent >= previous.Count) {
                _selectedEvent = -1;
                return;
            }

            var selected = previous[_selectedEvent];
            for (int i = Math.Min(_selectedEvent, _events.Count - 1); i >= 0; i--) {
                if (SameEvent(_events[i], selected)) {
                    _selectedEvent = i;
                    return;
                }
            }
            _selectedEvent = -1;
        }

        private static bool SameEvent(ProfileEvent a, ProfileEvent b) {
            return a.TimestampMs == b.TimestampMs
                && a.Type == b.Type
                && a.EntityIndex == b.EntityIndex
                && a.Generation == b.Generation
                && a.Tick == b.Tick
                && ReferenceEquals(a.ComponentType, b.ComponentType)
                && ReferenceEquals(a.CallStack, b.CallStack);
        }

        // Component events carry no generation (ProfileEvent.Generation is set for Created and
        // Destroyed only). An event belongs to the entity of the slot's last Created or Destroyed
        // event before it; before the slot's first one in the buffer, to the entity a later
        // Destroyed ends. When the next one is a Created, the event belongs to an earlier entity of
        // the slot (EarlierEntity); without any, to the entity in the slot now (SlotOccupant).
        private void ResolveGenerations() {
            int count = _events.Count;
            if (_eventGenerations.Length < count) {
                // Sized for a full buffer, so a filling buffer does not reallocate on every snapshot.
                _eventGenerations = new int[Math.Max(count, EcsProfiler.MaxEvents)];
            }

            _slotGenerations.Clear();
            for (int i = 0; i < count; i++) {
                var evt = _events[i];
                int generation = evt.Generation;
                if (evt.EntityIndex >= 0) {
                    if (generation > 0) {
                        _slotGenerations[evt.EntityIndex] = generation;
                    } else if (_slotGenerations.TryGetValue(evt.EntityIndex, out int last)) {
                        generation = last;
                    }
                }
                _eventGenerations[i] = generation;
            }

            _slotGenerations.Clear();
            for (int i = count - 1; i >= 0; i--) {
                var evt = _events[i];
                if (evt.EntityIndex < 0) {
                    continue;
                }
                if (evt.Generation > 0) {
                    _slotGenerations[evt.EntityIndex] = evt.Type == ProfileEventType.Destroyed
                        ? evt.Generation
                        : EarlierEntity;
                } else if (_eventGenerations[i] == SlotOccupant
                           && _slotGenerations.TryGetValue(evt.EntityIndex, out int next)) {
                    _eventGenerations[i] = next;
                }
            }
        }

        private static Color GetEventColor(ProfileEventType type) {
            return type switch {
                ProfileEventType.Created          => new Color(0.6f, 0.9f, 0.6f),
                ProfileEventType.Destroyed        => new Color(0.9f, 0.6f, 0.6f),
                ProfileEventType.ComponentAdded   => new Color(0.6f, 0.8f, 0.9f),
                ProfileEventType.ComponentRemoved => new Color(0.9f, 0.8f, 0.6f),
                ProfileEventType.StorageGrown     => new Color(1.0f, 0.55f, 0.35f),
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
                evt.EntityIndex < 0
                    ? $"Tick {evt.Tick} - {evt.Type} - {evt.ComponentType} at {evt.TimestampMs:F1}ms"
                    : $"Tick {evt.Tick} - {evt.Type} - {EntityLabel(evt.EntityIndex, _eventGenerations[_selectedEvent])} at {evt.TimestampMs:F1}ms",
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

        // Generation arguments other than a real generation (which starts at 1): the event does not
        // say which entity of the slot it was.
        // SlotOccupant: no later entity used the slot (an entity from before the recording).
        private const int SlotOccupant = 0;
        // EarlierEntity: a later entity used the slot, so the one there now is not it.
        private const int EarlierEntity = -1;

        // The generation is shown only when known. The debug name is that of the entity in the slot
        // now, so it is shown only when that is the event's entity: a reused slot holds another one.
        private static string EntityLabel(int entityIndex, int generation) {
            string label = generation > 0 ? $"E({entityIndex}v{generation})" : $"E({entityIndex})";
            string entityName = DebugName(entityIndex, generation);
            return entityName == null ? label : $"{label} \"{entityName}\"";
        }

        private static string DebugName(int entityIndex, int generation) {
            var world = WorldInspectorWindow.TargetWorld;
            if (generation == EarlierEntity || !WorldInspectorWindow.IsWorldValid(world)
                || !world.TryGetEntity(entityIndex, out var entity)) {
                return null;
            }
            return generation == SlotOccupant || entity.Generation == generation ? world.GetName(entity) : null;
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
