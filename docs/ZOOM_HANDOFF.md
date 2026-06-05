# Zoom Interaction Handoff

This note records the current zoom state and an older experimental branch that
lived outside the current project folder. It is intentionally kept separate from
the implementation because the experimental zoom code is not ready to merge.

## Current Viewer - 2026-06-05

`Pages/HomePage.xaml.cs` still uses the safer current implementation:

- Wheel and touchpad input update `_zoomScale`.
- `PreviewDeferredZoom()` applies a temporary `ScaleTransform` to
  `ImageSurface`.
- `ApplyZoomAsync()` commits the real surface size after a short debounce.
- `ResizeRendererAsync()` then resizes the D3D11 FP16 swap-chain surface.

This keeps the app buildable and avoids the experimental DXGI matrix path, but
it can still flash during aggressive wheel zoom because `SwapChainPanel` does
not compose perfectly under changing XAML transforms.

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
