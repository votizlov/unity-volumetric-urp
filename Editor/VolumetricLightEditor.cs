using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VolumetricLight))]
public sealed class VolumetricLightEditor : Editor
{
    SerializedProperty processVolumetricLighting;
    SerializedProperty bakedShaftLength;
    SerializedProperty bakedShaftColor;
    SerializedProperty bakedShaftMaterial;
    SerializedProperty bakedShaftMesh;
    SerializedProperty bakedShaftObject;

    void OnEnable()
    {
        processVolumetricLighting = serializedObject.FindProperty(nameof(VolumetricLight.processVolumetricLighting));
        bakedShaftLength = serializedObject.FindProperty(nameof(VolumetricLight.bakedShaftLength));
        bakedShaftColor = serializedObject.FindProperty(nameof(VolumetricLight.bakedShaftColor));
        bakedShaftMaterial = serializedObject.FindProperty(nameof(VolumetricLight.bakedShaftMaterial));
        bakedShaftMesh = serializedObject.FindProperty(nameof(VolumetricLight.bakedShaftMesh));
        bakedShaftObject = serializedObject.FindProperty(nameof(VolumetricLight.bakedShaftObject));
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(processVolumetricLighting);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Baked Light Shafts", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(bakedShaftLength, new GUIContent("Shaft Length"));
        EditorGUILayout.PropertyField(bakedShaftColor, new GUIContent("Shaft Color"));
        EditorGUILayout.PropertyField(bakedShaftMaterial, new GUIContent("Shaft Material"));

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.PropertyField(bakedShaftMesh, new GUIContent("Baked Mesh"));
            EditorGUILayout.PropertyField(bakedShaftObject, new GUIContent("Baked Object"));
        }

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(targets.Length != 1))
        {
            if (GUILayout.Button("Bake Light Shafts"))
                VolumetricLightShaftBaker.Bake((VolumetricLight)target);

            if (GUILayout.Button("Clear Baked Light Shafts"))
                VolumetricLightShaftBaker.Clear((VolumetricLight)target);
        }

        if (targets.Length != 1)
            EditorGUILayout.HelpBox("Baked shaft operations support one selected VolumetricLight at a time.", MessageType.Info);
    }
}
