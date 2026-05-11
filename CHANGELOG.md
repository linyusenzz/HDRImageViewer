# Changelog

## 0.2.0-prep - 2026-05-12

- Cleaned project root by removing monitor-driver cleanup artifacts and transient diagnostic outputs.
- Added version-control ignore rules for build outputs, local exports, logs, and local third-party checkouts.
- Documented current HDR viewer baseline, export paths, local `libultrahdr` dependency, and renderer refactor plan.
- Established the next renderer milestone: implement core display modes first (`Sdr`, `Hdr`, `GainMap`; `HdrUnclamped` after those), and defer APL/ABL curve import.

## 0.1.x - Current Prototype Baseline

- WinUI 3 photo viewer shell with FP16 scRGB `SwapChainPanel` renderer.
- JPEG Ultra HDR / gain-map detection and shader reconstruction.
- HEIF/AVIF HDR probing and WIC/FFmpeg fallback decode paths.
- Drag-and-drop image opening, filmstrip navigation, zoom/pan, crop UI, and fullscreen/immersive viewing.
- HDR export experiments: SDR crop, JPEG Ultra HDR via `libultrahdr`, gain-map preserving JPEG crop for compatible metadata, and JXL/AVIF single-layer HDR via FFmpeg fallback.
