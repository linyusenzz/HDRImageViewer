# Architecture

HDR Image Viewer is a WinUI 3 photo viewer with a custom Direct3D 11 HDR renderer. The shell handles normal viewer UX, while decode, metadata probing, HDR reconstruction, tone mapping, and presentation stay in services/rendering code.

## Project Layout

- `Pages/`: WinUI pages, XAML, and UI event wiring.
- `Presentation/`: UI-facing state/controllers such as filmstrip items and preload coordination.
- `ViewModels/`: bindable app state. Keep these free of `Microsoft.UI.Xaml.*` types.
- `Services/`: file probing, metadata reading, image decoding, settings, export, preloading, thumbnail generation, and native tool lookup.
- `Models/`: pure data records and enums shared by services, view models, and rendering.
- `Rendering/`: D3D11/scRGB swap-chain renderer, display HDR detection, tone mapping, and shader integration.
- `Infrastructure/`: reusable low-level app helpers.
- `docs/`: current architecture/build/codec notes plus archived historical status snapshots.

## Runtime Flow

1. `HomePage` receives file paths from the picker, drag/drop, system activation, filmstrip click, or folder navigation.
2. `ImageWorkspaceViewModel.LoadFileAsync` asks the preload/cache layer for an `ImageLoadResult`.
3. `ImageDocumentLoader` probes signatures, EXIF, gain maps, HEIF/AVIF color metadata, JPEG XL metadata, WIC support, and OpenEXR support.
4. `D3D11HdrRenderPipeline.LoadAsync` chooses a render path:
   - JPEG Ultra HDR / Adobe gain-map shader path.
   - HEIF-family gain-map shader path.
   - Single-layer HDR base-image path for HEIF/AVIF/JPEG XL/OpenEXR/JPEG XR candidates.
   - SDR fallback image path for non-HDR or unsupported files.
5. The renderer uploads decoded pixels as D3D11 textures, then performs gain-map reconstruction, HLG/PQ handling, color conversion, tone mapping, and presentation in GPU shaders.
6. `HomePage` updates zoom/layout, folder navigation, filmstrip selection, and adjacent preloads.

## Decode And Render Boundary

- Image container decode is not GPU accelerated today. The viewer uses LibHeifSharp for HEIF/AVIF HDR, `djxl.exe` plus WinRT `BitmapDecoder` for JPEG XL, Windows Imaging/WIC/WinRT for SDR JPEG/PNG and gain-map base images, and `HdrImageViewer.Native` for OpenEXR.
- HEIF/AVIF native CLI tools (`heif-dec.exe` / `avifdec.exe`) are fallbacks when the libheif binding throws or the decoded frame fails the green-frame corruption guard.
- Single-layer HDR pixels reach the renderer tagged with `Transfer=Pq` / `Hlg` / `LinearScRgb` and color-gamut flags. PQ inverse EOTF, HLG inverse OETF/OOTF, BT.2020/P3 conversion, exposure, and tone mapping happen in the shader.
- WIC is treated as a container/pixel decode and color-context source, not as a complete HDR viewing pipeline. HDR presentation semantics stay in renderer/export code.
- FFmpeg is not used by current decode or export paths.

## HDR Modes

The UI uses Adobe-style view modes and keeps headroom policy separate:

```csharp
public enum GainmapViewMode
{
    Sdr = 0,
    Adaptive = 1,
    AlternateImage = 2,
    GainMap = 3,
}
```

- `Sdr`: render the SDR base rendition for gain-map content; tone-map single-layer HDR down toward SDR.
- `Adaptive`: default system-adaptive presentation. Gain-map content computes weight from display headroom; single-layer HDR maps to the current display policy.
- `AlternateImage`: render the alternate HDR rendition for gain-map content by forcing effective weight/capacity to full image intent.
- `GainMap`: debug/pro inspection mode showing the gain-map texture, preserving RGB channels when present.

Headroom policy:

```csharp
public enum HdrHeadroomMode
{
    SystemAdaptive = 0,
    Manual = 1,
    AblSoftProof = 2,
}
```

- `SystemAdaptive`: uses Windows AdvancedColor/DXGI/EDID display capability.
- `Manual`: uses the visible Headroom slider, only in `Adaptive + Manual Override`.
- `AblSoftProof`: disabled placeholder until GPU APL reduction and imported display ABL curves exist.

## Renderer Constants

`GainMapShaderConstants` mirrors the HLSL `GainMapConstants` cbuffer; keep order and `Vector4` packing synchronized.

- `GainMapControl`: gain-map weight (x), Apple HDRGainMap flag (y), SDR base gamut (z), Adobe 203-nit SDR-reference adjustment `log2(SdrWhite / 203)` (w).
- `SourceEncoding`: single-layer source transfer and source color-primary flags.
- `DisplayMapping`: SDR white scale, display max scene clamp, target boost stops, and full-frame scene limit.
- `ToneMapInput`: tone-map enable flag, virtual target peak, measured tone-map peak, and content average.
- `ToneMapOutput`: physical target peak, tone mode, adaptive target peak, and global/midtone scale.
- `ViewModeParams`: view mode, headroom mode, reference-white exposure scale for single-layer/gain-map HDR, and SDR-mode wide-gamut clamp flag.

Adobe XMP gain-map weight uses a fixed 203-nit SDR reference. Apple HDRGainMap uses a display-relative convention and skips that adjustment.

## Viewer UX State

- `HomePage` owns photo-viewer gestures and commands.
- The bottom acrylic chrome contains open/reload, previous/next, filmstrip/file name, crop, HDR save-as, zoom, fit/fill, and fullscreen commands.
- The main toolbar has full-image HDR save-as. It prompts for single-layer HDR, Ultra HDR conversion, or gain-map preservation depending on the file.
- Crop mode keeps its own crop-bounds export path.
- Full-screen is immersive viewing: the main window hides title/navigation chrome while `HomePage` hides the inspector panel.
- Wheel/touchpad zoom remains sensitive; read `docs/ZOOM_HANDOFF.md` before changing `SwapChainPanel` sizing, temporary transforms, or DXGI matrix behavior.

## Performance Notes

- `ImagePreloadCache` stores metadata/load results for the active folder scope. Radius 1 keeps decoded pixels for hot neighbours; wider radius is metadata-first.
- Decoded preload cache is trimmed to 320 MB, and LOH compaction is requested after image loads.
- `DirectoryMetadataCache` keeps per-directory metadata in memory and flushes `.hdrimageviewer.meta.json` in debounced batches.
- LibHeifSharp HEIF/AVIF decode expands 10/12-bit samples to 16-bit in `Parallel.For` over rows and reports per-phase timings on the status line.
- Thumbnail loading is cancellable, focus-ordered, and limited to a nearby window.

## Current Follow-Ups

- Add GPU APL reduction on the HDR working target.
- Add user-imported ABL/display curve profiles and wire them into `AblSoftProof`.
- Keep edge-case HEIF-family gain-map samples and single-layer HLG/PQ samples in the validation matrix.
- Continue refining zoom behavior and cache policy as usage data surfaces.
