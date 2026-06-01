# Project Status - 2026-05-12

> Superseded for current work by `docs/PROJECT_STATUS_2026-05-19.md`. Keep this file as historical context for the renderer-mode and packaging baseline.

This document summarizes the current state of the WinUI 3 HDR image viewer after the latest UI, renderer-mode, export, packaging, memory, and preload work.

## Current Version Points

- `viewer-ui-v0.3` / `dfd678c`: UI and settings cleanup checkpoint.
- `18821e1`: current HEAD, shader constant semantics cleanup.
- `2026-05-17` working update: adjacent preload/memory pressure reductions, directory metadata cache, FFmpeg decode/export fallback removal, native single-layer HDR CLI export discovery, SDR-mode white-control cleanup, and single-layer HLG/PQ mapping refinements are present in the working tree.

## Build And Packaging

- Project: `HdrImageViewer.csproj`.
- Target: `net10.0-windows10.0.26100.0`.
- Main dev build command:

```powershell
dotnet build .\HdrImageViewer.csproj -p:Platform=x64 --no-restore
```

- Current build status: Debug x64 build passes with `0` warnings and `0` errors after the 2026-05-17 preload/cache/HDR mapping update.
- MSIX test package generated at:

```text
AppPackages\HdrImageViewer_1.0.0.0_x64_Test\HdrImageViewer_1.0.0.0_x64.msix
```

- Transfer-ready zip generated at:

```text
AppPackages\HdrImageViewer_1.0.0.0_x64_Test.zip
```

- The MSIX is signed with a local test certificate `CN=HdrImageViewer` and currently verifies as `Valid` on the build machine.
- The package folder includes `Install.ps1`, `Add-AppDevPackage.ps1`, `HdrImageViewer_TestCertificate.cer`, `Dependencies/`, and `安装说明.md`.
- Release package was rebuilt with `PublishTrimmed=false` to reduce WinUI/WinRT/JSON trimming risk.
- Packaging warning still present: `mspdbcmf.exe` missing, so symbol package generation is skipped. This does not block the MSIX package itself.

## Viewer UX State

- `HomePage` is kept mounted in `MainWindow` while Settings/About are shown as overlays. This avoids unloading `SwapChainPanel`, prevents tab-switch crashes, and preserves the loaded image when navigating away and back.
- Left pipeline menu was removed.
- Viewer has a photo-app style surface with bottom acrylic command chrome, filmstrip, same-folder navigation, fullscreen, crop entry points, drag/drop file opening, and inspector toggling.
- Keyboard shortcuts currently include previous/next image, fullscreen, inspector toggle, filmstrip toggle, fit, and actual size.
- Settings page now contains real app settings rather than placeholder HDR controls:
  - mouse wheel behavior
  - touchpad gestures
  - adjacent image preload
  - adjacent image preload radius
  - right inspector visibility
  - filmstrip visibility

## HDR Display Modes

The active mode model is Adobe-style and separates display mode from headroom policy.

```csharp
public enum GainmapViewMode
{
    Sdr = 0,
    Adaptive = 1,
    AlternateImage = 2,
    GainMap = 3,
}
```

- `Sdr`: render the base SDR rendition for gain-map images; single-layer HDR is tone-mapped toward SDR.
- `Adaptive`: default viewing mode. Uses headroom policy to decide gain-map weight or single-layer output mapping.
- `AlternateImage`: gain-map alternate HDR rendition. Effective gain-map weight is forced toward full image capacity rather than current display headroom.
- `GainMap`: debug/pro inspection view showing the gain-map texture. It preserves RGB channels because gain maps can be monochrome or per-channel/color.

Headroom policy is separate:

```csharp
public enum HdrHeadroomMode
{
    SystemAdaptive = 0,
    Manual = 1,
    AblSoftProof = 2,
}
```

- `SystemAdaptive`: uses Windows/DXGI/EDID display capability.
- `Manual`: exposes the headroom slider only in `Adaptive + Manual Override`.
- `AblSoftProof`: placeholder only; disabled until GPU APL reduction and ABL LUT import exist.

## Renderer State

- Rendering is handled by a custom D3D11 pipeline presented through WinUI `SwapChainPanel` with FP16 scRGB output.
- JPEG Ultra HDR / Adobe gain-map rendering uses a shader path with SDR base texture plus gain-map texture.
- Single-layer HDR files use Windows Imaging decode paths and explicit shader mapping where needed.
- Direct2D HDR ToneMap path exists for some base-image scenarios, but HLG/scRGB routes generally use explicit shader handling because WIC/D2D RGB HLG color context is not reliable enough.
- 2026-05-17 WIC/HDR documentation review: Microsoft WIC docs define pixel formats and color contexts, but do not make WIC a full HDR presentation pipeline. DirectX/Direct2D Advanced Color docs require scRGB/CCCS presentation, display MaxLuminance adaptation, HDR tone mapping, and white-level adjustment. Keep HEIF/AVIF/JXL PQ/HLG interpretation, reference white, headroom, and gain-map reconstruction in the app renderer/export paths rather than treating WIC-decoded pixels as final HDR viewing output.

The shader constant buffer has been clarified without changing render math:

- `GainMapControl`: gain-map interpolation weight and Apple HDRGainMap semantic flag.
- `SourceEncoding`: single-layer source transfer and color-primary conversion flags.
- `DisplayMapping`: SDR white scale, scene clamp, boost stops, and full-frame scene limit.
- `ToneMapInput`: tone-map enable flag, virtual target, measured source peak, and content average.
- `ToneMapOutput`: physical target, tone mode, adaptive target, and global/midtone scale.
- `ViewModeParams`: `GainmapViewMode` plus `HdrHeadroomMode`.

`GainMapShaderConstants` and the HLSL `GainMapConstants` cbuffer must remain layout-compatible.

## Decode And Export State

- Ultra HDR / Adobe gain-map JPEG probing and rendering are active.
- HEIF/AVIF probing reads container/color metadata such as PQ/HLG, BT.2020, bit depth, and auxiliary gain-map signals.
- Drag/drop accepts single or multiple files. Multi-file drops use the dropped order as navigation/filmstrip scope.
- Repeated folder scans use a hidden per-directory `.hdrimageviewer.meta.json` cache for file signature, gain-map/HEIF probes, EXIF summary, file size, and write time. Decode pixels are not written to this cache.
- Crop/export paths currently include:
  - SDR export through WIC encoders.
  - JPEG Ultra HDR conversion through local `libultrahdr` CLI.
  - Existing JPEG gain-map preserving crop through `libultrahdr` scenario 4 where metadata semantics are compatible.
  - Single-layer JXL/AVIF/HEIF HDR export through native `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe` when those tools are available.
- `external/libultrahdr` is a local ignored dependency. The app looks for `external/libultrahdr/build/Release/ultrahdr_app.exe`.
- Optional native single-layer HDR tools are discovered from PATH and `external/libjxl`, `external/libavif`, and `external/libheif`.
- On the current build machine, `cjxl.exe`, `avifenc.exe`, and `heif-enc.exe` are installed through MSYS2 UCRT64 at `C:\msys64\ucrt64\bin`; see `docs/NATIVE_HDR_EXPORT.md`.

## Known Limitations

- The MSIX is test-certificate signed, not store/commercial code signed. Other PCs must trust the included `.cer` or run the included install script.
- HDR behavior still depends on Windows HDR state, display EDID/DXGI reporting, GPU driver behavior, and SDR white configuration.
- WIC can still vary by installed Windows codec package and driver/OS behavior. It should remain a fallback or pixel-source component for HDR formats, not the authority for PQ/HLG/gain-map viewing semantics.
- Single-layer HLG/PQ parity against Windows Photos and macOS Photos is close enough for normal viewer use, but still deserves a final reference pass before packaging a broader release.
- HEIF-family gain-map auxiliary reconstruction is mostly in place for current probe/render paths, but edge-case samples should stay in the validation matrix.
- `AblSoftProof` is not implemented. GPU APL reduction and user-imported ABL/display curve profiles are the remaining major renderer feature.
- Zoom/pan behavior has improved but remains a sensitive area. Read `docs/ZOOM_HANDOFF.md` before changing `SwapChainPanel` sizing, temporary transforms, or DXGI matrix behavior.
- `PublishTrimmed=true` is risky for this app. Keep release packages untrimmed unless a source-generated JSON/WinRT-safe trimming pass is done.

## Next Recommended Work

1. Add GPU APL reduction on the HDR working target.
2. Add user-imported ABL/display curve profiles and wire them into `AblSoftProof`.
3. Run a focused final HDR validation matrix for `Sdr`, `Adaptive`, `AlternateImage`, and `GainMap` across Ultra HDR JPEG, HLG HEIC, PQ/HLG AVIF/JXL, and SDR files.
4. Formalize package signing strategy if the app needs wider distribution: trusted internal certificate, commercial code-signing certificate, or Microsoft Store pipeline.

## Operational Notes For Future Agents

- Do not revert the persistent viewer mounting in `MainWindow`; unloading `HomePage` caused crashes and forced image reloads.
- Do not collapse color gain maps to grayscale in `GainMap` view.
- Do not treat single-layer HDR files as gain-map files; HLG/PQ/scRGB images do not have gain-map weights.
- Keep generated packages, `external/`, `bin/`, `obj/`, test exports, and diagnostic logs out of git.
- Prefer small commits around renderer changes. Verify build before and after shader/HDR behavior edits.
