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
   - JPEG gain-map shader path for Ultra HDR / Adobe gain maps.
   - Single-layer HDR base-image path for HEIF/AVIF/JPEG XR/etc.
   - SDR fallback image path for non-HDR or unsupported files.
5. The renderer presents to a FP16 scRGB swap chain hosted by `SwapChainPanel`.
6. `HomePage` updates zoom/layout, folder navigation, filmstrip selection, and adjacent preloads.

Drag/drop behavior:

- Single image drops load the file and then use the normal same-folder navigation strategy.
- Multi-image drops load the first supported file and use the dropped-file order as the filmstrip/navigation list.
- Folder drops are intentionally not expanded yet, to avoid accidental large batch loads.

## HDR Mode Semantics

The current UI uses Adobe-style display modes:

- `Sdr`: render the SDR base rendition for gain-map content; tone-map single-layer HDR down to SDR.
- `Hdr`: default system-adaptive HDR presentation. Use Windows/display headroom to choose gain-map weight or single-layer output mapping.
- `GainMap`: debug/pro inspection mode showing the extracted gain-map texture rather than the photo. Gain maps may be monochrome or per-channel/color, so the preview preserves channels instead of forcing grayscale.
- `HdrUnclamped`: professional inspection mode that exposes image metadata capacity without system headroom clamping. The enum exists, but the UI should expose it only after `Sdr`, `Hdr`, and `GainMap` are verified.

Keep HDR headroom policy separate from display mode:

- `SystemAdaptive`: default; uses AdvancedColor/DXGI/EDID display capability.
- `Manual`: explicit user target for controlled testing. The slider plumbing exists but is hidden from the first-pass display-mode menu.
- `AblSoftProof`: future mode; uses GPU APL reduction plus imported display ABL curve. Do this after the three core modes are stable.

Gain-map HDR and single-layer HDR are intentionally different:

- Gain-map files start from an SDR base plus relative gain metadata. In `Hdr`, system headroom controls how much reconstructed boost is exposed.
- Single-layer HLG/PQ/scRGB files already contain HDR scene/display values and do not have gain maps. Future manual controls should adjust output mapping target, not gain-map boost.

## Viewer UX State

- `HomePage` owns photo-viewer gestures and commands.
- Folder images are tracked by `_folderImagePaths` and `_currentFolderIndex`.
- The bottom chrome is a unified acrylic overlay with one compact row:
  - Left: open/reload.
  - Center: previous/next plus same-folder filmstrip or the current file name.
  - Right: crop, zoom, fit/fill, and full-screen commands.
- `Presentation/FilmstripImageItem` is intentionally UI-facing because it stores an `ImageSource` thumbnail.
- `PhotoThumbnailService` first tries WIC color-managed downscale to sRGB, then falls back to Shell thumbnails, then URI thumbnails.
- Full-screen is an immersive viewing mode: the main window hides the custom title bar and NavigationView shell, while `HomePage` hides the inspector panel.

## Performance Notes

- `ImagePreloadCache` stores the current image plus nearby folder images. It keeps metadata/load results for the active scope, but trims decoded pixel payloads to a 384 MB budget so HDR preloads do not grow without bound.
- Wheel and touchpad zoom apply a temporary `ScaleTransform` during active input, then commit the real `SwapChainPanel` size after the input settles. Resize the D3D buffer before changing the XAML surface size when possible, otherwise the panel can flash while the swap-chain buffer catches up.
- Thumbnail loading is cancellable and ordered by distance from the current image.
- Do not decode full HDR images just to fill the filmstrip.

## Current Known Follow-Ups

- Verify the newly exposed `Sdr`, `Hdr`, and `GainMap` display modes before adding `HdrUnclamped` or APL/ABL import.
- Add a real APL/ABL display profile model after the core display modes are stable.
- Finish HEIF-family gain-map auxiliary reconstruction.
- Improve single-layer HLG/PQ parity against Windows Photos and macOS Photos.
- Add optional user controls for filmstrip visibility, cache radius, and zoom behavior.
- Move more page logic into small services once behavior stabilizes, while keeping XAML event wiring simple.
- Wheel/touchpad zoom still needs a more stable architecture. See `docs/ZOOM_HANDOFF.md` before touching `SwapChainPanel` zoom or DXGI matrix scaling.

## Version Control Notes

- Keep `external/`, `bin/`, `obj/`, `AppPackages/`, generated test exports, and diagnostic logs out of git.
- `external/libultrahdr` is currently a local dependency. Document changes to its CMake options instead of committing the checkout.
- Before large renderer changes, build a clean baseline and commit the preparation separately from shader or pipeline behavior changes.
