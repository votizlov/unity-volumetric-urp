using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Light shafts from the silhouette of geometry seen from a point or spot light: every edge between a face turned to
/// the light and a face turned away is extruded away from the light into a quad that fades out along its length.
/// Shared by the editor bake (VolumetricLightShaftBaker) and meshes rebuilt at runtime.
/// </summary>
public static class LightShaftMeshBuilder
{
    static readonly VertexAttributeDescriptor[] VertexLayout =
    {
        new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
        new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
    };

    public struct ShaftLight
    {
        public float3 position;
        public float3 spotForward;
        public bool isSpot;
        public float spotCosOuter;

        public static ShaftLight From(Light light) => new()
        {
            position = light.transform.position,
            spotForward = light.transform.forward,
            isSpot = light.type == LightType.Spot,
            spotCosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad)
        };
    }

    public readonly struct EdgeFace
    {
        public readonly int triangleIndex;
        public readonly byte lit;

        public EdgeFace(int triangleIndex, bool lit)
        {
            this.triangleIndex = triangleIndex;
            this.lit = lit ? (byte)1 : (byte)0;
        }
    }

    /// <summary>Silhouette edge a-b and its far end c-d, in shaft space.</summary>
    public struct ShaftQuad
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
        public Vector2 uv;
    }

    /// <summary>
    /// Appends a quad per silhouette edge of the triangles to <paramref name="quads"/>. Edges are matched by vertex
    /// position, so the mesh needs no welding, but faces must meet edge to edge: a T-junction hides its edge.
    /// </summary>
    public static JobHandle ScheduleExtract(
        NativeArray<float3> vertices,
        NativeArray<int> indices,
        float4x4 localToWorld,
        float4x4 shaftWorldToLocal,
        ShaftLight light,
        float shaftLength,
        NativeParallelMultiHashMap<ulong, EdgeFace> edgeFaces,
        NativeList<ShaftQuad> quads)
    {
        edgeFaces.Clear();
        if (edgeFaces.Capacity < indices.Length)
            edgeFaces.Capacity = indices.Length;

        var buildJob = new BuildEdgeFaceMapJob
        {
            vertices = vertices,
            indices = indices,
            localToWorld = localToWorld,
            lightPosition = light.position,
            spotForward = light.spotForward,
            lightType = light.isSpot ? 1 : 0,
            spotCosOuter = light.spotCosOuter,
            edgeFaces = edgeFaces.AsParallelWriter()
        };

        var extractJob = new ExtractShaftQuadsJob
        {
            vertices = vertices,
            indices = indices,
            localToWorld = localToWorld,
            shaftWorldToLocal = shaftWorldToLocal,
            lightPosition = light.position,
            shaftDistance = math.max(0.01f, shaftLength),
            edgeFaces = edgeFaces,
            quads = quads
        };

        JobHandle buildHandle = buildJob.Schedule(indices.Length / 3, 64);
        return extractJob.Schedule(buildHandle);
    }

    /// <summary>
    /// Rewrites <paramref name="target"/> with the quads, opaque at the silhouette edge and fading out at the far end.
    /// UV: v runs 0 at the edge to 1 at the far end, as the fade does, so a texture scrolled along v flows along the
    /// shafts; u runs along the edge at one unit per unit of its length, the same density on every shaft, each from 0.
    /// </summary>
    public static void WriteMesh(NativeArray<ShaftQuad> quads, Mesh target)
    {
        int vertexCount = quads.Length * 4;
        int indexCount = quads.Length * 6;
        var vertices = new NativeArray<ShaftVertex>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        var indices = new NativeArray<int>(indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        float3 min = new(float.PositiveInfinity);
        float3 max = new(float.NegativeInfinity);
        var edgeColor = new Color(1f, 1f, 1f, 1f);
        var endColor = new Color(1f, 1f, 1f, 0f);

        for (int i = 0; i < quads.Length; i++)
        {
            ShaftQuad quad = quads[i];
            int baseVertex = i * 4;
            // c continues b and d continues a away from the light
            float edge = math.distance(quad.a, quad.b);
            vertices[baseVertex] = new ShaftVertex { position = quad.a, normal = quad.normal, color = edgeColor, uv = new Vector2(0f, 0f) };
            vertices[baseVertex + 1] = new ShaftVertex { position = quad.b, normal = quad.normal, color = edgeColor, uv = new Vector2(edge, 0f) };
            vertices[baseVertex + 2] = new ShaftVertex { position = quad.c, normal = quad.normal, color = endColor, uv = new Vector2(edge, 1f) };
            vertices[baseVertex + 3] = new ShaftVertex { position = quad.d, normal = quad.normal, color = endColor, uv = new Vector2(0f, 1f) };

            int baseIndex = i * 6;
            indices[baseIndex] = baseVertex;
            indices[baseIndex + 1] = baseVertex + 1;
            indices[baseIndex + 2] = baseVertex + 2;
            indices[baseIndex + 3] = baseVertex;
            indices[baseIndex + 4] = baseVertex + 2;
            indices[baseIndex + 5] = baseVertex + 3;

            min = math.min(min, math.min(math.min(quad.a, quad.b), math.min(quad.c, quad.d)));
            max = math.max(max, math.max(math.max(quad.a, quad.b), math.max(quad.c, quad.d)));
        }

        target.Clear();
        target.SetVertexBufferParams(vertexCount, VertexLayout);
        target.SetVertexBufferData(vertices, 0, 0, vertexCount);
        target.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
        target.SetIndexBufferData(indices, 0, 0, indexCount);
        target.subMeshCount = 1;
        target.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontRecalculateBounds);
        target.bounds = quads.Length > 0 ? new Bounds((min + max) * 0.5f, max - min) : default;

        vertices.Dispose();
        indices.Dispose();
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
            float3 a = math.transform(localToWorld, vertices[i0]);
            float3 b = math.transform(localToWorld, vertices[i1]);
            float3 c = math.transform(localToWorld, vertices[i2]);
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

            // Each end runs along its own ray from the light, so the shaft widens like the light it carries
            float3 worldA = math.transform(localToWorld, vertices[indexA]);
            float3 worldB = math.transform(localToWorld, vertices[indexB]);
            float3 worldC = worldB + math.normalizesafe(worldB - lightPosition) * shaftDistance;
            float3 worldD = worldA + math.normalizesafe(worldA - lightPosition) * shaftDistance;
            float3 localA = math.transform(shaftWorldToLocal, worldA);
            float3 localB = math.transform(shaftWorldToLocal, worldB);
            float3 localC = math.transform(shaftWorldToLocal, worldC);
            float3 localD = math.transform(shaftWorldToLocal, worldD);
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
        uint hash = math.hash((int3)math.round(position * quantizationScale));

        // math.hash is linear, and the scale is a multiple of 32: on grid-aligned meshes (voxels) every hash shares its
        // low bits and the map key (min ^ max) of unit edges takes a handful of values, so all edges land in a few
        // buckets. The bijective murmur finalizer spreads them without merging distinct positions.
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        hash ^= hash >> 16;
        return hash;
    }
}
