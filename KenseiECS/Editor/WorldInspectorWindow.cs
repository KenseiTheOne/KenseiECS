#if UNITY_EDITOR && KENSEI_DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor
{
    /// <summary>
    /// Editor window that displays all alive entities in a World,
    /// their components, field values, nested structs, and lists.
    /// Supports editing values in play mode.
    /// 
    /// Open via menu: KenseiECS → World Inspector
    /// Set WorldInspectorWindow.TargetWorld from your bootstrap code.
    /// </summary>
    public class WorldInspectorWindow : EditorWindow
    {
        public static World TargetWorld;

        Vector2 _scrollPos;
        string _searchFilter = "";
        readonly HashSet<int> _expandedEntities = new();
        readonly HashSet<string> _expandedSections = new();

        [MenuItem("KenseiECS/World Inspector")]
        static void Open()
        {
            var window = GetWindow<WorldInspectorWindow>("KenseiECS World");
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.update += RepaintIfPlaying;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintIfPlaying;
        }

        void RepaintIfPlaying()
        {
            if (EditorApplication.isPlaying)
                Repaint();
        }

        void OnGUI()
        {
            if (TargetWorld == null)
            {
                EditorGUILayout.HelpBox(
                    "No World assigned.\nSet WorldInspectorWindow.TargetWorld from your code.",
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

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField($"Entities: {TargetWorld.EntityCount}", GUILayout.Width(100));

            int poolCount = 0;
            for (int i = 0; i < TargetWorld._pools.Length; i++)
                if (TargetWorld._pools[i] != null) poolCount++;

            EditorGUILayout.LabelField($"Pools: {poolCount}", GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            _searchFilter = EditorGUILayout.TextField(
                _searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(200));

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
                _searchFilter = "";

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // Entity list
        // =================================================================

        void DrawEntities()
        {
            var world = TargetWorld;

            for (int i = 0; i < world._nextIndex; i++)
            {
                if (!world._alive[i]) continue;

                var entity = new Entity(i, world._generations[i]);
                var components = GetEntityComponents(world, i);

                if (!string.IsNullOrEmpty(_searchFilter) && !MatchesFilter(entity, components))
                    continue;

                DrawEntity(world, entity, i, components);
            }
        }

        void DrawEntity(World world, Entity entity, int idx, List<ComponentInfo> components)
        {
            bool expanded = _expandedEntities.Contains(idx);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool newExpanded = EditorGUILayout.Foldout(expanded, "", true);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expandedEntities.Add(idx);
                else _expandedEntities.Remove(idx);
            }

            EditorGUILayout.LabelField(
                $"{entity}  [{components.Count} components]",
                EditorStyles.boldLabel);

            EditorGUILayout.EndHorizontal();

            if (newExpanded)
            {
                EditorGUI.indentLevel++;
                for (int c = 0; c < components.Count; c++)
                    DrawComponent(world, components[c], idx);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        // =================================================================
        // Component drawing with editing
        // =================================================================

        void DrawComponent(World world, ComponentInfo info, int entityIdx)
        {
            string key = $"{entityIdx}_{info.TypeName}";
            bool expanded = _expandedSections.Contains(key);

            bool newExpanded = EditorGUILayout.Foldout(expanded, info.TypeName, true);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expandedSections.Add(key);
                else _expandedSections.Remove(key);
            }

            if (!newExpanded) return;

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            object modified = DrawObject(info.Value, info.Value.GetType(), key);

            if (EditorGUI.EndChangeCheck() && modified != null)
            {
                // Write modified value back to pool
                info.Pool.SetRaw(entityIdx, modified);
            }

            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Recursive object drawing — handles primitives, nested structs, lists
        // =================================================================

        object DrawObject(object obj, Type type, string pathPrefix)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            bool changed = false;

            for (int f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                var value = field.GetValue(obj);
                string fieldKey = $"{pathPrefix}.{field.Name}";

                object newValue = DrawFieldValue(field.Name, field.FieldType, value, fieldKey);

                if (newValue != value)
                {
                    field.SetValue(obj, newValue);
                    changed = true;
                }
            }

            return changed ? obj : obj;
        }

        object DrawFieldValue(string name, Type type, object value, string key)
        {
            // Null
            if (value == null && !type.IsValueType)
            {
                EditorGUILayout.LabelField(name, "null");
                return value;
            }

            // Primitives and common Unity types
            if (type == typeof(float))
                return EditorGUILayout.FloatField(name, (float)value);

            if (type == typeof(int))
                return EditorGUILayout.IntField(name, (int)value);

            if (type == typeof(bool))
                return EditorGUILayout.Toggle(name, (bool)value);

            if (type == typeof(string))
                return EditorGUILayout.TextField(name, (string)value ?? "");

            if (type == typeof(double))
                return EditorGUILayout.DoubleField(name, (double)value);

            if (type == typeof(long))
                return EditorGUILayout.LongField(name, (long)value);

            if (type == typeof(Vector2))
                return EditorGUILayout.Vector2Field(name, (Vector2)value);

            if (type == typeof(Vector3))
                return EditorGUILayout.Vector3Field(name, (Vector3)value);

            if (type == typeof(Vector4))
                return EditorGUILayout.Vector4Field(name, (Vector4)value);

            if (type == typeof(Vector2Int))
                return EditorGUILayout.Vector2IntField(name, (Vector2Int)value);

            if (type == typeof(Vector3Int))
                return EditorGUILayout.Vector3IntField(name, (Vector3Int)value);

            if (type == typeof(Color))
                return EditorGUILayout.ColorField(name, (Color)value);

            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                var euler = EditorGUILayout.Vector3Field(name + " (euler)", q.eulerAngles);
                return Quaternion.Euler(euler);
            }

            if (type == typeof(Rect))
                return EditorGUILayout.RectField(name, (Rect)value);

            if (type == typeof(Bounds))
                return EditorGUILayout.BoundsField(name, (Bounds)value);

            if (type == typeof(AnimationCurve))
                return EditorGUILayout.CurveField(name, (AnimationCurve)value ?? new AnimationCurve());

            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(name, (Enum)value);

            // Unity Object references (GameObject, Component, ScriptableObject, etc.)
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return EditorGUILayout.ObjectField(name, (UnityEngine.Object)value, type, true);

            // List<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                DrawList(name, (IList)value, type, key);
                return value;
            }

            // Array T[]
            if (type.IsArray)
            {
                DrawArray(name, (Array)value, type, key);
                return value;
            }

            // Nested struct or class with public fields
            if (type.IsValueType && !type.IsPrimitive)
            {
                bool expanded = _expandedSections.Contains(key);
                bool newExpanded = EditorGUILayout.Foldout(expanded, name, true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _expandedSections.Add(key);
                    else _expandedSections.Remove(key);
                }

                if (newExpanded)
                {
                    EditorGUI.indentLevel++;
                    value = DrawObject(value, type, key);
                    EditorGUI.indentLevel--;
                }

                return value;
            }

            // Fallback
            EditorGUILayout.LabelField(name, value?.ToString() ?? "null");
            return value;
        }

        // =================================================================
        // List<T> drawing
        // =================================================================

        void DrawList(string name, IList list, Type listType, string key)
        {
            bool expanded = _expandedSections.Contains(key);
            int count = list?.Count ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expandedSections.Add(key);
                else _expandedSections.Remove(key);
            }

            if (!newExpanded || list == null) return;

            var elementType = listType.GetGenericArguments()[0];

            EditorGUI.indentLevel++;
            for (int i = 0; i < list.Count; i++)
            {
                string itemKey = $"{key}[{i}]";
                object newValue = DrawFieldValue($"[{i}]", elementType, list[i], itemKey);
                if (newValue != list[i])
                    list[i] = newValue;
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Array drawing
        // =================================================================

        void DrawArray(string name, Array array, Type arrayType, string key)
        {
            bool expanded = _expandedSections.Contains(key);
            int count = array?.Length ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expandedSections.Add(key);
                else _expandedSections.Remove(key);
            }

            if (!newExpanded || array == null) return;

            var elementType = arrayType.GetElementType();

            EditorGUI.indentLevel++;
            for (int i = 0; i < array.Length; i++)
            {
                string itemKey = $"{key}[{i}]";
                object newValue = DrawFieldValue($"[{i}]", elementType, array.GetValue(i), itemKey);
                if (newValue != array.GetValue(i))
                    array.SetValue(newValue, i);
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Helpers
        // =================================================================

        struct ComponentInfo
        {
            public string TypeName;
            public object Value;
            public IComponentPool Pool;
        }

        List<ComponentInfo> GetEntityComponents(World world, int entityIndex)
        {
            var result = new List<ComponentInfo>();

            for (int i = 0; i < world._pools.Length; i++)
            {
                var pool = world._pools[i];
                if (pool == null || !pool.Has(entityIndex)) continue;

                var value = pool.GetRaw(entityIndex);

                result.Add(new ComponentInfo
                {
                    TypeName = value.GetType().Name,
                    Value = value,
                    Pool = pool
                });
            }

            return result;
        }

        bool MatchesFilter(Entity entity, List<ComponentInfo> components)
        {
            var filter = _searchFilter.ToLowerInvariant();

            if (entity.ToString().ToLowerInvariant().Contains(filter))
                return true;

            for (int i = 0; i < components.Count; i++)
                if (components[i].TypeName.ToLowerInvariant().Contains(filter))
                    return true;

            return false;
        }
    }
}
#endif
