# Changelog

## 1.0.17.0 - 2026-06-09

Live Photo / Motion Photo companion-media support, Photos-style motion UI, and HDR video diagnostics.

- **Added Live Photo / Motion Photo companion-media detection**: still images now remain on the existing HDR still-image path while `LivePhotoProbe` detects same-basename `.mov` / `.mp4` / `.m4v` sidecars and Android/Google Motion Photo JPEG XMP entries with embedded ISO BMFF video offsets.
- **Kept motion assets out of the filmstrip**: paired videos and extracted embedded clips are modelled as companion media on `HdrImageDocument`, so folder navigation continues to show the still image once instead of treating motion segments as separate images.
- **Added a Photos-style motion overlay**: images with companion media show a compact top-left `动态` affordance with a Live Photo-like motion glyph and mute toggle, matching the Windows Photos interaction shape more closely than a generic video button.
- **Added native overlay playback**: clicking the motion affordance plays the sidecar video or extracted embedded MP4 through a WinUI `MediaPlayerElement` overlay above the still HDR frame, then returns to the static renderer when playback ends.
- **Added companion video HDR diagnostics**: the viewer now probes MP4/MOV `hvcC` and `colr` boxes for HEVC profile, bit depth, BT.2020/PQ/HLG CICP metadata, range, and frame size, then reports the result in the diagnostic panel separately from WinUI playback state.
- **Documented the follow-up renderer path**: architecture and codec docs now describe the current native overlay implementation and the later option to feed decoded motion-video frames into the D3D11 HDR renderer for tighter visual matching with still images.

## 1.0.16.0 - 2026-06-08

Single-layer HDR preview performance, first-open gain-map reliability, and viewer chrome refinements.

- **Accelerated single-layer HDR previews**: viewer preview decoding now respects a window-sized decode cap while export paths continue to decode at full resolution. HDR PNG/TIFF/JXL/EXR previews avoid full-size decode work when the display surface does not need it.
- **Made AVIF HDR decode prefer in-process libheif**: single-layer AVIF HDR now tries LibHeifSharp before the slower `avifdec -> temporary PNG -> WinRT` fallback path, cutting the tested AVIF preview from multi-second decode time to sub-second loading.
- **Removed the slow JXL temporary PNG preview path**: HDR JPEG XL preview now decodes through `djxl` to 16-bit PPM and reads the raw pixels directly, avoiding PNG compression plus WinRT re-decode overhead. JXL preview status reports `djxl`, downsampling, and PPM read timings.
- **Added native OpenEXR preview decoding**: `HdrImageViewer.Native` now exposes a preview decode entry point that reads reduced scanline blocks, with a tiled/mipmap fast path when available. EXR export and full-resolution decode paths remain unchanged.
- **Added detailed renderer/decode timing diagnostics**: render status now separates probe, resize, renderer load, decode, upload, draw/present, post-load resize, and backend-specific decode stages so slow formats can be traced to their actual bottleneck.
- **Fixed first-open gain-map rendering race**: HDR mode setup is now awaited during image load instead of running fire-and-forget, preventing the first dropped gain-map image from missing HDR rendering until navigating away and back.
- **Improved SDR fallback and viewer layout handling**: SDR/ICC images that use the XAML fallback skip unnecessary D3D decode work, keep their own fallback aspect ratio, and preserve the app's existing color-managed fallback behavior.
- **Refined viewer chrome layout**: inspector visibility, filmstrip sizing, and immersive/fullscreen chrome layout now use shared sizing constants and respond more consistently to window size changes.

## 1.0.15.0 - 2026-06-06

- **Added renderable ISO gain-map support for AVIF / HEIC / JXL samples**: AVIF ISO gain maps now use bundled `avifgainmaputil` to extract the gain-map image and metadata; HEIC `tmap` item graphs now locate the derived base+gain-map relationship and decode the grid gain-map item through LibHeifSharp; JPEG XL `jhgm` boxes now extract ISO metadata plus the embedded gain-map codestream and decode it through `djxl`.
- **Unified gain-map render input dispatch**: viewer rendering, adjacent preload, thumbnails, and HDR export source creation now all go through one gain-map decoder dispatcher so JPEG Ultra HDR, HEIF/AVIF tmap, HEIF auxiliary gain maps, and JXL jhgm files share the same GPU reconstruction path.
- **Bundled AVIF gain-map tooling without mixing DLL sets**: `avifgainmaputil.exe` and its MSYS2 runtime dependencies live under `external\encoders\x64\avifgainmaputil`; project copying and `NativeToolLocator` now support tool-specific subdirectories, and `eng\verify-codecs.ps1` checks that set.
- **Corrected format classification**: HEIF/AVIF ISO tmap and JPEG XL jhgm files are classified as `HdrImageKind.GainMap` instead of SDR metadata-only candidates, so HDR display controls and gain-map view modes remain enabled.
- **Fixed Adaptive gain-map weight for standard ISO/Adobe metadata**: standard gain maps now use `HdrCapacityMin/Max` and the current display headroom to compute reconstruction weight, instead of always rendering the full alternate image. This keeps AVIF/HEIC/JXL gain-map variants with different authored alternate headroom much closer in the default Adaptive view.
- **Fixed gain-map base transfer handling**: gain-map reconstruction now decodes the SDR base with the container-marked transfer curve, so BT.709 AVIF/HEIF bases are no longer treated as sRGB while JXL/sRGB bases keep the sRGB path. The same correction is used for preview rendering, thumbnail generation, tone-map analysis, and HDR export.
- **Improved ICC color-gamut detection for HEIF/AVIF gain maps**: ICC profiles are now parsed from structured `cicp` and `rXYZ/gXYZ/bXYZ` tags before falling back to text matching, so `prof` HEIC/AVIF primaries such as Display P3 or BT.2020 are no longer missed when the profile name is sparse.

## 1.0.14.0 - 2026-06-05

Local x64 package/dependency refresh, HEIC decode/runtime hardening, and single-layer HLG reference-white fixes.

- **Bumped the project/MSIX version to 1.0.14.0**: `HdrImageViewer.csproj` and `Package.appxmanifest` now agree on `1.0.14.0`; the current local package output is `AppPackages\HdrImageViewer_1.0.14.0_x64_Test`.
- **Bundled the current x64 native tool set from the project folder**: `external\encoders\x64` now contains the checked local runtime codec/tool staging set for JXL, AVIF, HEIF/HEIC, Ultra HDR JPEG export, LibHeifSharp native loading, and OpenEXR-related app output copying. `external\_deps` keeps the synced source/build cache inside the project folder without becoming a package input.
- **Centralized native tool discovery**: `NativeToolLocator` now owns command-line tool lookup for bundled `encoders\<arch>`, project-local `external\encoders\<arch>`, MSYS2 UCRT64, and PATH fallback. Service code no longer probes ad hoc `external\libjxl`, `external\libavif`, `external\libheif`, or `external\libultrahdr` build folders at runtime.
- **Added dependency verification and MSIX packaging tooling**: `eng\verify-codecs.ps1` checks the x64 bundled encoder set and `HdrImageViewer.Native` OpenEXR bridge, `-RepairUltraHdr` copies a locally built `ultrahdr_app.exe` from `external\_deps\libultrahdr\build\Release` into `external\encoders\x64` when needed, and `eng\publish-msix.ps1` builds the signed sideload MSIX without requiring local certificates in git.
- **Clarified external-tool compliance wording**: docs now separate IPC calls to user-installed CLI tools from actual third-party binary redistribution, and treat HEVC patent/commercial risk separately from open-source license obligations.
- **Replaced the broken Release `libde265.dll` runtime**: verified that the app-local `heif.dll` matched the known-good vcpkg build and isolated the green/magenta HEIC corruption to the Release `libde265.dll`. Rebuilt and replaced the app-local/vcpkg `libde265.dll` without adding FFmpeg to the viewer path.
- **Hardened in-process HEIF loading and fallback decode**: the LibHeifSharp resolver now searches for both `libheif.dll` and `heif.dll` under app-local encoder folders, detects corrupted green-frame single-layer HDR decodes, and falls back through native HEIF CLI, WIC FP16 scRGB, and WinRT RGBA16 paths when needed.
- **Fixed Apple HEIF gain-map primary decode**: HEIF gain-map rendering now uses Windows Imaging for the primary/base image while keeping LibHeifSharp for metadata and auxiliary gain-map extraction, avoiding corrupted primary HEVC output while preserving HDR gain-map reconstruction.
- **Corrected single-layer HLG tone mapping**: HLG decoding/tone mapping now uses the BT.2100/ARIB 1000-nit reference display model with nominal diffuse white around 203 nits, while app scene-linear units remain anchored at 80 nits internally.
- **Extended custom reference-white controls to gain-map HDR**: the custom reference-white slider now affects both single-layer PQ/HLG/linear scRGB rendering and gain-map HDR reconstruction, and now lives in the display-mode section directly under the HDR display-mode selector. Standard Adobe/ISO gain maps default to the 203-nit content reference; Apple HDRGainMap defaults to the current display SDR white and can be overridden for phone-like brightness matching.
- **Restored gain-map Alternate Image clipping semantics**: Alternate Image mode reconstructs the creator-authored alternate HDR rendition without display-fit tone mapping, while Adaptive mode still compresses highlights into the current display peak.
- **Retired JPEG XR from the single-layer HDR export list**: the active single-layer export surface is now built-in 16-bit PNG, float TIFF, OpenEXR through `HdrImageViewer.Native`, and native CLI JXL/AVIF/HEIF. JPEG XR remains a WIC decode/open candidate, not a main export target.

## 1.0.12.0 - 2026-06-03

JPEG XR export support.

- **Add JPEG XR HDR export**: single-layer HDR export now includes a `.jxr` target that writes FP16 linear scRGB through the Windows WIC JPEG XR encoder, matching the viewer's existing JPEG XR decode path.
- **Expose JXR in export UI/status**: the single-layer HDR crop/save-as flow now lists JPEG XR alongside PNG, TIFF, EXR, JXL, AVIF, and HEIF/HEIC, with progress/status text that covers both built-in encoders and native CLI backends.
- **Document JXR export support**: README and backend summaries now mark JPEG XR as a supported single-layer HDR export target.

## 1.0.11.0 - 2026-06-02

GitHub release packaging and native HDR decode/distribution cleanup.

- **Add GitHub portable release workflow**: added the `release-portable.yml` workflow plus `eng` build scripts for x64 portable zip generation, native OpenEXR bridge builds through vcpkg, tests, release notes, and GitHub Release asset upload.
- **Improve OpenEXR handling**: EXR probing now reads header metadata without decoding the full frame, reports dimensions and source primaries, supports ProPhoto RGB hints, and converts BT.2020 / ProPhoto scene-linear data toward the viewer's scRGB path.
- **Refine single-layer HDR display mapping**: the D3D11 renderer now separates content white scale, SDR preview scale, and display-fit tone mapping more explicitly for HLG, PQ, linear scRGB, and scene-linear EXR sources.
- **Portable dependency discovery**: native DLLs and optional encoder tools now use a normalized platform folder (`x64` / `x86` / `ARM64`) so local builds and GitHub portable zips can find bundled native dependencies consistently.
- **Repository distribution docs**: added project licensing, third-party notices, README install/build guidance, and ignore rules for generated artifacts and external binaries.

## 1.0.10.0 - 2026-06-01

About-page polish for the packaged build.

- **Show the installed app version in About**: the About page now displays the MSIX package version, with an assembly-version fallback for development and portable runs.
- **Add Weibo profile link**: the About page now links to the author's Weibo homepage at `https://weibo.com/u/6864619718`.

## 1.0.9.0 - 2026-06-01

File association support for using HDR Image Viewer as the default handler for supported image formats.

- **Registered supported image formats with Windows**: the packaged app now declares file type associations for JPEG / Ultra HDR candidates, JPEG XR, AVIF, HEIF/HEIC, JPEG XL, OpenEXR, Radiance HDR, PNG, WebP, and TIFF so Windows can offer HDR Image Viewer in "Open with" and Default apps.
- **Open files from system activation**: launching the app from a registered image file now routes the activation path into the viewer and opens the image immediately, including multi-file activation lists when Windows supplies them.
- **Default-app settings shortcut**: Settings now includes a system section with a button that opens Windows Default apps settings. Windows still requires the user to confirm default-app changes; apps cannot silently take over file associations.

## Unreleased - 2026-05-31

- **Split HomePage crop code into a partial file**: the crop-frame geometry, pointer/resize-thumb drag handlers, crop-export option selectors and `TryCalculateCropBounds` moved into `Pages/HomePage.Crop.cs` as `partial class HomePage` (the small crop command click-handlers stay in the main file). HomePage.xaml.cs drops to ~2480 lines, zero behaviour change.
- **Split HomePage export code into a partial file**: the ~990-line block of crop/save-as export orchestration (progress UI, the per-format `Export*Async` methods, the save-as option picker, the export-only enums/records and helpers like `HasExecutableOnPath`) moved verbatim into `Pages/HomePage.Export.cs` as `partial class HomePage`. Same type, same members — purely organisational, so HomePage.xaml.cs drops from ~3770 to ~2780 lines with zero behaviour change.
- **Extract `ImagePreloadController` from HomePage**: second HomePage split. The adjacent-image background preload loop, the cache-scope keep/priority sets (with their lock) and the `ImagePreloadCache` trimming moved into `Presentation/ImagePreloadController.cs`; HomePage keeps a thin `QueueAdjacentPreloads` wrapper that passes the folder paths, focus index, preload setting/radius and decode size in. Verified manually (filmstrip + prev/next navigation). No behaviour change.
- **Extract `FilmstripThumbnailController` from HomePage**: first step of breaking up the ~4000-line `HomePage.xaml.cs` god class. The filmstrip thumbnail cache, the bounded-concurrency background loader, the focus-ordered load list and the keep-window cache pruning moved verbatim into `Presentation/FilmstripThumbnailController.cs`; HomePage now owns a single `_filmstripThumbnails` controller and passes the current focus index / folder paths in as arguments instead of carrying the cache, CTS and seven methods itself (−144 lines). No behaviour change.
- **Reuse the frame-analysis staging texture**: `AnalyzeBackBuffer` allocated a fresh CPU-readable `R16G16B16A16_Float` staging texture on every call — and it runs on every present (load / resize / zoom commit), then `CopyResource` + `Map` the whole back buffer for the `frame avg/max` diagnostics and the `visible-pixels` flag. The staging texture is now cached and only rebuilt when the swap-chain size changes (`GetOrCreateFrameAnalysisStagingTexture` / `ReleaseFrameAnalysisStagingTexture`, disposed with the pipeline), removing a texture create+destroy churn on every frame presented. The diagnostics stay on by default.

## 1.0.5.0 - 2026-05-30

Stability hardening plus a working single-layer HDR exposure control. Submitted to the Microsoft Store as package version 1.0.5.0 (x64).

- **Disabled IL trimming for packaged builds**: `PublishTrimmed` was `True` for non-Debug, which halved the package (~89 MB → ~47 MB) by dropping unused framework assemblies but also risked silently breaking reflection-based `System.Text.Json` settings (the IL2026 warnings) and WinRT interop — and `AppSettingsService` swallows the resulting exceptions, so a trimmed regression would surface as lost settings rather than a crash. A smoke test of the trimmed package showed a seeded dark-theme setting not being applied, so trimming is now off (`PublishTrimmed=False`), matching the proven untrimmed 1.0.4 Store package.

- **Single-layer HDR diffuse-white / exposure control**: PQ (and HLG / linear scRGB) base images now have a real brightness anchor. Objective `frame avg/max` measurements showed the SDR-white override was a no-op on PQ output (it only nudged a tone-map knee), so there was no effective way to dial single-layer HDR brightness to match an external reference like Lightroom. A new `D3D11HdrRenderPipeline.SingleLayerExposureScale` (passed through the free `ViewModeParams.z` shader slot) multiplies the single-layer HDR scene-linear before tone mapping; SDR base images return earlier and are unaffected. `HomePage.CalculateSingleLayerExposureScale` drives it from the override slider, interpreting the value against PQ's 203-nit reference white (BT.2408): 203 keeps the image absolute (confirmed to match Windows Photos), lower values dim it, and it is decoupled from the system SDR white level (which does not change single-layer output). The render status line reports `exposure N.NNNx (diffuse white NNN nits)` when active.
- **Relabel the override control as "自定义参考白亮度" (custom reference-white luminance)**: the slider previously read "SDR 白点" but for single-layer HDR it now sets the diffuse-white / exposure target, so the header, automation names and value readout were renamed and a tooltip added (`203 nits = 绝对显示`). The default slider value moved from 400 to 203 so toggling the override on starts at absolute (no brightness jump), the step granularity dropped to 1 nit so 203 is reachable exactly, and the upper bound was lowered from 800 to 600 nits (with the C# clamps matched).
- **Orphaned native encoder/decoder processes on cancel**: The four `RunProcessAsync` helpers (`SingleLayerHdrExportService.cs`, `GainMapHdrExportService.cs`, `BitmapDecodeService.cs`, `JxlProbe.cs`) awaited `WaitForExitAsync(token)` but never terminated the spawned CLI (`cjxl` / `avifenc` / `heif-enc` / `djxl` / `jxlinfo` / `ultrahdr_app`) when cancelled, leaving it running orphaned and holding file handles. They now kill the entire child process tree on cancellation and drain stdout/stderr with `CancellationToken.None` so the reads finish at EOF instead of surfacing as unobserved cancellations.
- **GPU resize failures crashing the whole app**: Resize / composition-scale / display-change / HDR-mode `async void` handlers in `HomePage.xaml.cs` awaited `ResizeRendererAsync` with no guard, so a D3D failure (device removed, TDR, or an FP16 swap chain the GPU can't create) propagated out of `async void` as an unhandled exception and killed the process. `ResizeRendererAsync` is now the single guarded choke point: it swallows expected cancellation/disposal and reports other GPU errors to the render status line instead of throwing.
- **Unreliable SDR fallback image on packaged builds**: The SDR fallback in `LoadImagePathAsync` set `FallbackImage.Source = new BitmapImage(new Uri(path))`, but packaged WinUI 3 apps cannot reliably load arbitrary `file://` paths through `BitmapImage.UriSource`, so the last-resort preview could silently stay blank exactly when the D3D path had already failed. The new `ShowSdrFallbackImageAsync` streams the file via `StorageFile.OpenReadAsync` + `SetSourceAsync`, keeping the old URI attempt only as an inner catch so behaviour never regresses.

## Unreleased - 2026-05-20

Added native HEIF/HEIC Gain-Map rendering, reconstruction, and export support.

- **HEIF Gain-Map Decoder**: Integrated `LibHeifSharp` in `HeifGainMapDecoder.cs` to locate and extract auxiliary gain maps (supporting Apple's proprietary `HDRGainMap 1.0` XMP namespace and standard Adobe/ISO 21496-1 parameters).
- **GPU Shader Reconstruction**: Added support for both Apple proprietary (exponential headroom-based) and standard ISO 21496-1 (log-space blending) reconstruction equations in D3D11 pixel shaders.
- **Unified Pipeline Integration**: Routed HEIF gain maps through `D3D11HdrRenderPipeline.cs`, `ImagePreloadCache.cs`, and export services (`GainMapHdrExportService.cs`, `SingleLayerHdrExportService.cs`).
- **UI Mode Toggle Fix**: Corrected the classification from `SingleLayerHdr` to `HdrImageKind.GainMap` in `DecoderCatalog.cs`, and updated `HomePage.xaml.cs` to use `HasRenderableGainMap` instead of `IsRenderableUltraHdr` so `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap` modes work for HEIF gain-map images.
- **Color Profile & Metadata**: Correctly identified Display P3 color primaries / ICC profile in Apple HEIF files rather than showing "unknown/未知" tags.

## Unreleased - 2026-05-19

Decode pipeline overhaul (single-layer HDR open time went from ~12.5 s to ~400 ms on a 4240x2832 HEIC HLG sample, 31x). All decode work happens on the `decode-optimization` branch with baseline `9cc0bdc`. Roll back to that SHA to restore the previous pipeline.

- Added in-process HEIF/AVIF HDR decoding through LibHeifSharp 3.2.0 (LGPL-3.0) backed by MSYS2 ucrt64 `libheif.dll` + `libde265-0.dll` + `libdav1d-7.dll`. Replaces the previous `spawn heif-dec.exe / avifdec.exe -> write 16-bit PNG to temp -> WinRT BitmapDecoder` chain. AVIF is handled by libheif via the dav1d backend, so HEIC and AVIF now share one in-process path.
- Added a `NativeLibrary.SetDllImportResolver` hook so .NET's P/Invoke search (which does not look at PATH) finds `libheif.dll` and its transitive dlls in MSYS2 ucrt64. The resolver uses `LoadLibraryEx` with `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` so the transitive HEVC/AV1/sharpyuv dlls resolve from the same directory as `libheif.dll`.
- Added 10/12-bit to 16-bit MSB-replicating expansion over libheif's `InterleavedRgba64LE` output. Source bit depth comes from `HeifImageHandle.BitDepth`. Expansion is parallelised across rows.
- Kept the previous `heif-dec.exe / avifdec.exe` CLI path as a fallback when the libheif binding throws (the fallback's `DecoderName` carries the binding exception type and message for diagnosis).
- Bypassed the redundant `ConvertHdrEncodedToLinearScRgb` CPU pass on the viewer base-image path. `DecodeFileAsync` now sets `preserveHdrTransfer=true` so the bitmap reaches the renderer in PQ/HLG transfer and the existing `DecodeBaseImageSample` shader path handles PQ inverse EOTF, HLG inverse OETF with OOTF, and BT.2020->BT.709 on the GPU. Thumbnail and export paths are unaffected.
- Parallelised `ConvertHdrEncodedToLinearScRgb` across rows (still used by paths that ask for linear scRGB output, e.g. the libheif fallback's non-preserve case).
- Parallelised Ultra HDR primary + gain-map JPEG decode via `Task.WhenAll` and added a `(byte[], offset, count)` overload to `BitmapDecodeService.DecodeBytesAsync` so the JPEG container byte[] is shared instead of sliced into two fresh copies.
- Replaced the `InMemoryRandomAccessStream + DataWriter.WriteBytes/StoreAsync/FlushAsync` round-trip in `BitmapDecodeService.DecodeBytesAsync` with `new MemoryStream(bytes).AsRandomAccessStream()`. Zero behaviour change, removes the WinRT writer pipeline copy.
- Replaced the `ReadAllBytesAsync(pngPath) -> InMemoryRandomAccessStream` step in the native CLI fallback with a direct `FileStream.AsRandomAccessStream`. Saves one full-image byte[] copy on the fallback path (still relevant when libheif throws).
- Moved `DirectoryMetadataCache` through an in-memory per-directory layer guarded by `SemaphoreSlim`, with a 1.5 s debounced flush via a shared `Timer`. Per-image `StoreAsync` no longer round-trips JSON through the filesystem; the hidden `.hdrimageviewer.meta.json` is rewritten in batches.
- Raised `ImagePreloadCache.MaxDecodedPixelCacheBytes` from 192 MB to 320 MB and removed the HDR-specific early return in `PreloadAsync`. Single-layer HDR neighbours (radius=1) now keep decoded pixels so prev/next navigation is effectively instant; radius>1 is still metadata-only.
- Surfaced per-phase decode timings on the renderer status line so future regressions are visible: `libheif in-process [open Nms, decode Nms, copy Nms, src N-bit]` on success, `libheif heif-dec [spawn+decode Nms] ... [winrt png Nms] [fallback because <exception>]` on fallback.

Round-trip Ultra HDR rendering fix (single-layer HDR -> exported Ultra HDR -> reopened in viewer). The bug previously reported as "pale/desaturated" turned out to be three independent problems stacked on top of each other:

- Fixed gain-map weight to use the Adobe / Ultra HDR fixed 203-nit SDR reference (`log2(displayPeak / 203)`) instead of the user's Windows SDR white setting (`log2(displayPeak / userSdrWhite)`). On a setup with SDR white 280 nit and peak 465 nit, the weight went from 0.319 to 0.521 (a 62% underdrive). The adjustment is `log2(SdrWhite / 203)`, carried through the previously unused `GainMapControl.w` shader slot, and applied only to Adobe XMP gain maps - Apple HDRGainMap keeps its display-relative convention.
- Restored wide-gamut handling for Display P3 / BT.2100 Ultra HDR bases. The earlier attempt to force `ColorManageToSrgb` up-front (to chase the pale bug) collapsed the base into BT.709 primaries before the shader ran and left SDR mode and gain-map mode looking identically narrow. `UltraHdrGainMapDecoder` now keeps `DoNotColorManage` for P3 / BT.2100 bases so the shader's `ConvertGainMapBaseToBt709` matrix runs and produces the small negative scRGB channels that the FP16 swap chain needs to drive a wide-gamut display.
- Removed the `max(ConvertGainMapBaseToBt709(hdr), 0.0f)` clip on the HDR branch of `PSMain`. P3 / BT.2020 colors outside the BT.709 hull convert to scRGB negatives, and those negatives are how wide-gamut survives on an scRGB FP16 swap chain. Clipping them collapsed every gain-map-boosted P3 color back onto BT.709 and made HDR look desaturated next to SDR. `ApplyAdaptiveToneMapWithWhiteScale` was already safe with negative inputs (peak picks the largest positive channel, the scalar remap preserves signs, `ClampToDisplayPeak` only enforces an upper bound).
- Mirrored the spec-compliant weight calculation in `CalculateGainMapWeightForStatus` (C#) so the status line shows the same weight value the GPU shader actually applies.

Other 2026-05-19 work that landed earlier on this branch baseline (`9cc0bdc`):

- Added Ultra HDR export controls for monochrome vs RGB gain maps and SDR base gamut selection.
- Added export progress UI so long Ultra HDR / HDR save operations no longer look frozen.
- Added primary JPEG ICC gamut probing for gain-map files, including BT.709/sRGB, Display P3, and BT.2020 detection.
- Added gain-map renderer plumbing for SDR base gamut diagnostics and P3/BT.2020 conversion experiments.
- Renamed `test\新建文件夹` to `test\single-layer-hdr-alt` to make the sample directory self-describing.
- Documented the Ultra HDR round-trip playback blocker (now resolved later in this same dated entry above): Ultra HDR files exported by this app from single-layer HDR sources can look correct in other software while appearing slightly pale/desaturated when reopened in this viewer.
- Rejected and removed default native `libultrahdr` full-image decode for viewing because it broke viewer display-mode/headroom semantics.

## Unreleased - 2026-05-16

- Added a user setting for adjacent image preload radius and wired it into the viewer preload queue.
- Reduced viewer memory pressure by keeping compact tone-map analysis samples instead of full CPU pixel copies after GPU texture upload.
- Changed adjacent preloading so only immediate neighbors keep decoded pixels; wider preload ranges now load metadata only and the decoded cache budget is lower.
- Avoided retaining a full XAML fallback decode when D3D rendering succeeds, disabled the duplicate D2D base-image path by default, and request LOH compaction after image loads.
- Limited filmstrip thumbnail loading/cache to nearby images and reduced thumbnail decode size to match the visible filmstrip.
- Capped the default viewer/preload decode size to a lower quick-view resolution to reduce memory spikes and improve multi-image navigation latency.
- Restored single-layer HEIF/AVIF HDR decode to the full-quality path so HLG/PQ transfer semantics are not altered by preview downscaling.
- Fixed single-layer HDR SDR view so it maps to SDR reference white instead of inheriting HDR desktop SDR-white/headroom scaling.
- Hid and ignored the SDR-white override whenever the viewer is in SDR mode.
- Refined single-layer HLG handling so HLG OOTF and tone-map reference white no longer drift when the Windows SDR content brightness changes.
- Restored the bottom filmstrip/toolbar auto-hide behavior for both normal and immersive viewing, with a short fade/slide animation on show and hide.
- Removed FFmpeg from the viewer decode path and replaced FFmpeg-backed single-layer HDR export with native CLI discovery for `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe`.
- Added a main-toolbar single-layer HDR save-as command that reuses the JXL/AVIF/HEIF native export choices without entering crop mode.
- Fixed main-toolbar single-layer HDR save-as so it exports the full decoded source image instead of the current downsampled render surface region.
- Changed main-toolbar HDR save-as to prompt for export mode, including single-layer PQ/HLG, Ultra HDR conversion, and gain-map preservation when supported by the current file.
- Added main-toolbar single-layer HDR export controls for HLG target peak and gain-map reconstruction strength.
- Added JPEG XL preview through native `jxlinfo.exe` metadata probing and `djxl.exe` PNG decode, including PQ/HLG single-layer HDR routing.
- Added a hidden per-directory `.hdrimageviewer.meta.json` cache for file signature, gain-map/HEIF probes, EXIF summary, file size, and write time so folder navigation can skip repeated metadata scans without caching decoded pixels.
- Documented the WIC/HDR boundary from Microsoft WIC, DirectX Advanced Color, and Direct2D HDR guidance: WIC is a pixel/color-context source, while PQ/HLG/gain-map interpretation, reference white, headroom, tone mapping, and final HDR presentation stay in the app renderer/export pipeline.

## 0.2.0-prep - 2026-05-12

- Cleaned project root by removing monitor-driver cleanup artifacts and transient diagnostic outputs.
- Added version-control ignore rules for build outputs, local exports, logs, and local third-party checkouts.
- Documented current HDR viewer baseline, export paths, local `libultrahdr` dependency, and renderer refactor plan.
- Established the next renderer milestone: implement core display modes first (`Sdr`, `Adaptive`, `AlternateImage`, `GainMap`) and defer APL/ABL curve import.
- Added explicit HDR display modes in the UI and renderer: `SDR`, `Adaptive`, `Alternate Image`, and `Gain Map`.
- Added headroom-source UI for `Adaptive`: `System Auto`, `Manual Override`, and disabled `ABL Simulation` placeholder.
- Restored the Headroom slider for `Adaptive + Manual Override` only.
- Made `Gain Map` preserve color/per-channel gain-map previews and disabled it for single-layer HLG/PQ/scRGB images that have no gain map.
- Added an app-local SDR white override slider. This changes the viewer render baseline and status diagnostics, not the Windows global SDR white setting.
- Fixed cached Home page navigation state when switching between Settings and Viewer.
- Removed the left navigation Pipeline page and simplified the right-side image information panel.

## 0.1.x - Current Prototype Baseline

- WinUI 3 photo viewer shell with FP16 scRGB `SwapChainPanel` renderer.
- JPEG Ultra HDR / gain-map detection and shader reconstruction.
- HEIF/AVIF HDR probing and WIC/FFmpeg fallback decode paths.
- Drag-and-drop image opening, filmstrip navigation, zoom/pan, crop UI, and fullscreen/immersive viewing.
- HDR export experiments: SDR crop, JPEG Ultra HDR via `libultrahdr`, gain-map preserving JPEG crop for compatible metadata, and JXL/AVIF single-layer HDR via FFmpeg fallback.
