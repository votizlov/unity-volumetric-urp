using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using System.Runtime.InteropServices;

public sealed class VolumetricLightingRenderPass : ScriptableRenderPass
{
    const string ComputeKernelName = "CSMain";
    const int MinCookieAtlasResolution = 128;
    const int MaxCookieAtlasResolution = 4096;
    const int MaxCookieSizeDivisor = 16;
    const int CookieWarningIntervalFrames = 300;

    [StructLayout(LayoutKind.Sequential)]
    struct AdditionalLightGpuData
    {
        internal Vector4 position;
        internal Vector4 color;
        internal Vector4 attenuation;
        internal Vector4 spotDirection;
        internal Vector4 extraData;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VolumetricCookieGpuData
    {
        internal Matrix4x4 worldToCookie;
        internal Vector4 atlasRect;
    }

    VolumetricLightingRendererFeature.Settings settings;
    Material compositeMaterial;
    Material[] blurMaterials;
    ComputeShader froxelUpdateShader;
    ComputeShader cachedShader;
    GraphicsBuffer additionalLightsBuffer;
    AdditionalLightGpuData[] additionalLightsCpuData = System.Array.Empty<AdditionalLightGpuData>();
    Texture2D[] additionalLightCookieTextures = System.Array.Empty<Texture2D>();
    Matrix4x4[] additionalLightCookieMatrices = System.Array.Empty<Matrix4x4>();
    GraphicsBuffer cookieDataBuffer;
    VolumetricCookieGpuData[] cookieDataCpuData = System.Array.Empty<VolumetricCookieGpuData>();
    Texture2D[] uniqueCookieTextures = System.Array.Empty<Texture2D>();
    Vector4[] uniqueCookieAtlasRects = System.Array.Empty<Vector4>();
    Vector2Int[] uniqueCookieAtlasSizes = System.Array.Empty<Vector2Int>();
    Texture2DAtlas cookieAtlas;
    int cookieAtlasResolution;
    int cookieSizeDivisor = 1;
    long previousCookieRequestPixelCount = long.MaxValue;
    int activeUniqueCookieCount;
    int lastCookieWarningFrame = -CookieWarningIntervalFrames;
    bool cookieAtlasNeedsClear;
    int computeKernelIndex = -1;
    int threadGroupSizeX = 4;
    int threadGroupSizeY = 4;
    int threadGroupSizeZ = 4;

    public VolumetricLightingRenderPass(string profilerTag)
    {
        profilingSampler = new ProfilingSampler(profilerTag);
        ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    public void Setup(VolumetricLightingRendererFeature.Settings sourceSettings, Material sourceMaterial, Material[] sourceBlurMaterials, ComputeShader sourceShader)
    {
        settings = sourceSettings;
        compositeMaterial = sourceMaterial;
        blurMaterials = sourceBlurMaterials;
        froxelUpdateShader = sourceShader;
        renderPassEvent = settings.renderPassEvent;

        if (froxelUpdateShader == null)
            return;

        if (cachedShader != froxelUpdateShader || computeKernelIndex < 0)
        {
            computeKernelIndex = froxelUpdateShader.FindKernel(ComputeKernelName);
            froxelUpdateShader.GetKernelThreadGroupSizes(
                computeKernelIndex,
                out uint groupSizeX,
                out uint groupSizeY,
                out uint groupSizeZ);
            threadGroupSizeX = Mathf.Max(1, (int)groupSizeX);
            threadGroupSizeY = Mathf.Max(1, (int)groupSizeY);
            threadGroupSizeZ = Mathf.Max(1, (int)groupSizeZ);
            cachedShader = froxelUpdateShader;
        }
    }

    public void Dispose()
    {
        additionalLightsBuffer?.Release();
        additionalLightsBuffer = null;
        cookieDataBuffer?.Release();
        cookieDataBuffer = null;
        cookieAtlas?.Release();
        cookieAtlas = null;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (settings == null
            || compositeMaterial == null
            || blurMaterials == null
            || blurMaterials.Length == 0
            || blurMaterials[0] == null
            || froxelUpdateShader == null
            || computeKernelIndex < 0
            || !SystemInfo.supportsComputeShaders)
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalLightData lightData = frameData.Get<UniversalLightData>();
        Camera camera = cameraData.camera;

        if (camera == null
            || !resourceData.cameraDepthTexture.IsValid())
        {
            return;
        }

        bool hasMainDirectionalLight = lightData.mainLightIndex >= 0
            && lightData.mainLightIndex < lightData.visibleLights.Length
            && lightData.visibleLights[lightData.mainLightIndex].light != null
            && lightData.visibleLights[lightData.mainLightIndex].lightType == LightType.Directional
            && ShouldProcessLight(lightData.visibleLights[lightData.mainLightIndex].light);

        bool enableDirectLighting = settings.enableDirectLighting && hasMainDirectionalLight;
        bool requestAdditionalLights = settings.enableAdditionalLights;
        if (!enableDirectLighting && !requestAdditionalLights)
            return;

        int gridX = Mathf.Max(1, settings.gridX);
        int gridY = Mathf.Max(1, settings.gridY);
        int gridZ = Mathf.Max(1, settings.gridZ);
        int stepCount = Mathf.Max(1, settings.stepCount);
        float nearDepth = Mathf.Max(0.01f, camera.nearClipPlane + Mathf.Max(0f, settings.nearOffset));
        float maxDistance = Mathf.Max(nearDepth + 0.01f, settings.maxDistance);
        VolumetricLightingRendererFeature.DebugMode debugMode = settings.debugMode;
        bool outputAccumulation = debugMode == VolumetricLightingRendererFeature.DebugMode.Accumulation;
        bool outputIntermediateTexture = debugMode == VolumetricLightingRendererFeature.DebugMode.IntermediateTexture;
        bool outputBlurredTexture = debugMode == VolumetricLightingRendererFeature.DebugMode.BlurredTexture;
        bool outputIntermediateSolidColor = debugMode == VolumetricLightingRendererFeature.DebugMode.IntermediateSolidColor;
        bool useIntermediateLightingTexture =
            settings.enableKawaseBlur
            || outputIntermediateTexture
            || outputBlurredTexture
            || outputIntermediateSolidColor;

        RenderTextureDescriptor froxelDescriptor = new(gridX, gridY, GraphicsFormat.R16G16B16A16_SFloat, 0)
        {
            msaaSamples = 1,
            volumeDepth = gridZ,
            dimension = TextureDimension.Tex3D,
            enableRandomWrite = true,
            sRGB = false,
            useMipMap = false,
            autoGenerateMips = false
        };

        TextureHandle froxelTexture = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph,
            froxelDescriptor,
            "VolumetricLighting.FroxelTexture",
            false,
            FilterMode.Bilinear);

        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
        Matrix4x4 inverseProjection = gpuProjection.inverse;
        Matrix4x4 cameraToWorld = camera.cameraToWorldMatrix;
        Vector3 lightDirection = hasMainDirectionalLight
            ? -lightData.visibleLights[lightData.mainLightIndex].light.transform.forward
            : Vector3.down;
        Color lightColor = hasMainDirectionalLight
            ? lightData.visibleLights[lightData.mainLightIndex].finalColor
            : Color.black;
        int cookieDataCount = 0;
        int additionalLightCount = requestAdditionalLights
            ? UpdateAdditionalLightsBuffer(lightData, out cookieDataCount)
            : 0;
        bool enableAdditionalLights = requestAdditionalLights && additionalLightCount > 0;

        if (!enableDirectLighting && !enableAdditionalLights)
            return;

        TextureHandle cookieAtlasTexture = default;
        if (enableAdditionalLights && cookieDataCount > 0)
        {
            cookieAtlasTexture = renderGraph.ImportTexture(cookieAtlas.AtlasTexture);
            RecordCookieAtlasUpdatePass(renderGraph, cookieAtlasTexture);
        }

        using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<ComputePassData>(
                   "Volumetric Lighting Froxel Update",
                   out ComputePassData passData,
                   profilingSampler))
        {
            passData.computeShader = froxelUpdateShader;
            passData.computeKernelIndex = computeKernelIndex;
            passData.cameraDepthTexture = resourceData.cameraDepthTexture;
            passData.froxelTexture = froxelTexture;
            passData.additionalLightsBuffer = additionalLightCount > 0
                ? renderGraph.ImportBuffer(additionalLightsBuffer)
                : default;
            passData.cookieAtlasTexture = cookieAtlasTexture;
            passData.cookieDataBuffer = cookieDataCount > 0
                ? renderGraph.ImportBuffer(cookieDataBuffer)
                : default;
            passData.additionalLightCount = additionalLightCount;
            passData.cookieDataCount = cookieDataCount;
            passData.gridX = gridX;
            passData.gridY = gridY;
            passData.gridZ = gridZ;
            passData.screenWidth = cameraData.cameraTargetDescriptor.width;
            passData.screenHeight = cameraData.cameraTargetDescriptor.height;
            passData.nearDepth = nearDepth;
            passData.maxDistance = maxDistance;
            passData.inverseProjection = inverseProjection;
            passData.cameraToWorld = cameraToWorld;
            passData.mainLightDirection = lightDirection;
            passData.mainLightColor = lightColor;
            passData.dispatchGroupsX = Mathf.CeilToInt(gridX / (float)threadGroupSizeX);
            passData.dispatchGroupsY = Mathf.CeilToInt(gridY / (float)threadGroupSizeY);
            passData.dispatchGroupsZ = Mathf.CeilToInt(gridZ / (float)threadGroupSizeZ);
            passData.enableDirectLighting = enableDirectLighting;
            passData.enableAdditionalLights = enableAdditionalLights;

            builder.UseTexture(passData.cameraDepthTexture, AccessFlags.Read);
            builder.UseTexture(passData.froxelTexture, AccessFlags.Write);
            if (additionalLightCount > 0)
                builder.UseBuffer(passData.additionalLightsBuffer, AccessFlags.Read);
            if (cookieDataCount > 0)
            {
                builder.UseTexture(passData.cookieAtlasTexture, AccessFlags.Read);
                builder.UseBuffer(passData.cookieDataBuffer, AccessFlags.Read);
            }
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (ComputePassData data, ComputeGraphContext context) => ExecuteComputePass(data, context));
        }

        RenderTextureDescriptor lightingDescriptor = cameraData.cameraTargetDescriptor;
        lightingDescriptor.depthBufferBits = 0;
        lightingDescriptor.msaaSamples = 1;
        lightingDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        lightingDescriptor.sRGB = false;
        lightingDescriptor.useMipMap = false;
        lightingDescriptor.autoGenerateMips = false;
        lightingDescriptor.enableRandomWrite = false;

        TextureHandle lightingTexture = TextureHandle.nullHandle;
        if (useIntermediateLightingTexture)
        {
            lightingTexture = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph,
                lightingDescriptor,
                "VolumetricLighting.LightingTexture",
                false,
                FilterMode.Bilinear);
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RasterPassData>(
                   useIntermediateLightingTexture ? "Volumetric Lighting Accumulate" : "Volumetric Lighting Composite",
                   out RasterPassData passData,
                   profilingSampler))
        {
            passData.material = compositeMaterial;
            passData.sourceTexture = useIntermediateLightingTexture
                ? resourceData.activeColorTexture
                : renderGraph.defaultResources.blackTexture;
            passData.cameraDepthTexture = resourceData.cameraDepthTexture;
            passData.froxelTexture = froxelTexture;
            passData.gridX = gridX;
            passData.gridY = gridY;
            passData.gridZ = gridZ;
            passData.screenWidth = cameraData.cameraTargetDescriptor.width;
            passData.screenHeight = cameraData.cameraTargetDescriptor.height;
            passData.nearDepth = nearDepth;
            passData.maxDistance = maxDistance;
            passData.scatteringStrength = Mathf.Max(0f, settings.scatteringStrength);
            passData.stepCount = stepCount;
            passData.jitterStrength = Mathf.Clamp01(settings.jitterStrength);
            passData.compositeDepthBias = Mathf.Max(0f, settings.compositeDepthBias);
            passData.debugOpacity = Mathf.Clamp01(settings.debugOpacity);
            passData.inverseProjection = inverseProjection;
            passData.mainLightColor = lightColor;
            passData.debugMode = (int)debugMode;

            builder.UseTexture(passData.sourceTexture, AccessFlags.Read);
            builder.UseTexture(passData.cameraDepthTexture, AccessFlags.Read);
            builder.UseTexture(passData.froxelTexture, AccessFlags.Read);
            if (useIntermediateLightingTexture)
            {
                builder.SetRenderAttachment(lightingTexture, 0, AccessFlags.WriteAll);
            }
            else
            {
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.WriteAll);
            }

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            passData.passIndex = outputIntermediateSolidColor
                ? 6
                : useIntermediateLightingTexture || outputAccumulation
                    ? 2
                    : -1;
            builder.SetRenderFunc(static (RasterPassData data, RasterGraphContext context) => ExecuteRasterPass(data, context));
        }

        if (useIntermediateLightingTexture)
        {
            int finalPassIndex = outputIntermediateTexture ? 4 : outputBlurredTexture ? 5 : 3;
            float baseKawaseIteration = Mathf.Max(0f, settings.kawaseIteration);

            int blurPassCount = 1;
            if (finalPassIndex == 3 && settings.enableKawaseBlur && settings.enableMultipassKawase)
            {
                int requestedPasses = Mathf.Clamp(settings.kawaseBlurPassCount, 1, blurMaterials.Length);
                while (blurPassCount < requestedPasses && blurMaterials[blurPassCount] != null)
                    ++blurPassCount;
            }

            TextureHandle blurSource = lightingTexture;
            if (blurPassCount > 1)
            {
                TextureHandle blurPingPongTexture = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    lightingDescriptor,
                    "VolumetricLighting.KawaseBlurTexture",
                    false,
                    FilterMode.Bilinear);

                for (int blurPassIndex = 0; blurPassIndex < blurPassCount - 1; ++blurPassIndex)
                {
                    TextureHandle blurDestination = (blurPassIndex & 1) == 0 ? blurPingPongTexture : lightingTexture;
                    using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RasterPassData>(
                               "Volumetric Lighting Kawase Blur Iterate",
                               out RasterPassData passData,
                               profilingSampler))
                    {
                        passData.material = blurMaterials[blurPassIndex];
                        passData.sourceTexture = blurSource;
                        passData.lightingTexture = blurSource;
                        passData.screenWidth = cameraData.cameraTargetDescriptor.width;
                        passData.screenHeight = cameraData.cameraTargetDescriptor.height;
                        passData.mainLightColor = lightColor;
                        passData.kawaseIteration = baseKawaseIteration + blurPassIndex;
                        passData.passIndex = 7;

                        builder.UseTexture(passData.sourceTexture, AccessFlags.Read);
                        builder.SetRenderAttachment(blurDestination, 0, AccessFlags.WriteAll);
                        builder.AllowPassCulling(false);
                        builder.AllowGlobalStateModification(true);
                        builder.SetRenderFunc(static (RasterPassData data, RasterGraphContext context) => ExecuteRasterPass(data, context));
                    }

                    blurSource = blurDestination;
                }
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RasterPassData>(
                       outputIntermediateTexture
                           ? "Volumetric Lighting Debug Intermediate"
                           : outputBlurredTexture
                               ? "Volumetric Lighting Debug Blurred"
                               : "Volumetric Lighting Kawase Blur",
                       out RasterPassData passData,
                       profilingSampler))
            {
                passData.material = blurMaterials[blurPassCount - 1];
                passData.sourceTexture = blurSource;
                passData.lightingTexture = blurSource;
                passData.screenWidth = cameraData.cameraTargetDescriptor.width;
                passData.screenHeight = cameraData.cameraTargetDescriptor.height;
                passData.mainLightColor = lightColor;
                passData.kawaseIteration = baseKawaseIteration + (blurPassCount - 1);

                builder.UseTexture(passData.sourceTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                passData.passIndex = finalPassIndex;
                builder.SetRenderFunc(static (RasterPassData data, RasterGraphContext context) => ExecuteRasterPass(data, context));
            }
        }
    }

    static void ExecuteComputePass(ComputePassData data, ComputeGraphContext context)
    {
        if (data.dispatchGroupsX <= 0 || data.dispatchGroupsY <= 0 || data.dispatchGroupsZ <= 0)
            return;

        LocalKeyword directLightingKeyword = new(data.computeShader, VolumetricLightingShaderIDs.DirectLightingKeyword);
        if (data.enableDirectLighting)
            context.cmd.EnableKeyword(data.computeShader, directLightingKeyword);
        else
            context.cmd.DisableKeyword(data.computeShader, directLightingKeyword);

        LocalKeyword additionalLightsKeyword = new(data.computeShader, VolumetricLightingShaderIDs.AdditionalLightsKeyword);
        if (data.enableAdditionalLights)
            context.cmd.EnableKeyword(data.computeShader, additionalLightsKeyword);
        else
            context.cmd.DisableKeyword(data.computeShader, additionalLightsKeyword);

        LocalKeyword volumetricCookiesKeyword = new(data.computeShader, VolumetricLightingShaderIDs.VolumetricCookiesKeyword);
        if (data.cookieDataCount > 0)
            context.cmd.EnableKeyword(data.computeShader, volumetricCookiesKeyword);
        else
            context.cmd.DisableKeyword(data.computeShader, volumetricCookiesKeyword);

        context.cmd.SetComputeTextureParam(
            data.computeShader,
            data.computeKernelIndex,
            VolumetricLightingShaderIDs.CameraDepthTexture,
            data.cameraDepthTexture);
        context.cmd.SetComputeTextureParam(
            data.computeShader,
            data.computeKernelIndex,
            VolumetricLightingShaderIDs.FroxelTexture,
            data.froxelTexture);
        if (data.additionalLightCount > 0)
        {
            context.cmd.SetComputeBufferParam(
                data.computeShader,
                data.computeKernelIndex,
                VolumetricLightingShaderIDs.AdditionalLightsBuffer,
                data.additionalLightsBuffer);
        }
        if (data.cookieDataCount > 0)
        {
            context.cmd.SetComputeTextureParam(
                data.computeShader,
                data.computeKernelIndex,
                VolumetricLightingShaderIDs.CookieAtlas,
                data.cookieAtlasTexture);
            context.cmd.SetComputeBufferParam(
                data.computeShader,
                data.computeKernelIndex,
                VolumetricLightingShaderIDs.CookieDataBuffer,
                data.cookieDataBuffer);
        }
        context.cmd.SetComputeVectorParam(
            data.computeShader,
            VolumetricLightingShaderIDs.GridParams,
            new Vector4(data.gridX, data.gridY, data.gridZ, 0f));
        context.cmd.SetComputeVectorParam(
            data.computeShader,
            VolumetricLightingShaderIDs.ScreenSize,
            new Vector4(data.screenWidth, data.screenHeight, 1f / Mathf.Max(1, data.screenWidth), 1f / Mathf.Max(1, data.screenHeight)));
        context.cmd.SetComputeVectorParam(
            data.computeShader,
            VolumetricLightingShaderIDs.VolumeParams,
            new Vector4(data.nearDepth, data.maxDistance, 0f, 0f));
        context.cmd.SetComputeMatrixParam(
            data.computeShader,
            VolumetricLightingShaderIDs.InverseProjectionMatrix,
            data.inverseProjection);
        context.cmd.SetComputeMatrixParam(
            data.computeShader,
            VolumetricLightingShaderIDs.CameraToWorldMatrix,
            data.cameraToWorld);
        context.cmd.SetComputeVectorParam(
            data.computeShader,
            VolumetricLightingShaderIDs.MainLightDirection,
            new Vector4(data.mainLightDirection.x, data.mainLightDirection.y, data.mainLightDirection.z, 0f));
        context.cmd.SetComputeVectorParam(
            data.computeShader,
            VolumetricLightingShaderIDs.MainLightColor,
            data.mainLightColor);
        context.cmd.SetComputeIntParam(
            data.computeShader,
            VolumetricLightingShaderIDs.AdditionalLightCount,
            data.additionalLightCount);
        context.cmd.SetComputeIntParam(
            data.computeShader,
            VolumetricLightingShaderIDs.CookieDataCount,
            data.cookieDataCount);
        context.cmd.DispatchCompute(
            data.computeShader,
            data.computeKernelIndex,
            data.dispatchGroupsX,
            data.dispatchGroupsY,
            data.dispatchGroupsZ);
    }

    void RecordCookieAtlasUpdatePass(RenderGraph renderGraph, TextureHandle cookieAtlasTexture)
    {
        using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<CookieAtlasPassData>(
                   "Volumetric Lighting Cookie Atlas Update",
                   out CookieAtlasPassData passData,
                   profilingSampler))
        {
            passData.atlas = cookieAtlas;
            passData.atlasTexture = cookieAtlasTexture;
            passData.cookieTextures = uniqueCookieTextures;
            passData.atlasRects = uniqueCookieAtlasRects;
            passData.cookieCount = activeUniqueCookieCount;
            passData.clearAtlas = cookieAtlasNeedsClear;

            builder.UseTexture(passData.atlasTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (CookieAtlasPassData data, UnsafeGraphContext context) =>
            {
                CommandBuffer commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                if (data.clearAtlas)
                    data.atlas.ClearTarget(commandBuffer);

                for (int cookieIndex = 0; cookieIndex < data.cookieCount; ++cookieIndex)
                {
                    Texture2D cookieTexture = data.cookieTextures[cookieIndex];
                    if (cookieTexture == null || data.atlasRects[cookieIndex] == Vector4.zero)
                        continue;

                    data.atlas.UpdateTexture(
                        commandBuffer,
                        cookieTexture,
                        ref data.atlasRects[cookieIndex],
                        true,
                        false);
                }
            });
        }

        cookieAtlasNeedsClear = false;
    }

    int UpdateAdditionalLightsBuffer(UniversalLightData lightData, out int cookieDataCount)
    {
        int additionalVisibleLightIndex = 0;
        int bufferLightCount = 0;

        for (int visibleLightIndex = 0; visibleLightIndex < lightData.visibleLights.Length; ++visibleLightIndex)
        {
            if (visibleLightIndex == lightData.mainLightIndex)
                continue;

            VisibleLight visibleLight = lightData.visibleLights[visibleLightIndex];
            Light light = visibleLight.light;
            int shadowLightIndex = additionalVisibleLightIndex++;

            if (light == null)
                continue;

            if (!light.TryGetComponent(out VolumetricLight volumetricLight) || !volumetricLight.ShouldProcess)
                continue;

            if (visibleLight.lightType != LightType.Point && visibleLight.lightType != LightType.Spot)
                continue;

            EnsureAdditionalLightsCpuCapacity(bufferLightCount + 1);
            additionalLightsCpuData[bufferLightCount] = CreateAdditionalLightGpuData(visibleLight, light, shadowLightIndex);
            additionalLightCookieTextures[bufferLightCount] = visibleLight.lightType == LightType.Spot
                && volumetricLight.applyVolumetricCookie
                ? volumetricLight.volumetricCookie
                : null;
            additionalLightCookieMatrices[bufferLightCount] = additionalLightCookieTextures[bufferLightCount] != null
                ? CreateSpotCookieMatrix(visibleLight)
                : Matrix4x4.identity;
            ++bufferLightCount;
        }

        if (bufferLightCount <= 0)
        {
            cookieDataCount = 0;
            activeUniqueCookieCount = 0;
            return 0;
        }

        cookieDataCount = PrepareCookieData(bufferLightCount);

        int stride = Marshal.SizeOf<AdditionalLightGpuData>();
        if (additionalLightsBuffer == null || additionalLightsBuffer.count < bufferLightCount || additionalLightsBuffer.stride != stride)
        {
            additionalLightsBuffer?.Release();
            additionalLightsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferLightCount, stride);
        }

        additionalLightsBuffer.SetData(additionalLightsCpuData, 0, 0, bufferLightCount);
        return bufferLightCount;
    }

    void EnsureAdditionalLightsCpuCapacity(int count)
    {
        if (additionalLightsCpuData.Length >= count)
            return;

        int newSize = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
        additionalLightsCpuData = new AdditionalLightGpuData[newSize];
        additionalLightCookieTextures = new Texture2D[newSize];
        additionalLightCookieMatrices = new Matrix4x4[newSize];
    }

    int PrepareCookieData(int lightCount)
    {
        activeUniqueCookieCount = BuildUniqueCookieList(lightCount);
        if (activeUniqueCookieCount <= 0)
            return 0;

        EnsureCookieAtlas(settings.cookieAtlasResolution);
        cookieAtlasNeedsClear = false;

        long cookieRequestPixelCount = GetCookieRequestPixelCount();
        long atlasPixelCount = (long)cookieAtlasResolution * cookieAtlasResolution;
        int approximateDivisor = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(cookieRequestPixelCount / (float)atlasPixelCount)));
        bool canRecoverResolution = cookieSizeDivisor > approximateDivisor
            && cookieRequestPixelCount < previousCookieRequestPixelCount;
        if (canRecoverResolution || !TryAllocateCookies(cookieSizeDivisor, true))
            RepackActiveCookies();
        previousCookieRequestPixelCount = cookieRequestPixelCount;

        int cookieDataCount = 0;
        int failedCookieLightCount = 0;
        EnsureCookieDataCpuCapacity(lightCount);

        for (int lightIndex = 0; lightIndex < lightCount; ++lightIndex)
        {
            Texture2D cookieTexture = additionalLightCookieTextures[lightIndex];
            if (cookieTexture == null)
                continue;

            int uniqueCookieIndex = FindUniqueCookieIndex(cookieTexture);
            if (uniqueCookieIndex < 0 || uniqueCookieAtlasRects[uniqueCookieIndex] == Vector4.zero)
            {
                ++failedCookieLightCount;
                continue;
            }

            cookieDataCpuData[cookieDataCount] = new VolumetricCookieGpuData
            {
                worldToCookie = additionalLightCookieMatrices[lightIndex],
                atlasRect = ShrinkCookieAtlasRect(
                    uniqueCookieAtlasRects[uniqueCookieIndex],
                    uniqueCookieAtlasSizes[uniqueCookieIndex])
            };

            AdditionalLightGpuData lightData = additionalLightsCpuData[lightIndex];
            lightData.extraData.z = cookieDataCount;
            additionalLightsCpuData[lightIndex] = lightData;
            ++cookieDataCount;
        }

        if (failedCookieLightCount > 0 && Time.frameCount - lastCookieWarningFrame >= CookieWarningIntervalFrames)
        {
            Debug.LogWarning(
                $"Volumetric cookie atlas ({cookieAtlasResolution}x{cookieAtlasResolution}) could not fit "
                + $"{failedCookieLightCount} visible spot light cookie(s). Those lights will render unmodulated.");
            lastCookieWarningFrame = Time.frameCount;
        }

        if (cookieDataCount <= 0)
            return 0;

        int stride = Marshal.SizeOf<VolumetricCookieGpuData>();
        if (cookieDataBuffer == null || cookieDataBuffer.count < cookieDataCount || cookieDataBuffer.stride != stride)
        {
            cookieDataBuffer?.Release();
            cookieDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cookieDataCount, stride);
        }

        cookieDataBuffer.SetData(cookieDataCpuData, 0, 0, cookieDataCount);
        return cookieDataCount;
    }

    int BuildUniqueCookieList(int lightCount)
    {
        int uniqueCount = 0;
        EnsureUniqueCookieCapacity(lightCount);

        for (int lightIndex = 0; lightIndex < lightCount; ++lightIndex)
        {
            Texture2D cookieTexture = additionalLightCookieTextures[lightIndex];
            if (cookieTexture == null)
                continue;

            bool alreadyAdded = false;
            for (int cookieIndex = 0; cookieIndex < uniqueCount; ++cookieIndex)
            {
                if (uniqueCookieTextures[cookieIndex] == cookieTexture)
                {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded)
                uniqueCookieTextures[uniqueCount++] = cookieTexture;
        }

        // Larger textures first gives the static atlas allocator a more reliable packing order.
        for (int cookieIndex = 1; cookieIndex < uniqueCount; ++cookieIndex)
        {
            Texture2D texture = uniqueCookieTextures[cookieIndex];
            long area = (long)texture.width * texture.height;
            int insertionIndex = cookieIndex;
            while (insertionIndex > 0)
            {
                Texture2D previous = uniqueCookieTextures[insertionIndex - 1];
                long previousArea = (long)previous.width * previous.height;
                if (previousArea > area
                    || (previousArea == area && previous.GetInstanceID() <= texture.GetInstanceID()))
                {
                    break;
                }

                uniqueCookieTextures[insertionIndex] = previous;
                --insertionIndex;
            }
            uniqueCookieTextures[insertionIndex] = texture;
        }

        return uniqueCount;
    }

    bool TryAllocateCookies(int sizeDivisor, bool reuseCachedAllocations)
    {
        bool allocatedAll = true;
        for (int cookieIndex = 0; cookieIndex < activeUniqueCookieCount; ++cookieIndex)
        {
            Texture2D cookieTexture = uniqueCookieTextures[cookieIndex];
            Vector2Int allocationSize = GetCookieAllocationSize(cookieTexture, sizeDivisor);
            uniqueCookieAtlasSizes[cookieIndex] = allocationSize;

            Vector4 atlasRect = Vector4.zero;
            bool allocated = reuseCachedAllocations && cookieAtlas.IsCached(out atlasRect, cookieTexture);
            if (!allocated)
            {
                allocated = cookieAtlas.AllocateTextureWithoutBlit(
                    cookieTexture,
                    allocationSize.x,
                    allocationSize.y,
                    ref atlasRect);
            }

            uniqueCookieAtlasRects[cookieIndex] = allocated ? atlasRect : Vector4.zero;
            allocatedAll &= allocated;
        }

        return allocatedAll;
    }

    long GetCookieRequestPixelCount()
    {
        long pixelCount = 0;
        for (int cookieIndex = 0; cookieIndex < activeUniqueCookieCount; ++cookieIndex)
        {
            Texture2D cookieTexture = uniqueCookieTextures[cookieIndex];
            Vector2Int allocationSize = GetCookieAllocationSize(cookieTexture, 1);
            pixelCount += (long)allocationSize.x * allocationSize.y;
        }

        return pixelCount;
    }

    void RepackActiveCookies()
    {
        bool packedAll = false;
        for (int divisor = 1; divisor <= MaxCookieSizeDivisor; ++divisor)
        {
            cookieAtlas.ResetAllocator();
            cookieAtlasNeedsClear = true;
            if (!TryAllocateCookies(divisor, false))
                continue;

            cookieSizeDivisor = divisor;
            packedAll = true;
            break;
        }

        if (packedAll)
            return;

        // Retain as many deterministic, largest-first allocations as possible at the minimum size.
        cookieAtlas.ResetAllocator();
        cookieAtlasNeedsClear = true;
        cookieSizeDivisor = MaxCookieSizeDivisor;
        TryAllocateCookies(cookieSizeDivisor, false);
    }

    void EnsureCookieAtlas(int requestedResolution)
    {
        int normalizedResolution = Mathf.NextPowerOfTwo(Mathf.Clamp(
            requestedResolution,
            MinCookieAtlasResolution,
            MaxCookieAtlasResolution));
        if (cookieAtlas != null && cookieAtlasResolution == normalizedResolution)
            return;

        cookieAtlas?.Release();
        cookieAtlasResolution = normalizedResolution;
        cookieSizeDivisor = 1;
        previousCookieRequestPixelCount = long.MaxValue;
        cookieAtlas = new Texture2DAtlas(
            cookieAtlasResolution,
            cookieAtlasResolution,
            GraphicsFormat.R8G8B8A8_SRGB,
            FilterMode.Bilinear,
            false,
            "VolumetricLighting.CookieAtlas",
            false);
    }

    void EnsureCookieDataCpuCapacity(int count)
    {
        if (cookieDataCpuData.Length >= count)
            return;

        cookieDataCpuData = new VolumetricCookieGpuData[Mathf.NextPowerOfTwo(Mathf.Max(1, count))];
    }

    void EnsureUniqueCookieCapacity(int count)
    {
        if (uniqueCookieTextures.Length >= count)
            return;

        int newSize = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
        uniqueCookieTextures = new Texture2D[newSize];
        uniqueCookieAtlasRects = new Vector4[newSize];
        uniqueCookieAtlasSizes = new Vector2Int[newSize];
    }

    int FindUniqueCookieIndex(Texture2D cookieTexture)
    {
        for (int cookieIndex = 0; cookieIndex < activeUniqueCookieCount; ++cookieIndex)
        {
            if (uniqueCookieTextures[cookieIndex] == cookieTexture)
                return cookieIndex;
        }

        return -1;
    }

    Vector2Int GetCookieAllocationSize(Texture2D cookieTexture, int sizeDivisor)
    {
        float sourceWidth = Mathf.Max(1f, cookieTexture.width);
        float sourceHeight = Mathf.Max(1f, cookieTexture.height);
        float fitScale = Mathf.Min(1f, cookieAtlasResolution / Mathf.Max(sourceWidth, sourceHeight));
        float allocationScale = fitScale / Mathf.Max(1, sizeDivisor);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(sourceWidth * allocationScale), 4, cookieAtlasResolution),
            Mathf.Clamp(Mathf.RoundToInt(sourceHeight * allocationScale), 4, cookieAtlasResolution));
    }

    static Vector4 ShrinkCookieAtlasRect(Vector4 atlasRect, Vector2Int allocationSize)
    {
        float offsetX = 0.5f / Mathf.Max(1, allocationSize.x);
        float offsetY = 0.5f / Mathf.Max(1, allocationSize.y);
        atlasRect.z += atlasRect.x * offsetX;
        atlasRect.w += atlasRect.y * offsetY;
        atlasRect.x *= Mathf.Max(0f, 1f - 2f * offsetX);
        atlasRect.y *= Mathf.Max(0f, 1f - 2f * offsetY);
        return atlasRect;
    }

    static Matrix4x4 CreateSpotCookieMatrix(in VisibleLight visibleLight)
    {
        Matrix4x4 projection = Matrix4x4.Perspective(
            visibleLight.spotAngle,
            1f,
            0.001f,
            Mathf.Max(0.002f, visibleLight.range));
        projection.SetColumn(2, projection.GetColumn(2) * -1f);
        return projection * visibleLight.localToWorldMatrix.inverse;
    }

    static bool ShouldProcessLight(Light light)
    {
        return light != null
            && light.TryGetComponent(out VolumetricLight volumetricLight)
            && volumetricLight.ShouldProcess;
    }

    static AdditionalLightGpuData CreateAdditionalLightGpuData(in VisibleLight visibleLight, Light light, int shadowLightIndex)
    {
        Matrix4x4 lightLocalToWorld = visibleLight.localToWorldMatrix;
        Vector4 lightPosition = lightLocalToWorld.GetColumn(3);
        Vector4 lightColor = visibleLight.finalColor;
        Vector4 lightAttenuation = Vector4.zero;
        Vector4 lightSpotDirection = Vector4.zero;

        float lightRangeSqr = light.range * light.range;
        float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
        float fadeRangeSqr = fadeStartDistanceSqr - lightRangeSqr;
        lightAttenuation.x = 1.0f / Mathf.Max(0.0001f, lightRangeSqr);
        lightAttenuation.y = -lightRangeSqr / fadeRangeSqr;

        float isSpotLight = visibleLight.lightType == LightType.Spot ? 1.0f : 0.0f;
        if (visibleLight.lightType == LightType.Spot)
        {
            float spotAngle = light.spotAngle;
            float? innerSpotAngle = light.innerSpotAngle;
            if (spotAngle < 2.6f)
            {
                spotAngle = 2.6f;
                innerSpotAngle = Mathf.Min(innerSpotAngle.Value, 2.6f);
            }

            float cosOuterAngle = Mathf.Cos(Mathf.Deg2Rad * spotAngle * 0.5f);
            float cosInnerAngle = Mathf.Cos(innerSpotAngle.Value * Mathf.Deg2Rad * 0.5f);
            float smoothAngleRange = Mathf.Max(0.001f, cosInnerAngle - cosOuterAngle);
            float invAngleRange = 1.0f / smoothAngleRange;

            lightAttenuation.z = invAngleRange;
            lightAttenuation.w = -cosOuterAngle * invAngleRange;

            Vector4 dir = lightLocalToWorld.GetColumn(2);
            lightSpotDirection = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);
        }

        return new AdditionalLightGpuData
        {
            position = new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f),
            color = lightColor,
            attenuation = lightAttenuation,
            spotDirection = lightSpotDirection,
            extraData = new Vector4(shadowLightIndex, isSpotLight, -1.0f, 0.0f)
        };
    }

    static void ExecuteRasterPass(RasterPassData data, RasterGraphContext context)
    {
        if (data.cameraDepthTexture.IsValid())
            data.material.SetTexture(VolumetricLightingShaderIDs.CameraDepthTexture, data.cameraDepthTexture);
        if (data.froxelTexture.IsValid())
            data.material.SetTexture(VolumetricLightingShaderIDs.FroxelTexture, data.froxelTexture);
        if (data.lightingTexture.IsValid())
            data.material.SetTexture(VolumetricLightingShaderIDs.LightingTexture, data.lightingTexture);

        data.material.SetVector(VolumetricLightingShaderIDs.GridParams, new Vector4(data.gridX, data.gridY, data.gridZ, 0f));
        data.material.SetVector(
            VolumetricLightingShaderIDs.ScreenSize,
            new Vector4(data.screenWidth, data.screenHeight, 1f / Mathf.Max(1, data.screenWidth), 1f / Mathf.Max(1, data.screenHeight)));
        data.material.SetVector(VolumetricLightingShaderIDs.VolumeParams, new Vector4(data.nearDepth, data.maxDistance, 0f, 0f));
        data.material.SetMatrix(VolumetricLightingShaderIDs.InverseProjectionMatrix, data.inverseProjection);
        data.material.SetColor(VolumetricLightingShaderIDs.MainLightColor, data.mainLightColor);
        data.material.SetFloat(VolumetricLightingShaderIDs.ScatteringStrength, data.scatteringStrength);
        data.material.SetFloat(VolumetricLightingShaderIDs.JitterStrength, data.jitterStrength);
        data.material.SetFloat(VolumetricLightingShaderIDs.CompositeDepthBias, data.compositeDepthBias);
        data.material.SetFloat(VolumetricLightingShaderIDs.DebugOpacity, data.debugOpacity);
        data.material.SetFloat(VolumetricLightingShaderIDs.KawaseIteration, data.kawaseIteration);
        data.material.SetInt(VolumetricLightingShaderIDs.StepCount, data.stepCount);

        int passIndex = data.passIndex >= 0
            ? data.passIndex
            : (data.debugMode == (int)VolumetricLightingRendererFeature.DebugMode.FroxelOverlay ? 1 : 0);
        Blitter.BlitTexture(context.cmd, data.sourceTexture, Vector2.one, data.material, passIndex);
    }

    sealed class ComputePassData
    {
        internal ComputeShader computeShader;
        internal BufferHandle additionalLightsBuffer;
        internal BufferHandle cookieDataBuffer;
        internal TextureHandle cameraDepthTexture;
        internal TextureHandle froxelTexture;
        internal TextureHandle cookieAtlasTexture;
        internal Matrix4x4 inverseProjection;
        internal Matrix4x4 cameraToWorld;
        internal Vector3 mainLightDirection;
        internal Color mainLightColor;
        internal int computeKernelIndex;
        internal int gridX;
        internal int gridY;
        internal int gridZ;
        internal int screenWidth;
        internal int screenHeight;
        internal int dispatchGroupsX;
        internal int dispatchGroupsY;
        internal int dispatchGroupsZ;
        internal int additionalLightCount;
        internal int cookieDataCount;
        internal bool enableDirectLighting;
        internal bool enableAdditionalLights;
        internal float nearDepth;
        internal float maxDistance;
    }

    sealed class CookieAtlasPassData
    {
        internal Texture2DAtlas atlas;
        internal TextureHandle atlasTexture;
        internal Texture2D[] cookieTextures;
        internal Vector4[] atlasRects;
        internal int cookieCount;
        internal bool clearAtlas;
    }

    sealed class RasterPassData
    {
        internal Material material;
        internal TextureHandle sourceTexture;
        internal TextureHandle cameraDepthTexture;
        internal TextureHandle froxelTexture;
        internal TextureHandle lightingTexture;
        internal Matrix4x4 inverseProjection;
        internal Color mainLightColor;
        internal int gridX;
        internal int gridY;
        internal int gridZ;
        internal int screenWidth;
        internal int screenHeight;
        internal int stepCount;
        internal int debugMode;
        internal int passIndex;
        internal float nearDepth;
        internal float maxDistance;
        internal float scatteringStrength;
        internal float jitterStrength;
        internal float compositeDepthBias;
        internal float debugOpacity;
        internal float kawaseIteration;
    }
}
