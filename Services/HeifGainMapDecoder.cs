using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using LibHeifSharp;

namespace HdrImageViewer.Services;

public static class HeifGainMapDecoder
{
    public static async Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.HeifAvifProbe is not { IsHeifFamily: true, HasGainMapSignal: true })
        {
            throw new InvalidOperationException("The selected document does not contain a renderable HEIF/AVIF gain map.");
        }

        // Trigger the static constructor of BitmapDecodeService to ensure libheif.dll resolver is registered.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(BitmapDecodeService).TypeHandle);

        return await Task.Run(() => DecodeRenderInputsCore(document, maxPixelSize, cancellationToken), cancellationToken);
    }

    public static Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        return DecodeRenderInputsAsync(document, maxPixelSize: null, cancellationToken);
    }

    private static GainMapRenderInputs DecodeRenderInputsCore(
        HdrImageDocument document,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (document.HeifAvifProbe is { HasGainMapAuxiliary: false, HasIsoGainMapSignal: true })
        {
            return DecodeTmapRenderInputsCore(document, maxPixelSize, cancellationToken);
        }

        using var context = new HeifContext(document.Path);
        using var primaryHandle = context.GetPrimaryImageHandle();
        cancellationToken.ThrowIfCancellationRequested();

        // Locate the gain map auxiliary image handle
        HeifImageHandle? gainMapHandle = null;
        var auxIds = primaryHandle.GetAuxiliaryImageIds();
        foreach (var id in auxIds)
        {
            var aux = primaryHandle.GetAuxiliaryImage(id);
            var auxType = aux.GetAuxiliaryType();
            if (auxType.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", StringComparison.OrdinalIgnoreCase)
                || auxType.Contains("gain", StringComparison.OrdinalIgnoreCase))
            {
                gainMapHandle = aux;
                break;
            }
            else
            {
                aux.Dispose();
            }
        }

        if (gainMapHandle == null)
        {
            throw new InvalidOperationException("Could not locate gain map auxiliary image inside HEIF container.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            GainMapMetadata? metadata = null;
            var xmpBytes = primaryHandle.GetXmpMetadata();
            if (xmpBytes is not null && xmpBytes.Length > 0)
            {
                try
                {
                    var xmpString = Encoding.UTF8.GetString(xmpBytes);
                    var start = IndexOfXmlStart(xmpString);
                    if (start >= 0)
                    {
                        var end = IndexOfXmlEnd(xmpString, start);
                        var cleanXml = end > start ? xmpString[start..end] : xmpString[start..].TrimEnd('\0');
                        var xdoc = XDocument.Parse(cleanXml);

                        metadata = ExtractMetadata(xdoc);
                        if (metadata is null)
                        {
                            var appleHeadroom = TryReadAppleXmpHeadroom(xdoc);
                            if (appleHeadroom is not null)
                            {
                                metadata = CreateAppleHdrGainMapMetadata(appleHeadroom);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore XMP parsing errors
                }
            }

            if (metadata is null)
            {
                metadata = CreateAppleHdrGainMapMetadata(null);
            }

            // Determine base image color gamut
            var gamut = DetectPrimaryColorGamut(primaryHandle, document.HeifAvifProbe);
            var primaryTransfer = DetectPrimaryTransfer(primaryHandle, document.HeifAvifProbe);

            // Decode the SDR base with Windows Imaging instead of libheif. The
            // local vcpkg/libde265 HEVC path can corrupt Apple HEIC primary
            // images while the auxiliary gain-map item still decodes correctly.
            var primary = DecodePrimaryWithWindowsImaging(document, gamut, maxPixelSize, cancellationToken);
            var gainMap = DecodeHeifImageHandle(gainMapHandle, "gain map", maxPixelSize);

            // Create constants
            var constants = metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal)
                ? CreateAppleConstants(metadata, gamut, primaryTransfer)
                : CreateStandardConstants(metadata, gamut, primaryTransfer);

            return new GainMapRenderInputs(primary, gainMap, constants);
        }
        finally
        {
            gainMapHandle.Dispose();
        }
    }

    private static GainMapRenderInputs DecodeTmapRenderInputsCore(
        HdrImageDocument document,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var description = HeifTmapBoxReader.Read(document.Path);
        using var context = new HeifContext(document.Path);
        using var primaryHandle = context.GetPrimaryImageHandle();
        using var gainMapHandle = context.GetImageHandle(CreateHeifItemId(description.GainMapItemId));
        cancellationToken.ThrowIfCancellationRequested();

        var gamut = DetectPrimaryColorGamut(primaryHandle, document.HeifAvifProbe);
        var primaryTransfer = DetectPrimaryTransfer(primaryHandle, document.HeifAvifProbe);
        var primary = DecodePrimaryWithWindowsImaging(document, gamut, maxPixelSize, cancellationToken);
        var gainMap = DecodeHeifImageHandle(gainMapHandle, $"ISO tmap gain map item #{description.GainMapItemId}", maxPixelSize);
        var constants = description.Metadata.CreateConstants(gamut, primaryTransfer);

        return new GainMapRenderInputs(primary, gainMap, constants);
    }

    private static DecodedBitmap DecodeHeifImageHandle(HeifImageHandle handle, string sourceName, int? maxPixelSize)
    {
        var bitDepth = Math.Clamp(handle.BitDepth, 8, 16);
        var use16Bit = bitDepth > 8;
        var chroma = use16Bit
            ? (BitConverter.IsLittleEndian ? HeifChroma.InterleavedRgba64LE : HeifChroma.InterleavedRgba64BE)
            : HeifChroma.InterleavedRgba32;

        using var image = handle.Decode(HeifColorspace.Rgb, chroma);
        var plane = image.GetPlane(HeifChannel.Interleaved);
        var width = plane.Width;
        var height = plane.Height;
        var bytesPerPixel = use16Bit ? 8 : 4;
        var rowBytes = checked(width * bytesPerPixel);
        var pixels = new byte[checked(rowBytes * height)];
        var src = plane.Scan0;
        var stride = plane.Stride;
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(src, y * stride), pixels, y * rowBytes, rowBytes);
        }

        if (use16Bit && bitDepth < 16)
        {
            var leftShift = 16 - bitDepth;
            var rightShift = bitDepth - leftShift;
            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                var rowSamples = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan(y * rowBytes, rowBytes));
                for (var i = 0; i < rowSamples.Length; i++)
                {
                    var v = rowSamples[i];
                    rowSamples[i] = (ushort)((v << leftShift) | (v >> rightShift));
                }
            });
        }

        return new DecodedBitmap(
            width,
            height,
            pixels,
            ColorManagedToSrgb: false, // shader handles Display P3 or BT.2020 base conversion
            DecoderName: $"libheif ({sourceName})",
            PixelFormat: use16Bit ? DecodedBitmapPixelFormat.Rgba16Unorm : DecodedBitmapPixelFormat.Rgba8Unorm,
            Transfer: DecodedBitmapTransfer.Sdr,
            UsesBt2020Primaries: false
        );
    }

    private static DecodedBitmap DecodePrimaryWithWindowsImaging(
        HdrImageDocument document,
        GainMapColorGamut colorGamut,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encodedBytes = File.ReadAllBytes(document.Path);
        var colorManageToSrgb = colorGamut is GainMapColorGamut.Unknown or GainMapColorGamut.Bt709;
        var bitmap = BitmapDecodeService.DecodeBytesAsync(
                encodedBytes,
                colorManageToSrgb,
                respectExifOrientation: false,
                maxPixelSize,
                cancellationToken)
            .GetAwaiter()
            .GetResult();

        return bitmap with
        {
            DecoderName = $"Windows Imaging HEIF primary ({(colorManageToSrgb ? "ColorManageToSrgb" : "DoNotColorManage")})",
            UsesBt2020Primaries = colorGamut == GainMapColorGamut.Bt2100,
            ColorGamut = colorGamut,
        };
    }

    private static GainMapColorGamut DetectPrimaryColorGamut(HeifImageHandle primaryHandle, HeifAvifProbeResult? probe)
    {
        var nclx = primaryHandle.NclxColorProfile;
        var rawPrimaries = (int?)(nclx?.ColorPrimaries) ?? probe?.ColorPrimaries;
        var nclxGamut = rawPrimaries switch
        {
            9 => GainMapColorGamut.Bt2100,
            12 => GainMapColorGamut.DisplayP3,
            1 => GainMapColorGamut.Bt709,
            _ => GainMapColorGamut.Unknown,
        };
        if (nclxGamut != GainMapColorGamut.Unknown)
        {
            return nclxGamut;
        }

        var iccProfile = primaryHandle.IccColorProfile?.GetIccProfileBytes();
        var iccGamut = iccProfile is { Length: > 0 }
            ? IccColorProfileDetector.DetectColorGamut(iccProfile)
            : GainMapColorGamut.Unknown;
        if (iccGamut != GainMapColorGamut.Unknown)
        {
            return iccGamut;
        }

        return probe?.HasAppleHdrGainMapSignal == true
            ? GainMapColorGamut.DisplayP3
            : GainMapColorGamut.Bt709;
    }

    private static float DetectPrimaryTransfer(HeifImageHandle primaryHandle, HeifAvifProbeResult? probe)
    {
        var nclx = primaryHandle.NclxColorProfile;
        var transfer = (int?)(nclx?.TransferCharacteristics) ?? probe?.TransferCharacteristics;
        return transfer is 1 or 6
            ? HdrColorMath.GainMapBaseTransferBt709
            : HdrColorMath.GainMapBaseTransferSrgb;
    }

    private static int IndexOfXmlStart(string text)
    {
        var xmpStart = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        if (xmpStart >= 0)
        {
            return xmpStart;
        }

        return text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
    }

    private static int IndexOfXmlEnd(string text, int start)
    {
        var xmpEnd = text.IndexOf("</x:xmpmeta>", start, StringComparison.Ordinal);
        if (xmpEnd >= 0)
        {
            return xmpEnd + "</x:xmpmeta>".Length;
        }

        var rdfEnd = text.IndexOf("</rdf:RDF>", start, StringComparison.Ordinal);
        return rdfEnd >= 0 ? rdfEnd + "</rdf:RDF>".Length : -1;
    }

    private static double? TryReadAppleXmpHeadroom(XDocument document)
    {
        var AppleHdrGainMapNamespace = "http://ns.apple.com/HDRGainMap/1.0/";
        foreach (var element in document.Descendants())
        {
            var rawHeadroom = GetAttributeValue(element, "HDRGainMapHeadroom", AppleHdrGainMapNamespace)
                ?? GetAttributeValue(element, "HDRGainMapHeadroom")
                ?? (element.Name.LocalName == "HDRGainMapHeadroom" ? element.Value : null);
            if (double.TryParse(rawHeadroom, NumberStyles.Float, CultureInfo.InvariantCulture, out var headroom))
            {
                return headroom;
            }
        }
        return null;
    }

    private static GainMapMetadata? ExtractMetadata(XDocument document)
    {
        var adobeNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";
        var isoNamespace = "http://ns.iso.org/iso21496-1/1.0/";
        var rdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        foreach (var desc in document.Descendants(XName.Get("Description", rdfNamespace)))
        {
            var version = GetAttributeValue(desc, "Version", isoNamespace)
                ?? GetAttributeValue(desc, "Version", adobeNamespace);

            if (!string.IsNullOrWhiteSpace(version))
            {
                var source = GetAttributeValue(desc, "Version", isoNamespace) is not null
                    ? "ISO 21496-1 HEIF metadata"
                    : "Adobe HEIF gain-map metadata";

                return new GainMapMetadata(
                    version,
                    GetGainMapValue(desc, "GainMapMin", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "GainMapMax", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "Gamma", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "OffsetSDR", isoNamespace, adobeNamespace) ?? GetGainMapValue(desc, "OffsetSdr", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "OffsetHDR", isoNamespace, adobeNamespace) ?? GetGainMapValue(desc, "OffsetHdr", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "HDRCapacityMin", isoNamespace, adobeNamespace) ?? GetGainMapValue(desc, "HdrCapacityMin", isoNamespace, adobeNamespace),
                    GetGainMapValue(desc, "HDRCapacityMax", isoNamespace, adobeNamespace) ?? GetGainMapValue(desc, "HdrCapacityMax", isoNamespace, adobeNamespace),
                    TryParseBoolean(GetGainMapValue(desc, "BaseRenditionIsHDR", isoNamespace, adobeNamespace) ?? GetGainMapValue(desc, "BaseRenditionIsHdr", isoNamespace, adobeNamespace)),
                    source);
            }
        }

        return null;
    }

    private static string? GetGainMapValue(XElement element, string localName, string ns1, string ns2)
    {
        var attr = GetAttributeValue(element, localName, ns1) ?? GetAttributeValue(element, localName, ns2);
        if (!string.IsNullOrWhiteSpace(attr))
        {
            return attr;
        }
        var child = element.Elements().FirstOrDefault(c =>
            (c.Name.LocalName == localName && (c.Name.NamespaceName == ns1 || c.Name.NamespaceName == ns2)));
        return child is null ? null : NormalizeGainMapElementValue(child);
    }

    private static string? NormalizeGainMapElementValue(XElement element)
    {
        var values = element
            .Descendants()
            .Where(child => child.Name.LocalName == "li")
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return values.Length > 0
            ? string.Join(", ", values)
            : element.Value;
    }

    private static bool TryParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return bool.TryParse(value, out var result) ? result : string.Equals(value, "1", StringComparison.Ordinal) || string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAttributeValue(XElement element, string localName, string? namespaceName = null)
    {
        var attribute = element
            .Attributes()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName == localName
                && (namespaceName is null || candidate.Name.NamespaceName == namespaceName));
        return attribute?.Value;
    }

    private static GainMapMetadata CreateAppleHdrGainMapMetadata(double? headroom)
    {
        var normalizedHeadroom = Math.Clamp(headroom ?? 8.0, 1.0, 64.0); // 8x fallback headroom (3.0 stops)
        var capacity = Math.Log2(normalizedHeadroom);
        var source = headroom is null
            ? $"Apple HDRGainMap (fallback {normalizedHeadroom:0.###}x)"
            : $"Apple HDRGainMap (headroom {normalizedHeadroom:0.###}x, {capacity:0.###} stops from XMP)";
        return new GainMapMetadata(
            "1.0",
            "0",
            normalizedHeadroom.ToString("0.###", CultureInfo.InvariantCulture),
            "1",
            "0",
            "0",
            "0",
            capacity.ToString("0.###", CultureInfo.InvariantCulture),
            false,
            source);
    }

    private static GainMapShaderConstants CreateAppleConstants(
        GainMapMetadata metadata,
        GainMapColorGamut primaryColorGamut,
        float primaryTransfer)
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
            SourceEncoding = new Vector4(primaryTransfer, 0.0f, 0.0f, 0.0f),
            Orientation = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(0.0f, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
    }

    private static GainMapShaderConstants CreateStandardConstants(
        GainMapMetadata metadata,
        GainMapColorGamut primaryColorGamut,
        float primaryTransfer)
    {
        var hdrCapacityMin = ParseScalar(metadata.HdrCapacityMin, 0.0);
        var gainMapMin = ParseVector(metadata.GainMapMin, 0.0);
        var gainMapMax = ParseVector(metadata.GainMapMax, 1.0);
        var gamma = ParseVector(metadata.Gamma, 1.0);
        var offsetSdr = ParseVector(metadata.OffsetSdr, 1.0 / 64.0);
        var offsetHdr = ParseVector(metadata.OffsetHdr, 1.0 / 64.0);
        var hdrCapacityMax = ParseScalar(metadata.HdrCapacityMax, Math.Max(hdrCapacityMin + 1.0, gainMapMax[0]));

        return new GainMapShaderConstants
        {
            GainMapMin = ToVector4(gainMapMin),
            GainMapMax = ToVector4(gainMapMax),
            Gamma = ToVector4(gamma),
            OffsetSdr = ToVector4(offsetSdr),
            OffsetHdr = ToVector4(offsetHdr),
            GainMapControl = new Vector4(1.0f, 0.0f, ToShaderGamut(primaryColorGamut), 0.0f),
            SourceEncoding = new Vector4(primaryTransfer, 0.0f, 0.0f, 0.0f),
            Orientation = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4((float)hdrCapacityMin, (float)hdrCapacityMax, 0.0f, 0.0f),
        };
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

    private static double ParseScalar(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static HeifItemId CreateHeifItemId(int itemId)
    {
        var value = default(HeifItemId);
        var field = typeof(HeifItemId).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LibHeifSharp HeifItemId layout changed; cannot address HEIF tmap gain-map item.");
        field.SetValueDirect(__makeref(value), checked((uint)itemId));
        return value;
    }
}
