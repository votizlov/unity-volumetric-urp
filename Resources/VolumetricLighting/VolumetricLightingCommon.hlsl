#ifndef VOLUMETRIC_LIGHTING_COMMON_INCLUDED
#define VOLUMETRIC_LIGHTING_COMMON_INCLUDED

static const float VOLUMETRIC_LIGHTING_EPSILON = 1e-4;

float2 VolumetricLighting_GetFullscreenTriangleUV(uint vertexID)
{
    return float2((vertexID << 1) & 2, vertexID & 2);
}

float4 VolumetricLighting_GetFullscreenTrianglePositionCS(uint vertexID)
{
    float2 uv = VolumetricLighting_GetFullscreenTriangleUV(vertexID);
    return float4(uv * 2.0 - 1.0, 0.0, 1.0);
}

float2 VolumetricLighting_GetDepthUv(float2 uv)
{
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    return uv;
}

float VolumetricLighting_SliceIndexToLinearDepth(float sliceIndex, float sliceCount, float nearDepth, float maxDistance)
{
    float t = saturate((sliceIndex + 0.5) / max(sliceCount, 1.0));
    return nearDepth * pow(maxDistance / max(nearDepth, VOLUMETRIC_LIGHTING_EPSILON), t);
}

float VolumetricLighting_LinearDepthToSlice01(float linearDepth, float nearDepth, float maxDistance)
{
    float safeNear = max(nearDepth, VOLUMETRIC_LIGHTING_EPSILON);
    float safeDepth = clamp(linearDepth, safeNear, maxDistance);
    float ratio = max(maxDistance / safeNear, 1.0 + VOLUMETRIC_LIGHTING_EPSILON);
    return saturate(log2(safeDepth / safeNear) / log2(ratio));
}

float3 VolumetricLighting_GetViewRay(float2 uv, float4x4 inverseProjection)
{
    float2 ndc = uv * 2.0 - 1.0;
    float4 clipPosition = float4(ndc, 1.0, 1.0);
    float4 viewPosition = mul(inverseProjection, clipPosition);
    float3 ray = viewPosition.xyz / max(abs(viewPosition.z), VOLUMETRIC_LIGHTING_EPSILON);
    return ray;
}

float VolumetricLighting_Hash11(float value)
{
    return frac(sin(value * 12.9898) * 43758.5453);
}

float3 VolumetricLighting_HashColor(int3 cell)
{
    float seed = dot(float3(cell), float3(17.0, 59.4, 113.0));
    return saturate(float3(
        VolumetricLighting_Hash11(seed + 0.37),
        VolumetricLighting_Hash11(seed + 1.79),
        VolumetricLighting_Hash11(seed + 4.11)));
}

#endif
