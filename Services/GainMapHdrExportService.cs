using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using Windows.Graphics.Imaging;

namespace HdrImageViewer.Services;

public sealed record GainMapHdrExportCapability(
    bool CanWriteJpegUltraHdr,
    string Backend,
    string Details,
    bool CanWriteHeifGainMap = false);

public enum UltraHdrGainMapChannelMode
{
    Monochrome,
    Rgb,
}

public enum UltraHdrSdrBaseColorGamut
{
    Auto,
    Bt709,
    DisplayP3,
    Bt2100,
}

public sealed record UltraHdrExportOptions(
    UltraHdrGainMapChannelMode GainMapChannelMode = UltraHdrGainMapChannelMode.Monochrome,
    UltraHdrSdrBaseColorGamut SdrBaseColorGamut = UltraHdrSdrBaseColorGamut.Auto)
{
    public static UltraHdrExportOptions Default { get; } = new();
}

public static class GainMapHdrExportService
{
    private const float AppReferenceWhiteNits = 80.0f;
    private const float UltraHdrReferenceWhiteNits = 203.0f;
    private const float DefaultHlgPeakNits = 1000.0f;
    private const int RgbaHalfFloatFormat = 4;
    private const int Bt709Gamut = 0;
    private const int DisplayP3Gamut = 1;
    private const int Bt2100Gamut = 2;
    private const int LinearTransfer = 0;

    public static GainMapHdrExportCapability GetCapability()
    {
        var cli = FindUltraHdrAppExecutable();
        if (cli is not null)
        {
            return new GainMapHdrExportCapability(
                true,
                "libultrahdr CLI",
                $"found {cli}",
                File.Exists(Path.Combine(Path.GetDirectoryName(cli)!, "libheif-uhdr.dll")));
        }

        return new GainMapHdrExportCapability(
            false,
            "libultrahdr planned",
            "未找到 libultrahdr CLI。请运行 eng\\build-native.ps1，或把 ultrahdr_app.exe 放到 external\\encoders\\x64。");
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds, outputPath, UltraHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds, outputPath, options, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds: null, outputPath, UltraHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds: null, outputPath, options, cancellationToken);
    }

    public static Task<string> ExportAsync(
        HdrImageDocument document,
        string outputPath,
        UltraHdrExportOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExportCoreAsync(document, null, outputPath, options ?? UltraHdrExportOptions.Default, cancellationToken);

    public static Task<string> ExportAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        UltraHdrExportOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExportCoreAsync(document, bounds, outputPath, options ?? UltraHdrExportOptions.Default, cancellationToken);

    private static async Task<string> ExportCoreAsync(
        HdrImageDocument document,
        BitmapBounds? bounds,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        var cli = FindUltraHdrAppExecutable()
            ?? throw new NotSupportedException(GetCapability().Details);

        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        var isJpeg = extension is ".jpg" or ".jpeg";
        if (!isJpeg && extension is not (".heic" or ".heif" or ".avif"))
        {
            throw new NotSupportedException("Gain Map 导出支持 JPEG、HEIC 和 AVIF。");
        }

        if (!isJpeg && !GetCapability().CanWriteHeifGainMap)
        {
            throw new NotSupportedException("未找到支持 HEIC / AVIF Gain Map 的 libultrahdr 后端，请重新构建编解码库。");
        }

        var resolvedOptions = options with
        {
            SdrBaseColorGamut = ResolveSdrBaseColorGamut(options.SdrBaseColorGamut, document),
        };

        using var source = await CreateSourceAsync(document, cancellationToken);
        var exportBounds = bounds ?? new BitmapBounds
        {
            X = 0,
            Y = 0,
            Width = checked((uint)source.Width),
            Height = checked((uint)source.Height),
        };
        if (exportBounds.Width == 0 || exportBounds.Height == 0
            || (ulong)exportBounds.X + exportBounds.Width > (ulong)source.Width
            || (ulong)exportBounds.Y + exportBounds.Height > (ulong)source.Height)
        {
            throw new InvalidOperationException($"裁切区域超出 HDR 源尺寸: {exportBounds.X},{exportBounds.Y} {exportBounds.Width}x{exportBounds.Height}, source {source.Width}x{source.Height}。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "ultrahdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var rawPath = Path.Combine(tempDir, "hdr-rgba-f16.raw");
        var candidateExtension = extension == ".heif" ? ".heic" : extension;
        var candidateOutput = Path.Combine(tempDir, "gainmap" + candidateExtension);

        try
        {
            await WriteRgbaHalfFloatRawAsync(rawPath, source, exportBounds, cancellationToken);
            await RunUltraHdrEncodeAsync(cli, rawPath, candidateOutput, exportBounds, resolvedOptions, cancellationToken);
            string metadataSummary;
            if (isJpeg)
            {
                var probe = await GainMapJpegProbe.ProbeAsync(candidateOutput, cancellationToken);
                if (!probe.IsRenderableUltraHdr)
                {
                    throw new InvalidOperationException($"导出结果未检测到可渲染 gain map: {probe.DisplayStatus}");
                }

                metadataSummary = $"gain max {probe.Metadata?.GainMapMax}, capacity {probe.Metadata?.HdrCapacityMax}";
            }
            else
            {
                var probe = await HeifAvifProbe.ProbeAsync(candidateOutput, cancellationToken);
                if (!probe.IsHeifFamily || (!probe.HasIsoGainMapSignal && !probe.HasGainMapAuxiliary))
                {
                    throw new InvalidOperationException("导出结果缺少 ISO Gain Map 元数据。");
                }

                if (probe.HasIsoGainMapSignal && !probe.HasGainMapAuxiliary)
                {
                    _ = HeifTmapBoxReader.Read(candidateOutput);
                }

                metadataSummary = "ISO 21496-1; container + gain-map metadata verified";
            }

            await ExportFileTransaction.CopyAsync(candidateOutput, outputPath, cancellationToken);
            return $"libultrahdr CLI; {extension}; {source.Description}; {DescribeGainMapChannelMode(resolvedOptions.GainMapChannelMode)} gain map; SDR base {DescribeSdrBaseGamut(resolvedOptions.SdrBaseColorGamut)}; {exportBounds.Width}x{exportBounds.Height}; {metadataSummary}";
        }
        finally
        {
            TryDeleteFile(rawPath);
            TryDeleteFile(candidateOutput);
            TryDeleteDirectory(tempDir);
        }
    }

    public static async Task<string> ExportPreservedJpegGainMapCropAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var cli = FindUltraHdrAppExecutable()
            ?? throw new NotSupportedException(GetCapability().Details);
        if (document.GainMapProbe is not { IsRenderableUltraHdr: true } probe || probe.Metadata is null)
        {
            throw new InvalidOperationException("Gain-map 保真裁切只支持已经包含可渲染 JPEG gain-map 的图片。");
        }

        if (probe.Metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Apple HDRGainMap 的增益图语义不同，当前不能用 libultrahdr scenario 4 做保真封装；请先使用“转为 Ultra HDR”。");
        }

        var primaryBytes = await UltraHdrGainMapDecoder.ReadSegmentAsync(document.Path, 0, checked((int)probe.PrimaryImageEndOffset!.Value), cancellationToken);
        var gainMapBytes = await UltraHdrGainMapDecoder.ReadSegmentAsync(document.Path, probe.GainMapOffset!.Value, probe.GainMapLength!.Value, cancellationToken);
        var primary = await BitmapDecodeService.DecodeBytesAsync(primaryBytes, colorManageToSrgb: true, respectExifOrientation: false, cancellationToken);
        var gainMap = await BitmapDecodeService.DecodeBytesAsync(gainMapBytes, colorManageToSrgb: false, respectExifOrientation: false, cancellationToken);
        var orientation = probe.ExifOrientation ?? 1;
        var primaryTransform = new ExifOrientationTransform(primary.PixelWidth, primary.PixelHeight, orientation);
        var gainMapTransform = new ExifOrientationTransform(gainMap.PixelWidth, gainMap.PixelHeight, orientation);
        if (bounds.Width == 0 || bounds.Height == 0
            || (ulong)bounds.X + bounds.Width > (ulong)primaryTransform.Width
            || (ulong)bounds.Y + bounds.Height > (ulong)primaryTransform.Height)
        {
            throw new InvalidOperationException($"裁切区域超出 base 尺寸: {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}, source {primaryTransform.Width}x{primaryTransform.Height}。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "gainmap-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var basePath = Path.Combine(tempDir, "base-crop.jpg");
        var gainPath = Path.Combine(tempDir, "gain-crop.jpg");
        var metadataPath = Path.Combine(tempDir, "metadata.cfg");
        var candidateOutput = Path.Combine(tempDir, "crop-preserved-ultrahdr.jpg");

        try
        {
            var gainBounds = MapBounds(bounds, primaryTransform.Width, primaryTransform.Height, gainMapTransform.Width, gainMapTransform.Height);
            await EncodeDecodedBitmapJpegCropAsync(primary, bounds, primaryTransform, basePath, BitmapPixelFormat.Bgra8, cancellationToken);
            await EncodeDecodedBitmapJpegCropAsync(gainMap, gainBounds, gainMapTransform, gainPath, BitmapPixelFormat.Bgra8, cancellationToken);
            await WriteMetadataConfigAsync(probe.Metadata, metadataPath, cancellationToken);
            await RunUltraHdrScenario4Async(cli, basePath, gainPath, metadataPath, candidateOutput, cancellationToken);

            var outputProbe = await GainMapJpegProbe.ProbeAsync(candidateOutput, cancellationToken);
            if (!outputProbe.IsRenderableUltraHdr)
            {
                throw new InvalidOperationException($"保真裁切已封装 JPEG，但反查未检测到可渲染 gain-map: {outputProbe.DisplayStatus}");
            }

            await ExportFileTransaction.CopyAsync(candidateOutput, outputPath, cancellationToken);
            return $"libultrahdr scenario 4; preserved metadata from {probe.Metadata.Source}; base {bounds.Width}x{bounds.Height}, gain {gainBounds.Width}x{gainBounds.Height}; gain max {outputProbe.Metadata?.GainMapMax ?? "?"}, capacity {outputProbe.Metadata?.HdrCapacityMin ?? "?"}-{outputProbe.Metadata?.HdrCapacityMax ?? "?"}";
        }
        finally
        {
            TryDeleteFile(basePath);
            TryDeleteFile(gainPath);
            TryDeleteFile(metadataPath);
            TryDeleteFile(candidateOutput);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<IHdrSceneSource> CreateSourceAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        if (document.HasRenderableGainMap)
        {
            var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, cancellationToken);
            return new GainMapSceneSource(inputs);
        }

        var bitmap = await BitmapDecodeService.DecodeDocumentForHdrExportAsync(document, cancellationToken);
        if (!bitmap.IsHdrEncoded)
        {
            throw new InvalidOperationException("当前图片不是可重建的 HDR 源，无法生成 Ultra HDR gain-map。需要 gain-map JPEG 或 HLG/PQ/linear HDR 单层源。");
        }

        return new BitmapSceneSource(bitmap);
    }

    private static async Task WriteRgbaHalfFloatRawAsync(
        string rawPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        var width = checked((int)bounds.Width);
        var height = checked((int)bounds.Height);
        var rowStride = checked(width * 8);
        var batchRowCount = Math.Clamp((512 * 1024) / Math.Max(rowStride, 8), 1, Math.Max(height, 1));
        var batchBuffer = ArrayPool<byte>.Shared.Rent(checked(batchRowCount * rowStride));
        var whiteScale = AppReferenceWhiteNits / UltraHdrReferenceWhiteNits;

        try
        {
            await using var stream = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
            for (var batchStartY = 0; batchStartY < height; batchStartY += batchRowCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentRows = Math.Min(batchRowCount, height - batchStartY);
                var startY = batchStartY;

                Parallel.For(0, currentRows, new ParallelOptions { CancellationToken = cancellationToken }, localRow =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = localRow * rowStride;
                    var sourceY = checked((int)bounds.Y + startY + localRow);
                    for (var x = 0; x < width; x++)
                    {
                        var sourceX = checked((int)bounds.X + x);
                        var scene = Vector3.Max(Vector3.Zero, source.ReadSceneLinearBt2020(sourceX, sourceY)) * whiteScale;
                        WriteHalfLittleEndian(batchBuffer, offset, scene.X);
                        WriteHalfLittleEndian(batchBuffer, offset + 2, scene.Y);
                        WriteHalfLittleEndian(batchBuffer, offset + 4, scene.Z);
                        WriteHalfLittleEndian(batchBuffer, offset + 6, 1.0f);
                        offset += 8;
                    }
                });

                await stream.WriteAsync(batchBuffer.AsMemory(0, currentRows * rowStride), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batchBuffer);
        }
    }

    private static async Task RunUltraHdrEncodeAsync(
        string cli,
        string rawPath,
        string outputPath,
        BitmapBounds bounds,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        using var process = CreateUltraHdrProcess(cli);
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(rawPath);
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add(bounds.Width.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(bounds.Height.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(RgbaHalfFloatFormat.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(Bt2100Gamut.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(ToUltraHdrGamutCode(options.SdrBaseColorGamut).ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(LinearTransfer.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-R");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-q");
        process.StartInfo.ArgumentList.Add("95");
        process.StartInfo.ArgumentList.Add("-Q");
        process.StartInfo.ArgumentList.Add("95");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-M");
        process.StartInfo.ArgumentList.Add(options.GainMapChannelMode == UltraHdrGainMapChannelMode.Rgb ? "1" : "0");
        process.StartInfo.ArgumentList.Add("-D");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-L");
        process.StartInfo.ArgumentList.Add("1000");
        process.StartInfo.ArgumentList.Add("-z");
        process.StartInfo.ArgumentList.Add(outputPath);

        await RunProcessAsync(process, cancellationToken);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException("libultrahdr 编码结束但没有生成有效图像文件。");
        }
    }

    private static string DescribeGainMapChannelMode(UltraHdrGainMapChannelMode mode)
    {
        return mode == UltraHdrGainMapChannelMode.Rgb ? "RGB" : "monochrome";
    }

    public static UltraHdrSdrBaseColorGamut ResolveSdrBaseColorGamut(
        UltraHdrSdrBaseColorGamut requested,
        HdrImageDocument document)
    {
        _ = requested;
        _ = document;
        return UltraHdrSdrBaseColorGamut.DisplayP3;
    }

    public static string DescribeSdrBaseGamut(UltraHdrSdrBaseColorGamut gamut)
    {
        return gamut switch
        {
            UltraHdrSdrBaseColorGamut.Auto => "Auto",
            UltraHdrSdrBaseColorGamut.DisplayP3 => "Display P3",
            UltraHdrSdrBaseColorGamut.Bt2100 => "BT.2020 / Rec.2100",
            _ => "BT.709 / sRGB",
        };
    }

    private static int ToUltraHdrGamutCode(UltraHdrSdrBaseColorGamut gamut)
    {
        return gamut switch
        {
            UltraHdrSdrBaseColorGamut.DisplayP3 => DisplayP3Gamut,
            UltraHdrSdrBaseColorGamut.Bt2100 => Bt2100Gamut,
            _ => Bt709Gamut,
        };
    }

    private static async Task RunUltraHdrScenario4Async(
        string cli,
        string basePath,
        string gainPath,
        string metadataPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = CreateUltraHdrProcess(cli);
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(basePath);
        process.StartInfo.ArgumentList.Add("-g");
        process.StartInfo.ArgumentList.Add(gainPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(metadataPath);
        process.StartInfo.ArgumentList.Add("-z");
        process.StartInfo.ArgumentList.Add(outputPath);

        await RunProcessAsync(process, cancellationToken);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException("libultrahdr scenario 4 结束但没有生成有效图像文件。");
        }
    }

    private static BitmapBounds MapBounds(BitmapBounds bounds, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var x0 = (long)bounds.X * targetWidth / Math.Max(sourceWidth, 1);
        var y0 = (long)bounds.Y * targetHeight / Math.Max(sourceHeight, 1);
        var x1 = ((long)(bounds.X + bounds.Width) * targetWidth + sourceWidth - 1) / Math.Max(sourceWidth, 1);
        var y1 = ((long)(bounds.Y + bounds.Height) * targetHeight + sourceHeight - 1) / Math.Max(sourceHeight, 1);
        var x = (uint)Math.Clamp(x0, 0, Math.Max(targetWidth - 1, 0));
        var y = (uint)Math.Clamp(y0, 0, Math.Max(targetHeight - 1, 0));
        var width = (uint)Math.Clamp(x1 - x0, 1, Math.Max(targetWidth - x, 1));
        var height = (uint)Math.Clamp(y1 - y0, 1, Math.Max(targetHeight - y, 1));
        return new BitmapBounds { X = x, Y = y, Width = width, Height = height };
    }

    private static async Task EncodeDecodedBitmapJpegCropAsync(
        DecodedBitmap bitmap,
        BitmapBounds bounds,
        ExifOrientationTransform orientation,
        string outputPath,
        BitmapPixelFormat outputFormat,
        CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(() => ConvertCropToBgra8(bitmap, bounds, orientation, cancellationToken), cancellationToken);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, useAsync: true);
        using var randomAccess = output.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccess);
        encoder.SetPixelData(
            outputFormat,
            BitmapAlphaMode.Ignore,
            bounds.Width,
            bounds.Height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte[] ConvertCropToBgra8(DecodedBitmap bitmap, BitmapBounds bounds, ExifOrientationTransform orientation, CancellationToken cancellationToken)
    {
        var width = checked((int)bounds.Width);
        var height = checked((int)bounds.Height);
        var result = new byte[checked(width * height * 4)];

        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orientedY = checked((int)bounds.Y + y);
            var destination = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var (sourceX, sourceY) = orientation.MapToSource(checked((int)bounds.X + x), orientedY);
                var rgb = ReadEncodedRgb(bitmap, sourceX, sourceY);
                result[destination] = ToByte(rgb.Z);
                result[destination + 1] = ToByte(rgb.Y);
                result[destination + 2] = ToByte(rgb.X);
                result[destination + 3] = 255;
                destination += 4;
            }
        });

        return result;
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0.0f, 255.0f);
    }

    private static async Task WriteMetadataConfigAsync(
        GainMapMetadata metadata,
        string path,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"--maxContentBoost {ToLinearBoost(metadata.GainMapMax ?? metadata.HdrCapacityMax, 2.0f).ToString("0.########", CultureInfo.InvariantCulture)}",
            $"--minContentBoost {ToLinearBoost(metadata.GainMapMin ?? metadata.HdrCapacityMin, 1.0f).ToString("0.########", CultureInfo.InvariantCulture)}",
            $"--gamma {NormalizeMetadataNumberList(metadata.Gamma, "1")}",
            $"--offsetSdr {NormalizeMetadataNumberList(metadata.OffsetSdr, "0")}",
            $"--offsetHdr {NormalizeMetadataNumberList(metadata.OffsetHdr, "0")}",
            $"--hdrCapacityMin {ToLinearBoost(metadata.HdrCapacityMin, 1.0f).ToString("0.########", CultureInfo.InvariantCulture)}",
            $"--hdrCapacityMax {ToLinearBoost(metadata.HdrCapacityMax ?? metadata.GainMapMax, 2.0f).ToString("0.########", CultureInfo.InvariantCulture)}",
            "--useBaseColorSpace 1",
        };
        await File.WriteAllLinesAsync(path, lines, cancellationToken);
    }

    private static float ToLinearBoost(string? stopValue, float fallback)
    {
        var value = ParseFirstFloat(stopValue);
        if (value is null)
        {
            return fallback;
        }

        return MathF.Pow(2.0f, value.Value);
    }

    private static string NormalizeMetadataNumberList(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString("0.########", CultureInfo.InvariantCulture)
                : part)
            .ToArray();
        return parts.Length == 0 ? fallback : string.Join(" ", parts);
    }

    private static float? ParseFirstFloat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var part in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static Process CreateUltraHdrProcess(string cli)
    {
        return NativeProcessRunner.Create(cli);
    }

    private static Task RunProcessAsync(Process process, CancellationToken cancellationToken)
    {
        return NativeProcessRunner.RunAsync(process, $"libultrahdr ({process.StartInfo.FileName})", cancellationToken);
    }

    private static string? FindUltraHdrAppExecutable()
    {
        return NativeToolLocator.FindFirstTool("ultrahdr_app.exe", "uhdr_app.exe", "ultrahdr.exe");
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

    private static void WriteHalfLittleEndian(byte[] data, int offset, float value)
    {
        var bits = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(value, 0.0f, 10000.0f / UltraHdrReferenceWhiteNits));
        data[offset] = (byte)bits;
        data[offset + 1] = (byte)(bits >> 8);
    }

    private interface IHdrSceneSource : IDisposable
    {
        int Width { get; }

        int Height { get; }

        string Description { get; }

        Vector3 ReadSceneLinearBt2020(int x, int y);
    }

    private sealed class BitmapSceneSource(DecodedBitmap bitmap) : IHdrSceneSource
    {
        public int Width => bitmap.PixelWidth;

        public int Height => bitmap.PixelHeight;

        public string Description => $"single-layer source {bitmap.RenderEncodingSummary}";

        public Vector3 ReadSceneLinearBt2020(int x, int y)
        {
            var encoded = ReadEncodedRgb(bitmap, x, y);
            var scene = bitmap.Transfer switch
            {
                DecodedBitmapTransfer.Pq => HdrColorMath.PqToSceneLinear(encoded),
                DecodedBitmapTransfer.Hlg => HdrColorMath.HlgToSceneLinear(encoded, DefaultHlgPeakNits / AppReferenceWhiteNits),
                DecodedBitmapTransfer.LinearScRgb => encoded,
                DecodedBitmapTransfer.LinearSceneScRgb => encoded,
                _ => bitmap.ColorManagedToSrgb
                    ? HdrColorMath.SrgbToLinear(encoded)
                    : HdrColorMath.DecodeSdrEncodedToLinear(
                        encoded,
                        bitmap.EffectiveColorGamut,
                        bitmap.Transfer == DecodedBitmapTransfer.Bt709
                            ? HdrColorMath.GainMapBaseTransferBt709
                            : HdrColorMath.GainMapBaseTransferSrgb),
            };

            var gamut = bitmap.Transfer is DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb
                && !bitmap.UsesBt2020Primaries
                ? GainMapColorGamut.Bt709
                : bitmap.ColorManagedToSrgb
                    ? GainMapColorGamut.Bt709
                    : bitmap.EffectiveColorGamut;
            return HdrColorMath.ConvertLinearToBt2020(scene, gamut);
        }

        public void Dispose()
        {
        }
    }

    private sealed class GainMapSceneSource(GainMapRenderInputs inputs) : IHdrSceneSource
    {
        private readonly ExifOrientationTransform _orientation = new(
            inputs.Primary.PixelWidth, inputs.Primary.PixelHeight, (int)inputs.Constants.Orientation.X);

        public int Width => _orientation.Width;

        public int Height => _orientation.Height;

        public string Description => $"gain-map source base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}";

        public Vector3 ReadSceneLinearBt2020(int x, int y)
        {
            (x, y) = _orientation.MapToSource(x, y);
            var sdr = HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(inputs.Primary, x, y), inputs.Constants);
            var gain = ReadGainMapSample(inputs.GainMap, x, y, inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
            var scene = inputs.Constants.GainMapControl.Y > 0.5f
                ? HdrColorMath.ReconstructAppleHdrSample(sdr, gain, inputs.Constants.GainMapMax.X, 1.0f)
                : HdrColorMath.ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, 1.0f);
            if (inputs.Constants.GainMapControl.Y <= 0.5f)
            {
                scene *= UltraHdrReferenceWhiteNits / AppReferenceWhiteNits;
            }

            return HdrColorMath.ConvertGainMapBaseToBt2020(scene, inputs.Constants);
        }

        public void Dispose()
        {
        }
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

    private static Vector3 ReadGainMapSample(DecodedBitmap bitmap, int primaryX, int primaryY, int primaryWidth, int primaryHeight)
    {
        var x = Math.Clamp((int)((primaryX + 0.5f) * bitmap.PixelWidth / Math.Max(primaryWidth, 1)), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)((primaryY + 0.5f) * bitmap.PixelHeight / Math.Max(primaryHeight, 1)), 0, bitmap.PixelHeight - 1);
        return ReadEncodedRgb(bitmap, x, y);
    }

    private static float ReadHalfLittleEndian(byte[] data, int offset)
    {
        var bits = unchecked((ushort)(data[offset] | (data[offset + 1] << 8)));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }
}
