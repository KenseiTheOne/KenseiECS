#if UNITY_EDITOR && KENSEI_DEBUG
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Custom inspector for EcsEntityView.
    /// Displays entity components with editable fields, nested structs, lists.
    /// Entity fields are clickable — navigates to that entity with Back/Forward history.
    /// </summary>
    [CustomEditor(typeof(EcsEntityView))]
    public class EcsEntityViewInspector : UnityEditor.Editor {
        private const int MaxHistorySize = 50;

        // Navigation history
        private readonly List<Entity> _history = new();
        private int _historyIndex = -1;

        // Current entity being inspected (may differ from view's entity during navigation)
        private Entity _currentEntity;
        private bool _navigating;

        // Foldout state
        private readonly HashSet<string> _expandedSections = new();

        private void OnEnable() {
            var view = (EcsEntityView)target;
            if (view.IsAlive) {
                _currentEntity = view.Entity;
                _history.Clear();
                _history.Add(_currentEntity);
                _historyIndex = 0;
                _navigating = false;
            }
        }

        public override void OnInspectorGUI() {
            var view = (EcsEntityView)target;

            if (view.World == null) {
                EditorGUILayout.HelpBox("No World bound.", MessageType.Warning);
                return;
            }

            // Sync with view if not navigating
            if (!_navigating) {
                _currentEntity = view.Entity;
                if (_history.Count == 0 || _history[_historyIndex] != _currentEntity) {
                    _history.Clear();
                    _history.Add(_currentEntity);
                    _historyIndex = 0;
                }
            }

            DrawNavigationBar(view.World);
            DrawEntityHeader(view.World);
            DrawComponents(view.World);

            // Repaint during play for live updates
            if (Application.isPlaying) {
                Repaint();
            }
        }

        // =================================================================
        // Navigation bar
        // =================================================================

        private void DrawNavigationBar(World world) {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Back button
            EditorGUI.BeginDisabledGroup(_historyIndex <= 0);
            if (GUILayout.Button("< Back", EditorStyles.toolbarButton, GUILayout.Width(60))) {
                _historyIndex--;
                _currentEntity = _history[_historyIndex];
                _navigating = true;
            }
            EditorGUI.EndDisabledGroup();

            // Forward button
            EditorGUI.BeginDisabledGroup(_historyIndex >= _history.Count - 1);
            if (GUILayout.Button("Forward >", EditorStyles.toolbarButton, GUILayout.Width(70))) {
                _historyIndex++;
                _currentEntity = _history[_historyIndex];
                _navigating = true;
            }
            EditorGUI.EndDisabledGroup();

            // Home button — return to original entity
            var view = (EcsEntityView)target;
            if (_navigating) {
                if (GUILayout.Button("Home", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                    NavigateTo(view.Entity);
                    _navigating = false;
                }
            }

            GUILayout.FlexibleSpace();

            // Current entity label
            EditorGUILayout.LabelField(_currentEntity.ToString(), EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Entity header
        // =================================================================

        private void DrawEntityHeader(World world) {
            if (!world.IsAlive(_currentEntity)) {
                EditorGUILayout.HelpBox(
                    $"{_currentEntity} is not alive.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(
                $"{_currentEntity}",
                EditorStyles.boldLabel);
        }

        // =================================================================
        // Component list
        // =================================================================

        private void DrawComponents(World world) {
            if (!world.IsAlive(_currentEntity)) {
                return;
            }

            int entityIdx = _currentEntity.Index;

            foreach (var pool in world.ActivePools) {
                if (!pool.Has(entityIdx)) {
                    continue;
                }

                var value = pool.GetRaw(entityIdx);
                var typeName = value.GetType().Name;
                string key = $"{entityIdx}_{typeName}";

                bool expanded = _expandedSections.Contains(key);
                bool newExpanded = EditorGUILayout.Foldout(expanded, typeName, true);
                if (newExpanded != expanded) {
                    if (newExpanded) {
                        _expandedSections.Add(key);
                    } else {
                        _expandedSections.Remove(key);
                    }
                }

                if (!newExpanded) {
                    continue;
                }

                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();

                object modified = ComponentDrawer.DrawObject(
                    value, value.GetType(), key, _expandedSections,
                    (name, entity) => DrawEntityField(name, entity, world));

                if (EditorGUI.EndChangeCheck() && modified != null) {
                    pool.SetRaw(entityIdx, modified);
                }

                EditorGUI.indentLevel--;
            }
        }

        // =================================================================
        // Entity field — clickable navigation
        // =================================================================

        private void DrawEntityField(string name, Entity entity, World world) {
            EditorGUILayout.BeginHorizontal();

            bool alive = world.IsAlive(entity);
            string label = alive
                ? $"{entity}"
                : $"{entity} [Not Alive]";

            var style = alive
                ? ComponentDrawer.EntityAliveStyle
                : ComponentDrawer.EntityDeadStyle;

            EditorGUILayout.LabelField(name, GUILayout.Width(EditorGUIUtility.labelWidth));

            if (alive) {
                if (GUILayout.Button(label, style)) {
                    NavigateTo(entity);
                }
            } else {
                EditorGUILayout.LabelField(label, style);
            }

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Navigation
        // =================================================================

        private void NavigateTo(Entity entity) {
            // Trim forward history if we're not at the end
            if (_historyIndex < _history.Count - 1) {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            _history.Add(entity);
            _historyIndex = _history.Count - 1;
            _currentEntity = entity;
            _navigating = true;

            // Cap history size
            if (_history.Count > MaxHistorySize) {
                int excess = _history.Count - MaxHistorySize;
                _history.RemoveRange(0, excess);
                _historyIndex -= excess;
            }
        }
    }
}
#endif
