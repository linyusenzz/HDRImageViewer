using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

internal sealed record JxlGainMapBox(
    IsoGainMapMetadata Metadata,
    byte[] GainMapCodestream,
    GainMapColorGamut AlternateColorGamut = GainMapColorGamut.Unknown);

internal static class JxlGainMapDecoder
{
    public static async Task<GainMapBaseThumbnailInput> DecodeBaseForThumbnailAsync(
        HdrImageDocument document,
        int maxPixelSize,
        CancellationToken cancellationToken = default)
    {
        if (document.JxlProbe is not { IsJxl: true, HasGainMapBox: true } probe)
        {
            throw new InvalidOperationException("The selected document does not contain a JPEG XL gain-map box.");
        }

        var decodedPrimary = await BitmapDecodeService.DecodeDocumentAsync(document, maxPixelSize, cancellationToken);
        var primaryGamut = ResolvePrimaryColorGamut(probe);
        var primaryTransfer = ResolvePrimaryTransfer(probe);
        var primary = decodedPrimary with
        {
            DecoderName = $"{decodedPrimary.DecoderName} [JXL gain-map base]",
            UsesBt2020Primaries = primaryGamut == GainMapColorGamut.Bt2100,
            ColorGamut = primaryGamut,
        };
        var constants = new GainMapShaderConstants
        {
            GainMapControl = new Vector4(1.0f, 0.0f, HdrColorMath.ToShaderGamut(primaryGamut), 0.0f),
            SourceEncoding = new Vector4(primaryTransfer, 0.0f, 0.0f, 0.0f),
            Orientation = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
        };
        return new GainMapBaseThumbnailInput(primary, constants);
    }

    public static async Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.JxlProbe is not { IsJxl: true, HasGainMapBox: true })
        {
            throw new InvalidOperationException("The selected document does not contain a JPEG XL gain-map box.");
        }

        var box = await ReadGainMapBoxAsync(document.Path, cancellationToken);
        var primaryTask = BitmapDecodeService.DecodeDocumentAsync(document, maxPixelSize, cancellationToken);
        var gainMapTask = DecodeGainMapAsync(box.GainMapCodestream, maxPixelSize, cancellationToken);
        await Task.WhenAll(primaryTask, gainMapTask);

        var primaryGamut = ResolvePrimaryColorGamut(document.JxlProbe);
        var decodedPrimary = await primaryTask;
        var decodedGainMap = await gainMapTask;
        var primary = decodedPrimary with
        {
            DecoderName = $"{decodedPrimary.DecoderName} [JXL gain-map base]",
            UsesBt2020Primaries = primaryGamut == GainMapColorGamut.Bt2100,
            ColorGamut = primaryGamut,
        };
        return new GainMapRenderInputs(
            primary,
            decodedGainMap,
            box.Metadata.CreateConstants(
                primaryGamut,
                ResolvePrimaryTransfer(document.JxlProbe),
                orientation: 1.0f,
                alternateColorGamut: box.AlternateColorGamut));
    }

    public static IsoGainMapMetadata? TryReadMetadata(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);
            var fileLength = stream.Length;
            if (fileLength >= 12)
            {
                Span<byte> signature = stackalloc byte[12];
                if (stream.ReadAtLeast(signature, 12, throwOnEndOfStream: false) == 12)
                {
                    stream.Position = HasContainerSignature(signature) ? 12 : 0;
                }
            }

            Span<byte> header = stackalloc byte[16];
            while (stream.Position + 8 <= fileLength)
            {
                var boxStart = stream.Position;
                if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
                {
                    break;
                }

                var size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var type = Encoding.ASCII.GetString(header.Slice(4, 4));
                long size = size32;
                var headerSize = 8;
                if (size32 == 1)
                {
                    if (stream.Position + 8 > fileLength
                        || stream.ReadAtLeast(header.Slice(8, 8), 8, throwOnEndOfStream: false) < 8)
                    {
                        break;
                    }

                    size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)));
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    size = fileLength - boxStart;
                }

                if (size < headerSize || size > fileLength - boxStart)
                {
                    break;
                }

                if (type == "jhgm")
                {
                    var payloadLength = size - headerSize;
                    var prefixLength = (int)Math.Min(payloadLength, 512);
                    var prefix = new byte[prefixLength];
                    if (stream.ReadAtLeast(prefix, prefixLength, throwOnEndOfStream: false) < 8 || prefix[0] != 0)
                    {
                        return null;
                    }

                    var metadataSize = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(1, 2));
                    if (metadataSize <= 0 || 3 + metadataSize > prefixLength)
                    {
                        return null;
                    }

                    return IsoGainMapMetadataParser.Parse(
                        prefix.AsSpan(3, metadataSize),
                        IsoGainMapMetadataPayloadKind.JpegXl);
                }

                stream.Position = boxStart + size;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DecodedBitmap> DecodeGainMapAsync(
        byte[] gainMapCodestream,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () =>
                {
                    var decoded = JxlNativeDecoder.DecodeGainMapCodestream(gainMapCodestream, cancellationToken);
                    return BitmapPreviewResampler.Downscale(decoded, maxPixelSize, cancellationToken);
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back to djxl CLI when libjxl native DLL is unavailable.
        }

        var djxl = NativeToolLocator.FindTool("djxl.exe")
            ?? throw new InvalidOperationException("未找到 djxl.exe，无法解码 JPEG XL gain-map。请把 libjxl 工具放到 external\\encoders\\x64。");
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "jxl-gainmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var gainJxlPath = Path.Combine(tempDir, "gain.jxl");
        var gainPngPath = Path.Combine(tempDir, "gain.png");

        try
        {
            await File.WriteAllBytesAsync(gainJxlPath, gainMapCodestream, cancellationToken);
            using var process = NativeProcessRunner.Create(djxl);
            process.StartInfo.ArgumentList.Add(gainJxlPath);
            process.StartInfo.ArgumentList.Add(gainPngPath);
            process.StartInfo.ArgumentList.Add("--quiet");
            await NativeProcessRunner.RunAsync(process, "libjxl djxl gain-map", cancellationToken);

            return await BitmapDecodeService.DecodeFileRawRgba16Async(
                gainPngPath,
                "libjxl jhgm gain map",
                maxPixelSize,
                cancellationToken);
        }
        finally
        {
            TryDeleteFile(gainJxlPath);
            TryDeleteFile(gainPngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    internal static async Task<JxlGainMapBox> ReadGainMapBoxAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var fileLength = stream.Length;
        if (fileLength >= 12)
        {
            var signature = new byte[12];
            await stream.ReadExactlyAsync(signature, cancellationToken);
            stream.Position = HasContainerSignature(signature) ? 12 : 0;
        }

        var header = new byte[8];
        while (stream.Position + header.Length <= fileLength)
        {
            var boxStart = stream.Position;
            await stream.ReadExactlyAsync(header, cancellationToken);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (stream.Position + 8 > fileLength)
                {
                    break;
                }

                var largeSizeBytes = new byte[8];
                await stream.ReadExactlyAsync(largeSizeBytes, cancellationToken);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(largeSizeBytes));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = fileLength - boxStart;
            }

            if (size < headerSize || size > fileLength - boxStart)
            {
                break;
            }

            if (type == "jhgm")
            {
                var payloadLength = checked((int)(size - headerSize));
                var payload = new byte[payloadLength];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                return ParseGainMapBundle(payload);
            }

            if (type == "brob" && size - headerSize > 4)
            {
                await stream.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken);
                if (header.AsSpan(0, 4).SequenceEqual("jhgm"u8))
                {
                    var payload = await DecompressGainMapBoxAsync(stream, size - headerSize - 4, cancellationToken);
                    return ParseGainMapBundle(payload);
                }
            }

            stream.Position = boxStart + size;
        }

        throw new InvalidOperationException("JPEG XL container did not contain a jhgm gain-map box.");
    }

    private static JxlGainMapBox ParseGainMapBundle(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 || payload[0] != 0)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain-map bundle version is unsupported.");
        }

        var metadataSize = BinaryPrimitives.ReadUInt16BigEndian(payload[1..3]);
        var offset = 3;
        if (metadataSize <= 0 || offset + metadataSize > payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain-map metadata is truncated.");
        }

        var metadata = IsoGainMapMetadataParser.Parse(
            payload.Slice(offset, metadataSize),
            IsoGainMapMetadataPayloadKind.JpegXl);
        offset += metadataSize;
        if (offset >= payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain-map codestream is missing.");
        }

        // jhgm stores a byte length for the compact ColorEncoding.
        var encodingLength = payload[offset++];
        if (encodingLength > payload.Length - offset)
        {
            throw new InvalidDataException("Truncated JPEG XL alternate color encoding.");
        }

        var encodedGamut = encodingLength == 0
            ? GainMapColorGamut.Unknown
            : JxlNativeDecoder.ReadCompactColorGamut(payload.Slice(offset, encodingLength));
        offset += encodingLength;
        if (offset + 4 > payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm alternate ICC field is truncated.");
        }

        var altIccSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)));
        offset += 4;
        if (altIccSize > payload.Length - offset)
        {
            throw new InvalidOperationException("JPEG XL jhgm alternate ICC field has an invalid length.");
        }

        var altGamut = encodedGamut == GainMapColorGamut.Unknown && altIccSize > 0
            ? IccColorProfileDetector.DetectColorGamut(payload.Slice(offset, altIccSize))
            : encodedGamut;
        if (!metadata.UseBaseColorSpace && encodingLength > 0 && altGamut == GainMapColorGamut.Unknown)
            throw new NotSupportedException("Unsupported JPEG XL alternate color encoding.");
        offset += altIccSize;

        if (offset + 4 <= payload.Length && !LooksLikeJxlCodestream(payload[offset..]))
        {
            var declaredGainMapSize = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            if (declaredGainMapSize > 0
                && declaredGainMapSize <= int.MaxValue
                && declaredGainMapSize <= payload.Length - offset - 4)
            {
                offset += 4;
                return new JxlGainMapBox(metadata, payload.Slice(offset, (int)declaredGainMapSize).ToArray(), altGamut);
            }
        }

        if (offset >= payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain-map codestream is empty.");
        }

        return new JxlGainMapBox(metadata, payload[offset..].ToArray(), altGamut);
    }

    private static bool HasContainerSignature(ReadOnlySpan<byte> data)
    {
        return data.Length >= 12
            && BinaryPrimitives.ReadUInt32BigEndian(data[..4]) == 12
            && Encoding.ASCII.GetString(data.Slice(4, 4)) == "JXL "
            && data[8] == 0x0D
            && data[9] == 0x0A
            && data[10] == 0x87
            && data[11] == 0x0A;
    }

    private static async Task<byte[]> DecompressGainMapBoxAsync(Stream stream, long remaining, CancellationToken token)
    {
        const int maxPayloadBytes = 64 * 1024 * 1024;
        using var decoder = new BrotliDecoder();
        using var output = new MemoryStream();
        var compressed = new byte[16 * 1024];
        var decoded = new byte[16 * 1024];
        int offset = 0, count = 0;
        var status = OperationStatus.NeedMoreData;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (status == OperationStatus.NeedMoreData)
            {
                count = (int)Math.Min(remaining, compressed.Length);
                if (count == 0)
                {
                    throw new InvalidDataException("Truncated JPEG XL compressed gain map.");
                }

                await stream.ReadExactlyAsync(compressed.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
                offset = 0;
            }

            status = decoder.Decompress(compressed.AsSpan(offset, count), decoded, out var consumed, out var written);
            offset += consumed;
            count -= consumed;
            if (status == OperationStatus.InvalidData || output.Length + written > maxPayloadBytes)
            {
                throw new InvalidDataException("Invalid JPEG XL compressed gain map or decoded size exceeds 64 MiB.");
            }

            output.Write(decoded, 0, written);
            if (status == OperationStatus.Done)
            {
                return output.ToArray();
            }
        }
    }

    private static bool LooksLikeJxlCodestream(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A;
    }

    private static GainMapColorGamut ResolvePrimaryColorGamut(JxlProbeResult probe)
    {
        var gamut = probe.ColorGamut;
        return gamut == GainMapColorGamut.Unknown ? GainMapColorGamut.Bt709 : gamut;
    }

    private static float ResolvePrimaryTransfer(JxlProbeResult probe)
    {
        return probe.TransferFunction.Contains("BT.709", StringComparison.OrdinalIgnoreCase)
            || probe.TransferFunction.Contains("Rec.709", StringComparison.OrdinalIgnoreCase)
            ? HdrColorMath.GainMapBaseTransferBt709
            : HdrColorMath.GainMapBaseTransferSrgb;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
