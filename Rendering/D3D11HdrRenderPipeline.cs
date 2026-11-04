using System.Buffers;
using System.Numerics;
using System.Diagnostics;
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
using D2DHdrToneMap = Vortice.Direct2D1.Effects.HdrToneMap;
using D2DWhiteLevelAdjustment = Vortice.Direct2D1.Effects.WhiteLevelAdjustment;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline : IHdrRenderPipeline, IDisposable
{
    private const float ToneModeGainMap = 0.0f;
    private const float ToneModeSingleLayerSystem = 1.0f;
    private const float ToneModeSingleLayerDisplayFit = 2.0f;
    private const float SingleLayerHdrReferenceWhiteScale = HdrColorMath.UltraHdrReferenceWhiteNits / HdrColorMath.ReferenceWhiteNits;
    private const float HlgReferenceScenePeak = 1000.0f / 80.0f;
    private const float HlgReferenceWhiteScene = 203.0f / 80.0f;
    private const bool UseD2DSystemToneMapForBaseHdr = false;
    private const string GainMapVertexShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.GainMap.VS.cso";
    private const string GainMapPixelShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.GainMap.PS.cso";
    private const string BaseImagePixelShaderResourceName =
        "HdrImageViewer.Rendering.Shaders.BaseImage.PS.cso";

    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
        D3DFeatureLevel.Level_10_1,
        D3DFeatureLevel.Level_10_0,
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
    private D2DHdrToneMap? _d2dBaseToneMap;
    private D2DWhiteLevelAdjustment? _d2dBaseWhiteLevel;
    private IWICImagingFactory? _wicFactory;
    private ID3D11VertexShader? _gainMapVertexShader;
    private ID3D11PixelShader? _gainMapPixelShader;
    private ID3D11PixelShader? _baseImagePixelShader;
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
    private float _d2dMeasuredInputMaxNits;
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
    private string? _gainSampleStatsSummary;
    private bool _frameVerificationPending = true;
    private FrameAnalysis _lastFrameAnalysis = new(false, 0.0f, "frame verification pending");

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
        _panel = panel;
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
            _frameVerificationPending = true;
            _lastFrameAnalysis = new FrameAnalysis(false, 0.0f, "frame verification pending");

            EnsureRenderTargetView();
            if (_context is not null && _swapChain is not null && _renderTargetView is not null)
            {
                _context.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                _context.Flush();
                _swapChain.Present(1, PresentFlags.None).CheckError();
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
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (viewport is { } target)
            {
                if (!target.IsValid) throw new ArgumentOutOfRangeException(nameof(viewport));
                _renderImageLayout = target.ImageLayout;
                EnsureDevice();
                if (_swapChain is null) CreateSwapChain(target.PixelWidth, target.PixelHeight);
                else if (_pixelWidth != target.PixelWidth || _pixelHeight != target.PixelHeight)
                    ResizeSwapChain(target.PixelWidth, target.PixelHeight);
                else ConfigureSwapChainPanelScale();
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
        finally
        {
            _renderOperationGate.Release();
        }
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
        finally
        {
            _renderOperationGate.Release();
        }
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
        finally
        {
            _renderOperationGate.Release();
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _previewAnalysis.Dispose();
        DetachSwapChainFromPanel();
        ReleaseFrameAnalysisStagingTexture();
        ReleaseGainMapResources();
        _gainMapConstantsBuffer?.Dispose();
        _linearClampSampler?.Dispose();
        _baseImagePixelShader?.Dispose();
        _gainMapPixelShader?.Dispose();
        _gainMapVertexShader?.Dispose();
        ReleaseD2DTargetBitmap();
        _wicFactory?.Dispose();
        _d2dContext5?.Dispose();
        _d2dContext2?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _renderTargetView?.Dispose();
        _swapChain3?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
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
        var decodeMaxPixelSize = fullResolution ? null : CalculateViewerDecodeMaxPixelSize(document);
        var decodeMs = 0L;
        var uploadMs = 0L;
        if (!string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase)
            || _loadedGainMapWriteTimeUtc != lastWriteTimeUtc
            || !_loadedGainMapMode
            || !DecodedCachePolicy.IsAtLeastAsDetailed(_loadedDecodeMaxPixelSize, decodeMaxPixelSize))
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
            LastRenderStatus = $"Gain-map textures loaded{(wasPreloaded ? " from preload" : string.Empty)}: base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}";
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
        var decodeMaxPixelSize = fullResolution ? null : CalculateViewerDecodeMaxPixelSize(document);
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
                LastRenderStatus = $"Base texture loaded{(wasPreloaded ? " from preload" : string.Empty)}: {bitmap.PixelWidth}x{bitmap.PixelHeight} via {bitmap.RenderEncodingSummary}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            || _baseImagePixelShader is null)
        {
            var vertexShader = LoadEmbeddedShaderBytecode(
                GainMapVertexShaderResourceName);
            var pixelShader = LoadEmbeddedShaderBytecode(
                GainMapPixelShaderResourceName);
            var basePixelShader = LoadEmbeddedShaderBytecode(
                BaseImagePixelShaderResourceName);
            _gainMapVertexShader = _device.CreateVertexShader(
                vertexShader,
                null);
            _gainMapPixelShader = _device.CreatePixelShader(
                pixelShader,
                null);
            _baseImagePixelShader = _device.CreatePixelShader(
                basePixelShader,
                null);
        }

        _linearClampSampler ??=
            _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _gainMapConstantsBuffer ??= _device.CreateBuffer(
            (uint)Marshal.SizeOf<GainMapShaderConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
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
            return null;
        }

        return CalculateDecodeTargetForSurface(Math.Max(_pixelWidth, _pixelHeight));
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

        ReleaseGainMapResources();
        _primaryTexture = CreateRgbaTexture(inputs.Primary);
        _gainMapTexture = CreateRgbaTexture(inputs.GainMap);
        _primaryTextureView = _device.CreateShaderResourceView(_primaryTexture, null);
        _gainMapTextureView = _device.CreateShaderResourceView(_gainMapTexture, null);
        _primaryAnalysisSource = CreateBitmapAnalysisSource(inputs.Primary);
        _gainMapAnalysisSource = CreateGainMapAnalysisSource(inputs.Primary, inputs.GainMap, inputs.Constants);
        _frameVerificationPending = true;
        InvalidateToneMapAnalysis();

        _gainMapConstants = inputs.Constants;
        _contentPixelWidth = inputs.Primary.PixelWidth;
        _contentPixelHeight = inputs.Primary.PixelHeight;
        _contentOrientation = _gainMapConstants.Orientation.X;
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

        ReleaseGainMapResources();
        _primaryTexture = CreateRgbaTexture(bitmap);
        _primaryTextureView = _device.CreateShaderResourceView(_primaryTexture, null);
        _primaryAnalysisSource = CreateBitmapAnalysisSource(bitmap);
        _gainMapAnalysisSource = null;
        _frameVerificationPending = true;
        InvalidateToneMapAnalysis();
        _baseDecoderName = bitmap.DecoderName;
        _baseEncodingSummary = bitmap.RenderEncodingSummary;
        _gainMapConstants = default;
        _gainMapConstants.GainMapControl = Vector4.Zero;
        _gainMapConstants.SourceEncoding = new Vector4(
            bitmap.Transfer switch
            {
                DecodedBitmapTransfer.LinearScRgb => 4.0f,
                DecodedBitmapTransfer.LinearSceneScRgb => 5.0f,
                DecodedBitmapTransfer.Hlg => 2.0f,
                DecodedBitmapTransfer.Pq => 3.0f,
                _ => 1.0f,
            },
            ToShaderColorGamut(bitmap.EffectiveColorGamut),
            0.0f,
            0.0f);
        _contentPixelWidth = bitmap.PixelWidth;
        _contentPixelHeight = bitmap.PixelHeight;
        _contentOrientation = 1.0f;
        UpdateGainMapConstantsBuffer();
    }

    private ID3D11Texture2D CreateRgbaTexture(DecodedBitmap bitmap)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("D3D11 device has not been created.");
        }

        var description = new Texture2DDescription(
            bitmap.PixelFormat switch
            {
                DecodedBitmapPixelFormat.Rgba16Float => Format.R16G16B16A16_Float,
                DecodedBitmapPixelFormat.Rgba16Unorm => Format.R16G16B16A16_UNorm,
                _ => Format.R8G8B8A8_UNorm,
            },
            checked((uint)bitmap.PixelWidth),
            checked((uint)bitmap.PixelHeight),
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        var handle = GCHandle.Alloc(bitmap.RgbaPixels, GCHandleType.Pinned);
        try
        {
            var rowPitch = checked((uint)(bitmap.PixelWidth * bitmap.BytesPerPixel));
            var depthPitch = checked(rowPitch * (uint)bitmap.PixelHeight);
            var initialData = new SubresourceData(handle.AddrOfPinnedObject(), rowPitch, depthPitch);
            return _device.CreateTexture2D(in description, initialData);
        }
        finally
        {
            handle.Free();
        }
    }

    private static BitmapAnalysisSource CreateBitmapAnalysisSource(DecodedBitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.PixelWidth / 128);
        var stepY = Math.Max(1, bitmap.PixelHeight / 128);
        var samples = new List<Vector3>(128 * 128);

        for (var y = 0; y < bitmap.PixelHeight; y += stepY)
        {
            for (var x = 0; x < bitmap.PixelWidth; x += stepX)
            {
                samples.Add(bitmap.IsHdrEncoded
                    ? ReadEncodedRgb(bitmap, x, y)
                    : ReadLinearSrgb(bitmap, x, y));
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
            [.. samples]);
    }

    private static float ToShaderColorGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.ProPhoto => 3.0f,
            GainMapColorGamut.DisplayP3 => 1.0f,
            GainMapColorGamut.Bt2100 => 2.0f,
            _ => 0.0f,
        };
    }

    private static string DescribeColorGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => "Display P3",
            GainMapColorGamut.Bt2100 => "BT.2020",
            GainMapColorGamut.ProPhoto => "ProPhoto RGB",
            _ => "BT.709",
        };
    }

    private static GainMapAnalysisSource CreateGainMapAnalysisSource(
        DecodedBitmap primary,
        DecodedBitmap gainMap,
        GainMapShaderConstants constants)
    {
        var stepX = Math.Max(1, primary.PixelWidth / 128);
        var stepY = Math.Max(1, primary.PixelHeight / 128);
        var samples = new List<GainMapAnalysisSample>(128 * 128);

        for (var y = 0; y < primary.PixelHeight; y += stepY)
        {
            for (var x = 0; x < primary.PixelWidth; x += stepX)
            {
                samples.Add(new GainMapAnalysisSample(
                    HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(primary, x, y), constants),
                    ReadGainMapSample(gainMap, x, y, primary.PixelWidth, primary.PixelHeight)));
            }
        }

        return new GainMapAnalysisSource([.. samples]);
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
        _context.Flush();
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

        if (bitmap.ColorGamut is GainMapColorGamut.DisplayP3 or GainMapColorGamut.Bt2100 or GainMapColorGamut.ProPhoto)
        {
            _d2dFallbackStatus = $"Base D2D system pipeline skipped: shader handles {DescribeColorGamut(bitmap.ColorGamut)} source gamut";
            return false;
        }

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
            EnsureD2DBaseImageResources(document, bitmap, ref d2dStage);
            if (!bitmap.IsHdrEncoded)
            {
                if (_d2dBaseColorManagement is null)
                {
                    throw new InvalidOperationException("D2D color management graph was not created.");
                }

                d2dStage = "draw D2D color-managed graph";
                DrawD2DBaseColorManagedImageGraph();
                CaptureAnalysisPixelsIfRequested();

                d2dStage = "present D2D color-managed frame";
                var colorManagedFrameAnalysis = AnalyzeBackBufferIfPending();
                LastFrameHasVisiblePixels = colorManagedFrameAnalysis.HasVisiblePixels;
                _context?.Flush();
                _swapChain.Present(1, PresentFlags.None).CheckError();
                LastRenderStatus = $"Base image D2D color-managed pipeline presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; WIC {_d2dWicSourceSummary}; color {_d2dSourceColorSummary} -> {_d2dDestinationColorSummary}; {colorManagedFrameAnalysis.Summary}; {BuildOutputSummary()}";
                return true;
            }

            if (_d2dBaseToneMap is null || _d2dBaseWhiteLevel is null)
            {
                throw new InvalidOperationException("D2D base effect graph was not created.");
            }

            var analysis = _toneMapAnalysis.VirtualTargetPeak > 0.0f
                ? _toneMapAnalysis
                : AnalyzeBaseHdrToneMapInput(_gainMapConstants);

            d2dStage = "update HDR tone map effect";
            var inputMaxNits = CalculateD2DInputMaxLuminanceNits(analysis);
            if (_d2dMeasuredInputMaxNits > inputMaxNits)
            {
                inputMaxNits = _d2dMeasuredInputMaxNits;
            }

            var outputMaxNits = CalculateD2DOutputMaxLuminanceNits(analysis);
            _d2dBaseToneMap.InputMaxLuminance = inputMaxNits;
            _d2dBaseToneMap.OutputMaxLuminance = outputMaxNits;
            _d2dBaseToneMap.DisplayMode = _displayConfiguration.IsHighDynamicRange
                ? HDRToneMapDisplayMode.Hdr
                : HDRToneMapDisplayMode.Sdr;

            d2dStage = "update white level effect";
            var (whiteInputNits, whiteOutputNits, whiteSummary) = CalculateD2DWhiteLevelAdjustment(outputMaxNits);
            _d2dBaseWhiteLevel.InputWhiteLebel = whiteInputNits;
            _d2dBaseWhiteLevel.OutputWhiteLevel = whiteOutputNits;

            d2dStage = "draw D2D graph";
            DrawD2DBaseImageGraph();

            var frameAnalysis = AnalyzeBackBufferIfPending();
            var d2dInputFeedbackSummary = string.Empty;
            var outputMaxScene = Math.Max(outputMaxNits / 80.0f, 1.0f);
            if (frameAnalysis.MaxSceneValue > outputMaxScene * 1.03f)
            {
                d2dStage = "redraw D2D graph with measured peak";
                var measuredInputMaxNits = Math.Clamp(
                    frameAnalysis.MaxSceneValue * 80.0f,
                    outputMaxNits,
                    10000.0f);
                if (measuredInputMaxNits > inputMaxNits)
                {
                    inputMaxNits = measuredInputMaxNits;
                    _d2dMeasuredInputMaxNits = Math.Max(_d2dMeasuredInputMaxNits, measuredInputMaxNits);
                    _d2dBaseToneMap.InputMaxLuminance = inputMaxNits;
                    DrawD2DBaseImageGraph();
                    frameAnalysis = AnalyzeBackBufferIfPending();
                    d2dInputFeedbackSummary = $"; measured input max {inputMaxNits:0} nits";
                }
            }

            d2dStage = "present D2D frame";
            CaptureAnalysisPixelsIfRequested();
            LastFrameHasVisiblePixels = frameAnalysis.HasVisiblePixels;
            _context?.Flush();
            _swapChain.Present(1, PresentFlags.None).CheckError();
            LastRenderStatus = $"Base image D2D system pipeline presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; WIC {_d2dWicSourceSummary}; color {_d2dSourceColorSummary} -> {_d2dDestinationColorSummary}; HDR tone map input/output {inputMaxNits:0}/{outputMaxNits:0} nits ({_d2dBaseToneMap.DisplayMode}){d2dInputFeedbackSummary}; white level {whiteSummary}; {frameAnalysis.Summary}; {BuildOutputSummary()}";
            return true;
        }
        catch (Exception ex)
        {
            ReleaseD2DBaseImageResources();
            _d2dFallbackStatus = $"Base D2D system pipeline skipped at {d2dStage}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void EnsureD2DBaseImageResources(
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
            && _d2dBaseWriteTimeUtc == lastWriteTimeUtc
            && _d2dBaseImageSource is not null
            && _d2dBaseColorManagement is not null
            && _d2dBaseToneMap is not null
            && _d2dBaseWhiteLevel is not null)
        {
            return;
        }

        ReleaseD2DBaseImageResources();

        d2dStage = "create WIC decoder";
        _d2dBaseDecoder = _wicFactory.CreateDecoderFromFileName(
            document.Path,
            FileAccess.Read,
            DecodeOptions.CacheOnDemand);

        d2dStage = "decode WIC frame";
        _d2dBaseFrame = _d2dBaseDecoder.GetFrame(0);

        d2dStage = "convert WIC source";
        _d2dBaseWicSource = CreateWicSourceForDirect2D(_d2dBaseFrame, bitmap, out _d2dWicSourceSummary);

        d2dStage = "create D2D image source from WIC";
        _d2dBaseImageSource = _d2dContext2.CreateImageSourceFromWic(
            _d2dBaseWicSource,
            ImageSourceLoadingOptions.None,
            DCommonAlphaMode.Unknown);

        d2dStage = "create source color context";
        _d2dBaseSourceColorContext = CreateD2DSourceColorContext(bitmap, _d2dBaseFrame, out _d2dSourceColorSummary);

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

        d2dStage = "create HDR tone map effect";
        _d2dBaseToneMap = new D2DHdrToneMap(_d2dContext);
        _d2dBaseToneMap.SetInputEffect(0, _d2dBaseColorManagement, true);

        d2dStage = "create white level effect";
        _d2dBaseWhiteLevel = new D2DWhiteLevelAdjustment(_d2dContext);
        _d2dBaseWhiteLevel.SetInputEffect(0, _d2dBaseToneMap, true);

        _d2dBasePath = document.Path;
        _d2dBaseWriteTimeUtc = lastWriteTimeUtc;
    }

    private void DrawD2DBaseImageGraph()
    {
        if (_d2dContext is null || _d2dBaseWhiteLevel is null)
        {
            throw new InvalidOperationException("D2D base graph has not been created.");
        }

        _previewAnalysis.Invalidate();
        _d2dContext.UnitMode = UnitMode.Pixels;
        _d2dContext.BeginDraw();
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.Clear(new Color4(0.0f, 0.0f, 0.0f, 1.0f));
        _d2dContext.Transform = CalculateD2DImageTransform();
        _d2dContext.DrawImage(_d2dBaseWhiteLevel, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, CompositeMode.SourceOver);
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.EndDraw().CheckError();
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

    private ID2D1ColorContext CreateD2DSourceColorContext(
        BitmapAnalysisSource bitmap,
        IWICBitmapFrameDecode frame,
        out string summary)
    {
        if (_d2dContext is null)
        {
            throw new InvalidOperationException("D2D context has not been created.");
        }

        var attempts = new List<string>();
        var wicSummary = TryCreateD2DSourceColorContextFromWic(frame, out var wicColorContext);
        if (wicColorContext is not null)
        {
            summary = wicSummary;
            return wicColorContext;
        }

        attempts.Add(wicSummary);
        if (_d2dContext5 is not null && bitmap.Transfer == DecodedBitmapTransfer.Pq && bitmap.UsesBt2020Primaries)
        {
            try
            {
                summary = $"{string.Join("; ", attempts)}; DXGI RGB PQ BT.2020";
                return _d2dContext5.CreateColorContextFromDxgiColorSpace(ColorSpaceType.RgbFullG2084NoneP2020);
            }
            catch (Exception ex)
            {
                attempts.Add($"DXGI RGB PQ BT.2020 failed ({ex.GetType().Name})");
            }
        }

        if (bitmap.Transfer == DecodedBitmapTransfer.Hlg && bitmap.UsesBt2020Primaries)
        {
            summary = $"{string.Join("; ", attempts)}; scRGB half fallback for WIC RGB HLG source";
            return _d2dContext.CreateColorContext(ColorSpace.ScRgb, []);
        }

        var fallbackSpace = bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm && bitmap.IsHdrEncoded
            ? ColorSpace.ScRgb
            : ColorSpace.Srgb;
        summary = $"{string.Join("; ", attempts)}; {fallbackSpace} fallback";
        return _d2dContext.CreateColorContext(fallbackSpace, []);
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

    private float CalculateD2DInputMaxLuminanceNits(ToneMapAnalysis analysis)
    {
        var scenePeak = analysis.ContentPeak > 0.0f
            ? analysis.ContentPeak
            : _displayConfiguration.MaxSceneValue > 0.0f ? _displayConfiguration.MaxSceneValue : 12.5f;
        return Math.Clamp(scenePeak * 80.0f, 80.0f, 10000.0f);
    }

    private float CalculateD2DOutputMaxLuminanceNits(ToneMapAnalysis analysis)
    {
        if (analysis.AdaptiveTargetPeak > 0.0f)
        {
            return Math.Clamp(analysis.AdaptiveTargetPeak * 80.0f, 80.0f, 10000.0f);
        }

        if (_displayCapacityOverrideLog2 is { } overrideStops)
        {
            return (float)Math.Clamp(
                _displayConfiguration.SdrWhiteLevelInNits * Math.Pow(2.0, overrideStops),
                80.0,
                10000.0);
        }

        return _displayConfiguration.HasReliablePeakLuminance
            ? (float)Math.Clamp(_displayConfiguration.MaxLuminanceInNits, 80.0, 10000.0)
            : 1000.0f;
    }

    private (float InputNits, float OutputNits, string Summary) CalculateD2DWhiteLevelAdjustment(float toneMappedOutputMaxNits)
    {
        if (_displayConfiguration.IsHighDynamicRange)
        {
            return (80.0f, 80.0f, "80->80 nits no-op for HDR FP16");
        }

        var output = Math.Clamp(toneMappedOutputMaxNits, 80.0f, 10000.0f);
        return (80.0f, output, $"80->{output:0} nits for SDR/WCG desktop");
    }

    private void RenderBaseImage(HdrImageDocument document)
    {
        if (UseD2DSystemToneMapForBaseHdr
            && _primaryAnalysisSource?.IsHdrEncoded == true
            && TryRenderBaseImageWithDirect2D(document))
        {
            return;
        }

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
        _context.Flush();
        _swapChain.Present(1, PresentFlags.None).CheckError();
        var d2dFallback = string.IsNullOrWhiteSpace(_d2dFallbackStatus) ? string.Empty : $"{_d2dFallbackStatus}; ";
        LastRenderStatus = $"{d2dFallback}Base image shader presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; {BuildBaseImageMappingSummary()}; {frameAnalysis.Summary}; {BuildOutputSummary()}";
    }

    private void ReleaseD2DBaseImageResources()
    {
        _d2dBaseWhiteLevel?.Dispose();
        _d2dBaseToneMap?.Dispose();
        _d2dBaseColorManagement?.Dispose();
        _d2dBaseDestinationColorContext?.Dispose();
        _d2dBaseSourceColorContext?.Dispose();
        _d2dBaseImageSource?.Dispose();
        _d2dBaseWicSource?.Dispose();
        _d2dBaseFrame?.Dispose();
        _d2dBaseDecoder?.Dispose();
        _d2dBaseWhiteLevel = null;
        _d2dBaseToneMap = null;
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
        _d2dMeasuredInputMaxNits = 0.0f;
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
        _primaryAnalysisSource = null;
        _gainMapAnalysisSource = null;
        _d2dFallbackStatus = null;
        _gainMapConstants = default;
        _toneMapAnalysis = default;
        _toneMappingEnabledForCurrentFrame = false;
        InvalidateToneMapAnalysis();
        _frameVerificationPending = true;
        _lastFrameAnalysis = new FrameAnalysis(false, 0.0f, "frame verification pending");
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
        Vector3[] Samples)
    {
        public bool IsHdrEncoded => Transfer is DecodedBitmapTransfer.Hlg or DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb;
    }

    private sealed record GainMapAnalysisSource(GainMapAnalysisSample[] Samples);

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
}
