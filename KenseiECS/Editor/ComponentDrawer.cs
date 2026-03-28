#if UNITY_EDITOR && KENSEI_DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KenseiECS.Editor {
    /// <summary>
    /// Shared utility for drawing ECS component fields in editor inspectors.
    /// Handles primitives, Unity types, enums, nested structs, lists, arrays, and Entity fields.
    /// </summary>
    internal static class ComponentDrawer {
        // Cached FieldInfo per type to avoid per-frame reflection
        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();

        // Cached GUIStyles — lazily initialized
        private static GUIStyle _entityAliveStyle;
        private static GUIStyle _entityDeadStyle;

        internal static GUIStyle EntityAliveStyle {
            get {
                if (_entityAliveStyle == null) {
                    _entityAliveStyle = new GUIStyle(EditorStyles.label) {
                        fontStyle = FontStyle.Bold
                    };
                    _entityAliveStyle.normal.textColor = new Color(0.3f, 0.6f, 1f);
                }
                return _entityAliveStyle;
            }
        }

        internal static GUIStyle EntityDeadStyle {
            get {
                if (_entityDeadStyle == null) {
                    _entityDeadStyle = new GUIStyle(EditorStyles.label) {
                        fontStyle = FontStyle.Italic
                    };
                    _entityDeadStyle.normal.textColor = Color.gray;
                }
                return _entityDeadStyle;
            }
        }

        internal static FieldInfo[] GetCachedFields(Type type) {
            if (!_fieldCache.TryGetValue(type, out var fields)) {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                _fieldCache[type] = fields;
            }
            return fields;
        }

        /// <summary>
        /// Draw all public fields of an object. Returns the (possibly modified) object.
        /// </summary>
        /// <param name="onEntityField">Optional callback for Entity-type fields (navigation support).</param>
        internal static object DrawObject(
            object obj,
            Type type,
            string pathPrefix,
            HashSet<string> expandedSections,
            Action<string, Entity> onEntityField = null
        ) {
            var fields = GetCachedFields(type);

            for (int f = 0; f < fields.Length; f++) {
                var field = fields[f];
                var value = field.GetValue(obj);
                string fieldKey = $"{pathPrefix}.{field.Name}";

                object newValue = DrawFieldValue(
                    field.Name, field.FieldType, value, fieldKey,
                    expandedSections, onEntityField);

                if (!AreEqual(newValue, value)) {
                    field.SetValue(obj, newValue);
                }
            }

            return obj;
        }

        internal static object DrawFieldValue(
            string name,
            Type type,
            object value,
            string key,
            HashSet<string> expandedSections,
            Action<string, Entity> onEntityField = null
        ) {
            // Entity — special: clickable navigation if callback provided
            if (type == typeof(Entity)) {
                if (onEntityField != null) {
                    onEntityField(name, (Entity)value);
                } else {
                    EditorGUILayout.LabelField(name, value.ToString());
                }
                return value;
            }

            // Null reference types
            if (value == null && !type.IsValueType) {
                EditorGUILayout.LabelField(name, "null");
                return value;
            }

            // Primitives
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

            // Unity types
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
            if (type == typeof(Rect)) {
                return EditorGUILayout.RectField(name, (Rect)value);
            }
            if (type == typeof(Bounds)) {
                return EditorGUILayout.BoundsField(name, (Bounds)value);
            }

            if (type == typeof(Quaternion)) {
                var q = (Quaternion)value;
                var euler = EditorGUILayout.Vector3Field(name + " (euler)", q.eulerAngles);
                return Quaternion.Euler(euler);
            }

            if (type == typeof(AnimationCurve)) {
                return EditorGUILayout.CurveField(name, (AnimationCurve)value ?? new AnimationCurve());
            }

            if (type.IsEnum) {
                return EditorGUILayout.EnumPopup(name, (Enum)value);
            }

            // Unity Object references
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) {
                return EditorGUILayout.ObjectField(name, (UnityEngine.Object)value, type, true);
            }

            // List<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) {
                DrawList(name, (IList)value, type, key, expandedSections, onEntityField);
                return value;
            }

            // Array
            if (type.IsArray) {
                DrawArray(name, (Array)value, type, key, expandedSections, onEntityField);
                return value;
            }

            // Nested struct
            if (type.IsValueType && !type.IsPrimitive) {
                bool expanded = expandedSections.Contains(key);
                bool newExpanded = EditorGUILayout.Foldout(expanded, name, true);
                if (newExpanded != expanded) {
                    if (newExpanded) {
                        expandedSections.Add(key);
                    } else {
                        expandedSections.Remove(key);
                    }
                }

                if (newExpanded) {
                    EditorGUI.indentLevel++;
                    value = DrawObject(value, type, key, expandedSections, onEntityField);
                    EditorGUI.indentLevel--;
                }

                return value;
            }

            // Fallback
            EditorGUILayout.LabelField(name, value?.ToString() ?? "null");
            return value;
        }

        private static void DrawList(
            string name, IList list, Type listType, string key,
            HashSet<string> expandedSections,
            Action<string, Entity> onEntityField
        ) {
            bool expanded = expandedSections.Contains(key);
            int count = list?.Count ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    expandedSections.Add(key);
                } else {
                    expandedSections.Remove(key);
                }
            }

            if (!newExpanded || list == null) {
                return;
            }

            var elementType = listType.GetGenericArguments()[0];

            EditorGUI.indentLevel++;
            for (int i = 0; i < list.Count; i++) {
                string itemKey = $"{key}[{i}]";
                object newValue = DrawFieldValue(
                    $"[{i}]", elementType, list[i], itemKey,
                    expandedSections, onEntityField);
                if (!AreEqual(newValue, list[i])) {
                    list[i] = newValue;
                }
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawArray(
            string name, Array array, Type arrayType, string key,
            HashSet<string> expandedSections,
            Action<string, Entity> onEntityField
        ) {
            bool expanded = expandedSections.Contains(key);
            int count = array?.Length ?? 0;

            bool newExpanded = EditorGUILayout.Foldout(expanded, $"{name}  [{count} items]", true);
            if (newExpanded != expanded) {
                if (newExpanded) {
                    expandedSections.Add(key);
                } else {
                    expandedSections.Remove(key);
                }
            }

            if (!newExpanded || array == null) {
                return;
            }

            var elementType = arrayType.GetElementType();

            EditorGUI.indentLevel++;
            for (int i = 0; i < array.Length; i++) {
                string itemKey = $"{key}[{i}]";
                object oldValue = array.GetValue(i);
                object newValue = DrawFieldValue(
                    $"[{i}]", elementType, oldValue, itemKey,
                    expandedSections, onEntityField);
                if (!AreEqual(newValue, oldValue)) {
                    array.SetValue(newValue, i);
                }
            }
            EditorGUI.indentLevel--;
        }

        private static bool AreEqual(object a, object b) {
            if (a == null && b == null) {
                return true;
            }
            if (a == null || b == null) {
                return false;
            }
            return a.Equals(b);
        }
    }
}
#endif
