using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

internal sealed record JxlGainMapBox(IsoGainMapMetadata Metadata, byte[] GainMapCodestream);

internal static class JxlGainMapDecoder
{
    public static async Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.JxlProbe is not { IsJxl: true, HasGainMapBox: true })
        {
            throw new InvalidOperationException("The selected document does not contain a JPEG XL gain-map box.");
        }

        var djxl = NativeToolLocator.FindTool("djxl.exe")
            ?? throw new InvalidOperationException("未找到 djxl.exe，无法解码 JPEG XL gain-map。请把 libjxl 工具放到 external\\encoders\\x64。");
        var box = await ReadGainMapBoxAsync(document.Path, cancellationToken);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "jxl-gainmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var gainJxlPath = Path.Combine(tempDir, "gain.jxl");
        var gainPngPath = Path.Combine(tempDir, "gain.png");

        try
        {
            await File.WriteAllBytesAsync(gainJxlPath, box.GainMapCodestream, cancellationToken);
            using var process = NativeProcessRunner.Create(djxl);
            process.StartInfo.ArgumentList.Add(gainJxlPath);
            process.StartInfo.ArgumentList.Add(gainPngPath);
            process.StartInfo.ArgumentList.Add("--quiet");
            await NativeProcessRunner.RunAsync(process, "libjxl djxl gain-map", cancellationToken);

            var primaryTask = BitmapDecodeService.DecodeDocumentAsync(document, maxPixelSize, cancellationToken);
            var gainMapTask = BitmapDecodeService.DecodeFileRawRgba16Async(
                gainPngPath,
                "libjxl jhgm gain map",
                maxPixelSize,
                cancellationToken);
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
                box.Metadata.CreateConstants(primaryGamut, ResolvePrimaryTransfer(document.JxlProbe)));
        }
        finally
        {
            TryDeleteFile(gainJxlPath);
            TryDeleteFile(gainPngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<JxlGainMapBox> ReadGainMapBoxAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
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

            if (size < headerSize || boxStart + size > fileLength)
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

        var hasColorEncoding = payload[offset++] != 0;
        if (hasColorEncoding)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain maps with embedded alternate color encoding are not supported yet.");
        }

        if (offset + 4 > payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm alternate ICC field is truncated.");
        }

        var altIccSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)));
        offset += 4;
        if (altIccSize < 0 || offset + altIccSize > payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm alternate ICC field has an invalid length.");
        }

        offset += altIccSize;
        if (offset + 4 <= payload.Length && !LooksLikeJxlCodestream(payload[offset..]))
        {
            var declaredGainMapSize = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            if (declaredGainMapSize > 0
                && declaredGainMapSize <= int.MaxValue
                && offset + 4 + (int)declaredGainMapSize <= payload.Length)
            {
                offset += 4;
                return new JxlGainMapBox(metadata, payload.Slice(offset, (int)declaredGainMapSize).ToArray());
            }
        }

        if (offset >= payload.Length)
        {
            throw new InvalidOperationException("JPEG XL jhgm gain-map codestream is empty.");
        }

        return new JxlGainMapBox(metadata, payload[offset..].ToArray());
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

    private static bool LooksLikeJxlCodestream(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A;
    }

    private static GainMapColorGamut ResolvePrimaryColorGamut(JxlProbeResult probe)
    {
        if (probe.UsesBt2020Primaries)
        {
            return GainMapColorGamut.Bt2100;
        }

        return probe.ColorPrimaries.Contains("P3", StringComparison.OrdinalIgnoreCase)
            ? GainMapColorGamut.DisplayP3
            : GainMapColorGamut.Bt709;
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
