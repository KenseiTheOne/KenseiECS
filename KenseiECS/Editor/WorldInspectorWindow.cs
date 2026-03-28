#if UNITY_EDITOR && KENSEI_DEBUG
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Editor window that displays all alive entities in a World,
    /// their components, field values, nested structs, and lists.
    /// Supports editing values in play mode.
    ///
    /// Open via menu: KenseiECS → World Inspector
    /// Auto-discovers any MonoBehaviour implementing IEcsWorldProvider.
    /// </summary>
    public class WorldInspectorWindow : EditorWindow {
        internal static World TargetWorld;

        private const int EntitiesPerPage = 100;

        private Vector2 _scrollPos;
        private string _searchFilter = "";
        private readonly HashSet<int> _expandedEntities = new();
        private readonly HashSet<string> _expandedSections = new();
        private readonly List<ComponentInfo> _componentBuffer = new();
        private int _currentPage;
        private double _lastRepaintTime;
        private double _lastAutoBindTime;

        [MenuItem("KenseiECS/World Inspector")]
        private static void Open() {
            var window = GetWindow<WorldInspectorWindow>("KenseiECS World");
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

        private void OnPlayModeChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingPlayMode) {
                TargetWorld = null;
            }
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

        private static bool IsWorldValid(World world) {
            return world != null && !world.IsDestroyed;
        }

        private static void TryAutoBindWorld() {
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

            DrawToolbar();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawEntities();
            EditorGUILayout.EndScrollView();

            DrawPagination();
        }

        // =================================================================
        // Toolbar
        // =================================================================

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

        // =================================================================
        // Entity list with pagination
        // =================================================================

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
                var components = GetEntityComponents(world, i);

                if (filterLower != null && !MatchesFilter(entity, components, filterLower)) {
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
                $"{entity}  [{components.Count} components]",
                EditorStyles.boldLabel);

            EditorGUILayout.EndHorizontal();

            if (newExpanded) {
                EditorGUI.indentLevel++;
                for (int c = 0; c < components.Count; c++) {
                    DrawComponent(world, components[c], idx);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
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

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            object modified = ComponentDrawer.DrawObject(
                info.Value, info.Value.GetType(), key, _expandedSections);

            if (EditorGUI.EndChangeCheck() && modified != null) {
                info.Pool.SetRaw(entityIdx, modified);
            }

            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Helpers
        // =================================================================

        private struct ComponentInfo {
            public string TypeName;
            public object Value;
            public IComponentPool Pool;
        }

        private List<ComponentInfo> GetEntityComponents(World world, int entityIndex) {
            _componentBuffer.Clear();

            foreach (var pool in world.ActivePools) {
                if (!pool.Has(entityIndex)) {
                    continue;
                }

                var value = pool.GetRaw(entityIndex);

                _componentBuffer.Add(new ComponentInfo {
                    TypeName = value.GetType().Name,
                    Value = value,
                    Pool = pool
                });
            }

            return _componentBuffer;
        }

        private static bool MatchesFilter(Entity entity, List<ComponentInfo> components, string filterLower) {
            if (entity.Index.ToString().Contains(filterLower)) {
                return true;
            }

            for (int i = 0; i < components.Count; i++) {
                if (components[i].TypeName.IndexOf(filterLower, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
