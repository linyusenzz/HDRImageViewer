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

## Display Mode Architecture

The renderer now exposes explicit Adobe-style view modes and keeps headroom policy as a separate concept. The first implemented stage is intentionally conservative: it maps the new modes onto the existing shader and tone-map code without replacing the full constant-buffer model yet.

### View Mode

```csharp
public enum GainmapViewMode
{
    Sdr = 0,
    Adaptive = 1,
    AlternateImage = 2,
    GainMap = 3,
}
```

- `Sdr`: render the base SDR rendition. Gain-map interpolation is bypassed by forcing effective weight to 0. Single-layer HDR sources tone-map back toward SDR white.
- `Adaptive`: default viewing mode. It computes target headroom from the selected headroom policy and derives gain-map weight from that target.
- `AlternateImage`: render the alternate HDR rendition for gain-map content by forcing effective weight to 1 / capacity max. It deliberately ignores the current display or slider limit and leaves out-of-range clipping to the downstream display path.
- `GainMap`: debug mode. Shows the gain-map texture as an SDR grayscale inspection image. Gain maps may be monochrome or color/per-channel in source metadata, so this visualization is a debug view, not proof of channel semantics.

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
- `SystemAdaptive`: read Windows/DXGI/EDID display headroom.
- `Manual`: use the visible Headroom slider. This slider appears only when `GainmapViewMode.Adaptive` and `HdrHeadroomMode.Manual` are selected.
- `AblSoftProof`: disabled placeholder for now. It should use GPU APL reduction on the HDR working target, not CPU sampling and not the final swap-chain backbuffer.

### Proposed Rendering Modules

- `HdrDisplayConfigurator`: reads AdvancedColor/DXGI/EDID state, resolves SDR white, display peak, full-frame peak, and target headroom.
- `ShaderRenderer`: owns D3D11 textures, render targets, shader resource binding, and draw passes.
- `AplComputeEngine`: future compute shader component for async GPU APL reduction and readback.
- `RenderParams`: explicit constant buffer replacing overloaded `Weight`, `DisplayAdjustment`, `ToneMap`, and `ToneMap2` semantics.

## Current Single-Layer HDR Modes

- `Sdr` forces the explicit shader path and clamps/tone-maps output toward SDR-range presentation.
- `Adaptive` keeps the normal system-adaptive HDR path. PQ sources may use the Direct2D system pipeline; HLG/scRGB sources use the explicit shader path.
- `AlternateImage` is meaningful for gain-map alternate renditions. For single-layer HDR it currently behaves as an unclamped/original HDR inspection path because HLG/PQ/scRGB files do not have gain-map weight or capacity metadata.
- `GainMap` is not a valid single-layer HDR mode. HLG/PQ/scRGB images have no gain map, so the UI disables it.

The old `Manual Peak` and `Display Fit` behavior remains in renderer plumbing as `HdrHeadroomMode` work, but it is no longer the primary UI model.

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
2. Verify `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap` mode behavior across Ultra HDR JPEG, HLG HEIC, PQ/HLG AVIF, and SDR files.
3. Keep `ABL Simulation` disabled until GPU APL reduction exists.
4. Replace overloaded shader constants with explicit render/display parameter structures.
5. Promote HEIC/AVIF fallbacks to native 10-bit/PQ/HLG decode and add HEIF-family gain-map reconstruction.
6. Add display APL/ABL curve profiles only after the core modes are stable.
