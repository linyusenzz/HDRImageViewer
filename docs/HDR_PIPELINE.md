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
5. Decode primary SDR and gain-map JPEGs into RGBA textures. The primary JPEG APP2 ICC profile is probed for BT.709/sRGB, Display P3, or BT.2020/Rec.2100 so the shader can reason about SDR base gamut instead of assuming every Ultra HDR base is sRGB.
6. Upload primary and gain-map textures to D3D11.
7. Apply EXIF orientation and reconstruct HDR in a full-screen pixel shader.
8. Present shader output on the FP16 scRGB swap chain.

## Metadata Handling

- Ultra HDR / Adobe XMP metadata is the first renderable gain-map path.
- ISO 21496-1 signatures are detected and surfaced in the UI. When ISO and Ultra HDR metadata are both present, the app marks the Adobe XMP path as a fallback until a dedicated ISO 21496-1 binary metadata parser is added.
- EXIF orientation is parsed from the primary JPEG and applied in the shader to both the SDR image and gain map.
- ICC profile presence and known gamut are detected in APP2 segments. The viewer keeps Display P3 / BT.2100 bases on `DoNotColorManage` so the shader's gamut conversion produces scRGB negatives that drive a wide-gamut HDR display; sRGB / Unknown bases stay on `ColorManageToSrgb` since the conversion there is a no-op.
- HEIF/AVIF containers are probed through ISO BMFF boxes. The app reads `ftyp`, item info, item properties, `nclx` color metadata, pixel bit depth, and auxiliary gain-map signals. HDR HEIF / AVIF are decoded in-process through LibHeifSharp (`InterleavedRgba64LE`), with NCLX-derived `Transfer` (PQ/HLG) and `UsesBt2020Primaries` driving the shader's transfer-handling path. The `heif-dec.exe` / `avifdec.exe` CLI route is retained as a fallback.

## Display Mode Architecture

The renderer exposes explicit Adobe-style view modes and keeps headroom policy as a separate concept. The current constant buffer still uses packed `float4` fields for efficient D3D11 upload, but the field names now separate gain-map control, source encoding, display mapping, tone-map input, tone-map output, and view/headroom mode state.

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
- `GainMap`: debug mode. Shows the extracted gain-map texture in SDR range and preserves RGB channels when the gain map is color/per-channel. A separate grayscale inspection toggle can be added later, but the default must not collapse color gain maps to luma.

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
- `RenderParams`: future module-level wrapper around the current explicit packed constant groups.

### Current Shader Constant Groups

`GainMapShaderConstants` and the HLSL `GainMapConstants` cbuffer must stay layout-compatible. The packed groups are:

- `GainMapControl`: gain-map interpolation weight (x), Apple HDRGainMap semantic flag (y), SDR base gamut (z), and `log2(SdrWhite / 203)` SDR-reference adjustment used by Adobe-XMP `CalculateGainMapWeight` (w). The Adobe spec normalizes display headroom against a fixed 203-nit SDR reference; the renderer adds this w-channel adjustment to `DisplayMapping.z` so the weight reflects spec semantics rather than the user's Windows SDR white setting. Apple HDRGainMap uses a display-relative convention and skips the adjustment.
- `SourceEncoding`: single-layer source transfer and color-primary conversion flags.
- `DisplayMapping`: SDR white scale, max scene clamp, target boost stops, and full-frame scene limit.
- `ToneMapInput`: tone-map enable flag, virtual target peak, measured tone-map peak, and content average.
- `ToneMapOutput`: physical target peak, tone mode, adaptive target peak, and global/midtone scale.
- `ViewModeParams`: `GainmapViewMode` plus `HdrHeadroomMode`.

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
2. Verify `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap` mode behavior across Ultra HDR JPEG, HLG HEIC, PQ/HLG AVIF/JXL, and SDR files. The single-layer-HDR-to-Ultra-HDR round-trip pale/desaturated regression that motivated this list is now resolved on `decode-optimization`; revalidate the matrix against the post-fix renderer.
3. Keep `ABL Simulation` disabled until GPU APL reduction exists.
4. Promote the explicit packed shader constant groups into small typed render/display parameter structures.
5. The libheif binding now covers in-process 10/12-bit HEIC + AVIF decode (PQ/HLG and BT.2020 are read from NCLX). Next remaining decode work is to keep the heif-dec/avifdec CLI fallback healthy and reach feature parity for HEIF-family gain-map reconstruction on top of the binding.
6. Add display APL/ABL curve profiles only after the core modes are stable.
