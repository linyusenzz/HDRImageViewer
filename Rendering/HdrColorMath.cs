using System.Numerics;

namespace HdrImageViewer.Rendering;

internal static class HdrColorMath
{
    public const float ReferenceWhiteNits = 80.0f;
    public const float UltraHdrReferenceWhiteNits = 203.0f;

    public static Vector3 SrgbToLinear(Vector3 value)
    {
        return new Vector3(
            SrgbToLinear(value.X),
            SrgbToLinear(value.Y),
            SrgbToLinear(value.Z));
    }

    public static float SrgbToLinear(float value)
    {
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    public static float Rec709ToLinear(float value)
    {
        return value < 0.081f
            ? value / 4.5f
            : MathF.Pow((value + 0.099f) / 1.099f, 1.0f / 0.45f);
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
        float referenceWhiteNits = ReferenceWhiteNits)
    {
        var hlgScene = new Vector3(
            HlgToSceneLinearChannel(Math.Clamp(encoded.X, 0.0f, 1.0f)),
            HlgToSceneLinearChannel(Math.Clamp(encoded.Y, 0.0f, 1.0f)),
            HlgToSceneLinearChannel(Math.Clamp(encoded.Z, 0.0f, 1.0f)));
        var gamma = CalculateHlgSystemGamma(targetScenePeak, referenceWhiteNits);
        var hlgLuma = Math.Max((0.2627f * hlgScene.X) + (0.6780f * hlgScene.Y) + (0.0593f * hlgScene.Z), 0.000001f);
        return hlgScene * MathF.Pow(hlgLuma, gamma - 1.0f) * targetScenePeak;
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

    public static Vector3 ProPhotoToBt709(Vector3 value)
    {
        return new Vector3(
            (2.034368f * value.X) - (0.727634f * value.Y) - (0.306733f * value.Z),
            (-0.228827f * value.X) + (1.231753f * value.Y) - (0.002927f * value.Z),
            (-0.008558f * value.X) - (0.153268f * value.Y) + (1.161827f * value.Z));
    }

    public static Vector3 P3ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.753833f * value.X) + (0.198597f * value.Y) + (0.047570f * value.Z),
            (0.045744f * value.X) + (0.941777f * value.Y) + (0.012479f * value.Z),
            (-0.001210f * value.X) + (0.017601f * value.Y) + (0.983608f * value.Z));
    }

    public static Vector3 ConvertGainMapBaseToBt709(Vector3 value, GainMapShaderConstants constants)
    {
        return constants.GainMapControl.Z switch
        {
            > 2.5f => ProPhotoToBt709(value),
            > 1.5f => Bt2020ToBt709(value),
            > 0.5f => P3ToBt709(value),
            _ => value,
        };
    }

    public static Vector3 ConvertGainMapBaseToBt709(
        Vector3 value,
        GainMapShaderConstants constants,
        ColorGamutMappingMode mode)
    {
        var converted = constants.GainMapControl.Z switch
        {
            > 2.5f => ProPhotoToBt709(value),
            > 1.5f => Bt2020ToBt709(value),
            > 0.5f => P3ToBt709(value),
            _ => value,
        };

        return mode == ColorGamutMappingMode.Clip
            ? Vector3.Max(converted, Vector3.Zero)
            : converted;
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

    public static Vector3 ConvertGainMapBaseToBt2020(Vector3 value, GainMapShaderConstants constants)
    {
        return constants.GainMapControl.Z switch
        {
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
        var logRecovery = new Vector3(
            MathF.Pow(Math.Clamp(gain.X, 0.0f, 1.0f), 1.0f / Math.Max(constants.Gamma.X, 0.0001f)),
            MathF.Pow(Math.Clamp(gain.Y, 0.0f, 1.0f), 1.0f / Math.Max(constants.Gamma.Y, 0.0001f)),
            MathF.Pow(Math.Clamp(gain.Z, 0.0f, 1.0f), 1.0f / Math.Max(constants.Gamma.Z, 0.0001f)));
        var logBoost = Vector3.Lerp(
            new Vector3(constants.GainMapMin.X, constants.GainMapMin.Y, constants.GainMapMin.Z),
            new Vector3(constants.GainMapMax.X, constants.GainMapMax.Y, constants.GainMapMax.Z),
            logRecovery);
        return new Vector3(
            ReconstructAdobeHdrChannel(sdr.X, constants.OffsetSdr.X, constants.OffsetHdr.X, logBoost.X, weight),
            ReconstructAdobeHdrChannel(sdr.Y, constants.OffsetSdr.Y, constants.OffsetHdr.Y, logBoost.Y, weight),
            ReconstructAdobeHdrChannel(sdr.Z, constants.OffsetSdr.Z, constants.OffsetHdr.Z, logBoost.Z, weight));
    }

    public static Vector3 ReconstructAppleHdrSample(Vector3 sdr, Vector3 gain, float headroom, float weight)
    {
        var linearGain = new Vector3(
            Rec709ToLinear(Math.Clamp(gain.X, 0.0f, 1.0f)),
            Rec709ToLinear(Math.Clamp(gain.Y, 0.0f, 1.0f)),
            Rec709ToLinear(Math.Clamp(gain.Z, 0.0f, 1.0f)));
        var effectiveHeadroom = MathF.Pow(Math.Max(headroom, 1.0f), Math.Clamp(weight, 0.0f, 1.0f));
        return sdr * (Vector3.One + ((effectiveHeadroom - 1.0f) * linearGain));
    }

    private static float ReconstructAdobeHdrChannel(float sdr, float offsetSdr, float offsetHdr, float logBoost, float weight)
    {
        return Math.Max(0.0f, ((sdr + offsetSdr) * MathF.Pow(2.0f, logBoost * weight)) - offsetHdr);
    }

    private static float PqToSceneLinearChannel(float value, float referenceWhiteNits)
    {
        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 32.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 128.0f;
        const float c3 = 2392.0f / 128.0f;
        var y = MathF.Pow(Math.Max(value, 0.0f), 1.0f / m2);
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
}
