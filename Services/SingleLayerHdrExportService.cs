using System.Diagnostics;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using Windows.Graphics.Imaging;

namespace HdrImageViewer.Services;

public enum SingleLayerHdrExportTransfer
{
    Pq,
    Hlg,
}

public static class SingleLayerHdrExportService
{
    private const float ReferenceWhiteNits = 80.0f;
    private const float DefaultHlgPeakNits = 1000.0f;

    public static async Task<string> ExportAsync(
        HdrImageDocument document,
        BitmapBounds bounds,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = FindExecutableOnPath("ffmpeg.exe")
            ?? throw new InvalidOperationException("未找到 ffmpeg.exe，当前无法写出 JXL/AVIF 单层 HDR 文件。");

        using var source = await CreateSourceAsync(document, cancellationToken);
        if (bounds.X + bounds.Width > source.Width || bounds.Y + bounds.Height > source.Height)
        {
            throw new InvalidOperationException($"裁切区域超出 HDR 源尺寸: {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}, source {source.Width}x{source.Height}。");
        }

        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        if (extension is not ".jxl" and not ".avif")
        {
            throw new InvalidOperationException("当前单层 HDR 编码只接入了 JXL 和 AVIF。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        await EncodeWithFfmpegAsync(ffmpegPath, source, bounds, outputPath, transfer, cancellationToken);
        return $"{source.Description}; {DescribeTransfer(transfer)}; {extension.TrimStart('.').ToUpperInvariant()}";
    }

    private static async Task<IHdrSceneSource> CreateSourceAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            var inputs = await UltraHdrGainMapDecoder.DecodeRenderInputsAsync(document, cancellationToken);
            return new GainMapSceneSource(inputs);
        }

        var bitmap = await BitmapDecodeService.DecodeFileAsync(document.Path, document.HeifAvifProbe, cancellationToken);
        return new BitmapSceneSource(bitmap);
    }

    private static async Task EncodeWithFfmpegAsync(
        string ffmpegPath,
        IHdrSceneSource source,
        BitmapBounds bounds,
        string outputPath,
        SingleLayerHdrExportTransfer transfer,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        AddCommonFfmpegInputArguments(process, bounds, transfer);
        if (Path.GetExtension(outputPath).Equals(".jxl", StringComparison.OrdinalIgnoreCase))
        {
            AddJxlOutputArguments(process, transfer, outputPath);
        }
        else
        {
            AddAvifOutputArguments(process, transfer, outputPath);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("启动 FFmpeg 编码进程失败。");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await WriteRawRgb48Async(process.StandardInput.BaseStream, source, bounds, transfer, cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;

        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            TryDeleteFile(outputPath);
            throw new InvalidOperationException($"FFmpeg HDR 编码失败: {error.Trim()}");
        }
    }

    private static void AddCommonFfmpegInputArguments(
        Process process,
        BitmapBounds bounds,
        SingleLayerHdrExportTransfer transfer)
    {
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("rawvideo");
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add("rgb48le");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add($"{bounds.Width}x{bounds.Height}");
        process.StartInfo.ArgumentList.Add("-r");
        process.StartInfo.ArgumentList.Add("1");
        AddColorMetadata(process, transfer);
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add("pipe:0");
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add("1");
        AddColorMetadata(process, transfer);
    }

    private static void AddJxlOutputArguments(Process process, SingleLayerHdrExportTransfer transfer, string outputPath)
    {
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add("libjxl");
        process.StartInfo.ArgumentList.Add("-distance");
        process.StartInfo.ArgumentList.Add("1.0");
        process.StartInfo.ArgumentList.Add("-effort");
        process.StartInfo.ArgumentList.Add("5");
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add("rgb48le");
        AddColorMetadata(process, transfer);
        process.StartInfo.ArgumentList.Add(outputPath);
    }

    private static void AddAvifOutputArguments(Process process, SingleLayerHdrExportTransfer transfer, string outputPath)
    {
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add("libaom-av1");
        process.StartInfo.ArgumentList.Add("-still-picture");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-crf");
        process.StartInfo.ArgumentList.Add("8");
        process.StartInfo.ArgumentList.Add("-b:v");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add("yuv444p10le");
        AddColorMetadata(process, transfer);
        process.StartInfo.ArgumentList.Add(outputPath);
    }

    private static void AddColorMetadata(Process process, SingleLayerHdrExportTransfer transfer)
    {
        process.StartInfo.ArgumentList.Add("-color_primaries");
        process.StartInfo.ArgumentList.Add("bt2020");
        process.StartInfo.ArgumentList.Add("-color_trc");
        process.StartInfo.ArgumentList.Add(transfer == SingleLayerHdrExportTransfer.Hlg ? "arib-std-b67" : "smpte2084");
        process.StartInfo.ArgumentList.Add("-colorspace");
        process.StartInfo.ArgumentList.Add("bt2020nc");
    }

    private static async Task WriteRawRgb48Async(
        Stream stream,
        IHdrSceneSource source,
        BitmapBounds bounds,
        SingleLayerHdrExportTransfer transfer,
        CancellationToken cancellationToken)
    {
        var row = new byte[checked((int)bounds.Width * 6)];
        var hlgTargetScenePeak = DefaultHlgPeakNits / ReferenceWhiteNits;
        for (var y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            var sourceY = checked((int)bounds.Y + y);
            for (var x = 0; x < bounds.Width; x++)
            {
                var sourceX = checked((int)bounds.X + x);
                var scene = Vector3.Max(Vector3.Zero, source.ReadSceneLinearBt2020(sourceX, sourceY));
                var encoded = transfer == SingleLayerHdrExportTransfer.Hlg
                    ? HlgEncode(scene, hlgTargetScenePeak)
                    : PqEncode(scene);
                WriteUInt16LittleEndian(row, offset, ToUInt16(encoded.X));
                WriteUInt16LittleEndian(row, offset + 2, ToUInt16(encoded.Y));
                WriteUInt16LittleEndian(row, offset + 4, ToUInt16(encoded.Z));
                offset += 6;
            }

            await stream.WriteAsync(row.AsMemory(0, offset), cancellationToken);
        }
    }

    private static ushort ToUInt16(float value)
    {
        return (ushort)Math.Clamp(MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 65535.0f), 0.0f, 65535.0f);
    }

    private static void WriteUInt16LittleEndian(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
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
        var gamma = CalculateHlgSystemGamma(targetScenePeak);
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

    private static float CalculateHlgSystemGamma(float targetScenePeak)
    {
        var targetNits = Math.Max(targetScenePeak * ReferenceWhiteNits, 100.0f);
        return Math.Clamp(1.2f + (0.42f * MathF.Log10(targetNits / 1000.0f)), 1.0f, 1.35f);
    }

    private static string DescribeTransfer(SingleLayerHdrExportTransfer transfer)
    {
        return transfer == SingleLayerHdrExportTransfer.Hlg ? "HLG BT.2020" : "PQ BT.2020";
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
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
                DecodedBitmapTransfer.Hlg => HlgToSceneLinear(ReadEncodedRgb(bitmap, x, y), DefaultHlgPeakNits / ReferenceWhiteNits),
                DecodedBitmapTransfer.LinearScRgb => ReadEncodedRgb(bitmap, x, y),
                _ => ReadLinearSrgb(bitmap, x, y),
            };

            return bitmap.UsesBt2020Primaries && bitmap.Transfer is not DecodedBitmapTransfer.LinearScRgb
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
            var sdr = ReadLinearSrgb(inputs.Primary, x, y);
            var gain = ReadGainMapSample(inputs.GainMap, x, y, inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
            var p709 = inputs.Constants.Weight.Y > 0.5f
                ? ReconstructAppleHdrSample(sdr, gain, inputs.Constants.GainMapMax.X, 1.0f)
                : ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, 1.0f);
            return Bt709ToBt2020(p709);
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
        var index = checked(((y * bitmap.PixelWidth) + x) * bitmap.BytesPerPixel);
        return new Vector3(
            bitmap.RgbaPixels[index] / 255.0f,
            bitmap.RgbaPixels[index + 1] / 255.0f,
            bitmap.RgbaPixels[index + 2] / 255.0f);
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
        return nits / ReferenceWhiteNits;
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

    private static Vector3 Bt709ToBt2020(Vector3 value)
    {
        return new Vector3(
            (0.6274040f * value.X) + (0.3292820f * value.Y) + (0.0433136f * value.Z),
            (0.0690970f * value.X) + (0.9195400f * value.Y) + (0.0113612f * value.Z),
            (0.0163916f * value.X) + (0.0880132f * value.Y) + (0.8955951f * value.Z));
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



