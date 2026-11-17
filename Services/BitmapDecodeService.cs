using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using LibHeifSharp;
using Vortice.WIC;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using HeifChromaEnum = LibHeifSharp.HeifChroma;
using LibHeifTransferCharacteristics = LibHeifSharp.TransferCharacteristics;
using LibHeifColorPrimaries = LibHeifSharp.ColorPrimaries;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Services;

public static class BitmapDecodeService
{
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    static BitmapDecodeService()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HeifContext).Assembly, ResolveLibheifNativeDependency);
        }
        catch
        {
        }
    }

    private static IntPtr ResolveLibheifNativeDependency(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "libheif", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in EnumerateLibheifCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var handle = LoadLibraryEx(candidate, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> EnumerateLibheifCandidates()
    {
        foreach (var fileName in new[] { "libheif.dll", "heif.dll" })
        {
            yield return Path.Combine(AppContext.BaseDirectory, fileName);
            yield return Path.Combine(AppContext.BaseDirectory, "encoders", NativeToolLocator.PlatformDirectoryName, fileName);
            yield return @"C:\msys64\ucrt64\bin\" + fileName;
            yield return @"C:\msys64\mingw64\bin\" + fileName;
        }
    }

    public static async Task<DecodedBitmap> DecodeFileAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            path,
            gainMapProbe: null,
            heifAvifProbe,
            jxlProbe: null,
            wicImageProbe: null,
            maxPixelSize,
            allowHdrDownscale: false,
            preserveHdrTransfer: true,
            cancellationToken,
            preserveAlpha: true);
    }

    public static async Task<DecodedBitmap> DecodeDocumentAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            document.Path,
            document.GainMapProbe,
            document.HeifAvifProbe,
            document.JxlProbe,
            document.WicImageProbe,
            maxPixelSize,
            allowHdrDownscale: true,
            preserveHdrTransfer: true,
            cancellationToken,
            preserveAlpha: true);
    }

    public static async Task<DecodedBitmap> DecodeFileForThumbnailAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            path,
            gainMapProbe: null,
            heifAvifProbe,
            jxlProbe: null,
            wicImageProbe: null,
            maxPixelSize,
            allowHdrDownscale: true,
            preserveHdrTransfer: true,
            cancellationToken,
            preserveAlpha: true);
    }

    public static async Task<DecodedBitmap> DecodeDocumentForThumbnailAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            document.Path,
            document.GainMapProbe,
            document.HeifAvifProbe,
            document.JxlProbe,
            document.WicImageProbe,
            maxPixelSize,
            allowHdrDownscale: true,
            preserveHdrTransfer: true,
            cancellationToken,
            preserveAlpha: true);
    }

    public static async Task<DecodedBitmap> DecodeFileForHdrExportAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            path,
            gainMapProbe: null,
            heifAvifProbe,
            jxlProbe: null,
            wicImageProbe: null,
            maxPixelSize: null,
            allowHdrDownscale: false,
            preserveHdrTransfer: true,
            cancellationToken);
    }

    public static async Task<DecodedBitmap> DecodeDocumentForHdrExportAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(
            document.Path,
            document.GainMapProbe,
            document.HeifAvifProbe,
            document.JxlProbe,
            document.WicImageProbe,
            maxPixelSize: null,
            allowHdrDownscale: false,
            preserveHdrTransfer: true,
            cancellationToken);
    }

    public static Task<DecodedBitmap> DecodeFileRawRgba16Async(
        string path,
        string decoderName,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return DecodeFileWithWinRTAsync(
            path,
            colorManageToSrgb: false,
            respectExifOrientation: false,
            BitmapPixelFormat.Rgba16,
            DecodedBitmapTransfer.Sdr,
            usesBt2020Primaries: false,
            decoderName,
            maxPixelSize,
            cancellationToken);
    }

    public static Task<DecodedBitmap> DecodeFileSdrWithWindowsImagingAsync(
        string path,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        string decoderName,
        int? maxPixelSize = null,
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown,
        CancellationToken cancellationToken = default)
    {
        return DecodeFileWithWinRTAsync(
            path,
            colorManageToSrgb,
            respectExifOrientation,
            BitmapPixelFormat.Rgba8,
            DecodedBitmapTransfer.Sdr,
            usesBt2020Primaries: colorGamut == GainMapColorGamut.Bt2100,
            decoderName,
            maxPixelSize,
            cancellationToken,
            colorGamut);
    }

    private static async Task<DecodedBitmap> DecodeFileCoreAsync(
        string path,
        GainMapProbeResult? gainMapProbe,
        HeifAvifProbeResult? heifAvifProbe,
        JxlProbeResult? jxlProbe,
        WicImageProbeResult? wicImageProbe,
        int? maxPixelSize,
        bool allowHdrDownscale,
        bool preserveHdrTransfer,
        CancellationToken cancellationToken,
        bool preserveAlpha = false)
    {
        var extension = Path.GetExtension(path);
        var containerKind = await FileSignatureProbe.DetectAsync(path, cancellationToken);
        try
        {
            if (containerKind == FileContainerKind.Jxl || string.Equals(extension, ".jxl", StringComparison.OrdinalIgnoreCase))
            {
                return await DecodeJxlAsync(path, jxlProbe, maxPixelSize, allowHdrDownscale, preserveHdrTransfer, cancellationToken, preserveAlpha);
            }

            if (string.Equals(extension, ".exr", StringComparison.OrdinalIgnoreCase))
            {
                return await DecodeExrWithNativeToolAsync(path, maxPixelSize, preserveAlpha, cancellationToken);
            }

            if (containerKind == FileContainerKind.RadianceHdr || DecoderCatalog.IsRadianceHdrExtension(extension))
            {
                var effectiveMaxSize = allowHdrDownscale ? maxPixelSize : null;
                return await Task.Run(
                    () => RadianceHdrDecoder.DecodeFile(path, effectiveMaxSize, cancellationToken),
                    cancellationToken);
            }

            if (DecoderCatalog.IsJpegXrExtension(extension))
            {
                if (!allowHdrDownscale)
                {
                    maxPixelSize = null;
                }

                return await DecodeJpegXrAsync(path, maxPixelSize, cancellationToken);
            }

            if (wicImageProbe is null && WicImageProbe.IsWicProbeExtension(extension))
            {
                wicImageProbe = await WicImageProbe.ProbeAsync(path, cancellationToken);
            }

            if (wicImageProbe is { } wicProbe && (wicProbe.IsPng || wicProbe.IsTiff))
            {
                var resolvedWicGamut = WicImageProbe.ResolveColorGamut(wicProbe);
                var isWideGamutWic = resolvedWicGamut is GainMapColorGamut.DisplayP3
                    or GainMapColorGamut.Bt2100
                    or GainMapColorGamut.AdobeRgb
                    or GainMapColorGamut.ProPhoto;

                if (wicProbe.IsHighBitDepth || wicProbe.IsPngHdr || wicProbe.IsTiffPq || isWideGamutWic)
                {
                    if (wicProbe.IsTiffFloatingPoint)
                    {
                        if (!allowHdrDownscale)
                        {
                            maxPixelSize = null;
                        }

                        try
                        {
                            return await Task.Run(
                                () => DecodeWicHalfLinearScRgb(
                                    path,
                                    $"{wicProbe.DecoderName} floating point -> RGBA16F",
                                    maxPixelSize,
                                    wicProbe.UsesBt2020Primaries,
                                    cancellationToken),
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                        }
                    }

                    var transfer = wicProbe.IsPngPq || wicProbe.IsTiffPq
                        ? DecodedBitmapTransfer.Pq
                        : wicProbe.IsPngHlg
                            ? DecodedBitmapTransfer.Hlg
                            : DecodedBitmapTransfer.Sdr;
                    var isHdrWic = transfer is DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg;
                    if (isHdrWic && !allowHdrDownscale)
                    {
                        maxPixelSize = null;
                    }

                    var colorGamut = isWideGamutWic ? resolvedWicGamut : GainMapColorGamut.Unknown;
                    var colorManageToSrgb = !isHdrWic && colorGamut is GainMapColorGamut.Unknown or GainMapColorGamut.Bt709;

                    return await DecodeFileWithWinRTAsync(
                        path,
                        colorManageToSrgb,
                        respectExifOrientation: true,
                        BitmapPixelFormat.Rgba16,
                        transfer,
                        usesBt2020Primaries: colorGamut == GainMapColorGamut.Bt2100,
                        isHdrWic
                            ? $"{wicProbe.DecoderName} {wicProbe.WicTransferName} {wicProbe.WicPrimariesName}"
                            : wicProbe.IsHighBitDepth
                                ? $"{wicProbe.DecoderName} high bit-depth{DescribeColorGamutSuffix(colorGamut)}"
                                : $"{wicProbe.DecoderName} wide-gamut{DescribeColorGamutSuffix(colorGamut)}",
                        maxPixelSize,
                        cancellationToken,
                        colorGamut,
                        preserveAlpha);
                }
            }

            if (heifAvifProbe?.HasHdrTransfer == true)
            {
                if (!allowHdrDownscale)
                {
                    maxPixelSize = null;
                }

                string? libheifFallbackReason = null;
                try
                {
                    var bitmap = await Task.Run(
                        () => DownscalePreviewBitmapIfNeeded(
                            DecodeHeifAvifHdrWithLibheif(path, heifAvifProbe, cancellationToken),
                            maxPixelSize,
                            cancellationToken),
                        cancellationToken);
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    libheifFallbackReason = $"libheif binding failed ({ex.GetType().Name}: {ex.Message})";
                }

                try
                {
                    var bitmap = await DecodeHeifAvifHdrWithNativeToolAsync(path, heifAvifProbe, maxPixelSize, cancellationToken, preserveAlpha);
                    if (libheifFallbackReason is not null)
                    {
                        bitmap = bitmap with { DecoderName = $"{bitmap.DecoderName} [fallback because {libheifFallbackReason}]" };
                    }
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }

                Exception? wicHalfFallbackFailure = null;
                try
                {
                    var bitmap = await Task.Run(
                        () => DecodeWicHalfLinearScRgb(
                            path,
                            $"WIC FP16 half ({DescribeHdrTransfer(heifAvifProbe)} decoded by Windows {DescribeHeifAvifCodec(path)} codec to linear scRGB; container {DescribeContainerPrimaries(heifAvifProbe)})",
                            maxPixelSize,
                            usesBt2020Primaries: false,
                            cancellationToken),
                        cancellationToken);
                    if (libheifFallbackReason is not null)
                    {
                        bitmap = bitmap with { DecoderName = $"{bitmap.DecoderName} [fallback because {libheifFallbackReason}]" };
                    }
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    wicHalfFallbackFailure = ex;
                }

                Exception? winRtFallbackFailure = null;
                try
                {
                    var bitmap = await DecodeFileWithWinRTAsync(
                        path,
                        colorManageToSrgb: false,
                        respectExifOrientation: true,
                        BitmapPixelFormat.Rgba16,
                        heifAvifProbe.TransferCharacteristics == 16 ? DecodedBitmapTransfer.Pq : DecodedBitmapTransfer.Hlg,
                        heifAvifProbe.HasBt2020,
                        $"Windows Imaging {DescribeHeifAvifCodec(path)} HDR",
                        maxPixelSize,
                        cancellationToken,
                        GetHeifColorGamut(heifAvifProbe),
                        preserveAlpha);
                    if (libheifFallbackReason is not null || wicHalfFallbackFailure is not null)
                    {
                        var reasons = new List<string>();
                        if (libheifFallbackReason is not null)
                        {
                            reasons.Add(libheifFallbackReason);
                        }

                        if (wicHalfFallbackFailure is not null)
                        {
                            reasons.Add($"WIC FP16 failed ({wicHalfFallbackFailure.GetType().Name}: {wicHalfFallbackFailure.Message})");
                        }

                        bitmap = bitmap with { DecoderName = $"{bitmap.DecoderName} [fallback because {string.Join("; ", reasons)}]" };
                    }
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    winRtFallbackFailure = ex;
                }

                throw new InvalidOperationException(
                    $"Windows Imaging {DescribeHeifAvifCodec(path)} HDR decode failed (WIC FP16: {wicHalfFallbackFailure?.GetType().Name}: {wicHalfFallbackFailure?.Message}; WinRT Rgba16: {winRtFallbackFailure?.GetType().Name}: {winRtFallbackFailure?.Message}).",
                    winRtFallbackFailure ?? wicHalfFallbackFailure);
            }

            if (heifAvifProbe is { IsHeifFamily: true } sdrHeifProbe)
            {
                var heifWideGamut = GetHeifColorGamut(sdrHeifProbe);
                if (heifWideGamut is GainMapColorGamut.DisplayP3 or GainMapColorGamut.Bt2100)
                {
                    return await DecodeFileWithWinRTAsync(
                        path,
                        colorManageToSrgb: false,
                        respectExifOrientation: true,
                        BitmapPixelFormat.Rgba16,
                        DecodedBitmapTransfer.Sdr,
                        usesBt2020Primaries: heifWideGamut == GainMapColorGamut.Bt2100,
                        $"Windows Imaging {DescribeHeifAvifCodec(path)} wide-gamut{DescribeColorGamutSuffix(heifWideGamut)}",
                        maxPixelSize,
                        cancellationToken,
                        heifWideGamut,
                        preserveAlpha);
                }
            }

            if ((containerKind == FileContainerKind.Jpeg || DecoderCatalog.IsJpegExtension(extension))
                && gainMapProbe?.PrimaryColorGamut is GainMapColorGamut.DisplayP3
                    or GainMapColorGamut.Bt2100
                    or GainMapColorGamut.AdobeRgb
                    or GainMapColorGamut.ProPhoto)
            {
                var jpegGamut = gainMapProbe.PrimaryColorGamut;
                return await DecodeFileWithWinRTAsync(
                    path,
                    colorManageToSrgb: false,
                    respectExifOrientation: true,
                    BitmapPixelFormat.Rgba16,
                    DecodedBitmapTransfer.Sdr,
                    usesBt2020Primaries: jpegGamut == GainMapColorGamut.Bt2100,
                    $"Windows Imaging JPEG wide-gamut{DescribeColorGamutSuffix(jpegGamut)}",
                    maxPixelSize,
                    cancellationToken,
                    jpegGamut,
                    preserveAlpha);
            }

            return await DecodeFileWithWinRTAsync(
                path,
                colorManageToSrgb: true,
                respectExifOrientation: true,
                BitmapPixelFormat.Rgba8,
                DecodedBitmapTransfer.Sdr,
                usesBt2020Primaries: false,
                "Windows Imaging",
                maxPixelSize,
                cancellationToken,
                preserveAlpha: preserveAlpha);
        }
        catch (Exception ex) when (containerKind == FileContainerKind.HeifFamily && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Windows Imaging {DescribeHeifAvifCodec(path)} decode failed ({ex.GetType().Name}: {ex.Message}). Install or repair the Windows HEIF/AVIF codec package to preview this file.",
                ex);
        }
    }

    public static Task<DecodedBitmap> DecodeFileAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe,
        CancellationToken cancellationToken)
    {
        return DecodeFileAsync(path, heifAvifProbe, maxPixelSize: null, cancellationToken);
    }

    public static Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return DecodeBytesAsync(
            encodedBytes,
            0,
            encodedBytes.Length,
            colorManageToSrgb,
            respectExifOrientation,
            maxPixelSize,
            cancellationToken);
    }

    public static async Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        int offset,
        int count,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeBytesAsync(
            encodedBytes,
            offset,
            count,
            colorManageToSrgb,
            respectExifOrientation,
            BitmapPixelFormat.Rgba8,
            DecodedBitmapTransfer.Sdr,
            usesBt2020Primaries: false,
            "Windows Imaging",
            maxPixelSize,
            cancellationToken);
    }

    public static Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        CancellationToken cancellationToken)
    {
        return DecodeBytesAsync(encodedBytes, colorManageToSrgb, respectExifOrientation, maxPixelSize: null, cancellationToken);
    }

    private static DecodedBitmap DecodeHeifAvifHdrWithLibheif(
        string path,
        HeifAvifProbeResult probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var openTimer = Stopwatch.StartNew();

        using var context = new HeifContext(path);
        using var handle = context.GetPrimaryImageHandle();
        cancellationToken.ThrowIfCancellationRequested();
        var openMs = openTimer.ElapsedMilliseconds;

        var decodeTimer = Stopwatch.StartNew();
        var chroma = BitConverter.IsLittleEndian
            ? HeifChromaEnum.InterleavedRgba64LE
            : HeifChromaEnum.InterleavedRgba64BE;
        using var image = handle.Decode(HeifColorspace.Rgb, chroma);
        cancellationToken.ThrowIfCancellationRequested();
        var decodeMs = decodeTimer.ElapsedMilliseconds;

        var copyTimer = Stopwatch.StartNew();
        var plane = image.GetPlane(HeifChannel.Interleaved);
        var width = plane.Width;
        var height = plane.Height;
        var rowBytes = checked(width * 8);
        var pixels = new byte[checked(rowBytes * height)];
        var src = plane.Scan0;
        var stride = plane.Stride;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(IntPtr.Add(src, y * stride), pixels, y * rowBytes, rowBytes);
        }

        var sourceBitDepth = Math.Clamp(handle.BitDepth, 8, 16);
        if (sourceBitDepth < 16)
        {
            var leftShift = 16 - sourceBitDepth;
            var rightShift = sourceBitDepth - leftShift;
            System.Threading.Tasks.Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
            {
                var rowSamples = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan(y * rowBytes, rowBytes));
                for (var i = 0; i < rowSamples.Length; i++)
                {
                    var v = rowSamples[i];
                    rowSamples[i] = (ushort)((v << leftShift) | (v >> rightShift));
                }
            });
        }

        ThrowIfLikelyLibheifCorruptFrame(pixels, width, height);
        var copyMs = copyTimer.ElapsedMilliseconds;

        var nclx = image.NclxColorProfile;
        var transferChar = nclx?.TransferCharacteristics
            ?? (probe.TransferCharacteristics == 16 ? LibHeifTransferCharacteristics.Smpte2084 : LibHeifTransferCharacteristics.HLG);
        var primaries = nclx?.ColorPrimaries
            ?? (probe.HasBt2020 ? LibHeifColorPrimaries.BT2020 : LibHeifColorPrimaries.BT709);
        var transfer = transferChar == LibHeifTransferCharacteristics.Smpte2084
            ? DecodedBitmapTransfer.Pq
            : DecodedBitmapTransfer.Hlg;
        var colorGamut = primaries switch
        {
            LibHeifColorPrimaries.BT2020 => GainMapColorGamut.Bt2100,
            LibHeifColorPrimaries.Smpte432 => GainMapColorGamut.DisplayP3,
            _ => probe.ColorPrimaries == 12
                ? GainMapColorGamut.DisplayP3
                : probe.HasBt2020 ? GainMapColorGamut.Bt2100 : GainMapColorGamut.Bt709,
        };
        var usesBt2020 = colorGamut == GainMapColorGamut.Bt2100;

        return new DecodedBitmap(
            width,
            height,
            pixels,
            ColorManagedToSrgb: false,
            $"libheif in-process [open {openMs}ms, decode {decodeMs}ms, copy {copyMs}ms, src {sourceBitDepth}-bit]; {(transfer == DecodedBitmapTransfer.Pq ? "PQ" : "HLG")} {DescribeColorGamut(colorGamut)}",
            DecodedBitmapPixelFormat.Rgba16Unorm,
            transfer,
            usesBt2020,
            colorGamut);
    }

    private static void ThrowIfLikelyLibheifCorruptFrame(byte[] rgba64Pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba64Pixels.Length < width * 8)
        {
            return;
        }

        var stepX = Math.Max(1, width / 96);
        var stepY = Math.Max(1, height / 96);
        var samples = 0;
        var greenDominant = 0;
        double sumR = 0.0;
        double sumG = 0.0;
        double sumB = 0.0;
        for (var y = 0; y < height; y += stepY)
        {
            var rowStart = checked(y * width * 8);
            for (var x = 0; x < width; x += stepX)
            {
                var index = checked(rowStart + (x * 8));
                if (index + 5 >= rgba64Pixels.Length)
                {
                    continue;
                }

                var r = ReadUInt16LittleEndian(rgba64Pixels, index) / 65535.0;
                var g = ReadUInt16LittleEndian(rgba64Pixels, index + 2) / 65535.0;
                var b = ReadUInt16LittleEndian(rgba64Pixels, index + 4) / 65535.0;
                sumR += r;
                sumG += g;
                sumB += b;
                if ((g > 0.80 && r < 0.12 && b < 0.12)
                    || (g > 0.28 && g > r * 4.0 && g > b * 4.0))
                {
                    greenDominant++;
                }

                samples++;
            }
        }

        if (samples == 0)
        {
            return;
        }

        var greenRatio = greenDominant / (double)samples;
        var avgR = sumR / samples;
        var avgG = sumG / samples;
        var avgB = sumB / samples;
        if ((greenRatio > 0.35 && avgG > 0.55 && avgR < 0.25 && avgB < 0.25)
            || (greenRatio > 0.45 && avgG > 0.20 && avgG > avgR * 3.0 && avgG > avgB * 3.0))
        {
            throw new InvalidOperationException(
                $"libheif decoded a likely corrupt green frame (green samples {greenRatio:P0}, average RGB {avgR:0.###}/{avgG:0.###}/{avgB:0.###}); falling back to Windows Imaging.");
        }
    }

    private static async Task<DecodedBitmap> DecodeHeifAvifHdrWithNativeToolAsync(
        string path,
        HeifAvifProbeResult probe,
        int? maxPixelSize,
        CancellationToken cancellationToken,
        bool preserveAlpha = false)
    {
        var extension = Path.GetExtension(path);
        var isAvif = string.Equals(extension, ".avif", StringComparison.OrdinalIgnoreCase);
        var tool = isAvif
            ? NativeToolLocator.FindTool("avifdec.exe")
            : NativeToolLocator.FindTool("heif-dec.exe");
        if (tool is null)
        {
            throw new InvalidOperationException($"未找到 {(isAvif ? "avifdec.exe" : "heif-dec.exe")}。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "heif-avif-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pngPath = Path.Combine(tempDir, "decoded.png");
        try
        {
            using var process = NativeProcessRunner.Create(tool);
            if (isAvif)
            {
                process.StartInfo.ArgumentList.Add("-d");
                process.StartInfo.ArgumentList.Add("16");
            }
            else
            {
                process.StartInfo.ArgumentList.Add("--quiet");
            }

            process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add(pngPath);
            var spawnTimer = Stopwatch.StartNew();
            await NativeProcessRunner.RunAsync(process, isAvif ? "libavif avifdec" : "libheif heif-dec", cancellationToken);
            var spawnMs = spawnTimer.ElapsedMilliseconds;
            var transfer = probe.TransferCharacteristics == 16 ? DecodedBitmapTransfer.Pq : DecodedBitmapTransfer.Hlg;
            var winrtTimer = Stopwatch.StartNew();
            var bitmap = await DecodeFileWithWinRTAsync(
                pngPath,
                colorManageToSrgb: false,
                respectExifOrientation: true,
                BitmapPixelFormat.Rgba16,
                transfer,
                probe.HasBt2020,
                $"{(isAvif ? "libavif avifdec" : "libheif heif-dec")} [spawn+decode {spawnMs}ms]; {DescribeHdrTransfer(probe)} {DescribeContainerPrimaries(probe)}",
                maxPixelSize,
                cancellationToken,
                GetHeifColorGamut(probe),
                preserveAlpha);
            var winrtMs = winrtTimer.ElapsedMilliseconds;
            return bitmap with { DecoderName = $"{bitmap.DecoderName} [winrt png {winrtMs}ms]" };
        }
        finally
        {
            TryDeleteFile(pngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<DecodedBitmap> DecodeJxlAsync(
        string path,
        JxlProbeResult? knownInfo,
        int? maxPixelSize,
        bool allowHdrDownscale,
        bool preserveHdrTransfer,
        CancellationToken cancellationToken,
        bool preserveAlpha = false)
    {
        var info = knownInfo
            ?? await JxlProbe.ProbeAsync(path, forceSignatureDetected: true, cancellationToken)
            ?? new JxlProbeResult(true, null, null, null, "未知", "未知", null, null, "JPEG XL");
        var transfer = DecodeTransferFromJxl(info);
        var isHdr = transfer is DecodedBitmapTransfer.Pq
            or DecodedBitmapTransfer.Hlg
            or DecodedBitmapTransfer.LinearSceneScRgb;
        var effectiveMaxPixelSize = (!isHdr || allowHdrDownscale) ? maxPixelSize : null;

        string? nativeFallbackReason = null;
        try
        {
            var bitmap = await Task.Run(
                () => JxlNativeDecoder.DecodeFile(
                    path,
                    transfer,
                    info.ColorGamut,
                    $"{info.TransferSummary}; {info.ColorSummary}",
                    preserveAlpha,
                    cancellationToken),
                cancellationToken);
            bitmap = DownscalePreviewBitmapIfNeeded(bitmap, effectiveMaxPixelSize, cancellationToken);
            return preserveHdrTransfer || transfer is not (DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg)
                ? bitmap
                : ConvertHdrEncodedToLinearScRgb(bitmap, info.IntensityTargetNits, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            nativeFallbackReason = $"libjxl binding failed ({ex.GetType().Name}: {ex.Message})";
        }

        DecodedBitmap fallback;
        if (transfer == DecodedBitmapTransfer.LinearSceneScRgb)
        {
            fallback = await DecodeLinearJxlWithDjxlAsync(path, info, effectiveMaxPixelSize, preserveAlpha, cancellationToken);
        }
        else
        {
            fallback = await DecodeJxlWithDjxlAsync(path, info, maxPixelSize, allowHdrDownscale, preserveHdrTransfer, cancellationToken, preserveAlpha);
        }

        return nativeFallbackReason is null
            ? fallback
            : fallback with { DecoderName = $"{fallback.DecoderName} [fallback because {nativeFallbackReason}]" };
    }

    private static async Task<DecodedBitmap> DecodeJxlWithDjxlAsync(
        string path,
        JxlProbeResult? knownInfo,
        int? maxPixelSize,
        bool allowHdrDownscale,
        bool preserveHdrTransfer,
        CancellationToken cancellationToken,
        bool preserveAlpha = false)
    {
        var djxl = NativeToolLocator.FindTool("djxl.exe")
            ?? throw new InvalidOperationException("未找到 djxl.exe，无法预览 JPEG XL。请安装 libjxl 工具或把 djxl.exe 放到 PATH。");
        var info = knownInfo ?? await JxlProbe.ProbeAsync(path, cancellationToken)
            ?? new JxlProbeResult(true, null, null, null, "未知", "未知", null, null, "JPEG XL");
        var transfer = DecodeTransferFromJxl(info);
        if (!allowHdrDownscale && transfer is (DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg))
        {
            maxPixelSize = null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "jxl-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pngPath = Path.Combine(tempDir, "decoded.png");
        var ppmPath = Path.Combine(tempDir, "decoded.ppm");
        try
        {
            // PPM has no alpha channel. Thumbnail requests retain it through
            // 16-bit PNG; the export/preview PPM route remains unchanged.
            var useRawPpm = transfer is DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg && !preserveAlpha;
            var outputPath = useRawPpm ? ppmPath : pngPath;
            using var process = NativeProcessRunner.Create(djxl);
            process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add(outputPath);
            process.StartInfo.ArgumentList.Add("--quiet");
            if (useRawPpm)
            {
                process.StartInfo.ArgumentList.Add("--output_format");
                process.StartInfo.ArgumentList.Add("ppm");
            }
            if (transfer != DecodedBitmapTransfer.Sdr)
            {
                process.StartInfo.ArgumentList.Add("--bits_per_sample");
                process.StartInfo.ArgumentList.Add("16");
            }
            var downsampling = CalculateJxlDownsampling(info, maxPixelSize);
            if (downsampling > 1)
            {
                process.StartInfo.ArgumentList.Add("--downsampling");
                process.StartInfo.ArgumentList.Add(downsampling.ToString(CultureInfo.InvariantCulture));
            }
            var djxlTimer = Stopwatch.StartNew();
            await NativeProcessRunner.RunAsync(process, "libjxl djxl", cancellationToken);
            var djxlMs = djxlTimer.ElapsedMilliseconds;

            var jxlGamut = info.ColorGamut;
            var isWideGamutSdrJxl = transfer == DecodedBitmapTransfer.Sdr
                && jxlGamut is GainMapColorGamut.DisplayP3
                    or GainMapColorGamut.Bt2100
                    or GainMapColorGamut.AdobeRgb
                    or GainMapColorGamut.ProPhoto;

            if (useRawPpm)
            {
                var ppmTimer = Stopwatch.StartNew();
                var ppmBitmap = PortableImageReader.ReadPpmAsRgba16(
                    ppmPath,
                    $"libjxl djxl [djxl {djxlMs}ms, downsampling {downsampling}x, PPM16 raw path]; {info.TransferSummary}; {info.ColorSummary}",
                    transfer,
                    info.UsesBt2020Primaries,
                    cancellationToken);
                var ppmMs = ppmTimer.ElapsedMilliseconds;
                ppmBitmap = ppmBitmap with
                {
                    DecoderName = $"{ppmBitmap.DecoderName} [read PPM {ppmMs}ms]",
                    ColorGamut = jxlGamut == GainMapColorGamut.Unknown ? ppmBitmap.ColorGamut : jxlGamut,
                };
                ppmBitmap = DownscalePreviewBitmapIfNeeded(ppmBitmap, maxPixelSize, cancellationToken);
                return preserveHdrTransfer ? ppmBitmap : ConvertHdrEncodedToLinearScRgb(ppmBitmap, info.IntensityTargetNits, cancellationToken);
            }

            var winrtTimer = Stopwatch.StartNew();
            var bitmap = await DecodeFileWithWinRTAsync(
                pngPath,
                colorManageToSrgb: transfer == DecodedBitmapTransfer.Sdr && !isWideGamutSdrJxl,
                respectExifOrientation: false,
                transfer == DecodedBitmapTransfer.Sdr && !isWideGamutSdrJxl ? BitmapPixelFormat.Rgba8 : BitmapPixelFormat.Rgba16,
                transfer,
                info.UsesBt2020Primaries,
                $"libjxl djxl [djxl {djxlMs}ms, downsampling {downsampling}x]; {info.TransferSummary}; {info.ColorSummary}",
                maxPixelSize,
                cancellationToken,
                jxlGamut,
                preserveAlpha: preserveAlpha);
            var winrtMs = winrtTimer.ElapsedMilliseconds;
            bitmap = bitmap with { DecoderName = $"{bitmap.DecoderName} [temp PNG WinRT {winrtMs}ms]" };
            return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, info.IntensityTargetNits, cancellationToken);
        }
        finally
        {
            TryDeleteFile(pngPath);
            TryDeleteFile(ppmPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<DecodedBitmap> DecodeLinearJxlWithDjxlAsync(
        string path,
        JxlProbeResult info,
        int? maxPixelSize,
        bool preserveAlpha,
        CancellationToken cancellationToken)
    {
        var djxl = NativeToolLocator.FindTool("djxl.exe")
            ?? throw new InvalidOperationException("未找到 djxl.exe，无法预览线性 HDR JPEG XL。");
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "jxl-linear-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var exrPath = Path.Combine(tempDir, "decoded.exr");
        try
        {
            // Integer PNG/PPM clamps floating-point HDR values to [0, 1]. EXR
            // retains extended-range RGB and alpha; request linear BT.709 so
            // wide-gamut JXL never relies on an EXR reader's default primaries.
            using var process = NativeProcessRunner.Create(djxl);
            foreach (var argument in new[] { path, exrPath, "--quiet", "--color_space=RGB_D65_SRG_Rel_Lin" })
                process.StartInfo.ArgumentList.Add(argument);
            var downsampling = CalculateJxlDownsampling(info, maxPixelSize);
            if (downsampling > 1)
            {
                process.StartInfo.ArgumentList.Add("--downsampling");
                process.StartInfo.ArgumentList.Add(downsampling.ToString(CultureInfo.InvariantCulture));
            }
            var timer = Stopwatch.StartNew();
            await NativeProcessRunner.RunAsync(process, "libjxl djxl floating-point EXR", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the source EXR scene-linear units. Do not apply an sRGB
            // curve, display-nits tone map, or the PFM fallback's scRGB tag.
            var bitmap = await Task.Run(() => NativeExrDecoder.Decode(exrPath, maxPixelSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (preserveAlpha) UnpremultiplyExrThumbnailPixels(bitmap, cancellationToken);
            bitmap = DownscalePreviewBitmapIfNeeded(bitmap, maxPixelSize, cancellationToken);
            return bitmap with
            {
                DecoderName = $"libjxl djxl [floating-point EXR, linear BT.709, {timer.ElapsedMilliseconds}ms, downsampling {downsampling}x]; {info.TransferSummary}; {info.ColorSummary}"
            };
        }
        finally
        {
            TryDeleteFile(exrPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static DecodedBitmapTransfer DecodeTransferFromJxl(JxlProbeResult info)
    {
        return info.TransferFunction.Contains("PQ", StringComparison.OrdinalIgnoreCase)
            ? DecodedBitmapTransfer.Pq
            : info.TransferFunction.Contains("HLG", StringComparison.OrdinalIgnoreCase)
                ? DecodedBitmapTransfer.Hlg
                : info.TransferFunction.Contains("linear", StringComparison.OrdinalIgnoreCase)
                    ? DecodedBitmapTransfer.LinearSceneScRgb
                    : info.TransferFunction.Contains("709", StringComparison.OrdinalIgnoreCase)
                        ? DecodedBitmapTransfer.Bt709
                        : DecodedBitmapTransfer.Sdr;
    }

    private static int CalculateJxlDownsampling(JxlProbeResult info, int? maxPixelSize)
    {
        if (maxPixelSize is not > 0
            || info.PixelWidth is not > 0
            || info.PixelHeight is not > 0)
        {
            return 1;
        }

        var ratio = Math.Max(info.PixelWidth.Value, info.PixelHeight.Value) / (double)maxPixelSize.Value;
        if (ratio >= 7.0)
        {
            return 8;
        }

        if (ratio >= 3.5)
        {
            return 4;
        }

        return ratio >= 1.75 ? 2 : 1;
    }

    internal static DecodedBitmap DownscalePreviewBitmapIfNeeded(
        DecodedBitmap bitmap, int? maxPixelSize, CancellationToken cancellationToken) =>
        BitmapPreviewResampler.Downscale(bitmap, maxPixelSize, cancellationToken);

    private static async Task<DecodedBitmap> DecodeExrWithNativeToolAsync(
        string path,
        int? maxPixelSize,
        bool preserveAlpha,
        CancellationToken cancellationToken)
    {
        string? nativeFallbackReason = null;
        try
        {
            var bitmap = await Task.Run(() => NativeExrDecoder.Decode(path, maxPixelSize), cancellationToken);
            if (preserveAlpha) UnpremultiplyExrThumbnailPixels(bitmap, cancellationToken);
            return DownscalePreviewBitmapIfNeeded(bitmap, maxPixelSize, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            nativeFallbackReason = $"{ex.GetType().Name}: {ex.Message}";
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "exr-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pfmPath = Path.Combine(tempDir, "decoded.pfm");
        try
        {
            var backend = await ConvertExrToPfmAsync(path, pfmPath, cancellationToken);
            var bitmap = PortableImageReader.ReadPfmAsLinearScRgb(
                pfmPath,
                backend,
                cancellationToken);
            bitmap = DownscalePreviewBitmapIfNeeded(bitmap, maxPixelSize, cancellationToken);
            return nativeFallbackReason is null
                ? bitmap
                : bitmap with { DecoderName = $"{bitmap.DecoderName} [fallback because native EXR failed: {nativeFallbackReason}]" };
        }
        catch (Exception ex) when (nativeFallbackReason is not null && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"HdrImageViewer.Native EXR decode failed ({nativeFallbackReason}); fallback EXR decode failed ({ex.GetType().Name}: {ex.Message}).",
                ex);
        }
        finally
        {
            TryDeleteFile(pfmPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static void UnpremultiplyExrThumbnailPixels(DecodedBitmap bitmap, CancellationToken cancellationToken)
    {
        // OpenEXR RGB is associated with alpha. Thumbnail color conversion
        // operates on straight light values and premultiplies only after the
        // sRGB curve; undo EXR association without changing full-size decode.
        var pixels = MemoryMarshal.Cast<byte, Half>(bitmap.RgbaPixels.AsSpan());
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var offset = (y * bitmap.PixelWidth + x) * 4;
                var alpha = (float)pixels[offset + 3];
                if (alpha >= 1f) continue;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = alpha > 0 && float.IsFinite(alpha)
                        ? (Half)Math.Clamp((float)pixels[offset + channel] / alpha, -65504f, 65504f)
                        : (Half)0;
            }
        }
    }

    private static async Task<string> ConvertExrToPfmAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (NativeToolLocator.FindTool("oiiotool.exe") is { } oiiotool)
        {
            using var process = NativeProcessRunner.Create(oiiotool);
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(outputPath);
            await NativeProcessRunner.RunAsync(process, "OpenImageIO oiiotool EXR->PFM", cancellationToken);
            return "OpenImageIO oiiotool -> PFM float";
        }

        if (NativeToolLocator.FindTool("magick.exe") is { } magick)
        {
            using var process = NativeProcessRunner.Create(magick);
            process.StartInfo.ArgumentList.Add(inputPath);
            // EXR samples are already scene-linear; tag them without applying gamma.
            process.StartInfo.ArgumentList.Add("-set");
            process.StartInfo.ArgumentList.Add("colorspace");
            process.StartInfo.ArgumentList.Add("RGB");
            process.StartInfo.ArgumentList.Add("-define");
            process.StartInfo.ArgumentList.Add("quantum:format=floating-point");
            process.StartInfo.ArgumentList.Add("-depth");
            process.StartInfo.ArgumentList.Add("32");
            process.StartInfo.ArgumentList.Add(outputPath);
            await NativeProcessRunner.RunAsync(process, "ImageMagick EXR->PFM", cancellationToken);
            return "ImageMagick -> PFM float";
        }

        throw new InvalidOperationException(
            "未找到 EXR 解码工具。请安装 OpenImageIO 的 oiiotool.exe 或 ImageMagick 的 magick.exe，并将其加入 PATH，或放到 external/openimageio/bin / external/imagemagick/bin。");
    }

    private static DecodedBitmap DecodeWicHalfLinearScRgb(
        string path,
        string decoderName,
        int? maxPixelSize,
        bool usesBt2020Primaries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var setupTimer = Stopwatch.StartNew();
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(
            path,
            FileAccess.Read,
            DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var createMs = setupTimer.ElapsedMilliseconds;

        var convertTimer = Stopwatch.StartNew();
        using var converter = factory.CreateFormatConverter();
        converter.Initialize(
            frame,
            WicPixelFormat.Format64bppRGBAHalf,
            BitmapDitherType.None,
            null!,
            0.0,
            BitmapPaletteType.Custom);
        var convertMs = convertTimer.ElapsedMilliseconds;

        IWICBitmapSource source = converter;
        IWICBitmapScaler? scaler = null;
        var scaleMs = 0L;
        if (BitmapPreviewResampler.TryCalculateScaledSize((int)converter.Size.Width, (int)converter.Size.Height, maxPixelSize, out var scaledWidth, out var scaledHeight))
        {
            var scaleTimer = Stopwatch.StartNew();
            scaler = factory.CreateBitmapScaler();
            scaler.Initialize(converter, scaledWidth, scaledHeight, Vortice.WIC.BitmapInterpolationMode.Fant);
            source = scaler;
            scaleMs = scaleTimer.ElapsedMilliseconds;
        }

        try
        {
            var size = source.Size;
            var width = checked((int)size.Width);
            var height = checked((int)size.Height);
            var stride = checked(width * 8);
            var pixels = new byte[checked(stride * height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var copyTimer = Stopwatch.StartNew();
            try
            {
                source.CopyPixels(
                    new Vortice.Mathematics.RectI(0, 0, width, height),
                    checked((uint)stride),
                    checked((uint)pixels.Length),
                    handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            var copyMs = copyTimer.ElapsedMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();

            return new DecodedBitmap(
                width,
                height,
                pixels,
                ColorManagedToSrgb: false,
                $"{decoderName} [WIC create {createMs}ms, convert {convertMs}ms, scale {scaleMs}ms, copy {copyMs}ms]",
                DecodedBitmapPixelFormat.Rgba16Float,
                DecodedBitmapTransfer.LinearScRgb,
                usesBt2020Primaries);
        }
        finally
        {
            scaler?.Dispose();
        }
    }

    private static async Task<DecodedBitmap> DecodeJpegXrAsync(
        string path,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        Exception? wicHalfFailure = null;
        try
        {
            return await Task.Run(
                () => DecodeWicHalfLinearScRgb(path, "WIC JPEG XR FP16/scRGB", maxPixelSize, false, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            wicHalfFailure = ex;
        }

        Exception? winRtRgba16Failure = null;
        try
        {
            var bitmap = await DecodeFileWithWinRTAsync(
                path,
                colorManageToSrgb: false,
                respectExifOrientation: false,
                BitmapPixelFormat.Rgba16,
                DecodedBitmapTransfer.Sdr,
                usesBt2020Primaries: false,
                "WinRT JPEG XR RGBA16 fallback",
                maxPixelSize,
                cancellationToken);
            return bitmap with
            {
                DecoderName = $"{bitmap.DecoderName} [fallback because {DescribeDecodeFailure("WIC FP16/scRGB", wicHalfFailure)}]",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            winRtRgba16Failure = ex;
        }

        try
        {
            var bitmap = await DecodeFileWithWinRTAsync(
                path,
                colorManageToSrgb: true,
                respectExifOrientation: false,
                BitmapPixelFormat.Rgba8,
                DecodedBitmapTransfer.Sdr,
                usesBt2020Primaries: false,
                "WinRT JPEG XR RGBA8 fallback",
                maxPixelSize,
                cancellationToken);
            return bitmap with
            {
                DecoderName = $"{bitmap.DecoderName} [fallback because {DescribeDecodeFailure("WIC FP16/scRGB", wicHalfFailure)}; {DescribeDecodeFailure("WinRT RGBA16", winRtRgba16Failure)}]",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Windows JPEG XR decode failed ({DescribeDecodeFailure("WIC FP16/scRGB", wicHalfFailure)}; {DescribeDecodeFailure("WinRT RGBA16", winRtRgba16Failure)}; {DescribeDecodeFailure("WinRT RGBA8", ex)}).",
                ex);
        }
    }

    private static string DescribeDecodeFailure(string stage, Exception? exception)
    {
        return exception is null
            ? $"{stage}: not attempted"
            : $"{stage}: {exception.GetType().Name}: {exception.Message}";
    }

    private static string DescribeHdrTransfer(HeifAvifProbeResult probe)
    {
        return probe.TransferCharacteristics == 16 ? "PQ" : "HLG";
    }

    private static string DescribeContainerPrimaries(HeifAvifProbeResult probe)
    {
        return probe.HasBt2020 ? "BT.2020" : "unknown primaries";
    }

    private static string DescribeHeifAvifCodec(string path)
    {
        return string.Equals(Path.GetExtension(path), ".avif", StringComparison.OrdinalIgnoreCase)
            ? "AVIF"
            : "HEIF/HEIC";
    }

    private static DecodedBitmap ConvertHdrEncodedToLinearScRgb(DecodedBitmap bitmap, double? hlgTargetNits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap.Transfer is not (DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg)
            || bitmap.PixelFormat != DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            return bitmap;
        }

        var output = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 8)];
        var hlgTargetScenePeak = Math.Max((float)(hlgTargetNits ?? 1000.0), 100.0f) / 80.0f;
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var bytesPerInputPixel = bitmap.BytesPerPixel;
        var inputPixels = bitmap.RgbaPixels;
        var transfer = bitmap.Transfer;
        System.Threading.Tasks.Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            var rowInputStart = checked(y * width * bytesPerInputPixel);
            var rowOutputStart = checked(y * width * 8);
            for (var x = 0; x < width; x++)
            {
                var inputIndex = checked(rowInputStart + (x * bytesPerInputPixel));
                var outputIndex = checked(rowOutputStart + (x * 8));
                var encoded = new System.Numerics.Vector3(
                    ReadUInt16LittleEndian(inputPixels, inputIndex) / 65535.0f,
                    ReadUInt16LittleEndian(inputPixels, inputIndex + 2) / 65535.0f,
                    ReadUInt16LittleEndian(inputPixels, inputIndex + 4) / 65535.0f);
                var linear = transfer == DecodedBitmapTransfer.Pq
                    ? HdrColorMath.PqToSceneLinear(encoded)
                    : HdrColorMath.HlgToSceneLinear(encoded, hlgTargetScenePeak);
                linear = ThumbnailPixelConverter.ConvertLinearToBt709(linear, bitmap.EffectiveColorGamut);

                WriteHalfLittleEndian(output, outputIndex, linear.X);
                WriteHalfLittleEndian(output, outputIndex + 2, linear.Y);
                WriteHalfLittleEndian(output, outputIndex + 4, linear.Z);
                WriteHalfLittleEndian(output, outputIndex + 6, 1.0f);
            }
        });

        return bitmap with
        {
            RgbaPixels = output,
            ColorManagedToSrgb = false,
            DecoderName = $"{bitmap.DecoderName} -> linear scRGB",
            PixelFormat = DecodedBitmapPixelFormat.Rgba16Float,
            Transfer = DecodedBitmapTransfer.LinearSceneScRgb,
            UsesBt2020Primaries = false,
            ColorGamut = GainMapColorGamut.Bt709,
        };
    }

    private static string DescribeColorGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => "Display P3",
            GainMapColorGamut.Bt2100 => "BT.2020",
            GainMapColorGamut.AdobeRgb => "Adobe RGB",
            GainMapColorGamut.ProPhoto => "ProPhoto RGB",
            _ => "BT.709",
        };
    }

    private static GainMapColorGamut GetHeifColorGamut(HeifAvifProbeResult probe) =>
        probe.ColorPrimaries == 12 ? GainMapColorGamut.DisplayP3
        : probe.HasBt2020 ? GainMapColorGamut.Bt2100 : GainMapColorGamut.Bt709;

    private static string DescribeColorGamutSuffix(GainMapColorGamut gamut)
    {
        return gamut == GainMapColorGamut.Unknown ? string.Empty : $"; {DescribeColorGamut(gamut)}";
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

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        BitmapPixelFormat pixelFormat,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string decoderName,
        int? maxPixelSize,
        CancellationToken cancellationToken,
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown)
    {
        return DecodeBytesAsync(
            encodedBytes,
            0,
            encodedBytes.Length,
            colorManageToSrgb,
            respectExifOrientation,
            pixelFormat,
            transfer,
            usesBt2020Primaries,
            decoderName,
            maxPixelSize,
            cancellationToken,
            colorGamut);
    }

    private static async Task<DecodedBitmap> DecodeBytesAsync(
        byte[] encodedBytes,
        int offset,
        int count,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        BitmapPixelFormat pixelFormat,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string decoderName,
        int? maxPixelSize,
        CancellationToken cancellationToken,
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var memoryStream = new MemoryStream(encodedBytes, offset, count, writable: false, publiclyVisible: false);
        using var stream = memoryStream.AsRandomAccessStream();
        var createTimer = Stopwatch.StartNew();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var createMs = createTimer.ElapsedMilliseconds;
        return await DecodeFromBitmapDecoderAsync(
            decoder,
            colorManageToSrgb,
            respectExifOrientation,
            pixelFormat,
            transfer,
            usesBt2020Primaries,
            decoderName,
            maxPixelSize,
            colorGamut,
            createMs,
            cancellationToken: cancellationToken);
    }

    private static async Task<DecodedBitmap> DecodeFileWithWinRTAsync(
        string path,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        BitmapPixelFormat pixelFormat,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string decoderName,
        int? maxPixelSize,
        CancellationToken cancellationToken,
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown,
        bool preserveAlpha = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var stream = fileStream.AsRandomAccessStream();
        var createTimer = Stopwatch.StartNew();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var createMs = createTimer.ElapsedMilliseconds;
        return await DecodeFromBitmapDecoderAsync(
            decoder,
            colorManageToSrgb,
            respectExifOrientation,
            pixelFormat,
            transfer,
            usesBt2020Primaries,
            decoderName,
            maxPixelSize,
            colorGamut,
            createMs,
            preserveAlpha,
            cancellationToken);
    }

    private static async Task<DecodedBitmap> DecodeFromBitmapDecoderAsync(
        BitmapDecoder decoder,
        bool colorManageToSrgb,
        bool respectExifOrientation,
        BitmapPixelFormat pixelFormat,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string decoderName,
        int? maxPixelSize,
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown,
        long createDecoderMs = 0,
        bool preserveAlpha = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var orientationMode = respectExifOrientation
            ? ExifOrientationMode.RespectExifOrientation
            : ExifOrientationMode.IgnoreExifOrientation;

        var transform = CreateDecodeTransform(
            respectExifOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth,
            respectExifOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight,
            maxPixelSize);

        var pixelTimer = Stopwatch.StartNew();
        var pixelData = await decoder.GetPixelDataAsync(
            pixelFormat,
            preserveAlpha ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore,
            transform,
            orientationMode,
            colorManageToSrgb ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);
        var pixelMs = pixelTimer.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        var detachTimer = Stopwatch.StartNew();
        var pixels = pixelData.DetachPixelData();
        var detachMs = detachTimer.ElapsedMilliseconds;

        var width = transform.ScaledWidth > 0 ? transform.ScaledWidth : respectExifOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth;
        var height = transform.ScaledHeight > 0 ? transform.ScaledHeight : respectExifOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight;
        var timing = $" [WinRT create {createDecoderMs}ms, pixels {pixelMs}ms, detach {detachMs}ms]";
        return new DecodedBitmap(
            checked((int)width),
            checked((int)height),
            pixels,
            colorManageToSrgb,
            decoderName + timing,
            pixelFormat == BitmapPixelFormat.Rgba16 ? DecodedBitmapPixelFormat.Rgba16Unorm : DecodedBitmapPixelFormat.Rgba8Unorm,
            transfer,
            usesBt2020Primaries,
            colorGamut);
    }

    private static BitmapTransform CreateDecodeTransform(uint width, uint height, int? maxPixelSize)
    {
        if (!BitmapPreviewResampler.TryCalculateScaledSize((int)width, (int)height, maxPixelSize, out var scaledWidth, out var scaledHeight))
        {
            return new BitmapTransform();
        }

        return new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
        };
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
