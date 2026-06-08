# Codecs And Formats

This document is the current format, codec, native-tool, and HDR metadata reference. Build/package mechanics live in `docs/BUILD_AND_PACKAGING.md`.

## Runtime Format Status

| Format | Open / Preview | HDR display | Export | Notes |
| --- | --- | --- | --- | --- |
| JPEG / JPG | Supported | SDR and Ultra HDR / gain map supported | SDR and Ultra HDR | JPEG APP/XMP probing detects Ultra HDR, Adobe gain map, ISO 21496-1 signals, Apple HDRGainMap JPEG signals, and ICC base gamut. |
| PNG | Supported | SDR/high-bit-depth/HDR metadata candidates | SDR and 16-bit HDR PNG | HDR PNG export writes PNG cICP HLG/PQ BT.2020 metadata. |
| TIFF / TIF | Supported | SDR/high-bit-depth/float candidates | SDR and float HDR TIFF | Float HDR TIFF export writes uncompressed 32-bit IEEE float RGB in linear scRGB/BT.709. |
| JPEG XR / WDP / HDP | Supported as WIC decode candidate | scRGB/FP16 candidates | Not a primary export target | Kept for opening/preview; no longer in the main single-layer HDR export list. |
| HEIF / HEIC | Partial | Single-layer PQ/HLG and HEIF-family gain map supported | HEIC HDR via `heif-enc.exe` | Single-layer HDR prefers LibHeifSharp; gain-map primary/base uses Windows Imaging and auxiliary/tmap gain maps use LibHeifSharp. |
| AVIF | Partial | Single-layer PQ/HLG and ISO gain map supported | AVIF HDR via `avifenc.exe` | Single-layer HDR uses LibHeifSharp/native fallback; gain-map AVIF uses `avifgainmaputil.exe` to extract the gain-map image and ISO metadata. |
| JPEG XL / JXL | Requires `jxlinfo.exe` / `djxl.exe` | PQ/HLG/linear metadata and jhgm gain maps routed to renderer | JXL HDR via `cjxl.exe` | Current x64 bundled tools are in `external\encoders\x64`. |
| OpenEXR / EXR | Supported when native bridge is present | Scene-linear half/float RGBA16F | EXR via native bridge | Uses `HdrImageViewer.Native` + OpenEXR runtime DLLs. |
| Radiance HDR / RGBE | Planned | Planned | Not supported | File association is reserved, decoder not complete. |
| WebP | System-decoder SDR baseline | Not a main HDR path | Not supported | Treated as ordinary image compatibility path. |

## Native Tool Lookup

`NativeToolLocator` is the shared runtime lookup path for command-line encoders/probes:

1. App output `encoders\<arch>`.
2. App output tool subdirectories such as `encoders\<arch>\avifgainmaputil`.
3. Project-local `external\encoders\<arch>`.
4. Project-local tool subdirectories such as `external\encoders\<arch>\avifgainmaputil`.
5. `C:\msys64\ucrt64\bin`.
6. `PATH`.

The app no longer probes ad hoc `external\libjxl`, `external\libavif`, `external\libheif`, or `external\libultrahdr` build folders at runtime. Those belong under `external\_deps` as local source/build cache, not runtime inputs.

These command-line tools are launched as external processes. When a user installs them separately, HDR Image Viewer is not redistributing those third-party binaries. Redistribution obligations only attach to binaries that a release package actually carries, for example files copied from `external\encoders\<arch>` into `encoders\<arch>`.

## Bundled x64 Tool Set

Current local x64 bundled encoder source:

```text
external\encoders\x64
```

Verified command-line versions:

- `cjxl.exe --version`: `cjxl v0.11.2`.
- `avifgainmaputil.exe help`: `libavif 1.4.1` with dav1d/aom/rav1e/svt backends.
- `avifenc.exe --version`: `libavif 1.4.1`, `aom v3.13.3`.
- `heif-enc.exe --version`: `libheif 1.22.2`.
- `heif-enc.exe --list-encoders`: lists `x265` for HEIC and `aom` for AVIF.

`avifgainmaputil.exe` and its MSYS2 DLL set live under `external\encoders\x64\avifgainmaputil` to avoid mixing its `libavif-16.dll` / `libyuv.dll` ABI with the root AVIF/HEIF tool set.

The current bundled x64 directory is about `55.52 MB` uncompressed before the AVIF gain-map subdirectory. `libx265.dll` is the largest root file at about `16.28 MB`.

Run this from the repo root to verify the bundled tools and native bridge:

```powershell
.\eng\verify-codecs.ps1
```

If `ultrahdr_app.exe` is missing but a local libultrahdr build exists:

```powershell
.\eng\verify-codecs.ps1 -RepairUltraHdr
```

That repair path copies from:

```text
external\_deps\libultrahdr\build\Release\ultrahdr_app.exe
```

## HEIF / AVIF Decode

For single-layer HEIF/AVIF HDR:

1. LibHeifSharp decodes in-process to `InterleavedRgba64LE`.
2. 10/12-bit samples are expanded to 16-bit full range with MSB replication.
3. NCLX metadata drives `Transfer=Pq` / `Hlg` and BT.2020 flags.
4. A green-frame corruption guard rejects likely corrupted libheif frames.
5. Fallback order is native CLI (`heif-dec.exe` / `avifdec.exe`), WIC FP16 scRGB, then WinRT RGBA16.

LibHeifSharp native DLL resolution checks both `libheif.dll` and `heif.dll` from app-local `encoders\<arch>` first, then MSYS2 fallback directories. .NET P/Invoke does not consult PATH by default, so `BitmapDecodeService` registers a `NativeLibrary.SetDllImportResolver`.

## HEIF / AVIF / JXL Gain Map

Gain-map rendering supports JPEG Ultra HDR, Apple HEIF auxiliary gain maps, HEIF/AVIF ISO tmap/gain-map files, and JPEG XL `jhgm` boxes.

Current implementation:

- `HeifAvifProbe` detects item graph, NCLX color metadata, bit depth, auxiliary gain-map signals, and ISO `tmap` signals.
- `JxlProbe` detects JPEG XL `jhgm` gain-map boxes from `jxlinfo` output.
- `DecoderCatalog` classifies renderable auxiliary/tmap/jhgm gain-map files as `HdrImageKind.GainMap`.
- `HeifGainMapDecoder` uses Windows Imaging for the primary/base image to avoid known corrupted primary HEVC output on local libheif/libde265 paths.
- HEIF auxiliary gain-map items and XMP metadata are extracted through LibHeifSharp.
- HEIF ISO tmap files parse the BMFF item graph, locate the derived base+gain-map relationship, parse binary ISO 21496 metadata, and decode the referenced grid gain-map item through LibHeifSharp.
- AVIF ISO gain-map files use `avifgainmaputil.exe` to extract the gain-map image and print binary-derived metadata.
- JPEG XL `jhgm` files parse the box bundle, read ISO 21496 metadata, extract the embedded naked JXL gain-map codestream, and decode that codestream through `djxl.exe`.
- `D3D11HdrRenderPipeline` reconstructs the HDR frame in the same gain-map shader architecture used by JPEG gain-map files.
- The custom reference-white override also applies to gain-map HDR. Standard Adobe/ISO gain maps default to a 203-nit content reference; Apple HDRGainMap defaults to the current display SDR white, and the slider can override that reconstructed HDR brightness anchor.

Apple model summary:

- `HDRGainMapHeadroom` is interpreted as HDR peak over SDR white.
- If XMP headroom is missing but an auxiliary gain map exists, fallback headroom is `8.0x` / 3 stops.

Adobe/ISO model summary:

- Parses `GainMapMin`, `GainMapMax`, `Gamma`, `OffsetSDR`, `OffsetHDR`, `HDRCapacityMin`, `HDRCapacityMax`, and `BaseRenditionIsHDR`.
- Standard gain maps use log-space blending.

## Planned Live Photo / Motion Photo

Live Photo / Motion Photo support is companion-media support, not a replacement for the current HDR still-image decode path.

Initial support:

- Apple-style sidecar detection: open the still image normally, then locate same-basename `.mov`, `.mp4`, or `.m4v` companion files.
- Android/Google Motion Photo JPEG detection: read XMP `MicroVideoOffset` or container item `Length`/`Padding` metadata and extract the embedded ISO BMFF video segment to a temporary `.mp4` for native playback.
- The filmstrip continues to list the still image once. Paired `.mov`/`.mp4` files or extracted embedded video clips are companion media, not standalone viewer entries.
- Playback uses the Windows/WinUI native media stack layered above the still frame. This keeps the existing D3D11 HDR renderer focused on still-image presentation and gain-map reconstruction.

Target scope for later passes:

- Apple Live Photo: prefer content identifier metadata where available. Same-basename matching is useful, but should not remain the only association rule.
- Android Motion Photo: cover more vendor-specific metadata variants and HEIC/AVIF containers with embedded motion segments.
- HDR playback consistency: optionally decode motion-video frames into the custom D3D11 HDR presentation path so tone mapping, headroom, crop/zoom, and ABL soft proof can match still-image rendering more closely.
- FFmpeg is a suitable optional CLI fallback for `ffprobe`-style inspection, embedded-video extraction, or temporary remux/transcode when native playback fails.
- MPV/libmpv is not the default plan. It is powerful, but it adds a much larger playback dependency, WinUI hosting complexity, and extra distribution/licensing review for a short-motion-photo feature.

Out of scope for the first pass:

- Re-exporting edited still images while preserving Apple Live Photo or Android Motion Photo bundles.
- Editing or re-encoding the motion segment.
- Routing motion-video frames through the custom D3D11 HDR still-image renderer.

## Single-Layer HDR Export

The main toolbar and crop export paths share the same single-layer HDR export surface:

- `.png`: built-in 16-bit RGB PNG with cICP HLG/PQ BT.2020 metadata.
- `.tif`: built-in uncompressed float TIFF.
- `.exr`: `HdrImageViewer.Native` OpenEXR bridge when available.
- `.jxl`: `cjxl.exe`.
- `.avif`: `avifenc.exe`.
- `.heic`: `heif-enc.exe`.

For native CLI encoders, `SingleLayerHdrExportService` writes a temporary 16-bit PQ/HLG BT.2020 PNG, then passes it to the selected encoder with matching transfer/color metadata. Missing native tools keep their picker choices disabled.

## Ultra HDR / Gain-Map Export

JPEG Ultra HDR / gain-map export uses `ultrahdr_app.exe` discovered through `NativeToolLocator`, normally from `encoders\<arch>` or `external\encoders\<arch>`.

Recommended libultrahdr build flags:

- `UHDR_WRITE_XMP=ON`
- `UHDR_WRITE_ISO=ON`

That lets exported JPEGs carry both Adobe-compatible XMP and ISO 21496-1 metadata.

## HEIC / x265 Note

HEIC HDR export needs 10-bit HEVC encoder support. The default vcpkg `x265:x64-windows` dynamic build exposes only the 8-bit API, so the current bundled setup uses a multilib x265 DLL:

1. Build 12-bit x265 static library with `HIGH_BIT_DEPTH=ON` and `MAIN12=ON`.
2. Build 10-bit x265 static library with `HIGH_BIT_DEPTH=ON`.
3. Build the 8-bit shared `libx265.dll` with `LINKED_10BIT=ON`, `LINKED_12BIT=ON`, and `EXTRA_LIB` pointing at the 10-bit and 12-bit static libraries.
4. Replace `external\encoders\x64\libx265.dll`.

Licensing and patent/commercial risk are separate:

- IPC use of a user-installed `heif-enc.exe` does not by itself mean this project redistributes libheif/x265 binaries.
- If a portable zip, MSIX, Store package, or other bundle actually includes `heif-enc.exe`, `libx265.dll`, or related HEIC/HEVC runtime DLLs, re-check the licenses and source-offer/notice obligations for those distributed files.
- HEVC patent/commercial licensing, regional rules, and Store policy may still matter even when open-source license obligations are satisfied.
