using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;

namespace HdrImageViewer.Rendering;

internal static class HdrColorMath
{
    public const float ReferenceWhiteNits = 80.0f;
    public const float UltraHdrReferenceWhiteNits = 203.0f;
    public const float GainMapBaseTransferSrgb = 0.0f;
    public const float GainMapBaseTransferBt709 = 1.0f;

    public static float ToShaderGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => 1.0f,
            GainMapColorGamut.Bt2100 => 2.0f,
            GainMapColorGamut.ProPhoto => 3.0f,
            GainMapColorGamut.AdobeRgb => 4.0f,
            _ => 0.0f,
        };
    }

    public static Vector3 SrgbToLinear(Vector3 value)
    {
        return new Vector3(
            SrgbToLinear(value.X),
            SrgbToLinear(value.Y),
            SrgbToLinear(value.Z));
    }

    public static float SrgbToLinear(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            return 0.0f;
        }

        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    public static float AdobeRgbToLinear(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            return 0.0f;
        }

        return MathF.Pow(value, 563.0f / 256.0f);
    }

    public static Vector3 AdobeRgbToLinear(Vector3 value)
    {
        return new Vector3(
            AdobeRgbToLinear(value.X),
            AdobeRgbToLinear(value.Y),
            AdobeRgbToLinear(value.Z));
    }

    public static float RommRgbToLinear(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            return 0.0f;
        }

        return value < 0.03125f
            ? value / 16.0f
            : MathF.Pow(value, 1.8f);
    }

    public static Vector3 RommRgbToLinear(Vector3 value)
    {
        return new Vector3(
            RommRgbToLinear(value.X),
            RommRgbToLinear(value.Y),
            RommRgbToLinear(value.Z));
    }

    public static Vector3 DecodeSdrEncodedToLinear(Vector3 encoded, GainMapColorGamut gamut, float sdrTransfer = GainMapBaseTransferSrgb)
    {
        if (gamut == GainMapColorGamut.AdobeRgb)
        {
            return AdobeRgbToLinear(encoded);
        }

        if (gamut == GainMapColorGamut.ProPhoto)
        {
            return RommRgbToLinear(encoded);
        }

        return sdrTransfer is > 0.5f and < 1.5f
            ? Rec709ToLinear(encoded)
            : SrgbToLinear(encoded);
    }

    public static float Rec709ToLinear(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            return 0.0f;
        }

        return value < 0.081f
            ? value / 4.5f
            : MathF.Pow((value + 0.099f) / 1.099f, 1.0f / 0.45f);
    }

    public static Vector3 Rec709ToLinear(Vector3 value)
    {
        return new Vector3(
            Rec709ToLinear(value.X),
            Rec709ToLinear(value.Y),
            Rec709ToLinear(value.Z));
    }

    public static Vector3 DecodeGainMapBaseToLinear(Vector3 encoded, GainMapShaderConstants constants)
    {
        if (constants.GainMapControl.Z > 3.5f)
        {
            return AdobeRgbToLinear(encoded);
        }

        if (constants.GainMapControl.Z > 2.5f)
        {
            return RommRgbToLinear(encoded);
        }

        return constants.SourceEncoding.X is > 0.5f and < 1.5f
            ? Rec709ToLinear(encoded)
            : SrgbToLinear(encoded);
    }

    public static Vector3 PqToSceneLinear(Vector3 encoded, float referenceWhiteNits = ReferenceWhiteNits)
    {
        return new Vector3(
            PqToSceneLinearChannel(encoded.X, referenceWhiteNits),
            PqToSceneLinearChannel(encoded.Y, referenceWhiteNits),
            PqToSceneLinearChannel(encoded.Z, referenceWhiteNits));
    }

    public static Vector3 HlgToSceneLinear(
        Vector3 encoded,
        float targetScenePeak,
        float referenceWhiteNits = ReferenceWhiteNits,
        GainMapColorGamut gamut = GainMapColorGamut.Bt2100)
    {
        var gamma = CalculateHlgSystemGamma(targetScenePeak, referenceWhiteNits);
        return HlgToSceneLinearWithGamma(encoded, targetScenePeak, gamma, gamut);
    }

    public static Vector3 HlgToSceneLinearWithGamma(
        Vector3 encoded,
        float targetScenePeak,
        float systemGamma,
        GainMapColorGamut gamut = GainMapColorGamut.Bt2100)
    {
        var r = float.IsFinite(encoded.X) ? Math.Clamp(encoded.X, 0.0f, 1.0f) : 0.0f;
        var g = float.IsFinite(encoded.Y) ? Math.Clamp(encoded.Y, 0.0f, 1.0f) : 0.0f;
        var b = float.IsFinite(encoded.Z) ? Math.Clamp(encoded.Z, 0.0f, 1.0f) : 0.0f;
        var hlgScene = new Vector3(
            HlgToSceneLinearChannel(r),
            HlgToSceneLinearChannel(g),
            HlgToSceneLinearChannel(b));
        var lumaWeights = gamut switch
        {
            GainMapColorGamut.DisplayP3 => new Vector3(0.228975f, 0.691739f, 0.079287f),
            GainMapColorGamut.Bt709 or GainMapColorGamut.Unknown => new Vector3(0.2126f, 0.7152f, 0.0722f),
            _ => new Vector3(0.2627f, 0.6780f, 0.0593f),
        };
        var hlgLuma = Math.Max(Vector3.Dot(hlgScene, lumaWeights), 0.000001f);
        return hlgScene * MathF.Pow(hlgLuma, systemGamma - 1.0f) * targetScenePeak;
    }

    public static float CalculateHlgSystemGamma(
        float targetScenePeak,
        float referenceWhiteNits = ReferenceWhiteNits)
    {
        var targetNits = Math.Max(targetScenePeak * referenceWhiteNits, 100.0f);
        return Math.Clamp(1.2f + (0.42f * MathF.Log10(targetNits / 1000.0f)), 1.0f, 1.35f);
    }

    public static Vector3 Bt2020ToBt709(Vector3 value)
    {
        return new Vector3(
            (1.660491f * value.X) - (0.587641f * value.Y) - (0.072850f * value.Z),
            (-0.124550f * value.X) + (1.132900f * value.Y) - (0.008349f * value.Z),
            (-0.018151f * value.X) - (0.100579f * value.Y) + (1.118730f * value.Z));
    }

    public static Vector3 Bt709ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.6274040f * value.X) + (0.3292820f * value.Y) + (0.0433136f * value.Z),
            (0.0690970f * value.X) + (0.9195400f * value.Y) + (0.0113612f * value.Z),
            (0.0163916f * value.X) + (0.0880132f * value.Y) + (0.8955951f * value.Z));
    }

    public static Vector3 P3ToBt709(Vector3 value)
    {
        return new Vector3(
            (1.224940f * value.X) - (0.224940f * value.Y),
            (-0.042057f * value.X) + (1.042057f * value.Y),
            (-0.019638f * value.X) - (0.078636f * value.Y) + (1.098274f * value.Z));
    }

    public static Vector3 Bt709ToP3(Vector3 value)
    {
        return new Vector3(
            (0.822462f * value.X) + (0.177538f * value.Y),
            (0.033194f * value.X) + (0.966806f * value.Y),
            (0.017083f * value.X) + (0.072398f * value.Y) + (0.910519f * value.Z));
    }

    public static Vector3 ProPhotoToBt709(Vector3 value)
    {
        return new Vector3(
            (2.034368f * value.X) - (0.727634f * value.Y) - (0.306733f * value.Z),
            (-0.228827f * value.X) + (1.231753f * value.Y) - (0.002927f * value.Z),
            (-0.008558f * value.X) - (0.153268f * value.Y) + (1.161827f * value.Z));
    }

    public static Vector3 Bt709ToProPhoto(Vector3 value)
    {
        return new Vector3(
            (0.529280f * value.X) + (0.330153f * value.Y) + (0.140567f * value.Z),
            (0.098366f * value.X) + (0.873464f * value.Y) + (0.028170f * value.Z),
            (0.016875f * value.X) + (0.117659f * value.Y) + (0.865466f * value.Z));
    }

    public static Vector3 AdobeRgbToBt709(Vector3 value)
    {
        return new Vector3(
            (1.398356f * value.X) - (0.398356f * value.Y),
            value.Y,
            (-0.042929f * value.Y) + (1.042929f * value.Z));
    }

    public static Vector3 Bt709ToAdobeRgb(Vector3 value)
    {
        return new Vector3(
            (0.715126f * value.X) + (0.284874f * value.Y),
            value.Y,
            (0.041162f * value.Y) + (0.958838f * value.Z));
    }

    public static Vector3 AdobeRgbToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.877334f * value.X) + (0.077494f * value.Y) + (0.045172f * value.Z),
            (0.096623f * value.X) + (0.891527f * value.Y) + (0.011850f * value.Z),
            (0.022921f * value.X) + (0.043037f * value.Y) + (0.934042f * value.Z));
    }

    public static Vector3 ProPhotoToBt2020(Vector3 value)
    {
        return Bt709ToBt2020(ProPhotoToBt709(value));
    }

    public static Vector3 P3ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.753833f * value.X) + (0.198597f * value.Y) + (0.047570f * value.Z),
            (0.045744f * value.X) + (0.941777f * value.Y) + (0.012479f * value.Z),
            (-0.001210f * value.X) + (0.017601f * value.Y) + (0.983608f * value.Z));
    }

    public static Vector3 ConvertLinearToBt709(
        Vector3 value,
        GainMapColorGamut gamut,
        ColorGamutMappingMode mode = ColorGamutMappingMode.Managed)
    {
        var converted = gamut switch
        {
            GainMapColorGamut.AdobeRgb => AdobeRgbToBt709(value),
            GainMapColorGamut.ProPhoto => ProPhotoToBt709(value),
            GainMapColorGamut.Bt2100 => Bt2020ToBt709(value),
            GainMapColorGamut.DisplayP3 => P3ToBt709(value),
            _ => value,
        };

        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertLinearToBt2020(Vector3 value, GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.Bt2100 => value,
            GainMapColorGamut.DisplayP3 => P3ToBt2020(value),
            GainMapColorGamut.AdobeRgb => AdobeRgbToBt2020(value),
            GainMapColorGamut.ProPhoto => ProPhotoToBt2020(value),
            _ => Bt709ToBt2020(value),
        };
    }

    public static Vector3 ConvertShaderGamutToBt709(
        Vector3 value,
        float shaderGamut,
        ColorGamutMappingMode mode = ColorGamutMappingMode.Managed)
    {
        var converted = shaderGamut switch
        {
            > 3.5f => AdobeRgbToBt709(value),
            > 2.5f => ProPhotoToBt709(value),
            > 1.5f => Bt2020ToBt709(value),
            > 0.5f => P3ToBt709(value),
            _ => value,
        };

        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertBt709ToShaderGamut(Vector3 value, float shaderGamut)
    {
        return shaderGamut switch
        {
            > 3.5f => Bt709ToAdobeRgb(value),
            > 2.5f => Bt709ToProPhoto(value),
            > 1.5f => Bt709ToBt2020(value),
            > 0.5f => Bt709ToP3(value),
            _ => value,
        };
    }

    public static Vector3 ConvertShaderGamutToBt2020(Vector3 value, float shaderGamut)
    {
        return shaderGamut switch
        {
            > 3.5f => AdobeRgbToBt2020(value),
            > 2.5f => ProPhotoToBt2020(value),
            > 1.5f => value,
            > 0.5f => P3ToBt2020(value),
            _ => Bt709ToBt2020(value),
        };
    }

    public static Vector3 ConvertGainMapBaseToBt709(Vector3 value, GainMapShaderConstants constants)
    {
        return ConvertShaderGamutToBt709(value, constants.GainMapControl.Z);
    }

    public static Vector3 ConvertGainMapBaseToBt709(
        Vector3 value,
        GainMapShaderConstants constants,
        ColorGamutMappingMode mode)
    {
        return ConvertShaderGamutToBt709(value, constants.GainMapControl.Z, mode);
    }

    public static Vector3 ReconstructGainMapHdrToBt709(
        Vector3 sdrBaseLinear,
        Vector3 gain,
        GainMapShaderConstants constants,
        float weight,
        ColorGamutMappingMode mode = ColorGamutMappingMode.Managed)
    {
        if (constants.GainMapControl.Y > 0.5f)
        {
            var appleHdr = ReconstructAppleHdrSample(sdrBaseLinear, gain, constants.GainMapMax.X, weight);
            return ConvertGainMapBaseToBt709(appleHdr, constants, mode);
        }

        if (constants.SourceEncoding.Z > 0.5f)
        {
            var sdrBt709 = ConvertShaderGamutToBt709(sdrBaseLinear, constants.GainMapControl.Z);
            var sdrAlt = ConvertBt709ToShaderGamut(sdrBt709, constants.SourceEncoding.W);
            var hdrAlt = ReconstructAdobeHdrSampleUnclamped(sdrAlt, gain, constants, weight);
            return ConvertShaderGamutToBt709(hdrAlt, constants.SourceEncoding.W, mode);
        }

        var hdrBase = ReconstructAdobeHdrSample(sdrBaseLinear, gain, constants, weight);
        return ConvertGainMapBaseToBt709(hdrBase, constants, mode);
    }

    public static Vector3 ReconstructGainMapHdrToBt2020(
        Vector3 sdrBaseLinear,
        Vector3 gain,
        GainMapShaderConstants constants,
        float weight)
    {
        if (constants.GainMapControl.Y > 0.5f)
        {
            var appleHdr = ReconstructAppleHdrSample(sdrBaseLinear, gain, constants.GainMapMax.X, weight);
            return ConvertGainMapBaseToBt2020(appleHdr, constants);
        }

        if (constants.SourceEncoding.Z > 0.5f)
        {
            var sdrBt709 = ConvertShaderGamutToBt709(sdrBaseLinear, constants.GainMapControl.Z);
            var sdrAlt = ConvertBt709ToShaderGamut(sdrBt709, constants.SourceEncoding.W);
            var hdrAlt = ReconstructAdobeHdrSampleUnclamped(sdrAlt, gain, constants, weight);
            return ConvertShaderGamutToBt2020(hdrAlt, constants.SourceEncoding.W);
        }

        var hdrBase = ReconstructAdobeHdrSample(sdrBaseLinear, gain, constants, weight);
        return ConvertGainMapBaseToBt2020(hdrBase, constants);
    }

    public static float CalculateGainMapWeight(
        GainMapShaderConstants constants,
        float displayHeadroomStops)
    {
        var explicitWeight = float.IsFinite(constants.GainMapControl.X)
            ? Math.Clamp(constants.GainMapControl.X, 0.0f, 1.0f)
            : 0.0f;
        var baseCapacity = constants.HdrCapacity.X;
        var altCapacity = constants.HdrCapacity.Y;
        if (MathF.Abs(altCapacity - baseCapacity) <= 0.0001f)
        {
            return explicitWeight;
        }

        var adaptiveWeight = Math.Clamp(
            (displayHeadroomStops - baseCapacity) / (altCapacity - baseCapacity),
            0.0f,
            1.0f);
        return explicitWeight * adaptiveWeight;
    }

    public static Vector3 ConvertBt2020ToBt709(Vector3 value, ColorGamutMappingMode mode)
    {
        var converted = Bt2020ToBt709(value);
        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertP3ToBt709(Vector3 value, ColorGamutMappingMode mode)
    {
        var converted = P3ToBt709(value);
        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertProPhotoToBt709(Vector3 value, ColorGamutMappingMode mode)
    {
        var converted = ProPhotoToBt709(value);
        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertAdobeRgbToBt709(Vector3 value, ColorGamutMappingMode mode)
    {
        var converted = AdobeRgbToBt709(value);
        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
    }

    public static Vector3 ConvertGainMapBaseToBt2020(Vector3 value, GainMapShaderConstants constants)
    {
        return constants.GainMapControl.Z switch
        {
            > 3.5f => AdobeRgbToBt2020(value),
            > 2.5f => ProPhotoToBt2020(value),
            > 1.5f => value,
            > 0.5f => P3ToBt2020(value),
            _ => Bt709ToBt2020(value),
        };
    }

    public static Vector3 ReconstructAdobeHdrSample(
        Vector3 sdr,
        Vector3 gain,
        GainMapShaderConstants constants,
        float weight)
    {
        var unclamped = ReconstructAdobeHdrSampleUnclamped(sdr, gain, constants, weight);
        return Vector3.Max(unclamped, Vector3.Zero);
    }

    private static Vector3 ReconstructAdobeHdrSampleUnclamped(
        Vector3 sdr,
        Vector3 gain,
        GainMapShaderConstants constants,
        float weight)
    {
        var gx = float.IsFinite(gain.X) ? Math.Clamp(gain.X, 0.0f, 1.0f) : 0.0f;
        var gy = float.IsFinite(gain.Y) ? Math.Clamp(gain.Y, 0.0f, 1.0f) : 0.0f;
        var gz = float.IsFinite(gain.Z) ? Math.Clamp(gain.Z, 0.0f, 1.0f) : 0.0f;
        var invGammaX = 1.0f / Math.Max(constants.Gamma.X, 0.0001f);
        var invGammaY = 1.0f / Math.Max(constants.Gamma.Y, 0.0001f);
        var invGammaZ = 1.0f / Math.Max(constants.Gamma.Z, 0.0001f);
        var logRecovery = new Vector3(
            MathF.Abs(invGammaX - 1.0f) <= 0.0001f ? gx : MathF.Pow(gx, invGammaX),
            MathF.Abs(invGammaY - 1.0f) <= 0.0001f ? gy : MathF.Pow(gy, invGammaY),
            MathF.Abs(invGammaZ - 1.0f) <= 0.0001f ? gz : MathF.Pow(gz, invGammaZ));
        var logBoost = Vector3.Lerp(
            new Vector3(constants.GainMapMin.X, constants.GainMapMin.Y, constants.GainMapMin.Z),
            new Vector3(constants.GainMapMax.X, constants.GainMapMax.Y, constants.GainMapMax.Z),
            logRecovery);
        return new Vector3(
            ((sdr.X + constants.OffsetSdr.X) * MathF.Pow(2.0f, logBoost.X * weight)) - constants.OffsetHdr.X,
            ((sdr.Y + constants.OffsetSdr.Y) * MathF.Pow(2.0f, logBoost.Y * weight)) - constants.OffsetHdr.Y,
            ((sdr.Z + constants.OffsetSdr.Z) * MathF.Pow(2.0f, logBoost.Z * weight)) - constants.OffsetHdr.Z);
    }

    public static Vector3 ReconstructAppleHdrSample(Vector3 sdr, Vector3 gain, float headroom, float weight)
    {
        var effectiveHeadroom = MathF.Pow(Math.Max(headroom, 1.0f), Math.Clamp(weight, 0.0f, 1.0f));
        return ReconstructAppleHdrSampleWithEffectiveHeadroom(sdr, gain, effectiveHeadroom);
    }

    public static Vector3 ReconstructAppleHdrSampleWithEffectiveHeadroom(
        Vector3 sdr,
        Vector3 gain,
        float effectiveHeadroom)
    {
        var linearGain = new Vector3(
            Rec709ToLinear(gain.X),
            Rec709ToLinear(gain.Y),
            Rec709ToLinear(gain.Z));
        return sdr * (Vector3.One + ((effectiveHeadroom - 1.0f) * linearGain));
    }

    private static float PqToSceneLinearChannel(float value, float referenceWhiteNits)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            return 0.0f;
        }

        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 32.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 128.0f;
        const float c3 = 2392.0f / 128.0f;
        var y = MathF.Pow(Math.Min(value, 1.0f), 1.0f / m2);
        var nits = 10000.0f * MathF.Pow(Math.Max((y - c1) / Math.Max(c2 - (c3 * y), 0.000001f), 0.0f), 1.0f / m1);
        return nits / referenceWhiteNits;
    }

    private static float HlgToSceneLinearChannel(float value)
    {
        const float a = 0.17883277f;
        const float b = 0.28466892f;
        const float c = 0.55991073f;
        return value <= 0.5f
            ? (value * value) / 3.0f
            : (MathF.Exp((value - c) / a) + b) / 12.0f;
    }

    public static void SanitizeRgbaHalfBufferInPlace(Span<byte> rgbaHalfBytes)
    {
        var words = MemoryMarshal.Cast<byte, ushort>(rgbaHalfBytes);
        for (var i = 0; i < words.Length; i++)
        {
            var bits = words[i];
            if ((bits & 0x7C00) == 0x7C00)
            {
                // NaN (mantissa != 0) -> 0.0f for RGB, 1.0f for A; +/-Inf (mantissa == 0) -> +/-65504.0f.
                words[i] = (bits & 0x03FF) != 0
                    ? ((i & 3) == 3 ? (ushort)0x3C00 : (ushort)0x0000)
                    : (ushort)((bits & 0x8000) | 0x7BFF);
            }
        }
    }
}
