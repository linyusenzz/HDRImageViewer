# HDR Image Viewer

WinUI 3 photo viewer focused on HDR still images: JPEG Ultra HDR / gain-map, HEIF/AVIF HDR probes, and single-layer HDR export experiments.

## Current Baseline

- WinUI 3 shell with a photo-viewer surface, inspector panel, filmstrip, folder navigation, drag-and-drop file opening, crop UI, zoom/pan, and immersive viewing.
- Direct3D 11 renderer presenting to a `SwapChainPanel` backed by an FP16 scRGB swap chain.
- JPEG Ultra HDR / Adobe gain-map probe and shader reconstruction path.
- HEIF/AVIF container probe for PQ/HLG, BT.2020, bit depth, and auxiliary gain-map signals.
- Single-layer HDR rendering through WIC/FFmpeg fallback paths, with explicit shader mapping where needed.
- HDR crop/export paths:
  - SDR preview export through WIC encoders.
  - JPEG Ultra HDR conversion through local `libultrahdr` CLI.
  - Existing JPEG gain-map preserving crop path through `libultrahdr` scenario 4 where metadata semantics are compatible.
  - Single-layer JXL/AVIF HDR fallback export through FFmpeg.

## Build

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64
```

## Run

```powershell
dotnet run --project .\HdrImageViewer.csproj -p:Platform=x64 --no-build
```

## Local Dependencies

- `external/libultrahdr` is a local checkout/build dependency and is ignored by git.
- The app currently discovers `external/libultrahdr/build/Release/ultrahdr_app.exe` for JPEG Ultra HDR export.
- Rebuild libultrahdr with `UHDR_WRITE_XMP=ON` and `UHDR_WRITE_ISO=ON` so exported JPEGs carry both Adobe-compatible XMP and ISO 21496-1 metadata.

## Next Engineering Step

Refactor the HDR renderer modes before adding APL/ABL profile import:

1. Add explicit `GainmapViewMode`: `Sdr`, `Hdr`, `GainMap`, `HdrUnclamped`.
2. Split HDR headroom policy from view mode: system adaptive first, manual/ABL later.
3. Replace overloaded shader constants with a clearer `RenderParams` model.
4. Add a clean gain-map debug view and SDR base rendition path.
5. Defer GPU APL reduction and custom ABL LUT import until the three core display modes are stable.

See `docs/HDR_PIPELINE.md` and `docs/DEVELOPMENT_OVERVIEW.md` for implementation boundaries.
