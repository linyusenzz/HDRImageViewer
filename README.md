# HDR Image Viewer

WinUI 3 photo viewer focused on HDR still images: JPEG Ultra HDR / gain-map, HEIF/AVIF/JPEG XL HDR probes, and single-layer HDR export experiments.

## Current Baseline

- WinUI 3 shell with a photo-viewer surface, inspector panel, filmstrip, folder navigation, drag-and-drop file opening, crop UI, zoom/pan, and immersive viewing.
- Direct3D 11 renderer presenting to a `SwapChainPanel` backed by an FP16 scRGB swap chain.
- JPEG Ultra HDR / Adobe gain-map probe and shader reconstruction path.
- HEIF/AVIF container probe for PQ/HLG, BT.2020, bit depth, and auxiliary gain-map signals.
- JPEG XL probe/preview through native `jxlinfo.exe` / `djxl.exe`, including single-layer HDR routing.
- Single-layer HDR rendering through Windows Imaging paths, with explicit shader mapping where needed.
- HDR crop/export paths:
  - SDR preview export through WIC encoders.
  - JPEG Ultra HDR conversion through local `libultrahdr` CLI, with monochrome/RGB gain-map and SDR base-gamut options.
  - Existing JPEG gain-map preserving crop path through `libultrahdr` scenario 4 where metadata semantics are compatible.
  - Single-layer JXL/AVIF/HEIF HDR export through native `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe` when those tools are available.

## Build

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64
```

## Run

```powershell
dotnet run --project .\HdrImageViewer.csproj -p:Platform=x64 --no-build
```

## Portable Release

GitHub Releases publish an unsigned portable zip for advanced users. The normal
end-user install path should still be Microsoft Store, where Windows handles
signing and updates.

Install from GitHub Releases:

1. Download `HdrImageViewer-<version>-win-x64-portable.zip`.
2. Extract it to a normal folder, for example `C:\Apps\HdrImageViewer`.
3. Run `HdrImageViewer.exe`.

No certificate installation is required for the portable zip. Windows may show
a SmartScreen warning because the GitHub build is unsigned.

Optional format tools:

- Most common formats work out of the box.
- JPEG XL preview uses `jxlinfo.exe` and `djxl.exe` when available.
- JPEG XL / AVIF / HEIC HDR export uses `cjxl.exe`, `avifenc.exe`, and
  `heif-enc.exe` when available.
- Ultra HDR JPEG export can use `ultrahdr_app.exe` when advanced users place it
  on `PATH`.

Install optional tools with MSYS2 UCRT64:

```powershell
winget install --id MSYS2.MSYS2 --source winget
C:\msys64\usr\bin\pacman.exe -Syu --noconfirm
C:\msys64\usr\bin\pacman.exe -S --needed --noconfirm mingw-w64-ucrt-x86_64-libjxl mingw-w64-ucrt-x86_64-libavif mingw-w64-ucrt-x86_64-libheif
```

Restart HDR Image Viewer after installing. The app auto-detects tools in
`C:\msys64\ucrt64\bin` and also checks `PATH`.

Create a local portable zip:

```powershell
.\eng\publish-portable.ps1 -Version 1.0.10 -Platform x64
```

The GitHub workflow `.github/workflows/release-portable.yml` runs the same
script when a `v*` tag is pushed, then uploads
`artifacts/HdrImageViewer-<version>-win-x64-portable.zip` to the GitHub Release.
Set the optional repository variable `STORE_URL` to include the Store install
link in release notes.

## Local Dependencies

- `external/libultrahdr` is a local checkout/build dependency and is ignored by git.
- The app currently discovers `external/libultrahdr/build/Release/ultrahdr_app.exe` for JPEG Ultra HDR export.
- Rebuild libultrahdr with `UHDR_WRITE_XMP=ON` and `UHDR_WRITE_ISO=ON` so exported JPEGs carry both Adobe-compatible XMP and ISO 21496-1 metadata.
- Optional single-layer HDR export tools are auto-discovered from PATH or `external/libjxl`, `external/libavif`, and `external/libheif`: `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe`.
- On this machine those tools are installed through MSYS2 UCRT64 under `C:\msys64\ucrt64\bin`. See `docs/NATIVE_HDR_EXPORT.md`.

## Current Engineering Focus

The HDR renderer now uses Adobe-style display modes: `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap`. Headroom policy is separate: `SystemAdaptive`, `Manual`, and a disabled `AblSoftProof` placeholder.

The 2026-05-19 round of work on `decode-optimization` did two big things on top of the baseline:

- Rewrote the single-layer HDR decode path around in-process libheif via `LibHeifSharp`, dropping HEIC HDR open time from ~12.5 s to ~400 ms on a 4240x2832 HLG sample. The `heif-dec.exe` / `avifdec.exe` CLI route remains as a fallback. JPEG XL still uses `djxl.exe`.
- Fixed the Ultra HDR round-trip pale/desaturated rendering by stacking three independent fixes: the Adobe 203-nit SDR-reference adjustment for the gain-map weight, the restored Display P3 / BT.2100 wide-gamut path through the shader, and the removal of the HDR-branch `max(...,0)` clip that was discarding scRGB negatives.

Remaining focused work:

1. Re-run validation across Ultra HDR JPEG, HLG HEIC, PQ/HLG AVIF/JXL, and SDR files on the post-fix renderer.
2. Resume GPU APL reduction and imported ABL/display curve profiles for `AblSoftProof`.
3. Promote the HEIF-family gain-map reconstruction story now that libheif decode is in-process.

See `docs/HDR_PIPELINE.md`, `docs/DEVELOPMENT_OVERVIEW.md`, and `docs/PROJECT_STATUS_2026-05-19.md` for implementation boundaries and current status.
