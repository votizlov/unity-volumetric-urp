using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class VolumetricLightShaftBaker
{
    const string ShaftObjectName = "Baked Light Shafts";
    const string GeneratedRoot = "Assets/Generated/VolumetricLightShafts";
    const string DefaultMaterialPath = GeneratedRoot + "/BakedLightShaftsDefault.mat";
    const string ShaderName = "VolumetricLighting/BakedLightShafts";

    public static void Bake(VolumetricLight volumetricLight)
    {
        if (volumetricLight == null)
            return;

        Light light = volumetricLight.GetComponent<Light>();
        if (light == null)
        {
            Debug.LogWarning("VolumetricLight requires a Light component.", volumetricLight);
            return;
        }

        if (light.type != LightType.Point && light.type != LightType.Spot)
        {
            Debug.LogWarning("Baked light shafts currently support Point and Spot lights only.", volumetricLight);
            return;
        }

        float shaftDistance = Mathf.Max(0.01f, volumetricLight.bakedShaftLength);
        Matrix4x4 shaftLocalToWorld = Matrix4x4.TRS(light.transform.position, light.transform.rotation, Vector3.one);
        Matrix4x4 shaftWorldToLocal = shaftLocalToWorld.inverse;
        List<MeshFilter> meshFilters = CollectInfluencedMeshes(light);
        var shaftLight = LightShaftMeshBuilder.ShaftLight.From(light);
        var quads = new NativeList<LightShaftMeshBuilder.ShaftQuad>(Allocator.TempJob);
        Mesh mesh = new();
        int quadCount;

        try
        {
            for (int i = 0; i < meshFilters.Count; i++)
                AppendMeshShafts(meshFilters[i], shaftLight, shaftDistance, shaftWorldToLocal, quads);

            LightShaftMeshBuilder.WriteMesh(quads.AsArray(), mesh);
            quadCount = quads.Length;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            UnityEngine.Object.DestroyImmediate(mesh);
            return;
        }
        finally
        {
            quads.Dispose();
        }

        mesh.name = $"{light.gameObject.name}_BakedLightShafts";

        string meshPath = SaveOrOverwriteMesh(volumetricLight, mesh);
        Mesh assetMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (assetMesh != null)
            mesh = assetMesh;

        Undo.RecordObject(volumetricLight, "Bake Light Shafts");
        volumetricLight.bakedShaftMesh = mesh;
        GameObject shaftObject = EnsureShaftObject(volumetricLight, shaftLocalToWorld);
        MeshFilter shaftMeshFilter = shaftObject.GetComponent<MeshFilter>();
        MeshRenderer shaftRenderer = shaftObject.GetComponent<MeshRenderer>();
        Undo.RecordObject(shaftMeshFilter, "Bake Light Shafts");
        Undo.RecordObject(shaftRenderer, "Bake Light Shafts");
        shaftMeshFilter.sharedMesh = mesh;
        shaftObject.SetActive(true);
        shaftRenderer.enabled = true;
        shaftRenderer.forceRenderingOff = false;
        shaftRenderer.sharedMaterial = volumetricLight.bakedShaftMaterial != null
            ? volumetricLight.bakedShaftMaterial
            : EnsureDefaultMaterial();
        shaftRenderer.SetPropertyBlock(CreateShaftPropertyBlock(volumetricLight));
        shaftRenderer.shadowCastingMode = ShadowCastingMode.Off;
        shaftRenderer.receiveShadows = false;
        volumetricLight.bakedShaftObject = shaftObject;

        EditorUtility.SetDirty(volumetricLight);
        EditorUtility.SetDirty(shaftObject);
        EditorSceneManager.MarkSceneDirty(volumetricLight.gameObject.scene);
        Debug.Log(
            $"Baked {quadCount} light shaft quads from {meshFilters.Count} influenced meshes to {meshPath}. " +
            $"Bounds center {mesh.bounds.center}, extents {mesh.bounds.extents}.",
            volumetricLight);
    }

    public static void Clear(VolumetricLight volumetricLight)
    {
        if (volumetricLight == null)
            return;

        Undo.RecordObject(volumetricLight, "Clear Baked Light Shafts");
        GameObject shaftObject = volumetricLight.bakedShaftObject;
        if (shaftObject == null)
        {
            Transform child = volumetricLight.transform.Find(ShaftObjectName);
            if (child != null)
                shaftObject = child.gameObject;
        }

        if (shaftObject != null)
            Undo.DestroyObjectImmediate(shaftObject);

        volumetricLight.bakedShaftObject = null;
        volumetricLight.bakedShaftMesh = null;
        EditorUtility.SetDirty(volumetricLight);
        EditorSceneManager.MarkSceneDirty(volumetricLight.gameObject.scene);
    }

    static List<MeshFilter> CollectInfluencedMeshes(Light light)
    {
        MeshFilter[] allMeshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        List<MeshFilter> influenced = new();
        for (int i = 0; i < allMeshFilters.Length; i++)
        {
            MeshFilter meshFilter = allMeshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null || meshFilter.GetComponentInParent<VolumetricLight>() != null)
                continue;

            MeshRenderer renderer = meshFilter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            if (IsRendererInfluencedByLight(renderer, light))
                influenced.Add(meshFilter);
        }

        return influenced;
    }

    static bool IsRendererInfluencedByLight(Renderer renderer, Light light)
    {
        Bounds bounds = renderer.bounds;
        Vector3 lightPosition = light.transform.position;
        if (bounds.SqrDistance(lightPosition) > light.range * light.range)
            return false;

        if (light.type != LightType.Spot)
            return true;

        Vector3 forward = light.transform.forward;
        float cosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
        Vector3 extents = bounds.extents;
        float radius = extents.magnitude;
        Vector3 toCenter = bounds.center - lightPosition;
        float distance = toCenter.magnitude;
        if (distance <= radius)
            return true;

        float relaxedCos = Mathf.Cos((light.spotAngle * 0.5f + Mathf.Asin(Mathf.Clamp01(radius / distance)) * Mathf.Rad2Deg) * Mathf.Deg2Rad);
        return Vector3.Dot(forward, toCenter / distance) >= Mathf.Min(cosOuter, relaxedCos);
    }

    static void AppendMeshShafts(
        MeshFilter meshFilter,
        LightShaftMeshBuilder.ShaftLight shaftLight,
        float shaftDistance,
        Matrix4x4 shaftWorldToLocal,
        NativeList<LightShaftMeshBuilder.ShaftQuad> quads)
    {
        Mesh mesh = meshFilter.sharedMesh;
        using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
        Mesh.MeshData meshData = meshDataArray[0];
        int vertexCount = meshData.vertexCount;
        int indexCount = GetTotalIndexCount(meshData);
        if (vertexCount <= 0 || indexCount < 3)
            return;

        NativeArray<float3> localVertices = new(vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        NativeArray<int> meshIndices = new(indexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        NativeParallelMultiHashMap<ulong, LightShaftMeshBuilder.EdgeFace> edgeFaces = new(indexCount, Allocator.TempJob);

        try
        {
            NativeArray<Vector3> tempVertices = new(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            meshData.GetVertices(tempVertices);
            for (int i = 0; i < vertexCount; i++)
                localVertices[i] = tempVertices[i];
            tempVertices.Dispose();

            CopyIndices(meshData, meshIndices);

            LightShaftMeshBuilder.ScheduleExtract(
                localVertices,
                meshIndices,
                meshFilter.transform.localToWorldMatrix,
                shaftWorldToLocal,
                shaftLight,
                shaftDistance,
                edgeFaces,
                quads).Complete();
        }
        finally
        {
            if (edgeFaces.IsCreated)
                edgeFaces.Dispose();
            if (meshIndices.IsCreated)
                meshIndices.Dispose();
            if (localVertices.IsCreated)
                localVertices.Dispose();
        }
    }

    static int GetTotalIndexCount(Mesh.MeshData meshData)
    {
        int total = 0;
        for (int i = 0; i < meshData.subMeshCount; i++)
            total += (int)meshData.GetSubMesh(i).indexCount;
        return total - total % 3;
    }

    static void CopyIndices(Mesh.MeshData meshData, NativeArray<int> destination)
    {
        int offset = 0;
        for (int subMesh = 0; subMesh < meshData.subMeshCount; subMesh++)
        {
            int subMeshIndexCount = (int)meshData.GetSubMesh(subMesh).indexCount;
            if (meshData.indexFormat == IndexFormat.UInt16)
            {
                NativeArray<ushort> source = new(subMeshIndexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                meshData.GetIndices(source, subMesh);
                int copyCount = subMeshIndexCount - subMeshIndexCount % 3;
                for (int i = 0; i < copyCount; i++)
                    destination[offset + i] = source[i];
                offset += copyCount;
                source.Dispose();
            }
            else
            {
                NativeArray<int> source = new(subMeshIndexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                meshData.GetIndices(source, subMesh);
                int copyCount = subMeshIndexCount - subMeshIndexCount % 3;
                NativeArray<int>.Copy(source, 0, destination, offset, copyCount);
                offset += copyCount;
                source.Dispose();
            }
        }
    }

    static string SaveOrOverwriteMesh(VolumetricLight volumetricLight, Mesh mesh)
    {
        EnsureFolder(GeneratedRoot);
        string path = AssetDatabase.GetAssetPath(volumetricLight.bakedShaftMesh);
        if (string.IsNullOrEmpty(path))
        {
            string sceneName = volumetricLight.gameObject.scene.IsValid()
                ? volumetricLight.gameObject.scene.name
                : "UnsavedScene";
            string sceneFolder = $"{GeneratedRoot}/{SanitizeFileName(sceneName)}";
            EnsureFolder(sceneFolder);
            path = $"{sceneFolder}/{SanitizeFileName(volumetricLight.gameObject.name)}_Shafts.asset";
        }

        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = mesh.name;
            EditorUtility.SetDirty(existing);
        }
        else
        {
            AssetDatabase.CreateAsset(mesh, path);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return path;
    }

    static GameObject EnsureShaftObject(VolumetricLight volumetricLight, Matrix4x4 localToWorld)
    {
        GameObject shaftObject = volumetricLight.bakedShaftObject;
        if (shaftObject == null)
        {
            Transform existing = volumetricLight.transform.Find(ShaftObjectName);
            if (existing != null)
                shaftObject = existing.gameObject;
        }

        if (shaftObject == null)
        {
            shaftObject = new GameObject(ShaftObjectName);
            Undo.RegisterCreatedObjectUndo(shaftObject, "Create Baked Light Shafts");
            shaftObject.transform.SetParent(volumetricLight.transform, false);
        }

        Undo.RecordObject(shaftObject.transform, "Bake Light Shafts");
        shaftObject.transform.SetPositionAndRotation(localToWorld.GetColumn(3), localToWorld.rotation);
        shaftObject.transform.localScale = Vector3.one;

        if (!shaftObject.TryGetComponent(out MeshFilter _))
            Undo.AddComponent<MeshFilter>(shaftObject);
        if (!shaftObject.TryGetComponent(out MeshRenderer _))
            Undo.AddComponent<MeshRenderer>(shaftObject);

        return shaftObject;
    }

    static MaterialPropertyBlock CreateShaftPropertyBlock(VolumetricLight volumetricLight)
    {
        MaterialPropertyBlock propertyBlock = new();
        propertyBlock.SetColor("_Color", volumetricLight.bakedShaftColor);
        return propertyBlock;
    }

    static Material EnsureDefaultMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(DefaultMaterialPath);
        if (material != null)
            return material;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
            shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingBakedShafts.shader");
        if (shader == null)
            return null;

        EnsureFolder(GeneratedRoot);
        material = new Material(shader)
        {
            name = "BakedLightShaftsDefault"
        };
        material.SetColor("_Color", new Color(1f, 0.95f, 0.75f, 1f));
        AssetDatabase.CreateAsset(material, DefaultMaterialPath);
        AssetDatabase.SaveAssets();
        return material;
    }

    static void EnsureFolder(string assetFolder)
    {
        string[] parts = assetFolder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Light" : value;
    }
}
