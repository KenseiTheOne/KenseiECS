#if UNITY_EDITOR && KENSEI_DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
        public static World TargetWorld;

        private Vector2 _scrollPos;
        private string _searchFilter = "";
        private readonly HashSet<int> _expandedEntities = new();
        private readonly HashSet<string> _expandedSections = new();
        private readonly List<ComponentInfo> _componentBuffer = new();

        [MenuItem("KenseiECS/World Inspector")]
        private static void Open() {
            var window = GetWindow<WorldInspectorWindow>("KenseiECS World");
            window.Show();
        }

        private void OnEnable() {
            EditorApplication.update += RepaintIfPlaying;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable() {
            EditorApplication.update -= RepaintIfPlaying;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingPlayMode) {
                TargetWorld = null;
            }
        }

        private void RepaintIfPlaying() {
            if (!EditorApplication.isPlaying) {
                return;
            }

            if (TargetWorld == null) {
                TryAutoBindWorld();
            }

            Repaint();
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
            if (TargetWorld == null) {
                EditorGUILayout.HelpBox(
                    "No World found.\nAdd a MonoBehaviour implementing IEcsWorldProvider to your scene.",
                    MessageType.Info);
                return;
            }

            DrawToolbar();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawEntities();
            EditorGUILayout.EndScrollView();
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

            _searchFilter = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(200));

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) {
                _searchFilter = "";
            }

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Entity list
        // =================================================================

        private void DrawEntities() {
            var world = TargetWorld;

            foreach (var entity in world.AliveEntities) {
                int i = entity.Index;
                var components = GetEntityComponents(world, i);

                if (!string.IsNullOrEmpty(_searchFilter) && !MatchesFilter(entity, components)) {
                    continue;
                }

                DrawEntity(world, entity, i, components);
            }
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
        // Component drawing with editing
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

            object modified = DrawObject(info.Value, info.Value.GetType(), key);

            if (EditorGUI.EndChangeCheck() && modified != null) {
                info.Pool.SetRaw(entityIdx, modified);
            }

            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Recursive object drawing — handles primitives, nested structs, lists
        // =================================================================

        private object DrawObject(object obj, Type type, string pathPrefix) {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            bool changed = false;

            for (int f = 0; f < fields.Length; f++) {
                var field = fields[f];
                var value = field.GetValue(obj);
                string fieldKey = $"{pathPrefix}.{field.Name}";

                object newValue = DrawFieldValue(field.Name, field.FieldType, value, fieldKey);

                if (newValue != value) {
                    field.SetValue(obj, newValue);
                    changed = true;
                }
            }

            return changed ? obj : obj;
        }

        private object DrawFieldValue(string name, Type type, object value, string key) {
            if (value == null && !type.IsValueType) {
                EditorGUILayout.LabelField(name, "null");
                return value;
            }

            if (type == typeof(float)) {
                return EditorGUILayout.FloatField(name, (float)value);
            }
            if (type == typeof(int)) {
                return EditorGUILayout.IntField(name, (int)value);
            }
            if (type == typeof(bool)) {
                return EditorGUILayout.Toggle(name, (bool)value);
            }
            if (type == typeof(string)) {
                return EditorGUILayout.TextField(name, (string)value ?? "");
            }
            if (type == typeof(double)) {
                return EditorGUILayout.DoubleField(name, (double)value);
            }
            if (type == typeof(long)) {
                return EditorGUILayout.LongField(name, (long)value);
            }
            if (type == typeof(Vector2)) {
                return EditorGUILayout.Vector2Field(name, (Vector2)value);
            }
            if (type == typeof(Vector3)) {
                return EditorGUILayout.Vector3Field(name, (Vector3)value);
            }
            if (type == typeof(Vector4)) {
                return EditorGUILayout.Vector4Field(name, (Vector4)value);
            }
            if (type == typeof(Vector2Int)) {
                return EditorGUILayout.Vector2IntField(name, (Vector2Int)value);
            }
            if (type == typeof(Vector3Int)) {
                return EditorGUILayout.Vector3IntField(name, (Vector3Int)value);
            }
            if (type == typeof(Color)) {
                return EditorGUILayout.ColorField(name, (Color)value);
            }
            if (type == typeof(Quaternion)) {
                var q = (Quaternion)value;
                var euler = EditorGUILayout.Vector3Field(name + " (euler)", q.eulerAngles);
                return Quaternion.Euler(euler);
            }
            if (type == typeof(Rect)) {
                return EditorGUILayout.RectField(name, (Rect)value);
            }
            if (type == typeof(Bounds)) {
                return EditorGUILayout.BoundsField(name, (Bounds)value);
            }
            if (type == typeof(AnimationCurve)) {
                return EditorGUILayout.CurveField(name, (AnimationCurve)value ?? new AnimationCurve());
            }
            if (type.IsEnum) {
                return EditorGUILayout.EnumPopup(name, (Enum)value);
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) {
                return EditorGUILayout.ObjectField(name, (UnityEngine.Object)value, type, true);
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) {
                DrawList(name, (IList)value, type, key);
                return value;
            }
            if (type.IsArray) {
                DrawArray(name, (Array)value, type, key);
                return value;
            }

            if (type.IsValueType && !type.IsPrimitive) {
                bool expanded = _expandedSections.Contains(key);
                bool newExpanded = EditorGUILayout.Foldout(expanded, name, true);
                if (newExpanded != expanded) {
                    if (newExpanded) {
                        _expandedSections.Add(key);
                    } else {
                        _expandedSections.Remove(key);
                    }
                }

                if (newExpanded) {
                    EditorGUI.indentLevel++;
                    value = DrawObject(value, type, key);
                    EditorGUI.indentLevel--;
                }

                return value;
            }

            EditorGUILayout.LabelField(name, value?.ToString() ?? "null");
            return value;
        }

        // =================================================================
        // List<T> drawing
        // =================================================================

        private void DrawList(string name, IList list, Type listType, string key) {
            bool expanded = _expandedSections.Contains(key);
            int count = list?.Count ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    _expandedSections.Add(key);
                } else {
                    _expandedSections.Remove(key);
                }
            }

            if (!newExpanded || list == null) {
                return;
            }

            var elementType = listType.GetGenericArguments()[0];

            EditorGUI.indentLevel++;
            for (int i = 0; i < list.Count; i++) {
                string itemKey = $"{key}[{i}]";
                object newValue = DrawFieldValue($"[{i}]", elementType, list[i], itemKey);
                if (newValue != list[i]) {
                    list[i] = newValue;
                }
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Array drawing
        // =================================================================

        private void DrawArray(string name, Array array, Type arrayType, string key) {
            bool expanded = _expandedSections.Contains(key);
            int count = array?.Length ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    _expandedSections.Add(key);
                } else {
                    _expandedSections.Remove(key);
                }
            }

            if (!newExpanded || array == null) {
                return;
            }

            var elementType = arrayType.GetElementType();

            EditorGUI.indentLevel++;
            for (int i = 0; i < array.Length; i++) {
                string itemKey = $"{key}[{i}]";
                object newValue = DrawFieldValue($"[{i}]", elementType, array.GetValue(i), itemKey);
                if (newValue != array.GetValue(i)) {
                    array.SetValue(newValue, i);
                }
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

        private bool MatchesFilter(Entity entity, List<ComponentInfo> components) {
            var filter = _searchFilter.ToLowerInvariant();

            if (entity.ToString().ToLowerInvariant().Contains(filter)) {
                return true;
            }

            for (int i = 0; i < components.Count; i++) {
                if (components[i].TypeName.ToLowerInvariant().Contains(filter)) {
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
