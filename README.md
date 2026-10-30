# Unity Volumetric URP

![Volumetric lighting demo](screenshot1.png)

Compact volumetric lighting system for the Universal Render Pipeline.

Designed to add visible light beams, atmospheric depth, and shadowed volumetric response in URP scenes.

## Features

- Main directional light volumetrics
- Additional point and spot light volumetrics
- Independent RGB volumetric cookies for selected spot lights
- Shadowed light scattering
- Per-light opt-in control through `Runtime/VolumetricLight.cs`
- Independent toggles for direct lighting and additional lights
- Debug views and optional Kawase blur

## Uniform distance fog

Enable `enableUniformFog` on the renderer feature to replace light scattering with
uniform additive white fog. It is disabled by default. `uniformFogDensity` controls
brightness per world unit independently of `scatteringStrength`; `maxDistance` caps
the fog distance. The `_VL_UNIFORM_FOG` shader variant integrates distance analytically
and skips froxel updates, light cookies and all shadow-map sampling. It works without
any enabled lights and with the existing accumulation/debug and blur paths.

For local environment transitions, call
`VolumetricLightingRendererFeature.SetUniformFogOverride(camera, density)` and
`ClearUniformFogOverride(camera)` when leaving or disabling the environment controller.
Overrides affect only that camera and never modify the renderer asset.

## Setup

1. Add `VolumetricLightingRendererFeature` to your URP renderer.
2. Configure the renderer feature settings in the inspector.
3. Add `VolumetricLight` to every `Light` that should affect volumetrics.
4. For the sun or other primary directional light, keep `enableDirectLighting` enabled.
5. For point and spot lights, enable `enableAdditionalLights`.

Lights without `VolumetricLight` are ignored by the volumetric pass.

## Renderer Feature Settings

`VolumetricLightingRendererFeature.Settings` includes:

- `gridX`, `gridY`, `gridZ` for froxel resolution
- `nearOffset` and `maxDistance` for volume depth range
- `scatteringStrength`, `stepCount`, and `jitterStrength` for sampling quality
- `compositeDepthBias` for compositing control
- `enableDirectLighting` to allow or disable main directional light scattering
- `enableAdditionalLights` to allow or disable point and spot light scattering
- `cookieAtlasResolution` for the package-owned volumetric cookie atlas (defaults to 1024)
- `enableKawaseBlur` and `kawaseIteration` for post blur
- `debugMode` and `debugOpacity` for visualization
- `renderPassEvent` for integration timing

## Per-Light Control

Attach `VolumetricLight` to any light you want processed by the volumetric system.

- `processVolumetricLighting = true` includes the light
- `processVolumetricLighting = false` excludes the light
- disabling the component also excludes the light

## Volumetric Spot Light Cookies

Volumetric cookies are independent from Unity's `Light.cookie`, so the pattern in the
volume can differ from the pattern used for surface lighting.

1. Select a spot light with a `VolumetricLight` component.
2. Enable **Apply Cookie** in the **Volumetric Cookie** section.
3. Assign an RGB `Texture2D` to **Cookie**.

The toggle is disabled by default. Point and directional lights ignore these fields.
An enabled light with no assigned texture continues to render without cookie modulation.

Visible volumetric cookies share a bilinear, mipless atlas owned by the renderer feature.
The atlas resolution is rounded to a power of two between 128 and 4096. When the active
textures do not fit, the system repacks and uniformly downsizes them. If the atlas still
overflows, affected lights remain visible without cookie modulation and a throttled warning
is logged. Increase `cookieAtlasResolution` when cookie detail or capacity is insufficient.
