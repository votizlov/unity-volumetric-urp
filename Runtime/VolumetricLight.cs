using UnityEngine;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public sealed class VolumetricLight : MonoBehaviour
{
    public bool processVolumetricLighting = true;

    [Header("Volumetric Cookie")]
    [Tooltip("Apply the independently assigned volumetric cookie to this light. Only spot lights support volumetric cookies.")]
    public bool applyVolumetricCookie;
    [Tooltip("RGB texture projected through the spot light cone. This does not use or modify Light.cookie.")]
    public Texture2D volumetricCookie;

    [Header("Baked Light Shafts")]
    [FormerlySerializedAs("bakedShaftDistance")]
    [Min(0.01f)] public float bakedShaftLength = 12f;
    public Color bakedShaftColor = new Color(1f, 0.95f, 0.75f, 1f);
    public Material bakedShaftMaterial;
    [HideInInspector] public Mesh bakedShaftMesh;
    [HideInInspector] public GameObject bakedShaftObject;

    public bool ShouldProcess => isActiveAndEnabled && processVolumetricLighting;

    void OnDrawGizmosSelected()
    {
        Mesh mesh = ResolveBakedShaftMesh();
        if (mesh == null)
            return;

        Transform shaftTransform = ResolveBakedShaftTransform();
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || triangles == null || vertices.Length == 0 || triangles.Length < 3)
            return;

        Color gizmoColor = bakedShaftColor;
        if (gizmoColor.a < 0.25f)
            gizmoColor = new Color(1f, 0.85f, 0.05f, 1f);
        else
            gizmoColor.a = 1f;

        Gizmos.color = gizmoColor;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int indexA = triangles[i];
            int indexB = triangles[i + 1];
            int indexC = triangles[i + 2];
            if (!IsValidVertexIndex(indexA, vertices.Length)
                || !IsValidVertexIndex(indexB, vertices.Length)
                || !IsValidVertexIndex(indexC, vertices.Length))
            {
                continue;
            }

            Vector3 a = shaftTransform.TransformPoint(vertices[indexA]);
            Vector3 b = shaftTransform.TransformPoint(vertices[indexB]);
            Vector3 c = shaftTransform.TransformPoint(vertices[indexC]);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, a);
        }

        float sphereRadius = Mathf.Max(0.025f, mesh.bounds.extents.magnitude * 0.003f);
        for (int i = 0; i < vertices.Length; i++)
            Gizmos.DrawSphere(shaftTransform.TransformPoint(vertices[i]), sphereRadius);
    }

    [ContextMenu("Log Baked Shaft Vertices")]
    void LogBakedShaftVertices()
    {
        Mesh mesh = ResolveBakedShaftMesh();
        if (mesh == null)
        {
            Debug.LogWarning("No baked shaft mesh is assigned.", this);
            return;
        }

        Transform shaftTransform = ResolveBakedShaftTransform();
        Vector3[] vertices = mesh.vertices;
        Debug.Log($"Baked shaft mesh '{mesh.name}' has {vertices.Length} vertices and bounds {mesh.bounds}.", this);
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 localPosition = vertices[i];
            Vector3 worldPosition = shaftTransform.TransformPoint(localPosition);
            Debug.Log($"Shaft vertex {i}: local {localPosition}, world {worldPosition}", this);
        }
    }

    Mesh ResolveBakedShaftMesh()
    {
        if (bakedShaftMesh != null)
            return bakedShaftMesh;

        if (bakedShaftObject != null && bakedShaftObject.TryGetComponent(out MeshFilter meshFilter))
            return meshFilter.sharedMesh;

        return null;
    }

    Transform ResolveBakedShaftTransform()
    {
        return bakedShaftObject != null ? bakedShaftObject.transform : transform;
    }

    static bool IsValidVertexIndex(int index, int vertexCount)
    {
        return index >= 0 && index < vertexCount;
    }
}
