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
        public bool enableDirectLighting = true;
        public bool enableAdditionalLights = false;
        public bool enableKawaseBlur = false;
        [Min(0f)] public float kawaseIteration = 1f;
        public DebugMode debugMode = DebugMode.Off;
        [Range(0f, 1f)] public float debugOpacity = 0.65f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
    }

    const string ComputeShaderResourcePath = "VolumetricLighting/VolumetricLightingFroxelUpdate";
    const string CompositeShaderResourcePath = "VolumetricLighting/VolumetricLightingComposite";

    [SerializeField] Settings settings = new Settings();

    VolumetricLightingRenderPass renderPass;
    ComputeShader froxelUpdateShader;
    Material compositeMaterial;
    // Second instance of the composite shader used by the blur/debug pass. Both raster passes set
    // material properties inside RenderGraph render funcs, which all run before the command buffer
    // executes; sharing one material would let the later pass overwrite the earlier pass's values.
    Material blurMaterial;

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
        if (froxelUpdateShader == null || compositeMaterial == null || blurMaterial == null)
            return;

        renderPass.Setup(settings, compositeMaterial, blurMaterial, froxelUpdateShader);
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
        if (blurMaterial != null)
        {
            CoreUtils.Destroy(blurMaterial);
            blurMaterial = null;
        }
    }

    void EnsureResources()
    {
        if (froxelUpdateShader == null)
            froxelUpdateShader = Resources.Load<ComputeShader>(ComputeShaderResourcePath);

        if (compositeMaterial == null || blurMaterial == null)
        {
            Shader compositeShader = Resources.Load<Shader>(CompositeShaderResourcePath);
            if (compositeShader != null)
            {
                if (compositeMaterial == null)
                    compositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
                if (blurMaterial == null)
                    blurMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
            }
        }
    }
}
