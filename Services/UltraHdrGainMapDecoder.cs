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
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.GainMapProbe is not { IsRenderableUltraHdr: true } probe || probe.Metadata is null)
        {
            throw new InvalidOperationException("The selected document does not contain a renderable Ultra HDR gain map.");
        }

        var container = await File.ReadAllBytesAsync(document.Path, cancellationToken);
        var primaryLength = checked((int)probe.PrimaryImageEndOffset!.Value);
        var gainMapOffset = checked((int)probe.GainMapOffset!.Value);
        var gainMapLength = probe.GainMapLength!.Value;

        // Preserve the base's wide-gamut primaries (Display P3 / BT.2100) through
        // DoNotColorManage so the shader's P3/BT.2020 -> BT.709 matrix produces
        // scRGB negatives, and the swap chain can drive a wide-gamut display in
        // HDR mode. WIC's ColorManageToSrgb is reserved for sRGB / unknown bases
        // since there the conversion is a no-op anyway. Adobe/ISO gain-map
        // reconstruction is later scaled from its 203-nit reference into the
        // app's 80-nit scene-linear working space before tone mapping.
        var primaryColorManageToSrgb = probe.PrimaryColorGamut is GainMapColorGamut.Unknown or GainMapColorGamut.Bt709;
        var primaryTask = BitmapDecodeService.DecodeBytesAsync(
            container,
            0,
            primaryLength,
            colorManageToSrgb: primaryColorManageToSrgb,
            respectExifOrientation: false,
            maxPixelSize,
            cancellationToken);
        var gainMapTask = BitmapDecodeService.DecodeBytesAsync(
            container,
            gainMapOffset,
            gainMapLength,
            colorManageToSrgb: false,
            respectExifOrientation: false,
            maxPixelSize,
            cancellationToken);
        await Task.WhenAll(primaryTask, gainMapTask);
        var primary = primaryTask.Result;
        var gainMap = gainMapTask.Result;
        var constants = CreateConstants(probe.Metadata, probe.ExifOrientation, probe.PrimaryColorGamut);

        return new GainMapRenderInputs(primary, gainMap, constants);
    }

    public static Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        return DecodeRenderInputsAsync(document, maxPixelSize: null, cancellationToken);
    }

    private static GainMapShaderConstants CreateConstants(GainMapMetadata metadata, int? exifOrientation, GainMapColorGamut primaryColorGamut)
    {
        if (metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal))
        {
            return CreateAppleConstants(metadata, exifOrientation, primaryColorGamut);
        }

        var hdrCapacityMin = ParseScalar(metadata.HdrCapacityMin, 0.0);
        var gainMapMin = ParseVector(metadata.GainMapMin, 0.0);
        var gainMapMax = ParseVector(metadata.GainMapMax, 1.0);
        var gamma = ParseVector(metadata.Gamma, 1.0);
        var offsetSdr = ParseVector(metadata.OffsetSdr, DefaultOffset);
        var offsetHdr = ParseVector(metadata.OffsetHdr, DefaultOffset);
        var hdrCapacityMax = ParseScalar(metadata.HdrCapacityMax, Math.Max(hdrCapacityMin + 1.0, gainMapMax[0]));

        return new GainMapShaderConstants
        {
            GainMapMin = ToVector4(gainMapMin),
            GainMapMax = ToVector4(gainMapMax),
            Gamma = ToVector4(gamma),
            OffsetSdr = ToVector4(offsetSdr),
            OffsetHdr = ToVector4(offsetHdr),
            GainMapControl = new Vector4(1.0f, 0.0f, ToShaderGamut(primaryColorGamut), 0.0f),
            SourceEncoding = Vector4.Zero,
            Orientation = new Vector4(NormalizeOrientation(exifOrientation), 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4((float)hdrCapacityMin, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
    }

    private static GainMapShaderConstants CreateAppleConstants(GainMapMetadata metadata, int? exifOrientation, GainMapColorGamut primaryColorGamut)
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
            GainMapControl = new Vector4(1.0f, 1.0f, ToShaderGamut(primaryColorGamut), 0.0f),
            SourceEncoding = Vector4.Zero,
            Orientation = new Vector4(NormalizeOrientation(exifOrientation), 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(0.0f, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
    }

    private static float NormalizeOrientation(int? exifOrientation)
    {
        return exifOrientation is >= 1 and <= 8 ? exifOrientation.Value : 1.0f;
    }

    private static float ToShaderGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => 1.0f,
            GainMapColorGamut.Bt2100 => 2.0f,
            _ => 0.0f,
        };
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
