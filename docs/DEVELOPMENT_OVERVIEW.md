# Development Overview

This app is a WinUI 3 HDR image viewer. It combines normal photo-viewer UX with a custom DirectX HDR renderer.

## Project Layout

- `Pages/`: WinUI pages, XAML, and UI event wiring.
- `Presentation/`: small UI-facing state objects used by XAML, such as filmstrip items.
- `ViewModels/`: bindable app state. Keep these free of `Microsoft.UI.Xaml.*` types.
- `Services/`: file probing, metadata reading, image decoding, settings, preloading, and thumbnail generation.
- `Models/`: pure data records and enums shared by services, view models, and rendering.
- `Rendering/`: D3D11/scRGB swap-chain renderer, display HDR detection, tone mapping, and shader integration.
- `Infrastructure/`: reusable low-level app helpers such as `ObservableObject`.
- `docs/`: architecture notes for future development.

## Main Runtime Flow

1. `HomePage` receives file paths from the picker, drag/drop, filmstrip click, or folder navigation.
2. `ImageWorkspaceViewModel.LoadFileAsync` asks `ImagePreloadCache` for an `ImageLoadResult`.
3. `ImageDocumentLoader` probes signatures, metadata, EXIF, gain maps, HEIF/AVIF color metadata, and decoder support.
4. `D3D11HdrRenderPipeline.LoadAsync` chooses a render path:
   - JPEG gain-map shader path for Ultra HDR / Adobe gain maps. This remains the default viewing path so `Sdr`, `Adaptive`, `AlternateImage`, `GainMap`, and the headroom slider keep consistent semantics.
   - Single-layer HDR base-image path for HEIF/AVIF/JPEG XR/etc.
   - SDR fallback image path for non-HDR or unsupported files.
5. The renderer uploads decoded pixels as D3D11 textures, then performs gain-map reconstruction, HLG/PQ handling, color conversion, tone mapping, and presentation in GPU shaders.
6. `HomePage` updates zoom/layout, folder navigation, filmstrip selection, and adjacent preloads.

Decode/render boundary:

- Image container decode is not GPU accelerated today. The viewer uses a mix of (a) in-process `libheif` via `LibHeifSharp` for HEIF/AVIF HDR, (b) `djxl.exe` plus WinRT `BitmapDecoder` for JPEG XL, (c) Windows Imaging / WIC / WinRT `BitmapDecoder` for SDR JPEG/PNG and Ultra HDR gain-map base/secondary, then uploads decoded pixels to D3D11 textures. The HEIF/AVIF native CLI tools (`heif-dec.exe`/`avifdec.exe`) are kept as fallbacks for when the libheif binding throws.
- The libheif binding finds `libheif.dll` plus its HEVC/AV1 backends in `C:\msys64\ucrt64\bin` via a `NativeLibrary.SetDllImportResolver` in `BitmapDecodeService`'s static constructor; .NET P/Invoke does not consult PATH by default, so this hook is required even when the directory is on PATH.
- Single-layer HDR pixels reach the renderer carrying `Transfer=Pq`/`Hlg` + `UsesBt2020Primaries` (`preserveHdrTransfer=true`). The shader runs PQ inverse EOTF, HLG inverse OETF with OOTF, and BT.2020->BT.709 on the GPU through `DecodeBaseImageSample`. Thumbnail and export paths use the dedicated `DecodeFileForThumbnailAsync`/`DecodeFileForHdrExportAsync` overloads and keep the linear-scRGB CPU pass.
- HDR rendering after upload is GPU accelerated through the FP16 scRGB swap chain and custom D3D11 shaders.
- FFmpeg is no longer used by the viewer decode or export paths. Single-layer JXL/AVIF/HEIF HDR export uses native CLI discovery for `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe`.

WIC/HDR boundary:

- WIC is treated as a container/pixel decode and color-context source, not as a complete HDR viewing pipeline. Microsoft documentation states that WIC pixel formats do not themselves define color space; color space comes from the frame color context or must be inferred only as a fallback.
- WIC exposes some HDR-related formats, including `GUID_WICPixelFormat32bppR10G10B10A2HDR10` for HDR10/PQ BT.2020/ST 2084, but that does not cover all single-layer HDR semantics used by HEIF/AVIF/JXL, especially HLG and gain-map reconstruction.
- The Windows-provided WIC HEIF codec documentation only clearly lists depth/gain alternate representations for HEIF. Do not assume WIC will return fully interpreted scene-linear or display-linear PQ/HLG pixels for HEIF-family HDR content.
- Microsoft DirectX/Direct2D Advanced Color guidance puts HDR viewing responsibilities in the render pipeline: convert to scRGB/CCCS, apply transfer/color conversion, adapt to display MaxLuminance, perform HDR tone mapping, and handle white-level/reference-white adjustment. This app therefore keeps PQ/HLG EOTF/OOTF, BT.2020 conversion, reference white, headroom, and tone mapping in the renderer/export code instead of trusting WIC output as final presentation data.
- Relevant Microsoft docs: WIC native pixel formats, WIC color management, DirectX Advanced Color, Direct2D HDR tone map effect, and Direct2D white level adjustment effect.

Drag/drop behavior:

- Single image drops load the file and then use the normal same-folder navigation strategy.
- Multi-image drops load the first supported file and use the dropped-file order as the filmstrip/navigation list.
- Folder drops are intentionally not expanded yet, to avoid accidental large batch loads.

## HDR Mode Semantics

The current UI uses Adobe-style display modes:

- `Sdr`: render the SDR base rendition for gain-map content; tone-map single-layer HDR down to SDR.
- `Adaptive`: default system-adaptive presentation. Use the selected headroom policy to choose gain-map weight or single-layer output mapping.
- `AlternateImage`: render the gain-map alternate HDR rendition by using capacity max / weight 1. This is intentionally separate from `Adaptive`.
- `GainMap`: debug/pro inspection mode showing the gain-map texture rather than the photo. Preserve RGB channels because gain maps may be monochrome or per-channel/color.

Keep HDR headroom policy separate from display mode:

- `SystemAdaptive`: default; uses AdvancedColor/DXGI/EDID display capability.
- `Manual`: explicit user target for controlled testing. The Headroom slider appears only in `Adaptive + Manual Override`.
- `AblSoftProof`: future mode; uses GPU APL reduction plus imported display ABL curve. It is currently disabled in the UI.

Gain-map HDR and single-layer HDR are intentionally different:

- Gain-map files start from an SDR base plus relative gain metadata. In `Adaptive`, headroom controls how much reconstructed boost is exposed. In `AlternateImage`, effective weight is forced to 1.
- Single-layer HLG/PQ/scRGB files already contain HDR scene/display values and do not have gain maps. Future manual controls should adjust output mapping target, not gain-map boost.
- Single-layer HLG uses a fixed 1000 nit HLG OOTF reference and a fixed HLG tone-map reference white so rendered contrast does not change when the Windows SDR content brightness slider changes.

## Viewer UX State

- `HomePage` owns photo-viewer gestures and commands.
- Folder images are tracked by `_folderImagePaths` and `_currentFolderIndex`.
- The bottom chrome is a unified acrylic overlay with one compact row:
  - Left: open/reload.
  - Center: previous/next plus same-folder filmstrip or the current file name.
  - Right: crop, zoom, fit/fill, and full-screen commands.
- `Presentation/FilmstripImageItem` is intentionally UI-facing because it stores an `ImageSource` thumbnail.
- `PhotoThumbnailService` first tries WIC color-managed downscale to sRGB, then falls back to Shell thumbnails, then URI thumbnails.
- The bottom chrome auto-hides in both normal and immersive viewing after pointer/navigation activity settles, using a short opacity and vertical slide animation. Crop mode keeps the chrome visible.
- The main toolbar has an HDR save-as command for full-image export. It prompts for single-layer HDR, Ultra HDR conversion, or gain-map preservation depending on the current file; crop mode keeps its separate crop-bounds export path.
- For single-layer HDR save-as, the dialog can choose PQ/HLG, HLG target peak, and gain-map reconstruction strength when the source is a gain-map image.
- For Ultra HDR conversion, the dialog can choose monochrome/RGB gain-map generation and the SDR base gamut. Export progress is shown during long operations.
- Full-screen is an immersive viewing mode: the main window hides the custom title bar and NavigationView shell, while `HomePage` hides the inspector panel.

## Performance Notes

- `ImagePreloadCache` stores metadata/load results for the active folder scope. Adjacent preload is metadata-first beyond radius=1; the hot ring (radius=1) keeps decoded pixels (including single-layer HDR after the libheif binding landed). Decoded preloads are trimmed to a 320 MB budget, and large object heap compaction is requested after image loads so released pixel buffers do not linger.
- `DirectoryMetadataCache` keeps a per-directory `DirectoryMetadataCacheFile` in memory guarded by a `SemaphoreSlim`. `StoreAsync` updates the in-memory copy and arms a 1.5 s debounced flush via a shared `System.Threading.Timer`. The hidden `.hdrimageviewer.meta.json` is rewritten in batches instead of once per image. Decoded pixels are still intentionally not cached on disk.
- `BitmapDecodeService` has a static `NativeLibrary.SetDllImportResolver` that resolves the `libheif` P/Invoke target through the MSYS2 ucrt64 install. The libheif binding decodes HEIF/AVIF HDR directly to `InterleavedRgba64LE`, then expands 10/12-bit samples to 16-bit full range with an MSB-replicating shift in `Parallel.For` over rows. Per-phase timings (`open`, `decode`, `copy`, `src N-bit`) show up on the renderer status line for regression visibility.
- Single-layer HDR decode time on a 4240x2832 HEIC HLG sample is ~400 ms via the libheif binding. The previous `heif-dec.exe` CLI path was ~12 s and is kept as a fallback only.
- `SingleLayerHdrExportService` writes a temporary 16-bit PQ/HLG BT.2020 PNG and invokes native `cjxl.exe`, `avifenc.exe`, or `heif-enc.exe` when present. Missing tools keep their picker choices disabled.
- Native encoder setup and local install paths are documented in `docs/NATIVE_HDR_EXPORT.md`.
- Wheel and touchpad zoom apply a temporary `ScaleTransform` during active input, then commit the real `SwapChainPanel` size after the input settles. Resize the D3D buffer before changing the XAML surface size when possible, otherwise the panel can flash while the swap-chain buffer catches up.
- Thumbnail loading is cancellable, ordered by distance from the current image, and limited to a nearby window so large folders do not keep every thumbnail decoded.
- Do not decode full HDR images just to fill the filmstrip.

## Renderer Constant Notes

- `GainMapShaderConstants` is mirrored by the HLSL `GainMapConstants` cbuffer; keep order and `Vector4` packing synchronized.
- `GainMapControl` carries gain-map interpolation weight (x), Apple HDRGainMap semantic flag (y), SDR base gamut (z), and the Adobe 203-nit SDR reference adjustment `log2(SdrWhite / 203)` (w). The `w` channel is written into the uploaded constants from `D3D11HdrRenderPipeline.UpdateGainMapConstantsBuffer` each frame; the field on `_gainMapConstants` itself is not back-populated, so the C# `CalculateGainMapWeightForStatus` mirror reads the adjustment off the live `HdrDisplayConfiguration` instead. Apple HDRGainMap skips this adjustment in both HLSL and C# paths. Must stay layout-compatible with the HLSL cbuffer.
- `SourceEncoding` is only for single-layer source transfer and color-primary flags.
- `DisplayMapping`, `ToneMapInput`, `ToneMapOutput`, and `ViewModeParams` carry display policy and tone-map state without changing reconstruction metadata.

## Current Known Follow-Ups

- Add GPU APL reduction on the HDR working target.
- Add user-imported ABL/display curve profiles and wire them into `AblSoftProof`.
- Re-run the HDR validation matrix on the post-fix renderer. The single-layer-HDR-to-Ultra-HDR round-trip pale/desaturated regression is now resolved on `decode-optimization` (Adobe 203-nit SDR-reference weight + restored P3 wide-gamut path + no `max(...,0)` clip in the HDR PSMain branch); confirm parity across all canonical test files before merging.
- Run a final HDR validation pass across `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap`.
- Keep edge-case HEIF-family gain-map samples and single-layer HLG/PQ samples in the validation matrix.
- Continue refining user controls for zoom behavior and cache policy as usage data surfaces.
- Move more page logic into small services once behavior stabilizes, while keeping XAML event wiring simple.
- Wheel/touchpad zoom still needs a more stable architecture. See `docs/ZOOM_HANDOFF.md` before touching `SwapChainPanel` zoom or DXGI matrix scaling.

## Version Control Notes

- Keep `external/`, `bin/`, `obj/`, `AppPackages/`, `.claude/`, generated test exports, and diagnostic logs out of git.
- `external/libultrahdr` is currently a local dependency. Document changes to its CMake options instead of committing the checkout.
- `LibHeifSharp` 3.2.0 is a NuGet dependency (LGPL-3.0). `libheif.dll` and its transitive HEVC/AV1 backends are loaded dynamically from the user's MSYS2 install at runtime, so no native binaries are vendored in the repo.
- Before large renderer changes, build a clean baseline and commit the preparation separately from shader or pipeline behavior changes. The decode-pipeline rewrite that landed late on 2026-05-19 followed this rule: branch `decode-optimization` starts with a baseline commit (`9cc0bdc`) that snapshots the dirty working tree before any optimization, then each optimization is one commit on top.
