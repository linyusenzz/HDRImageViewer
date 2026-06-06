using System.Buffers.Binary;
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
        var box = ReadGainMapBox(document.Path);
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

            var primaryTask = BitmapDecodeService.DecodeFileAsync(document.Path, heifAvifProbe: null, maxPixelSize, cancellationToken);
            var gainMapTask = BitmapDecodeService.DecodeFileRawRgba16Async(
                gainPngPath,
                "libjxl jhgm gain map",
                maxPixelSize,
                cancellationToken);
            await Task.WhenAll(primaryTask, gainMapTask);

            var primaryGamut = ResolvePrimaryColorGamut(document.JxlProbe);
            var primary = primaryTask.Result with
            {
                DecoderName = $"{primaryTask.Result.DecoderName} [JXL gain-map base]",
                UsesBt2020Primaries = primaryGamut == GainMapColorGamut.Bt2100,
                ColorGamut = primaryGamut,
            };
            return new GainMapRenderInputs(
                primary,
                gainMapTask.Result,
                box.Metadata.CreateConstants(primaryGamut, ResolvePrimaryTransfer(document.JxlProbe)));
        }
        finally
        {
            TryDeleteFile(gainJxlPath);
            TryDeleteFile(gainPngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static JxlGainMapBox ReadGainMapBox(string path)
    {
        var data = File.ReadAllBytes(path);
        var offset = HasContainerSignature(data) ? 12 : 0;
        while (offset + 8 <= data.Length)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 16 > data.Length)
                {
                    break;
                }

                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8)));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = data.Length - offset;
            }

            if (size < headerSize || offset + size > data.Length)
            {
                break;
            }

            if (type == "jhgm")
            {
                return ParseGainMapBundle(data.AsSpan(offset + headerSize, checked((int)size - headerSize)));
            }

            offset += checked((int)size);
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

    private static bool HasContainerSignature(byte[] data)
    {
        return data.Length >= 12
            && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) == 12
            && System.Text.Encoding.ASCII.GetString(data, 4, 4) == "JXL "
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
