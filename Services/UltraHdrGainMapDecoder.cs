using System.Globalization;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

public static class UltraHdrGainMapDecoder
{
    private const double DefaultOffset = 1.0 / 64.0;

    public static async Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document.GainMapProbe is not { IsRenderableUltraHdr: true } probe || probe.Metadata is null)
        {
            throw new InvalidOperationException("The selected document does not contain a renderable Ultra HDR gain map.");
        }

        var container = await File.ReadAllBytesAsync(document.Path, cancellationToken);
        var primaryBytes = container.AsSpan(0, checked((int)probe.PrimaryImageEndOffset!.Value)).ToArray();
        var gainMapBytes = container.AsSpan(checked((int)probe.GainMapOffset!.Value), probe.GainMapLength!.Value).ToArray();

        var primary = await BitmapDecodeService.DecodeBytesAsync(
            primaryBytes,
            colorManageToSrgb: true,
            respectExifOrientation: false,
            cancellationToken);
        var gainMap = await BitmapDecodeService.DecodeBytesAsync(
            gainMapBytes,
            colorManageToSrgb: false,
            respectExifOrientation: false,
            cancellationToken);
        var constants = CreateConstants(probe.Metadata, probe.ExifOrientation);

        return new GainMapRenderInputs(primary, gainMap, constants);
    }

    private static GainMapShaderConstants CreateConstants(GainMapMetadata metadata, int? exifOrientation)
    {
        if (metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal))
        {
            return CreateAppleConstants(metadata, exifOrientation);
        }

        var gainMapMin = ParseVector(metadata.GainMapMin, 0.0);
        var gainMapMax = ParseVector(metadata.GainMapMax, 1.0);
        var gamma = ParseVector(metadata.Gamma, 1.0);
        var offsetSdr = ParseVector(metadata.OffsetSdr, DefaultOffset);
        var offsetHdr = ParseVector(metadata.OffsetHdr, DefaultOffset);
        var hdrCapacityMin = ParseScalar(metadata.HdrCapacityMin, 0.0);
        var hdrCapacityMax = ParseScalar(metadata.HdrCapacityMax, Math.Max(hdrCapacityMin + 1.0, gainMapMax[0]));

        return new GainMapShaderConstants
        {
            GainMapMin = ToVector4(gainMapMin),
            GainMapMax = ToVector4(gainMapMax),
            Gamma = ToVector4(gamma),
            OffsetSdr = ToVector4(offsetSdr),
            OffsetHdr = ToVector4(offsetHdr),
            Weight = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
            Orientation = new Vector4(NormalizeOrientation(exifOrientation), 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4((float)hdrCapacityMin, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
    }

    private static GainMapShaderConstants CreateAppleConstants(GainMapMetadata metadata, int? exifOrientation)
    {
        var headroom = Math.Max(ParseScalar(metadata.GainMapMax, 2.0), 1.0);
        var hdrCapacityMax = Math.Max(ParseScalar(metadata.HdrCapacityMax, Math.Log2(headroom)), 0.0);

        return new GainMapShaderConstants
        {
            GainMapMin = Vector4.Zero,
            GainMapMax = new Vector4((float)headroom, (float)headroom, (float)headroom, 0.0f),
            Gamma = new Vector4(1.0f, 1.0f, 1.0f, 0.0f),
            OffsetSdr = Vector4.Zero,
            OffsetHdr = Vector4.Zero,
            Weight = new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
            Orientation = new Vector4(NormalizeOrientation(exifOrientation), 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(0.0f, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
    }

    private static float NormalizeOrientation(int? exifOrientation)
    {
        return exifOrientation is >= 1 and <= 8 ? exifOrientation.Value : 1.0f;
    }

    private static Vector4 ToVector4(double[] values)
    {
        return new Vector4((float)values[0], (float)values[1], (float)values[2], 0.0f);
    }

    private static double[] ParseVector(string? value, double fallback)
    {
        var values = ParseNumbers(value, fallback);
        return values.Length switch
        {
            >= 3 => [values[0], values[1], values[2]],
            _ => [values[0], values[0], values[0]],
        };
    }

    private static double ParseScalar(string? value, double fallback)
    {
        return ParseNumbers(value, fallback)[0];
    }

    private static double[] ParseNumbers(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [fallback];
        }

        var parsed = value
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .Where(number => !double.IsNaN(number))
            .ToArray();

        return parsed.Length == 0 ? [fallback] : parsed;
    }
}
