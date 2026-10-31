#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BreakEternity.EditorTools
{
    /// <summary>
    /// Inspector support for BigDouble fields: shows the value as text (e.g. "1.23e1000") and accepts every format
    /// that BigDouble.Parse understands. The displayed text is ToString(): the shortest text that parses back to exactly
    /// the stored components, so confirming it unchanged never alters the value. The stored components are only written when the text is
    /// changed. Invalid input is ignored with a warning.
    /// </summary>
    [CustomPropertyDrawer(typeof(BigDouble))]
    public class BigDoubleDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var signProp = property.FindPropertyRelative("_sign");
            var layerProp = property.FindPropertyRelative("_layer");
            var magProp = property.FindPropertyRelative("_mag");
            if (signProp == null || layerProp == null || magProp == null)
            {
                EditorGUI.LabelField(position, label.text, "BigDouble fields not found");
                return;
            }

            var current = BigDouble.fromComponents_noNormalize(signProp.doubleValue, layerProp.doubleValue, magProp.doubleValue);

            EditorGUI.BeginProperty(position, label, property);
            bool mixed = signProp.hasMultipleDifferentValues || layerProp.hasMultipleDifferentValues || magProp.hasMultipleDifferentValues;
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            string text = EditorGUI.DelayedTextField(position, label, mixed ? "" : current.ToString());
            if (EditorGUI.EndChangeCheck())
            {
                if (BigDouble.TryParse(text, out var parsed))
                {
                    signProp.doubleValue = parsed.sign;
                    layerProp.doubleValue = parsed.layer;
                    magProp.doubleValue = parsed.mag;
                }
                else
                {
                    Debug.LogWarning("'" + text + "' is not a valid BigDouble; the value was not changed.");
                }
            }
            EditorGUI.showMixedValue = false;
            EditorGUI.EndProperty();
        }
    }

    /// <summary>
    /// Inspector support for SerializableBigDouble fields: the same text field as for BigDouble. A valid input is
    /// stored as its lossless save text (field "value", unchanged JSON format); invalid input is ignored with a warning
    /// and never overwrites the stored value. Stored text that is not a valid BigDouble is shown as it is.
    /// </summary>
    [CustomPropertyDrawer(typeof(SerializableBigDouble))]
    public class SerializableBigDoubleDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var valueProp = property.FindPropertyRelative("value");
            if (valueProp == null)
            {
                EditorGUI.LabelField(position, label.text, "SerializableBigDouble field not found");
                return;
            }

            string stored = valueProp.stringValue;
            string shown;
            if (string.IsNullOrEmpty(stored))
            {
                shown = "0";
            }
            else
            {
                shown = BigDouble.TryParse(stored, out var number) ? number.ToString() : stored;
            }

            EditorGUI.BeginProperty(position, label, property);
            bool mixed = valueProp.hasMultipleDifferentValues;
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            string text = EditorGUI.DelayedTextField(position, label, mixed ? "" : shown);
            if (EditorGUI.EndChangeCheck())
            {
                if (BigDouble.TryParse(text, out var parsed))
                {
                    valueProp.stringValue = parsed.ToSaveString();
                }
                else
                {
                    Debug.LogWarning("'" + text + "' is not a valid BigDouble; the value was not changed.");
                }
            }
            EditorGUI.showMixedValue = false;
            EditorGUI.EndProperty();
        }
    }
}
#endif
