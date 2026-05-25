using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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
        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Color> colors = new();
        List<int> indices = new();

        try
        {
            for (int i = 0; i < meshFilters.Count; i++)
                AppendMeshShafts(meshFilters[i], light, shaftDistance, shaftWorldToLocal, vertices, normals, colors, indices);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            return;
        }

        Mesh mesh = BuildMesh(vertices, normals, colors, indices);
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
            $"Baked {indices.Count / 6} light shaft quads from {meshFilters.Count} influenced meshes to {meshPath}. " +
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
        Light light,
        float shaftDistance,
        Matrix4x4 shaftWorldToLocal,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Color> colors,
        List<int> indices)
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
        NativeParallelMultiHashMap<ulong, EdgeFace> edgeFaces = new(indexCount, Allocator.TempJob);
        NativeList<ShaftQuad> quads = new(indexCount, Allocator.TempJob);

        try
        {
            NativeArray<Vector3> tempVertices = new(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            meshData.GetVertices(tempVertices);
            for (int i = 0; i < vertexCount; i++)
                localVertices[i] = tempVertices[i];
            tempVertices.Dispose();

            CopyIndices(meshData, meshIndices);

            Matrix4x4 localToWorldMatrix = meshFilter.transform.localToWorldMatrix;
            var buildJob = new BuildEdgeFaceMapJob
            {
                vertices = localVertices,
                indices = meshIndices,
                localToWorld = ToFloat4x4(localToWorldMatrix),
                lightPosition = light.transform.position,
                spotForward = light.transform.forward,
                lightType = light.type == LightType.Spot ? 1 : 0,
                spotCosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad),
                edgeFaces = edgeFaces.AsParallelWriter()
            };

            var extractJob = new ExtractShaftQuadsJob
            {
                vertices = localVertices,
                indices = meshIndices,
                localToWorld = ToFloat4x4(localToWorldMatrix),
                shaftWorldToLocal = ToFloat4x4(shaftWorldToLocal),
                lightPosition = light.transform.position,
                shaftDistance = shaftDistance,
                edgeFaces = edgeFaces,
                quads = quads
            };

            JobHandle buildHandle = buildJob.Schedule(indexCount / 3, 64);
            extractJob.Schedule(buildHandle).Complete();

            for (int i = 0; i < quads.Length; i++)
            {
                ShaftQuad quad = quads[i];
                int baseVertex = vertices.Count;
                vertices.Add(quad.a);
                vertices.Add(quad.b);
                vertices.Add(quad.c);
                vertices.Add(quad.d);
                normals.Add(quad.normal);
                normals.Add(quad.normal);
                normals.Add(quad.normal);
                normals.Add(quad.normal);
                colors.Add(new Color(1f, 1f, 1f, 1f));
                colors.Add(new Color(1f, 1f, 1f, 1f));
                colors.Add(new Color(1f, 1f, 1f, 0f));
                colors.Add(new Color(1f, 1f, 1f, 0f));
                indices.Add(baseVertex);
                indices.Add(baseVertex + 1);
                indices.Add(baseVertex + 2);
                indices.Add(baseVertex);
                indices.Add(baseVertex + 2);
                indices.Add(baseVertex + 3);
            }
        }
        finally
        {
            if (quads.IsCreated)
                quads.Dispose();
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

    static Mesh BuildMesh(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> indices)
    {
        Mesh.MeshDataArray writableMeshData = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData meshData = writableMeshData[0];
        var vertexAttributes = new NativeArray<VertexAttributeDescriptor>(3, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        vertexAttributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        vertexAttributes[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
        vertexAttributes[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4);
        meshData.SetVertexBufferParams(vertices.Count, vertexAttributes);
        vertexAttributes.Dispose();

        meshData.SetIndexBufferParams(indices.Count, IndexFormat.UInt32);
        NativeArray<ShaftVertex> vertexData = meshData.GetVertexData<ShaftVertex>();
        for (int i = 0; i < vertices.Count; i++)
        {
            vertexData[i] = new ShaftVertex
            {
                position = vertices[i],
                normal = normals[i],
                color = colors[i]
            };
        }

        NativeArray<int> indexData = meshData.GetIndexData<int>();
        for (int i = 0; i < indices.Count; i++)
            indexData[i] = indices[i];

        meshData.subMeshCount = 1;
        meshData.SetSubMesh(0, new SubMeshDescriptor(0, indices.Count, MeshTopology.Triangles)
        {
            firstVertex = 0,
            vertexCount = vertices.Count,
            bounds = CalculateBounds(vertices)
        });

        Mesh mesh = new();
        Mesh.ApplyAndDisposeWritableMeshData(writableMeshData, mesh);
        mesh.SetTriangles(indices, 0, true);
        return mesh;
    }

    static Bounds CalculateBounds(List<Vector3> vertices)
    {
        if (vertices.Count == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds bounds = new(vertices[0], Vector3.zero);
        for (int i = 1; i < vertices.Count; i++)
            bounds.Encapsulate(vertices[i]);
        return bounds;
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

    static float4x4 ToFloat4x4(Matrix4x4 matrix)
    {
        return new float4x4(
            matrix.m00, matrix.m01, matrix.m02, matrix.m03,
            matrix.m10, matrix.m11, matrix.m12, matrix.m13,
            matrix.m20, matrix.m21, matrix.m22, matrix.m23,
            matrix.m30, matrix.m31, matrix.m32, matrix.m33);
    }

    readonly struct EdgeFace
    {
        public readonly int triangleIndex;
        public readonly byte lit;

        public EdgeFace(int triangleIndex, bool lit)
        {
            this.triangleIndex = triangleIndex;
            this.lit = lit ? (byte)1 : (byte)0;
        }
    }

    struct ShaftQuad
    {
        public float3 a;
        public float3 b;
        public float3 c;
        public float3 d;
        public float3 normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShaftVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Color color;
    }

    [BurstCompile]
    struct BuildEdgeFaceMapJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> vertices;
        [ReadOnly] public NativeArray<int> indices;
        public float4x4 localToWorld;
        public float3 lightPosition;
        public float3 spotForward;
        public int lightType;
        public float spotCosOuter;
        public NativeParallelMultiHashMap<ulong, EdgeFace>.ParallelWriter edgeFaces;

        public void Execute(int triangleIndex)
        {
            int indexOffset = triangleIndex * 3;
            int i0 = indices[indexOffset];
            int i1 = indices[indexOffset + 1];
            int i2 = indices[indexOffset + 2];
            float3 a = TransformPoint(localToWorld, vertices[i0]);
            float3 b = TransformPoint(localToWorld, vertices[i1]);
            float3 c = TransformPoint(localToWorld, vertices[i2]);
            float3 normal = math.normalizesafe(math.cross(b - a, c - a));
            float3 center = (a + b + c) / 3f;
            float3 toLight = math.normalizesafe(lightPosition - center);
            bool lit = math.dot(normal, toLight) > 0f;
            if (lightType == 1)
                lit &= math.dot(math.normalizesafe(center - lightPosition), spotForward) >= spotCosOuter;

            AddEdge(i0, i1, triangleIndex, lit);
            AddEdge(i1, i2, triangleIndex, lit);
            AddEdge(i2, i0, triangleIndex, lit);
        }

        void AddEdge(int a, int b, int triangleIndex, bool lit)
        {
            edgeFaces.Add(EdgeKey(vertices[a], vertices[b]), new EdgeFace(triangleIndex, lit));
        }
    }

    [BurstCompile]
    struct ExtractShaftQuadsJob : IJob
    {
        [ReadOnly] public NativeArray<float3> vertices;
        [ReadOnly] public NativeArray<int> indices;
        public float4x4 localToWorld;
        public float4x4 shaftWorldToLocal;
        public float3 lightPosition;
        public float shaftDistance;
        [ReadOnly] public NativeParallelMultiHashMap<ulong, EdgeFace> edgeFaces;
        public NativeList<ShaftQuad> quads;

        public void Execute()
        {
            int triangleCount = indices.Length / 3;
            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                int indexOffset = triangleIndex * 3;
                TryEmitEdge(indices[indexOffset], indices[indexOffset + 1], triangleIndex);
                TryEmitEdge(indices[indexOffset + 1], indices[indexOffset + 2], triangleIndex);
                TryEmitEdge(indices[indexOffset + 2], indices[indexOffset], triangleIndex);
            }
        }

        void TryEmitEdge(int indexA, int indexB, int triangleIndex)
        {
            ulong key = EdgeKey(vertices[indexA], vertices[indexB]);
            NativeParallelMultiHashMapIterator<ulong> iterator;
            EdgeFace face;
            int minTriangle = int.MaxValue;
            bool hasLit = false;
            bool hasShadow = false;
            int count = 0;

            if (!edgeFaces.TryGetFirstValue(key, out face, out iterator))
                return;

            do
            {
                count++;
                minTriangle = math.min(minTriangle, face.triangleIndex);
                hasLit |= face.lit != 0;
                hasShadow |= face.lit == 0;
            }
            while (edgeFaces.TryGetNextValue(out face, ref iterator));

            if (count < 2 || !hasLit || !hasShadow || triangleIndex != minTriangle)
                return;

            float3 worldA = TransformPoint(localToWorld, vertices[indexA]);
            float3 worldB = TransformPoint(localToWorld, vertices[indexB]);
            float3 edgeCenter = (worldA + worldB) * 0.5f;
            float3 extrusion = math.normalizesafe(edgeCenter - lightPosition) * shaftDistance;
            float3 worldC = worldB + extrusion;
            float3 worldD = worldA + extrusion;
            float3 localA = TransformPoint(shaftWorldToLocal, worldA);
            float3 localB = TransformPoint(shaftWorldToLocal, worldB);
            float3 localC = TransformPoint(shaftWorldToLocal, worldC);
            float3 localD = TransformPoint(shaftWorldToLocal, worldD);
            float3 normal = math.normalizesafe(math.cross(localB - localA, localC - localA), new float3(0f, 1f, 0f));

            quads.Add(new ShaftQuad
            {
                a = localA,
                b = localB,
                c = localC,
                d = localD,
                normal = normal
            });
        }
    }

    static ulong EdgeKey(float3 a, float3 b)
    {
        uint hashA = PositionHash(a);
        uint hashB = PositionHash(b);
        uint min = math.min(hashA, hashB);
        uint max = math.max(hashA, hashB);
        return ((ulong)min << 32) | max;
    }

    static uint PositionHash(float3 position)
    {
        const float quantizationScale = 100000f;
        return math.hash((int3)math.round(position * quantizationScale));
    }

    static float3 TransformPoint(float4x4 matrix, float3 point)
    {
        return math.transform(matrix, point);
    }
}
