# TIC Current Architecture Audit

Date: 2026-09-21  
Scope: Read-only architecture audit of the current Thermal Imaging Camera (TIC) implementation. No implementation changes were made.

## Executive summary

The current TIC is a second Unity camera rendering into a fixed 1024×1024 RenderTexture through a dedicated URP renderer. That renderer first produces a normal color view of the scene, then runs a custom Render Graph feature that builds a temperature mask, expands and blurs it, and composites a white-hot presentation over the camera image. The RenderTexture is displayed on the TIC screen mesh.

There are two distinct heat representations in the current implementation:

1. **Object/surface temperature:** `HazardTemperature` stores one normalized temperature per component and writes it to each eligible child renderer through a MaterialPropertyBlock. A mask shader can vary that value spatially around a world-space heat origin, but the underlying stored value is still object-level.
2. **Direct-fire screen field:** the renderer feature projects up to 16 active fire sources into viewport space. The final composite draws a radial field around each projected point, independent of scene geometry and depth.

The reported bullseye and broad white/color blocks are direct consequences of the second representation. The final composite explicitly maps the hottest center of each projected radial field to white, with yellow, orange, and red bands farther out. Because this field is screen-space and has no scene-depth or occlusion test, it can color foreground cabinetry, counters, walls, and multiple meshes around a fire even when those surfaces are not the heat source. The field uses `max`, not addition, so overlap between thermal sources is not numerically accumulating heat; however, overlapping additive flame/ember VFX can already saturate the source image before the TIC composite, and several later saturation operations can create flat white plateaus.

The implementation is structurally compatible with Unity 6 URP Render Graph, and the recent Editor log shows the current TIC shaders compiling for D3D11. It is not yet demonstrated to be suitable for Quest 2: it renders the scene a second time, redraws all opaque geometry into an RG16F mask, performs four full-screen heat passes and a composite, and uses a fixed-resolution target. On-device GPU timing, memory bandwidth, thermal stability, and XR behavior remain unverified.

The live Unity control endpoint was unreachable during this audit. Serialized assets, the last-scene record, source code, Git state, screenshots, and recent Editor logs were inspected. Statements below therefore distinguish serialized/current code facts from runtime observations and inferences.

## 1. Current system map

```text
Networked fire/gameplay state
    │
    ├─ FireProfileController.currentTemperature / maxTemperature
    ├─ FlammableObject.onFire
    └─ NetworkedFireState replication and authority
                    │
                    ▼
             HazardTemperature
       normalized scalar + direct/radiant state
            │                    │
            │ MaterialPropertyBlock
            ▼                    ▼
 Opaque temperature mask   ThermalRadiationManager
   R: surface warmth       local 3 m transfer to nearby
   G: direct-source flag   eligible renderer objects
            │
            ├─ max dilation H/V
            └─ weighted blur H/V
                    │
                    ▼
Normal TIC camera color ──► final white-hot composite
                              ▲
                              │
                  projected direct-fire sources
                  up to 16 radial screen fields
                              │
                              ▼
                  ThermalRenderTexture (1024²)
                              │
                              ▼
                     TIC screen material
```

### Responsibility boundaries

| Responsibility | Current owner | Actual behavior |
|---|---|---|
| Gameplay fire state | `FlammableObject`, `FireProfileController`, Ignis/OAVA, `NetworkedFireState` | Determines burning, profile temperature, extinguish/reset state, and replicated lifecycle. |
| Stored temperature data | `HazardTemperature` | One normalized scalar per component, plus direct-source and radiant target state. Estimates 21–520 °C for display. |
| Environmental heat transfer | `ThermalRadiationManager` | Every 0.2 s, transfers a bounded fraction of direct-source heat to nearby eligible objects within 3 m, using occlusion checks. Sources and contributions combine by maximum. |
| Per-surface heat shape | `TicTemperatureMask.shader` | Redraws visible opaque geometry. Starts from the object scalar and optionally attenuates it radially around a world-space origin. |
| Direct fire volume/color shape | `TicThermalRendererFeature` and `TicWhiteHotComposite.shader` | Projects source points into viewport space and draws geometry-independent radial fields. Sources combine by maximum. |
| Heat spreading on the image | `TicHeatDiffusion.shader` | Two max-dilation passes followed by two normalized weighted-blur passes. It expands/smooths the mask but does not add energy. |
| White-hot color mapping/final image | `TicWhiteHotComposite.shader` | Converts normal scene color to stylized grayscale, brightens from the surface mask, then overlays red→orange→yellow→white direct-source fields. |
| Readout | `ThermalTemperatureDisplay` | Center physics raycast; displays the nearest parent `HazardTemperature`, otherwise ambient. It does not sample the rendered TIC pixels or hot-gas field. |

## 2. TIC prefab and camera path

### Serialized facts

- `Assets/TIC.prefab` contains the TIC root and `Scanner`, `Screen`, `Handle`, `Camera`, and `Base` children.
- The TIC and its camera are on layer 10 (`Equipment`).
- The camera targets `Assets/Resources/Materials/ThermalRenderTexture.renderTexture`.
- The camera's URP renderer index is `1`, selecting `ThermalRendererData` from both the PC and Mobile pipeline assets.
- The camera and thermal renderer currently cull/render all layers. There is no current thermal-only layer isolation.
- Post-processing is disabled on this camera. HDR is enabled and MSAA is allowed at the camera level.
- `ThermalTemperatureDisplay` is attached directly to the TIC camera.
- `Assets/Resources/Materials/Screen.mat` is a URP/Lit opaque material whose base texture is the thermal RenderTexture.

### Render target

`ThermalRenderTexture.renderTexture` is fixed at 1024×1024, single-sampled, linear, with no usable mip chain and a depth/stencil attachment. It is not configured for dynamic resolution. The PC pipeline uses render scale 1.3 and 4× MSAA; Mobile uses render scale 0.8 and 1× MSAA, but the explicit TIC target remains fixed at 1024×1024.

### Renderer selection

Both `PC_RPAsset.asset` and `Mobile_RPAsset.asset` contain the normal renderer at index 0 and `ThermalRendererData.asset` at index 1. The TIC camera explicitly chooses index 1. This linkage is internally consistent in the serialized assets.

## 3. Active thermal render pipeline

### Renderer data and automatic setup

`ThermalRendererData.asset` currently contains one active custom renderer feature: `TIC Continuous Thermal Field`, implemented by `TicThermalRendererFeature`.

`Assets/Editor/TicThermalRendererSetup.cs` is an `[InitializeOnLoad]` editor utility. After script reload, it loads the thermal renderer, removes legacy Render Objects features named `White Hot Scene Detail` and `Thermal Hazard Overlay`, creates or repairs the current feature as a renderer-data subasset, rebuilds the feature map, and saves assets. Opening the project or recompiling scripts can therefore mutate `ThermalRendererData.asset`. The current serialized result is consistent with that utility.

### Render Graph pass sequence

The current feature executes after transparents and uses `RecordRenderGraph`; URP compatibility mode is disabled in the project settings. Its path is:

1. Take the camera's fully rendered scene color, including transparent fire/smoke VFX.
2. Allocate an `RG16_SFloat` temperature mask.
3. Redraw visible opaque renderers from all layers using `TicTemperatureMask.shader`.
4. Run horizontal and vertical max-dilation passes.
5. Run horizontal and vertical weighted-blur passes.
6. Composite scene color, raw mask, blurred mask, and projected direct-source fields.
7. Publish the composite texture as the camera color output.

The feature creates its runtime materials with `Shader.Find`. The recent Editor log records the current TIC shaders compiling successfully for D3D11, including mobile keyword variants. No recent TIC shader error or C# compiler error was found in that log. This is historical log evidence, not a fresh live compile triggered by this audit.

### Mask semantics

`TicTemperatureMask.shader` writes:

- **R:** surface warmth.
- **G:** direct-source/alarm heat.

It uses `BlendOp Max`, so overlapping draws retain the hottest value rather than adding values. It respects scene depth and writes no depth. The pass targets opaque queues and common URP forward/unlit shader tags.

For a direct flammable object, the shader attenuates the object's scalar temperature around `_HeatOrigin` using `_HeatRadius`. This provides per-fragment variation across that renderer even though the stored temperature is object-level. Other heated renderers receive their scalar value without that localized direct-fire attenuation.

The final composite reads the R surface-warmth channel but does not use the G direct-source channel. G is therefore currently generated and diffused without contributing to final color.

### Final composite

The composite performs three visually important operations:

1. It turns the original scene into a contrast-shaped grayscale image with noise and vignette.
2. It brightens that grayscale using raw and diffused R-channel surface warmth.
3. It overlays a separate projected radial field for each direct heat source.

The direct field's palette is explicitly:

```text
outer/low heat → deep red → red → orange → yellow → white ← center/high heat
```

White is therefore the designed endpoint of the hottest direct-source color, not merely an incidental clipping artifact.

## 4. Temperature sources and lifecycle

### `HazardTemperature`

Each `HazardTemperature` stores one normalized temperature. It gathers child renderers while excluding `ParticleSystemRenderer`, computes a combined bounds-based heat origin/radius, registers with the radiation manager, and applies values through a per-renderer MaterialPropertyBlock.

For a burning object, its target is based on `FireProfileController.currentTemperature / maxTemperature`. If that data is unavailable, it falls back to Ignis age and ultimately a fully hot target. For a non-burning object, its target can come from the radiation manager. The applied target is the maximum of direct and radiant heat, not their sum.

Existing scene objects serialize `heatUpSpeed` and `coolDownSpeed` near `0.5`, although the current class default for new components is lower. Cooling multiplies the configured speed by `0.09`; at a serialized speed of `0.5`, a full-scale object takes roughly 22 seconds to cool to ambient.

The estimated display temperature interpolates from 21 °C to a capped hot endpoint between 350 and 520 °C. Even if a fire profile uses a higher gameplay maximum, the TIC readout estimate caps at 520 °C.

### Residual heat after extinguish

`Extinguish()` stops external ignition but deliberately preserves stored temperature. More importantly, an extinguished direct source remains marked as a direct source until its stored temperature falls almost to zero. During that cooling interval it can:

- remain in the projected direct-source list,
- retain the colored radial field,
- continue radiating heat to nearby receiver objects.

`ResetTemperature()` is the hard reset that immediately snaps to ambient and clears direct/radiant state. The full scenario reset invokes this path through `ThermalRadiationManager.ResetAllTemperatures`; single-object extinguish cleanup preserves cooling.

### Spatial radiation

`ThermalRadiationManager` is a runtime, persistent manager that updates at 0.2-second intervals. It:

- treats direct sources above a small heat threshold as emitters;
- searches colliders within 3 m;
- can add `HazardTemperature` at runtime to nearby eligible renderer objects that do not already have it;
- excludes particle renderers, disabled/inactive objects, camera/canvas/player objects, and very large renderer bounds;
- ray-checks obstruction between source and receiver;
- uses a steep distance falloff and transfers up to 28% of source heat;
- combines all contributions by maximum, not addition.

This is the current physically situated heat-transfer representation. It affects actual object temperature scalars and can persist while they cool. It does not itself ignite gameplay objects. Very large architectural surfaces are excluded from runtime component addition, although pre-authored `HazardTemperature` components may still exist on scene objects.

### Scene scale

The last recorded active scene was:

`Assets/Scenes/Scenarios/Backup+Old/DECORATED/DAYTIME/controlled fire Scenario 1 decorated DAYTIME 1.unity`

That scene serializes approximately 376 `HazardTemperature`, 355 `FireProfileController`, and 356 `NetworkedFireState` components. The one enabled build-settings scene, `Assets/SCENARIOS/Scenario 1 (controlled; 1 story; daytime) 1.unity`, contains roughly 347 of each fire/temperature component and a TIC prefab instance. Recent Editor log output also reported 357 fires during a play session, broadly agreeing with the serialized scene scale.

## 5. Direct-fire projection and the visual failure mode

### How source fields are built

The renderer feature asks `ThermalRadiationManager` for up to the first 16 registered active direct sources. It does not sort them by temperature, distance, visibility, screen influence, or gameplay importance.

Each source is projected into TIC viewport coordinates. Its screen radius is derived from a small temperature-dependent world-radius range, projected through the camera, then clamped to a viewport-space range. This radius is separate from `HazardTemperature.ApparentHeatRadius` and is not based on the visible flame plume, source renderer silhouette, gas volume, or nearby heated-surface bounds.

The composite evaluates a soft radial blob around that point. Multiple source fields combine with `max`. Sources behind the camera or outside the viewport are skipped, but there is no depth-buffer comparison or world-space occlusion check in the final source-field overlay.

### Why the result becomes a bullseye

The current shader intentionally creates concentric temperature bands from a radial scalar. It maps the hottest center to white and progressively maps lower heat to yellow, orange, and red. A bullseye is therefore the expected output of the current mathematical model.

### Why unrelated geometry becomes flat white or colored

The projected field is evaluated for every output pixel based only on screen distance from the projected source. It does not know which mesh produced the pixel or whether another object lies between the pixel and the fire. A cabinet, counter, appliance, wall, or person in front of or near the projected origin can therefore receive the same overlay as the visible flame.

At the hottest center, the palette itself is white. The composite also raises brightness from surface warmth and modulates the source palette with the grayscale scene. Saturation then clips the result. Large areas inside the high-value center can consequently collapse into a flat white plateau.

### Overlap and blowout

The thermal calculations do **not** add heat across sources:

- mask draws combine by maximum;
- radiation contributions combine by maximum;
- projected source fields combine by maximum;
- blur weights are normalized.

Thermal-source overlap is thus not the direct cause of unbounded numeric heat accumulation. There are still several routes to visual blowout:

- the source palette deliberately ends at white;
- the composite adds raw and blurred versions of the same R-channel warmth before saturation;
- flame and ember VFX use additive blending in the source camera image;
- multiple bright transparent VFX layers can saturate the scene color before grayscale conversion;
- the final composite applies further brightening and clamps the output.

Smoke uses alpha blending and can darken or obscure the normal source image, but it supplies no independent temperature value.

### Mesh dividers

The current direct-source field is continuous across mesh boundaries, which can hide seams but causes geometry-independent coloring. The surface mask remains a depth-tested redraw of individual opaque renderers, so per-object scalars, bounds, depth edges, and shader-tag eligibility can still reveal divisions in its grayscale-brightness contribution.

Older iterations used per-object override rendering and localized object fields more directly, which made mesh boundaries especially prominent. The diagnostic screenshots appear to cover multiple implementation stages, so they should not all be treated as proof of the exact current code path. They do consistently illustrate the architectural tension between object-bound heat and a continuous fire volume.

## 6. Fire, smoke, particles, and independent thermal information

The current fire system uses the Ignis/OAVA FlameEngine. The inspected fire prefab selects the `Wild` VFX variant. Its VFX Graph contains additive flame and ember outputs and alpha-blended smoke outputs.

These effects contribute to the TIC only in two ways:

1. They are rendered optically into the TIC camera's normal source color because the custom pass executes after transparents and the camera renders all layers.
2. Their owning fire object can supply a projected `HazardTemperature` source point through gameplay state.

The particles/VFX themselves do not carry independent `HazardTemperature`, do not write the opaque temperature mask, and do not provide per-particle or per-voxel heat data. Smoke is therefore visible smoke, not a thermal gas volume; flame luminosity affects source RGB, not the temperature model. The deleted heat-aura/trail implementation previously created explicit visual geometry, but that system is no longer active.

## 7. Thermometer/readout behavior

`ThermalTemperatureDisplay` creates a screen-space-camera HUD at runtime with a center reticle and a Celsius readout. It casts a physics ray up to 50 m through all layers except `ThermalFX`, ignores triggers, and displays the nearest parent's `HazardTemperature.EstimatedCelsius`. If none exists, it reports ambient temperature.

The readout does not sample:

- the TIC RenderTexture;
- the projected direct-source radial field;
- the raw or blurred thermal mask;
- flame/smoke pixels;
- hot gas between the camera and the hit surface.

The number can therefore disagree with the visible overlay. For example, a foreground cabinet can be colored by an unoccluded screen-space fire field while the raycast reports the cabinet's cooler object temperature.

## 8. Multiplayer and authority

`NetworkedFireState` uses one stable Normcore owner per fire as the authority. The first eligible client claims ownership, with failover behavior; the player operating an extinguisher is not necessarily that owner. The authority advances core fire simulation and publishes lifecycle/temperature state. Puppets mirror the model.

The model includes reliable lifecycle/heat-visual state and a frequently updated profile temperature. On remote clients, the replicated profile value is applied locally, after which each client's `HazardTemperature` derives its normalized direct temperature. Toggling replicated heat visuals calls `Ignite()` or `Extinguish()` on `HazardTemperature`; extinguish preserves residual heat.

Environmental radiation and dynamically added receiver components are currently local calculations rather than explicitly replicated authoritative state. They may be visually similar given matching geometry and source state, but physics timing, registry ordering, occlusion, and dynamically discovered receivers can diverge between clients. Because only the first 16 registered active sources are sent to the renderer and the list is unsorted, source selection can also depend on local registration order.

A current uncommitted test change sets the configured-fire start minimum to one player; its own comment identifies two players as the intended restored value. This appears to be a deliberate temporary multiplayer test override, not part of the TIC rendering design.

## 9. Legacy, inactive, and unfinished paths

The repository contains evidence of earlier TIC approaches that should not be confused with the active pipeline:

- The previous `ThermalRendererData` used two Render Objects features: a whole-scene opaque thermal override and a transparent `ThermalFX` overlay.
- `ThermalRadialOverride` and `M_ThermalOverride` remain on disk but are not referenced by the current renderer data.
- The earlier override mapped heat through a purple/green/red palette and used object-local radial behavior, which could produce per-mesh flat regions and seams.
- Deleted `ThermalHeatEffects` created billboard aura geometry on the `ThermalFX` layer.
- Deleted `ThermalHeatTrailManager` placed and decayed ground heat quads.
- Deleted `ThermalFireRegistry` discovered newly burning objects.
- A new `ThermalWhiteHot.shader` and `M_ThermalWhiteHot` material exist as untracked files but are not referenced by the active renderer or prefab.
- The current active hidden shaders are instead located dynamically by the renderer feature.

The active system is therefore the custom Render Graph feature plus its three hidden shaders, not the retained override material, inactive white-hot material, or deleted aura/trail system.

## 10. URP, XR, and Quest 2 assessment

### What is established

- Project version: Unity 6.0.66f1.
- URP and VFX Graph: 17.0.4.
- The current feature implements `RecordRenderGraph` and updates the camera color resource correctly for the Render Graph path.
- URP compatibility mode is disabled, so the intended Render Graph path is active.
- Recent local Editor logs show current TIC shaders compiling for D3D11 without identified TIC shader errors.
- The TIC camera is allowed to participate in XR, targets an offscreen texture, and selects the custom renderer on both PC and Mobile RP assets.

### What is not established

- Successful rendering in an actual Quest 2 build.
- Supported/performance-safe use of the chosen RG16F intermediate format on the target device.
- Whether the offscreen XR camera renders once, per eye, or incurs other avoidable XR overhead in the deployed configuration.
- GPU frame time and bandwidth cost at 1024×1024.
- Memory lifetime and aliasing behavior of all Render Graph intermediates on device.
- Visual equivalence between Editor/D3D11 and Quest/Vulkan or GLES.
- Sustained performance with the full fire/VFX/network scene.

### Cost and risk profile

Per TIC camera frame, the system currently pays for:

- a full secondary scene render including transparent VFX;
- another draw of eligible opaque scene geometry for the mask;
- an RG16F mask;
- four full-screen dilation/blur operations;
- a full-screen composite;
- CPU collection/projection of up to 16 sources;
- runtime radiation processing across hundreds of registered temperature components;
- a fixed 1024×1024 output even when the displayed TIC screen occupies fewer pixels.

That is a high-risk architecture for Quest 2 until measured. It may be viable at a reduced update rate/resolution or with a more selective scene representation, but the current repository contains no device profiling evidence sufficient to make that claim.

The recent local play log also contains an OpenXR `XR_ERROR_FORM_FACTOR_UNAVAILABLE` condition. That is expected when no compatible headset is available and does not prove a project defect, but it means the audited session provides no live headset validation.

## 11. Evidence status and limitations

### Directly verified from current serialized assets/source

- Prefab hierarchy, camera target, renderer index, layers, and material linkage.
- RP asset renderer lists and current renderer feature serialization.
- Render Graph pass sequence and shader math.
- Temperature lifecycle, radiation rules, source limits, and combination modes.
- Network state paths and reset/extinguish behavior.
- VFX blending declarations.
- Thermometer raycast behavior.
- Git-visible active, modified, deleted, and untracked TIC files.

### Observed in recent logs

- Assembly reload completed without an identified C# compile error.
- TIC shaders were compiled for D3D11.
- A play session initialized hundreds of network fire objects.
- OpenXR could not acquire a local headset form factor.

### Inferences that require runtime validation

- The exact contribution of additive fire VFX to a particular white region.
- Which supplied screenshot corresponds to the latest shader revision.
- Actual source selection order and visibility when more than 16 fires remain hot.
- Client-to-client divergence of locally derived radiation.
- Quest 2 render cost, XR camera behavior, and format support.

The Unity CLI reported the project through pipeline discovery but could not reach a live Editor control server. No scene state was changed and no play-mode action was taken during this audit.

## 12. Architectural conclusions

1. **The current rendering result is consistent with the code.** A red/orange/yellow/white radial bullseye is explicitly authored in the composite shader.
2. **The primary visual failure is representational, not a small threshold bug.** A screen-space radial source field is being used to stand in for a three-dimensional, depth-aware fire/hot-gas volume.
3. **Surface temperature and fire-volume temperature are separate but visually mixed.** Object temperatures are scene/geometry based; direct fire color is screen based. The thermometer measures only the former.
4. **Current thermal fields use maximum, not additive accumulation.** Overlap blowout is more likely palette/saturation/VFX related than temperature summation.
5. **Residual heat is intentionally retained.** Extinguished direct sources remain colored and radiative until nearly ambient unless a hard reset is used.
6. **Smoke and flame particles have optical but not independent thermal data.** They cannot currently express a temperature distribution distinct from the source point and owner object.
7. **The G mask channel is unfinished or redundant in the current composite.** It is populated and diffused but never read.
8. **The setup tool is an implicit asset mutator.** Script reload can rewrite the thermal renderer asset, which should be accounted for in source-control and reproducibility expectations.
9. **Render Graph compatibility is plausible and supported by the code structure.** Quest suitability is unproven and should be treated as a performance-validation question, not an established property.
10. **The architecture has accumulated inactive residue.** Legacy override materials/shaders and an unreferenced white-hot path make it easy to mistake inactive experiments for the production render route.

## 13. Focused questions before redesign

1. Which scene and screenshot should be treated as the canonical current failure case? Several `Assets/Temp` captures appear to show different intermediate implementations.
2. What should the visible thermal “aura” represent: heated solid surfaces, flame combustion, hot gases/smoke, a firefighter-oriented detection aid, or a deliberate blend of those concepts?
3. Should direct-fire color obey scene depth and occlusion, or should it intentionally reveal a hidden fire through foreground objects?
4. Should the colored region match visible flame/smoke volume, a physically estimated hot-gas volume, the burning object's bounds, or a fixed safety envelope?
5. Should large walls, floors, counters, and cabinets acquire and retain heat, or should thermal storage be limited to tagged gameplay objects?
6. Should smoke and hot gas have independent temperatures and cooling behavior, or is optical smoke plus a source temperature sufficient?
7. After extinguishment, how long should a fire source remain visibly hot and capable of heating nearby objects? Should its residual heat stay colored or transition immediately to grayscale white-hot surface heat?
8. Is pure white an acceptable hottest direct-fire color, or must the hottest colored hazard remain yellow/orange while white is reserved for grayscale hot surfaces?
9. Should multiple nearby sources combine by maximum, bounded addition, or a more physical energy model?
10. Should the TIC overlay replace scene luminance in hot regions or tint/preserve structural detail? This determines whether flame VFX brightness should influence final thermal brightness at all.
11. What temperature thresholds or reference standard should govern ambient, warm, dangerous, and saturated display states? The current palette is normalized rather than tied to explicit Celsius thresholds.
12. Must the on-screen readout match the final visible pixel field, or should it continue reporting only the raycasted object's stored temperature?
13. Must radiantly heated surroundings be authoritative and synchronized across multiplayer clients, or is a locally derived visual approximation acceptable?
14. What is the Quest 2 budget for TIC resolution, update frequency, and GPU time, and must the TIC run continuously while visible in both eyes?
15. When more than 16 sources are active, should selection prioritize hottest, nearest, visible, largest screen influence, or scenario-critical fires?

## Referenced current files

- `Assets/TIC.prefab`
- `Assets/Resources/Materials/ThermalRenderTexture.renderTexture`
- `Assets/Resources/Materials/Screen.mat`
- `Assets/Settings/PC_RPAsset.asset`
- `Assets/Settings/Mobile_RPAsset.asset`
- `Assets/Settings/ThermalRendererData.asset`
- `Assets/Editor/TicThermalRendererSetup.cs`
- `Assets/Scripts/Rendering/TicThermalRendererFeature.cs`
- `Assets/Scripts/Rendering/TicTemperatureMask.shader`
- `Assets/Scripts/Rendering/TicHeatDiffusion.shader`
- `Assets/Scripts/Rendering/TicWhiteHotComposite.shader`
- `Assets/Scripts/HazardTemperature.cs`
- `Assets/Scripts/ThermalRadiationManager.cs`
- `Assets/Scripts/ThermalTemperatureDisplay.cs`
- `Assets/Scripts/NetworkedFireState.cs`
- `Assets/Scripts/FireProfileController.cs`
- `Assets/Scripts/UniversalHazardController.cs`

