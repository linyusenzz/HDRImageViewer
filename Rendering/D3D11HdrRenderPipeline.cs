using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2D = Vortice.Direct2D1.D2D1;
using D2DColorManagement = Vortice.Direct2D1.Effects.ColorManagement;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline : IHdrRenderPipeline, IDisposable
{
    internal const int MaxTextureDimension = 16384;
    private const float ToneModeGainMap = 0.0f;
    private const float ToneModeSingleLayerSystem = 1.0f;
    private const float ToneModeSingleLayerDisplayFit = 2.0f;
    private const float SingleLayerHdrReferenceWhiteScale = HdrColorMath.UltraHdrReferenceWhiteNits / HdrColorMath.ReferenceWhiteNits;
    private const float HlgReferenceScenePeak = 1000.0f / 80.0f;
    private const float HlgReferenceWhiteScene = 203.0f / 80.0f;
    private const string GainMapVertexShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.GainMap.VS.cso";
    private const string GainMapPixelShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.GainMap.PS.cso";
    private const string BaseImagePixelShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.BaseImage.PS.cso";
    private const string LinearizePixelShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.Linearize.PS.cso";

    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
    ];

    private static readonly Guid WinUiSwapChainPanelNativeGuid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    // Loading a document awaits background decode. Keep LoadAsync and the
    // size-change driven ResizeAsync from interleaving their D3D resource
    // updates while either operation is suspended.
    private readonly SemaphoreSlim _renderOperationGate = new(1, 1);
    private SwapChainPanel? _panel;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private IDXGISwapChain3? _swapChain3;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1DeviceContext2? _d2dContext2;
    private ID2D1DeviceContext5? _d2dContext5;
    private ID2D1Bitmap1? _d2dTargetBitmap;
    private IWICBitmapDecoder? _d2dBaseDecoder;
    private IWICBitmapFrameDecode? _d2dBaseFrame;
    private IWICBitmapSource? _d2dBaseWicSource;
    private ID2D1ImageSourceFromWic? _d2dBaseImageSource;
    private ID2D1ColorContext? _d2dBaseSourceColorContext;
    private ID2D1ColorContext? _d2dBaseDestinationColorContext;
    private D2DColorManagement? _d2dBaseColorManagement;
    private IWICImagingFactory? _wicFactory;
    private ID3D11VertexShader? _gainMapVertexShader;
    private ID3D11PixelShader? _gainMapPixelShader;
    private ID3D11PixelShader? _baseImagePixelShader;
    private ID3D11PixelShader? _linearizePixelShader;
    private ID3D11SamplerState? _linearClampSampler;
    private ID3D11Buffer? _gainMapConstantsBuffer;
    private ID3D11Texture2D? _primaryTexture;
    private ID3D11Texture2D? _gainMapTexture;
    private ID3D11ShaderResourceView? _primaryTextureView;
    private ID3D11ShaderResourceView? _gainMapTextureView;
    private ID3D11Texture2D? _frameAnalysisStagingTexture;
    private int _frameAnalysisStagingWidth;
    private int _frameAnalysisStagingHeight;
    private BitmapAnalysisSource? _primaryAnalysisSource;
    private GainMapAnalysisSource? _gainMapAnalysisSource;
    private string _baseDecoderName = "none";
    private string _baseEncodingSummary = "none";
    private string? _d2dFallbackStatus;
    private string? _d2dBasePath;
    private DateTime? _d2dBaseWriteTimeUtc;
    private string _d2dWicSourceSummary = "none";
    private string _d2dSourceColorSummary = "none";
    private string _d2dDestinationColorSummary = "none";
    private HdrImageDocument? _document;
    private string? _loadedGainMapPath;
    private DateTime? _loadedGainMapWriteTimeUtc;
    private bool _loadedGainMapMode;
    private int? _loadedDecodeMaxPixelSize;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _contentPixelWidth;
    private int _contentPixelHeight;
    private float _contentOrientation = 1.0f;
    private bool _isDisposed;
    private long _deviceGeneration;
    private bool _scRgbColorSpaceAvailable;
    private bool _scRgbColorSpaceApplied;
    private string _panelBindingStatus = "WinUI swap chain not bound";
    private string _swapChainTransformStatus = "swap chain DPI transform not set";
    private float? _displayCapacityOverrideLog2;
    private bool _adaptiveToneMappingEnabled;
    private float _referenceWhiteExposureScale = 1.0f;
    private ColorGamutMappingMode _colorGamutMappingMode = ColorGamutMappingMode.Managed;
    private GainmapViewMode _viewMode = GainmapViewMode.Adaptive;
    private HdrHeadroomMode _headroomMode = HdrHeadroomMode.SystemAdaptive;
    private bool _toneMappingEnabledForCurrentFrame;
    private ToneMapAnalysis _toneMapAnalysis;
    private GainMapShaderConstants _gainMapConstants;
    private HdrDisplayConfiguration _displayConfiguration = HdrDisplayConfiguration.Unknown;
    private Vector4? _renderImageLayout;
    private readonly ViewModeAnalysisCache<ToneMapModeState> _toneMapModeCache = new();
    private CachedToneMapSampleStats? _cachedToneMapSampleStats;
    private string? _gainSampleStatsSummary;
    private bool _frameVerificationPending;
    private FrameAnalysis _lastFrameAnalysis = new(false, 0.0f, "frame verification not requested");

    public HdrRenderIntent Intent
    {
        get => _viewMode switch
        {
            GainmapViewMode.Sdr => HdrRenderIntent.ShowBaseSdr,
            GainmapViewMode.GainMap => HdrRenderIntent.ShowGainMap,
            _ => HdrRenderIntent.ReconstructHdr,
        };
        set
        {
            ViewMode = value switch
            {
                HdrRenderIntent.ShowBaseSdr or HdrRenderIntent.ToneMapToSdr => GainmapViewMode.Sdr,
                HdrRenderIntent.ShowGainMap => GainmapViewMode.GainMap,
                _ => GainmapViewMode.Adaptive,
            };
        }
    }

    public GainmapViewMode ViewMode
    {
        get => _viewMode;
        set => ApplySettings(Settings with { ViewMode = value });
    }

    public HdrHeadroomMode HeadroomMode
    {
        get => _headroomMode;
        set => ApplySettings(Settings with { HeadroomMode = value });
    }

    public float? DisplayCapacityOverrideLog2
    {
        get => _displayCapacityOverrideLog2;
        set => ApplySettings(Settings with { DisplayCapacityOverrideLog2 = value });
    }

    public bool AdaptiveToneMappingEnabled
    {
        get => _adaptiveToneMappingEnabled;
        set => ApplySettings(Settings with { AdaptiveToneMappingEnabled = value });
    }

    // Exposure / diffuse-white scale applied before tone mapping. 1.0 keeps the
    // content's default reference white; values below 1.0 dim the image and
    // values above 1.0 brighten it. The shader uses this for single-layer HDR
    // and gain-map HDR output paths.
    public float ReferenceWhiteExposureScale
    {
        get => _referenceWhiteExposureScale;
        set => ApplySettings(Settings with { ReferenceWhiteExposureScale = value });
    }

    public ColorGamutMappingMode ColorGamutMappingMode
    {
        get => _colorGamutMappingMode;
        set => ApplySettings(Settings with { ColorGamutMappingMode = value });
    }

    public HdrRendererSettings Settings => new(
        _viewMode,
        _headroomMode,
        _displayCapacityOverrideLog2,
        _adaptiveToneMappingEnabled,
        _referenceWhiteExposureScale,
        _colorGamutMappingMode,
        _displayConfiguration);

    public bool ApplySettings(HdrRendererSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var viewMode = Enum.IsDefined(settings.ViewMode)
            ? settings.ViewMode
            : GainmapViewMode.Adaptive;
        var headroomMode = Enum.IsDefined(settings.HeadroomMode)
            ? settings.HeadroomMode
            : HdrHeadroomMode.SystemAdaptive;
        float? displayCapacityOverride = settings.DisplayCapacityOverrideLog2 is { } capacity
            && float.IsFinite(capacity)
                ? Math.Clamp(capacity, 0.0f, 16.0f)
                : null;
        var exposureScale = float.IsFinite(settings.ReferenceWhiteExposureScale)
            ? Math.Clamp(settings.ReferenceWhiteExposureScale, 0.05f, 16.0f)
            : 1.0f;
        var gamutMappingMode = Enum.IsDefined(settings.ColorGamutMappingMode)
            ? settings.ColorGamutMappingMode
            : ColorGamutMappingMode.Managed;
        var displayConfiguration = settings.DisplayConfiguration ?? HdrDisplayConfiguration.Unknown;

        if (_viewMode == viewMode
            && _headroomMode == headroomMode
            && Nullable.Equals(_displayCapacityOverrideLog2, displayCapacityOverride)
            && _adaptiveToneMappingEnabled == settings.AdaptiveToneMappingEnabled
            && Math.Abs(_referenceWhiteExposureScale - exposureScale) <= 0.0001f
            && _colorGamutMappingMode == gamutMappingMode
            && Equals(_displayConfiguration, displayConfiguration))
        {
            return false;
        }

        _viewMode = viewMode;
        _headroomMode = headroomMode;
        _displayCapacityOverrideLog2 = displayCapacityOverride;
        _adaptiveToneMappingEnabled = settings.AdaptiveToneMappingEnabled;
        _referenceWhiteExposureScale = exposureScale;
        _colorGamutMappingMode = gamutMappingMode;
        _displayConfiguration = displayConfiguration;
        InvalidateToneMapAnalysis();
        _previewAnalysis.Invalidate();
        return true;
    }

    public string LastRenderStatus { get; private set; } = "Renderer not attached";

    public bool LastFrameHasVisiblePixels { get; private set; }

    public bool IsSwapChainPanelBound { get; private set; }

    public long DeviceGeneration => _deviceGeneration;

    public int ContentPixelWidth => _contentPixelWidth;

    public int ContentPixelHeight => _contentPixelHeight;

    public float ContentOrientation => _contentOrientation;

    public double? ContentDisplayAspectRatio
    {
        get
        {
            if (_contentPixelWidth <= 0 || _contentPixelHeight <= 0)
            {
                return null;
            }

            return OrientationSwapsDimensions(_contentOrientation)
                ? (double)_contentPixelHeight / _contentPixelWidth
                : (double)_contentPixelWidth / _contentPixelHeight;
        }
    }

    public HdrDisplayConfiguration DisplayConfiguration
    {
        get => _displayConfiguration;
        set => ApplySettings(Settings with
        {
            DisplayConfiguration = value ?? HdrDisplayConfiguration.Unknown,
        });
    }

    public void Attach(SwapChainPanel panel)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var panelChanged = !ReferenceEquals(_panel, panel);
        _panel = panel;
        if (panelChanged && _swapChain is not null)
        {
            ConfigureSwapChainPanelScale();
            TryBindSwapChainToPanel();
        }

        LastRenderStatus = "Renderer attached";
    }

    public void InvalidateImageCache()
    {
        _loadedGainMapPath = null;
        _loadedGainMapWriteTimeUtc = null;
        _loadedGainMapMode = false;
        _loadedDecodeMaxPixelSize = null;
    }

    public void DetachSwapChainForXamlFallback()
    {
        DetachSwapChainFromPanel();
        _panelBindingStatus = "WinUI swap chain detached for XAML fallback";
    }

    public void RestoreSwapChainPanelBinding()
    {
        if (_swapChain is not null)
        {
            TryBindSwapChainToPanel();
        }
    }

    public Task LoadAsync(HdrImageDocument document, CancellationToken cancellationToken)
    {
        return LoadCoreAsync(document, fullResolution: false, cancellationToken);
    }

    internal Task LoadAsync(HdrImageDocument document, HdrRenderViewport viewport, CancellationToken cancellationToken) =>
        LoadCoreAsync(document, fullResolution: false, cancellationToken, viewport);

    public Task LoadFullResolutionAsync(HdrImageDocument document, CancellationToken cancellationToken)
    {
        return LoadCoreAsync(document, fullResolution: true, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _document = null;
            ReleaseGainMapResources();
            LastFrameHasVisiblePixels = false;
            _frameVerificationPending = false;
            _lastFrameAnalysis = new FrameAnalysis(false, 0.0f, "frame verification not requested");

            try
            {
                EnsureRenderTargetView();
                if (_context is not null && _swapChain is not null && _renderTargetView is not null)
                {
                    _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                    _swapChain.Present(1, PresentFlags.None).CheckError();
                }
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                RecoverFromDeviceLost(_pixelWidth, _pixelHeight);
                EnsureRenderTargetView();
                if (_context is not null && _swapChain is not null && _renderTargetView is not null)
                {
                    _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                    _swapChain.Present(1, PresentFlags.None).CheckError();
                }
            }

            LastRenderStatus = "Renderer cleared";
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private async Task LoadCoreAsync(
        HdrImageDocument document,
        bool fullResolution,
        CancellationToken cancellationToken,
        HdrRenderViewport? viewport = null)
    {
        if (viewport is { IsValid: false })
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }

        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await ExecuteLoadCoreAsync(document, fullResolution, viewport, cancellationToken);
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverFromDeviceLost(
                    viewport?.PixelWidth ?? _pixelWidth,
                    viewport?.PixelHeight ?? _pixelHeight);
                await ExecuteLoadCoreAsync(document, fullResolution, viewport, cancellationToken);
            }
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private async Task ExecuteLoadCoreAsync(
        HdrImageDocument document,
        bool fullResolution,
        HdrRenderViewport? viewport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (viewport is { } target)
        {
            _renderImageLayout = target.ImageLayout;
            EnsureDevice();
            if (_swapChain is null)
            {
                CreateSwapChain(target.PixelWidth, target.PixelHeight);
            }
            else if (_pixelWidth != target.PixelWidth || _pixelHeight != target.PixelHeight)
            {
                ResizeSwapChain(target.PixelWidth, target.PixelHeight);
            }
            else
            {
                ConfigureSwapChainPanelScale();
            }
        }
        else if (_panel is not null && _pixelWidth > 0 && _pixelHeight > 0 && (_device is null || _swapChain is null))
        {
            EnsureDevice();
            if (_swapChain is null)
            {
                CreateSwapChain(_pixelWidth, _pixelHeight);
            }
        }

        _document = document;
        if (document.HasRenderableGainMap && _swapChain is not null)
        {
            await PresentGainMapFrameAsync(document, cancellationToken, fullResolution);
            return;
        }

        if (_swapChain is not null)
        {
            await PresentBaseImageFrameAsync(document, cancellationToken, fullResolution);
            return;
        }

        PresentProbeFrame();
    }

    public Task ResizeAsync(
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken)
    {
        return ResizeCoreAsync(
            pixelWidth,
            pixelHeight,
            imageLayout: null,
            cancellationToken);
    }

    internal Task ResizeAsync(
        HdrRenderViewport viewport,
        CancellationToken cancellationToken)
    {
        if (!viewport.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                "Render viewport dimensions and image layout must be finite and positive.");
        }

        return ResizeCoreAsync(
            viewport.PixelWidth,
            viewport.PixelHeight,
            viewport.ImageLayout,
            cancellationToken);
    }

    internal async Task RedrawAsync(
        HdrRenderViewport viewport,
        CancellationToken cancellationToken)
    {
        if (!viewport.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                "Render viewport dimensions and image layout must be finite and positive.");
        }

        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await ExecuteRedrawCoreAsync(viewport, cancellationToken);
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverFromDeviceLost(viewport.PixelWidth, viewport.PixelHeight);
                await ExecuteRedrawCoreAsync(viewport, cancellationToken);
            }
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private async Task ExecuteRedrawCoreAsync(
        HdrRenderViewport viewport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _renderImageLayout = viewport.ImageLayout;

        if (_panel is null)
        {
            LastRenderStatus = "Viewport redraw skipped: panel missing";
            return;
        }

        EnsureDevice();
        if (_swapChain is null)
        {
            CreateSwapChain(viewport.PixelWidth, viewport.PixelHeight);
        }
        else if (_pixelWidth != viewport.PixelWidth || _pixelHeight != viewport.PixelHeight)
        {
            ResizeSwapChain(viewport.PixelWidth, viewport.PixelHeight);
        }
        else
        {
            ConfigureSwapChainPanelScale();
        }

        if (_document?.HasRenderableGainMap == true)
        {
            if (_primaryTextureView is not null && _gainMapTextureView is not null)
            {
                RenderGainMap();
            }
            else
            {
                await PresentGainMapFrameAsync(_document, cancellationToken);
            }

            return;
        }

        if (_document is not null)
        {
            if (_primaryTextureView is not null)
            {
                RenderBaseImage(_document);
            }
            else
            {
                await PresentBaseImageFrameAsync(_document, cancellationToken);
            }

            return;
        }

        PresentProbeFrame();
    }

    internal void RequestFrameVerification()
    {
        _frameVerificationPending = true;
    }

    private async Task ResizeCoreAsync(
        int pixelWidth,
        int pixelHeight,
        Vector4? imageLayout,
        CancellationToken cancellationToken)
    {
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await ExecuteResizeCoreAsync(pixelWidth, pixelHeight, imageLayout, cancellationToken);
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverFromDeviceLost(pixelWidth, pixelHeight);
                await ExecuteResizeCoreAsync(pixelWidth, pixelHeight, imageLayout, cancellationToken);
            }
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    private async Task ExecuteResizeCoreAsync(
        int pixelWidth,
        int pixelHeight,
        Vector4? imageLayout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _renderImageLayout = imageLayout;

        if (_panel is null || pixelWidth <= 0 || pixelHeight <= 0)
        {
            LastRenderStatus = $"Resize skipped: panel={_panel is not null}, size={pixelWidth}x{pixelHeight}";
            return;
        }

        EnsureDevice();

        if (_swapChain is null)
        {
            CreateSwapChain(pixelWidth, pixelHeight);
        }
        else if (_pixelWidth != pixelWidth || _pixelHeight != pixelHeight)
        {
            ResizeSwapChain(pixelWidth, pixelHeight);
        }
        else
        {
            ConfigureSwapChainPanelScale();
        }

        if (_document?.HasRenderableGainMap == true)
        {
            await PresentGainMapFrameAsync(_document, cancellationToken);
            return;
        }

        if (_document is not null)
        {
            await PresentBaseImageFrameAsync(_document, cancellationToken);
            return;
        }

        PresentProbeFrame();
    }

    public void Dispose()
    {
        _isDisposed = true;
        _previewAnalysis.Dispose();
        ResetDeviceResources();
        _renderOperationGate.Dispose();
    }

    private async Task PresentGainMapFrameAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken,
        bool fullResolution = false)
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            LastRenderStatus = "Gain-map render skipped: device, swap chain, or size missing";
            return;
        }

        EnsureGainMapDeviceResources();
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
        int? decodeMaxPixelSize = fullResolution ? MaxTextureDimension : CalculateViewerDecodeMaxPixelSize(document);
        var decodeMs = 0L;
        var uploadMs = 0L;
        if (!string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase)
            || _loadedGainMapWriteTimeUtc != lastWriteTimeUtc
            || !_loadedGainMapMode
            || !DecodedCachePolicy.IsAtLeastAsDetailed(_loadedDecodeMaxPixelSize, decodeMaxPixelSize))
        {
            try
            {
                var wasPreloaded = ImagePreloadCache.TryGetGainMapInputs(document.Path, lastWriteTimeUtc, decodeMaxPixelSize, out var inputs);
                if (!wasPreloaded)
                {
                    var decodeTimer = Stopwatch.StartNew();
                    inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, decodeMaxPixelSize, cancellationToken);
                    decodeMs = decodeTimer.ElapsedMilliseconds;
                }

                var uploadTimer = Stopwatch.StartNew();
                if (!LoadGainMapTextures(inputs))
                {
                    return;
                }
                uploadMs = uploadTimer.ElapsedMilliseconds;

                _loadedGainMapPath = document.Path;
                _loadedGainMapWriteTimeUtc = lastWriteTimeUtc;
                _loadedGainMapMode = true;
                _loadedDecodeMaxPixelSize = decodeMaxPixelSize;
                LastRenderStatus = $"Gain-map textures loaded{(wasPreloaded ? " from preload" : string.Empty)}: base {_contentPixelWidth}x{_contentPixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SharpGenException ex) when (IsDeviceLost(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                var hasPreviewLoaded = fullResolution
                    && _primaryTextureView is not null
                    && _gainMapTextureView is not null
                    && _loadedGainMapMode
                    && string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase);
                if (hasPreviewLoaded)
                {
                    RenderGainMap();
                    LastRenderStatus = $"Full-resolution gain-map decode failed (preserving preview): {ex.GetType().Name}: {ex.Message}";
                    return;
                }

                ReleaseGainMapResources();
                PresentProbeFrame();
                LastRenderStatus = $"Gain-map decode failed: {ex.GetType().Name}: {ex.Message}";
                return;
            }
        }

        var drawTimer = Stopwatch.StartNew();
        RenderGainMap();
        var drawMs = drawTimer.ElapsedMilliseconds;
        LastRenderStatus = $"{LastRenderStatus}; renderer timing decode {decodeMs}ms, upload {uploadMs}ms, draw+present {drawMs}ms";
    }

    private async Task PresentBaseImageFrameAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken,
        bool fullResolution = false)
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            LastRenderStatus = "Base render skipped: device, swap chain, or size missing";
            return;
        }

        EnsureGainMapDeviceResources();
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
        int? decodeMaxPixelSize = fullResolution ? MaxTextureDimension : CalculateViewerDecodeMaxPixelSize(document);
        var decodeMs = 0L;
        var uploadMs = 0L;
        if (!string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase)
            || _loadedGainMapWriteTimeUtc != lastWriteTimeUtc
            || _loadedGainMapMode
            || !DecodedCachePolicy.IsAtLeastAsDetailed(_loadedDecodeMaxPixelSize, decodeMaxPixelSize))
        {
            try
            {
                var wasPreloaded = ImagePreloadCache.TryGetBaseBitmap(document.Path, lastWriteTimeUtc, decodeMaxPixelSize, out var bitmap);
                if (!wasPreloaded)
                {
                    var decodeTimer = Stopwatch.StartNew();
                    bitmap = await BitmapDecodeService.DecodeDocumentAsync(document, decodeMaxPixelSize, cancellationToken);
                    decodeMs = decodeTimer.ElapsedMilliseconds;
                }

                var uploadTimer = Stopwatch.StartNew();
                LoadBaseImageTexture(bitmap);
                uploadMs = uploadTimer.ElapsedMilliseconds;
                _loadedGainMapPath = document.Path;
                _loadedGainMapWriteTimeUtc = lastWriteTimeUtc;
                _loadedGainMapMode = false;
                _loadedDecodeMaxPixelSize = decodeMaxPixelSize;
                LastRenderStatus = $"Base texture loaded{(wasPreloaded ? " from preload" : string.Empty)}: {_contentPixelWidth}x{_contentPixelHeight} via {bitmap.RenderEncodingSummary}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SharpGenException ex) when (IsDeviceLost(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                var hasPreviewLoaded = fullResolution
                    && _primaryTextureView is not null
                    && !_loadedGainMapMode
                    && string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase);
                if (hasPreviewLoaded)
                {
                    RenderBaseImage(document);
                    LastRenderStatus = $"Full-resolution base decode failed (preserving preview): {ex.GetType().Name}: {ex.Message}";
                    return;
                }

                ReleaseGainMapResources();
                PresentProbeFrame();
                LastRenderStatus = $"Base decode failed: {ex.GetType().Name}: {ex.Message}";
                return;
            }
        }

        var drawTimer = Stopwatch.StartNew();
        RenderBaseImage(document);
        var drawMs = drawTimer.ElapsedMilliseconds;
        LastRenderStatus = $"{LastRenderStatus}; renderer timing decode {decodeMs}ms, upload {uploadMs}ms, draw+present {drawMs}ms";
    }

    private void EnsureGainMapDeviceResources()
    {
        if (_device is null)
        {
            LastRenderStatus = "Shader resources skipped: D3D device missing";
            return;
        }

        if (_gainMapVertexShader is null
            || _gainMapPixelShader is null
            || _baseImagePixelShader is null
            || _linearizePixelShader is null)
        {
            _gainMapVertexShader ??= _device.CreateVertexShader(
                LoadEmbeddedShaderBytecode(GainMapVertexShaderResourceName),
                null);
            _gainMapPixelShader ??= _device.CreatePixelShader(
                LoadEmbeddedShaderBytecode(GainMapPixelShaderResourceName),
                null);
            _baseImagePixelShader ??= _device.CreatePixelShader(
                LoadEmbeddedShaderBytecode(BaseImagePixelShaderResourceName),
                null);
            _linearizePixelShader ??= _device.CreatePixelShader(
                LoadEmbeddedShaderBytecode(LinearizePixelShaderResourceName),
                null);
        }

        _linearClampSampler ??= CreateFilteringSampler(_device);
        _gainMapConstantsBuffer ??= _device.CreateBuffer(
            (uint)Marshal.SizeOf<GainMapShaderConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
    }

    private static ID3D11SamplerState CreateFilteringSampler(ID3D11Device device)
    {
        try
        {
            var aniso = SamplerDescription.AnisotropicClamp;
            aniso.MaxAnisotropy = 4;
            return device.CreateSamplerState(aniso);
        }
        catch (SharpGenException)
        {
            return device.CreateSamplerState(SamplerDescription.LinearClamp);
        }
    }

    private static byte[] LoadEmbeddedShaderBytecode(string resourceName)
    {
        using var stream = typeof(D3D11HdrRenderPipeline).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded shader resource is missing: {resourceName}");
        if (stream.Length <= 0 || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Embedded shader resource has an invalid size: "
                + $"{resourceName} ({stream.Length} bytes)");
        }

        var bytecode = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytecode);
        return bytecode;
    }

    private int? CalculateViewerDecodeMaxPixelSize(HdrImageDocument document)
    {
        if (DecoderCatalog.IsJpegXrExtension(Path.GetExtension(document.Path)))
        {
            return MaxTextureDimension;
        }

        return Math.Min(CalculateDecodeTargetForSurface(Math.Max(_pixelWidth, _pixelHeight)), MaxTextureDimension);
    }

    /// <summary>
    /// Shared decode-size formula for the viewer and the adjacent-image
    /// preloader. Preloaded decodes are only usable when the cached request is
    /// at least the renderer's request (<see cref="ImagePreloadCache"/> size
    /// check), so both sides must derive their target from this one formula.
    /// </summary>
    internal static int CalculateDecodeTargetForSurface(double maxSurfaceDimensionInPixels)
    {
        if (maxSurfaceDimensionInPixels <= 0)
        {
            return 3072;
        }

        return Math.Clamp((int)Math.Ceiling(maxSurfaceDimensionInPixels * 1.50), 1600, 3072);
    }

    private static DecodedBitmap EnsureSafeTextureDimensions(DecodedBitmap bitmap)
    {
        if (bitmap.PixelWidth > MaxTextureDimension || bitmap.PixelHeight > MaxTextureDimension)
        {
            return BitmapPreviewResampler.Downscale(bitmap, MaxTextureDimension, CancellationToken.None);
        }

        return bitmap;
    }

    private bool LoadGainMapTextures(GainMapRenderInputs inputs)
    {
        if (_device is null || _context is null || _gainMapConstantsBuffer is null)
        {
            LastRenderStatus = $"Gain-map texture upload skipped: missing {BuildMissingResourceList(
                ("device", _device is null),
                ("context", _context is null),
                ("constant buffer", _gainMapConstantsBuffer is null))}";
            return false;
        }

        var primary = EnsureSafeTextureDimensions(inputs.Primary);
        var gainMap = EnsureSafeTextureDimensions(inputs.GainMap);
        var sourceTransfer = inputs.Constants.SourceEncoding.X;
        var sourceGamut = inputs.Constants.GainMapControl.Z;
        ID3D11Texture2D? newPrimaryTexture = null;
        ID3D11ShaderResourceView? newPrimaryView = null;
        ID3D11Texture2D? newGainMapTexture = null;
        ID3D11ShaderResourceView? newGainMapView = null;
        bool isPreLinearized;
        bool isPremultipliedAlpha;
        try
        {
            newPrimaryTexture = CreateLinearPrimaryTexture(
                primary,
                sourceTransfer,
                sourceGamut,
                out newPrimaryView,
                out isPreLinearized,
                out isPremultipliedAlpha);
            newGainMapTexture = CreateMipmappedRgbaTexture(
                gainMap,
                ResolveRawTextureFormat(gainMap.PixelFormat),
                out newGainMapView);
        }
        catch
        {
            newGainMapView?.Dispose();
            newGainMapTexture?.Dispose();
            newPrimaryView?.Dispose();
            newPrimaryTexture?.Dispose();
            throw;
        }

        ReleaseGainMapResources();
        _primaryTexture = newPrimaryTexture;
        _primaryTextureView = newPrimaryView;
        _gainMapTexture = newGainMapTexture;
        _gainMapTextureView = newGainMapView;
        _primaryAnalysisSource = CreateBitmapAnalysisSource(primary, includeSamples: false);
        _gainMapAnalysisSource = CreateGainMapAnalysisSource(primary, gainMap, inputs.Constants);
        InvalidateToneMapAnalysis();

        _gainMapConstants = inputs.Constants;
        _contentPixelWidth = primary.PixelWidth;
        _contentPixelHeight = primary.PixelHeight;
        _contentOrientation = _gainMapConstants.Orientation.X > 0.5f ? _gainMapConstants.Orientation.X : 1.0f;
        _gainMapConstants.Orientation = new Vector4(
            _contentOrientation,
            isPreLinearized ? 1.0f : 0.0f,
            isPremultipliedAlpha ? 1.0f : 0.0f,
            0.0f);
        UpdateGainMapConstantsBuffer();
        return true;
    }

    private void LoadBaseImageTexture(DecodedBitmap bitmap)
    {
        if (_device is null)
        {
            LastRenderStatus = "Base texture upload skipped: D3D device missing";
            return;
        }

        bitmap = EnsureSafeTextureDimensions(bitmap);
        var sourceTransfer = bitmap.Transfer switch
        {
            DecodedBitmapTransfer.LinearScRgb => 4.0f,
            DecodedBitmapTransfer.LinearSceneScRgb => 5.0f,
            DecodedBitmapTransfer.Hlg => 2.0f,
            DecodedBitmapTransfer.Pq => 3.0f,
            DecodedBitmapTransfer.Bt709 => 1.0f,
            _ => 0.0f,
        };
        var sourceGamut = ToShaderColorGamut(bitmap.EffectiveColorGamut);
        ID3D11Texture2D? newPrimaryTexture = null;
        ID3D11ShaderResourceView? newPrimaryView = null;
        bool isPreLinearized;
        bool isPremultipliedAlpha;
        try
        {
            newPrimaryTexture = CreateLinearPrimaryTexture(
                bitmap,
                sourceTransfer,
                sourceGamut,
                out newPrimaryView,
                out isPreLinearized,
                out isPremultipliedAlpha);
        }
        catch
        {
            newPrimaryView?.Dispose();
            newPrimaryTexture?.Dispose();
            throw;
        }

        ReleaseGainMapResources();
        _primaryTexture = newPrimaryTexture;
        _primaryTextureView = newPrimaryView;
        _primaryAnalysisSource = CreateBitmapAnalysisSource(bitmap, includeSamples: bitmap.IsHdrEncoded);
        _gainMapAnalysisSource = null;
        InvalidateToneMapAnalysis();
        _baseDecoderName = bitmap.DecoderName;
        _baseEncodingSummary = bitmap.RenderEncodingSummary;
        _gainMapConstants = default;
        _gainMapConstants.GainMapControl = Vector4.Zero;
        _gainMapConstants.SourceEncoding = new Vector4(
            sourceTransfer,
            sourceGamut,
            0.0f,
            0.0f);
        _contentPixelWidth = bitmap.PixelWidth;
        _contentPixelHeight = bitmap.PixelHeight;
        _contentOrientation = 1.0f;
        _gainMapConstants.Orientation = new Vector4(
            _contentOrientation,
            isPreLinearized ? 1.0f : 0.0f,
            isPremultipliedAlpha ? 1.0f : 0.0f,
            0.0f);
        UpdateGainMapConstantsBuffer();
    }

    private static Format ResolveRawTextureFormat(DecodedBitmapPixelFormat pixelFormat) =>
        pixelFormat switch
        {
            DecodedBitmapPixelFormat.Rgba16Float => Format.R16G16B16A16_Float,
            DecodedBitmapPixelFormat.Rgba16Unorm => Format.R16G16B16A16_UNorm,
            _ => Format.R8G8B8A8_UNorm,
        };

    private ID3D11Texture2D CreateLinearPrimaryTexture(
        DecodedBitmap bitmap,
        float sourceTransfer,
        float sourceGamut,
        out ID3D11ShaderResourceView srv,
        out bool isPreLinearized,
        out bool isPremultipliedAlpha)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("D3D11 device has not been created.");
        }

        bitmap = EnsureSafeTextureDimensions(bitmap);
        var width = checked((uint)bitmap.PixelWidth);
        var height = checked((uint)bitmap.PixelHeight);
        var rowPitch = checked((uint)(bitmap.PixelWidth * bitmap.BytesPerPixel));
        var depthPitch = checked(rowPitch * height);
        var hasNonOpaqueAlpha = HasNonOpaqueAlpha(bitmap);

        // 16-bit float inputs without straight-alpha transparency are already scene/display linear,
        // and opaque 8-bit sRGB/Display P3 inputs decode + filter + generate mipmaps in linear light
        // directly via R8G8B8A8_UNorm_SRgb. Transparent images go through LinearizeSourcePSMain so
        // mipmaps and cubic taps filter premultiplied linear RGBA without dark edge halos.
        var canUseHardwareSrgb = bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm
            && sourceTransfer <= 0.5f
            && sourceGamut <= 2.5f;
        if (!hasNonOpaqueAlpha && (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Float || canUseHardwareSrgb))
        {
            var directLinearFormat = bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Float
                ? Format.R16G16B16A16_Float
                : Format.R8G8B8A8_UNorm_SRgb;
            isPreLinearized = true;
            isPremultipliedAlpha = false;

            if (_context is not null && (width > 1 || height > 1))
            {
                ID3D11Texture2D? mipTexture = null;
                ID3D11ShaderResourceView? mipView = null;
                try
                {
                    var mipDescription = new Texture2DDescription(
                        directLinearFormat,
                        width,
                        height,
                        arraySize: 1,
                        mipLevels: 0,
                        BindFlags.ShaderResource | BindFlags.RenderTarget,
                        ResourceUsage.Default,
                        CpuAccessFlags.None,
                        sampleCount: 1,
                        sampleQuality: 0,
                        ResourceOptionFlags.GenerateMips);
                    mipTexture = _device.CreateTexture2D(mipDescription);
                    mipView = _device.CreateShaderResourceView(mipTexture, null);
                    _context.UpdateSubresource(bitmap.RgbaPixels, mipTexture, 0, rowPitch, depthPitch, null);
                    _context.GenerateMips(mipView);
                    srv = mipView;
                    return mipTexture;
                }
                catch (SharpGenException ex) when (!IsDeviceLost(ex))
                {
                    mipView?.Dispose();
                    mipTexture?.Dispose();
                }
            }

            return CreateImmutableRgbaTexture(bitmap, directLinearFormat, out srv);
        }

        // Non-sRGB transfers (PQ, HLG, BT.709, Adobe RGB, ProPhoto), 16-bit UNorm inputs, and
        // transparent straight-alpha images: linearize (and premultiply when transparent) into
        // R16G16B16A16_Float mip 0 on the GPU first so mipmap downscaling and trilinear/cubic
        // filtering operate in premultiplied linear light.
        EnsureGainMapDeviceResources();
        var rawTexture = CreateImmutableRgbaTexture(
            bitmap,
            ResolveRawTextureFormat(bitmap.PixelFormat),
            out var rawSrv);
        if (_context is not null
            && _gainMapVertexShader is not null
            && _linearizePixelShader is not null
            && _gainMapConstantsBuffer is not null)
        {
            ID3D11Texture2D? linearTexture = null;
            ID3D11ShaderResourceView? linearView = null;
            try
            {
                var generateMips = width > 1 || height > 1;
                var linearDescription = new Texture2DDescription(
                    Format.R16G16B16A16_Float,
                    width,
                    height,
                    arraySize: 1,
                    mipLevels: generateMips ? 0u : 1u,
                    BindFlags.ShaderResource | BindFlags.RenderTarget,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    sampleCount: 1,
                    sampleQuality: 0,
                    generateMips ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None);
                linearTexture = _device.CreateTexture2D(linearDescription);
                linearView = _device.CreateShaderResourceView(linearTexture, null);

                var mip0RtvDescription = new RenderTargetViewDescription
                {
                    Format = Format.R16G16B16A16_Float,
                    ViewDimension = RenderTargetViewDimension.Texture2D,
                    Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
                };
                using (var mip0Rtv = _device.CreateRenderTargetView(linearTexture, mip0RtvDescription))
                {
                    var linearizeConstants = new GainMapShaderConstants
                    {
                        SourceEncoding = new Vector4(sourceTransfer, sourceGamut, 0.0f, 0.0f),
                        Orientation = new Vector4(1.0f, 0.0f, hasNonOpaqueAlpha ? 1.0f : 0.0f, 0.0f),
                    };
                    _context.UpdateSubresource(in linearizeConstants, _gainMapConstantsBuffer);
                    _context.OMSetBlendState(null);
                    _context.OMSetDepthStencilState(null, 0);
                    _context.RSSetState(null);
                    _context.OMSetRenderTargets(mip0Rtv, null);
                    _context.RSSetViewport(0.0f, 0.0f, width, height, 0.0f, 1.0f);
                    _context.IASetInputLayout(null);
                    _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    _context.VSSetShader(_gainMapVertexShader);
                    _context.PSSetShader(_linearizePixelShader);
                    _context.PSSetShaderResource(0, rawSrv);
                    _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
                    _context.Draw(3, 0);
                    _context.PSUnsetShaderResource(0);
                    if (_renderTargetView is not null)
                    {
                        _context.OMSetRenderTargets(_renderTargetView, null);
                    }
                }

                if (generateMips)
                {
                    _context.GenerateMips(linearView);
                }

                rawSrv.Dispose();
                rawTexture.Dispose();
                srv = linearView;
                isPreLinearized = true;
                isPremultipliedAlpha = hasNonOpaqueAlpha;
                return linearTexture;
            }
            catch (SharpGenException ex) when (!IsDeviceLost(ex))
            {
                linearView?.Dispose();
                linearTexture?.Dispose();
            }
        }

        srv = rawSrv;
        isPreLinearized = false;
        isPremultipliedAlpha = false;
        return rawTexture;
    }

    private ID3D11Texture2D CreateMipmappedRgbaTexture(
        DecodedBitmap bitmap,
        Format format,
        out ID3D11ShaderResourceView srv)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("D3D11 device has not been created.");
        }

        var width = checked((uint)bitmap.PixelWidth);
        var height = checked((uint)bitmap.PixelHeight);
        var rowPitch = checked((uint)(bitmap.PixelWidth * bitmap.BytesPerPixel));
        var depthPitch = checked(rowPitch * height);
        if (_context is not null && (width > 1 || height > 1))
        {
            ID3D11Texture2D? mipTexture = null;
            ID3D11ShaderResourceView? mipView = null;
            try
            {
                var mipDescription = new Texture2DDescription(
                    format,
                    width,
                    height,
                    arraySize: 1,
                    mipLevels: 0,
                    BindFlags.ShaderResource | BindFlags.RenderTarget,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    sampleCount: 1,
                    sampleQuality: 0,
                    ResourceOptionFlags.GenerateMips);
                mipTexture = _device.CreateTexture2D(mipDescription);
                mipView = _device.CreateShaderResourceView(mipTexture, null);
                _context.UpdateSubresource(bitmap.RgbaPixels, mipTexture, 0, rowPitch, depthPitch, null);
                _context.GenerateMips(mipView);
                srv = mipView;
                return mipTexture;
            }
            catch (SharpGenException ex) when (!IsDeviceLost(ex))
            {
                mipView?.Dispose();
                mipTexture?.Dispose();
            }
        }

        return CreateImmutableRgbaTexture(bitmap, format, out srv);
    }

    private static bool HasNonOpaqueAlpha(DecodedBitmap bitmap)
    {
        var pixels = bitmap.RgbaPixels;
        var count = bitmap.PixelWidth * bitmap.PixelHeight;
        if (count <= 0)
        {
            return false;
        }

        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset] < 255)
                {
                    return true;
                }
            }

            return false;
        }

        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            for (var offset = 6; offset + 1 < pixels.Length; offset += 8)
            {
                if ((pixels[offset] & pixels[offset + 1]) != 0xFF)
                {
                    return true;
                }
            }

            return false;
        }

        const ushort halfOneBits = 0x3C00;
        for (var offset = 6; offset + 1 < pixels.Length; offset += 8)
        {
            var bits = (ushort)(pixels[offset] | (pixels[offset + 1] << 8));
            if (bits != halfOneBits)
            {
                return true;
            }
        }

        return false;
    }

    private ID3D11Texture2D CreateImmutableRgbaTexture(
        DecodedBitmap bitmap,
        Format format,
        out ID3D11ShaderResourceView srv)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("D3D11 device has not been created.");
        }

        var width = checked((uint)bitmap.PixelWidth);
        var height = checked((uint)bitmap.PixelHeight);
        var rowPitch = checked((uint)(bitmap.PixelWidth * bitmap.BytesPerPixel));
        var depthPitch = checked(rowPitch * height);
        var immutableDescription = new Texture2DDescription(
            format,
            width,
            height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        ID3D11Texture2D? fallbackTexture = null;
        var handle = GCHandle.Alloc(bitmap.RgbaPixels, GCHandleType.Pinned);
        try
        {
            var initialData = new SubresourceData(handle.AddrOfPinnedObject(), rowPitch, depthPitch);
            fallbackTexture = _device.CreateTexture2D(in immutableDescription, initialData);
            srv = _device.CreateShaderResourceView(fallbackTexture, null);
            return fallbackTexture;
        }
        catch
        {
            fallbackTexture?.Dispose();
            throw;
        }
        finally
        {
            handle.Free();
        }
    }

    private static BitmapAnalysisSource CreateBitmapAnalysisSource(DecodedBitmap bitmap, bool includeSamples = true)
    {
        if (!includeSamples || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return new BitmapAnalysisSource(
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                bitmap.PixelFormat,
                bitmap.Transfer,
                bitmap.ColorManagedToSrgb,
                bitmap.UsesBt2020Primaries,
                bitmap.EffectiveColorGamut,
                GridWidth: 0,
                GridHeight: 0,
                []);
        }

        var stepX = Math.Max(1, bitmap.PixelWidth / 128);
        var stepY = Math.Max(1, bitmap.PixelHeight / 128);
        var gridWidth = ((bitmap.PixelWidth - 1) / stepX) + 1;
        var gridHeight = ((bitmap.PixelHeight - 1) / stepY) + 1;
        var samples = new Vector3[gridWidth * gridHeight];
        var index = 0;

        for (var y = 0; y < bitmap.PixelHeight; y += stepY)
        {
            for (var x = 0; x < bitmap.PixelWidth; x += stepX)
            {
                var encoded = ReadEncodedRgb(bitmap, x, y);
                samples[index++] = bitmap.IsHdrEncoded
                    ? encoded
                    : HdrColorMath.DecodeSdrEncodedToLinear(
                        encoded,
                        bitmap.EffectiveColorGamut,
                        bitmap.Transfer == DecodedBitmapTransfer.Bt709
                            ? HdrColorMath.GainMapBaseTransferBt709
                            : HdrColorMath.GainMapBaseTransferSrgb);
            }
        }

        return new BitmapAnalysisSource(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            bitmap.PixelFormat,
            bitmap.Transfer,
            bitmap.ColorManagedToSrgb,
            bitmap.UsesBt2020Primaries,
            bitmap.EffectiveColorGamut,
            gridWidth,
            gridHeight,
            samples);
    }

    private static float ToShaderColorGamut(GainMapColorGamut gamut)
    {
        return HdrColorMath.ToShaderGamut(gamut);
    }

    private static string DescribeColorGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => "Display P3",
            GainMapColorGamut.Bt2100 => "BT.2020",
            GainMapColorGamut.ProPhoto => "ProPhoto RGB",
            GainMapColorGamut.AdobeRgb => "Adobe RGB",
            _ => "BT.709",
        };
    }

    private static GainMapAnalysisSource CreateGainMapAnalysisSource(
        DecodedBitmap primary,
        DecodedBitmap gainMap,
        GainMapShaderConstants constants)
    {
        if (primary.PixelWidth <= 0 || primary.PixelHeight <= 0)
        {
            return new GainMapAnalysisSource(0, 0, []);
        }

        var stepX = Math.Max(1, primary.PixelWidth / 128);
        var stepY = Math.Max(1, primary.PixelHeight / 128);
        var gridWidth = ((primary.PixelWidth - 1) / stepX) + 1;
        var gridHeight = ((primary.PixelHeight - 1) / stepY) + 1;
        var samples = new GainMapAnalysisSample[gridWidth * gridHeight];
        var index = 0;

        for (var y = 0; y < primary.PixelHeight; y += stepY)
        {
            for (var x = 0; x < primary.PixelWidth; x += stepX)
            {
                samples[index++] = new GainMapAnalysisSample(
                    HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(primary, x, y), constants),
                    ReadGainMapSample(gainMap, x, y, primary.PixelWidth, primary.PixelHeight));
            }
        }

        return new GainMapAnalysisSource(gridWidth, gridHeight, samples);
    }

    private void RenderGainMap()
    {
        EnsureRenderTargetView();

        if (_context is null
            || _swapChain is null
            || _renderTargetView is null
            || _gainMapVertexShader is null
            || _gainMapPixelShader is null
            || _linearClampSampler is null
            || _gainMapConstantsBuffer is null
            || _primaryTextureView is null
            || _gainMapTextureView is null)
        {
            LastRenderStatus = $"Gain-map draw skipped: missing {BuildMissingResourceList(
                ("context", _context is null),
                ("swap chain", _swapChain is null),
                ("render target", _renderTargetView is null),
                ("vertex shader", _gainMapVertexShader is null),
                ("gain-map pixel shader", _gainMapPixelShader is null),
                ("sampler", _linearClampSampler is null),
                ("constant buffer", _gainMapConstantsBuffer is null),
                ("primary texture view", _primaryTextureView is null),
                ("gain-map texture view", _gainMapTextureView is null))}";
            return;
        }

        _context.OMSetBlendState(null);
        _context.OMSetDepthStencilState(null, 0);
        _context.RSSetState(null);
        _context.OMSetRenderTargets(_renderTargetView, null);
        _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
        _context.RSSetViewport(0.0f, 0.0f, _pixelWidth, _pixelHeight, 0.0f, 1.0f);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_gainMapVertexShader);
        _context.PSSetShader(_gainMapPixelShader);
        _context.PSSetShaderResources(0, [_primaryTextureView, _gainMapTextureView]);
        _context.PSSetSampler(0, _linearClampSampler);
        _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
        DrawWithViewerTools();

        var frameAnalysis = AnalyzeBackBufferIfPending();
        LastFrameHasVisiblePixels = frameAnalysis.HasVisiblePixels;
        _context.PSUnsetShaderResources(0, 2);
        _swapChain.Present(1, PresentFlags.None).CheckError();
        LastRenderStatus = $"Gain-map shader presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; {BuildGainMapSummary()}; {frameAnalysis.Summary}; {BuildOutputSummary()}";
    }

    private bool TryRenderBaseImageWithDirect2D(HdrImageDocument document)
    {
        _d2dFallbackStatus = null;
        if (_primaryAnalysisSource is not { } bitmap)
        {
            return false;
        }

        if (bitmap.IsHdrEncoded && EffectiveViewModeForCurrentFrame != GainmapViewMode.Adaptive)
        {
            _d2dFallbackStatus = $"Base D2D system pipeline skipped: view mode {EffectiveViewModeForCurrentFrame} uses explicit shader path";
            return false;
        }

        if (bitmap.IsHdrEncoded)
        {
            _d2dFallbackStatus = "Base D2D system pipeline skipped: using explicit HDR shader for consistent cross-format tone mapping";
            return false;
        }

        if (bitmap.ColorManagedToSrgb)
        {
            _d2dFallbackStatus = "Base D2D system pipeline skipped: decoder already applied ICC -> sRGB";
            return false;
        }

        if (bitmap.ColorGamut is GainMapColorGamut.DisplayP3 or GainMapColorGamut.Bt2100 or GainMapColorGamut.ProPhoto or GainMapColorGamut.AdobeRgb)
        {
            _d2dFallbackStatus = $"Base D2D system pipeline skipped: shader handles {DescribeColorGamut(bitmap.ColorGamut)} source gamut";
            return false;
        }

        EnsureDirect2DReady();
        if (_d2dContext is null
            || _d2dContext2 is null
            || _d2dTargetBitmap is null
            || _wicFactory is null
            || _swapChain is null)
        {
            _d2dFallbackStatus = $"Base D2D system pipeline unavailable: missing {BuildMissingResourceList(
                ("D2D context", _d2dContext is null),
                ("D2D image-source context", _d2dContext2 is null),
                ("D2D target", _d2dTargetBitmap is null),
                ("WIC factory", _wicFactory is null),
                ("swap chain", _swapChain is null))}";
            return false;
        }

        var d2dStage = "prepare";
        try
        {
            d2dStage = "update constants";
            UpdateGainMapConstantsBuffer();
            if (!EnsureD2DBaseImageResources(document, bitmap, ref d2dStage))
            {
                return false;
            }

            if (_d2dBaseColorManagement is null)
            {
                throw new InvalidOperationException("D2D color management graph was not created.");
            }

            d2dStage = "draw D2D color-managed graph";
            DrawD2DBaseColorManagedImageGraph();

            d2dStage = "present D2D color-managed frame";
            var colorManagedFrameAnalysis = AnalyzeBackBufferIfPending();
            LastFrameHasVisiblePixels = colorManagedFrameAnalysis.HasVisiblePixels;
            _swapChain.Present(1, PresentFlags.None).CheckError();
            LastRenderStatus = $"Base image D2D color-managed pipeline presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; WIC {_d2dWicSourceSummary}; color {_d2dSourceColorSummary} -> {_d2dDestinationColorSummary}; {colorManagedFrameAnalysis.Summary}; {BuildOutputSummary()}";
            return true;
        }
        catch (SharpGenException ex) when (IsDeviceLost(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            ReleaseD2DBaseImageResources();
            _d2dFallbackStatus = $"Base D2D system pipeline skipped at {d2dStage}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private bool EnsureD2DBaseImageResources(
        HdrImageDocument document,
        BitmapAnalysisSource bitmap,
        ref string d2dStage)
    {
        if (_d2dContext is null || _d2dContext2 is null || _wicFactory is null)
        {
            throw new InvalidOperationException("D2D or WIC resources have not been created.");
        }

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
        if (string.Equals(_d2dBasePath, document.Path, StringComparison.OrdinalIgnoreCase)
            && _d2dBaseWriteTimeUtc == lastWriteTimeUtc)
        {
            if (_d2dBaseImageSource is not null && _d2dBaseColorManagement is not null)
            {
                return true;
            }

            _d2dFallbackStatus = "Base D2D system pipeline skipped: no embedded WIC color context";
            return false;
        }

        ReleaseD2DBaseImageResources();

        d2dStage = "create WIC decoder";
        _d2dBaseDecoder = _wicFactory.CreateDecoderFromFileName(
            document.Path,
            FileAccess.Read,
            DecodeOptions.CacheOnDemand);

        d2dStage = "decode WIC frame";
        _d2dBaseFrame = _d2dBaseDecoder.GetFrame(0);

        d2dStage = "inspect WIC color context";
        var wicSummary = TryCreateD2DSourceColorContextFromWic(_d2dBaseFrame, out var wicColorContext);
        if (wicColorContext is null)
        {
            ReleaseD2DBaseImageResources();
            _d2dBasePath = document.Path;
            _d2dBaseWriteTimeUtc = lastWriteTimeUtc;
            _d2dFallbackStatus = $"Base D2D system pipeline skipped: {wicSummary} (using mipmapped shader path)";
            return false;
        }

        _d2dBaseSourceColorContext = wicColorContext;
        _d2dSourceColorSummary = wicSummary;

        d2dStage = "convert WIC source";
        _d2dBaseWicSource = CreateWicSourceForDirect2D(_d2dBaseFrame, bitmap, out _d2dWicSourceSummary);

        d2dStage = "create D2D image source from WIC";
        _d2dBaseImageSource = _d2dContext2.CreateImageSourceFromWic(
            _d2dBaseWicSource,
            ImageSourceLoadingOptions.None,
            DCommonAlphaMode.Unknown);

        d2dStage = "create destination color context";
        _d2dBaseDestinationColorContext = CreateD2DDestinationColorContext(out _d2dDestinationColorSummary);

        d2dStage = "create color management effect";
        _d2dBaseColorManagement = new D2DColorManagement(_d2dContext)
        {
            SourceColorContext = _d2dBaseSourceColorContext,
            DestinationColorContext = _d2dBaseDestinationColorContext,
            SourceRenderingIntent = ColorManagementRenderingIntent.Perceptual,
            DestinationRenderingIntent = ColorManagementRenderingIntent.Perceptual,
            AlphaMode = ColorManagementAlphaMode.Premultiplied,
            Quality = ColormanagementQuality.Normal,
        };
        _d2dBaseColorManagement.SetInput(0, _d2dBaseImageSource, true);

        _d2dBasePath = document.Path;
        _d2dBaseWriteTimeUtc = lastWriteTimeUtc;
        return true;
    }

    private void DrawD2DBaseColorManagedImageGraph()
    {
        if (_d2dContext is null || _d2dBaseColorManagement is null)
        {
            throw new InvalidOperationException("D2D color management graph has not been created.");
        }

        _previewAnalysis.Invalidate();
        _d2dContext.UnitMode = UnitMode.Pixels;
        _d2dContext.BeginDraw();
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.Clear(new Color4(0.0f, 0.0f, 0.0f, 1.0f));
        _d2dContext.Transform = CalculateD2DImageTransform();
        _d2dContext.DrawImage(_d2dBaseColorManagement, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, CompositeMode.SourceOver);
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.EndDraw().CheckError();
    }

    private IWICBitmapSource CreateWicSourceForDirect2D(
        IWICBitmapFrameDecode frame,
        BitmapAnalysisSource bitmap,
        out string summary)
    {
        if (_wicFactory is null)
        {
            throw new InvalidOperationException("WIC factory has not been created.");
        }

        var sourceFormat = frame.PixelFormat;
        var targetFormat = IsFloatingPointWicPixelFormat(sourceFormat)
            ? WicPixelFormat.Format64bppPRGBAHalf
            : bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm
                ? WicPixelFormat.Format64bppPRGBA
                : WicPixelFormat.Format32bppPRGBA;
        var converter = _wicFactory.CreateFormatConverter();
        converter.Initialize(
            frame,
            targetFormat,
            BitmapDitherType.None,
            null!,
            0.0,
            BitmapPaletteType.Custom).CheckError();
        var size = converter.Size;
        summary = $"{size.Width}x{size.Height}, {DescribeWicPixelFormat(sourceFormat)} -> {DescribeWicPixelFormat(targetFormat)}";
        return converter;
    }

    private string TryCreateD2DSourceColorContextFromWic(
        IWICBitmapFrameDecode frame,
        out ID2D1ColorContext? colorContext)
    {
        colorContext = null;
        if (_wicFactory is null || _d2dContext is null)
        {
            return "WIC color context unavailable";
        }

        IWICColorContext[]? contexts = null;
        try
        {
            contexts = frame.TryGetColorContexts(_wicFactory);
            if (contexts.Length == 0)
            {
                return "WIC color context none";
            }

            colorContext = _d2dContext.CreateColorContextFromWicColorContext(contexts[0]);
            return $"WIC color context {contexts[0].Type}";
        }
        catch (Exception ex)
        {
            return $"WIC color context failed ({ex.GetType().Name})";
        }
        finally
        {
            if (contexts is not null)
            {
                foreach (var context in contexts)
                {
                    context.Dispose();
                }
            }
        }
    }

    private ID2D1ColorContext CreateD2DDestinationColorContext(out string summary)
    {
        if (_d2dContext is null)
        {
            throw new InvalidOperationException("D2D context has not been created.");
        }

        if (_d2dContext5 is not null)
        {
            summary = "DXGI scRGB";
            return _d2dContext5.CreateColorContextFromDxgiColorSpace(ColorSpaceType.RgbFullG10NoneP709);
        }

        summary = "scRGB";
        return _d2dContext.CreateColorContext(ColorSpace.ScRgb, []);
    }

    private Matrix3x2 CalculateD2DImageTransform()
    {
        var layout = GetCurrentImageLayout();
        var scaleX = layout.X * _pixelWidth / Math.Max(_contentPixelWidth, 1);
        var scaleY = layout.Y * _pixelHeight / Math.Max(_contentPixelHeight, 1);
        var offsetX = layout.Z * _pixelWidth;
        var offsetY = layout.W * _pixelHeight;
        return Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(offsetX, offsetY);
    }

    private void RenderBaseImage(HdrImageDocument document)
    {
        EnsureRenderTargetView();
        if (!ComparisonEnabled && _primaryAnalysisSource?.IsHdrEncoded != true && TryRenderBaseImageWithDirect2D(document))
        {
            return;
        }

        if (_context is null
            || _swapChain is null
            || _renderTargetView is null
            || _gainMapVertexShader is null
            || _baseImagePixelShader is null
            || _linearClampSampler is null
            || _gainMapConstantsBuffer is null
            || _primaryTextureView is null)
        {
            LastRenderStatus = $"Base draw skipped: missing {BuildMissingResourceList(
                ("context", _context is null),
                ("swap chain", _swapChain is null),
                ("render target", _renderTargetView is null),
                ("vertex shader", _gainMapVertexShader is null),
                ("base pixel shader", _baseImagePixelShader is null),
                ("sampler", _linearClampSampler is null),
                ("constant buffer", _gainMapConstantsBuffer is null),
                ("primary texture view", _primaryTextureView is null))}";
            return;
        }

        _context.OMSetBlendState(null);
        _context.OMSetDepthStencilState(null, 0);
        _context.RSSetState(null);
        _context.OMSetRenderTargets(_renderTargetView, null);
        _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
        _context.RSSetViewport(0.0f, 0.0f, _pixelWidth, _pixelHeight, 0.0f, 1.0f);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_gainMapVertexShader);
        _context.PSSetShader(_baseImagePixelShader);
        _context.PSSetShaderResource(0, _primaryTextureView);
        _context.PSSetSampler(0, _linearClampSampler);
        _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
        DrawWithViewerTools();

        var frameAnalysis = AnalyzeBackBufferIfPending();
        LastFrameHasVisiblePixels = frameAnalysis.HasVisiblePixels;
        _context.PSUnsetShaderResources(0, 1);
        _swapChain.Present(1, PresentFlags.None).CheckError();
        var d2dFallback = string.IsNullOrWhiteSpace(_d2dFallbackStatus) ? string.Empty : $"{_d2dFallbackStatus}; ";
        LastRenderStatus = $"{d2dFallback}Base image shader presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; {BuildBaseImageMappingSummary()}; {frameAnalysis.Summary}; {BuildOutputSummary()}";
    }

    private void ReleaseD2DBaseImageResources()
    {
        _d2dBaseColorManagement?.Dispose();
        _d2dBaseDestinationColorContext?.Dispose();
        _d2dBaseSourceColorContext?.Dispose();
        _d2dBaseImageSource?.Dispose();
        _d2dBaseWicSource?.Dispose();
        _d2dBaseFrame?.Dispose();
        _d2dBaseDecoder?.Dispose();
        _d2dBaseColorManagement = null;
        _d2dBaseDestinationColorContext = null;
        _d2dBaseSourceColorContext = null;
        _d2dBaseImageSource = null;
        _d2dBaseWicSource = null;
        _d2dBaseFrame = null;
        _d2dBaseDecoder = null;
        _d2dBasePath = null;
        _d2dBaseWriteTimeUtc = null;
        _d2dWicSourceSummary = "none";
        _d2dSourceColorSummary = "none";
        _d2dDestinationColorSummary = "none";
    }

    private void ReleaseGainMapResources()
    {
        ReleaseD2DBaseImageResources();
        _primaryTextureView?.Dispose();
        _gainMapTextureView?.Dispose();
        _primaryTexture?.Dispose();
        _gainMapTexture?.Dispose();
        _primaryTextureView = null;
        _gainMapTextureView = null;
        _primaryTexture = null;
        _gainMapTexture = null;
        ClearAnalysis();
        _gainSampleStatsSummary = null;
        _cachedToneMapSampleStats = null;
        _primaryAnalysisSource = null;
        _gainMapAnalysisSource = null;
        _d2dFallbackStatus = null;
        _gainMapConstants = default;
        _toneMapAnalysis = default;
        _toneMappingEnabledForCurrentFrame = false;
        InvalidateToneMapAnalysis();
        _frameVerificationPending = false;
        _lastFrameAnalysis = new FrameAnalysis(false, 0.0f, "frame verification not requested");
        _contentPixelWidth = 0;
        _contentPixelHeight = 0;
        _contentOrientation = 1.0f;
        _loadedGainMapPath = null;
        _loadedGainMapWriteTimeUtc = null;
        _loadedGainMapMode = false;
        _loadedDecodeMaxPixelSize = null;
    }

    private readonly record struct FrameAnalysis(bool HasVisiblePixels, float MaxSceneValue, string Summary);

    private sealed record BitmapAnalysisSource(
        int PixelWidth,
        int PixelHeight,
        DecodedBitmapPixelFormat PixelFormat,
        DecodedBitmapTransfer Transfer,
        bool ColorManagedToSrgb,
        bool UsesBt2020Primaries,
        GainMapColorGamut ColorGamut,
        int GridWidth,
        int GridHeight,
        Vector3[] Samples)
    {
        public bool IsHdrEncoded => Transfer is DecodedBitmapTransfer.Hlg or DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb;
    }

    private sealed record GainMapAnalysisSource(
        int GridWidth,
        int GridHeight,
        GainMapAnalysisSample[] Samples);

    private readonly record struct GainMapAnalysisSample(Vector3 Sdr, Vector3 Gain);

    private readonly record struct ToneMapAnalysis(
        float ContentPeak,
        float HighPercentilePeak,
        float ToneMapPeak,
        float ContentAverage,
        float VirtualTargetPeak,
        float PhysicalTargetPeak,
        float AdaptiveTargetPeak,
        float FullFrameLimit,
        float GlobalScale);

    private readonly record struct ToneMapModeState(
        Vector4 Input, Vector4 Output, ToneMapAnalysis Analysis, bool Enabled);

    private sealed record CachedToneMapSampleStats(
        bool IsGainMap,
        float Parameter,
        ColorGamutMappingMode GamutMappingMode,
        float UnscaledContentPeak,
        float UnscaledHighPercentilePeak,
        float UnscaledAverage);
}
