using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private const int MaxAnalysisStripPixels = 524_288;

    private Vector4 _viewerToolsConstants = new(0, 1, 0, 0);
    private readonly PreviewAnalysisController _previewAnalysis = new();
    private ID3D11Texture2D? _analysisRenderTarget;
    private ID3D11RenderTargetView? _analysisRenderTargetView;
    private ID3D11Texture2D? _analysisStagingTexture;
    private int _analysisTextureWidth;
    private int _analysisTextureHeight;
    private ID3D11Texture2D? _pixelProbeRenderTarget;
    private ID3D11RenderTargetView? _pixelProbeRenderTargetView;
    private ID3D11Texture2D? _pixelProbeStagingTexture;

    public bool ComparisonEnabled { get; set; }
    public float ComparisonPosition { get; set; } = 0.5f;
    public long PreviewVersion => _previewAnalysis.Version;
    public LuminanceSnapshot? AnalysisSnapshot => _previewAnalysis.Snapshot;
    public string? AnalysisError { get; private set; }

    public void ClearAnalysis()
    {
        _previewAnalysis.Invalidate(clearSnapshot: true);
        AnalysisError = null;
    }

    private void ReleaseViewerToolGpuResources()
    {
        _analysisRenderTargetView?.Dispose();
        _analysisRenderTarget?.Dispose();
        _analysisStagingTexture?.Dispose();
        _analysisRenderTargetView = null;
        _analysisRenderTarget = null;
        _analysisStagingTexture = null;
        _analysisTextureWidth = 0;
        _analysisTextureHeight = 0;

        _pixelProbeRenderTargetView?.Dispose();
        _pixelProbeRenderTarget?.Dispose();
        _pixelProbeStagingTexture?.Dispose();
        _pixelProbeRenderTargetView = null;
        _pixelProbeRenderTarget = null;
        _pixelProbeStagingTexture = null;
    }

    public bool TrySampleViewportPixel(
        int x,
        int y,
        out Vector3 scRgb,
        out float nits,
        out int imageX,
        out int imageY)
    {
        scRgb = default;
        nits = 0.0f;
        imageX = 0;
        imageY = 0;

        if (_isDisposed
            || _document is null
            || _pixelWidth <= 0
            || _pixelHeight <= 0
            || _contentPixelWidth <= 0
            || _contentPixelHeight <= 0
            || x < 0
            || y < 0
            || x >= _pixelWidth
            || y >= _pixelHeight)
        {
            return false;
        }

        if (!_renderOperationGate.Wait(0))
        {
            return false;
        }

        try
        {
            if (_isDisposed
                || _device is null
                || _context is null
                || _swapChain is null
                || _document is null
                || _pixelWidth <= 0
                || _pixelHeight <= 0
                || _contentPixelWidth <= 0
                || _contentPixelHeight <= 0
                || x >= _pixelWidth
                || y >= _pixelHeight)
            {
                return false;
            }

            var layout = GetCurrentImageLayout();
            if (layout.X <= 0.0001f || layout.Y <= 0.0001f)
            {
                return false;
            }

            var panelU = (x + 0.5f) / _pixelWidth;
            var panelV = (y + 0.5f) / _pixelHeight;
            var fittedU = (panelU - layout.Z) / layout.X;
            var fittedV = (panelV - layout.W) / layout.Y;
            if (fittedU < 0.0f || fittedU > 1.0f || fittedV < 0.0f || fittedV > 1.0f)
            {
                return false;
            }

            var sourceUv = ApplyShaderOrientation(new Vector2(fittedU, fittedV), _contentOrientation);
            imageX = Math.Clamp((int)(sourceUv.X * _contentPixelWidth), 0, _contentPixelWidth - 1);
            imageY = Math.Clamp((int)(sourceUv.Y * _contentPixelHeight), 0, _contentPixelHeight - 1);

            EnsurePixelProbeResources();
            if (_pixelProbeStagingTexture is null)
            {
                return false;
            }

            if (!TryRenderSinglePixelProbe(x, y, panelU))
            {
                using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                _context.CopySubresourceRegion(
                    _pixelProbeStagingTexture,
                    0,
                    0,
                    0,
                    0,
                    backBuffer,
                    0,
                    new Box(x, y, 0, x + 1, y + 1, 1));
            }

            _context.Map(_pixelProbeStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var r = ReadHalf(mapped.DataPointer, 0);
                var g = ReadHalf(mapped.DataPointer, 2);
                var b = ReadHalf(mapped.DataPointer, 4);
                if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                {
                    return false;
                }

                scRgb = new Vector3(r, g, b);
                nits = LuminanceSnapshot.ToNits(scRgb);
                return true;
            }
            finally
            {
                _context.Unmap(_pixelProbeStagingTexture, 0);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private bool TryRenderSinglePixelProbe(int x, int y, float panelU)
    {
        if (_context is null
            || _pixelProbeRenderTarget is null
            || _pixelProbeRenderTargetView is null
            || _pixelProbeStagingTexture is null
            || _gainMapVertexShader is null
            || _linearClampSampler is null
            || _gainMapConstantsBuffer is null
            || _primaryTextureView is null
            || _document is null)
        {
            return false;
        }

        var isGainMap = _document.HasRenderableGainMap && _loadedGainMapMode;
        if (isGainMap && (_gainMapPixelShader is null || _gainMapTextureView is null))
        {
            return false;
        }

        if (!isGainMap && _baseImagePixelShader is null)
        {
            return false;
        }

        var savedMode = _viewMode;
        var savedViewerTools = _viewerToolsConstants;
        try
        {
            if (ComparisonEnabled)
            {
                var splitPos = Math.Clamp(ComparisonPosition, 0.0f, 1.0f);
                _viewMode = panelU <= splitPos ? GainmapViewMode.Sdr : GainmapViewMode.Adaptive;
            }

            _viewerToolsConstants = new Vector4(0.0f, 1.0f, 1.0f, 0.0f);
            UpdateGainMapConstantsBuffer();

            _context.OMSetBlendState(null);
            _context.OMSetDepthStencilState(null, 0);
            _context.RSSetState(null);
            _context.OMSetRenderTargets(_pixelProbeRenderTargetView, null);
            _context.RSSetViewport(-x, -y, _pixelWidth, _pixelHeight, 0.0f, 1.0f);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_gainMapVertexShader);
            if (isGainMap)
            {
                _context.PSSetShader(_gainMapPixelShader);
                _context.PSSetShaderResources(0, [_primaryTextureView, _gainMapTextureView!]);
            }
            else
            {
                _context.PSSetShader(_baseImagePixelShader);
                _context.PSSetShaderResource(0, _primaryTextureView);
            }

            _context.PSSetSampler(0, _linearClampSampler);
            _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
            _context.Draw(3, 0);
            _context.PSUnsetShaderResources(0, 2);

            _context.CopySubresourceRegion(
                _pixelProbeStagingTexture,
                0,
                0,
                0,
                0,
                _pixelProbeRenderTarget,
                0,
                new Box(0, 0, 0, 1, 1, 1));
            return true;
        }
        finally
        {
            if (_renderTargetView is not null)
            {
                _context.OMSetRenderTargets(_renderTargetView, null);
            }

            _viewMode = savedMode;
            _viewerToolsConstants = savedViewerTools;
            UpdateGainMapConstantsBuffer();
        }
    }

    private void EnsurePixelProbeResources()
    {
        if (_device is null)
        {
            return;
        }

        if (_pixelProbeRenderTarget is null || _pixelProbeRenderTargetView is null)
        {
            _pixelProbeRenderTargetView?.Dispose();
            _pixelProbeRenderTarget?.Dispose();
            var rtDesc = new Texture2DDescription(
                Format.R16G16B16A16_Float,
                1,
                1,
                arraySize: 1,
                mipLevels: 1,
                BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                sampleCount: 1,
                sampleQuality: 0,
                ResourceOptionFlags.None);
            _pixelProbeRenderTarget = _device.CreateTexture2D(rtDesc);
            _pixelProbeRenderTargetView = _device.CreateRenderTargetView(_pixelProbeRenderTarget, null);
        }

        _pixelProbeStagingTexture ??= _device.CreateTexture2D(new Texture2DDescription(
            Format.R16G16B16A16_Float,
            1,
            1,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None));
    }

    private static Vector2 ApplyShaderOrientation(Vector2 uv, float orientation)
    {
        if (orientation < 1.5f) return uv;
        if (orientation < 2.5f) return new Vector2(1.0f - uv.X, uv.Y);
        if (orientation < 3.5f) return new Vector2(1.0f - uv.X, 1.0f - uv.Y);
        if (orientation < 4.5f) return new Vector2(uv.X, 1.0f - uv.Y);
        if (orientation < 5.5f) return new Vector2(uv.Y, uv.X);
        if (orientation < 6.5f) return new Vector2(uv.Y, 1.0f - uv.X);
        if (orientation < 7.5f) return new Vector2(1.0f - uv.Y, 1.0f - uv.X);
        return new Vector2(1.0f - uv.Y, uv.X);
    }

    public async Task<LuminanceSnapshot?> AnalyzeCurrentPreviewAsync(
        bool includeGainMapComparison = false,
        float minimumChromaticityNits = 0,
        CancellationToken cancellationToken = default)
    {
        CapturedAnalysisFrame? frame = null;
        CapturedAnalysisFrame? sdrFrame = null;
        CapturedAnalysisFrame? alternateFrame = null;
        try
        {
            await _renderOperationGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearAnalysis();
                if (_document is null || _pixelWidth <= 0 || _pixelHeight <= 0)
                {
                    return null;
                }

                try
                {
                    await EnsureTexturesReadyForAnalysisAsync(cancellationToken);
                    var captureComparison = includeGainMapComparison && _document.HasRenderableGainMap && _loadedGainMapMode;
                    CapturePreviewAnalysisFramesOffscreen(captureComparison, out frame, out sdrFrame, out alternateFrame);
                }
                catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
                {
                    frame?.ReturnBuffer();
                    sdrFrame?.ReturnBuffer();
                    alternateFrame?.ReturnBuffer();
                    frame = sdrFrame = alternateFrame = null;

                    cancellationToken.ThrowIfCancellationRequested();
                    RecoverFromDeviceLost(_pixelWidth, _pixelHeight);
                    await EnsureTexturesReadyForAnalysisAsync(cancellationToken);
                    var captureComparison = includeGainMapComparison && _document.HasRenderableGainMap && _loadedGainMapMode;
                    CapturePreviewAnalysisFramesOffscreen(captureComparison, out frame, out sdrFrame, out alternateFrame);
                }
            }
            finally
            {
                _renderOperationGate.Release();
            }

            if (frame is null)
            {
                return null;
            }

            // The render thread owns capture/Map/Unmap. The worker receives only
            // detached pooled strip buffers and value types, after the render gate is released.
            return await _previewAnalysis.AnalyzeAsync(
                frame.Version,
                token =>
                {
                    LuminanceSnapshot Analyze(CapturedAnalysisFrame captured, bool retainGrid, GainMapChromaticityComparison? gmComparison, CancellationToken ct) =>
                        new(
                            captured.Width,
                            captured.Height,
                            captured.Strips,
                            captured.Layout,
                            captured.Version,
                            captured.SdrWhiteNits,
                            minimumChromaticityNits,
                            retainGrid,
                            ct)
                        {
                            GainMapComparison = gmComparison,
                        };

                    var comparison = sdrFrame is not null && alternateFrame is not null
                        ? new GainMapChromaticityComparison(
                            Analyze(sdrFrame, retainGrid: false, gmComparison: null, token),
                            Analyze(alternateFrame, retainGrid: false, gmComparison: null, token))
                        : null;
                    return Analyze(frame, retainGrid: true, comparison, token);
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (frame is null || frame.Version == PreviewVersion)
            {
                AnalysisError = ex.Message;
            }

            return null;
        }
        finally
        {
            frame?.ReturnBuffer();
            sdrFrame?.ReturnBuffer();
            alternateFrame?.ReturnBuffer();
        }
    }

    private async Task EnsureTexturesReadyForAnalysisAsync(CancellationToken cancellationToken)
    {
        if (_document is null)
        {
            return;
        }

        EnsureDevice();
        EnsureGainMapDeviceResources();

        if (_document.HasRenderableGainMap)
        {
            if (_primaryTextureView is null || _gainMapTextureView is null || !_loadedGainMapMode)
            {
                if (_swapChain is null && _panel is not null && _pixelWidth > 0 && _pixelHeight > 0)
                {
                    CreateSwapChain(_pixelWidth, _pixelHeight);
                }

                if (_swapChain is not null)
                {
                    await PresentGainMapFrameAsync(_document, cancellationToken);
                }
            }
        }
        else if (_primaryTextureView is null || _loadedGainMapMode)
        {
            if (_swapChain is null && _panel is not null && _pixelWidth > 0 && _pixelHeight > 0)
            {
                CreateSwapChain(_pixelWidth, _pixelHeight);
            }

            if (_swapChain is not null)
            {
                await PresentBaseImageFrameAsync(_document, cancellationToken);
            }
        }
    }

    internal async Task LoadOffscreenForTestingAsync(
        HdrImageDocument document,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureDevice();
            EnsureGainMapDeviceResources();
            _document = document;
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            _renderImageLayout = null;
            var decodeMaxPixelSize = CalculateViewerDecodeMaxPixelSize(document);
            if (document.HasRenderableGainMap)
            {
                var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, decodeMaxPixelSize, cancellationToken);
                _ = LoadGainMapTextures(inputs);
                _loadedGainMapMode = true;
            }
            else
            {
                var bitmap = await BitmapDecodeService.DecodeDocumentAsync(document, decodeMaxPixelSize, cancellationToken);
                LoadBaseImageTexture(bitmap);
                _loadedGainMapMode = false;
            }

            _loadedGainMapPath = document.Path;
            _loadedGainMapWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
            _loadedDecodeMaxPixelSize = decodeMaxPixelSize;
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    internal async Task<RenderedFrameCapture?> CaptureRenderedFrameAsync(
        CancellationToken cancellationToken = default)
    {
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_document is null || _pixelWidth <= 0 || _pixelHeight <= 0)
            {
                return null;
            }

            try
            {
                await EnsureTexturesReadyForAnalysisAsync(cancellationToken);
                return CaptureRenderedPreviewStripsOffscreen();
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverFromDeviceLost(_pixelWidth, _pixelHeight);
                await EnsureTexturesReadyForAnalysisAsync(cancellationToken);
                return CaptureRenderedPreviewStripsOffscreen();
            }
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private static int CalculateAnalysisStripHeight(int pixelWidth, int pixelHeight) =>
        Math.Clamp(
            MaxAnalysisStripPixels / Math.Max(1, pixelWidth),
            1,
            Math.Max(1, pixelHeight));

    private void EnsureAnalysisGpuResources(int width, int height)
    {
        if (_device is null)
        {
            return;
        }

        if (_analysisRenderTarget is not null
            && _analysisRenderTargetView is not null
            && _analysisStagingTexture is not null
            && _analysisTextureWidth == width
            && _analysisTextureHeight == height)
        {
            return;
        }

        _analysisRenderTargetView?.Dispose();
        _analysisRenderTarget?.Dispose();
        _analysisStagingTexture?.Dispose();
        _analysisRenderTargetView = null;
        _analysisRenderTarget = null;
        _analysisStagingTexture = null;
        _analysisTextureWidth = 0;
        _analysisTextureHeight = 0;

        var rtDesc = new Texture2DDescription(
            Format.R16G16B16A16_Float,
            checked((uint)width),
            checked((uint)height),
            arraySize: 1,
            mipLevels: 1,
            BindFlags.RenderTarget,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);
        var stagingDesc = new Texture2DDescription(
            Format.R16G16B16A16_Float,
            checked((uint)width),
            checked((uint)height),
            arraySize: 1,
            mipLevels: 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _analysisRenderTarget = _device.CreateTexture2D(rtDesc);
        _analysisRenderTargetView = _device.CreateRenderTargetView(_analysisRenderTarget, null);
        _analysisStagingTexture = _device.CreateTexture2D(stagingDesc);
        _analysisTextureWidth = width;
        _analysisTextureHeight = height;
    }

    private void CapturePreviewAnalysisFramesOffscreen(
        bool captureComparison,
        out CapturedAnalysisFrame? frame,
        out CapturedAnalysisFrame? sdrFrame,
        out CapturedAnalysisFrame? alternateFrame)
    {
        frame = null;
        sdrFrame = null;
        alternateFrame = null;
        AnalysisError = null;

        if (_device is null
            || _context is null
            || _document is null
            || _gainMapVertexShader is null
            || _linearClampSampler is null
            || _gainMapConstantsBuffer is null
            || _primaryTextureView is null
            || _pixelWidth <= 0
            || _pixelHeight <= 0)
        {
            return;
        }

        var isGainMap = _document.HasRenderableGainMap && _loadedGainMapMode;
        if (isGainMap && (_gainMapPixelShader is null || _gainMapTextureView is null))
        {
            return;
        }

        if (!isGainMap && _baseImagePixelShader is null)
        {
            return;
        }

        var fullWidth = _pixelWidth;
        var fullHeight = _pixelHeight;
        var stripHeight = CalculateAnalysisStripHeight(fullWidth, fullHeight);
        EnsureAnalysisGpuResources(fullWidth, stripHeight);
        if (_analysisRenderTarget is null || _analysisRenderTargetView is null || _analysisStagingTexture is null)
        {
            return;
        }

        var savedMode = _viewMode;
        var savedGamutMode = _colorGamutMappingMode;
        var savedViewerTools = _viewerToolsConstants;
        var version = PreviewVersion;
        var layout = GetCurrentImageLayout();
        var sdrWhiteNits = EffectiveSceneToSdrWhiteScale * 80.0f;

        try
        {
            _context.OMSetBlendState(null);
            _context.OMSetDepthStencilState(null, 0);
            _context.RSSetState(null);
            _context.OMSetRenderTargets(_analysisRenderTargetView, null);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_gainMapVertexShader);
            if (isGainMap)
            {
                _context.PSSetShader(_gainMapPixelShader);
                _context.PSSetShaderResources(0, [_primaryTextureView, _gainMapTextureView!]);
            }
            else
            {
                _context.PSSetShader(_baseImagePixelShader);
                _context.PSSetShaderResource(0, _primaryTextureView);
            }

            _context.PSSetSampler(0, _linearClampSampler);
            _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
            _viewerToolsConstants = new Vector4(0.0f, 1.0f, 1.0f, 0.0f);

            if (captureComparison && isGainMap)
            {
                _colorGamutMappingMode = ColorGamutMappingMode.Managed;

                _viewMode = GainmapViewMode.Sdr;
                UpdateGainMapConstantsBuffer();
                sdrFrame = CaptureModeFrameInStrips(fullWidth, fullHeight, stripHeight, layout, version, sdrWhiteNits);

                _viewMode = GainmapViewMode.AlternateImage;
                UpdateGainMapConstantsBuffer();
                alternateFrame = CaptureModeFrameInStrips(fullWidth, fullHeight, stripHeight, layout, version, sdrWhiteNits);

                _colorGamutMappingMode = savedGamutMode;
            }

            _viewMode = ComparisonEnabled ? GainmapViewMode.Adaptive : savedMode;
            UpdateGainMapConstantsBuffer();
            frame = CaptureModeFrameInStrips(fullWidth, fullHeight, stripHeight, layout, version, sdrWhiteNits);
        }
        catch (SharpGenException ex) when (IsDeviceLost(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            frame?.ReturnBuffer();
            sdrFrame?.ReturnBuffer();
            alternateFrame?.ReturnBuffer();
            frame = sdrFrame = alternateFrame = null;
            AnalysisError = ex.Message;
        }
        finally
        {
            _viewMode = savedMode;
            _colorGamutMappingMode = savedGamutMode;
            _viewerToolsConstants = savedViewerTools;
            _context.PSUnsetShaderResources(0, 2);
            if (_renderTargetView is not null)
            {
                _context.OMSetRenderTargets(_renderTargetView, null);
            }

            UpdateGainMapConstantsBuffer();
        }
    }

    private RenderedFrameCapture? CaptureRenderedPreviewStripsOffscreen()
    {
        if (_device is null
            || _context is null
            || _document is null
            || _gainMapVertexShader is null
            || _linearClampSampler is null
            || _gainMapConstantsBuffer is null
            || _primaryTextureView is null
            || _pixelWidth <= 0
            || _pixelHeight <= 0)
        {
            return null;
        }

        var isGainMap = _document.HasRenderableGainMap && _loadedGainMapMode;
        if (isGainMap && (_gainMapPixelShader is null || _gainMapTextureView is null))
        {
            return null;
        }

        if (!isGainMap && _baseImagePixelShader is null)
        {
            return null;
        }

        var fullWidth = _pixelWidth;
        var fullHeight = _pixelHeight;
        var stripHeight = CalculateAnalysisStripHeight(fullWidth, fullHeight);
        EnsureAnalysisGpuResources(fullWidth, stripHeight);
        if (_analysisRenderTarget is null || _analysisRenderTargetView is null || _analysisStagingTexture is null)
        {
            return null;
        }

        var savedMode = _viewMode;
        var savedViewerTools = _viewerToolsConstants;
        var layout = GetCurrentImageLayout();
        var sdrWhiteNits = EffectiveSceneToSdrWhiteScale * 80.0f;
        var stripCount = (fullHeight + stripHeight - 1) / stripHeight;
        var strips = new AnalysisStrip[stripCount];
        var capturedCount = 0;

        try
        {
            _context.OMSetBlendState(null);
            _context.OMSetDepthStencilState(null, 0);
            _context.RSSetState(null);
            _context.OMSetRenderTargets(_analysisRenderTargetView, null);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_gainMapVertexShader);
            if (isGainMap)
            {
                _context.PSSetShader(_gainMapPixelShader);
                _context.PSSetShaderResources(0, [_primaryTextureView, _gainMapTextureView!]);
            }
            else
            {
                _context.PSSetShader(_baseImagePixelShader);
                _context.PSSetShaderResource(0, _primaryTextureView);
            }

            _context.PSSetSampler(0, _linearClampSampler);
            _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);

            for (var startY = 0; startY < fullHeight; startY += stripHeight)
            {
                var rows = Math.Min(stripHeight, fullHeight - startY);
                _context.ClearRenderTargetView(_analysisRenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                _context.RSSetViewport(0.0f, -startY, fullWidth, fullHeight, 0.0f, 1.0f);

                if (ComparisonEnabled)
                {
                    var splitPos = Math.Clamp(ComparisonPosition, 0.0f, 1.0f);
                    if (splitPos < 1.0f)
                    {
                        _viewMode = GainmapViewMode.Adaptive;
                        _viewerToolsConstants = new Vector4(splitPos, 1.0f, 1.0f, 0.0f);
                        UpdateGainMapConstantsBuffer();
                        _context.Draw(3, 0);
                    }

                    if (splitPos > 0.0f)
                    {
                        _viewMode = GainmapViewMode.Sdr;
                        _viewerToolsConstants = new Vector4(0.0f, splitPos, 1.0f, 0.0f);
                        UpdateGainMapConstantsBuffer();
                        _context.Draw(3, 0);
                    }
                }
                else
                {
                    _viewMode = savedMode;
                    _viewerToolsConstants = new Vector4(0.0f, 1.0f, 1.0f, 0.0f);
                    UpdateGainMapConstantsBuffer();
                    _context.Draw(3, 0);
                }

                strips[capturedCount++] = ReadbackAnalysisStrip(fullWidth, startY, rows);
            }

            return new RenderedFrameCapture(fullWidth, fullHeight, strips, layout, sdrWhiteNits);
        }
        catch
        {
            for (var i = 0; i < capturedCount; i++)
            {
                ArrayPool<byte>.Shared.Return(strips[i].Pixels);
            }

            throw;
        }
        finally
        {
            _viewMode = savedMode;
            _viewerToolsConstants = savedViewerTools;
            _context.PSUnsetShaderResources(0, 2);
            if (_renderTargetView is not null)
            {
                _context.OMSetRenderTargets(_renderTargetView, null);
            }

            UpdateGainMapConstantsBuffer();
        }
    }

    private CapturedAnalysisFrame CaptureModeFrameInStrips(
        int fullWidth,
        int fullHeight,
        int stripHeight,
        Vector4 layout,
        long version,
        float sdrWhiteNits)
    {
        var stripCount = (fullHeight + stripHeight - 1) / stripHeight;
        var strips = new AnalysisStrip[stripCount];
        var capturedCount = 0;
        try
        {
            for (var startY = 0; startY < fullHeight; startY += stripHeight)
            {
                var rows = Math.Min(stripHeight, fullHeight - startY);
                _context!.ClearRenderTargetView(_analysisRenderTargetView!, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                _context.RSSetViewport(0.0f, -startY, fullWidth, fullHeight, 0.0f, 1.0f);
                _context.Draw(3, 0);
                strips[capturedCount++] = ReadbackAnalysisStrip(fullWidth, startY, rows);
            }

            return new CapturedAnalysisFrame(fullWidth, fullHeight, strips, layout, version, sdrWhiteNits);
        }
        catch
        {
            for (var i = 0; i < capturedCount; i++)
            {
                ArrayPool<byte>.Shared.Return(strips[i].Pixels);
            }

            throw;
        }
    }

    private AnalysisStrip ReadbackAnalysisStrip(int width, int startY, int rows)
    {
        var rowByteLength = checked(width * 8);
        var totalByteLength = checked(rowByteLength * rows);
        var buffer = ArrayPool<byte>.Shared.Rent(totalByteLength);
        try
        {
            _context!.CopySubresourceRegion(
                _analysisStagingTexture!,
                0,
                0,
                0,
                0,
                _analysisRenderTarget!,
                0,
                new Box(0, 0, 0, width, rows, 1));
            _context.Map(_analysisStagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                for (var y = 0; y < rows; y++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)),
                        buffer,
                        y * rowByteLength,
                        rowByteLength);
                }
            }
            finally
            {
                _context.Unmap(_analysisStagingTexture!, 0);
            }

            return new AnalysisStrip(startY, rows, buffer);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private void DrawWithViewerTools()
    {
        _previewAnalysis.Invalidate();
        var savedMode = _viewMode;
        try
        {
            if (ComparisonEnabled)
            {
                var splitPos = Math.Clamp(ComparisonPosition, 0.0f, 1.0f);
                if (splitPos < 1.0f)
                {
                    // The comparison's HDR side always uses system/manual adaptive HDR.
                    _viewMode = GainmapViewMode.Adaptive;
                    _viewerToolsConstants = new Vector4(splitPos, 1.0f, 0.0f, 0.0f);
                    UpdateGainMapConstantsBuffer();
                    _context!.Draw(3, 0);
                }

                if (splitPos > 0.0f)
                {
                    _viewMode = GainmapViewMode.Sdr;
                    _viewerToolsConstants = new Vector4(0.0f, splitPos, 0.0f, 0.0f);
                    UpdateGainMapConstantsBuffer();
                    _context!.Draw(3, 0);
                }
            }
            else
            {
                _viewerToolsConstants = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);
                UpdateGainMapConstantsBuffer();
                _context!.Draw(3, 0);
            }
        }
        finally
        {
            _viewMode = savedMode;
            _viewerToolsConstants = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);
            if (ComparisonEnabled)
            {
                UpdateGainMapConstantsBuffer();
            }
        }
    }

    internal sealed class RenderedFrameCapture(
        int width,
        int height,
        AnalysisStrip[] strips,
        Vector4 layout,
        float sdrWhiteNits) : IDisposable
    {
        private int _disposed;

        public int Width { get; } = width;
        public int Height { get; } = height;
        public IReadOnlyList<AnalysisStrip> Strips { get; } = strips;
        public Vector4 Layout { get; } = layout;
        public float SdrWhiteNits { get; } = sdrWhiteNits;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                foreach (var strip in strips)
                {
                    ArrayPool<byte>.Shared.Return(strip.Pixels);
                }
            }
        }
    }

    private sealed class CapturedAnalysisFrame(
        int width,
        int height,
        AnalysisStrip[] strips,
        Vector4 layout,
        long version,
        float sdrWhiteNits)
    {
        private int _returned;

        public int Width { get; } = width;
        public int Height { get; } = height;
        public IReadOnlyList<AnalysisStrip> Strips { get; } = strips;
        public Vector4 Layout { get; } = layout;
        public long Version { get; } = version;
        public float SdrWhiteNits { get; } = sdrWhiteNits;

        public void ReturnBuffer()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                foreach (var strip in strips)
                {
                    ArrayPool<byte>.Shared.Return(strip.Pixels);
                }
            }
        }
    }
}
