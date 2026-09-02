#if UNITY_EDITOR && KENSEI_DEBUG
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Editor window over a World with three tabs:
    ///   Entities — alive entities with their components, field values, nested structs
    ///              and lists, editable in play mode;
    ///   Filters  — every registered filter with its match count and memory;
    ///   Pools    — every component pool with its counts, capacities and memory.
    ///
    /// Open via menu: KenseiECS → World Inspector
    /// Auto-discovers any MonoBehaviour implementing IEcsWorldProvider.
    /// </summary>
    public class WorldInspectorWindow : EditorWindow {
        internal static World TargetWorld;

        private const int EntitiesPerPage = 100;
        private const float NumberWidth = 70f;
        private const float SizeWidth = 70f;
        private const float MemoryWidth = 90f;

        private static readonly string[] Tabs = { "Entities", "Filters", "Pools" };
        private static readonly Comparison<ComponentPoolBase> ByAllocatedBytesDescending =
            (a, b) => b.AllocatedBytes.CompareTo(a.AllocatedBytes);

        private static GUIStyle _numberStyle;

        private int _tab;
        private Vector2 _scrollPos;
        private Vector2 _filtersScrollPos;
        private Vector2 _poolsScrollPos;
        private string _searchFilter = "";
        private readonly HashSet<int> _expandedEntities = new();
        private readonly HashSet<string> _expandedSections = new();
        private readonly List<ComponentInfo> _componentBuffer = new();
        private readonly List<int> _typeBuffer = new();
        private readonly List<ComponentPoolBase> _poolBuffer = new();
        private int _currentPage;
        private double _lastRepaintTime;
        private double _lastAutoBindTime;

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

        [MenuItem("KenseiECS/World Inspector")]
        private static void Open() {
            var window = GetWindow<WorldInspectorWindow>("KenseiECS World");
            window.Show();
        }

        // Statics survive Enter Play Mode without domain reload, so the hook lives
        // outside any window instance: a world from a previous session must never
        // be inspected in the next one.
        [InitializeOnLoadMethod]
        private static void HookPlayModeChanges() {
            EditorApplication.playModeStateChanged += ClearTargetWorld;
        }

        private static void ClearTargetWorld(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                TargetWorld = null;
            }
        }

        private void OnEnable() {
            EditorApplication.update += ThrottledRepaint;
        }

        private void OnDisable() {
            EditorApplication.update -= ThrottledRepaint;
        }

        private void ThrottledRepaint() {
            if (!EditorApplication.isPlaying) {
                return;
            }

            double now = EditorApplication.timeSinceStartup;

            // Auto-bind at most once per second
            if (TargetWorld == null || !IsWorldValid(TargetWorld)) {
                TargetWorld = null;
                if (now - _lastAutoBindTime > 1.0) {
                    _lastAutoBindTime = now;
                    TryAutoBindWorld();
                }
            }

            // Repaint at most ~10 times per second
            if (now - _lastRepaintTime > 0.1) {
                _lastRepaintTime = now;
                Repaint();
            }
        }

        internal static bool IsWorldValid(World world) {
            return world != null && !world.IsDestroyed;
        }

        internal static void TryAutoBindWorld() {
#if UNITY_2023_1_OR_NEWER
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
#else
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
            foreach (var mb in behaviours) {
                if (mb is IEcsWorldProvider provider && provider.World != null) {
                    TargetWorld = provider.World;
                    return;
                }
            }
        }

        private void OnGUI() {
            if (!IsWorldValid(TargetWorld)) {
                TargetWorld = null;
                EditorGUILayout.HelpBox(
                    "No World found.\nAdd a MonoBehaviour implementing IEcsWorldProvider to your scene.",
                    MessageType.Info);
                return;
            }

            _tab = GUILayout.Toolbar(_tab, Tabs);

            switch (_tab) {
                case 0:
                    DrawEntitiesTab();
                    break;
                case 1:
                    DrawFiltersTab();
                    break;
                default:
                    DrawPoolsTab();
                    break;
            }
        }

        // =================================================================
        // Entities tab
        // =================================================================

        private void DrawEntitiesTab() {
            DrawToolbar();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawEntities();
            EditorGUILayout.EndScrollView();

            DrawPagination();
        }

        private void DrawToolbar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField($"Entities: {TargetWorld.EntityCount}", GUILayout.Width(100));

            int poolCount = 0;
            foreach (var pool in TargetWorld.ActivePools) {
                poolCount++;
            }

            EditorGUILayout.LabelField($"Pools: {poolCount}", GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            string newSearch = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(200));
            if (newSearch != _searchFilter) {
                _searchFilter = newSearch;
                _currentPage = 0;
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                _searchFilter = "";
                _currentPage = 0;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntities() {
            var world = TargetWorld;
            string filterLower = string.IsNullOrEmpty(_searchFilter)
                ? null
                : _searchFilter.ToLowerInvariant();

            int displayed = 0;
            int skipped = 0;
            int skipTarget = _currentPage * EntitiesPerPage;

            foreach (var entity in world.AliveEntities) {
                int i = entity.Index;
                var components = GetEntityComponents(world, entity);

                if (filterLower != null && !MatchesFilter(world, entity, components, filterLower)) {
                    continue;
                }

                if (skipped < skipTarget) {
                    skipped++;
                    continue;
                }

                if (displayed >= EntitiesPerPage) {
                    break;
                }

                DrawEntity(world, entity, i, components);
                displayed++;
            }

            if (displayed == 0) {
                EditorGUILayout.LabelField("No entities match the filter.",
                    EditorStyles.centeredGreyMiniLabel);
            }
        }

        private void DrawPagination() {
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

        private void DrawEntity(World world, Entity entity, int idx, List<ComponentInfo> components) {
            bool expanded = _expandedEntities.Contains(idx);
            string entityName = world.GetName(entity);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool newExpanded = EditorGUILayout.Foldout(expanded, "", true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    _expandedEntities.Add(idx);
                } else {
                    _expandedEntities.Remove(idx);
                }
            }

            EditorGUILayout.LabelField(
                entityName == null
                    ? $"{entity}  [{components.Count} components]"
                    : $"{entity}  \"{entityName}\"  [{components.Count} components]",
                EditorStyles.boldLabel);

            EditorGUILayout.EndHorizontal();

            if (newExpanded) {
                EditorGUI.indentLevel++;
                DrawNameField(world, entity, entityName);
                for (int c = 0; c < components.Count; c++) {
                    DrawComponent(world, components[c], idx);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawNameField(World world, Entity entity, string entityName) {
            string current = entityName ?? "";
            string edited = EditorGUILayout.DelayedTextField("Name", current);
            if (edited != current) {
                world.SetName(entity, edited.Length == 0 ? null : edited);
            }
        }

        // =================================================================
        // Component drawing with editing — delegates to shared ComponentDrawer
        // =================================================================

        private void DrawComponent(World world, ComponentInfo info, int entityIdx) {
            string key = $"{entityIdx}_{info.TypeName}";
            bool expanded = _expandedSections.Contains(key);

            bool newExpanded = EditorGUILayout.Foldout(expanded, info.TypeName, true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    _expandedSections.Add(key);
                } else {
                    _expandedSections.Remove(key);
                }
            }

            if (!newExpanded) {
                return;
            }

            var value = info.Pool.GetRaw(entityIdx);

            EditorGUI.indentLevel++;
            if (ComponentDrawer.DrawObject(value, info.Pool.ComponentType, key, _expandedSections)) {
                info.Pool.SetRaw(entityIdx, value);
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Filters tab
        // =================================================================

        private void DrawFiltersTab() {
            var world = TargetWorld;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField($"Filters: {world.FilterCount}", GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Filter", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("Count", NumberStyle, GUILayout.Width(NumberWidth));
            GUILayout.Label("Dense cap", NumberStyle, GUILayout.Width(NumberWidth));
            GUILayout.Label("Memory", NumberStyle, GUILayout.Width(MemoryWidth));
            EditorGUILayout.EndHorizontal();

            long totalBytes = 0;

            _filtersScrollPos = EditorGUILayout.BeginScrollView(_filtersScrollPos);
            for (int i = 0; i < world.FilterCount; i++) {
                var filter = world.GetFilter(i);
                long bytes = filter.AllocatedBytes;
                totalBytes += bytes;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(filter.ToString(), EditorStyles.label, GUILayout.ExpandWidth(true));
                GUILayout.Label(filter.Count.ToString(), NumberStyle, GUILayout.Width(NumberWidth));
                GUILayout.Label(filter.DenseCapacity.ToString(), NumberStyle, GUILayout.Width(NumberWidth));
                GUILayout.Label(FormatBytes(bytes), NumberStyle, GUILayout.Width(MemoryWidth));
                EditorGUILayout.EndHorizontal();
            }

            if (world.FilterCount == 0) {
                EditorGUILayout.LabelField("No filters registered.", EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndScrollView();

            DrawTotalLine($"Total: {FormatBytes(totalBytes)}");
        }

        // =================================================================
        // Pools tab
        // =================================================================

        private void DrawPoolsTab() {
            var world = TargetWorld;

            _poolBuffer.Clear();
            foreach (var pool in world.ActivePools) {
                _poolBuffer.Add(pool);
            }
            _poolBuffer.Sort(ByAllocatedBytesDescending);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField($"Pools: {_poolBuffer.Count}", GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Component", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("Count", NumberStyle, GUILayout.Width(NumberWidth));
            GUILayout.Label("Sparse cap", NumberStyle, GUILayout.Width(NumberWidth));
            GUILayout.Label("Dense cap", NumberStyle, GUILayout.Width(NumberWidth));
            GUILayout.Label("Size", NumberStyle, GUILayout.Width(SizeWidth));
            GUILayout.Label("Memory", NumberStyle, GUILayout.Width(MemoryWidth));
            EditorGUILayout.EndHorizontal();

            long poolBytes = 0;

            _poolsScrollPos = EditorGUILayout.BeginScrollView(_poolsScrollPos);
            for (int i = 0; i < _poolBuffer.Count; i++) {
                var pool = _poolBuffer[i];
                long bytes = pool.AllocatedBytes;
                poolBytes += bytes;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(pool.ComponentType.Name, EditorStyles.label, GUILayout.ExpandWidth(true));
                GUILayout.Label(pool.Count.ToString(), NumberStyle, GUILayout.Width(NumberWidth));
                GUILayout.Label(pool.SparseCapacity.ToString(), NumberStyle, GUILayout.Width(NumberWidth));
                GUILayout.Label(pool.DenseCapacity.ToString(), NumberStyle, GUILayout.Width(NumberWidth));
                GUILayout.Label(pool.ComponentSize == 0 ? "managed" : $"{pool.ComponentSize} B", NumberStyle, GUILayout.Width(SizeWidth));
                GUILayout.Label(FormatBytes(bytes), NumberStyle, GUILayout.Width(MemoryWidth));
                EditorGUILayout.EndHorizontal();
            }

            if (_poolBuffer.Count == 0) {
                EditorGUILayout.LabelField("No pools registered.", EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndScrollView();

            long filterBytes = 0;
            for (int i = 0; i < world.FilterCount; i++) {
                filterBytes += world.GetFilter(i).AllocatedBytes;
            }

            DrawTotalLine($"Pools {FormatBytes(poolBytes)} + filters {FormatBytes(filterBytes)} = {FormatBytes(poolBytes + filterBytes)}");
        }

        private static void DrawTotalLine(string text) {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Helpers
        // =================================================================

        private struct ComponentInfo {
            public string TypeName;
            public ComponentPoolBase Pool;
        }

        private List<ComponentInfo> GetEntityComponents(World world, Entity entity) {
            _componentBuffer.Clear();
            _typeBuffer.Clear();
            world.GetComponentTypes(entity, _typeBuffer);

            for (int i = 0; i < _typeBuffer.Count; i++) {
                int typeIndex = _typeBuffer[i];
                _componentBuffer.Add(new ComponentInfo {
                    TypeName = ComponentType.NameOf(typeIndex),
                    Pool = world.GetPool(typeIndex)
                });
            }

            return _componentBuffer;
        }

        private static bool MatchesFilter(World world, Entity entity, List<ComponentInfo> components, string filterLower) {
            if (entity.Index.ToString().Contains(filterLower)) {
                return true;
            }

            string entityName = world.GetName(entity);
            if (entityName != null && entityName.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            for (int i = 0; i < components.Count; i++) {
                if (components[i].TypeName.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
            }

            return false;
        }

        private static string FormatBytes(long bytes) {
            if (bytes >= 1L << 20) {
                return $"{bytes / (double)(1L << 20):F2} MB";
            }
            if (bytes >= 1L << 10) {
                return $"{bytes / (double)(1L << 10):F1} KB";
            }
            return $"{bytes} B";
        }
    }
}
#endif
