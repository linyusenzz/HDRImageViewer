# Zoom Interaction Handoff

This note records the current zoom state and an older experimental branch that
lived outside the current project folder. It is intentionally kept separate from
the implementation because the experimental zoom code is not ready to merge.

## Current Viewer

`HdrSwapChainHost` is a `PreviewSurface` sibling that stretches to the visible
viewport. `ImageSurface` remains the scroll extent for pan, fallback, crop, and
Live Photo chrome.

- Wheel and touchpad input update `_zoomScale` and the logical `ImageSurface`
  size used by `ScrollViewer`.
- During the zoom animation, `SwapChainZoomPreviewTransform` scales the
  current HDR frame around the pointer. GPU `ImageLayout` is committed after
  the gesture settles so the preview stays at 16 ms even when redraw is busy.
- Shader `ImageLayout` maps the viewport onto the logical image; pan and the
  settled zoom call `RedrawAsync` without `ResizeBuffers`.
- `ResizeBuffers` runs only when the physical preview size or DPI changes.
- Do not assign `HdrSwapChainHost.Width`/`Height` from the zoomed image size.
  `SwapChainPanel` has no natural size; a 0×0 host presents a blank frame.

Precision touchpad and touchscreen gestures use `ImageScroller`'s native zoom
and pan. Native `ZoomFactor` is included in the renderer's visible image layout
during a gesture, then baked into the logical image size and reset to 1 on
completion. Starting native manipulation clears any pending wheel anchor so
two-finger panning cannot be snapped back to an older offset. Mouse wheel commands
are handled before the scroller's default wheel behavior; Ctrl always zooms.

## Experimental Branch Summary

The newer directory tried to remove the temporary XAML `ScaleTransform` and
instead drive zoom through a hybrid of XAML layout changes and DXGI
`MatrixTransform`:

- Add `_appliedZoom`, `_fitWidth`, `_fitHeight`, and a serialized zoom worker.
- Use `ApplyPanelStretch()` for zoom-out when the existing swap-chain buffer is
  large enough.
- Use `ResizeBuffers` for zoom-in.
- Preserve a zoom matrix across DPI transform updates.

That branch documents its own open bug: wheel-down after zooming in does not
reliably return to the previous size, and rapid wheel input can leave the
visual surface and D3D buffer out of sync. For that reason it was not merged.

## Recommended Fix Direction

The best long-term fix is to stop making `SwapChainPanel` the interactive zoom
surface. A more stable architecture would render the HDR result into an
offscreen FP16 target, display it through a normal XAML surface during
interaction, and re-render sharply after zoom settles. That matches how common
photo viewers separate smooth interaction from final high-quality rendering.

If keeping the current architecture, avoid multiple sources of truth. Choose
one authority for zoom state, buffer size, and scroll offsets, then make resize
events ignore stale commits with an epoch/token instead of matching dimensions.

## Merge Guidance

Safe to reuse from the experimental directory:

- cache byte-budget logic;
- crop export code;
- small UI state improvements;
- documentation.

Do not blindly merge:

- `EnsureZoomWorkerAsync`;
- `_appliedZoom` / `_fitWidth` / `_fitHeight` layout authority;
- `ApplyPanelStretch`, `ApplyZoomPreviewScale`, `ResetZoomPreviewScale`;
- pre-resize present and `Present(0)` timing changes.
