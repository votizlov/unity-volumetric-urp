using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class VolumetricLightingRendererFeature : ScriptableRendererFeature
{
    public enum DebugMode
    {
        Off = 0,
        FroxelOverlay = 1,
        Accumulation = 2,
        IntermediateTexture = 3,
        BlurredTexture = 4,
        IntermediateSolidColor = 5
    }

    [System.Serializable]
    public sealed class Settings
    {
        [Min(1)] public int gridX = 160;
        [Min(1)] public int gridY = 90;
        [Min(1)] public int gridZ = 64;
        [Min(0f)] public float nearOffset = 0.5f;
        [Min(0.1f)] public float maxDistance = 48f;
        [Min(0f)] public float scatteringStrength = 0.35f;
        [Min(1)] public int stepCount = 48;
        [Range(0f, 1f)] public float jitterStrength = 0.25f;
        [Min(0f)] public float compositeDepthBias = 0.01f;
        [Header("Uniform Fog")]
        [Tooltip("Replace shadowed light scattering with uniform distance fog. Does not sample shadow maps.")]
        public bool enableUniformFog = false;
        [Tooltip("Uniform additive fog density per world unit, independent of scattering strength.")]
        [Min(0f)] public float uniformFogDensity = 0.005f;
        public bool enableDirectLighting = true;
        public bool enableAdditionalLights = false;
        [Header("Volumetric Cookies")]
        [Tooltip("Resolution of the RGB atlas used by independently assigned volumetric spot light cookies. Rounded to a power of two between 128 and 4096.")]
        [Min(128)] public int cookieAtlasResolution = 1024;
        [Header("Kawase Blur")]
        public bool enableKawaseBlur = false;
        [Min(0f)] public float kawaseIteration = 1f;
        [Tooltip("Run the Kawase blur as a chain of real render passes with growing offsets instead of emulating one iteration in a single pass.")]
        public bool enableMultipassKawase = false;
        [Range(1, MaxKawaseBlurPasses)] public int kawaseBlurPassCount = 3;
        public DebugMode debugMode = DebugMode.Off;
        [Range(0f, 1f)] public float debugOpacity = 0.65f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
    }

    // Camera-local overrides leave renderer assets untouched and preserve their defaults.
    static readonly System.Collections.Generic.Dictionary<Camera, float> uniformFogOverrides = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetUniformFogOverrides() => uniformFogOverrides.Clear();

    public static void SetUniformFogOverride(Camera camera, float density)
    {
        if (camera != null) uniformFogOverrides[camera] = Mathf.Max(0f, density);
    }

    public static void ClearUniformFogOverride(Camera camera)
    {
        if (!ReferenceEquals(camera, null)) uniformFogOverrides.Remove(camera);
    }

    internal static bool GetUniformFog(Camera camera, Settings settings, out float density)
    {
        if (uniformFogOverrides.TryGetValue(camera, out density)) return true;
        density = Mathf.Max(0f, settings.uniformFogDensity);
        return settings.enableUniformFog;
    }

    public const int MaxKawaseBlurPasses = 8;

    const string ComputeShaderResourcePath = "VolumetricLighting/VolumetricLightingFroxelUpdate";
    const string CompositeShaderResourcePath = "VolumetricLighting/VolumetricLightingComposite";

    [SerializeField] Settings settings = new Settings();

    VolumetricLightingRenderPass renderPass;
    ComputeShader froxelUpdateShader;
    Material compositeMaterial;
    // Extra instances of the composite shader, one per blur/debug pass. Every raster pass sets
    // material properties inside RenderGraph render funcs, which all run before the command buffer
    // executes; sharing one material would let a later pass overwrite an earlier pass's values.
    readonly Material[] blurMaterials = new Material[MaxKawaseBlurPasses];

    public override void Create()
    {
        renderPass = new VolumetricLightingRenderPass(name);
        EnsureResources();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderer == null
            || renderingData.cameraData.xrRendering
            || !renderingData.cameraData.resolveFinalTarget)
        {
            return;
        }

        Camera camera = renderingData.cameraData.camera;
        if (camera == null
            || camera.cameraType == CameraType.Preview
            || camera.cameraType == CameraType.Reflection)
        {
            return;
        }

        EnsureResources();
        if (froxelUpdateShader == null || compositeMaterial == null || blurMaterials[0] == null)
            return;

        renderPass.Setup(settings, compositeMaterial, blurMaterials, froxelUpdateShader);
        renderer.EnqueuePass(renderPass);
    }

    protected override void Dispose(bool disposing)
    {
        renderPass?.Dispose();
        if (compositeMaterial != null)
        {
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
        }
        for (int materialIndex = 0; materialIndex < blurMaterials.Length; ++materialIndex)
        {
            if (blurMaterials[materialIndex] != null)
            {
                CoreUtils.Destroy(blurMaterials[materialIndex]);
                blurMaterials[materialIndex] = null;
            }
        }
    }

    void EnsureResources()
    {
        if (froxelUpdateShader == null)
            froxelUpdateShader = Resources.Load<ComputeShader>(ComputeShaderResourcePath);

        Shader compositeShader = null;
        if (compositeMaterial == null)
        {
            compositeShader = Resources.Load<Shader>(CompositeShaderResourcePath);
            if (compositeShader != null)
                compositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
        }

        int blurMaterialCount = settings.enableMultipassKawase
            ? Mathf.Clamp(settings.kawaseBlurPassCount, 1, MaxKawaseBlurPasses)
            : 1;
        for (int materialIndex = 0; materialIndex < blurMaterialCount; ++materialIndex)
        {
            if (blurMaterials[materialIndex] != null)
                continue;
            if (compositeShader == null)
                compositeShader = Resources.Load<Shader>(CompositeShaderResourcePath);
            if (compositeShader == null)
                return;
            blurMaterials[materialIndex] = CoreUtils.CreateEngineMaterial(compositeShader);
        }
    }
}
