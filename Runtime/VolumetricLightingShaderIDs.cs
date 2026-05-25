using UnityEngine;

static class VolumetricLightingShaderIDs
{
    internal const string DirectLightingKeyword = "_VL_DIRECT_LIGHTING";
    internal const string AdditionalLightsKeyword = "_VL_ADDITIONAL_LIGHTS";

    internal static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
    internal static readonly int KawaseDepthTexture = Shader.PropertyToID("_VolumetricLightingKawaseDepthTexture");
    internal static readonly int FroxelTexture = Shader.PropertyToID("_VolumetricLightingFroxelTexture");
    internal static readonly int LightingTexture = Shader.PropertyToID("_VolumetricLightingTexture");
    internal static readonly int AdditionalLightsBuffer = Shader.PropertyToID("_VolumetricLightingAdditionalLights");
    internal static readonly int AdditionalLightCount = Shader.PropertyToID("_VolumetricLightingAdditionalLightCount");
    internal static readonly int GridParams = Shader.PropertyToID("_VolumetricLightingGridParams");
    internal static readonly int ScreenSize = Shader.PropertyToID("_VolumetricLightingScreenSize");
    internal static readonly int VolumeParams = Shader.PropertyToID("_VolumetricLightingVolumeParams");
    internal static readonly int InverseProjectionMatrix = Shader.PropertyToID("_VolumetricLightingInverseProjectionMatrix");
    internal static readonly int CameraToWorldMatrix = Shader.PropertyToID("_VolumetricLightingCameraToWorldMatrix");
    internal static readonly int MainLightDirection = Shader.PropertyToID("_VolumetricLightingMainLightDirection");
    internal static readonly int MainLightColor = Shader.PropertyToID("_VolumetricLightingMainLightColor");
    internal static readonly int ScatteringStrength = Shader.PropertyToID("_VolumetricLightingScatteringStrength");
    internal static readonly int JitterStrength = Shader.PropertyToID("_VolumetricLightingJitterStrength");
    internal static readonly int CompositeDepthBias = Shader.PropertyToID("_VolumetricLightingCompositeDepthBias");
    internal static readonly int DebugOpacity = Shader.PropertyToID("_VolumetricLightingDebugOpacity");
    internal static readonly int StepCount = Shader.PropertyToID("_VolumetricLightingStepCount");
    internal static readonly int KawaseIteration = Shader.PropertyToID("_VolumetricLightingKawaseIteration");
}
