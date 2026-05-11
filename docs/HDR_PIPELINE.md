# HDR Image Pipeline

This project keeps the WinUI shell separate from HDR image decoding and presentation.

## Runtime Shape

1. Probe the file container and metadata.
2. Decode the base image and optional gain map.
3. Normalize pixels into an internal linear working representation.
4. Reconstruct gain-map HDR or map single-layer HDR into an HDR working target.
5. Apply display-mode policy and output mapping.
6. Present through a DirectX FP16 scRGB swap chain hosted by WinUI `SwapChainPanel`.

## Format Targets

- Gain map: JPEG Ultra HDR, ISO 21496-1, Adobe Gain Map metadata.
- Single-layer HDR: JPEG XR first, then EXR, AVIF/HEIF, JPEG XL, and RGBE.
- SDR fallback: WIC JPEG, PNG, TIFF, and WebP where available.

## Boundaries

- `Pages` own XAML and UI event wiring.
- `ViewModels` expose app state and never reference `Microsoft.UI.Xaml.*`.
- `Services` classify files and will host decoder selection.
- `Rendering` defines the HDR renderer contract. The implementation should become a native DirectX component.

## Gain Map Priority

The first supported HDR path is JPEG gain map:

1. Probe primary JPEG APP1 XMP for `hdrgm:Version="1.0"`.
2. Locate `Container:Directory` entries whose `Item:Semantic` is `GainMap`.
3. Find and parse the appended gain-map JPEG.
4. Read gain-map XMP rendering metadata from the secondary image.
5. Decode primary SDR and gain-map JPEGs into RGBA textures. The primary decode requests WIC color management into sRGB so embedded ICC profiles are honored before gain-map math.
6. Upload primary and gain-map textures to D3D11.
7. Apply EXIF orientation and reconstruct HDR in a full-screen pixel shader.
8. Present shader output on the FP16 scRGB swap chain.

## Metadata Handling

- Ultra HDR / Adobe XMP metadata is the first renderable gain-map path.
- ISO 21496-1 signatures are detected and surfaced in the UI. When ISO and Ultra HDR metadata are both present, the app marks the Adobe XMP path as a fallback until a dedicated ISO 21496-1 binary metadata parser is added.
- EXIF orientation is parsed from the primary JPEG and applied in the shader to both the SDR image and gain map.
- ICC profile presence is detected in APP2 segments, and the primary JPEG decode uses WIC color management to normalize SDR pixels to sRGB before reconstruction.
- HEIF/AVIF containers are probed through ISO BMFF boxes. The app currently reads `ftyp`, item info, item properties, `nclx` color metadata, pixel bit depth, and auxiliary gain-map signals. HDR HEIF can be decoded through WIC FP16 into the scRGB working space; raw PQ/HLG fallbacks still use explicit shader transfer handling.

## Display Mode Refactor Plan

The current renderer has grown around transitional concepts such as `System Auto`, `Manual Peak`, and `Display Fit`. Before implementing APL/ABL curve import, replace that with explicit Adobe-style view modes plus a separate headroom policy.

### View Mode

```csharp
public enum GainmapViewMode
{
    Sdr = 0,
    Hdr = 1,
    GainMap = 2,
    HdrUnclamped = 3,
}
```

- `Sdr`: gain-map sources show the base SDR rendition. Single-layer HDR sources tone-map to SDR.
- `Hdr`: default viewing mode. Uses system/display capability to choose gain-map weight and output mapping.
- `GainMap`: debug mode. Shows the raw gain-map texture or decoded boost view, not the photo.
- `HdrUnclamped`: inspection mode. Uses image metadata capacity without system headroom clamping. DWM/display behavior still applies after scRGB output, so do not describe this as direct panel clipping.

### Headroom Policy

```csharp
public enum HdrHeadroomMode
{
    SystemAdaptive = 0,
    Manual = 1,
    AblSoftProof = 2,
}
```

- Keep this separate from `GainmapViewMode`.
- Implement `SystemAdaptive` first.
- `Manual` can reuse the current slider after the mode naming is clean.
- `AblSoftProof` comes last and should use GPU APL reduction on the HDR working target, not CPU sampling and not the final swap-chain backbuffer.

### Proposed Rendering Modules

- `HdrDisplayConfigurator`: reads AdvancedColor/DXGI/EDID state, resolves SDR white, display peak, full-frame peak, and target headroom.
- `ShaderRenderer`: owns D3D11 textures, render targets, shader resource binding, and draw passes.
- `AplComputeEngine`: future compute shader component for async GPU APL reduction and readback.
- `RenderParams`: explicit constant buffer replacing overloaded `Weight`, `DisplayAdjustment`, `ToneMap`, and `ToneMap2` semantics.

## Current Single-Layer HDR Modes

- System Auto follows the Windows-style absolute scRGB path and tone maps the content peak toward the current display.
- Manual Peak uses the slider as an output highlight target in nits, without display APL/ABL adaptation.
- Display Fit uses the slider as the virtual highlight/content target, limits physical output to the display, and keeps SDR-range tones closer to paper white while rolling off highlights.

These are retained only as the current implementation baseline. The next renderer work should migrate their behavior into `GainmapViewMode.Hdr` plus `HdrHeadroomMode`.

## APL/ABL Compute Plan

Defer this until SDR/HDR/GainMap modes are stable.

When implemented, calculate APL from the intermediate HDR working render target before tone mapping:

1. Reconstruct content into a FP16 HDR working texture.
2. Dispatch `APLReduction.hlsl` over that texture.
3. Reduce luminance into partial sums using `groupshared` memory.
4. Reduce partial sums to one float asynchronously.
5. Use a ring of staging buffers or query/event fences so readback never blocks the WinUI thread.
6. Feed the resolved APL into an imported display ABL LUT to produce dynamic headroom.

Do not compute APL from the final swap-chain backbuffer. The backbuffer includes viewport scaling, letterboxing, and output mapping, which makes it the wrong measurement point for display soft proofing.

## Next Milestones

1. Add a full ISO 21496-1 metadata parser and prefer it over Ultra HDR XMP when both are present.
2. Refactor HDR display modes into `Sdr`, `Hdr`, and `GainMap`; add `HdrUnclamped` after the first three are correct.
3. Replace overloaded shader constants with explicit render/display parameter structures.
4. Promote HEIC/AVIF fallbacks to native 10-bit/PQ/HLG decode and add HEIF-family gain-map reconstruction.
5. Add display APL/ABL curve profiles only after the core modes are stable.
