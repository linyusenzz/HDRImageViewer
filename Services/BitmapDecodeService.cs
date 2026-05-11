using System.Diagnostics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;
using Vortice.WIC;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Services;

public static class BitmapDecodeService
{
    private static string? s_ffmpegPath;
    private static bool s_ffmpegPathResolved;

    public static async Task<DecodedBitmap> DecodeFileAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        CancellationToken cancellationToken = default)
    {
        var containerKind = await FileSignatureProbe.DetectAsync(path, cancellationToken);
        try
        {
            if (heifAvifProbe?.HasHdrTransfer == true)
            {
                try
                {
                    return await Task.Run(
                        () => DecodeHeifHdrWithWicHalf(path, heifAvifProbe, cancellationToken),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    try
                    {
                        return await DecodeHeifFamilyWithFfmpegAsync(path, heifAvifProbe, ex, cancellationToken);
                    }
                    catch
                    {
                        var fallbackBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                        return await DecodeHeifHdrBytesAsync(fallbackBytes, heifAvifProbe, cancellationToken);
                    }
                }
            }

            var encodedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return await DecodeBytesAsync(
                encodedBytes,
                colorManageToSrgb: true,
                respectExifOrientation: true,
                cancellationToken);
        }
        catch (Exception ex) when (containerKind == FileContainerKind.HeifFamily)
        {
            return await DecodeHeifFamilyWithFfmpegAsync(path, heifAvifProbe, ex, cancellationToken);
        }
    }

    public static async Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        CancellationToken cancellationToken = default)
    {
        return await DecodeBytesAsync(
            encodedBytes,
            colorManageToSrgb,
            respectExifOrientation,
            BitmapPixelFormat.Rgba8,
            DecodedBitmapTransfer.Sdr,
            usesBt2020Primaries: false,
            "Windows Imaging",
            cancellationToken);
    }

    private static async Task<DecodedBitmap> DecodeHeifHdrBytesAsync(
        byte[] encodedBytes,
        HeifAvifProbeResult probe,
        CancellationToken cancellationToken)
    {
        return await DecodeBytesAsync(
            encodedBytes,
            colorManageToSrgb: false,
            respectExifOrientation: true,
            BitmapPixelFormat.Rgba16,
            probe.TransferCharacteristics == 16 ? DecodedBitmapTransfer.Pq : DecodedBitmapTransfer.Hlg,
            probe.HasBt2020,
            "Windows Imaging HEIF HDR",
            cancellationToken);
    }

    private static DecodedBitmap DecodeHeifHdrWithWicHalf(
        string path,
        HeifAvifProbeResult probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(
            path,
            FileAccess.Read,
            DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        using var converter = factory.CreateFormatConverter();
        converter.Initialize(
            frame,
            WicPixelFormat.Format64bppRGBAHalf,
            BitmapDitherType.None,
            null!,
            0.0,
            BitmapPaletteType.Custom);

        var size = converter.Size;
        var width = checked((int)size.Width);
        var height = checked((int)size.Height);
        var stride = checked(width * 8);
        var pixels = new byte[checked(stride * height)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            converter.CopyPixels(
                new Vortice.Mathematics.RectI(0, 0, width, height),
                checked((uint)stride),
                checked((uint)pixels.Length),
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        return new DecodedBitmap(
            width,
            height,
            pixels,
            ColorManagedToSrgb: false,
            $"WIC FP16 half ({DescribeHdrTransfer(probe)} decoded by WIC to scRGB; container {DescribeContainerPrimaries(probe)})",
            DecodedBitmapPixelFormat.Rgba16Float,
            DecodedBitmapTransfer.LinearScRgb,
            false);
    }

    private static string DescribeHdrTransfer(HeifAvifProbeResult probe)
    {
        return probe.TransferCharacteristics == 16 ? "PQ" : "HLG";
    }

    private static string DescribeContainerPrimaries(HeifAvifProbeResult probe)
    {
        return probe.HasBt2020 ? "BT.2020" : "unknown primaries";
    }

    private static async Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        BitmapPixelFormat pixelFormat,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string decoderName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(encodedBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var orientationMode = respectExifOrientation
            ? ExifOrientationMode.RespectExifOrientation
            : ExifOrientationMode.IgnoreExifOrientation;

        var pixelData = await decoder.GetPixelDataAsync(
            pixelFormat,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            orientationMode,
            colorManageToSrgb ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);

        var width = respectExifOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth;
        var height = respectExifOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight;
        return new DecodedBitmap(
            checked((int)width),
            checked((int)height),
            pixelData.DetachPixelData(),
            colorManageToSrgb,
            decoderName,
            pixelFormat == BitmapPixelFormat.Rgba16 ? DecodedBitmapPixelFormat.Rgba16Unorm : DecodedBitmapPixelFormat.Rgba8Unorm,
            transfer,
            usesBt2020Primaries);
    }

    private static async Task<DecodedBitmap> DecodeHeifFamilyWithFfmpegAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe,
        Exception? windowsImagingException,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = TryFindExecutableOnPath("ffmpeg.exe");
        if (ffmpegPath is null)
        {
            var prefix = windowsImagingException is null
                ? "HEIF HDR decode prefers FFmpeg for unmodified 16-bit HLG/PQ samples"
                : $"Windows Imaging HEIF decode failed ({windowsImagingException.GetType().Name}: {windowsImagingException.Message})";
            throw new InvalidOperationException($"{prefix}, and ffmpeg.exe was not found on PATH.", windowsImagingException);
        }

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add("1");
        if (heifAvifProbe?.HasHdrTransfer == true)
        {
            process.StartInfo.ArgumentList.Add("-pix_fmt");
            process.StartInfo.ArgumentList.Add("rgba64be");
        }

        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("image2pipe");
        process.StartInfo.ArgumentList.Add("-vcodec");
        process.StartInfo.ArgumentList.Add("png");
        process.StartInfo.ArgumentList.Add("pipe:1");

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.exe for HEIF/AVIF fallback decode.");
        }

        await using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0 || output.Length == 0)
        {
            var prefix = windowsImagingException is null
                ? "FFmpeg HEIF HDR decode"
                : $"Windows Imaging HEIF decode failed ({windowsImagingException.GetType().Name}: {windowsImagingException.Message}); FFmpeg fallback";
            throw new InvalidOperationException(
                $"{prefix} failed with exit code {process.ExitCode}: {error.Trim()}",
                windowsImagingException);
        }

        var isHdr = heifAvifProbe?.HasHdrTransfer == true;
        var decoded = isHdr
            ? await DecodeBytesAsync(
                output.ToArray(),
                colorManageToSrgb: false,
                respectExifOrientation: false,
                BitmapPixelFormat.Rgba16,
                heifAvifProbe!.TransferCharacteristics == 16 ? DecodedBitmapTransfer.Pq : DecodedBitmapTransfer.Hlg,
                heifAvifProbe.HasBt2020,
                "FFmpeg 16-bit PNG pipe fallback",
                cancellationToken)
            : await DecodeBytesAsync(
                output.ToArray(),
                colorManageToSrgb: true,
                respectExifOrientation: false,
                cancellationToken);
        return decoded with { DecoderName = isHdr ? "FFmpeg 16-bit PNG pipe fallback" : "FFmpeg PNG pipe fallback" };
    }

    private static string? TryFindExecutableOnPath(string fileName)
    {
        if (s_ffmpegPathResolved)
        {
            return s_ffmpegPath;
        }

        s_ffmpegPathResolved = true;
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
                    s_ffmpegPath = candidate;
                    return s_ffmpegPath;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }
}
