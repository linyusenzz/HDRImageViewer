using System.Buffers.Binary;
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
        return await DecodeFileCoreAsync(path, heifAvifProbe, maxPixelSize, allowHdrDownscale: false, preserveHdrTransfer: true, cancellationToken);
    }

    public static async Task<DecodedBitmap> DecodeFileForThumbnailAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(path, heifAvifProbe, maxPixelSize, allowHdrDownscale: true, preserveHdrTransfer: true, cancellationToken);
    }

    public static async Task<DecodedBitmap> DecodeFileForHdrExportAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe = null,
        CancellationToken cancellationToken = default)
    {
        return await DecodeFileCoreAsync(path, heifAvifProbe, maxPixelSize: null, allowHdrDownscale: false, preserveHdrTransfer: true, cancellationToken);
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

    private static async Task<DecodedBitmap> DecodeFileCoreAsync(
        string path,
        HeifAvifProbeResult? heifAvifProbe,
        int? maxPixelSize,
        bool allowHdrDownscale,
        bool preserveHdrTransfer,
        CancellationToken cancellationToken)
    {
        var containerKind = await FileSignatureProbe.DetectAsync(path, cancellationToken);
        try
        {
            if (string.Equals(Path.GetExtension(path), ".jxl", StringComparison.OrdinalIgnoreCase))
            {
                return await DecodeJxlWithDjxlAsync(path, maxPixelSize, allowHdrDownscale, preserveHdrTransfer, cancellationToken);
            }

            if (string.Equals(Path.GetExtension(path), ".exr", StringComparison.OrdinalIgnoreCase))
            {
                return await DecodeExrWithNativeToolAsync(path, cancellationToken);
            }

            if (DecoderCatalog.IsJpegXrExtension(Path.GetExtension(path)))
            {
                if (!allowHdrDownscale)
                {
                    maxPixelSize = null;
                }

                return await Task.Run(
                    () => DecodeWicHalfLinearScRgb(path, "WIC JPEG XR FP16/scRGB", maxPixelSize, false, cancellationToken),
                    cancellationToken);
            }

            if (await WicImageProbe.ProbeAsync(path, cancellationToken) is { } wicProbe
                && (wicProbe.IsPng || wicProbe.IsTiff)
                && (wicProbe.IsHighBitDepth || wicProbe.IsPngHdr || wicProbe.IsTiffPq
                    || wicProbe.UsesDisplayP3Primaries || wicProbe.UsesBt2020Primaries))
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
                                wicProbe.IsTiffBt2020,
                                cancellationToken),
                            cancellationToken);
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

                var colorGamut = wicProbe.UsesBt2020Primaries
                    ? GainMapColorGamut.Bt2100
                    : wicProbe.UsesDisplayP3Primaries
                        ? GainMapColorGamut.DisplayP3
                        : GainMapColorGamut.Unknown;

                // For wide-gamut (Display P3 / BT.2020) SDR images we deliberately
                // do NOT hard-convert to sRGB here. Decoding with DoNotColorManage
                // keeps the raw wide-gamut pixels, and tagging the bitmap with its
                // ColorGamut lets the base-image shader run P3->BT.709 /
                // BT.2020->BT.709 into extended-range scRGB. Those out-of-sRGB
                // channels (including small negatives) are exactly the wide-gamut
                // signal the FP16 scRGB swap chain forwards to the Windows
                // wide-gamut compositor. A WIC ICC->sRGB transform would instead
                // clamp the gamut into BT.709 and make saturated colours look
                // desaturated on a wide-gamut display.
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
                        : $"{wicProbe.DecoderName} high bit-depth{DescribeColorGamutSuffix(colorGamut)}",
                    maxPixelSize,
                    cancellationToken,
                    colorGamut);
            }

            if (heifAvifProbe?.HasHdrTransfer == true)
            {
                if (!allowHdrDownscale)
                {
                    maxPixelSize = null;
                }

                var isAvifHdr = string.Equals(Path.GetExtension(path), ".avif", StringComparison.OrdinalIgnoreCase);
                if (isAvifHdr)
                {
                    try
                    {
                        var bitmap = await DecodeHeifAvifHdrWithNativeToolAsync(path, heifAvifProbe, maxPixelSize, cancellationToken);
                        return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0);
                    }
                    catch
                    {
                    }
                }

                string? libheifFallbackReason = null;
                try
                {
                    var bitmap = await Task.Run(
                        () => DecodeHeifAvifHdrWithLibheif(path, heifAvifProbe, cancellationToken),
                        cancellationToken);
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0);
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
                    var bitmap = await DecodeHeifAvifHdrWithNativeToolAsync(path, heifAvifProbe, maxPixelSize, cancellationToken);
                    if (libheifFallbackReason is not null)
                    {
                        bitmap = bitmap with { DecoderName = $"{bitmap.DecoderName} [fallback because {libheifFallbackReason}]" };
                    }
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0);
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
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0);
                }
                catch (Exception ex)
                {
                    wicHalfFallbackFailure = ex;
                }

                Exception? winRtFallbackFailure = null;
                var fallbackBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                try
                {
                    var bitmap = await DecodeHeifHdrBytesAsync(path, fallbackBytes, heifAvifProbe, maxPixelSize, cancellationToken);
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
                    return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, hlgTargetNits: 1000.0);
                }
                catch (Exception ex)
                {
                    winRtFallbackFailure = ex;
                }

                throw new InvalidOperationException(
                    $"Windows Imaging {DescribeHeifAvifCodec(path)} HDR decode failed (WIC FP16: {wicHalfFallbackFailure?.GetType().Name}: {wicHalfFallbackFailure?.Message}; WinRT Rgba16: {winRtFallbackFailure?.GetType().Name}: {winRtFallbackFailure?.Message}).",
                    winRtFallbackFailure ?? wicHalfFallbackFailure);
            }

            var encodedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return await DecodeBytesAsync(
                encodedBytes,
                colorManageToSrgb: true,
                respectExifOrientation: true,
                maxPixelSize,
                cancellationToken);
        }
        catch (Exception ex) when (containerKind == FileContainerKind.HeifFamily)
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

    private static async Task<DecodedBitmap> DecodeHeifHdrBytesAsync(
        string path,
        byte[] encodedBytes,
        HeifAvifProbeResult probe,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        var bitmap = await DecodeBytesAsync(
            encodedBytes,
            colorManageToSrgb: false,
            respectExifOrientation: true,
            BitmapPixelFormat.Rgba16,
            probe.TransferCharacteristics == 16 ? DecodedBitmapTransfer.Pq : DecodedBitmapTransfer.Hlg,
            probe.HasBt2020,
            $"Windows Imaging {DescribeHeifAvifCodec(path)} HDR",
            maxPixelSize,
            cancellationToken);
        return bitmap;
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
            Marshal.Copy(IntPtr.Add(src, y * stride), pixels, y * rowBytes, rowBytes);
        }

        var sourceBitDepth = Math.Clamp(handle.BitDepth, 8, 16);
        if (sourceBitDepth < 16)
        {
            var leftShift = 16 - sourceBitDepth;
            var rightShift = sourceBitDepth - leftShift;
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
        CancellationToken cancellationToken)
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
            using var process = CreateNativeProcess(tool);
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
            await RunProcessAsync(process, isAvif ? "libavif avifdec" : "libheif heif-dec", cancellationToken);
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
                cancellationToken);
            var winrtMs = winrtTimer.ElapsedMilliseconds;
            return bitmap with { DecoderName = $"{bitmap.DecoderName} [winrt png {winrtMs}ms]" };
        }
        finally
        {
            TryDeleteFile(pngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<DecodedBitmap> DecodeJxlWithDjxlAsync(
        string path,
        int? maxPixelSize,
        bool allowHdrDownscale,
        bool preserveHdrTransfer,
        CancellationToken cancellationToken)
    {
        var djxl = NativeToolLocator.FindTool("djxl.exe")
            ?? throw new InvalidOperationException("未找到 djxl.exe，无法预览 JPEG XL。请安装 libjxl 工具或把 djxl.exe 放到 PATH。");
        var info = await JxlProbe.ProbeAsync(path, cancellationToken)
            ?? new JxlProbeResult(true, null, null, null, "未知", "未知", null, null, "JPEG XL");
        var transfer = DecodeTransferFromJxl(info);
        if (!allowHdrDownscale && transfer is (DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg))
        {
            maxPixelSize = null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "jxl-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pngPath = Path.Combine(tempDir, "decoded.png");
        try
        {
            using var process = CreateNativeProcess(djxl);
            process.StartInfo.ArgumentList.Add(path);
            process.StartInfo.ArgumentList.Add(pngPath);
            process.StartInfo.ArgumentList.Add("--quiet");
            await RunProcessAsync(process, "libjxl djxl", cancellationToken);
            var bitmap = await DecodeFileWithWinRTAsync(
                pngPath,
                colorManageToSrgb: transfer == DecodedBitmapTransfer.Sdr,
                respectExifOrientation: false,
                transfer == DecodedBitmapTransfer.Sdr ? BitmapPixelFormat.Rgba8 : BitmapPixelFormat.Rgba16,
                transfer,
                info.UsesBt2020Primaries,
                $"libjxl djxl; {info.TransferSummary}; {info.ColorSummary}",
                maxPixelSize,
                cancellationToken);
            return preserveHdrTransfer ? bitmap : ConvertHdrEncodedToLinearScRgb(bitmap, info.IntensityTargetNits);
        }
        finally
        {
            TryDeleteFile(pngPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static DecodedBitmapTransfer DecodeTransferFromJxl(JxlProbeResult info)
    {
        return info.TransferFunction.Contains("PQ", StringComparison.OrdinalIgnoreCase)
            ? DecodedBitmapTransfer.Pq
            : info.TransferFunction.Contains("HLG", StringComparison.OrdinalIgnoreCase)
                ? DecodedBitmapTransfer.Hlg
                : DecodedBitmapTransfer.Sdr;
    }

    private static async Task<DecodedBitmap> DecodeExrWithNativeToolAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string? nativeFallbackReason = null;
        try
        {
            return await Task.Run(() => NativeExrDecoder.Decode(path), cancellationToken);
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
            var bitmap = ReadPfmAsLinearScRgb(pfmPath, backend);
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

    private static async Task<string> ConvertExrToPfmAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (NativeToolLocator.FindTool("oiiotool.exe") is { } oiiotool)
        {
            using var process = CreateNativeProcess(oiiotool);
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(outputPath);
            await RunProcessAsync(process, "OpenImageIO oiiotool EXR->PFM", cancellationToken);
            return "OpenImageIO oiiotool -> PFM float";
        }

        if (NativeToolLocator.FindTool("magick.exe") is { } magick)
        {
            using var process = CreateNativeProcess(magick);
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-colorspace");
            process.StartInfo.ArgumentList.Add("RGB");
            process.StartInfo.ArgumentList.Add("-define");
            process.StartInfo.ArgumentList.Add("quantum:format=floating-point");
            process.StartInfo.ArgumentList.Add("-depth");
            process.StartInfo.ArgumentList.Add("32");
            process.StartInfo.ArgumentList.Add(outputPath);
            await RunProcessAsync(process, "ImageMagick EXR->PFM", cancellationToken);
            return "ImageMagick -> PFM float";
        }

        throw new InvalidOperationException(
            "未找到 EXR 解码工具。请安装 OpenImageIO 的 oiiotool.exe 或 ImageMagick 的 magick.exe，并将其加入 PATH，或放到 external/openimageio/bin / external/imagemagick/bin。");
    }

    private static DecodedBitmap ReadPfmAsLinearScRgb(string path, string backendName)
    {
        var data = File.ReadAllBytes(path);
        var offset = 0;
        var magic = ReadPfmToken(data, ref offset);
        var channels = magic switch
        {
            "PF" => 3,
            "Pf" => 1,
            _ => throw new InvalidOperationException("EXR 中间 PFM 输出无效：缺少 PF/Pf 文件头。"),
        };

        var width = int.Parse(ReadPfmToken(data, ref offset), CultureInfo.InvariantCulture);
        var height = int.Parse(ReadPfmToken(data, ref offset), CultureInfo.InvariantCulture);
        var scale = float.Parse(ReadPfmToken(data, ref offset), CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"EXR 中间 PFM 输出尺寸无效：{width}x{height}。");
        }

        var littleEndian = scale < 0;
        var expectedBytes = checked(width * height * channels * sizeof(float));
        if (data.Length - offset < expectedBytes)
        {
            throw new InvalidOperationException("EXR 中间 PFM 输出不完整。");
        }

        var pixels = new byte[checked(width * height * 8)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = checked(offset + (((sourceY * width) + x) * channels * sizeof(float)));
                var destinationIndex = checked(((y * width) + x) * 8);
                var r = ReadPfmFloat(data, sourceIndex, littleEndian);
                var g = channels == 1 ? r : ReadPfmFloat(data, sourceIndex + sizeof(float), littleEndian);
                var b = channels == 1 ? r : ReadPfmFloat(data, sourceIndex + (2 * sizeof(float)), littleEndian);
                WriteHalfLittleEndian(pixels, destinationIndex, r);
                WriteHalfLittleEndian(pixels, destinationIndex + 2, g);
                WriteHalfLittleEndian(pixels, destinationIndex + 4, b);
                WriteHalfLittleEndian(pixels, destinationIndex + 6, 1.0f);
            }
        }

        return new DecodedBitmap(
            width,
            height,
            pixels,
            ColorManagedToSrgb: false,
            $"OpenEXR scene-linear via {backendName}",
            DecodedBitmapPixelFormat.Rgba16Float,
            DecodedBitmapTransfer.LinearScRgb,
            UsesBt2020Primaries: false);
    }

    private static string ReadPfmToken(byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            var value = data[offset];
            if (value == '#')
            {
                while (offset < data.Length && data[offset] is not (byte)'\n')
                {
                    offset++;
                }
                continue;
            }

            if (!char.IsWhiteSpace((char)value))
            {
                break;
            }

            offset++;
        }

        var start = offset;
        while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]))
        {
            offset++;
        }

        if (start == offset)
        {
            throw new InvalidOperationException("EXR 中间 PFM 输出无效：文件头不完整。");
        }

        return System.Text.Encoding.ASCII.GetString(data, start, offset - start);
    }

    private static float ReadPfmFloat(byte[] data, int offset, bool littleEndian)
    {
        var bits = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(float)))
            : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, sizeof(float)));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static DecodedBitmap DecodeWicHalfLinearScRgb(
        string path,
        string decoderName,
        int? maxPixelSize,
        bool usesBt2020Primaries,
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

        IWICBitmapSource source = converter;
        IWICBitmapScaler? scaler = null;
        if (TryCalculateScaledSize((int)converter.Size.Width, (int)converter.Size.Height, maxPixelSize, out var scaledWidth, out var scaledHeight))
        {
            scaler = factory.CreateBitmapScaler();
            scaler.Initialize(converter, scaledWidth, scaledHeight, Vortice.WIC.BitmapInterpolationMode.Fant);
            source = scaler;
        }

        try
        {
            var size = source.Size;
            var width = checked((int)size.Width);
            var height = checked((int)size.Height);
            var stride = checked(width * 8);
            var pixels = new byte[checked(stride * height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
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

            return new DecodedBitmap(
                width,
                height,
                pixels,
                ColorManagedToSrgb: false,
                decoderName,
                DecodedBitmapPixelFormat.Rgba16Float,
                DecodedBitmapTransfer.LinearScRgb,
                usesBt2020Primaries);
        }
        finally
        {
            scaler?.Dispose();
        }
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

    private static DecodedBitmap ConvertHdrEncodedToLinearScRgb(DecodedBitmap bitmap, double? hlgTargetNits)
    {
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
        var usesBt2020 = bitmap.UsesBt2020Primaries;
        System.Threading.Tasks.Parallel.For(0, height, y =>
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
                if (usesBt2020)
                {
                    linear = HdrColorMath.Bt2020ToBt709(linear);
                }

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
            _ => "BT.709",
        };
    }

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
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await DecodeFromBitmapDecoderAsync(
            decoder,
            colorManageToSrgb,
            respectExifOrientation,
            pixelFormat,
            transfer,
            usesBt2020Primaries,
            decoderName,
            maxPixelSize,
            colorGamut);
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
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown)
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
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await DecodeFromBitmapDecoderAsync(
            decoder,
            colorManageToSrgb,
            respectExifOrientation,
            pixelFormat,
            transfer,
            usesBt2020Primaries,
            decoderName,
            maxPixelSize,
            colorGamut);
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
        GainMapColorGamut colorGamut = GainMapColorGamut.Unknown)
    {
        var orientationMode = respectExifOrientation
            ? ExifOrientationMode.RespectExifOrientation
            : ExifOrientationMode.IgnoreExifOrientation;

        var transform = CreateDecodeTransform(
            respectExifOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth,
            respectExifOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight,
            maxPixelSize);

        var pixelData = await decoder.GetPixelDataAsync(
            pixelFormat,
            BitmapAlphaMode.Ignore,
            transform,
            orientationMode,
            colorManageToSrgb ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);

        var width = transform.ScaledWidth > 0 ? transform.ScaledWidth : respectExifOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth;
        var height = transform.ScaledHeight > 0 ? transform.ScaledHeight : respectExifOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight;
        return new DecodedBitmap(
            checked((int)width),
            checked((int)height),
            pixelData.DetachPixelData(),
            colorManageToSrgb,
            decoderName,
            pixelFormat == BitmapPixelFormat.Rgba16 ? DecodedBitmapPixelFormat.Rgba16Unorm : DecodedBitmapPixelFormat.Rgba8Unorm,
            transfer,
            usesBt2020Primaries,
            colorGamut);
    }

    private static BitmapTransform CreateDecodeTransform(uint width, uint height, int? maxPixelSize)
    {
        if (!TryCalculateScaledSize((int)width, (int)height, maxPixelSize, out var scaledWidth, out var scaledHeight))
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

    private static bool TryCalculateScaledSize(
        int width,
        int height,
        int? maxPixelSize,
        out uint scaledWidth,
        out uint scaledHeight)
    {
        scaledWidth = 0;
        scaledHeight = 0;
        if (maxPixelSize is null || maxPixelSize <= 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var largerSide = Math.Max(width, height);
        if (largerSide <= maxPixelSize.Value)
        {
            return false;
        }

        var scale = maxPixelSize.Value / (double)largerSide;
        scaledWidth = Math.Max(1u, (uint)Math.Round(width * scale));
        scaledHeight = Math.Max(1u, (uint)Math.Round(height * scale));
        return true;
    }

    private static Process CreateNativeProcess(string executablePath)
    {
        var process = new Process();
        process.StartInfo.FileName = executablePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }

    private static async Task<string> RunProcessAsync(Process process, string backendName, CancellationToken cancellationToken)
    {
        if (!process.Start())
        {
            throw new InvalidOperationException($"启动 {backendName} 失败。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = string.Join("\n", new[] { output.Trim(), error.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
            throw new InvalidOperationException($"{backendName} 失败，exit {process.ExitCode}: {message}");
        }

        return string.Join("\n", new[] { output.Trim(), error.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have already exited or be inaccessible.
        }
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
