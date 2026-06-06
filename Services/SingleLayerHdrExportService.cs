using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using SharpGen.Runtime;
using Vortice.WIC;
using Windows.Graphics.Imaging;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Services;

public enum SingleLayerHdrExportTransfer
{
    Pq,
    Hlg,
}

public sealed record SingleLayerHdrExportCapability(
    string DisplayName,
    string Extension,
    string Backend,
    bool IsAvailable,
    string Details);

public sealed record SingleLayerHdrExportOptions(
    float HlgPeakNits = 1000.0f,
    float GainMapWeight = 1.0f,
    bool AutoGainMapWeight = false,
    bool MatchGainMapPreview = false,
    float PreviewDisplayBoostLog2 = 0.0f,
    float PreviewSceneToSdrWhiteScale = 1.0f,
    float PreviewMaxSceneValue = 0.0f)
{
    public static SingleLayerHdrExportOptions Default { get; } = new();

    public float NormalizedHlgPeakNits => Math.Clamp(HlgPeakNits, 400.0f, 1000.0f);

    public float NormalizedGainMapWeight => Math.Clamp(GainMapWeight, 0.0f, 1.0f);

    public float NormalizedPreviewDisplayBoostLog2 => Math.Clamp(PreviewDisplayBoostLog2, 0.0f, 16.0f);

    public float NormalizedPreviewSceneToSdrWhiteScale => Math.Clamp(PreviewSceneToSdrWhiteScale, 1.0f, 128.0f);

    public float NormalizedPreviewMaxSceneValue => Math.Clamp(PreviewMaxSceneValue, 0.0f, 128.0f);
}

public static class SingleLayerHdrExportService
{
    private const float ReferenceWhiteNits = 80.0f;
    private const float UltraHdrReferenceWhiteNits = 203.0f;
    private const float DefaultHlgPeakNits = 1000.0f;
    private const int Bt2020Primaries = 9;
    private const int Bt2020NonConstantMatrix = 9;
    private const int PqTransfer = 16;
    private const int HlgTransfer = 18;

    public static IReadOnlyList<SingleLayerHdrExportCapability> GetCapabilities()
    {
        var cjxl = NativeToolLocator.FindTool("cjxl.exe");
        var avifenc = NativeToolLocator.FindTool("avifenc.exe");
        var heifEnc = NativeToolLocator.FindTool("heif-enc.exe");

        return
        [
            new SingleLayerHdrExportCapability(
                "PNG 16-bit HDR",
                ".png",
                "built-in PNG writer",
                true,
                "writes 16-bit RGB PNG with PNG cICP HLG/PQ BT.2020 metadata"),
            new SingleLayerHdrExportCapability(
                "TIFF float HDR",
                ".tif",
                "built-in TIFF writer",
                true,
                "writes uncompressed 32-bit IEEE float RGB TIFF in linear scRGB/BT.709"),
            new SingleLayerHdrExportCapability(
                "JPEG XR HDR",
                ".jxr",
                "WIC JPEG XR encoder",
                true,
                "writes FP16 linear scRGB JPEG XR through Windows WIC"),
            new SingleLayerHdrExportCapability(
                "OpenEXR",
                ".exr",
                NativeExrDecoder.IsAvailable ? "HdrImageViewer.Native OpenEXR" : "OpenEXR native backend unavailable",
                NativeExrDecoder.IsAvailable,
                NativeExrDecoder.IsAvailable
                    ? "found HdrImageViewer.Native OpenEXR bridge"
                    : "HdrImageViewer.Native.dll is missing or was built without OpenEXR support"),
            new SingleLayerHdrExportCapability(
                "JPEG XL HDR",
                ".jxl",
                cjxl is null ? "libjxl planned" : "libjxl cjxl",
                cjxl is not null,
                cjxl is null
                    ? "未找到 cjxl.exe。放到 external/encoders/x64，或安装到 MSYS2/PATH 后启用。"
                    : $"found {cjxl}"),
            new SingleLayerHdrExportCapability(
                "AVIF HDR",
                ".avif",
                avifenc is null ? "libavif planned" : "libavif avifenc",
                avifenc is not null,
                avifenc is null
                    ? "未找到 avifenc.exe。放到 external/encoders/x64，或安装到 MSYS2/PATH 后启用。"
                    : $"found {avifenc}"),
            new SingleLayerHdrExportCapability(
                "HEIF/HEIC HDR",
                ".heic",
                heifEnc is null ? "libheif planned" : "libheif heif-enc",
                heifEnc is not null,
                heifEnc is null
                    ? "未找到 heif-enc.exe。放到 external/encoders/x64，或安装到 MSYS2/PATH 后启用。"
                    : $"found {heifEnc}"),
        ];
    }

    public static float? EstimateAutoGainMapWeight(
        HdrImageDocument document,
        SingleLayerHdrExportTransfer transfer,
        float hlgPeakNits)
    {
        var metadata = document.GainMapProbe?.Metadata;
        if (metadata is null)
        {
            return null;
        }

        var targetNits = transfer == SingleLayerHdrExportTransfer.Hlg
            ? Math.Clamp(hlgPeakNits, 400.0f, DefaultHlgPeakNits)
            : DefaultHlgPeakNits;
        var targetReferenceWhite = metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal)
            ? ReferenceWhiteNits
            : UltraHdrReferenceWhiteNits;
        var targetStops = MathF.Log2(Math.Max(targetNits, targetReferenceWhite) / targetReferenceWhite);
        var minCapacity = ParseGainMapScalar(metadata.HdrCapacityMin, 0.0f);
        var maxCapacity = ParseGainMapScalar(
            metadata.HdrCapacityMax,
            metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal)
                ? MathF.Log2(Math.Max(ParseGainMapScalar(metadata.GainMapMax, 2.0f), 1.0f))
                : Math.Max(minCapacity + 1.0f, ParseGainMapScalar(metadata.GainMapMax, 1.0f)));

        if (maxCapacity > minCapacity)
        {
            return Math.Clamp((targetStops - minCapacity) / (maxCapacity - minCapacity), 0.0f, 1.0f);
        }

        var encodedMax = metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal)
            ? MathF.Log2(Math.Max(ParseGainMapScalar(metadata.GainMapMax, 2.0f), 1.0f))
            : ParseGainMapVectorMax(metadata.GainMapMax, 1.0f);
        return encodedMax > 0.0001f
            ? Math.Clamp(targetStops / encodedMax, 0.0f, 1.0f)
            : null;
    }

    public static float? EstimatePreviewGainMapWeight(
        HdrImageDocument document,
        float displayBoostLog2)
    {
        var metadata = document.GainMapProbe?.Metadata;
        if (metadata is null)
        {
            return null;
        }

        var minCapacity = ParseGainMapScalar(metadata.HdrCapacityMin, 0.0f);
        var maxCapacity = ParseGainMapScalar(
            metadata.HdrCapacityMax,
            metadata.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal)
                ? MathF.Log2(Math.Max(ParseGainMapScalar(metadata.GainMapMax, 2.0f), 1.0f))
                : Math.Max(minCapacity + 1.0f, ParseGainMapVectorMax(metadata.GainMapMax, 1.0f)));

        if (maxCapacity > minCapacity)
        {
            return Math.Clamp((displayBoostLog2 - minCapacity) / (maxCapacity - minCapacity), 0.0f, 1.0f);
        }

        return null;
    }

    public static async Task<string> ExportAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds, outputPath, transfer, SingleLayerHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportAsync(
        HdrImageDocument document,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds: null, outputPath, transfer, SingleLayerHdrExportOptions.Default, cancellationToken);
    }

    public static async Task<string> ExportAsync(
        HdrImageDocument document,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        SingleLayerHdrExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return await ExportCoreAsync(document, bounds: null, outputPath, transfer, options, cancellationToken);
    }

    private static async Task<string> ExportCoreAsync(
        HdrImageDocument document,
        BitmapBounds? bounds,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        SingleLayerHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        var capability = GetCapabilities().FirstOrDefault(choice => string.Equals(choice.Extension, extension, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("当前单层 HDR 编码支持 PNG、TIFF、JPEG XR、EXR、JXL、AVIF 和 HEIF/HEIC。");
        if (!capability.IsAvailable)
        {
            throw new InvalidOperationException($"{capability.DisplayName} 后端不可用: {capability.Details}");
        }

        options = options with
        {
            HlgPeakNits = options.NormalizedHlgPeakNits,
            GainMapWeight = options.NormalizedGainMapWeight,
            PreviewDisplayBoostLog2 = options.NormalizedPreviewDisplayBoostLog2,
            PreviewSceneToSdrWhiteScale = options.NormalizedPreviewSceneToSdrWhiteScale,
            PreviewMaxSceneValue = options.NormalizedPreviewMaxSceneValue,
        };
        using var source = await CreateSourceAsync(document, transfer, options, cancellationToken);
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
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "single-layer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pngPath = Path.Combine(tempDir, "hdr-rgb16.png");
        var candidateOutput = Path.Combine(tempDir, "candidate" + extension);

        try
        {
            ExportLightLevelStats lightLevelStats;
            if (extension == ".jxr")
            {
                lightLevelStats = await WriteJpegXrAsync(candidateOutput, source, exportBounds, cancellationToken);
            }
            else
            {
                lightLevelStats = await WriteEncodedRgb16PngAsync(pngPath, source, exportBounds, transfer, options, cancellationToken);
                switch (extension)
                {
                    case ".png":
                        MoveReplacing(pngPath, candidateOutput);
                        break;
                    case ".tif":
                    case ".tiff":
                        await WriteFloatTiffAsync(candidateOutput, source, exportBounds, cancellationToken);
                        break;
                    case ".exr":
                        await WriteOpenExrAsync(candidateOutput, source, exportBounds, cancellationToken);
                        break;
                    case ".jxl":
                        await RunCjxlAsync(capability.Details, pngPath, candidateOutput, transfer, lightLevelStats, cancellationToken);
                        break;
                    case ".avif":
                        await RunAvifencAsync(capability.Details, pngPath, candidateOutput, transfer, lightLevelStats, cancellationToken);
                        break;
                    case ".heic":
                    case ".heif":
                        await RunHeifEncAsync(capability.Details, pngPath, candidateOutput, transfer, lightLevelStats, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("当前单层 HDR 编码支持 PNG、TIFF、JPEG XR、EXR、JXL、AVIF 和 HEIF/HEIC。");
                }
            }

            if (!File.Exists(candidateOutput) || new FileInfo(candidateOutput).Length == 0)
            {
                throw new InvalidOperationException($"{capability.Backend} 编码结束但没有生成有效文件。");
            }

            MoveReplacing(candidateOutput, outputPath);
            var jxlNote = extension == ".jxl" && transfer == SingleLayerHdrExportTransfer.Pq
                ? "; JXL PQ container intensity target is signaled by libjxl, commonly 10000 nits"
                : string.Empty;
            var transferSummary = extension == ".jxr" ? "linear scRGB FP16" : DescribeTransfer(transfer);
            return $"{capability.Backend}; {source.Description}; {transferSummary}; HLG peak {options.HlgPeakNits:0} nits; CLLI {lightLevelStats.MaxCllNits}/{lightLevelStats.MaxPallNits} nits{jxlNote}; {extension.TrimStart('.').ToUpperInvariant()}; {exportBounds.Width}x{exportBounds.Height}";
        }
        finally
        {
            TryDeleteFile(pngPath);
            TryDeleteFile(candidateOutput);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<ExportLightLevelStats> WriteJpegXrAsync(
        string outputPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() => WriteJpegXr(outputPath, source, bounds, cancellationToken), cancellationToken);
    }

    private static ExportLightLevelStats WriteJpegXr(
        string outputPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
        using var factory = new IWICImagingFactory();
        using var encoder = factory.CreateEncoder(ContainerFormatGuids.Wmp, stream, BitmapEncoderCacheOption.NoCache);
        using var frame = encoder.CreateNewFrame(out var encoderOptions);
        try
        {
            frame.Initialize(encoderOptions).CheckError();
            frame.SetSize(bounds.Width, bounds.Height).CheckError();
            frame.SetResolution(96.0, 96.0);

            var pixelFormat = WicPixelFormat.Format64bppRGBAHalf;
            frame.SetPixelFormat(ref pixelFormat);
            if (pixelFormat != WicPixelFormat.Format64bppRGBAHalf)
            {
                throw new NotSupportedException($"WIC JPEG XR encoder rejected FP16 scRGB pixel format and selected {pixelFormat}.");
            }

            var row = new byte[checked((int)bounds.Width * 8)];
            var maxPixelScene = 0.0f;
            double luminanceNitsSum = 0.0;
            long pixelCount = 0;

            for (var y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 0;
                var sourceY = checked((int)bounds.Y + y);
                for (var x = 0; x < bounds.Width; x++)
                {
                    var sourceX = checked((int)bounds.X + x);
                    var scene = ReadSceneLinearScRgb(source, sourceX, sourceY);
                    maxPixelScene = Math.Max(maxPixelScene, Math.Max(scene.X, Math.Max(scene.Y, scene.Z)));
                    luminanceNitsSum += Math.Max(0.0f, ((0.2126f * scene.X) + (0.7152f * scene.Y) + (0.0722f * scene.Z)) * ReferenceWhiteNits);
                    pixelCount++;
                    WriteHalfLittleEndian(row, offset, scene.X);
                    WriteHalfLittleEndian(row, offset + 2, scene.Y);
                    WriteHalfLittleEndian(row, offset + 4, scene.Z);
                    WriteHalfLittleEndian(row, offset + 6, 1.0f);
                    offset += 8;
                }

                frame.WritePixels<byte>(1, checked((uint)row.Length), row).CheckError();
            }

            frame.Commit();
            encoder.Commit();
            stream.Flush();

            return new ExportLightLevelStats(
                Math.Clamp((int)MathF.Ceiling(maxPixelScene * ReferenceWhiteNits), 1, 10000),
                Math.Clamp((int)Math.Ceiling(luminanceNitsSum / Math.Max(pixelCount, 1L)), 1, 10000));
        }
        finally
        {
            encoderOptions?.Dispose();
        }
    }

    private static async Task<IHdrSceneSource> CreateSourceAsync(
        HdrImageDocument document,
        SingleLayerHdrExportTransfer transfer,
        SingleLayerHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        if (document.HasRenderableGainMap)
        {
            var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, cancellationToken);
            var gainMapWeight = options.MatchGainMapPreview
                ? CalculatePreviewGainMapWeight(inputs.Constants, options)
                : options.AutoGainMapWeight
                ? CalculateAutoGainMapWeight(inputs.Constants, transfer, options)
                : options.GainMapWeight;
            return new GainMapSceneSource(inputs, gainMapWeight, options.AutoGainMapWeight, options.MatchGainMapPreview, options.PreviewSceneToSdrWhiteScale, options.PreviewMaxSceneValue);
        }

        var bitmap = await BitmapDecodeService.DecodeFileForHdrExportAsync(document.Path, document.HeifAvifProbe, cancellationToken);
        if (!bitmap.IsHdrEncoded)
        {
            throw new InvalidOperationException("当前图片不是 HDR 源，无法生成单层 HDR 文件。需要 gain-map JPEG 或 HLG/PQ/linear HDR 单层源。");
        }

        return new BitmapSceneSource(bitmap);
    }

    private static float CalculateAutoGainMapWeight(
        GainMapShaderConstants constants,
        SingleLayerHdrExportTransfer transfer,
        SingleLayerHdrExportOptions options)
    {
        var targetNits = transfer == SingleLayerHdrExportTransfer.Hlg
            ? options.NormalizedHlgPeakNits
            : DefaultHlgPeakNits;
        var targetReferenceWhite = constants.GainMapControl.Y > 0.5f
            ? ReferenceWhiteNits
            : UltraHdrReferenceWhiteNits;
        var targetStops = MathF.Log2(Math.Max(targetNits, targetReferenceWhite) / targetReferenceWhite);
        var minCapacity = constants.HdrCapacity.X;
        var maxCapacity = constants.HdrCapacity.Y;
        if (maxCapacity > minCapacity)
        {
            return Math.Clamp((targetStops - minCapacity) / (maxCapacity - minCapacity), 0.0f, 1.0f);
        }

        var encodedMax = constants.GainMapControl.Y > 0.5f
            ? MathF.Log2(Math.Max(constants.GainMapMax.X, 1.0f))
            : Math.Max(constants.GainMapMax.X, Math.Max(constants.GainMapMax.Y, constants.GainMapMax.Z));
        return encodedMax > 0.0001f
            ? Math.Clamp(targetStops / encodedMax, 0.0f, 1.0f)
            : Math.Clamp(constants.GainMapControl.X, 0.0f, 1.0f);
    }

    private static float CalculatePreviewGainMapWeight(
        GainMapShaderConstants constants,
        SingleLayerHdrExportOptions options)
    {
        var minCapacity = constants.HdrCapacity.X;
        var maxCapacity = constants.HdrCapacity.Y;
        if (maxCapacity > minCapacity)
        {
            var explicitWeight = Math.Clamp(constants.GainMapControl.X, 0.0f, 1.0f);
            var adaptiveWeight = Math.Clamp((options.PreviewDisplayBoostLog2 - minCapacity) / (maxCapacity - minCapacity), 0.0f, 1.0f);
            return explicitWeight * adaptiveWeight;
        }

        return Math.Clamp(constants.GainMapControl.X, 0.0f, 1.0f);
    }

    private static async Task<ExportLightLevelStats> WriteEncodedRgb16PngAsync(
        string pngPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        SingleLayerHdrExportTransfer transfer,
        SingleLayerHdrExportOptions options,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(pngPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, useAsync: true);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        await stream.WriteAsync(signature, cancellationToken);

        var ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, bounds.Width);
        WriteUInt32BigEndian(ihdr, 4, bounds.Height);
        ihdr[8] = 16;
        ihdr[9] = 2;
        await WritePngChunkAsync(stream, "IHDR", ihdr, cancellationToken);
        byte[] cicp = [(byte)Bt2020Primaries, (byte)GetTransferCode(transfer), 0, 1];
        await WritePngChunkAsync(stream, "cICP", cicp, cancellationToken);

        var lengthOffset = stream.Position;
        await WriteUInt32BigEndianAsync(stream, 0, cancellationToken);
        var crc = new Crc32();
        var typeBytes = Encoding.ASCII.GetBytes("IDAT");
        await stream.WriteAsync(typeBytes, cancellationToken);
        crc.Update(typeBytes);
        var dataStart = stream.Position;

        var outputPeakSceneLimit = transfer == SingleLayerHdrExportTransfer.Hlg
            ? options.HlgPeakNits / ReferenceWhiteNits
            : 10000.0f / ReferenceWhiteNits;
        var maxPixelScene = 0.0f;
        double luminanceNitsSum = 0.0;
        long pixelCount = 0;

        await using (var chunkStream = new CrcCountingStream(stream, crc))
        {
            using var deflate = new ZLibStream(chunkStream, CompressionLevel.Optimal, leaveOpen: true);
            var row = new byte[checked(1 + ((int)bounds.Width * 6))];
            var hlgTargetScenePeak = options.HlgPeakNits / ReferenceWhiteNits;
            for (var y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row[0] = 0;
                var offset = 1;
                var sourceY = checked((int)bounds.Y + y);
                for (var x = 0; x < bounds.Width; x++)
                {
                    var sourceX = checked((int)bounds.X + x);
                    var scene = Vector3.Max(Vector3.Zero, source.ReadSceneLinearBt2020(sourceX, sourceY));
                    var outputScene = Vector3.Min(scene, new Vector3(outputPeakSceneLimit));
                    maxPixelScene = Math.Max(maxPixelScene, Math.Max(outputScene.X, Math.Max(outputScene.Y, outputScene.Z)));
                    luminanceNitsSum += Math.Max(0.0f, ((0.2627f * outputScene.X) + (0.6780f * outputScene.Y) + (0.0593f * outputScene.Z)) * ReferenceWhiteNits);
                    pixelCount++;
                    var encoded = transfer == SingleLayerHdrExportTransfer.Hlg
                        ? HlgEncode(scene, hlgTargetScenePeak)
                        : PqEncode(scene);
                    WriteUInt16BigEndian(row, offset, ToUInt16(encoded.X));
                    WriteUInt16BigEndian(row, offset + 2, ToUInt16(encoded.Y));
                    WriteUInt16BigEndian(row, offset + 4, ToUInt16(encoded.Z));
                    offset += 6;
                }

                await deflate.WriteAsync(row, cancellationToken);
            }
        }

        var dataEnd = stream.Position;
        await WriteUInt32BigEndianAsync(stream, crc.Value, cancellationToken);
        stream.Position = lengthOffset;
        await WriteUInt32BigEndianAsync(stream, checked((uint)(dataEnd - dataStart)), cancellationToken);
        stream.Position = stream.Length;

        await WritePngChunkAsync(stream, "IEND", [], cancellationToken);
        return new ExportLightLevelStats(
            Math.Clamp((int)MathF.Ceiling(maxPixelScene * ReferenceWhiteNits), 1, 10000),
            Math.Clamp((int)Math.Ceiling(luminanceNitsSum / Math.Max(pixelCount, 1L)), 1, 10000));
    }

    private static async Task WriteFloatTiffAsync(
        string outputPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        const ushort shortType = 3;
        const ushort longType = 4;
        const ushort rationalType = 5;
        const ushort floatSampleFormat = 3;
        const int entryCount = 14;
        const int ifdOffset = 8;
        var ifdByteCount = 2 + (entryCount * 12) + 4;
        var extraOffset = ifdOffset + ifdByteCount;
        var extra = new byte[6 + 6 + 8 + 8];
        var bitsPerSampleOffset = extraOffset;
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(0, 2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2, 2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(4, 2), 32);
        var sampleFormatOffset = bitsPerSampleOffset + 6;
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(6, 2), floatSampleFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(8, 2), floatSampleFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(10, 2), floatSampleFormat);
        var xResolutionOffset = sampleFormatOffset + 6;
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(12, 4), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(16, 4), 1);
        var yResolutionOffset = xResolutionOffset + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(20, 4), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(24, 4), 1);
        var imageOffset = extraOffset + extra.Length;
        var imageByteCount = checked((uint)bounds.Width * bounds.Height * 3u * sizeof(float));

        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        byte[] header = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0];
        await stream.WriteAsync(header, cancellationToken);
        var ifd = new byte[ifdByteCount];
        BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(0, 2), entryCount);
        var entryIndex = 0;
        WriteTiffEntry(ifd, ref entryIndex, 256, longType, 1, bounds.Width);
        WriteTiffEntry(ifd, ref entryIndex, 257, longType, 1, bounds.Height);
        WriteTiffEntry(ifd, ref entryIndex, 258, shortType, 3, checked((uint)bitsPerSampleOffset));
        WriteTiffEntry(ifd, ref entryIndex, 259, shortType, 1, 1);
        WriteTiffEntry(ifd, ref entryIndex, 262, shortType, 1, 2);
        WriteTiffEntry(ifd, ref entryIndex, 273, longType, 1, checked((uint)imageOffset));
        WriteTiffEntry(ifd, ref entryIndex, 277, shortType, 1, 3);
        WriteTiffEntry(ifd, ref entryIndex, 278, longType, 1, bounds.Height);
        WriteTiffEntry(ifd, ref entryIndex, 279, longType, 1, imageByteCount);
        WriteTiffEntry(ifd, ref entryIndex, 282, rationalType, 1, checked((uint)xResolutionOffset));
        WriteTiffEntry(ifd, ref entryIndex, 283, rationalType, 1, checked((uint)yResolutionOffset));
        WriteTiffEntry(ifd, ref entryIndex, 284, shortType, 1, 1);
        WriteTiffEntry(ifd, ref entryIndex, 296, shortType, 1, 2);
        WriteTiffEntry(ifd, ref entryIndex, 339, shortType, 3, checked((uint)sampleFormatOffset));
        await stream.WriteAsync(ifd, cancellationToken);
        await stream.WriteAsync(extra, cancellationToken);

        var row = new byte[checked((int)bounds.Width * 3 * sizeof(float))];
        for (var y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            var sourceY = checked((int)bounds.Y + y);
            for (var x = 0; x < bounds.Width; x++)
            {
                var sourceX = checked((int)bounds.X + x);
                var scene = ReadSceneLinearScRgb(source, sourceX, sourceY);
                BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(offset, 4), scene.X);
                BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(offset + 4, 4), scene.Y);
                BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(offset + 8, 4), scene.Z);
                offset += 12;
            }

            await stream.WriteAsync(row, cancellationToken);
        }
    }

    private static async Task WriteOpenExrAsync(
        string outputPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        if (!NativeExrDecoder.IsAvailable)
        {
            throw new NotSupportedException("HdrImageViewer.Native OpenEXR backend is not available.");
        }

        var pixels = await Task.Run(() => CreateRgba16FScRgbPixels(source, bounds, cancellationToken), cancellationToken);
        NativeExrDecoder.Encode(outputPath, checked((int)bounds.Width), checked((int)bounds.Height), pixels);
    }

    private static byte[] CreateRgba16FScRgbPixels(
        IHdrSceneSource source,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        var pixels = new byte[checked((int)bounds.Width * (int)bounds.Height * 8)];
        var offset = 0;
        for (var y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = checked((int)bounds.Y + y);
            for (var x = 0; x < bounds.Width; x++)
            {
                var sourceX = checked((int)bounds.X + x);
                var scene = ReadSceneLinearScRgb(source, sourceX, sourceY);
                WriteHalfLittleEndian(pixels, offset, scene.X);
                WriteHalfLittleEndian(pixels, offset + 2, scene.Y);
                WriteHalfLittleEndian(pixels, offset + 4, scene.Z);
                WriteHalfLittleEndian(pixels, offset + 6, 1.0f);
                offset += 8;
            }
        }

        return pixels;
    }

    private static Vector3 ReadSceneLinearScRgb(IHdrSceneSource source, int x, int y)
    {
        return HdrColorMath.Bt2020ToBt709(source.ReadSceneLinearBt2020(x, y));
    }

    private static void WriteTiffEntry(byte[] ifd, ref int entryIndex, ushort tag, ushort type, uint count, uint value)
    {
        var offset = 2 + (entryIndex++ * 12);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(offset + 4, 4), count);
        if (type == 3 && count == 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(offset + 8, 2), checked((ushort)value));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(offset + 8, 4), value);
        }
    }

    private static async Task RunCjxlAsync(
        string details,
        string inputPath,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        ExportLightLevelStats lightLevelStats,
        CancellationToken cancellationToken)
    {
        using var process = NativeProcessRunner.Create(ExtractFoundPath(details));
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add("--distance=1.0");
        process.StartInfo.ArgumentList.Add("--effort=7");
        process.StartInfo.ArgumentList.Add("-x");
        process.StartInfo.ArgumentList.Add(transfer == SingleLayerHdrExportTransfer.Hlg
            ? "color_space=RGB_D65_202_Rel_HLG"
            : "color_space=RGB_D65_202_Rel_PeQ");
        await NativeProcessRunner.RunAsync(process, "libjxl cjxl", cancellationToken);
    }

    private static async Task RunAvifencAsync(
        string details,
        string inputPath,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        ExportLightLevelStats lightLevelStats,
        CancellationToken cancellationToken)
    {
        using var process = NativeProcessRunner.Create(ExtractFoundPath(details));
        process.StartInfo.ArgumentList.Add("--ignore-profile");
        process.StartInfo.ArgumentList.Add("--depth");
        process.StartInfo.ArgumentList.Add("10");
        process.StartInfo.ArgumentList.Add("--yuv");
        process.StartInfo.ArgumentList.Add("444");
        process.StartInfo.ArgumentList.Add("--qcolor");
        process.StartInfo.ArgumentList.Add("90");
        process.StartInfo.ArgumentList.Add("--speed");
        process.StartInfo.ArgumentList.Add("6");
        process.StartInfo.ArgumentList.Add("--cicp");
        process.StartInfo.ArgumentList.Add($"{Bt2020Primaries}/{GetTransferCode(transfer)}/{Bt2020NonConstantMatrix}");
        process.StartInfo.ArgumentList.Add("--range");
        process.StartInfo.ArgumentList.Add("full");
        process.StartInfo.ArgumentList.Add("--clli");
        process.StartInfo.ArgumentList.Add($"{lightLevelStats.MaxCllNits},{lightLevelStats.MaxPallNits}");
        process.StartInfo.ArgumentList.Add(inputPath);
        process.StartInfo.ArgumentList.Add(outputPath);
        await NativeProcessRunner.RunAsync(process, "libavif avifenc", cancellationToken);
    }

    private static async Task RunHeifEncAsync(
        string details,
        string inputPath,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        ExportLightLevelStats lightLevelStats,
        CancellationToken cancellationToken)
    {
        using var process = NativeProcessRunner.Create(ExtractFoundPath(details));
        process.StartInfo.ArgumentList.Add("--bit-depth");
        process.StartInfo.ArgumentList.Add("10");
        process.StartInfo.ArgumentList.Add("--quality");
        process.StartInfo.ArgumentList.Add("90");
        process.StartInfo.ArgumentList.Add("--matrix_coefficients");
        process.StartInfo.ArgumentList.Add(Bt2020NonConstantMatrix.ToString());
        process.StartInfo.ArgumentList.Add("--colour_primaries");
        process.StartInfo.ArgumentList.Add(Bt2020Primaries.ToString());
        process.StartInfo.ArgumentList.Add("--transfer_characteristic");
        process.StartInfo.ArgumentList.Add(GetTransferCode(transfer).ToString());
        process.StartInfo.ArgumentList.Add("--full_range_flag");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--clli");
        process.StartInfo.ArgumentList.Add($"{lightLevelStats.MaxCllNits},{lightLevelStats.MaxPallNits}");
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add(inputPath);
        await NativeProcessRunner.RunAsync(process, "libheif heif-enc", cancellationToken);
    }

    private static int GetTransferCode(SingleLayerHdrExportTransfer transfer)
    {
        return transfer == SingleLayerHdrExportTransfer.Hlg ? HlgTransfer : PqTransfer;
    }

    private static ushort ToUInt16(float value)
    {
        return (ushort)Math.Clamp(MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 65535.0f), 0.0f, 65535.0f);
    }

    private static void WriteUInt16BigEndian(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteHalfLittleEndian(byte[] data, int offset, float value)
    {
        if (!float.IsFinite(value))
        {
            value = 0.0f;
        }

        var bits = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(value, -65504.0f, 65504.0f));
        data[offset] = (byte)(bits & 0xFF);
        data[offset + 1] = (byte)(bits >> 8);
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static async Task WriteUInt32BigEndianAsync(Stream stream, uint value, CancellationToken cancellationToken)
    {
        var data = new byte[4];
        WriteUInt32BigEndian(data, 0, value);
        await stream.WriteAsync(data, cancellationToken);
    }

    private static async Task WritePngChunkAsync(Stream stream, string type, byte[] data, CancellationToken cancellationToken)
    {
        await WriteUInt32BigEndianAsync(stream, checked((uint)data.Length), cancellationToken);
        var crc = new Crc32();
        var typeBytes = Encoding.ASCII.GetBytes(type);
        await stream.WriteAsync(typeBytes, cancellationToken);
        crc.Update(typeBytes);
        if (data.Length > 0)
        {
            await stream.WriteAsync(data, cancellationToken);
            crc.Update(data);
        }

        await WriteUInt32BigEndianAsync(stream, crc.Value, cancellationToken);
    }

    private static Vector3 PqEncode(Vector3 scene)
    {
        return new Vector3(PqEncodeChannel(scene.X), PqEncodeChannel(scene.Y), PqEncodeChannel(scene.Z));
    }

    private static float PqEncodeChannel(float scene)
    {
        const float m1 = 2610.0f / 16384.0f;
        const float m2 = 2523.0f / 32.0f;
        const float c1 = 3424.0f / 4096.0f;
        const float c2 = 2413.0f / 128.0f;
        const float c3 = 2392.0f / 128.0f;
        var normalized = Math.Clamp(scene * ReferenceWhiteNits / 10000.0f, 0.0f, 1.0f);
        var y = MathF.Pow(normalized, m1);
        return MathF.Pow((c1 + (c2 * y)) / (1.0f + (c3 * y)), m2);
    }

    private static Vector3 HlgEncode(Vector3 scene, float targetScenePeak)
    {
        var displayRelative = Vector3.Clamp(scene / Math.Max(targetScenePeak, 1.0f), Vector3.Zero, Vector3.One);
        var gamma = HdrColorMath.CalculateHlgSystemGamma(targetScenePeak);
        var luma = Math.Max((0.2627f * displayRelative.X) + (0.6780f * displayRelative.Y) + (0.0593f * displayRelative.Z), 0.000001f);
        var hlgScene = displayRelative * MathF.Pow(luma, (1.0f - gamma) / gamma);
        return new Vector3(
            HlgEncodeChannel(hlgScene.X),
            HlgEncodeChannel(hlgScene.Y),
            HlgEncodeChannel(hlgScene.Z));
    }

    private static float HlgEncodeChannel(float value)
    {
        const float a = 0.17883277f;
        const float b = 0.28466892f;
        const float c = 0.55991073f;
        value = Math.Clamp(value, 0.0f, 1.0f);
        return value <= (1.0f / 12.0f)
            ? MathF.Sqrt(3.0f * value)
            : (a * MathF.Log((12.0f * value) - b)) + c;
    }

    private static string DescribeTransfer(SingleLayerHdrExportTransfer transfer)
    {
        return transfer == SingleLayerHdrExportTransfer.Hlg ? "HLG BT.2020" : "PQ BT.2020";
    }

    private static string ExtractFoundPath(string details)
    {
        const string prefix = "found ";
        return details.StartsWith(prefix, StringComparison.Ordinal) ? details[prefix.Length..] : details;
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
                DecodedBitmapTransfer.Pq => HdrColorMath.PqToSceneLinear(ReadEncodedRgb(bitmap, x, y)),
                DecodedBitmapTransfer.Hlg => HdrColorMath.HlgToSceneLinear(ReadEncodedRgb(bitmap, x, y), DefaultHlgPeakNits / ReferenceWhiteNits),
                DecodedBitmapTransfer.LinearScRgb => ReadEncodedRgb(bitmap, x, y),
                DecodedBitmapTransfer.LinearSceneScRgb => ReadEncodedRgb(bitmap, x, y),
                _ => ReadLinearSrgb(bitmap, x, y),
            };

            return bitmap.UsesBt2020Primaries
                ? scene
                : HdrColorMath.Bt709ToBt2020(scene);
        }

        public void Dispose()
        {
        }
    }

    private sealed class GainMapSceneSource(
        GainMapRenderInputs inputs,
        float gainMapWeight,
        bool autoGainMapWeight,
        bool matchPreview,
        float previewSceneToSdrWhiteScale,
        float previewMaxSceneValue) : IHdrSceneSource
    {
        public int Width => inputs.Primary.PixelWidth;

        public int Height => inputs.Primary.PixelHeight;

        public string Description => $"gain-map source base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}, gain-map weight {(matchPreview ? "preview " : autoGainMapWeight ? "auto " : string.Empty)}{gainMapWeight:0.##}";

        public Vector3 ReadSceneLinearBt2020(int x, int y)
        {
            var sdr = HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(inputs.Primary, x, y), inputs.Constants);
            var gain = ReadGainMapSample(inputs.GainMap, x, y, inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
            var scene = inputs.Constants.GainMapControl.Y > 0.5f
                ? HdrColorMath.ReconstructAppleHdrSample(sdr, gain, inputs.Constants.GainMapMax.X, gainMapWeight)
                : HdrColorMath.ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, gainMapWeight);
            var p709 = HdrColorMath.ConvertGainMapBaseToBt709(scene, inputs.Constants);
            if (inputs.Constants.GainMapControl.Y <= 0.5f)
            {
                p709 *= UltraHdrReferenceWhiteNits / ReferenceWhiteNits;
            }

            if (matchPreview)
            {
                if (inputs.Constants.GainMapControl.Y > 0.5f)
                {
                    p709 *= Math.Max(previewSceneToSdrWhiteScale, 1.0f);
                }

                if (previewMaxSceneValue > 0.0f)
                {
                    p709 = Vector3.Min(p709, new Vector3(previewMaxSceneValue));
                }
            }

            return HdrColorMath.Bt709ToBt2020(p709);
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

    private static Vector3 ReadLinearSrgb(DecodedBitmap bitmap, int x, int y)
    {
        var encoded = ReadEncodedRgb(bitmap, x, y);
        return HdrColorMath.SrgbToLinear(encoded);
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

    private static float ParseGainMapScalar(string? value, float fallback)
    {
        return ParseGainMapNumbers(value, fallback)[0];
    }

    private static float ParseGainMapVectorMax(string? value, float fallback)
    {
        var numbers = ParseGainMapNumbers(value, fallback);
        return numbers.Length switch
        {
            >= 3 => Math.Max(numbers[0], Math.Max(numbers[1], numbers[2])),
            _ => numbers[0],
        };
    }

    private static float[] ParseGainMapNumbers(string? value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [fallback];
        }

        var numbers = value
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : float.NaN)
            .Where(float.IsFinite)
            .ToArray();
        return numbers.Length > 0 ? numbers : [fallback];
    }

    private sealed record ExportLightLevelStats(int MaxCllNits, int MaxPallNits);

    private sealed class CrcCountingStream(Stream inner, Crc32 crc) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            crc.Update(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            crc.Update(buffer);
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            crc.Update(buffer.Span);
            return inner.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _value = 0xffffffff;

        public uint Value => _value ^ 0xffffffff;

        public void Update(byte[] data)
        {
            Update(data.AsSpan());
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                _value = Table[(_value ^ b) & 0xff] ^ (_value >> 8);
            }
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 1 ? 0xedb88320 ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
