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
    /// Custom inspector for EcsEntityView.
    /// Displays entity components with editable fields, nested structs, lists.
    /// Entity fields are clickable — navigates to that entity with Back/Forward history.
    /// </summary>
    [CustomEditor(typeof(EcsEntityView))]
    public class EcsEntityViewInspector : UnityEditor.Editor
    {
        // Navigation history
        readonly List<Entity> _history = new();
        int _historyIndex = -1;

        // Current entity being inspected (may differ from view's entity during navigation)
        Entity _currentEntity;
        bool _navigating;

        // Foldout state
        readonly HashSet<string> _expandedSections = new();

        void OnEnable()
        {
            var view = (EcsEntityView)target;
            if (view.IsAlive)
            {
                _currentEntity = view.Entity;
                _history.Clear();
                _history.Add(_currentEntity);
                _historyIndex = 0;
                _navigating = false;
            }
        }

        public override void OnInspectorGUI()
        {
            var view = (EcsEntityView)target;

            if (view.World == null)
            {
                EditorGUILayout.HelpBox("No World bound.", MessageType.Warning);
                return;
            }

            // Sync with view if not navigating
            if (!_navigating)
            {
                _currentEntity = view.Entity;
                if (_history.Count == 0 || _history[_historyIndex] != _currentEntity)
                {
                    _history.Clear();
                    _history.Add(_currentEntity);
                    _historyIndex = 0;
                }
            }

            DrawNavigationBar(view.World);
            DrawEntityHeader(view.World);
            DrawComponents(view.World);

            // Repaint during play for live updates
            if (Application.isPlaying)
                Repaint();
        }

        // =================================================================
        // Navigation bar
        // =================================================================

        void DrawNavigationBar(World world)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Back button
            EditorGUI.BeginDisabledGroup(_historyIndex <= 0);
            if (GUILayout.Button("◄ Back", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                _historyIndex--;
                _currentEntity = _history[_historyIndex];
                _navigating = true;
            }
            EditorGUI.EndDisabledGroup();

            // Forward button
            EditorGUI.BeginDisabledGroup(_historyIndex >= _history.Count - 1);
            if (GUILayout.Button("Forward ►", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                _historyIndex++;
                _currentEntity = _history[_historyIndex];
                _navigating = true;
            }
            EditorGUI.EndDisabledGroup();

            // Home button — return to original entity
            var view = (EcsEntityView)target;
            if (_navigating)
            {
                if (GUILayout.Button("Home", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
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

        void DrawEntityHeader(World world)
        {
            bool alive = world.IsAlive(_currentEntity);

            if (!alive)
            {
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

        void DrawComponents(World world)
        {
            if (!world.IsAlive(_currentEntity))
            {
                return;
            }

            int entityIdx = _currentEntity.Index;

            foreach (var pool in world.ActivePools)
            {
                if (!pool.Has(entityIdx))
                {
                    continue;
                }

                var value = pool.GetRaw(entityIdx);
                var typeName = value.GetType().Name;
                string key = $"{entityIdx}_{typeName}";

                bool expanded = _expandedSections.Contains(key);
                bool newExpanded = EditorGUILayout.Foldout(expanded, typeName, true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _expandedSections.Add(key);
                    else _expandedSections.Remove(key);
                }

                if (!newExpanded)
                {
                    continue;
                }

                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();

                object modified = DrawObject(value, value.GetType(), key, world);

                if (EditorGUI.EndChangeCheck() && modified != null)
                {
                    pool.SetRaw(entityIdx, modified);
                }

                EditorGUI.indentLevel--;
            }
        }

        // =================================================================
        // Recursive object drawing with Entity navigation
        // =================================================================

        object DrawObject(object obj, Type type, string pathPrefix, World world)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);

            for (int f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                var value = field.GetValue(obj);
                string fieldKey = $"{pathPrefix}.{field.Name}";

                object newValue = DrawFieldValue(field.Name, field.FieldType, value, fieldKey, world);

                if (newValue != value)
                    field.SetValue(obj, newValue);
            }

            return obj;
        }

        object DrawFieldValue(string name, Type type, object value, string key, World world)
        {
            // Entity — special: clickable navigation
            if (type == typeof(Entity))
            {
                DrawEntityField(name, (Entity)value, world);
                return value;
            }

            // Null reference types
            if (value == null && !type.IsValueType)
            {
                EditorGUILayout.LabelField(name, "null");
                return value;
            }

            // Primitives
            if (type == typeof(float))   return EditorGUILayout.FloatField(name, (float)value);
            if (type == typeof(int))     return EditorGUILayout.IntField(name, (int)value);
            if (type == typeof(bool))    return EditorGUILayout.Toggle(name, (bool)value);
            if (type == typeof(string))  return EditorGUILayout.TextField(name, (string)value ?? "");
            if (type == typeof(double))  return EditorGUILayout.DoubleField(name, (double)value);
            if (type == typeof(long))    return EditorGUILayout.LongField(name, (long)value);

            // Unity types
            if (type == typeof(Vector2))    return EditorGUILayout.Vector2Field(name, (Vector2)value);
            if (type == typeof(Vector3))    return EditorGUILayout.Vector3Field(name, (Vector3)value);
            if (type == typeof(Vector4))    return EditorGUILayout.Vector4Field(name, (Vector4)value);
            if (type == typeof(Vector2Int)) return EditorGUILayout.Vector2IntField(name, (Vector2Int)value);
            if (type == typeof(Vector3Int)) return EditorGUILayout.Vector3IntField(name, (Vector3Int)value);
            if (type == typeof(Color))      return EditorGUILayout.ColorField(name, (Color)value);
            if (type == typeof(Rect))       return EditorGUILayout.RectField(name, (Rect)value);
            if (type == typeof(Bounds))     return EditorGUILayout.BoundsField(name, (Bounds)value);

            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                var euler = EditorGUILayout.Vector3Field(name + " (euler)", q.eulerAngles);
                return Quaternion.Euler(euler);
            }

            if (type == typeof(AnimationCurve))
                return EditorGUILayout.CurveField(name, (AnimationCurve)value ?? new AnimationCurve());

            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(name, (Enum)value);

            // Unity Object references
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return EditorGUILayout.ObjectField(name, (UnityEngine.Object)value, type, true);

            // List<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                DrawList(name, (IList)value, type, key, world);
                return value;
            }

            // Array
            if (type.IsArray)
            {
                DrawArray(name, (Array)value, type, key, world);
                return value;
            }

            // Nested struct
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
                    value = DrawObject(value, type, key, world);
                    EditorGUI.indentLevel--;
                }

                return value;
            }

            // Fallback
            EditorGUILayout.LabelField(name, value?.ToString() ?? "null");
            return value;
        }

        // =================================================================
        // Entity field — clickable navigation
        // =================================================================

        void DrawEntityField(string name, Entity entity, World world)
        {
            EditorGUILayout.BeginHorizontal();

            bool alive = world.IsAlive(entity);
            string label = alive
                ? $"{entity}"
                : $"{entity} [Not Alive]";

            var style = new GUIStyle(EditorStyles.label);
            if (alive)
            {
                style.normal.textColor = new Color(0.3f, 0.6f, 1f);
                style.fontStyle = FontStyle.Bold;
            }
            else
            {
                style.normal.textColor = Color.gray;
                style.fontStyle = FontStyle.Italic;
            }

            EditorGUILayout.LabelField(name, GUILayout.Width(EditorGUIUtility.labelWidth));

            if (alive)
            {
                if (GUILayout.Button(label, style))
                    NavigateTo(entity);
            }
            else
            {
                EditorGUILayout.LabelField(label, style);
            }

            EditorGUILayout.EndHorizontal();
        }

        // =================================================================
        // List<T> drawing
        // =================================================================

        void DrawList(string name, IList list, Type listType, string key, World world)
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
                object newValue = DrawFieldValue($"[{i}]", elementType, list[i], itemKey, world);
                if (newValue != list[i])
                    list[i] = newValue;
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Array drawing
        // =================================================================

        void DrawArray(string name, Array array, Type arrayType, string key, World world)
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
                object newValue = DrawFieldValue($"[{i}]", elementType, array.GetValue(i), itemKey, world);
                if (newValue != array.GetValue(i))
                    array.SetValue(newValue, i);
            }
            EditorGUI.indentLevel--;
        }

        // =================================================================
        // Navigation
        // =================================================================

        void NavigateTo(Entity entity)
        {
            // Trim forward history if we're not at the end
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

            _history.Add(entity);
            _historyIndex = _history.Count - 1;
            _currentEntity = entity;
            _navigating = true;
        }
    }
}
#endif
