The current redesign went too far visually. Remove the new world-space hot-gas plume system entirely.

I do not want a detailed volumetric or geometric representation of hot gas around the fire.

The plume is currently producing large polygonal red/orange regions that look artificial and destroy the TIC image. This is worse than the previous implementation.

Preserve the architectural improvements that are still useful:

no old screen-space radial bullseyes;
no red → orange → yellow → white temperature progression;
keep NFPA-like high-temperature color ordering:
grayscale → yellow → orange → red
keep temperature and colorization separate;
keep HazardTemperature;
keep world-space surface heating and ThermalRadiationManager;
keep residual surface heat after extinguishment;
keep maximum-based heat combination rather than additive RGB temperature logic.

But remove the hot-gas proxy/plume path completely.

This includes removing or disabling:

TicHotGasProxy.shader;
runtime low-poly plume geometry;
plume mesh generation;
plume temperature writes into mask channel G;
any renderer-feature code whose only purpose is drawing the plume;
any plume sizing/noise logic.

Do not replace it with another volume, billboard, sphere, cone, or proxy.

Desired visual result

The TIC should once again look overwhelmingly like a grayscale thermal camera.

The previous grayscale presentation looked substantially better than the current result, so preserve or restore that visual character.

The image should approximately behave like:

most scene geometry:
black → dark gray → gray → light gray → white

genuinely hot surfaces:
grayscale → subtle yellow → orange → red

Color should be a small high-temperature portion of the final image, not the dominant appearance of the scene.

Do not allow whole counters, walls, floors, or large areas of the image to become flat yellow/red unless their actual HazardTemperature warrants it.

Structural detail must remain clearly visible.

Fire itself

Do not attempt to simulate hot gases.

The visible flame/fire VFX may remain part of the normal TIC camera render, but its optical brightness must not be interpreted as temperature.

If the fire VFX appears bright in grayscale, that is acceptable for now.

The meaningful thermal information should primarily come from:

FireProfileController
        ↓
HazardTemperature
        ↓
heated source/surfaces
        ↓
ThermalRadiationManager
        ↓
nearby heated geometry
        ↓
TIC colorization

In other words, we are intentionally accepting a simpler model:

fire heats objects and surfaces; the TIC visualizes those temperatures.

We do not need a simulated hot-air volume.

Restore the grayscale presentation

Inspect the current composite against the version immediately before the plume redesign.

The grayscale image in the previous version had much better visual quality.

Restore that grayscale behavior as much as possible while keeping the corrected temperature mapping.

Specifically:

preserve scene contrast and recognizable geometry;
avoid large flat regions;
avoid excessive yellow tint in moderately warm areas;
avoid heavily saturating the image;
preserve smooth white-hot grayscale response below the colorization threshold;
prevent additive fire VFX from washing the entire thermal image out.

The final composite should feel like a grayscale TIC with selective heat coloration, not a colored heatmap.

Color thresholds

Keep configurable project defaults:

yellow: 150 C
orange: 300 C
red:    450 C

These remain project defaults rather than NFPA-mandated values.

However, make the color effect visually conservative.

Near the yellow threshold, introduce only a modest yellow tint while retaining most of the grayscale luminance.

Orange should become more noticeable.

Red should be reserved for genuinely high-temperature surfaces.

Preserve luminance/detail inside all colored areas.

Do not produce flat:

#FFFF00
#FF8000
#FF0000

blocks.

Instead, color should modulate the existing thermal luminance.

Thermal mask

Simplify the thermal mask again.

If channel G exists solely for the new plume, remove that dependency.

Ideally the active thermal visualization should be driven primarily by the surface temperature in channel R.

Do not add another artificial spatial heat field.

Diffusion

Do not immediately restore the four old full-screen diffusion passes.

First see how the geometry-based temperature field looks without them.

If a tiny amount of smoothing is genuinely necessary, use the minimum possible amount and explain why.

Do not recreate broad halos.

Important constraint

The goal here is not maximum physical realism.

The goal is a believable firefighter TIC that:

looks good;
remains readable;
preserves the strong grayscale appearance we had before;
uses sensible high-heat colors;
does not show bullseyes;
does not show polygonal thermal blobs;
does not become a rainbow heatmap;
performs well on Quest 2.

Keep this implementation simple.

Validation

After editing, test or inspect:

Ambient scene with no active fire.
Fire next to a cabinet.
Heated cabinet after exposure.
Extinguished fire with residual surface heat.
Multiple fires.

The expected appearance is:

mostly grayscale
+ selective yellow/orange/red on actually hot geometry

not:

giant red/orange shapes covering the scene

Compile everything, inspect shader and C# errors, and use Unity runtime inspection if the Editor control tools are available.

Do not commit or push anything.