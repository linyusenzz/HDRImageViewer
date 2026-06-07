using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HdrImageViewer.Services;

public sealed record GainMapHdrExportCapability(
    bool CanWriteJpegUltraHdr,
    string Backend,
    string Details);

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
                $"found {cli}");
        }

        return new GainMapHdrExportCapability(
            false,
            "libultrahdr planned",
            "未找到 libultrahdr CLI。请运行 eng\\verify-codecs.ps1 -RepairUltraHdr，或把 ultrahdr_app.exe 放到 external\\encoders\\x64。");
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return await ExportJpegUltraHdrCoreAsync(document, bounds, outputPath, UltraHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return await ExportJpegUltraHdrCoreAsync(document, bounds, outputPath, options, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return await ExportJpegUltraHdrCoreAsync(document, bounds: null, outputPath, UltraHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportJpegUltraHdrAsync(
        HdrImageDocument document,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return await ExportJpegUltraHdrCoreAsync(document, bounds: null, outputPath, options, cancellationToken);
    }

    private static async Task<string> ExportJpegUltraHdrCoreAsync(
        HdrImageDocument document,
        BitmapBounds? bounds,
        string outputPath,
        UltraHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        var cli = FindUltraHdrAppExecutable()
            ?? throw new NotSupportedException(GetCapability().Details);

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
        if (exportBounds.X + exportBounds.Width > source.Width || exportBounds.Y + exportBounds.Height > source.Height)
        {
            throw new InvalidOperationException($"裁切区域超出 HDR 源尺寸: {exportBounds.X},{exportBounds.Y} {exportBounds.Width}x{exportBounds.Height}, source {source.Width}x{source.Height}。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "ultrahdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var rawPath = Path.Combine(tempDir, "hdr-rgba-f16.raw");
        var candidateOutput = Path.Combine(tempDir, "crop-ultrahdr.jpg");
        var metadataPath = Path.Combine(tempDir, "metadata.cfg");

        try
        {
            await WriteRgbaHalfFloatRawAsync(rawPath, source, exportBounds, cancellationToken);
            await RunUltraHdrEncodeAsync(cli, rawPath, candidateOutput, exportBounds, resolvedOptions, cancellationToken);
            await VerifyUltraHdrAsync(cli, candidateOutput, metadataPath, cancellationToken);
            MoveReplacing(candidateOutput, outputPath);
            var probe = await GainMapJpegProbe.ProbeAsync(outputPath, cancellationToken);
            if (!probe.IsRenderableUltraHdr)
            {
                TryDeleteFile(outputPath);
                throw new InvalidOperationException($"libultrahdr 已生成 JPEG，但反查未检测到可渲染 gain-map: {probe.DisplayStatus}");
            }

            var metadataSummary = probe.Metadata is null
                ? "metadata unread"
                : $"gain max {probe.Metadata.GainMapMax ?? "?"}, capacity {probe.Metadata.HdrCapacityMin ?? "?"}-{probe.Metadata.HdrCapacityMax ?? "?"}";
            return $"libultrahdr v1.4 CLI; {source.Description}; {DescribeGainMapChannelMode(resolvedOptions.GainMapChannelMode)} gain map; SDR base {DescribeSdrBaseGamut(resolvedOptions.SdrBaseColorGamut)}; {exportBounds.Width}x{exportBounds.Height}; {metadataSummary}";
        }
        finally
        {
            TryDeleteFile(rawPath);
            TryDeleteFile(candidateOutput);
            TryDeleteFile(metadataPath);
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

        var container = await File.ReadAllBytesAsync(document.Path, cancellationToken);
        var primaryBytes = container.AsSpan(0, checked((int)probe.PrimaryImageEndOffset!.Value)).ToArray();
        var gainMapBytes = container.AsSpan(checked((int)probe.GainMapOffset!.Value), probe.GainMapLength!.Value).ToArray();
        var primary = await BitmapDecodeService.DecodeBytesAsync(primaryBytes, colorManageToSrgb: true, respectExifOrientation: false, cancellationToken);
        var gainMap = await BitmapDecodeService.DecodeBytesAsync(gainMapBytes, colorManageToSrgb: false, respectExifOrientation: false, cancellationToken);
        if (bounds.X + bounds.Width > primary.PixelWidth || bounds.Y + bounds.Height > primary.PixelHeight)
        {
            throw new InvalidOperationException($"裁切区域超出 base 尺寸: {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}, source {primary.PixelWidth}x{primary.PixelHeight}。");
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
            var gainBounds = MapBounds(bounds, primary.PixelWidth, primary.PixelHeight, gainMap.PixelWidth, gainMap.PixelHeight);
            await EncodeDecodedBitmapJpegCropAsync(primary, bounds, basePath, BitmapPixelFormat.Bgra8, cancellationToken);
            await EncodeDecodedBitmapJpegCropAsync(gainMap, gainBounds, gainPath, BitmapPixelFormat.Bgra8, cancellationToken);
            await WriteMetadataConfigAsync(probe.Metadata, metadataPath, cancellationToken);
            await RunUltraHdrScenario4Async(cli, basePath, gainPath, metadataPath, candidateOutput, cancellationToken);
            await VerifyUltraHdrAsync(cli, candidateOutput, Path.Combine(tempDir, "verify-metadata.cfg"), cancellationToken);
            MoveReplacing(candidateOutput, outputPath);

            var outputProbe = await GainMapJpegProbe.ProbeAsync(outputPath, cancellationToken);
            if (!outputProbe.IsRenderableUltraHdr)
            {
                TryDeleteFile(outputPath);
                throw new InvalidOperationException($"保真裁切已封装 JPEG，但反查未检测到可渲染 gain-map: {outputProbe.DisplayStatus}");
            }

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
        await using var stream = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var row = new byte[checked((int)bounds.Width * 8)];
        var whiteScale = AppReferenceWhiteNits / UltraHdrReferenceWhiteNits;
        for (var y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            var sourceY = checked((int)bounds.Y + y);
            for (var x = 0; x < bounds.Width; x++)
            {
                var sourceX = checked((int)bounds.X + x);
                var scene = Vector3.Max(Vector3.Zero, source.ReadSceneLinearBt2020(sourceX, sourceY)) * whiteScale;
                WriteHalfLittleEndian(row, offset, scene.X);
                WriteHalfLittleEndian(row, offset + 2, scene.Y);
                WriteHalfLittleEndian(row, offset + 4, scene.Z);
                WriteHalfLittleEndian(row, offset + 6, 1.0f);
                offset += 8;
            }

            await stream.WriteAsync(row.AsMemory(0, offset), cancellationToken);
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
        process.StartInfo.ArgumentList.Add(bounds.Width.ToString());
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(bounds.Height.ToString());
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(RgbaHalfFloatFormat.ToString());
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(Bt2100Gamut.ToString());
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(ToUltraHdrGamutCode(options.SdrBaseColorGamut).ToString());
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(LinearTransfer.ToString());
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
            throw new InvalidOperationException("libultrahdr 编码结束但没有生成有效 JPEG 文件。");
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
        if (requested != UltraHdrSdrBaseColorGamut.Auto)
        {
            return requested;
        }

        return document.Format.Kind == HdrImageKind.SingleLayerHdr
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.JxlProbe?.IsHdrTransfer == true
                ? UltraHdrSdrBaseColorGamut.Bt2100
                : UltraHdrSdrBaseColorGamut.Bt709;
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
            throw new InvalidOperationException("libultrahdr scenario 4 结束但没有生成有效 JPEG 文件。");
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
        string outputPath,
        BitmapPixelFormat outputFormat,
        CancellationToken cancellationToken)
    {
        var pixels = ConvertCropToBgra8(bitmap, bounds);
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

    private static byte[] ConvertCropToBgra8(DecodedBitmap bitmap, BitmapBounds bounds)
    {
        var result = new byte[checked((int)bounds.Width * (int)bounds.Height * 4)];
        var destination = 0;
        for (var y = 0; y < bounds.Height; y++)
        {
            var sourceY = checked((int)bounds.Y + y);
            for (var x = 0; x < bounds.Width; x++)
            {
                var sourceX = checked((int)bounds.X + x);
                var rgb = ReadEncodedRgb(bitmap, sourceX, sourceY);
                result[destination++] = ToByte(rgb.Z);
                result[destination++] = ToByte(rgb.Y);
                result[destination++] = ToByte(rgb.X);
                result[destination++] = 255;
            }
        }

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

    private static async Task VerifyUltraHdrAsync(
        string cli,
        string outputPath,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        using var process = CreateUltraHdrProcess(cli);
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-j");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(metadataPath);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(LinearTransfer.ToString());
        process.StartInfo.ArgumentList.Add("-O");
        process.StartInfo.ArgumentList.Add(RgbaHalfFloatFormat.ToString());
        process.StartInfo.ArgumentList.Add("-z");
        process.StartInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(metadataPath)!, "verify-rgba-f16.raw"));

        await RunProcessAsync(process, cancellationToken);
        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length == 0)
        {
            throw new InvalidOperationException("libultrahdr 反查成功但未写出 gain-map metadata，导出结果不可信。");
        }
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

    private static void MoveReplacing(string source, string destination)
    {
        TryDeleteFile(destination);
        File.Move(source, destination);
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
            var scene = bitmap.Transfer switch
            {
                DecodedBitmapTransfer.Pq => PqToSceneLinear(ReadEncodedRgb(bitmap, x, y)),
                DecodedBitmapTransfer.Hlg => HlgToSceneLinear(ReadEncodedRgb(bitmap, x, y), DefaultHlgPeakNits / AppReferenceWhiteNits),
                DecodedBitmapTransfer.LinearScRgb => ReadEncodedRgb(bitmap, x, y),
                DecodedBitmapTransfer.LinearSceneScRgb => ReadEncodedRgb(bitmap, x, y),
                _ => ReadLinearSrgb(bitmap, x, y),
            };

            return bitmap.UsesBt2020Primaries
                ? scene
                : Bt709ToBt2020(scene);
        }

        public void Dispose()
        {
        }
    }

    private sealed class GainMapSceneSource(GainMapRenderInputs inputs) : IHdrSceneSource
    {
        public int Width => inputs.Primary.PixelWidth;

        public int Height => inputs.Primary.PixelHeight;

        public string Description => $"gain-map source base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}";

        public Vector3 ReadSceneLinearBt2020(int x, int y)
        {
            var sdr = HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(inputs.Primary, x, y), inputs.Constants);
            var gain = ReadGainMapSample(inputs.GainMap, x, y, inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
            var scene = inputs.Constants.GainMapControl.Y > 0.5f
                ? ReconstructAppleHdrSample(sdr, gain, inputs.Constants.GainMapMax.X, 1.0f)
                : ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, 1.0f);
            if (inputs.Constants.GainMapControl.Y <= 0.5f)
            {
                scene *= UltraHdrReferenceWhiteNits / AppReferenceWhiteNits;
            }

            return ConvertGainMapBaseToBt2020(scene, inputs.Constants);
        }

        public void Dispose()
        {
        }
    }

    private static Vector3 ReconstructAdobeHdrSample(Vector3 sdr, Vector3 gain, GainMapShaderConstants constants, float weight)
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
            ReconstructHdrChannel(sdr.X, constants.OffsetSdr.X, constants.OffsetHdr.X, logBoost.X, weight),
            ReconstructHdrChannel(sdr.Y, constants.OffsetSdr.Y, constants.OffsetHdr.Y, logBoost.Y, weight),
            ReconstructHdrChannel(sdr.Z, constants.OffsetSdr.Z, constants.OffsetHdr.Z, logBoost.Z, weight));
    }

    private static float ReconstructHdrChannel(float sdr, float offsetSdr, float offsetHdr, float logBoost, float weight)
    {
        return Math.Max(0.0f, ((sdr + offsetSdr) * MathF.Pow(2.0f, logBoost * weight)) - offsetHdr);
    }

    private static Vector3 ReconstructAppleHdrSample(Vector3 sdr, Vector3 gain, float headroom, float weight)
    {
        var linearGain = new Vector3(
            Rec709ToLinear(Math.Clamp(gain.X, 0.0f, 1.0f)),
            Rec709ToLinear(Math.Clamp(gain.Y, 0.0f, 1.0f)),
            Rec709ToLinear(Math.Clamp(gain.Z, 0.0f, 1.0f)));
        var effectiveHeadroom = MathF.Pow(Math.Max(headroom, 1.0f), Math.Clamp(weight, 0.0f, 1.0f));
        return sdr * (Vector3.One + ((effectiveHeadroom - 1.0f) * linearGain));
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

    private static Vector3 ReadLinearSrgb(DecodedBitmap bitmap, int x, int y)
    {
        var encoded = ReadEncodedRgb(bitmap, x, y);
        return new Vector3(SrgbToLinear(encoded.X), SrgbToLinear(encoded.Y), SrgbToLinear(encoded.Z));
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

    private static Vector3 PqToSceneLinear(Vector3 encoded)
    {
        return new Vector3(PqToSceneLinearChannel(encoded.X), PqToSceneLinearChannel(encoded.Y), PqToSceneLinearChannel(encoded.Z));
    }

    private static float PqToSceneLinearChannel(float value)
    {
        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 32.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 128.0f;
        const float c3 = 2392.0f / 128.0f;
        var y = MathF.Pow(Math.Max(value, 0.0f), 1.0f / m2);
        var nits = 10000.0f * MathF.Pow(Math.Max((y - c1) / Math.Max(c2 - (c3 * y), 0.000001f), 0.0f), 1.0f / m1);
        return nits / AppReferenceWhiteNits;
    }

    private static Vector3 HlgToSceneLinear(Vector3 encoded, float targetScenePeak)
    {
        var hlgScene = new Vector3(
            HlgToSceneLinearChannel(Math.Clamp(encoded.X, 0.0f, 1.0f)),
            HlgToSceneLinearChannel(Math.Clamp(encoded.Y, 0.0f, 1.0f)),
            HlgToSceneLinearChannel(Math.Clamp(encoded.Z, 0.0f, 1.0f)));
        var gamma = CalculateHlgSystemGamma(targetScenePeak);
        var hlgLuma = Math.Max((0.2627f * hlgScene.X) + (0.6780f * hlgScene.Y) + (0.0593f * hlgScene.Z), 0.000001f);
        return hlgScene * MathF.Pow(hlgLuma, gamma - 1.0f) * targetScenePeak;
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

    private static float CalculateHlgSystemGamma(float targetScenePeak)
    {
        var targetNits = Math.Max(targetScenePeak * AppReferenceWhiteNits, 100.0f);
        return Math.Clamp(1.2f + (0.42f * MathF.Log10(targetNits / 1000.0f)), 1.0f, 1.35f);
    }

    private static Vector3 Bt709ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.6274040f * value.X) + (0.3292820f * value.Y) + (0.0433136f * value.Z),
            (0.0690970f * value.X) + (0.9195400f * value.Y) + (0.0113612f * value.Z),
            (0.0163916f * value.X) + (0.0880132f * value.Y) + (0.8955951f * value.Z));
    }

    private static Vector3 P3ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.753833f * value.X) + (0.198597f * value.Y) + (0.047570f * value.Z),
            (0.045744f * value.X) + (0.941777f * value.Y) + (0.012479f * value.Z),
            (-0.001210f * value.X) + (0.017601f * value.Y) + (0.983608f * value.Z));
    }

    private static Vector3 ConvertGainMapBaseToBt2020(Vector3 value, GainMapShaderConstants constants)
    {
        return constants.GainMapControl.Z switch
        {
            > 1.5f => value,
            > 0.5f => P3ToBt2020(value),
            _ => Bt709ToBt2020(value),
        };
    }

    private static float SrgbToLinear(float value)
    {
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static float Rec709ToLinear(float value)
    {
        return value < 0.081f
            ? value / 4.5f
            : MathF.Pow((value + 0.099f) / 1.099f, 1.0f / 0.45f);
    }
}
