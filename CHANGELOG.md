# Changelog

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
