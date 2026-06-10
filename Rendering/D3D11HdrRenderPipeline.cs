using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
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

public sealed class D3D11HdrRenderPipeline : IHdrRenderPipeline, IDisposable
{
    private const float ToneModeGainMap = 0.0f;
    private const float ToneModeSingleLayerSystem = 1.0f;
    private const float ToneModeSingleLayerDisplayFit = 2.0f;
    private const float SingleLayerHdrReferenceWhiteScale = HdrColorMath.UltraHdrReferenceWhiteNits / HdrColorMath.ReferenceWhiteNits;
    private const float HlgReferenceScenePeak = 1000.0f / 80.0f;
    private const float HlgReferenceWhiteScene = 203.0f / 80.0f;
    private const bool UseD2DSystemToneMapForBaseHdr = false;

    private const string GainMapShaderSource = """
struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

Texture2D PrimaryTexture : register(t0);
Texture2D GainMapTexture : register(t1);
SamplerState LinearClampSampler : register(s0);

cbuffer GainMapConstants : register(b0)
{
    float4 GainMapMin;
    float4 GainMapMax;
    float4 Gamma;
    float4 OffsetSdr;
    float4 OffsetHdr;
    float4 GainMapControl;
    float4 SourceEncoding;
    float4 Orientation;
    float4 DisplayMapping;
    float4 HdrCapacity;
    float4 ImageLayout;
    float4 ToneMapInput;
    float4 ToneMapOutput;
    float4 ViewModeParams;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0f, -1.0f),
        float2(-1.0f, 3.0f),
        float2(3.0f, -1.0f),
    };

    float2 texCoords[3] =
    {
        float2(0.0f, 1.0f),
        float2(0.0f, -1.0f),
        float2(2.0f, 1.0f),
    };

    VertexOutput output;
    output.Position = float4(positions[vertexId], 0.0f, 1.0f);
    output.TexCoord = texCoords[vertexId];
    return output;
}

float2 ApplyOrientation(float2 uv)
{
    float orientation = Orientation.x;
    if (orientation < 1.5f) return uv;
    if (orientation < 2.5f) return float2(1.0f - uv.x, uv.y);
    if (orientation < 3.5f) return float2(1.0f - uv.x, 1.0f - uv.y);
    if (orientation < 4.5f) return float2(uv.x, 1.0f - uv.y);
    if (orientation < 5.5f) return float2(uv.y, uv.x);
    if (orientation < 6.5f) return float2(uv.y, 1.0f - uv.x);
    if (orientation < 7.5f) return float2(1.0f - uv.y, 1.0f - uv.x);
    return float2(1.0f - uv.y, uv.x);
}

float4 FitToImage(float2 panelUv)
{
    float2 fittedUv = (panelUv - ImageLayout.zw) / max(ImageLayout.xy, 0.0001f);
    float inside =
        step(0.0f, fittedUv.x) *
        step(fittedUv.x, 1.0f) *
        step(0.0f, fittedUv.y) *
        step(fittedUv.y, 1.0f);
    return float4(saturate(fittedUv), inside, 0.0f);
}

float3 SrgbToLinear(float3 value)
{
    float3 low = value / 12.92f;
    float3 high = pow((value + 0.055f) / 1.055f, 2.4f);
    return lerp(high, low, value <= 0.04045f);
}

float3 Rec709ToLinear(float3 value)
{
    float3 low = value / 4.5f;
    float3 high = pow((value + 0.099f) / 1.099f, 1.0f / 0.45f);
    return lerp(high, low, value < 0.081f);
}

float3 Bt2020ToBt709(float3 value)
{
    return float3(
        (1.660491f * value.r) - (0.587641f * value.g) - (0.072850f * value.b),
        (-0.124550f * value.r) + (1.132900f * value.g) - (0.008349f * value.b),
        (-0.018151f * value.r) - (0.100579f * value.g) + (1.118730f * value.b));
}

float3 P3ToBt709(float3 value)
{
    return float3(
        (1.224940f * value.r) - (0.224940f * value.g),
        (-0.042057f * value.r) + (1.042057f * value.g),
        (-0.019638f * value.r) - (0.078636f * value.g) + (1.098274f * value.b));
}

float3 ProPhotoToBt709(float3 value)
{
    return float3(
        (2.034368f * value.r) - (0.727634f * value.g) - (0.306733f * value.b),
        (-0.228827f * value.r) + (1.231753f * value.g) - (0.002927f * value.b),
        (-0.008558f * value.r) - (0.153268f * value.g) + (1.161827f * value.b));
}

float3 ConvertGainMapBaseToBt709(float3 value)
{
    float3 converted = value;
    if (GainMapControl.z > 2.5f)
    {
        converted = ProPhotoToBt709(value);
    }
    else if (GainMapControl.z > 1.5f)
    {
        converted = Bt2020ToBt709(value);
    }
    else if (GainMapControl.z > 0.5f)
    {
        converted = P3ToBt709(value);
    }

    return ViewModeParams.w > 0.5f ? max(converted, 0.0f) : converted;
}

float3 HlgToSceneLinear(float3 value)
{
    const float a = 0.17883277f;
    const float b = 0.28466892f;
    const float c = 0.55991073f;
    float3 low = (value * value) / 3.0f;
    float3 high = (exp((value - c) / a) + b) / 12.0f;
    return lerp(high, low, value <= 0.5f);
}

float3 PqToSceneLinear(float3 value)
{
    const float m1 = 2610.0f / 16384.0f;
    const float m2 = 2523.0f / 32.0f;
    const float c1 = 3424.0f / 4096.0f;
    const float c2 = 2413.0f / 128.0f;
    const float c3 = 2392.0f / 128.0f;
    float3 y = pow(max(value, 0.0f), 1.0f / m2);
    float3 nits = 10000.0f * pow(max((y - c1) / max(c2 - (c3 * y), 0.000001f), 0.0f), 1.0f / m1);
    return nits / 80.0f;
}

float CalculateHdrTargetScenePeak()
{
    if (SourceEncoding.x > 1.5f && SourceEncoding.x < 2.5f)
    {
        return 1000.0f / 80.0f;
    }

    return max(DisplayMapping.x * exp2(max(DisplayMapping.z, 0.0f)), DisplayMapping.x);
}

float CalculateHlgSystemGamma(float targetScenePeak)
{
    float targetNits = max(targetScenePeak * 80.0f, 100.0f);
    return clamp(1.2f + (0.42f * log10(targetNits / 1000.0f)), 1.0f, 1.35f);
}

float3 ClampToDisplayPeak(float3 value)
{
    if (DisplayMapping.y > 0.0f)
    {
        value = min(value, DisplayMapping.yyy);
    }

    return value;
}

float3 ApplySdrWhiteScale(float3 value)
{
    return value * max(DisplayMapping.x, 1.0f);
}

float3 ApplyAdaptiveToneMapWithWhiteScale(float3 value, float whiteScale)
{
    if (ToneMapInput.x < 0.5f)
    {
        return ClampToDisplayPeak(value);
    }

    whiteScale = max(whiteScale, 1.0f);
    float virtualTarget = max(ToneMapInput.y, whiteScale);
    float physicalTarget = ToneMapOutput.x > 0.0f ? ToneMapOutput.x : virtualTarget;
    float target = clamp(ToneMapOutput.z, whiteScale, max(physicalTarget, whiteScale));
    float tonePeak = max(ToneMapInput.z, max(virtualTarget, target));
    float contentAvg = max(ToneMapInput.w, 0.0f);
    float globalScale = clamp(ToneMapOutput.w, 0.02f, 1.0f);
    float3 mappedValue = value * globalScale;
    float scaledContentPeak = max(tonePeak * globalScale, target);

    float averageRelativeToWhite = contentAvg / max(whiteScale, 0.0001f);
    float kneeBlend = saturate((averageRelativeToWhite - 0.10f) / 0.70f);
    float kneeFactor = lerp(0.36f, 0.16f, kneeBlend);
    float knee = whiteScale + ((target - whiteScale) * kneeFactor);
    knee = clamp(knee, whiteScale * 0.85f, target * 0.92f);

    float peak = max(max(mappedValue.r, mappedValue.g), mappedValue.b);
    if (peak <= knee)
    {
        return ClampToDisplayPeak(mappedValue);
    }

    float sourceRange = max(scaledContentPeak - knee, 0.0001f);
    float targetRange = max(target - knee, 0.0001f);
    float x = max(peak - knee, 0.0f);
    float denominator = max(1.0f - exp(-sourceRange / targetRange), 0.0001f);
    float mappedPeak = knee + (targetRange * (1.0f - exp(-x / targetRange)) / denominator);
    mappedPeak = min(mappedPeak, target);
    return ClampToDisplayPeak(mappedValue * (mappedPeak / max(peak, 0.0001f)));
}

float3 ApplyAdaptiveToneMap(float3 value)
{
    return ApplyAdaptiveToneMapWithWhiteScale(value, max(DisplayMapping.x, 1.0f));
}

bool IsHlgTransfer()
{
    return SourceEncoding.x > 1.5f && SourceEncoding.x < 2.5f;
}

bool IsPqTransfer()
{
    return SourceEncoding.x > 2.5f && SourceEncoding.x < 3.5f;
}

bool IsLinearScRgbTransfer()
{
    return SourceEncoding.x > 3.5f && SourceEncoding.x < 4.5f;
}

bool IsLinearSceneScRgbTransfer()
{
    return SourceEncoding.x > 4.5f;
}

float GetSingleLayerContentWhiteScale()
{
    float exposure = max(ViewModeParams.z, 0.0f);
    if (IsHlgTransfer())
    {
        return max((203.0f / 80.0f) * exposure, 0.0001f);
    }

    if (IsPqTransfer() || IsLinearSceneScRgbTransfer())
    {
        return max((203.0f / 80.0f) * exposure, 0.0001f);
    }

    if (IsLinearScRgbTransfer())
    {
        float displayScale = SourceEncoding.y <= 0.5f ? max(DisplayMapping.x, 1.0f) : 1.0f;
        return max(displayScale * exposure, 0.0001f);
    }

    return max(DisplayMapping.x, 1.0f);
}

float GetSingleLayerToneMapWhiteScale()
{
    if (ViewModeParams.x < 0.5f)
    {
        return max(DisplayMapping.x, 1.0f);
    }

    return max(GetSingleLayerContentWhiteScale(), 1.0f);
}

float GetSingleLayerSdrPreviewScale()
{
    if (ViewModeParams.x >= 0.5f)
    {
        return 1.0f;
    }

    return max(DisplayMapping.x, 1.0f) / max(GetSingleLayerContentWhiteScale(), 0.0001f);
}

float3 ApplySingleLayerDisplayFitToneMap(float3 value)
{
    if (ToneMapInput.x < 0.5f)
    {
        return ClampToDisplayPeak(value);
    }

    float whiteScale = GetSingleLayerToneMapWhiteScale();
    float virtualTarget = max(ToneMapInput.y, whiteScale);
    float physicalTarget = ToneMapOutput.x > 0.0f ? ToneMapOutput.x : virtualTarget;
    float target = clamp(ToneMapOutput.z, whiteScale, max(physicalTarget, whiteScale));
    float tonePeak = max(ToneMapInput.z, max(virtualTarget, target));
    float midScale = clamp(ToneMapOutput.w, 0.10f, 1.0f);
    float pressure = saturate(1.0f - midScale);
    float3 workingValue = value * midScale;
    float luminance = max(dot(workingValue, float3(0.2126f, 0.7152f, 0.0722f)), 0.0f);
    if (luminance <= 0.000001f)
    {
        return ClampToDisplayPeak(workingValue);
    }

    float mappedLuminance;
    if (luminance <= whiteScale)
    {
        float midGamma = lerp(1.02f, 1.22f, pressure);
        mappedLuminance = whiteScale * pow(saturate(luminance / max(whiteScale, 0.0001f)), midGamma);
    }
    else
    {
        float sourceRange = max((tonePeak * midScale) - whiteScale, 0.0001f);
        float targetRange = max(target - whiteScale, 0.0001f);
        float normalized = saturate((luminance - whiteScale) / sourceRange);
        float shoulder = lerp(2.3f, 3.8f, pressure);
        float denominator = max(1.0f - exp(-shoulder), 0.0001f);
        float mappedExcess = targetRange * (1.0f - exp(-normalized * shoulder)) / denominator;
        mappedLuminance = min(whiteScale + mappedExcess, target);
    }

    return ClampToDisplayPeak(workingValue * (mappedLuminance / max(luminance, 0.0001f)));
}

float3 ApplySingleLayerToneMap(float3 value)
{
    if (ToneMapOutput.y > 1.5f)
    {
        return ApplySingleLayerDisplayFitToneMap(value);
    }

    return ApplyAdaptiveToneMapWithWhiteScale(value, GetSingleLayerToneMapWhiteScale());
}

float3 ApplyHdrOutputMapping(float3 value)
{
    return ApplyAdaptiveToneMap(ApplySdrWhiteScale(value));
}

float3 ApplySdrDisplayAdjustment(float3 value)
{
    return ClampToDisplayPeak(ApplySdrWhiteScale(value));
}

float3 DecodeBaseImageSample(float3 encoded)
{
    float transfer = SourceEncoding.x;
    float3 sceneLinear;
    bool isLinearSceneScRgb = transfer > 4.5f;
    bool isLinearScRgb = transfer > 3.5f && transfer < 4.5f;
    if (isLinearSceneScRgb || isLinearScRgb)
    {
        sceneLinear = encoded;
    }
    else if (transfer > 2.5f)
    {
        sceneLinear = PqToSceneLinear(encoded);
    }
    else if (transfer > 1.5f)
    {
        float targetPeak = CalculateHdrTargetScenePeak();
        float3 hlgScene = max(HlgToSceneLinear(encoded), 0.0f);
        float hlgLuma = dot(hlgScene, float3(0.2627f, 0.6780f, 0.0593f));
        sceneLinear = hlgScene * pow(max(hlgLuma, 0.000001f), CalculateHlgSystemGamma(targetPeak) - 1.0f) * targetPeak;
    }
    else
    {
        sceneLinear = SrgbToLinear(encoded);
    }

    if (SourceEncoding.y > 2.5f)
    {
        sceneLinear = ProPhotoToBt709(sceneLinear);
        if (ViewModeParams.w > 0.5f)
        {
            sceneLinear = max(sceneLinear, 0.0f);
        }
    }
    else if (SourceEncoding.y > 1.5f)
    {
        sceneLinear = Bt2020ToBt709(sceneLinear);
        if (ViewModeParams.w > 0.5f)
        {
            sceneLinear = max(sceneLinear, 0.0f);
        }
    }
    else if (SourceEncoding.y > 0.5f)
    {
        sceneLinear = P3ToBt709(sceneLinear);
        if (ViewModeParams.w > 0.5f)
        {
            sceneLinear = max(sceneLinear, 0.0f);
        }
    }

    if (transfer <= 1.5f)
    {
        return ApplySdrDisplayAdjustment(sceneLinear);
    }

    // Diffuse-white / exposure scale (1.0 = absolute). This only affects
    // single-layer HDR (PQ/HLG/linear scRGB) content.
    sceneLinear *= max(ViewModeParams.z, 0.0f);
    sceneLinear *= GetSingleLayerSdrPreviewScale();

    return ApplySingleLayerToneMap(sceneLinear);
}

float3 DecodeGainMapBaseSample(float3 encoded)
{
    if (SourceEncoding.x > 0.5f && SourceEncoding.x < 1.5f)
    {
        return Rec709ToLinear(encoded);
    }

    return SrgbToLinear(encoded);
}

float CalculateGainMapWeight()
{
    if (HdrCapacity.y <= HdrCapacity.x)
    {
        return saturate(GainMapControl.x);
    }

    float displayHeadroom = DisplayMapping.z;
    return saturate(GainMapControl.x) * saturate((displayHeadroom - HdrCapacity.x) / (HdrCapacity.y - HdrCapacity.x));
}

float CalculateGainMapSceneScale()
{
    float exposureScale = max(ViewModeParams.z, 0.0f);
    if (GainMapControl.y <= 0.5f)
    {
        return (203.0f / 80.0f) * exposureScale;
    }

    return max(DisplayMapping.x, 1.0f) * exposureScale;
}

float3 ApplyGainMapOutputMapping(float3 value)
{
    return ApplyAdaptiveToneMap(value * CalculateGainMapSceneScale());
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    float4 fit = FitToImage(input.TexCoord);
    clip(fit.z - 0.5f);
    float2 uv = ApplyOrientation(fit.xy);
    float3 sdr = DecodeGainMapBaseSample(PrimaryTexture.Sample(LinearClampSampler, uv).rgb);
    float3 recovery = saturate(GainMapTexture.Sample(LinearClampSampler, uv).rgb);

    if (ViewModeParams.x < 0.5f)
    {
        return float4(ApplySdrDisplayAdjustment(ConvertGainMapBaseToBt709(sdr)), 1.0f);
    }

    if (ViewModeParams.x > 2.5f && ViewModeParams.x < 3.5f)
    {
        return float4(ApplySdrDisplayAdjustment(SrgbToLinear(recovery)), 1.0f);
    }

    float3 hdr;
    if (GainMapControl.y > 0.5f)
    {
        float3 gain = saturate(Rec709ToLinear(recovery));
        float headroom = max(GainMapMax.x, 1.0f);
        float effectiveHeadroom = pow(headroom, CalculateGainMapWeight());
        hdr = sdr * (1.0f + ((effectiveHeadroom - 1.0f) * gain));
    }
    else
    {
        float3 logRecovery = pow(recovery, 1.0f / max(Gamma.rgb, 0.0001f));
        float3 logBoost = lerp(GainMapMin.rgb, GainMapMax.rgb, logRecovery);
        hdr = (sdr + OffsetSdr.rgb) * exp2(logBoost * CalculateGainMapWeight()) - OffsetHdr.rgb;
    }
    return float4(ApplyGainMapOutputMapping(ConvertGainMapBaseToBt709(hdr)), 1.0f);
}

float4 BaseImagePSMain(VertexOutput input) : SV_TARGET
{
    float4 fit = FitToImage(input.TexCoord);
    clip(fit.z - 0.5f);
    float2 uv = fit.xy;
    float3 encoded = PrimaryTexture.Sample(LinearClampSampler, uv).rgb;
    float3 mapped = DecodeBaseImageSample(encoded);
    return float4(mapped, 1.0f);
}
""";

    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
        D3DFeatureLevel.Level_10_1,
        D3DFeatureLevel.Level_10_0,
    ];

    private static readonly Guid WinUiSwapChainPanelNativeGuid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

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
    private int _pixelWidth;
    private int _pixelHeight;
    private int _contentPixelWidth;
    private int _contentPixelHeight;
    private float _contentOrientation = 1.0f;
    private bool _isDisposed;
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
        set
        {
            if (_viewMode == value)
            {
                return;
            }

            _viewMode = value;
            UpdateGainMapConstantsBuffer();
        }
    }

    public HdrHeadroomMode HeadroomMode
    {
        get => _headroomMode;
        set
        {
            if (_headroomMode == value)
            {
                return;
            }

            _headroomMode = value;
            UpdateGainMapConstantsBuffer();
        }
    }

    public float? DisplayCapacityOverrideLog2
    {
        get => _displayCapacityOverrideLog2;
        set
        {
            _displayCapacityOverrideLog2 = value;
            UpdateGainMapConstantsBuffer();
        }
    }

    public bool AdaptiveToneMappingEnabled
    {
        get => _adaptiveToneMappingEnabled;
        set
        {
            _adaptiveToneMappingEnabled = value;
            UpdateGainMapConstantsBuffer();
        }
    }

    // Exposure / diffuse-white scale applied before tone mapping. 1.0 keeps the
    // content's default reference white; values below 1.0 dim the image and
    // values above 1.0 brighten it. The shader uses this for single-layer HDR
    // and gain-map HDR output paths.
    public float ReferenceWhiteExposureScale
    {
        get => _referenceWhiteExposureScale;
        set
        {
            _referenceWhiteExposureScale = float.IsFinite(value) ? Math.Clamp(value, 0.05f, 16.0f) : 1.0f;
            UpdateGainMapConstantsBuffer();
        }
    }

    public ColorGamutMappingMode ColorGamutMappingMode
    {
        get => _colorGamutMappingMode;
        set
        {
            _colorGamutMappingMode = Enum.IsDefined(value) ? value : ColorGamutMappingMode.Managed;
            UpdateGainMapConstantsBuffer();
        }
    }

    public string LastRenderStatus { get; private set; } = "Renderer not attached";

    public bool LastFrameHasVisiblePixels { get; private set; }

    public bool IsSwapChainPanelBound { get; private set; }

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
        set
        {
            _displayConfiguration = value ?? HdrDisplayConfiguration.Unknown;
            UpdateGainMapConstantsBuffer();
        }
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

    public async Task LoadAsync(HdrImageDocument document, CancellationToken cancellationToken)
    {
        _document = document;
        cancellationToken.ThrowIfCancellationRequested();
        if (document.HasRenderableGainMap && _swapChain is not null)
        {
            await PresentGainMapFrameAsync(document, cancellationToken);
            return;
        }

        if (_swapChain is not null)
        {
            await PresentBaseImageFrameAsync(document, cancellationToken);
            return;
        }

        PresentProbeFrame();
    }

    public async Task ResizeAsync(int pixelWidth, int pixelHeight, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
    }

    private void EnsureDevice()
    {
        if (_device is not null)
        {
            return;
        }

        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels);
        _context = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();
        EnsureDirect2DDevice(dxgiDevice);
    }

    private void EnsureDirect2DDevice(IDXGIDevice dxgiDevice)
    {
        if (_d2dContext is not null)
        {
            return;
        }

        _d2dFactory = D2D.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;
        _d2dContext2 = _d2dContext.QueryInterfaceOrNull<ID2D1DeviceContext2>();
        _d2dContext5 = _d2dContext.QueryInterfaceOrNull<ID2D1DeviceContext5>();
        _wicFactory = new IWICImagingFactory();
    }

    private void CreateSwapChain(int pixelWidth, int pixelHeight)
    {
        if (_panel is null || _device is null || _factory is null)
        {
            return;
        }

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        var description = new SwapChainDescription1(
            (uint)pixelWidth,
            (uint)pixelHeight,
            Format.R16G16B16A16_Float,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        _swapChain = _factory.CreateSwapChainForComposition(_device, description, null);
        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        _swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
        ConfigureScRgbColorSpace();
        ConfigureSwapChainPanelScale();

        if (!TryBindSwapChainToPanel())
        {
            return;
        }

        CreateRenderTargetView();
    }

    private void ResizeSwapChain(int pixelWidth, int pixelHeight)
    {
        if (_swapChain is null)
        {
            return;
        }

        ReleaseD2DTargetBitmap();
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _swapChain.ResizeBuffers(
            bufferCount: 2,
            width: (uint)pixelWidth,
            height: (uint)pixelHeight,
            newFormat: Format.R16G16B16A16_Float,
            swapChainFlags: SwapChainFlags.None).CheckError();

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        ConfigureScRgbColorSpace();
        ConfigureSwapChainPanelScale();
        if (!TryBindSwapChainToPanel())
        {
            return;
        }

        CreateRenderTargetView();
    }

    private void ConfigureScRgbColorSpace()
    {
        _scRgbColorSpaceAvailable = false;
        _scRgbColorSpaceApplied = false;

        if (_swapChain3 is null)
        {
            return;
        }

        var support = _swapChain3.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
        _scRgbColorSpaceAvailable = (support & SwapChainColorSpaceSupportFlags.Present) == SwapChainColorSpaceSupportFlags.Present;
        if (_scRgbColorSpaceAvailable)
        {
            _swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            _scRgbColorSpaceApplied = true;
        }
    }

    private void ConfigureSwapChainPanelScale()
    {
        if (_panel is null || _swapChain2 is null)
        {
            _swapChainTransformStatus = "swap chain DPI transform unavailable";
            return;
        }

        var scaleX = Math.Max(0.0001f, _panel.CompositionScaleX);
        var scaleY = Math.Max(0.0001f, _panel.CompositionScaleY);
        var transform = Matrix3x2.CreateScale(1.0f / scaleX, 1.0f / scaleY);
        _swapChain2.MatrixTransform = transform;
        _swapChainTransformStatus = $"DPI transform {1.0f / scaleX:0.###}x{1.0f / scaleY:0.###} for scale {scaleX:0.###}x{scaleY:0.###}";
    }

    private void CreateRenderTargetView()
    {
        if (_device is null || _swapChain is null)
        {
            LastRenderStatus = "Create render target skipped: device or swap chain missing";
            return;
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer, null);
        CreateD2DTargetBitmap();
    }

    private void CreateD2DTargetBitmap()
    {
        if (_d2dContext is null || _swapChain is null)
        {
            return;
        }

        ReleaseD2DTargetBitmap();
        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new DCommonPixelFormat(Format.R16G16B16A16_Float, DCommonAlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _d2dTargetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, properties);
        _d2dContext.Target = _d2dTargetBitmap;
    }

    private void ReleaseD2DTargetBitmap()
    {
        if (_d2dContext is not null)
        {
            _d2dContext.Target = null;
        }

        _d2dTargetBitmap?.Dispose();
        _d2dTargetBitmap = null;
    }

    private void PresentProbeFrame()
    {
        EnsureRenderTargetView();

        if (_context is null || _swapChain is null || _renderTargetView is null)
        {
            LastRenderStatus = "Probe frame skipped: D3D resources missing";
            return;
        }

        var color = new Color4(1.35f, 1.35f, 1.35f, 1.0f);
        _context.ClearRenderTargetView(_renderTargetView, color);
        _context.Flush();
        _swapChain.Present(1, PresentFlags.None).CheckError();
        LastRenderStatus = $"Probe frame presented at {_pixelWidth}x{_pixelHeight}; {BuildOutputSummary()}";
    }

    private async Task PresentGainMapFrameAsync(HdrImageDocument document, CancellationToken cancellationToken)
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            LastRenderStatus = "Gain-map render skipped: device, swap chain, or size missing";
            return;
        }

        EnsureGainMapDeviceResources();
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
        var decodeMaxPixelSize = CalculateViewerDecodeMaxPixelSize(document);
        var decodeMs = 0L;
        var uploadMs = 0L;
        if (!string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase)
            || _loadedGainMapWriteTimeUtc != lastWriteTimeUtc
            || !_loadedGainMapMode)
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
            LastRenderStatus = $"Gain-map textures loaded{(wasPreloaded ? " from preload" : string.Empty)}: base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}";
        }

        var drawTimer = Stopwatch.StartNew();
        RenderGainMap();
        var drawMs = drawTimer.ElapsedMilliseconds;
        LastRenderStatus = $"{LastRenderStatus}; renderer timing decode {decodeMs}ms, upload {uploadMs}ms, draw+present {drawMs}ms";
    }

    private async Task PresentBaseImageFrameAsync(HdrImageDocument document, CancellationToken cancellationToken)
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            LastRenderStatus = "Base render skipped: device, swap chain, or size missing";
            return;
        }

        EnsureGainMapDeviceResources();
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(document.Path);
        var decodeMaxPixelSize = CalculateViewerDecodeMaxPixelSize(document);
        var decodeMs = 0L;
        var uploadMs = 0L;
        if (!string.Equals(_loadedGainMapPath, document.Path, StringComparison.OrdinalIgnoreCase)
            || _loadedGainMapWriteTimeUtc != lastWriteTimeUtc
            || _loadedGainMapMode)
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
                LastRenderStatus = $"Base texture loaded{(wasPreloaded ? " from preload" : string.Empty)}: {bitmap.PixelWidth}x{bitmap.PixelHeight} via {bitmap.RenderEncodingSummary}";
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

        if (_gainMapVertexShader is null || _gainMapPixelShader is null || _baseImagePixelShader is null)
        {
            var shaderFlags = ShaderFlags.OptimizationLevel3;
            var vertexShader = Compiler.Compile(GainMapShaderSource, "VSMain", "GainMap.hlsl", "vs_5_0", shaderFlags, EffectFlags.None);
            var pixelShader = Compiler.Compile(GainMapShaderSource, "PSMain", "GainMap.hlsl", "ps_5_0", shaderFlags, EffectFlags.None);
            var basePixelShader = Compiler.Compile(GainMapShaderSource, "BaseImagePSMain", "GainMap.hlsl", "ps_5_0", shaderFlags, EffectFlags.None);
            _gainMapVertexShader = _device.CreateVertexShader(vertexShader.Span, null);
            _gainMapPixelShader = _device.CreatePixelShader(pixelShader.Span, null);
            _baseImagePixelShader = _device.CreatePixelShader(basePixelShader.Span, null);
        }

        _linearClampSampler ??= _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _gainMapConstantsBuffer ??= _device.CreateBuffer(
            (uint)Marshal.SizeOf<GainMapShaderConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
    }

    private int? CalculateViewerDecodeMaxPixelSize(HdrImageDocument document)
    {
        if (DecoderCatalog.IsJpegXrExtension(Path.GetExtension(document.Path)))
        {
            return null;
        }

        var target = Math.Max(_pixelWidth, _pixelHeight);
        if (target <= 0)
        {
            return 3072;
        }

        return Math.Clamp((int)Math.Ceiling(target * 1.50), 1600, 3072);
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
        UpdateGainMapConstantsBuffer();
        _context.VSSetShader(_gainMapVertexShader);
        _context.PSSetShader(_gainMapPixelShader);
        _context.PSSetShaderResources(0, [_primaryTextureView, _gainMapTextureView]);
        _context.PSSetSampler(0, _linearClampSampler);
        _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
        _context.Draw(3, 0);

        var frameAnalysis = AnalyzeBackBuffer();
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

                d2dStage = "present D2D color-managed frame";
                var colorManagedFrameAnalysis = AnalyzeBackBuffer();
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

            var frameAnalysis = AnalyzeBackBuffer();
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
                    frameAnalysis = AnalyzeBackBuffer();
                    d2dInputFeedbackSummary = $"; measured input max {inputMaxNits:0} nits";
                }
            }

            d2dStage = "present D2D frame";
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
        var layout = CalculateUniformImageLayout(
            _contentPixelWidth,
            _contentPixelHeight,
            _contentOrientation,
            _pixelWidth,
            _pixelHeight);
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
        if (_primaryAnalysisSource?.IsHdrEncoded != true && TryRenderBaseImageWithDirect2D(document))
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
        UpdateGainMapConstantsBuffer();
        _context.VSSetShader(_gainMapVertexShader);
        _context.PSSetShader(_baseImagePixelShader);
        _context.PSSetShaderResource(0, _primaryTextureView);
        _context.PSSetSampler(0, _linearClampSampler);
        _context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
        _context.Draw(3, 0);

        var frameAnalysis = AnalyzeBackBuffer();
        LastFrameHasVisiblePixels = frameAnalysis.HasVisiblePixels;
        _context.PSUnsetShaderResources(0, 1);
        _context.Flush();
        _swapChain.Present(1, PresentFlags.None).CheckError();
        var d2dFallback = string.IsNullOrWhiteSpace(_d2dFallbackStatus) ? string.Empty : $"{_d2dFallbackStatus}; ";
        LastRenderStatus = $"{d2dFallback}Base image shader presented at {_pixelWidth}x{_pixelHeight}; {BuildLayoutSummary()}; decoder {_baseEncodingSummary}; {BuildBaseImageMappingSummary()}; {frameAnalysis.Summary}; {BuildOutputSummary()}";
    }

    private void UpdateGainMapConstantsBuffer()
    {
        if (_context is null || _gainMapConstantsBuffer is null)
        {
            return;
        }

        var constants = _gainMapConstants;
        constants.GainMapControl = new Vector4(
            constants.GainMapControl.X,
            constants.GainMapControl.Y,
            constants.GainMapControl.Z,
            0.0f);
        constants.DisplayMapping = new Vector4(
            EffectiveSceneToSdrWhiteScale,
            EffectiveMaxSceneValue,
            EffectiveDisplayBoostLog2,
            _displayConfiguration.MaxFullFrameSceneValue);
        constants.ImageLayout = CalculateUniformImageLayout(
            _contentPixelWidth,
            _contentPixelHeight,
            _contentOrientation,
            _pixelWidth,
            _pixelHeight);
        constants.ToneMapInput = BuildToneMapConstants(constants);
        constants.ToneMapOutput = BuildToneMapOutputConstants();
        constants.ViewModeParams = new Vector4((float)EffectiveViewModeForCurrentFrame, (float)_headroomMode, _referenceWhiteExposureScale, (float)_colorGamutMappingMode);
        _context.UpdateSubresource(in constants, _gainMapConstantsBuffer, 0, 0, 0, null);
    }

    private Vector4 BuildToneMapConstants(GainMapShaderConstants constants)
    {
        _toneMapAnalysis = default;
        _toneMappingEnabledForCurrentFrame = false;
        var effectiveViewMode = EffectiveViewModeForCurrentFrame;
        var baseHdrImage = _gainMapAnalysisSource is null
            && _primaryAnalysisSource?.IsHdrEncoded == true;
        var gainMapHdrImage = _gainMapAnalysisSource is not null;
        var alternateImageMode = effectiveViewMode == GainmapViewMode.AlternateImage;
        var baseHdrNeedsToneMap = baseHdrImage && !alternateImageMode;
        var gainMapNeedsDisplayFitToneMap = gainMapHdrImage
            && effectiveViewMode == GainmapViewMode.Adaptive;
        var adaptiveToneMapRequested = _adaptiveToneMappingEnabled && !alternateImageMode;
        var useToneMapping = adaptiveToneMapRequested || baseHdrNeedsToneMap || gainMapNeedsDisplayFitToneMap;
        if (!useToneMapping
            || _primaryAnalysisSource is null
            || _contentPixelWidth <= 0
            || _contentPixelHeight <= 0)
        {
            return Vector4.Zero;
        }

        var analysis = _gainMapAnalysisSource is not null
            ? AnalyzeGainMapToneMapInput(constants)
            : AnalyzeBaseHdrToneMapInput(constants);
        if (analysis.VirtualTargetPeak <= 0.0f)
        {
            return Vector4.Zero;
        }

        _toneMapAnalysis = analysis;
        _toneMappingEnabledForCurrentFrame = true;
        return new Vector4(
            1.0f,
            analysis.VirtualTargetPeak,
            analysis.ToneMapPeak,
            analysis.ContentAverage);
    }

    private Vector4 BuildToneMapOutputConstants()
    {
        if (!_toneMappingEnabledForCurrentFrame || _toneMapAnalysis.VirtualTargetPeak <= 0.0f)
        {
            return Vector4.Zero;
        }

        var mode = ToneModeGainMap;
        if (_gainMapAnalysisSource is null && _primaryAnalysisSource?.IsHdrEncoded == true)
        {
            mode = IsSingleLayerDisplayFitToneMapEnabled()
                ? ToneModeSingleLayerDisplayFit
                : ToneModeSingleLayerSystem;
        }

        return new Vector4(
            _toneMapAnalysis.PhysicalTargetPeak,
            mode,
            _toneMapAnalysis.AdaptiveTargetPeak,
            _toneMapAnalysis.GlobalScale);
    }

    private ToneMapAnalysis AnalyzeGainMapToneMapInput(GainMapShaderConstants constants)
    {
        if (_gainMapAnalysisSource is null)
        {
            return default;
        }

        var weight = CalculateGainMapWeightForStatus();
        var whiteScale = Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f);
        var sceneScale = CalculateGainMapSceneScale(constants);
        var virtualTargetPeak = CalculateBaseHdrVirtualTargetPeak(constants, whiteScale);
        var manualTarget = _displayCapacityOverrideLog2 is not null && !_adaptiveToneMappingEnabled;
        var effectiveMaxSceneValue = EffectiveMaxSceneValue;
        var physicalTargetPeak = manualTarget
            ? virtualTargetPeak
            : effectiveMaxSceneValue > 0.0f
                ? Math.Min(effectiveMaxSceneValue, virtualTargetPeak)
                : virtualTargetPeak;
        double luminanceSum = 0.0;
        var contentPeak = 0.0f;
        var samples = 0;
        var peakSamples = new List<float>(_gainMapAnalysisSource.Samples.Length);

        foreach (var sample in _gainMapAnalysisSource.Samples)
        {
            var hdr = constants.GainMapControl.Y > 0.5f
                ? HdrColorMath.ReconstructAppleHdrSample(sample.Sdr, sample.Gain, constants.GainMapMax.X, weight)
                : HdrColorMath.ReconstructAdobeHdrSample(sample.Sdr, sample.Gain, constants, weight);
            hdr = HdrColorMath.ConvertGainMapBaseToBt709(hdr, constants, _colorGamutMappingMode);
            hdr *= sceneScale;

            if (!float.IsFinite(hdr.X) || !float.IsFinite(hdr.Y) || !float.IsFinite(hdr.Z))
            {
                continue;
            }

            var samplePeak = Math.Max(hdr.X, Math.Max(hdr.Y, hdr.Z));
            contentPeak = Math.Max(contentPeak, samplePeak);
            peakSamples.Add(samplePeak);
            luminanceSum += Math.Max(0.0f, (0.2126f * hdr.X) + (0.7152f * hdr.Y) + (0.0722f * hdr.Z));
            samples++;
        }

        var average = samples > 0 ? (float)(luminanceSum / samples) : 0.0f;
        var highPercentilePeak = CalculatePercentile(peakSamples, 0.995f);
        var toneMapPeak = Math.Max(highPercentilePeak, contentPeak * 0.55f);
        var fullFrameLimit = _displayConfiguration.MaxFullFrameSceneValue;
        var adaptiveTargetPeak = _adaptiveToneMappingEnabled
            ? CalculateAdaptiveToneMapTarget(
                whiteScale,
                virtualTargetPeak,
                physicalTargetPeak,
                average,
                fullFrameLimit)
            : physicalTargetPeak;
        var globalScale = _adaptiveToneMappingEnabled
            ? CalculateGlobalToneMapScale(adaptiveTargetPeak, toneMapPeak, average, fullFrameLimit)
            : 1.0f;
        return new ToneMapAnalysis(
            contentPeak,
            highPercentilePeak,
            toneMapPeak,
            average,
            virtualTargetPeak,
            physicalTargetPeak,
            adaptiveTargetPeak,
            fullFrameLimit,
            globalScale);
    }

    private ToneMapAnalysis AnalyzeBaseHdrToneMapInput(GainMapShaderConstants constants)
    {
        if (_primaryAnalysisSource is null || !_primaryAnalysisSource.IsHdrEncoded)
        {
            return default;
        }

        var whiteScale = CalculateBaseHdrToneMapWhiteScale(constants);
        var virtualTargetPeak = CalculateBaseHdrVirtualTargetPeak(constants, whiteScale);
        var decodeTargetPeak = constants.SourceEncoding.X > 1.5f && constants.SourceEncoding.X < 2.5f
            ? HlgReferenceScenePeak
            : virtualTargetPeak;
        var sliderTarget = _displayCapacityOverrideLog2 is not null;
        var displayFitToneMap = _adaptiveToneMappingEnabled || sliderTarget;
        var displayLimitedTargetPeak = EffectiveMaxSceneValue > 0.0f
            ? Math.Min(EffectiveMaxSceneValue, virtualTargetPeak)
            : virtualTargetPeak;
        var physicalTargetPeak = sliderTarget && !_adaptiveToneMappingEnabled
            ? virtualTargetPeak
            : displayLimitedTargetPeak;
        double luminanceSum = 0.0;
        var contentPeak = 0.0f;
        var samples = 0;
        var peakSamples = new List<float>(_primaryAnalysisSource.Samples.Length);

        foreach (var sample in _primaryAnalysisSource.Samples)
        {
            var hdr = ReconstructBaseHdrSample(sample, constants, decodeTargetPeak);
            if (!float.IsFinite(hdr.X) || !float.IsFinite(hdr.Y) || !float.IsFinite(hdr.Z))
            {
                continue;
            }

            var samplePeak = Math.Max(0.0f, Math.Max(hdr.X, Math.Max(hdr.Y, hdr.Z)));
            contentPeak = Math.Max(contentPeak, samplePeak);
            peakSamples.Add(samplePeak);
            luminanceSum += Math.Max(0.0f, (0.2126f * hdr.X) + (0.7152f * hdr.Y) + (0.0722f * hdr.Z));
            samples++;
        }

        var average = samples > 0 ? (float)(luminanceSum / samples) : 0.0f;
        var highPercentilePeak = CalculatePercentile(peakSamples, 0.995f);
        var measuredToneMapPeak = Math.Max(highPercentilePeak, contentPeak * 0.55f);
        var toneMapPeak = displayFitToneMap
            ? Math.Clamp(
                measuredToneMapPeak,
                Math.Max(whiteScale, physicalTargetPeak),
                Math.Max(Math.Max(virtualTargetPeak, physicalTargetPeak), whiteScale))
            : measuredToneMapPeak;
        var fullFrameLimit = _displayConfiguration.MaxFullFrameSceneValue;
        var adaptiveTargetPeak = displayFitToneMap
            ? CalculateAdaptiveToneMapTarget(
                whiteScale,
                virtualTargetPeak,
                physicalTargetPeak,
                average,
                fullFrameLimit)
            : physicalTargetPeak;
        var globalScale = displayFitToneMap
            ? CalculateSingleLayerDisplayFitMidScale(average, whiteScale, fullFrameLimit)
            : 1.0f;
        return new ToneMapAnalysis(
            contentPeak,
            highPercentilePeak,
            toneMapPeak,
            average,
            virtualTargetPeak,
            physicalTargetPeak,
            adaptiveTargetPeak,
            fullFrameLimit,
            globalScale);
    }

    private float CalculateBaseHdrToneMapWhiteScale(GainMapShaderConstants constants)
    {
        return EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr
            ? Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f)
            : Math.Max(CalculateBaseHdrContentWhiteScale(constants), 1.0f);
    }

    private float CalculateBaseHdrContentWhiteScale(GainMapShaderConstants constants)
    {
        var exposure = Math.Max(_referenceWhiteExposureScale, 0.0f);
        return constants.SourceEncoding.X switch
        {
            > 4.5f => SingleLayerHdrReferenceWhiteScale * exposure,
            > 3.5f => (constants.SourceEncoding.Y <= 0.5f ? Math.Max(constants.DisplayMapping.X, 1.0f) : 1.0f) * exposure,
            > 2.5f => SingleLayerHdrReferenceWhiteScale * exposure,
            > 1.5f => HlgReferenceWhiteScene * exposure,
            _ => Math.Max(constants.DisplayMapping.X, 1.0f),
        };
    }

    private float CalculateBaseHdrSdrPreviewScale(GainMapShaderConstants constants)
    {
        if (EffectiveViewModeForCurrentFrame != GainmapViewMode.Sdr)
        {
            return 1.0f;
        }

        return Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f)
            / Math.Max(CalculateBaseHdrContentWhiteScale(constants), 0.0001f);
    }

    private float CalculateBaseHdrVirtualTargetPeak(GainMapShaderConstants constants, float whiteScale)
    {
        if (EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr)
        {
            return Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f);
        }

        if (_displayCapacityOverrideLog2 is { } overrideStops)
        {
            var targetNits = _displayConfiguration.SdrWhiteLevelInNits * Math.Pow(2.0, overrideStops);
            return Math.Max((float)(targetNits / HdrColorMath.ReferenceWhiteNits), whiteScale);
        }

        if (EffectiveMaxSceneValue > 0.0f)
        {
            return Math.Max(EffectiveMaxSceneValue, whiteScale);
        }

        if (constants.SourceEncoding.X > 1.5f && constants.SourceEncoding.X < 2.5f)
        {
            return Math.Max(HlgReferenceScenePeak, whiteScale);
        }

        return Math.Max(
            whiteScale * MathF.Pow(2.0f, Math.Max(EffectiveDisplayBoostLog2, 0.0f)),
            whiteScale);
    }

    private Vector3 ReconstructBaseHdrSample(
        Vector3 sample,
        GainMapShaderConstants constants,
        float targetScenePeak)
    {
        var linear = constants.SourceEncoding.X switch
        {
            > 4.5f => sample,
            > 3.5f => sample,
            > 2.5f => HdrColorMath.PqToSceneLinear(sample),
            > 1.5f => HdrColorMath.HlgToSceneLinear(sample, targetScenePeak),
            _ => sample,
        };

        var p709 = constants.SourceEncoding.Y switch
        {
            > 2.5f => HdrColorMath.ConvertProPhotoToBt709(linear, _colorGamutMappingMode),
            > 1.5f => HdrColorMath.ConvertBt2020ToBt709(linear, _colorGamutMappingMode),
            > 0.5f => HdrColorMath.ConvertP3ToBt709(linear, _colorGamutMappingMode),
            _ => linear,
        };

        var mapped = constants.SourceEncoding.X > 3.5f && constants.SourceEncoding.X < 4.5f && constants.SourceEncoding.Y <= 0.5f
            ? p709 * Math.Max(constants.DisplayMapping.X, 1.0f)
            : p709;
        if (constants.SourceEncoding.X > 1.5f)
        {
            mapped *= Math.Max(_referenceWhiteExposureScale, 0.0f);
            mapped *= CalculateBaseHdrSdrPreviewScale(constants);
        }

        return mapped;
    }

    private static Vector3 ReadEncodedRgb(DecodedBitmap bitmap, int x, int y)
    {
        var index = checked(((y * bitmap.PixelWidth) + x) * bitmap.BytesPerPixel);
        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Float)
        {
            return new Vector3(
                ReadHalfLittleEndian(bitmap.RgbaPixels, index),
                ReadHalfLittleEndian(bitmap.RgbaPixels, index + 2),
                ReadHalfLittleEndian(bitmap.RgbaPixels, index + 4));
        }

        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            return new Vector3(
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index) / 65535.0f,
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index + 2) / 65535.0f,
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index + 4) / 65535.0f);
        }

        return new Vector3(
            bitmap.RgbaPixels[index] / 255.0f,
            bitmap.RgbaPixels[index + 1] / 255.0f,
            bitmap.RgbaPixels[index + 2] / 255.0f);
    }

    private static float ReadHalfLittleEndian(byte[] data, int offset)
    {
        var bits = unchecked((ushort)(data[offset] | (data[offset + 1] << 8)));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static Vector3 ReadLinearSrgb(DecodedBitmap bitmap, int x, int y)
    {
        var encoded = ReadEncodedRgb(bitmap, x, y);
        return HdrColorMath.SrgbToLinear(encoded);
    }

    private static Vector3 ReadGainMapSample(DecodedBitmap bitmap, int primaryX, int primaryY, int primaryWidth, int primaryHeight)
    {
        var x = Math.Clamp((int)((primaryX + 0.5f) * bitmap.PixelWidth / Math.Max(primaryWidth, 1)), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)((primaryY + 0.5f) * bitmap.PixelHeight / Math.Max(primaryHeight, 1)), 0, bitmap.PixelHeight - 1);
        return ReadEncodedRgb(bitmap, x, y);
    }

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static float CalculateAdaptiveToneMapTarget(
        float whiteScale,
        float virtualTarget,
        float physicalTarget,
        float contentAverage,
        float fullFrameLimit)
    {
        var outputTarget = Math.Clamp(physicalTarget, whiteScale, Math.Max(virtualTarget, whiteScale));
        var headroom = Math.Max(outputTarget - whiteScale, 0.0f);
        if (headroom <= 0.0f)
        {
            return outputTarget;
        }

        var averageRelativeToWhite = contentAverage / Math.Max(whiteScale, 0.0001f);
        var aplFactor = 1.0f / (1.0f + (1.6f * Math.Max(averageRelativeToWhite - 0.18f, 0.0f)));
        var minimumHeadroomFraction = fullFrameLimit > 0.0f && fullFrameLimit < whiteScale
            ? 0.42f
            : 0.28f;
        var target = whiteScale + (headroom * Math.Max(aplFactor, minimumHeadroomFraction));

        if (fullFrameLimit > 0.0f && outputTarget > whiteScale)
        {
            var fullFrameTarget = fullFrameLimit < whiteScale
                ? whiteScale + (headroom * 0.42f)
                : Math.Clamp(fullFrameLimit * 1.45f, whiteScale + (headroom * minimumHeadroomFraction), outputTarget);
            var pressureStart = Math.Min(whiteScale * 0.55f, Math.Max(fullFrameLimit * 0.85f, whiteScale * 0.25f));
            var fullFramePressure = Math.Clamp(
                (contentAverage - pressureStart) / Math.Max(fullFrameLimit - pressureStart, 0.0001f),
                0.0f,
                1.0f);
            target = Math.Min(target, Lerp(outputTarget, fullFrameTarget, fullFramePressure));
        }

        return Math.Clamp(target, whiteScale + (headroom * minimumHeadroomFraction), outputTarget);
    }

    private static float CalculateGlobalToneMapScale(
        float target,
        float toneMapPeak,
        float contentAverage,
        float fullFrameLimit)
    {
        var scale = toneMapPeak > target && toneMapPeak > 0.0f
            ? target / toneMapPeak
            : 1.0f;

        if (fullFrameLimit > 0.0f && contentAverage > fullFrameLimit)
        {
            scale = Math.Min(scale, fullFrameLimit / contentAverage);
        }

        return Math.Clamp(scale, 0.02f, 1.0f);
    }

    private static float CalculateSingleLayerDisplayFitMidScale(
        float contentAverage,
        float whiteScale,
        float fullFrameLimit)
    {
        var scale = 1.0f;
        if (fullFrameLimit > 0.0f && contentAverage > fullFrameLimit)
        {
            scale = Math.Min(scale, fullFrameLimit / contentAverage);
        }

        var averageRelativeToWhite = contentAverage / Math.Max(whiteScale, 0.0001f);
        if (averageRelativeToWhite > 0.45f)
        {
            scale = Math.Min(scale, 1.0f / (1.0f + (0.70f * (averageRelativeToWhite - 0.45f))));
        }

        return Math.Clamp(scale, 0.25f, 1.0f);
    }

    private static float CalculatePercentile(List<float> samples, float percentile)
    {
        if (samples.Count == 0)
        {
            return 0.0f;
        }

        samples.Sort();
        var index = (int)MathF.Round((samples.Count - 1) * Math.Clamp(percentile, 0.0f, 1.0f));
        return samples[Math.Clamp(index, 0, samples.Count - 1)];
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + ((end - start) * amount);
    }

    private static Vector4 CalculateUniformImageLayout(
        int contentPixelWidth,
        int contentPixelHeight,
        float orientation,
        int targetPixelWidth,
        int targetPixelHeight)
    {
        if (contentPixelWidth <= 0 || contentPixelHeight <= 0 || targetPixelWidth <= 0 || targetPixelHeight <= 0)
        {
            return new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
        }

        var displayedWidth = contentPixelWidth;
        var displayedHeight = contentPixelHeight;
        if (OrientationSwapsDimensions(orientation))
        {
            displayedWidth = contentPixelHeight;
            displayedHeight = contentPixelWidth;
        }

        var contentAspect = (float)displayedWidth / displayedHeight;
        var targetAspect = (float)targetPixelWidth / targetPixelHeight;
        var scaleX = 1.0f;
        var scaleY = 1.0f;
        if (targetAspect > contentAspect)
        {
            scaleX = contentAspect / targetAspect;
        }
        else
        {
            scaleY = targetAspect / contentAspect;
        }

        return new Vector4(
            scaleX,
            scaleY,
            (1.0f - scaleX) * 0.5f,
            (1.0f - scaleY) * 0.5f);
    }

    private static bool OrientationSwapsDimensions(float orientation)
    {
        return orientation is >= 4.5f and < 8.5f;
    }

    private string BuildOutputSummary()
    {
        var colorSpace = _scRgbColorSpaceApplied
            ? "scRGB swap chain"
            : _scRgbColorSpaceAvailable
                ? "scRGB color space available but not applied"
                : "scRGB color space unavailable";
        var capacityOverride = _displayCapacityOverrideLog2 is { } value
            ? $"; capacity override {value:0.###} stops target {_displayConfiguration.SdrWhiteLevelInNits * Math.Pow(2.0, value):0} nits"
            : string.Empty;
        return $"{_panelBindingStatus}; {_swapChainTransformStatus}; {colorSpace}; color gamut {BuildColorGamutMappingSummary()}{capacityOverride}; {_displayConfiguration.RenderSummary}";
    }

    private string BuildLayoutSummary()
    {
        var layout = CalculateUniformImageLayout(
            _contentPixelWidth,
            _contentPixelHeight,
            _contentOrientation,
            _pixelWidth,
            _pixelHeight);
        return $"source {_contentPixelWidth}x{_contentPixelHeight}, fit {layout.X:0.###}x{layout.Y:0.###}+{layout.Z:0.###},{layout.W:0.###}";
    }

    private string BuildGainMapSummary()
    {
        var modeLabel = _viewMode switch
        {
            GainmapViewMode.Sdr => "SDR",
            GainmapViewMode.Adaptive => "Adaptive",
            GainmapViewMode.AlternateImage => "Alternate Image",
            GainmapViewMode.GainMap => "Gain Map",
            _ => "Adaptive",
        };
        var toneMap = _toneMappingEnabledForCurrentFrame && _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? $", tone gain-map global scale {_toneMapAnalysis.GlobalScale:0.###}x, target {_toneMapAnalysis.AdaptiveTargetPeak:0.###}/{_toneMapAnalysis.PhysicalTargetPeak:0.###} physical ({CalculateToneMapCompressionRatio():0.##}x virtual {_toneMapAnalysis.VirtualTargetPeak:0.###}), full-frame {_toneMapAnalysis.FullFrameLimit:0.###}, content max/p99.5/tone/avg {_toneMapAnalysis.ContentPeak:0.###}/{_toneMapAnalysis.HighPercentilePeak:0.###}/{_toneMapAnalysis.ToneMapPeak:0.###}/{_toneMapAnalysis.ContentAverage:0.###}"
            : ", tone off";
        var baseGamut = _gainMapConstants.GainMapControl.Z switch
        {
            > 1.5f => "BT.2020",
            > 0.5f => "Display P3",
            _ => "BT.709/sRGB",
        };
        var baseTransfer = _gainMapConstants.SourceEncoding.X is > 0.5f and < 1.5f ? "BT.709" : "sRGB";
        var gainSampleStats = BuildGainSampleStats();
        return $"mode {modeLabel}, base {baseGamut}/{baseTransfer}, gain min {FormatVector3(_gainMapConstants.GainMapMin)}, max {FormatVector3(_gainMapConstants.GainMapMax)}, gamma {FormatVector3(_gainMapConstants.Gamma)}, cap {_gainMapConstants.HdrCapacity.X:0.###}-{_gainMapConstants.HdrCapacity.Y:0.###}, weight {CalculateGainMapWeightForStatus():0.###}, scene scale {CalculateGainMapSceneScale(_gainMapConstants):0.###}x, white scale {_displayConfiguration.SceneToSdrWhiteScale:0.###}x{gainSampleStats}{toneMap}";
    }

    private string BuildColorGamutMappingSummary()
    {
        return _colorGamutMappingMode switch
        {
            ColorGamutMappingMode.Clip => "clip",
            _ => "managed",
        };
    }

    private static string FormatVector3(Vector4 value)
    {
        return $"[{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}]";
    }

    private string BuildGainSampleStats()
    {
        if (_gainMapAnalysisSource is not { Samples.Length: > 0 } analysis)
        {
            return string.Empty;
        }

        var samples = analysis.Samples;
        var luma = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var g = samples[i].Gain;
            luma[i] = MathF.Max(MathF.Max(g.X, g.Y), g.Z);
        }
        Array.Sort(luma);
        float Percentile(float fraction)
        {
            var idx = Math.Clamp((int)MathF.Round(fraction * (luma.Length - 1)), 0, luma.Length - 1);
            return luma[idx];
        }
        return $", gain sample min/p50/p99/max {luma[0]:0.###}/{Percentile(0.5f):0.###}/{Percentile(0.99f):0.###}/{luma[^1]:0.###}";
    }

    private string BuildBaseImageMappingSummary()
    {
        var transfer = _gainMapConstants.SourceEncoding.X switch
        {
            > 4.5f => "scene-linear scRGB",
            > 3.5f => "linear scRGB",
            > 2.5f => "PQ",
            > 1.5f => "HLG",
            _ => "SDR"
        };
        if (transfer == "SDR")
        {
            var sdrPrimaries = _gainMapConstants.SourceEncoding.Y > 1.5f
                ? _gainMapConstants.SourceEncoding.Y > 2.5f ? "ProPhoto RGB to scRGB" : "BT.2020 to scRGB"
                : _gainMapConstants.SourceEncoding.Y > 0.5f ? "Display P3 to scRGB" : "sRGB/BT.709";
            return $"base map SDR {sdrPrimaries}, white scale {EffectiveSceneToSdrWhiteScale:0.###}x";
        }

        var targetScenePeak = CalculateBaseHdrVirtualTargetPeak(_gainMapConstants, CalculateBaseHdrToneMapWhiteScale(_gainMapConstants));
        var primaries = transfer is "linear scRGB" or "scene-linear scRGB"
            ? "working scRGB (P709, extended range)"
            : _gainMapConstants.SourceEncoding.Y > 2.5f
                ? "ProPhoto RGB to scRGB"
                : _gainMapConstants.SourceEncoding.Y > 1.5f
                    ? "BT.2020 to scRGB"
                    : _gainMapConstants.SourceEncoding.Y > 0.5f
                        ? "Display P3 to scRGB"
                        : "source primaries";
        var singleLayerDisplayFit = IsSingleLayerDisplayFitToneMapEnabled();
        var toneMode = singleLayerDisplayFit
            ? "display-fit highlight rolloff"
            : _displayCapacityOverrideLog2 is not null ? "manual peak" : "system auto";
        var scaleLabel = singleLayerDisplayFit ? "midtone scale" : "global scale";
        var toneMap = _toneMappingEnabledForCurrentFrame && _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? $", tone single-layer {toneMode} {scaleLabel} {_toneMapAnalysis.GlobalScale:0.###}x, target {_toneMapAnalysis.AdaptiveTargetPeak:0.###}/{_toneMapAnalysis.PhysicalTargetPeak:0.###} physical ({CalculateToneMapCompressionRatio():0.##}x virtual {_toneMapAnalysis.VirtualTargetPeak:0.###}), full-frame {_toneMapAnalysis.FullFrameLimit:0.###}, content max/p99.5/tone/avg {_toneMapAnalysis.ContentPeak:0.###}/{_toneMapAnalysis.HighPercentilePeak:0.###}/{_toneMapAnalysis.ToneMapPeak:0.###}/{_toneMapAnalysis.ContentAverage:0.###}"
            : ", tone off";
        var modeSummary = _viewMode == GainmapViewMode.GainMap
            ? "Adaptive (Gain Map unavailable: no gain map)"
            : EffectiveViewModeForCurrentFrame.ToString();
        var exposureReferenceWhite = transfer is "PQ" or "HLG" ? 203.0f : 80.0f;
        var exposureSummary = Math.Abs(_referenceWhiteExposureScale - 1.0f) > 0.001f
            ? $", exposure {_referenceWhiteExposureScale:0.###}x (diffuse white {_referenceWhiteExposureScale * exposureReferenceWhite:0} nits)"
            : string.Empty;
        var sdrClampSummary = EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr && _primaryAnalysisSource?.IsHdrEncoded == true
            ? ", SDR clamps HDR source to SDR white"
            : string.Empty;
        return $"base map {modeSummary} {transfer} {primaries}, target {targetScenePeak:0.###} scene ({targetScenePeak * 80.0f:0} nits){exposureSummary}{sdrClampSummary}{toneMap}";
    }

    private bool IsSingleLayerDisplayFitToneMapEnabled()
    {
        return _gainMapAnalysisSource is null
            && _primaryAnalysisSource?.IsHdrEncoded == true
            && (_adaptiveToneMappingEnabled || _displayCapacityOverrideLog2 is not null);
    }

    private float CalculateToneMapCompressionRatio()
    {
        return _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? _toneMapAnalysis.AdaptiveTargetPeak / _toneMapAnalysis.VirtualTargetPeak
            : 1.0f;
    }

    private float CalculateGainMapWeightForStatus()
    {
        var minCapacity = _gainMapConstants.HdrCapacity.X;
        var maxCapacity = _gainMapConstants.HdrCapacity.Y;
        if (maxCapacity <= minCapacity)
        {
            return Math.Clamp(_gainMapConstants.GainMapControl.X, 0.0f, 1.0f);
        }

        var explicitWeight = Math.Clamp(_gainMapConstants.GainMapControl.X, 0.0f, 1.0f);
        var adaptiveWeight = Math.Clamp((EffectiveDisplayBoostLog2 - minCapacity) / (maxCapacity - minCapacity), 0.0f, 1.0f);
        return explicitWeight * adaptiveWeight;
    }

    private float CalculateGainMapSceneScale(GainMapShaderConstants constants)
    {
        var exposureScale = Math.Max(_referenceWhiteExposureScale, 0.0f);
        if (constants.GainMapControl.Y <= 0.5f)
        {
            return (203.0f / 80.0f) * exposureScale;
        }

        return Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f) * exposureScale;
    }

    private GainmapViewMode EffectiveViewModeForCurrentFrame => _viewMode == GainmapViewMode.GainMap && _gainMapAnalysisSource is null
        ? GainmapViewMode.Adaptive
        : _viewMode;

    private float EffectiveDisplayBoostLog2 => EffectiveViewModeForCurrentFrame switch
    {
        GainmapViewMode.Sdr => 0.0f,
        GainmapViewMode.AlternateImage => Math.Max(_gainMapConstants.HdrCapacity.Y, _displayConfiguration.MaxDisplayBoostLog2),
        _ => _displayCapacityOverrideLog2 ?? _displayConfiguration.MaxDisplayBoostLog2,
    };

    private float EffectiveMaxSceneValue => EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr
        ? CalculateSdrModeMaxSceneValue()
        : EffectiveViewModeForCurrentFrame == GainmapViewMode.AlternateImage
            ? 0.0f
        : _displayCapacityOverrideLog2 is null ? _displayConfiguration.MaxSceneValue : 0.0f;

    private float CalculateSdrModeMaxSceneValue()
    {
        var whiteScale = Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f);
        if (_primaryAnalysisSource?.IsHdrEncoded == true)
        {
            return whiteScale;
        }

        // For a wide-gamut (Display P3 / BT.2020 / ProPhoto) SDR base image, the shader's
        // gamut conversion to BT.709 / scRGB produces channel values ABOVE the
        // SDR white scale for colours that sit outside the BT.709 hull (a fully
        // saturated P3 red becomes ~1.22 before the white-scale multiply). If we
        // cap ClampToDisplayPeak at the white scale, those out-of-gamut channels
        // are clipped back onto BT.709, collapsing the wide-gamut signal so a P3
        // background and an sRGB foreground become almost indistinguishable.
        // Allow headroom up to the display's scene capability so the wide-gamut
        // channels survive to the scRGB swap chain.
        var gamut = _primaryAnalysisSource?.ColorGamut ?? GainMapColorGamut.Unknown;
        if (gamut is GainMapColorGamut.DisplayP3 or GainMapColorGamut.Bt2100 or GainMapColorGamut.ProPhoto)
        {
            var displayCeiling = _displayConfiguration.MaxSceneValue;
            return displayCeiling > whiteScale ? displayCeiling : whiteScale;
        }

        return whiteScale;
    }

    private float EffectiveSceneToSdrWhiteScale => _displayConfiguration.SceneToSdrWhiteScale;

    private void EnsureRenderTargetView()
    {
        if (_renderTargetView is null && _device is not null && _swapChain is not null)
        {
            CreateRenderTargetView();
        }
    }

    private ID3D11Texture2D GetOrCreateFrameAnalysisStagingTexture()
    {
        // Reuse the CPU-readable staging texture across frames; only rebuild it
        // when the swap-chain size changes. Recreating it every present (it is
        // hit on every load / resize / zoom commit) was pure churn.
        if (_frameAnalysisStagingTexture is not null
            && _frameAnalysisStagingWidth == _pixelWidth
            && _frameAnalysisStagingHeight == _pixelHeight)
        {
            return _frameAnalysisStagingTexture;
        }

        _frameAnalysisStagingTexture?.Dispose();
        _frameAnalysisStagingTexture = null;

        var description = new Texture2DDescription(
            Format.R16G16B16A16_Float,
            checked((uint)_pixelWidth),
            checked((uint)_pixelHeight),
            arraySize: 1,
            mipLevels: 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        var texture = _device!.CreateTexture2D(description);
        _frameAnalysisStagingTexture = texture;
        _frameAnalysisStagingWidth = _pixelWidth;
        _frameAnalysisStagingHeight = _pixelHeight;
        return texture;
    }

    private void ReleaseFrameAnalysisStagingTexture()
    {
        _frameAnalysisStagingTexture?.Dispose();
        _frameAnalysisStagingTexture = null;
        _frameAnalysisStagingWidth = 0;
        _frameAnalysisStagingHeight = 0;
    }

    /// <summary>
    /// Rows that <see cref="AnalyzeBackBuffer"/> reads: the sparse sampling grid
    /// (same stepY formula as the sampling loop) plus the top/middle/bottom
    /// probe rows. Only these rows are copied into the staging texture.
    /// </summary>
    private static SortedSet<int> GetAnalysisRowIndices(int pixelHeight)
    {
        var stepY = Math.Max(1, pixelHeight / 64);
        var rows = new SortedSet<int>();
        for (var y = 0; y < pixelHeight; y += stepY)
        {
            rows.Add(y);
        }

        rows.Add(Math.Max(0, pixelHeight / 20));
        rows.Add(pixelHeight / 2);
        rows.Add(Math.Max(0, pixelHeight - (pixelHeight / 20) - 1));
        return rows;
    }

    private FrameAnalysis AnalyzeBackBuffer()
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            return new FrameAnalysis(false, 0.0f, "frame stats unavailable");
        }

        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var stagingTexture = GetOrCreateFrameAnalysisStagingTexture();
            // Copy only the rows the sampler below reads. A full CopyResource
            // moved the entire FP16 back buffer (~66 MB at 4K) into CPU-readable
            // memory on every present, while the sparse sampling grid plus the
            // three probe points only touch ~70 rows.
            foreach (var row in GetAnalysisRowIndices(_pixelHeight))
            {
                _context.CopySubresourceRegion(
                    stagingTexture, 0, 0, (uint)row, 0,
                    backBuffer, 0,
                    new Box(0, row, 0, _pixelWidth, row + 1, 1));
            }

            _context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mappedResource).CheckError();
            try
            {
                var stepX = Math.Max(1, _pixelWidth / 64);
                var stepY = Math.Max(1, _pixelHeight / 64);
                double sum = 0.0;
                var max = 0.0f;
                var samples = 0;

                for (var y = 0; y < _pixelHeight; y += stepY)
                {
                    var row = IntPtr.Add(mappedResource.DataPointer, checked(y * (int)mappedResource.RowPitch));
                    for (var x = 0; x < _pixelWidth; x += stepX)
                    {
                        var pixel = IntPtr.Add(row, checked(x * 8));
                        var r = ReadHalf(pixel, 0);
                        var g = ReadHalf(pixel, 2);
                        var b = ReadHalf(pixel, 4);
                        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                        {
                            continue;
                        }

                        var luminance = Math.Max(0.0f, (0.2126f * r) + (0.7152f * g) + (0.0722f * b));
                        sum += luminance;
                        max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
                        samples++;
                    }
                }

                var average = samples > 0 ? (float)(sum / samples) : 0.0f;
                var visible = max > 0.002f || average > 0.0005f;
                var top = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, Math.Max(0, _pixelHeight / 20));
                var middle = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, _pixelHeight / 2);
                var bottom = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, Math.Max(0, _pixelHeight - (_pixelHeight / 20) - 1));
                return new FrameAnalysis(visible, max, $"frame avg {average:0.###}, max {max:0.###}, y {top:0.###}/{middle:0.###}/{bottom:0.###}");
            }
            finally
            {
                _context.Unmap(stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            return new FrameAnalysis(true, 0.0f, $"frame stats unavailable: {ex.GetType().Name}");
        }
    }

    private static float ReadHalf(IntPtr pixel, int byteOffset)
    {
        var bits = unchecked((ushort)Marshal.ReadInt16(pixel, byteOffset));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static float SampleLuminance(IntPtr dataPointer, uint rowPitch, int x, int y)
    {
        var row = IntPtr.Add(dataPointer, checked(y * (int)rowPitch));
        var pixel = IntPtr.Add(row, checked(x * 8));
        var r = ReadHalf(pixel, 0);
        var g = ReadHalf(pixel, 2);
        var b = ReadHalf(pixel, 4);
        return Math.Max(0.0f, (0.2126f * r) + (0.7152f * g) + (0.0722f * b));
    }

    private static string BuildMissingResourceList(params (string Name, bool Missing)[] resources)
    {
        var missing = resources
            .Where(resource => resource.Missing)
            .Select(resource => resource.Name);
        return string.Join(", ", missing);
    }

    private static string DescribeWicPixelFormat(Guid pixelFormat)
    {
        if (pixelFormat == WicPixelFormat.Format64bppPRGBA)
        {
            return "64bpp PRGBA";
        }

        if (pixelFormat == WicPixelFormat.Format64bppRGBA)
        {
            return "64bpp RGBA";
        }

        if (pixelFormat == WicPixelFormat.Format64bppPRGBAHalf)
        {
            return "64bpp PRGBA half";
        }

        if (pixelFormat == WicPixelFormat.Format64bppRGBAHalf)
        {
            return "64bpp RGBA half";
        }

        if (pixelFormat == WicPixelFormat.Format32bppPRGBA)
        {
            return "32bpp PRGBA";
        }

        if (pixelFormat == WicPixelFormat.Format32bppRGBA)
        {
            return "32bpp RGBA";
        }

        if (pixelFormat == WicPixelFormat.Format32bppR10G10B10A2HDR10)
        {
            return "32bpp R10G10B10A2 HDR10";
        }

        return pixelFormat.ToString("D");
    }

    private static bool IsFloatingPointWicPixelFormat(Guid pixelFormat)
    {
        return pixelFormat == WicPixelFormat.Format64bppRGBAHalf
            || pixelFormat == WicPixelFormat.Format64bppPRGBAHalf
            || pixelFormat == WicPixelFormat.Format64bppRGBHalf
            || pixelFormat == WicPixelFormat.Format48bppRGBHalf
            || pixelFormat == WicPixelFormat.Format96bppRGBFloat
            || pixelFormat == WicPixelFormat.Format128bppRGBAFloat
            || pixelFormat == WicPixelFormat.Format128bppPRGBAFloat
            || pixelFormat == WicPixelFormat.Format128bppRGBFloat;
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
        _primaryAnalysisSource = null;
        _gainMapAnalysisSource = null;
        _d2dFallbackStatus = null;
        _gainMapConstants = default;
        _toneMapAnalysis = default;
        _toneMappingEnabledForCurrentFrame = false;
        _contentPixelWidth = 0;
        _contentPixelHeight = 0;
        _contentOrientation = 1.0f;
        _loadedGainMapPath = null;
        _loadedGainMapWriteTimeUtc = null;
        _loadedGainMapMode = false;
    }

    private bool TryBindSwapChainToPanel()
    {
        if (_panel is null || _swapChain is null)
        {
            _panelBindingStatus = "WinUI swap chain bind skipped";
            LastRenderStatus = "Swap chain panel bind skipped: panel or swap chain missing";
            return false;
        }

        try
        {
            SetSwapChainOnPanel(_panel, _swapChain.NativePointer);
            _panelBindingStatus = "WinUI swap chain bound";
            IsSwapChainPanelBound = true;
            return true;
        }
        catch (Exception ex)
        {
            _panelBindingStatus = $"WinUI swap chain bind failed: {ex.GetType().Name}";
            IsSwapChainPanelBound = false;
            LastRenderStatus = $"Swap chain panel bind failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void DetachSwapChainFromPanel()
    {
        if (_panel is null)
        {
            return;
        }

        try
        {
            SetSwapChainOnPanel(_panel, IntPtr.Zero);
            IsSwapChainPanelBound = false;
        }
        catch
        {
            // Best-effort cleanup; the panel may already be leaving the XAML tree.
        }
    }

    private static void SetSwapChainOnPanel(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = Marshal.GetIUnknownForObject(panel);
        var panelNative = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in WinUiSwapChainPanelNativeGuid, out panelNative));
            var vtable = Marshal.ReadIntPtr(panelNative);
            var setSwapChainPointer = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);
            var setSwapChain = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(setSwapChainPointer);
            Marshal.ThrowExceptionForHR(setSwapChain(panelNative, swapChain));
        }
        finally
        {
            if (panelNative != IntPtr.Zero)
            {
                Marshal.Release(panelNative);
            }

            Marshal.Release(unknown);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr panelNative, IntPtr swapChain);

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
}
