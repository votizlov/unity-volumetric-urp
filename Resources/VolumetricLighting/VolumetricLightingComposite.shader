Shader "Hidden/VolumetricLighting/Composite"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            Name "Composite"
            Blend One One
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE3D(_VolumetricLightingFroxelTexture);
            TEXTURE2D_X(_VolumetricLightingTexture);

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingGridParams;
            float4 _VolumetricLightingScreenSize;
            float4 _VolumetricLightingVolumeParams;
            float4x4 _VolumetricLightingInverseProjectionMatrix;
            float4 _VolumetricLightingMainLightColor;
            float _VolumetricLightingScatteringStrength;
            float _VolumetricLightingJitterStrength;
            float _VolumetricLightingCompositeDepthBias;
            float _VolumetricLightingDebugOpacity;
            float _VolumetricLightingKawaseIteration;
            int _VolumetricLightingStepCount;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            float VolumetricLighting_LoadSceneDepth(float2 uv)
            {
                float rawDepth = SampleSceneDepth(VolumetricLighting_GetDepthUv(uv));
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                if (!isfinite(sceneDepth) || sceneDepth <= 0.0)
                    sceneDepth = _VolumetricLightingVolumeParams.y;
                return min(sceneDepth, _VolumetricLightingVolumeParams.y);
            }

            float3 VolumetricLighting_AccumulateLighting(float2 uv, float sceneDepth)
            {
                float nearDepth = _VolumetricLightingVolumeParams.x;
                float maxDistance = _VolumetricLightingVolumeParams.y;
                float rayEnd = min(max(sceneDepth - _VolumetricLightingCompositeDepthBias, 0.0), maxDistance);
                if (rayEnd <= nearDepth)
                    return 0.0.xxx;

                int stepCount = max(_VolumetricLightingStepCount, 1);
                float stepLength = (rayEnd - nearDepth) / stepCount;
                float jitter = (VolumetricLighting_InterleavedGradientNoise(uv * _VolumetricLightingScreenSize.xy) - 0.5) * _VolumetricLightingJitterStrength;
                float3 accumulated = 0.0.xxx;

                [loop]
                for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
                {
                    float sampleDepth = nearDepth + (stepIndex + 0.5 + jitter) * stepLength;
                    float slice01 = VolumetricLighting_LinearDepthToSlice01(sampleDepth, nearDepth, maxDistance);
                    float3 sampleCoord = float3(uv, slice01);
                    float3 lighting = SAMPLE_TEXTURE3D_LOD(_VolumetricLightingFroxelTexture, sampler_LinearClamp, sampleCoord, 0).rgb;
                    accumulated += lighting * stepLength;
                }

                return accumulated;
            }

            float3 VolumetricLighting_Accumulate(float2 uv, float sceneDepth)
            {
                float maxDistance = _VolumetricLightingVolumeParams.y;
                float3 lighting = VolumetricLighting_AccumulateLighting(uv, sceneDepth);
                return lighting * (_VolumetricLightingScatteringStrength / max(maxDistance, 0.001));
            }

            half3 KawaseBlurFilter(half2 texCoord, half2 pixelSize, half iteration)
            {
                half2 texCoordSample;
                half2 halfPixelSize = pixelSize * 0.5h;
                half2 dUV = (pixelSize.xy * half2(iteration, iteration)) + halfPixelSize.xy;

                half3 cOut;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut = SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                cOut *= 0.25h;
                return cOut;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                float sceneDepth = VolumetricLighting_LoadSceneDepth(input.uv);
                float3 volumetric = VolumetricLighting_Accumulate(input.uv, sceneDepth);
                return half4(volumetric, 1.0);
            }

            half4 FragAccumulateScalar(Varyings input) : SV_Target
            {
                float sceneDepth = VolumetricLighting_LoadSceneDepth(input.uv);
                half3 volumetric = (half3)VolumetricLighting_Accumulate(input.uv, sceneDepth);
                return half4(volumetric, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugOverlay"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebug
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingGridParams;
            float4 _VolumetricLightingScreenSize;
            float4 _VolumetricLightingVolumeParams;
            float _VolumetricLightingDebugOpacity;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half4 FragDebug(Varyings input) : SV_Target
            {
                float rawDepth = SampleSceneDepth(VolumetricLighting_GetDepthUv(input.uv));
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                if (!isfinite(sceneDepth) || sceneDepth <= 0.0)
                    sceneDepth = _VolumetricLightingVolumeParams.y;
                sceneDepth = min(sceneDepth, _VolumetricLightingVolumeParams.y);

                int3 froxelCoord;
                froxelCoord.xy = min(int2(input.uv * _VolumetricLightingGridParams.xy), int2(_VolumetricLightingGridParams.xy) - 1);
                froxelCoord.z = min(
                    (int)floor(VolumetricLighting_LinearDepthToSlice01(sceneDepth, _VolumetricLightingVolumeParams.x, _VolumetricLightingVolumeParams.y) * _VolumetricLightingGridParams.z),
                    (int)_VolumetricLightingGridParams.z - 1);

                float3 color = VolumetricLighting_HashColor(froxelCoord);
                return half4(color, _VolumetricLightingDebugOpacity);
            }
            ENDHLSL
        }

        Pass
        {
            Name "AccumulateScalar"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAccumulateScalar
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE3D(_VolumetricLightingFroxelTexture);
            TEXTURE2D_X(_VolumetricLightingTexture);

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingGridParams;
            float4 _VolumetricLightingScreenSize;
            float4 _VolumetricLightingVolumeParams;
            float4x4 _VolumetricLightingInverseProjectionMatrix;
            float4 _VolumetricLightingMainLightColor;
            float _VolumetricLightingScatteringStrength;
            float _VolumetricLightingJitterStrength;
            float _VolumetricLightingCompositeDepthBias;
            float _VolumetricLightingDebugOpacity;
            float _VolumetricLightingKawaseIteration;
            int _VolumetricLightingStepCount;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            float VolumetricLighting_LoadSceneDepth(float2 uv)
            {
                float rawDepth = SampleSceneDepth(VolumetricLighting_GetDepthUv(uv));
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                if (!isfinite(sceneDepth) || sceneDepth <= 0.0)
                    sceneDepth = _VolumetricLightingVolumeParams.y;
                return min(sceneDepth, _VolumetricLightingVolumeParams.y);
            }

            float3 VolumetricLighting_AccumulateLighting(float2 uv, float sceneDepth)
            {
                float nearDepth = _VolumetricLightingVolumeParams.x;
                float maxDistance = _VolumetricLightingVolumeParams.y;
                float rayEnd = min(max(sceneDepth - _VolumetricLightingCompositeDepthBias, 0.0), maxDistance);
                if (rayEnd <= nearDepth)
                    return 0.0.xxx;

                int stepCount = max(_VolumetricLightingStepCount, 1);
                float stepLength = (rayEnd - nearDepth) / stepCount;
                float jitter = (VolumetricLighting_InterleavedGradientNoise(uv * _VolumetricLightingScreenSize.xy) - 0.5) * _VolumetricLightingJitterStrength;
                float3 accumulated = 0.0.xxx;

                [loop]
                for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
                {
                    float sampleDepth = nearDepth + (stepIndex + 0.5 + jitter) * stepLength;
                    float slice01 = VolumetricLighting_LinearDepthToSlice01(sampleDepth, nearDepth, maxDistance);
                    float3 sampleCoord = float3(uv, slice01);
                    accumulated += SAMPLE_TEXTURE3D_LOD(_VolumetricLightingFroxelTexture, sampler_LinearClamp, sampleCoord, 0).rgb * stepLength;
                }

                return accumulated;
            }

            float3 VolumetricLighting_Accumulate(float2 uv, float sceneDepth)
            {
                float maxDistance = _VolumetricLightingVolumeParams.y;
                float3 lighting = VolumetricLighting_AccumulateLighting(uv, sceneDepth);
                return lighting * (_VolumetricLightingScatteringStrength / max(maxDistance, 0.001));
            }

            half4 FragAccumulateScalar(Varyings input) : SV_Target
            {
                float sceneDepth = VolumetricLighting_LoadSceneDepth(input.uv);
                half3 volumetric = (half3)VolumetricLighting_Accumulate(input.uv, sceneDepth);
                return half4(volumetric, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "KawaseBlurComposite"
            Blend One One
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragKawaseComposite
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE2D_X(_VolumetricLightingTexture);

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingGridParams;
            float4 _VolumetricLightingScreenSize;
            float4 _VolumetricLightingVolumeParams;
            float4x4 _VolumetricLightingInverseProjectionMatrix;
            float4 _VolumetricLightingMainLightColor;
            float _VolumetricLightingScatteringStrength;
            float _VolumetricLightingJitterStrength;
            float _VolumetricLightingCompositeDepthBias;
            float _VolumetricLightingDebugOpacity;
            float _VolumetricLightingKawaseIteration;
            int _VolumetricLightingStepCount;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half3 KawaseBlurFilter(half2 texCoord, half2 pixelSize, half iteration)
            {
                half2 texCoordSample;
                half2 halfPixelSize = pixelSize * 0.5h;
                half2 dUV = (pixelSize.xy * half2(iteration, iteration)) + halfPixelSize.xy;

                half3 cOut;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut = SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                cOut *= 0.25h;
                return cOut;
            }

            half4 FragKawaseComposite(Varyings input) : SV_Target
            {
                half2 pixelSize = half2(_VolumetricLightingScreenSize.z, _VolumetricLightingScreenSize.w);
                // The lighting texture was rasterized with the same fullscreen triangle, so sampling
                // it needs the same top/bottom flip as the depth texture on UNITY_UV_STARTS_AT_TOP.
                half3 blurred = KawaseBlurFilter((half2)VolumetricLighting_GetDepthUv(input.uv), pixelSize, (half)_VolumetricLightingKawaseIteration);
                return half4(blurred, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugLightingTexture"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebugLightingTexture
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE2D_X(_VolumetricLightingTexture);

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half4 FragDebugLightingTexture(Varyings input) : SV_Target
            {
                half3 value = SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, VolumetricLighting_GetDepthUv(input.uv)).rgb;
                return half4(value, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugBlurredLightingTexture"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebugBlurredLightingTexture
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE2D_X(_VolumetricLightingTexture);

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingScreenSize;
            float _VolumetricLightingKawaseIteration;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half3 KawaseBlurFilter(half2 texCoord, half2 pixelSize, half iteration)
            {
                half2 texCoordSample;
                half2 halfPixelSize = pixelSize * 0.5h;
                half2 dUV = (pixelSize.xy * half2(iteration, iteration)) + halfPixelSize.xy;

                half3 cOut;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut = SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                cOut *= 0.25h;
                return cOut;
            }

            half4 FragDebugBlurredLightingTexture(Varyings input) : SV_Target
            {
                half2 pixelSize = half2(_VolumetricLightingScreenSize.z, _VolumetricLightingScreenSize.w);
                half3 blurred = KawaseBlurFilter((half2)VolumetricLighting_GetDepthUv(input.uv), pixelSize, (half)_VolumetricLightingKawaseIteration);
                return half4(blurred, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugIntermediateSolidColor"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebugIntermediateSolidColor
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half4 FragDebugIntermediateSolidColor(Varyings input) : SV_Target
            {
                return half4(1.0h, 0.0h, 0.0h, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "KawaseBlurIterate"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragKawaseIterate
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Packages/VolumetricLighting/Resources/VolumetricLighting/VolumetricLightingCommon.hlsl"
            TEXTURE2D_X(_VolumetricLightingTexture);

            CBUFFER_START(UnityPerMaterial)
            float4 _VolumetricLightingScreenSize;
            float _VolumetricLightingKawaseIteration;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = VolumetricLighting_GetFullscreenTrianglePositionCS(input.vertexID);
                output.uv = VolumetricLighting_GetFullscreenTriangleUV(input.vertexID);
                return output;
            }

            half3 KawaseBlurFilter(half2 texCoord, half2 pixelSize, half iteration)
            {
                half2 texCoordSample;
                half2 halfPixelSize = pixelSize * 0.5h;
                half2 dUV = (pixelSize.xy * half2(iteration, iteration)) + halfPixelSize.xy;

                half3 cOut;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut = SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y + dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x + dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                texCoordSample.x = texCoord.x - dUV.x;
                texCoordSample.y = texCoord.y - dUV.y;
                cOut += SAMPLE_TEXTURE2D_X(_VolumetricLightingTexture, sampler_LinearClamp, texCoordSample).rgb;

                cOut *= 0.25h;
                return cOut;
            }

            half4 FragKawaseIterate(Varyings input) : SV_Target
            {
                half2 pixelSize = half2(_VolumetricLightingScreenSize.z, _VolumetricLightingScreenSize.w);
                // Read/write flip cancels out per iteration: reading with the flip and writing with the
                // fullscreen triangle keeps the chained texture in the same orientation as the source.
                half3 blurred = KawaseBlurFilter((half2)VolumetricLighting_GetDepthUv(input.uv), pixelSize, (half)_VolumetricLightingKawaseIteration);
                return half4(blurred, 1.0h);
            }
            ENDHLSL
        }

    }
}
