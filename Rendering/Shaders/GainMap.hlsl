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
    float4 ViewerTools;
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

float2 ComputeRawImageUv(float2 panelUv)
{
    float2 rawFittedUv = (panelUv - ImageLayout.zw) / max(ImageLayout.xy, 0.0001f);
    return ApplyOrientation(rawFittedUv);
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

float4 ComputeCatmullRomWeights(float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return float4(
        (-0.5f * t) + t2 - (0.5f * t3),
        1.0f - (2.5f * t2) + (1.5f * t3),
        (0.5f * t) + (2.0f * t2) - (1.5f * t3),
        (-0.5f * t2) + (0.5f * t3));
}

float SanitizeFinite1(float v, float fallbackValue)
{
    uint bits = asuint(v);
    if ((bits & 0x7F800000u) == 0x7F800000u)
    {
        return fallbackValue;
    }

    return clamp(v, -65504.0f, 65504.0f);
}

float3 SanitizeFinite3(float3 v)
{
    return float3(
        SanitizeFinite1(v.r, 0.0f),
        SanitizeFinite1(v.g, 0.0f),
        SanitizeFinite1(v.b, 0.0f));
}

float4 SanitizeFinite4(float4 v)
{
    return float4(
        SanitizeFinite3(v.rgb),
        saturate(SanitizeFinite1(v.a, 1.0f)));
}

float4 SampleTextureFiltered(Texture2D tex, float2 uv, float2 duvDx, float2 duvDy, bool allowPixelGrid)
{
    uint texW = 1;
    uint texH = 1;
    tex.GetDimensions(texW, texH);
    float2 texSize = max(float2((float)texW, (float)texH), 1.0f);
    float2 dxTexels = duvDx * texSize;
    float2 dyTexels = duvDy * texSize;
    float texelsPerPixel = max(length(dxTexels), length(dyTexels));

    float4 trilinear = SanitizeFinite4(tex.SampleGrad(LinearClampSampler, uv, duvDx, duvDy));
    float4 result = trilinear;
    if (texelsPerPixel < 1.0f)
    {
        int2 maxTexel = int2((int)texW - 1, (int)texH - 1);
        float2 pos = (uv * texSize) - 0.5f;
        float2 basePos = floor(pos);
        float2 f = pos - basePos;
        int2 baseTexel = int2(basePos);

        float4 wx = ComputeCatmullRomWeights(f.x);
        float4 wy = ComputeCatmullRomWeights(f.y);
        float wxArr[4] = { wx.x, wx.y, wx.z, wx.w };
        float wyArr[4] = { wy.x, wy.y, wy.z, wy.w };

        float4 catmull = 0.0f;
        float4 c00 = 0.0f;
        float4 c10 = 0.0f;
        float4 c01 = 0.0f;
        float4 c11 = 0.0f;

        [unroll]
        for (int jy = 0; jy < 4; jy++)
        {
            int iy = clamp(baseTexel.y + jy - 1, 0, maxTexel.y);
            float4 rowSum = 0.0f;

            [unroll]
            for (int jx = 0; jx < 4; jx++)
            {
                int ix = clamp(baseTexel.x + jx - 1, 0, maxTexel.x);
                float4 tap = SanitizeFinite4(tex.Load(int3(ix, iy, 0)));
                rowSum += tap * wxArr[jx];

                if (jy == 1 && jx == 1) c00 = tap;
                if (jy == 1 && jx == 2) c10 = tap;
                if (jy == 2 && jx == 1) c01 = tap;
                if (jy == 2 && jx == 2) c11 = tap;
            }

            catmull += rowSum * wyArr[jy];
        }

        float4 min2x2 = min(min(c00, c10), min(c01, c11));
        float4 max2x2 = max(max(c00, c10), max(c01, c11));
        catmull = clamp(catmull, min2x2, max2x2);

        float4 magnified = catmull;
        if (allowPixelGrid)
        {
            float2 fSharp = saturate(((f - 0.5f) / max(texelsPerPixel, 0.0001f)) + 0.5f);
            float4 pixelGrid = lerp(lerp(c00, c10, fSharp.x), lerp(c01, c11, fSharp.x), fSharp.y);
            float gridBlend = smoothstep(0.20f, 0.09f, texelsPerPixel);
            magnified = lerp(catmull, pixelGrid, gridBlend);
        }

        float magBlend = smoothstep(1.0f, 0.75f, texelsPerPixel);
        result = lerp(trilinear, magnified, magBlend);
    }

    return result;
}

float3 SrgbToLinear(float3 value)
{
    float3 low = value / 12.92f;
    float3 high = pow(max((value + 0.055f) / 1.055f, 0.0f), 2.4f);
    return lerp(high, low, value <= 0.04045f);
}

float3 Rec709ToLinear(float3 value)
{
    float3 low = value / 4.5f;
    float3 high = pow(max((value + 0.099f) / 1.099f, 0.0f), 1.0f / 0.45f);
    return lerp(high, low, value < 0.081f);
}

float3 AdobeRgbToLinear(float3 value)
{
    return pow(max(value, 0.0f), 563.0f / 256.0f);
}

float3 RommRgbToLinear(float3 value)
{
    float3 clamped = max(value, 0.0f);
    float3 low = clamped / 16.0f;
    float3 high = pow(clamped, 1.8f);
    return lerp(high, low, value < 0.03125f);
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

float3 AdobeRgbToBt709(float3 value)
{
    return float3(
        (1.398356f * value.r) - (0.398356f * value.g),
        value.g,
        (-0.042929f * value.g) + (1.042929f * value.b));
}

float3 Bt709ToBt2020(float3 value)
{
    return float3(
        (0.6274040f * value.r) + (0.3292820f * value.g) + (0.0433136f * value.b),
        (0.0690970f * value.r) + (0.9195400f * value.g) + (0.0113612f * value.b),
        (0.0163916f * value.r) + (0.0880132f * value.g) + (0.8955951f * value.b));
}

float3 Bt709ToP3(float3 value)
{
    return float3(
        (0.822462f * value.r) + (0.177538f * value.g),
        (0.033194f * value.r) + (0.966806f * value.g),
        (0.017083f * value.r) + (0.072398f * value.g) + (0.910519f * value.b));
}

float3 Bt709ToProPhoto(float3 value)
{
    return float3(
        (0.529280f * value.r) + (0.330153f * value.g) + (0.140567f * value.b),
        (0.098366f * value.r) + (0.873464f * value.g) + (0.028170f * value.b),
        (0.016875f * value.r) + (0.117659f * value.g) + (0.865466f * value.b));
}

float3 Bt709ToAdobeRgb(float3 value)
{
    return float3(
        (0.715126f * value.r) + (0.284874f * value.g),
        value.g,
        (0.041162f * value.g) + (0.958838f * value.b));
}

float3 ConvertGamutToBt709Unclamped(float3 value, float gamut)
{
    if (gamut > 3.5f) return AdobeRgbToBt709(value);
    if (gamut > 2.5f) return ProPhotoToBt709(value);
    if (gamut > 1.5f) return Bt2020ToBt709(value);
    if (gamut > 0.5f) return P3ToBt709(value);
    return value;
}

float3 ConvertBt709ToGamut(float3 value, float gamut)
{
    if (gamut > 3.5f) return Bt709ToAdobeRgb(value);
    if (gamut > 2.5f) return Bt709ToProPhoto(value);
    if (gamut > 1.5f) return Bt709ToBt2020(value);
    if (gamut > 0.5f) return Bt709ToP3(value);
    return value;
}

float3 ConvertGainMapBaseToBt709(float3 value)
{
    float3 converted = ConvertGamutToBt709Unclamped(value, GainMapControl.z);
    return ViewModeParams.w > 0.5f ? max(converted, 0.0f) : converted;
}

float3 GetHlgLumaWeights(float gamut)
{
    if (gamut > 0.5f && gamut < 1.5f)
    {
        return float3(0.228975f, 0.691739f, 0.079287f);
    }

    if (gamut <= 0.5f)
    {
        return float3(0.2126f, 0.7152f, 0.0722f);
    }

    return float3(0.2627f, 0.6780f, 0.0593f);
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
    float result = max(DisplayMapping.x * exp2(max(DisplayMapping.z, 0.0f)), DisplayMapping.x);
    if (DisplayMapping.y > 0.0f)
    {
        result = max(DisplayMapping.y, DisplayMapping.x);
    }

    if (SourceEncoding.x > 1.5f && SourceEncoding.x < 2.5f)
    {
        if (ViewModeParams.x < 0.5f)
        {
            result = 1000.0f / 80.0f;
        }
        else if (DisplayMapping.y <= 0.0f && DisplayMapping.z <= 0.0f)
        {
            result = max(1000.0f / 80.0f, DisplayMapping.x);
        }
    }

    return result;
}

float CalculateHlgSystemGamma(float targetScenePeak)
{
    float targetNits = max(targetScenePeak * 80.0f, 100.0f);
    return clamp(1.2f + (0.42f * log10(targetNits / 1000.0f)), 1.0f, 1.35f);
}

float3 ClampToDisplayPeak(float3 value)
{
    float peak = DisplayMapping.y;
    if (peak > 0.0f)
    {
        float maxC = max(value.r, max(value.g, value.b));
        if (maxC > peak)
        {
            float3 ratioClamped = value * (peak / max(maxC, 0.0001f));
            float minC = max(min(value.r, min(value.g, value.b)), 0.0f);
            float whiteBlend = smoothstep(peak, peak * 3.0f, minC);
            value = lerp(ratioClamped, float3(peak, peak, peak), whiteBlend);
        }
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
    float result = max(DisplayMapping.x, 1.0f);
    if (IsHlgTransfer())
    {
        if (ViewModeParams.x < 0.5f)
        {
            result = max((203.0f / 80.0f) * exposure, 0.0001f);
        }
        else
        {
            float targetPeak = CalculateHdrTargetScenePeak();
            float hlgDiffuseWhite = pow(0.264977f, CalculateHlgSystemGamma(targetPeak)) * targetPeak;
            result = max(hlgDiffuseWhite * exposure, 0.0001f);
        }
    }
    else if (IsPqTransfer() || IsLinearSceneScRgbTransfer())
    {
        result = max((203.0f / 80.0f) * exposure, 0.0001f);
    }
    else if (IsLinearScRgbTransfer())
    {
        float displayScale = SourceEncoding.y <= 0.5f ? max(DisplayMapping.x, 1.0f) : 1.0f;
        result = max(displayScale * exposure, 0.0001f);
    }

    return result;
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

    float midGamma = lerp(1.02f, 1.22f, pressure);
    float mappedLuminance = luminance;
    if (luminance <= whiteScale)
    {
        mappedLuminance = whiteScale * pow(saturate(luminance / max(whiteScale, 0.0001f)), midGamma);
    }
    else
    {
        float targetRange = target - whiteScale;
        if (targetRange <= 0.0001f)
        {
            mappedLuminance = whiteScale;
        }
        else
        {
            float sourceRange = max((tonePeak * midScale) - whiteScale, 0.0001f);
            float excess = max(luminance - whiteScale, 0.0f);
            float compressionRatio = (midGamma * sourceRange) / targetRange;
            float shoulderPower = lerp(2.4f, 1.15f, saturate((compressionRatio - 1.5f) / 10.0f));
            float baseTerm = max(1.0f + ((midGamma * excess) / max(shoulderPower * targetRange, 0.0001f)), 1.0f);
            float mappedExcess = targetRange * (1.0f - pow(baseTerm, -shoulderPower));
            mappedLuminance = min(whiteScale + mappedExcess, target);
        }
    }

    return ClampToDisplayPeak(workingValue * (mappedLuminance / max(luminance, 0.0001f)));
}

float3 ApplySingleLayerToneMap(float3 value)
{
    float3 mapped = ApplyAdaptiveToneMapWithWhiteScale(
        value,
        GetSingleLayerToneMapWhiteScale());
    if (ToneMapOutput.y > 1.5f)
    {
        mapped = ApplySingleLayerDisplayFitToneMap(value);
    }

    return mapped;
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
    float3 sceneLinear = 0.0f;
    bool isLinearSceneScRgb = transfer > 4.5f;
    bool isLinearScRgb = transfer > 3.5f && transfer < 4.5f;
    if (Orientation.y > 0.5f)
    {
        if (transfer > 1.5f && transfer < 2.5f)
        {
            float targetPeak = CalculateHdrTargetScenePeak();
            float3 hlgScene = max(encoded, 0.0f);
            float hlgLuma = dot(hlgScene, GetHlgLumaWeights(SourceEncoding.y));
            sceneLinear = hlgScene * pow(max(hlgLuma, 0.000001f), CalculateHlgSystemGamma(targetPeak) - 1.0f) * targetPeak;
        }
        else
        {
            sceneLinear = encoded;
        }
    }
    else if (isLinearSceneScRgb || isLinearScRgb)
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
        float hlgLuma = dot(hlgScene, GetHlgLumaWeights(SourceEncoding.y));
        sceneLinear = hlgScene * pow(max(hlgLuma, 0.000001f), CalculateHlgSystemGamma(targetPeak) - 1.0f) * targetPeak;
    }
    else if (transfer > 0.5f && transfer < 1.5f)
    {
        sceneLinear = Rec709ToLinear(encoded);
    }
    else if (SourceEncoding.y > 3.5f)
    {
        sceneLinear = AdobeRgbToLinear(encoded);
    }
    else if (SourceEncoding.y > 2.5f)
    {
        sceneLinear = RommRgbToLinear(encoded);
    }
    else
    {
        sceneLinear = SrgbToLinear(encoded);
    }

    if (SourceEncoding.y > 3.5f)
    {
        sceneLinear = AdobeRgbToBt709(sceneLinear);
        if (ViewModeParams.w > 0.5f)
        {
            sceneLinear = max(sceneLinear, 0.0f);
        }
    }
    else if (SourceEncoding.y > 2.5f)
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
    float3 result = encoded;
    if (Orientation.y <= 0.5f)
    {
        if (GainMapControl.z > 3.5f)
        {
            result = AdobeRgbToLinear(encoded);
        }
        else if (GainMapControl.z > 2.5f)
        {
            result = RommRgbToLinear(encoded);
        }
        else if (SourceEncoding.x > 0.5f && SourceEncoding.x < 1.5f)
        {
            result = Rec709ToLinear(encoded);
        }
        else
        {
            result = SrgbToLinear(encoded);
        }
    }

    return result;
}

float CalculateGainMapWeight()
{
    if (abs(HdrCapacity.y - HdrCapacity.x) <= 0.0001f)
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

float InterleavedGradientNoise(float2 pixelPos)
{
    float3 magic = float3(0.06711056f, 0.00583715f, 52.9829189f);
    return frac(magic.z * frac(dot(floor(pixelPos), magic.xy)));
}

float3 CompositeOverCheckerboard(float3 mapped, float alpha, float2 pixelPos)
{
    alpha = saturate(alpha);
    if (alpha >= 0.999f)
    {
        return mapped;
    }

    if (ViewerTools.z > 0.5f)
    {
        return mapped * alpha;
    }

    float2 cell = floor(pixelPos / 12.0f);
    float parity = fmod(abs(cell.x + cell.y), 2.0f);
    float checkerLinear = parity < 0.5f ? 0.014f : 0.026f;
    float3 background = checkerLinear * max(DisplayMapping.x, 1.0f);
    return lerp(background, mapped, alpha);
}

float3 ApplyOutputDither(float3 color, float2 pixelPos)
{
    if (ViewerTools.z > 0.5f)
    {
        return color;
    }

    float ign = InterleavedGradientNoise(pixelPos);
    float amplitude = (0.5f / 1023.0f) * max(DisplayMapping.x, 1.0f);
    float dither = (ign - 0.5f) * 2.0f * amplitude;
    float luma = dot(max(color, 0.0f), float3(0.2126f, 0.7152f, 0.0722f));
    float fadeNearBlack = saturate(luma * 1023.0f);
    return color + (dither * fadeNearBlack);
}

float4 ApplyViewerTools(float3 mapped, float alpha, float2 panelUv, float2 pixelPos)
{
    clip(panelUv.x - ViewerTools.x);
    clip(ViewerTools.y - panelUv.x);
    float3 safeMapped = SanitizeFinite3(mapped);
    float3 composited = CompositeOverCheckerboard(safeMapped, alpha, pixelPos);
    float3 dithered = SanitizeFinite3(ApplyOutputDither(composited, pixelPos));
    return float4(dithered, 1.0f);
}

float3 UnpremultiplyIfNeeded(float3 rgb, float alpha)
{
    if (Orientation.z > 0.5f && alpha < 0.9999f)
    {
        return alpha > 0.00001f ? (rgb / alpha) : 0.0f;
    }

    return rgb;
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    float2 rawUv = ComputeRawImageUv(input.TexCoord);
    float2 duvDx = ddx(rawUv);
    float2 duvDy = ddy(rawUv);
    float4 fit = FitToImage(input.TexCoord);
    clip(fit.z - 0.5f);
    clip(input.TexCoord.x - ViewerTools.x);
    clip(ViewerTools.y - input.TexCoord.x);
    float2 uv = ApplyOrientation(fit.xy);

    bool showGainMap = ViewModeParams.x > 2.5f && ViewModeParams.x < 3.5f;
    float4 baseSample = SampleTextureFiltered(PrimaryTexture, uv, duvDx, duvDy, true);
    float3 unpremulRgb = UnpremultiplyIfNeeded(baseSample.rgb, baseSample.a);
    float3 sdr = DecodeGainMapBaseSample(unpremulRgb);
    float3 recovery = saturate(SampleTextureFiltered(GainMapTexture, uv, duvDx, duvDy, showGainMap).rgb);

    if (ViewModeParams.x < 0.5f)
    {
        return ApplyViewerTools(ApplySdrDisplayAdjustment(ConvertGainMapBaseToBt709(sdr)), baseSample.a, input.TexCoord, input.Position.xy);
    }

    if (showGainMap)
    {
        float3 gainPreview = GainMapControl.y > 0.5f
            ? Rec709ToLinear(recovery)
            : pow(max(recovery, 0.0f), 1.0f / max(Gamma.rgb, 0.0001f));
        return ApplyViewerTools(ApplySdrDisplayAdjustment(gainPreview), 1.0f, input.TexCoord, input.Position.xy);
    }

    float3 hdrBt709 = 0.0f;
    if (GainMapControl.y > 0.5f)
    {
        float3 gain = saturate(Rec709ToLinear(recovery));
        float headroom = max(GainMapMax.x, 1.0f);
        float effectiveHeadroom = pow(headroom, CalculateGainMapWeight());
        float3 hdrBase = sdr * (1.0f + ((effectiveHeadroom - 1.0f) * gain));
        hdrBt709 = ConvertGainMapBaseToBt709(hdrBase);
    }
    else
    {
        float3 logRecovery = pow(max(recovery, 0.0f), 1.0f / max(Gamma.rgb, 0.0001f));
        float3 logBoost = lerp(GainMapMin.rgb, GainMapMax.rgb, logRecovery);
        float weight = CalculateGainMapWeight();
        if (SourceEncoding.z > 0.5f)
        {
            float3 sdr709 = ConvertGamutToBt709Unclamped(sdr, GainMapControl.z);
            float3 sdrAlt = ConvertBt709ToGamut(sdr709, SourceEncoding.w);
            float3 hdrAlt = ((sdrAlt + OffsetSdr.rgb) * exp2(logBoost * weight)) - OffsetHdr.rgb;
            hdrBt709 = ConvertGamutToBt709Unclamped(hdrAlt, SourceEncoding.w);
            if (ViewModeParams.w > 0.5f)
            {
                hdrBt709 = max(hdrBt709, 0.0f);
            }
        }
        else
        {
            float3 hdrBase = max(((sdr + OffsetSdr.rgb) * exp2(logBoost * weight)) - OffsetHdr.rgb, 0.0f);
            hdrBt709 = ConvertGainMapBaseToBt709(hdrBase);
        }
    }

    return ApplyViewerTools(ApplyGainMapOutputMapping(hdrBt709), baseSample.a, input.TexCoord, input.Position.xy);
}

float4 BaseImagePSMain(VertexOutput input) : SV_TARGET
{
    float2 rawUv = ComputeRawImageUv(input.TexCoord);
    float2 duvDx = ddx(rawUv);
    float2 duvDy = ddy(rawUv);
    float4 fit = FitToImage(input.TexCoord);
    clip(fit.z - 0.5f);
    clip(input.TexCoord.x - ViewerTools.x);
    clip(ViewerTools.y - input.TexCoord.x);
    float2 uv = ApplyOrientation(fit.xy);

    float4 baseSample = SampleTextureFiltered(PrimaryTexture, uv, duvDx, duvDy, true);
    float3 unpremulRgb = UnpremultiplyIfNeeded(baseSample.rgb, baseSample.a);
    float3 mapped = DecodeBaseImageSample(unpremulRgb);
    return ApplyViewerTools(mapped, baseSample.a, input.TexCoord, input.Position.xy);
}

float4 LinearizeSourcePSMain(VertexOutput input) : SV_TARGET
{
    int2 texel = int2(input.Position.xy);
    float4 encodedSample = SanitizeFinite4(PrimaryTexture.Load(int3(texel, 0)));
    float3 encoded = encodedSample.rgb;
    float transfer = SourceEncoding.x;
    float gamut = SourceEncoding.y;
    float3 linearRgb = encoded;
    if (transfer > 3.5f)
    {
        linearRgb = encoded;
    }
    else if (transfer > 2.5f)
    {
        linearRgb = PqToSceneLinear(encoded);
    }
    else if (transfer > 1.5f)
    {
        linearRgb = max(HlgToSceneLinear(encoded), 0.0f);
    }
    else if (transfer > 0.5f)
    {
        linearRgb = Rec709ToLinear(encoded);
    }
    else if (gamut > 3.5f)
    {
        linearRgb = AdobeRgbToLinear(encoded);
    }
    else if (gamut > 2.5f)
    {
        linearRgb = RommRgbToLinear(encoded);
    }
    else
    {
        linearRgb = SrgbToLinear(encoded);
    }

    linearRgb = SanitizeFinite3(linearRgb);
    float alpha = saturate(encodedSample.a);
    if (Orientation.z > 0.5f)
    {
        linearRgb *= alpha;
    }

    return float4(linearRgb, alpha);
}

cbuffer LuminanceHeatmapConstants : register(b1)
{
    float4 HeatmapLayout;
    float4 HeatmapBands[8]; // linear scRGB palette, with minimum nits in w
};

float4 HeatmapPSMain(VertexOutput input) : SV_TARGET
{
    float2 uv = (input.TexCoord - HeatmapLayout.zw) / max(HeatmapLayout.xy, 0.0001f);
    clip(uv);
    clip(1.0f - uv);

    // The analysis pass writes RGBA16F, exactly like the mouse probe.
    float3 clean = SanitizeFinite3(PrimaryTexture.Load(int3(int2(input.Position.xy), 0)).rgb);
    float nits = max(0.0f, 80.0f * dot(clean, float3(0.2126f, 0.7152f, 0.0722f)));
    float3 color = HeatmapBands[0].rgb;
    [unroll] for (int i = 1; i < 8; ++i)
    {
        if (nits >= HeatmapBands[i].w)
        {
            color = HeatmapBands[i].rgb;
        }
    }
    return float4(color, 1.0f);
}
