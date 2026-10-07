using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Services;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private Vector4 _viewerToolsConstants = new(0, 1, 0, 0);
    private bool _captureAnalysis;
    private readonly PreviewAnalysisController _previewAnalysis = new();
    private CapturedAnalysisFrame? _capturedAnalysisFrame;
    private bool _captureGainMapComparison;
    private CapturedAnalysisFrame? _capturedSdrFrame;
    private CapturedAnalysisFrame? _capturedAlternateFrame;
    public bool ComparisonEnabled { get; set; }
    public float ComparisonPosition { get; set; } = 0.5f;
    public long PreviewVersion => _previewAnalysis.Version;
    public LuminanceSnapshot? AnalysisSnapshot => _previewAnalysis.Snapshot;
    public string? AnalysisError { get; private set; }

    public void ClearAnalysis()
    {
        _previewAnalysis.Invalidate(clearSnapshot: true);
        _captureAnalysis = false;
        _capturedAnalysisFrame = null;
        _captureGainMapComparison = false;
        _capturedSdrFrame = _capturedAlternateFrame = null;
        AnalysisError = null;
    }

    public async Task<LuminanceSnapshot?> AnalyzeCurrentPreviewAsync(CancellationToken cancellationToken,
        bool includeGainMapComparison = false)
    {
        CapturedAnalysisFrame? frame;
        CapturedAnalysisFrame? sdrFrame;
        CapturedAnalysisFrame? alternateFrame;
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearAnalysis();
            _captureAnalysis = true;
            _captureGainMapComparison = includeGainMapComparison && _document?.HasRenderableGainMap == true;
            if (_document?.HasRenderableGainMap == true) RenderGainMap();
            else if (_document is not null) RenderBaseImage(_document);
            frame = _capturedAnalysisFrame;
            sdrFrame = _capturedSdrFrame;
            alternateFrame = _capturedAlternateFrame;
        }
        finally
        {
            _captureAnalysis = false;
            _capturedAnalysisFrame = null;
            _captureGainMapComparison = false;
            _capturedSdrFrame = _capturedAlternateFrame = null;
            _renderOperationGate.Release();
        }

        if (frame is null) return null;
        try
        {
            // The render thread owns capture/Map/Unmap. The worker receives only
            // detached pixels and value types, after the render gate is released.
            return await _previewAnalysis.AnalyzeAsync(frame.Version,
                token =>
                {
                    static LuminanceSnapshot Analyze(CapturedAnalysisFrame captured, CancellationToken token) =>
                        new(captured.Width, captured.Height, captured.Pixels, captured.Layout,
                            captured.Version, captured.SdrWhiteNits, token);
                    var comparison = sdrFrame is not null && alternateFrame is not null
                        ? new GainMapChromaticityComparison(Analyze(sdrFrame, token), Analyze(alternateFrame, token))
                        : null;
                    return new LuminanceSnapshot(frame.Width, frame.Height, frame.Pixels, frame.Layout,
                        frame.Version, frame.SdrWhiteNits, token)
                    { GainMapComparison = comparison };
                }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (frame.Version == PreviewVersion) AnalysisError = ex.Message;
            return null;
        }
    }

    private void DrawWithViewerTools()
    {
        _previewAnalysis.Invalidate();
        var savedMode = _viewMode;
        var savedGamutMode = _colorGamutMappingMode;
        try
        {
            if (_captureGainMapComparison)
            {
                // Both samples use the same textures/layout/version. Draw into the
                // back buffer without presenting; the normal preview replaces them.
                // Bypass gamut clipping for this source comparison only.
                _captureGainMapComparison = false;
                _colorGamutMappingMode = ColorGamutMappingMode.Managed;
                _viewerToolsConstants = new Vector4(0, 1, 0, 0);
                _viewMode = GainmapViewMode.Sdr;
                UpdateGainMapConstantsBuffer();
                _context!.Draw(3, 0);
                CaptureAnalysisPixels();
                _capturedSdrFrame = _capturedAnalysisFrame;
                _viewMode = GainmapViewMode.AlternateImage;
                UpdateGainMapConstantsBuffer();
                _context.Draw(3, 0);
                CaptureAnalysisPixels();
                _capturedAlternateFrame = _capturedAnalysisFrame;
                _viewMode = savedMode;
                _colorGamutMappingMode = savedGamutMode;
            }
            // The comparison's HDR side always uses system/manual adaptive HDR.
            if (ComparisonEnabled) _viewMode = GainmapViewMode.Adaptive;
            _viewerToolsConstants = new Vector4(0, 1, 0, 0);
            UpdateGainMapConstantsBuffer();
            _context!.Draw(3, 0);
            if (_captureAnalysis)
            {
                _captureAnalysis = false;
                CaptureAnalysisPixels();
            }
            if (ComparisonEnabled)
            {
                _viewMode = GainmapViewMode.Sdr;
                _viewerToolsConstants = new Vector4(0, Math.Clamp(ComparisonPosition, 0, 1), 0, 0);
                UpdateGainMapConstantsBuffer();
                _context.Draw(3, 0);
            }
        }
        finally
        {
            _viewMode = savedMode;
            _colorGamutMappingMode = savedGamutMode;
            _viewerToolsConstants = new Vector4(0, 1, 0, 0);
            if (ComparisonEnabled) UpdateGainMapConstantsBuffer();
        }
    }

    private void CaptureAnalysisPixels()
    {
        _capturedAnalysisFrame = null;
        AnalysisError = null;
        try
        {
            if ((long)_pixelWidth * _pixelHeight > 8_500_000)
                throw new InvalidOperationException(Localization.GetString("AnalysisErrorTooLarge"));
            using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            using var staging = _device!.CreateTexture2D(new Texture2DDescription(
                Format.R16G16B16A16_Float, (uint)_pixelWidth, (uint)_pixelHeight,
                1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            var bytes = new byte[checked(_pixelWidth * _pixelHeight * 8)];
            _context!.CopyResource(staging, backBuffer);
            _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                for (var y = 0; y < _pixelHeight; y++)
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), bytes, y * _pixelWidth * 8, _pixelWidth * 8);
            }
            finally { _context.Unmap(staging, 0); }
            _capturedAnalysisFrame = new CapturedAnalysisFrame(_pixelWidth, _pixelHeight, bytes, GetCurrentImageLayout(), PreviewVersion,
                EffectiveSceneToSdrWhiteScale * 80);
        }
        catch (Exception ex) { AnalysisError = ex.Message; }
    }

    private sealed record CapturedAnalysisFrame(
        int Width, int Height, byte[] Pixels, Vector4 Layout, long Version, float SdrWhiteNits);
}
