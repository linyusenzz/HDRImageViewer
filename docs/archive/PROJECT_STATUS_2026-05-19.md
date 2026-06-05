# Project Status - 2026-05-19

> Archived historical snapshot. Current docs are `docs/ARCHITECTURE.md`, `docs/CODECS_AND_FORMATS.md`, and `docs/BUILD_AND_PACKAGING.md`.

This document captures the current working-tree state after the export, progress, speed, thumbnail, single-layer HDR, JPEG XL, and Ultra HDR color-investigation passes.

## Build And Run

- Project: `HdrImageViewer.csproj`.
- Target: `net10.0-windows10.0.26100.0`.
- Current verification command:

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64 --no-restore
dotnet run --project .\HdrImageViewer.csproj -p:Platform=x64 --no-build
```

- Latest local build status: Debug x64 passes with `0` warnings and `0` errors.
- Launch verification should use `dotnet run`; directly starting the built exe can produce misleading Windows App SDK/package-identity failures.

## Directory Cleanup

- Renamed the ad hoc sample folder `test\新建文件夹` to `test\single-layer-hdr-alt`.
- The folder contains alternate single-layer HDR test samples and its own `.hdrimageviewer.meta.json` cache.
- No generated native-decode temporary service remains in the source tree.

## Current Viewer State

- WinUI 3 viewer shell remains centered around `HomePage`, `ImageWorkspaceViewModel`, and `D3D11HdrRenderPipeline`.
- Viewer UI includes bottom acrylic controls, filmstrip, folder navigation, fullscreen/immersive mode, crop entry points, drag/drop opening, inspector panel, and HDR save-as flows.
- Folder scan performance is improved by `DirectoryMetadataCache`, now backed by an in-memory per-directory layer with 1.5 s debounced flush. Per-image `StoreAsync` is no longer a synchronous JSON round-trip.
- EXIF metadata reading avoids slow shell property queries and uses lightweight file/JPEG metadata paths where possible.
- Adjacent preloading for HEIF/AVIF/JXL single-layer HDR is no longer metadata-only on radius=1. Hot neighbours decode pixels through libheif and live in a 320 MB cache; radius>1 still loads metadata only.

## Decode Pipeline

The viewer's single-layer HDR decode path was rebuilt on the `decode-optimization` branch (baseline `9cc0bdc`). End-to-end open time on a 4240x2832 HEIC HLG sample is now ~400 ms (`open 13 ms, decode 300 ms, copy 86 ms`), down from ~12.5 s in the previous CLI-based path.

- HEIF/AVIF HDR: in-process libheif via LibHeifSharp 3.2.0. `BitmapDecodeService.DecodeHeifAvifHdrWithLibheif` opens the file through `HeifContext`, decodes to `HeifChroma.InterleavedRgba64LE`, copies plane rows into a managed byte[], and expands 10/12-bit samples to 16-bit (MSB-replicating `(v << leftShift) | (v >> rightShift)`) in parallel across rows. NCLX color profile drives `Transfer=Pq/Hlg` and `UsesBt2020Primaries`.
- HEIF/AVIF HDR fallback: after libheif rejects a file or the green-frame guard trips, the current chain tries native CLI (`heif-dec.exe`/`avifdec.exe`) first, then WIC FP16 scRGB, then WinRT RGBA16. The fallback's status line carries the libheif exception type/message and relevant timing/failure details.
- JXL HDR: unchanged. Still uses `djxl.exe` to write a temporary 16-bit PNG, then reads it through `BitmapDecodeService.DecodeFileWithWinRTAsync` (FileStream + `AsRandomAccessStream`, no extra byte[] copy).
- Ultra HDR / Adobe gain-map JPEG: primary + gain-map decode runs under `Task.WhenAll`; the container byte[] is shared via a new `(byte[] offset, count)` overload of `DecodeBytesAsync`.
- `BitmapDecodeService.DecodeBytesAsync` wraps `byte[]` through `new MemoryStream(...).AsRandomAccessStream()` instead of the WinRT `DataWriter` pipeline.
- Single-layer HDR viewer path passes `preserveHdrTransfer=true`. The redundant CPU `ConvertHdrEncodedToLinearScRgb` pass is bypassed; PQ inverse EOTF, HLG inverse OETF + OOTF, and BT.2020->BT.709 all happen in `DecodeBaseImageSample` on the GPU. Thumbnail and export paths still pass `preserveHdrTransfer=false` (or use the dedicated `DecodeFileForHdrExportAsync` overload) and retain the linear-scRGB output.

### Native dll resolution

A `NativeLibrary.SetDllImportResolver` is registered on the LibHeifSharp assembly in the `BitmapDecodeService` static constructor. It resolves both `libheif.dll` and `heif.dll` to (in order):

1. `<app output>\libheif.dll` / `<app output>\heif.dll`
2. `<app output>\encoders\<arch>\libheif.dll` / `<app output>\encoders\<arch>\heif.dll`
3. `C:\msys64\ucrt64\bin\libheif.dll` / `C:\msys64\ucrt64\bin\heif.dll`
4. `C:\msys64\mingw64\bin\libheif.dll` / `C:\msys64\mingw64\bin\heif.dll`

It calls `LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR)` so libheif's transitive dependencies resolve from the directory the resolved DLL lives in. The rest of the app's DLL search behaviour is untouched. CLI tool lookup is now centralized in `NativeToolLocator`, which checks bundled/app-local encoder paths before MSYS2/PATH.

## HDR Rendering State

- The default Ultra HDR / gain-map viewing path is still the custom D3D11 shader path.
- The temporary idea of defaulting Ultra HDR viewing to native `libultrahdr` decode was rejected and removed because it changed display-mode semantics: SDR could appear brighter than HDR modes and the headroom slider no longer controlled gain-map reconstruction correctly.
- Gain-map shader constants now carry an SDR base gamut flag in `GainMapControl.z`.
- JPEG APP2 ICC probing now detects common primary image gamuts: BT.709/sRGB, Display P3, and BT.2020/Rec.2100.
- The shader has P3-to-BT.709 and BT.2020-to-BT.709 conversion helpers for visible output and tone-map analysis.

## Export State

- Ultra HDR export supports selecting monochrome or RGB gain maps.
- Ultra HDR export supports selecting the SDR base gamut: Auto, BT.709/sRGB, Display P3, or BT.2020/Rec.2100.
- Export progress UI exists so long exports no longer look like the app is frozen.
- Single-layer HDR full-image export supports native JXL/AVIF/HEIF tools when present.
- Single-layer HDR export also exposes built-in 16-bit PNG, float TIFF, and OpenEXR when `HdrImageViewer.Native` is available. JPEG XR is kept as a decode/open candidate, not a primary export target.
- Gain-map sources can be baked into single-layer HDR export with a controllable reconstruction strength.
- Native HDR export tooling is documented in `docs/CODECS_AND_FORMATS.md`.

## Round-trip Ultra HDR Render Fix (Resolved)

Single-layer HDR -> this app exports Ultra HDR -> this app reopens the exported file used to render pale/desaturated compared to other Ultra HDR viewers. Resolved on `decode-optimization` in the `I` series of commits. The original "single bug" symptom was actually three independent issues stacked:

1. **Weight underdrive.** The shader's gain-map weight used `log2(displayPeak / userSdrWhiteNits)`, i.e. headroom over the user's Windows SDR white. Adobe / Ultra HDR spec uses a fixed 203-nit SDR reference (`log2(displayPeak / 203)`). On the reporter's setup (SDR white 280 nit, peak 465 nit) this dropped weight from 0.521 to 0.319 - a 62% under-application. Fix: `HdrDisplayConfiguration.GainMapSdrReferenceAdjustmentLog2 = log2(SdrWhite / 203)`, written into `GainMapControl.w` and added to `DisplayMapping.z` inside `CalculateGainMapWeight` (HLSL) and `CalculateGainMapWeightForStatus` (C#). Apple HDRGainMap keeps a display-relative convention and explicitly skips the adjustment via `GainMapControl.y > 0.5`.
2. **Wide gamut collapsed before shader.** Earlier work forced `ColorManageToSrgb` on Adobe XMP Display P3 / BT.2100 bases in an attempt to fix #1 directly. That converted the base into BT.709 primaries up-front and made the shader's `ConvertGainMapBaseToBt709` matrix a no-op, so SDR mode and HDR mode looked equally narrow. Fix: keep `DoNotColorManage` for P3 / BT.2100 bases so the shader matrix runs and produces small negative scRGB channels that survive to the wide-gamut display.
3. **Wide-gamut signal clipped at output.** The HDR branch of `PSMain` wrapped the BT.709 conversion in `max(ConvertGainMapBaseToBt709(hdr), 0.0f)`. P3 / BT.2020 colors outside the BT.709 hull need their negative scRGB channels intact - the Windows HDR compositor reads them as wide-gamut data. Clipping them collapsed gain-map-boosted P3 colors back onto BT.709 and produced the perceived "pale" / "desaturated" look in HDR mode (which was otherwise correct in SDR mode, where this clip was never present). Fix: drop the `max(...,0)`. `ApplyAdaptiveToneMapWithWhiteScale` is already negative-safe (peak picks the largest positive channel; the scalar remap preserves sign; `ClampToDisplayPeak` only enforces an upper bound).

Together the three fixes restore parity with other Ultra HDR viewers (verified on `test/IMG_3810-ultra-hdr.jpg`). libultrahdr 1.4's `mode 0` behaviour - it forcibly converts the SDR base to Display P3 primaries and writes a Display P3 ICC profile regardless of any `-c` passed in - is unchanged; the export side is intentionally left alone because the renderer fixes now handle the actual on-disk format correctly.

## Next Recommended Work

### Other followups

- The current libheif binding catches runtime failures and falls back to native CLI, WIC FP16, then WinRT RGBA16. If bundled `encoders\x64` is removed and MSYS2 is missing, check that the fallback chain still surfaces an actionable message rather than a black frame. The fallback chain is documented inline in `BitmapDecodeService.DecodeFileCoreAsync`.
- Memory: `MaxDecodedPixelCacheBytes = 320 MB` was sized for prev + current + next of typical 4K HEIC HDR (~96 MB each). If users typically open 8K HEIC HDR (`~265 MB each`), the budget may need another bump or radius=1 may need to fall back to next-only.
- License note: app-local `encoders\x64` may include libheif/libde265/x265/aom/libjxl/libavif and related runtime DLLs. Any future distribution must confirm replaceability/source obligations and list included components in the about/docs surface.

## Notes For Future Agents

- The decode pipeline overhaul lives on branch `decode-optimization` with baseline commit `9cc0bdc` (a single snapshot of the 2026-05-19 dirty working tree before the optimization sequence started). To roll the decode work back: `git reset --hard 9cc0bdc`. Individual optimizations are one commit each so you can also revert a single layer (see commit log).
- `master` is still at `f630989` ("Reduce preload memory pressure") and intentionally does **not** carry the LibHeifSharp dependency or any of the decode optimizations.
- Keep `external/`, `bin/`, `obj/`, `AppPackages/`, `.claude/`, generated exports, and diagnostic logs out of git.
- Use `apply_patch` for manual file edits.
- After WinUI app changes, build and launch through `dotnet run`, then verify the `HDR 图片查看器` top-level window is responsive.
- Verify `external\encoders\x64\heif.dll` (or the copied app output `encoders\x64\heif.dll`) before assuming the libheif binding will succeed. MSYS2 remains a fallback, not the first source of truth.
