using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

/// <summary>
/// In-process JPEG XL probing and decoding through P/Invoke bindings to the
/// bundled libjxl (libjxl.dll / jxl.dll). Supports container/codestream
/// metadata probing, PQ/HLG RGBA16, SDR RGBA8/RGBA16 (including Display P3 and
/// wide-gamut preservation), scene-linear Float16 RGBA16F, straight and
/// associated alpha channels, and in-memory jhgm gain-map codestream decoding.
/// </summary>
public static class JxlNativeDecoder
{
    private const string JxlLibraryName = "jxl.dll";
    private const string JxlThreadsLibraryName = "jxl_threads.dll";
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;

    // JxlDecoderStatus values from libjxl decode.h.
    private const int JXL_DEC_SUCCESS = 0;
    private const int JXL_DEC_NEED_MORE_INPUT = 2;
    private const int JXL_DEC_NEED_IMAGE_OUT_BUFFER = 5;
    private const int JXL_DEC_BASIC_INFO = 0x40;
    private const int JXL_DEC_COLOR_ENCODING = 0x100;
    private const int JXL_DEC_FULL_IMAGE = 0x1000;
    private const int JXL_DEC_BOX = 0x4000;

    // JxlDataType / JxlEndianness values from libjxl types.h.
    private const int JXL_TYPE_UINT8 = 2;
    private const int JXL_TYPE_UINT16 = 3;
    private const int JXL_TYPE_FLOAT16 = 4;
    private const int JXL_LITTLE_ENDIAN = 1;

    // JxlColorProfileTarget values from libjxl color_encoding.h / decode.h.
    private const int JXL_COLOR_PROFILE_TARGET_ORIGINAL = 0;
    private const int JXL_COLOR_PROFILE_TARGET_DATA = 1;

    // JxlColorSpace / JxlWhitePoint / JxlPrimaries / JxlTransferFunction / JxlRenderingIntent.
    private const int JXL_COLOR_SPACE_RGB = 0;
    private const int JXL_COLOR_SPACE_GRAY = 1;
    private const int JXL_WHITE_POINT_D65 = 1;
    private const int JXL_PRIMARIES_SRGB = 1;
    private const int JXL_PRIMARIES_CUSTOM = 2;
    private const int JXL_PRIMARIES_2100 = 9;
    private const int JXL_PRIMARIES_P3 = 11;
    private const int JXL_TRANSFER_FUNCTION_709 = 1;
    private const int JXL_TRANSFER_FUNCTION_LINEAR = 8;
    private const int JXL_TRANSFER_FUNCTION_SRGB = 13;
    private const int JXL_TRANSFER_FUNCTION_PQ = 16;
    private const int JXL_TRANSFER_FUNCTION_DCI = 17;
    private const int JXL_TRANSFER_FUNCTION_HLG = 18;
    private const int JXL_TRANSFER_FUNCTION_GAMMA = 65535;
    private const int JXL_RENDERING_INTENT_RELATIVE = 1;

    private const int MaxProbeCodestreamBytes = 1024 * 1024;
    private const int MaxIccProfileBytes = 4 * 1024 * 1024;
    private const uint JhgmBoxFourCcLittleEndian = 0x6D67686Au; // 'j', 'h', 'g', 'm'

    private static readonly object LoadLock = new();
    private static bool _librariesLoaded;
    private static IntPtr _threadParallelRunnerFunction;
    private static uint _decoderVersion;

    public static bool IsAvailable =>
        NativeToolLocator.FindFirstTool("libjxl.dll", JxlLibraryName) is not null;

    public static JxlProbeResult? TryProbe(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureNativeLibrariesLoaded();
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);
            var fileLength = stream.Length;
            if (fileLength < 2)
            {
                return null;
            }

            Span<byte> prefix = stackalloc byte[12];
            var prefixRead = ReadUpTo(stream, prefix);
            if (prefixRead < 2)
            {
                return null;
            }

            // Naked codestream: 0xFF 0x0A
            if (prefix[0] == 0xFF && prefix[1] == 0x0A)
            {
                stream.Position = 0;
                var readCount = (int)Math.Min(fileLength, MaxProbeCodestreamBytes);
                var rented = ArrayPool<byte>.Shared.Rent(readCount);
                try
                {
                    var actualRead = ReadUpTo(stream, rented.AsSpan(0, readCount));
                    return TryProbePinnedArray(rented, actualRead, knownHasGainMapBox: false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            // ISO BMFF JPEG XL container: 00 00 00 0C 'JXL ' 0D 0A 87 0A
            if (prefixRead == 12 && FileSignatureProbe.IsJxl(prefix))
            {
                var hasGainMapBox = false;
                byte[]? codestreamBuffer = null;
                var codestreamLength = 0;
                try
                {
                    Span<byte> boxHeader = stackalloc byte[16];
                    Span<byte> wrappedType = stackalloc byte[4];
                    long position = 12;
                    while (position + 8 <= fileLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Position = position;
                        if (ReadUpTo(stream, boxHeader[..8]) < 8)
                        {
                            break;
                        }

                        var size32 = BinaryPrimitives.ReadUInt32BigEndian(boxHeader[..4]);
                        var headerSize = 8;
                        long boxSize = size32;
                        if (size32 == 1)
                        {
                            if (position + 16 > fileLength || ReadUpTo(stream, boxHeader.Slice(8, 8)) < 8)
                            {
                                break;
                            }

                            var size64 = BinaryPrimitives.ReadUInt64BigEndian(boxHeader.Slice(8, 8));
                            if (size64 > long.MaxValue)
                            {
                                break;
                            }

                            boxSize = (long)size64;
                            headerSize = 16;
                        }
                        else if (size32 == 0)
                        {
                            boxSize = fileLength - position;
                        }

                        if (boxSize < headerSize || position + boxSize > fileLength)
                        {
                            break;
                        }

                        var payloadLength = boxSize - headerSize;
                        var typeSpan = boxHeader.Slice(4, 4);
                        if (typeSpan.SequenceEqual("jhgm"u8))
                        {
                            hasGainMapBox = true;
                        }
                        else if (typeSpan.SequenceEqual("brob"u8) && payloadLength >= 4)
                        {
                            if (ReadUpTo(stream, wrappedType) == 4 && wrappedType.SequenceEqual("jhgm"u8))
                            {
                                hasGainMapBox = true;
                            }
                        }
                        else if (typeSpan.SequenceEqual("jxlc"u8) && codestreamLength < MaxProbeCodestreamBytes && payloadLength > 0)
                        {
                            var toRead = (int)Math.Min(payloadLength, MaxProbeCodestreamBytes - codestreamLength);
                            codestreamBuffer ??= ArrayPool<byte>.Shared.Rent(MaxProbeCodestreamBytes);
                            var read = ReadUpTo(stream, codestreamBuffer.AsSpan(codestreamLength, toRead));
                            codestreamLength += read;
                        }
                        else if (typeSpan.SequenceEqual("jxlp"u8) && codestreamLength < MaxProbeCodestreamBytes && payloadLength > 4)
                        {
                            stream.Position = position + headerSize + 4;
                            var toRead = (int)Math.Min(payloadLength - 4, MaxProbeCodestreamBytes - codestreamLength);
                            codestreamBuffer ??= ArrayPool<byte>.Shared.Rent(MaxProbeCodestreamBytes);
                            var read = ReadUpTo(stream, codestreamBuffer.AsSpan(codestreamLength, toRead));
                            codestreamLength += read;
                        }

                        position += boxSize;
                    }

                    if (codestreamBuffer is not null && codestreamLength >= 2)
                    {
                        return TryProbePinnedArray(codestreamBuffer, codestreamLength, hasGainMapBox);
                    }
                }
                finally
                {
                    if (codestreamBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(codestreamBuffer);
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static JxlProbeResult? TryProbe(ReadOnlySpan<byte> data)
    {
        if (!FileSignatureProbe.IsJxl(data))
        {
            return null;
        }

        try
        {
            EnsureNativeLibrariesLoaded();
            var rented = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(rented);
                return TryProbePinnedArray(rented, data.Length, knownHasGainMapBox: false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch
        {
            return null;
        }
    }

    public static DecodedBitmap Decode(
        string path,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string colorDescription,
        CancellationToken cancellationToken)
    {
        var gamut = usesBt2020Primaries ? GainMapColorGamut.Bt2100 : GainMapColorGamut.Bt709;
        return DecodeFile(
            path,
            transfer,
            gamut,
            colorDescription,
            preserveAlpha: false,
            cancellationToken);
    }

    public static DecodedBitmap DecodeFile(
        string path,
        DecodedBitmapTransfer transfer,
        GainMapColorGamut knownGamut,
        string colorDescription,
        bool preserveAlpha,
        CancellationToken cancellationToken)
    {
        EnsureNativeLibrariesLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var readTimer = Stopwatch.StartNew();
        var encodedBytes = File.ReadAllBytes(path);
        var readMs = readTimer.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        return DecodeCore(
            encodedBytes,
            encodedBytes.Length,
            transfer,
            knownGamut,
            colorDescription,
            preserveAlpha,
            forceRawRgba16: false,
            readMs,
            cancellationToken);
    }

    public static DecodedBitmap DecodeCodestream(
        ReadOnlySpan<byte> codestream,
        DecodedBitmapTransfer transfer,
        GainMapColorGamut knownGamut,
        string colorDescription,
        bool preserveAlpha,
        CancellationToken cancellationToken = default)
    {
        EnsureNativeLibrariesLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, codestream.Length));
        try
        {
            codestream.CopyTo(rented);
            return DecodeCore(
                rented,
                codestream.Length,
                transfer,
                knownGamut,
                colorDescription,
                preserveAlpha,
                forceRawRgba16: false,
                readMs: null,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static DecodedBitmap DecodeGainMapCodestream(
        ReadOnlySpan<byte> codestream,
        CancellationToken cancellationToken = default)
    {
        EnsureNativeLibrariesLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, codestream.Length));
        try
        {
            codestream.CopyTo(rented);
            return DecodeCore(
                rented,
                codestream.Length,
                DecodedBitmapTransfer.Sdr,
                GainMapColorGamut.Unknown,
                "libjxl jhgm gain map",
                preserveAlpha: false,
                forceRawRgba16: true,
                readMs: null,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static JxlProbeResult? TryProbePinnedArray(byte[] data, int length, bool knownHasGainMapBox)
    {
        if (length <= 0 || _decoderVersion < 9000)
        {
            return null;
        }

        var decoder = JxlDecoderCreate(IntPtr.Zero);
        if (decoder == IntPtr.Zero)
        {
            return null;
        }

        var inputHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _ = JxlDecoderSetDecompressBoxes(decoder, 1);
            var events = JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | (knownHasGainMapBox ? 0 : JXL_DEC_BOX);
            if (JxlDecoderSubscribeEvents(decoder, events) != JXL_DEC_SUCCESS)
            {
                return null;
            }

            if (JxlDecoderSetInput(decoder, inputHandle.AddrOfPinnedObject(), (nuint)length) != JXL_DEC_SUCCESS)
            {
                return null;
            }

            JxlDecoderCloseInput(decoder);

            var gotBasicInfo = false;
            var gotColorEncoding = false;
            var hasGainMapBox = knownHasGainMapBox;
            JxlBasicInfo basicInfo = default;
            var transferFunction = "Unknown";
            var colorPrimaries = "Unknown";

            while (true)
            {
                var status = JxlDecoderProcessInput(decoder);
                if (status == JXL_DEC_BASIC_INFO)
                {
                    if (JxlDecoderGetBasicInfo(decoder, out basicInfo) != JXL_DEC_SUCCESS)
                    {
                        return null;
                    }

                    gotBasicInfo = true;
                    if (gotColorEncoding && knownHasGainMapBox)
                    {
                        break;
                    }
                }
                else if (status == JXL_DEC_COLOR_ENCODING)
                {
                    gotColorEncoding = true;
                    ResolveColorStrings(decoder, basicInfo, out transferFunction, out colorPrimaries);
                    if (gotBasicInfo && knownHasGainMapBox)
                    {
                        break;
                    }
                }
                else if (status == JXL_DEC_BOX)
                {
                    if (JxlDecoderGetBoxType(decoder, out var rawType, 0) == JXL_DEC_SUCCESS && rawType == JhgmBoxFourCcLittleEndian)
                    {
                        hasGainMapBox = true;
                    }
                    else if (JxlDecoderGetBoxType(decoder, out var unpackedType, 1) == JXL_DEC_SUCCESS && unpackedType == JhgmBoxFourCcLittleEndian)
                    {
                        hasGainMapBox = true;
                    }
                }
                else
                {
                    break;
                }
            }

            if (!gotBasicInfo || basicInfo.Xsize == 0 || basicInfo.Ysize == 0)
            {
                return null;
            }

            if (!gotColorEncoding && basicInfo.ExponentBitsPerSample > 0)
            {
                transferFunction = "linear";
                colorPrimaries = "sRGB / BT.709";
            }

            var hasExplicitLuminance =
                Math.Abs(basicInfo.IntensityTarget - 255.0f) > 0.01f
                || basicInfo.MinNits != 0.0f
                || basicInfo.RelativeToMaxDisplay != 0
                || basicInfo.LinearBelow != 0.0f;

            double? intensityTarget = hasExplicitLuminance && basicInfo.IntensityTarget > 0.0f
                ? Math.Round(basicInfo.IntensityTarget, 3)
                : null;
            double? minNits = hasExplicitLuminance
                ? Math.Round(basicInfo.MinNits, 6)
                : null;

            var channelsLabel = basicInfo.NumColorChannels == 1 ? "Grayscale" : "RGB";
            var alphaLabel = basicInfo.AlphaBits > 0 ? "+Alpha" : string.Empty;
            var summary = $"JPEG XL image, {basicInfo.Xsize}x{basicInfo.Ysize}, {basicInfo.BitsPerSample}-bit {channelsLabel}{alphaLabel}";

            return new JxlProbeResult(
                IsJxl: true,
                PixelWidth: checked((int)basicInfo.Xsize),
                PixelHeight: checked((int)basicInfo.Ysize),
                BitDepth: checked((int)basicInfo.BitsPerSample),
                TransferFunction: transferFunction,
                ColorPrimaries: colorPrimaries,
                IntensityTargetNits: intensityTarget,
                MinNits: minNits,
                Summary: summary,
                HasGainMapBox: hasGainMapBox);
        }
        finally
        {
            JxlDecoderDestroy(decoder);
            inputHandle.Free();
        }
    }

    private static DecodedBitmap DecodeCore(
        byte[] encodedBytes,
        int encodedLength,
        DecodedBitmapTransfer transfer,
        GainMapColorGamut knownGamut,
        string colorDescription,
        bool preserveAlpha,
        bool forceRawRgba16,
        long? readMs,
        CancellationToken cancellationToken)
    {
        if (encodedLength <= 0)
        {
            throw new InvalidOperationException("JPEG XL stream is empty.");
        }

        var decodeTimer = Stopwatch.StartNew();
        var decoder = JxlDecoderCreate(IntPtr.Zero);
        if (decoder == IntPtr.Zero)
        {
            throw new InvalidOperationException("JxlDecoderCreate returned null.");
        }

        var runner = IntPtr.Zero;
        var inputHandle = GCHandle.Alloc(encodedBytes, GCHandleType.Pinned);
        GCHandle outputHandle = default;
        try
        {
            if (_threadParallelRunnerFunction != IntPtr.Zero)
            {
                runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, JxlThreadParallelRunnerDefaultNumWorkerThreads());
                if (runner != IntPtr.Zero)
                {
                    ThrowOnError(JxlDecoderSetParallelRunner(decoder, _threadParallelRunnerFunction, runner), "JxlDecoderSetParallelRunner");
                }
            }

            if (transfer != DecodedBitmapTransfer.LinearSceneScRgb)
            {
                // Must be configured prior to JXL_DEC_BASIC_INFO; libjxl returns
                // JXL_DEC_ERROR if called during or after JXL_DEC_BASIC_INFO, and
                // ignores the flag when the image has no associated alpha channel.
                ThrowOnError(JxlDecoderSetUnpremultiplyAlpha(decoder, 1), "JxlDecoderSetUnpremultiplyAlpha");
            }

            var subscribeColorEncoding = _decoderVersion >= 9000
                && !forceRawRgba16
                && transfer is DecodedBitmapTransfer.Sdr or DecodedBitmapTransfer.Bt709 or DecodedBitmapTransfer.LinearSceneScRgb;
            var events = JXL_DEC_BASIC_INFO | JXL_DEC_FULL_IMAGE | (subscribeColorEncoding ? JXL_DEC_COLOR_ENCODING : 0);
            ThrowOnError(JxlDecoderSubscribeEvents(decoder, events), "JxlDecoderSubscribeEvents");
            ThrowOnError(JxlDecoderSetInput(decoder, inputHandle.AddrOfPinnedObject(), (nuint)encodedLength), "JxlDecoderSetInput");
            JxlDecoderCloseInput(decoder);

            var format = new JxlPixelFormat
            {
                NumChannels = 4,
                DataType = transfer == DecodedBitmapTransfer.LinearSceneScRgb
                    ? JXL_TYPE_FLOAT16
                    : JXL_TYPE_UINT16,
                Endianness = JXL_LITTLE_ENDIAN,
                Align = 0,
            };

            var width = 0;
            var height = 0;
            var bitsPerSample = 8u;
            var hasAlpha = false;
            var alphaPremultiplied = false;
            var usesOriginalProfile = false;
            var resolvedGamut = knownGamut;
            var resolvedTransfer = transfer;
            var colorManagedToSrgb = false;
            byte[]? pixels = null;
            var gotFullImage = false;

            while (!gotFullImage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = JxlDecoderProcessInput(decoder);
                switch (status)
                {
                    case JXL_DEC_BASIC_INFO:
                        ThrowOnError(JxlDecoderGetBasicInfo(decoder, out var info), "JxlDecoderGetBasicInfo");
                        width = checked((int)info.Xsize);
                        height = checked((int)info.Ysize);
                        if (width <= 0 || height <= 0)
                        {
                            throw new InvalidOperationException($"libjxl reported an invalid image size {info.Xsize}x{info.Ysize}.");
                        }

                        bitsPerSample = info.BitsPerSample;
                        hasAlpha = info.AlphaBits != 0;
                        alphaPremultiplied = info.AlphaPremultiplied != 0;
                        usesOriginalProfile = info.UsesOriginalProfile != 0;

                        if (!subscribeColorEncoding && !forceRawRgba16 && resolvedTransfer is DecodedBitmapTransfer.Sdr or DecodedBitmapTransfer.Bt709)
                        {
                            var use16BitSdr = bitsPerSample > 8 || IsWideGamut(resolvedGamut) || resolvedTransfer == DecodedBitmapTransfer.Bt709;
                            format.DataType = use16BitSdr ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8;
                            colorManagedToSrgb = !use16BitSdr && resolvedTransfer == DecodedBitmapTransfer.Sdr;
                        }

                        break;

                    case JXL_DEC_COLOR_ENCODING:
                        if (resolvedTransfer == DecodedBitmapTransfer.LinearSceneScRgb)
                        {
                            if (!usesOriginalProfile)
                            {
                                var preferredLinearSrgb = new JxlColorEncoding
                                {
                                    ColorSpace = JXL_COLOR_SPACE_RGB,
                                    WhitePoint = JXL_WHITE_POINT_D65,
                                    Primaries = JXL_PRIMARIES_SRGB,
                                    TransferFunction = JXL_TRANSFER_FUNCTION_LINEAR,
                                    RenderingIntent = JXL_RENDERING_INTENT_RELATIVE,
                                };
                                _ = JxlDecoderSetPreferredColorProfile(decoder, in preferredLinearSrgb);
                            }

                            resolvedGamut = DetectDataColorGamut(decoder, resolvedGamut);
                        }
                        else if (resolvedTransfer is DecodedBitmapTransfer.Sdr or DecodedBitmapTransfer.Bt709)
                        {
                            resolvedGamut = ValidateAndResolveSdrEncoding(decoder, ref resolvedTransfer);
                            var use16BitSdr = bitsPerSample > 8 || IsWideGamut(resolvedGamut) || resolvedTransfer == DecodedBitmapTransfer.Bt709;
                            format.DataType = use16BitSdr ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8;
                            format.Endianness = format.DataType == JXL_TYPE_UINT8 ? 0 : JXL_LITTLE_ENDIAN;
                            colorManagedToSrgb = !use16BitSdr && !IsWideGamut(resolvedGamut) && resolvedTransfer == DecodedBitmapTransfer.Sdr;
                        }

                        break;

                    case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
                        ThrowOnError(JxlDecoderImageOutBufferSize(decoder, in format, out var bufferSize), "JxlDecoderImageOutBufferSize");
                        var bytesPerPixel = format.DataType == JXL_TYPE_UINT8 ? 4u : 8u;
                        var expectedBytes = checked((nuint)width * (nuint)height * bytesPerPixel);
                        if (bufferSize != expectedBytes)
                        {
                            throw new InvalidOperationException($"libjxl requested an output buffer of {bufferSize} bytes for a {width}x{height} image (expected {expectedBytes}).");
                        }

                        pixels = new byte[checked((int)expectedBytes)];
                        outputHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                        ThrowOnError(JxlDecoderSetImageOutBuffer(decoder, in format, outputHandle.AddrOfPinnedObject(), bufferSize), "JxlDecoderSetImageOutBuffer");
                        break;

                    case JXL_DEC_FULL_IMAGE:
                        gotFullImage = true;
                        break;

                    case JXL_DEC_SUCCESS:
                        throw new InvalidOperationException("libjxl finished decoding without producing a full image.");

                    case JXL_DEC_NEED_MORE_INPUT:
                        throw new InvalidOperationException("libjxl requested more input past the end of the stream (truncated JPEG XL stream).");

                    default:
                        throw new InvalidOperationException($"libjxl decode failed (JxlDecoderProcessInput status {status}).");
                }
            }

            if (pixels is null)
            {
                throw new InvalidOperationException("libjxl produced a full image without requesting an output buffer.");
            }

            if (resolvedTransfer == DecodedBitmapTransfer.LinearSceneScRgb)
            {
                if (preserveAlpha && hasAlpha && alphaPremultiplied)
                {
                    UnpremultiplyHalfPixels(pixels, width, height, cancellationToken);
                }

                if (IsWideGamut(resolvedGamut))
                {
                    ConvertHalfPixelsToBt709(pixels, width, height, resolvedGamut, cancellationToken);
                }

                resolvedGamut = GainMapColorGamut.Bt709;
            }
            else if (hasAlpha && !preserveAlpha)
            {
                SetOpaqueAlpha(pixels, format.DataType == JXL_TYPE_UINT8);
            }

            var outputPixelFormat = format.DataType switch
            {
                JXL_TYPE_FLOAT16 => DecodedBitmapPixelFormat.Rgba16Float,
                JXL_TYPE_UINT8 => DecodedBitmapPixelFormat.Rgba8Unorm,
                _ => DecodedBitmapPixelFormat.Rgba16Unorm,
            };
            var effectiveGamut = resolvedGamut == GainMapColorGamut.Unknown
                ? GainMapColorGamut.Bt709
                : resolvedGamut;
            var usesBt2020 = effectiveGamut == GainMapColorGamut.Bt2100;
            var decodeMs = decodeTimer.ElapsedMilliseconds;
            var timing = readMs is { } rMs
                ? $"read {rMs}ms, decode {decodeMs}ms"
                : $"decode {decodeMs}ms";

            return new DecodedBitmap(
                width,
                height,
                pixels,
                ColorManagedToSrgb: colorManagedToSrgb,
                $"libjxl in-process [{timing}]; {colorDescription}",
                outputPixelFormat,
                resolvedTransfer,
                usesBt2020,
                forceRawRgba16 ? GainMapColorGamut.Unknown : effectiveGamut);
        }
        finally
        {
            JxlDecoderDestroy(decoder);
            if (runner != IntPtr.Zero)
            {
                JxlThreadParallelRunnerDestroy(runner);
            }

            if (outputHandle.IsAllocated)
            {
                outputHandle.Free();
            }

            inputHandle.Free();
        }
    }

    private static void ResolveColorStrings(
        IntPtr decoder,
        in JxlBasicInfo basicInfo,
        out string transferFunction,
        out string colorPrimaries)
    {
        if (JxlDecoderGetColorAsEncodedProfile(decoder, JXL_COLOR_PROFILE_TARGET_ORIGINAL, out var profile) == JXL_DEC_SUCCESS
            || JxlDecoderGetColorAsEncodedProfile(decoder, JXL_COLOR_PROFILE_TARGET_DATA, out profile) == JXL_DEC_SUCCESS)
        {
            transferFunction = DescribeTransferFunction(profile);
            colorPrimaries = DescribePrimaries(profile);
            return;
        }

        if (TryReadIccProfile(decoder, JXL_COLOR_PROFILE_TARGET_ORIGINAL, out var iccBytes))
        {
            var iccGamut = IccColorProfileDetector.DetectColorGamut(iccBytes);
            var iccTransfer = IccColorProfileDetector.DetectTransferCharacteristics(iccBytes);
            var iccDesc = IccColorProfileDetector.ExtractProfileDescription(iccBytes);

            transferFunction = iccTransfer switch
            {
                16 => "PQ / SMPTE ST 2084",
                18 => "HLG",
                1 => "BT.709",
                _ => basicInfo.ExponentBitsPerSample > 0 ? "linear" : "sRGB",
            };

            colorPrimaries = iccGamut switch
            {
                GainMapColorGamut.Bt2100 => "Rec.2100 / BT.2020",
                GainMapColorGamut.DisplayP3 => "Display P3",
                GainMapColorGamut.AdobeRgb => "Adobe RGB (1998)",
                GainMapColorGamut.ProPhoto => "ProPhoto RGB",
                GainMapColorGamut.Bt709 => "sRGB / BT.709",
                _ => !string.IsNullOrWhiteSpace(iccDesc) ? iccDesc : "ICC profile",
            };
            return;
        }

        transferFunction = basicInfo.ExponentBitsPerSample > 0 ? "linear" : "sRGB";
        colorPrimaries = "sRGB / BT.709";
    }

    private static string DescribeTransferFunction(in JxlColorEncoding profile)
    {
        return profile.TransferFunction switch
        {
            JXL_TRANSFER_FUNCTION_PQ => "PQ / SMPTE ST 2084",
            JXL_TRANSFER_FUNCTION_HLG => "HLG",
            JXL_TRANSFER_FUNCTION_LINEAR => "linear",
            JXL_TRANSFER_FUNCTION_SRGB => "sRGB",
            JXL_TRANSFER_FUNCTION_709 => "BT.709",
            JXL_TRANSFER_FUNCTION_DCI => "DCI",
            JXL_TRANSFER_FUNCTION_GAMMA => string.Create(CultureInfo.InvariantCulture, $"gamma {profile.Gamma:0.####}"),
            _ => "Unknown",
        };
    }

    private static string DescribePrimaries(in JxlColorEncoding profile)
    {
        if (profile.ColorSpace == JXL_COLOR_SPACE_GRAY)
        {
            return "sRGB / BT.709";
        }

        return MapGamutFromColorEncoding(profile) switch
        {
            GainMapColorGamut.Bt2100 => "Rec.2100 / BT.2020",
            GainMapColorGamut.DisplayP3 => "Display P3",
            GainMapColorGamut.AdobeRgb => "Adobe RGB (1998)",
            GainMapColorGamut.ProPhoto => "ProPhoto RGB",
            GainMapColorGamut.Bt709 => "sRGB / BT.709",
            _ => "Unknown",
        };
    }

    private static GainMapColorGamut DetectDataColorGamut(IntPtr decoder, GainMapColorGamut fallback)
    {
        if (JxlDecoderGetColorAsEncodedProfile(decoder, JXL_COLOR_PROFILE_TARGET_DATA, out var dataProfile) == JXL_DEC_SUCCESS)
        {
            var mapped = MapGamutFromColorEncoding(dataProfile);
            if (mapped != GainMapColorGamut.Unknown)
            {
                return mapped;
            }
        }

        if (TryReadIccProfile(decoder, JXL_COLOR_PROFILE_TARGET_DATA, out var icc))
        {
            var iccGamut = IccColorProfileDetector.DetectColorGamut(icc);
            if (iccGamut != GainMapColorGamut.Unknown)
            {
                return iccGamut;
            }
        }

        return fallback;
    }

    private static GainMapColorGamut ValidateAndResolveSdrEncoding(
        IntPtr decoder,
        ref DecodedBitmapTransfer resolvedTransfer)
    {
        if (JxlDecoderGetColorAsEncodedProfile(decoder, JXL_COLOR_PROFILE_TARGET_DATA, out var dataProfile) == JXL_DEC_SUCCESS)
        {
            if (dataProfile.ColorSpace == JXL_COLOR_SPACE_GRAY)
            {
                if (dataProfile.TransferFunction == JXL_TRANSFER_FUNCTION_SRGB)
                {
                    resolvedTransfer = DecodedBitmapTransfer.Sdr;
                    return GainMapColorGamut.Bt709;
                }

                if (dataProfile.TransferFunction == JXL_TRANSFER_FUNCTION_709)
                {
                    resolvedTransfer = DecodedBitmapTransfer.Bt709;
                    return GainMapColorGamut.Bt709;
                }

                throw new NotSupportedException("JPEG XL grayscale transfer curve requires external color management.");
            }

            var gamut = MapGamutFromColorEncoding(dataProfile);
            if (gamut != GainMapColorGamut.Unknown)
            {
                if (dataProfile.TransferFunction == JXL_TRANSFER_FUNCTION_SRGB)
                {
                    resolvedTransfer = DecodedBitmapTransfer.Sdr;
                    return gamut;
                }

                if (dataProfile.TransferFunction == JXL_TRANSFER_FUNCTION_709)
                {
                    resolvedTransfer = DecodedBitmapTransfer.Bt709;
                    return gamut;
                }

                if (dataProfile.TransferFunction == JXL_TRANSFER_FUNCTION_GAMMA)
                {
                    if ((gamut == GainMapColorGamut.AdobeRgb && Math.Abs(dataProfile.Gamma - (256.0 / 563.0)) <= 0.015)
                        || (gamut == GainMapColorGamut.ProPhoto && Math.Abs(dataProfile.Gamma - (1.0 / 1.8)) <= 0.015))
                    {
                        resolvedTransfer = DecodedBitmapTransfer.Sdr;
                        return gamut;
                    }
                }
            }

            throw new NotSupportedException("JPEG XL SDR color encoding requires external color management.");
        }

        if (TryReadIccProfile(decoder, JXL_COLOR_PROFILE_TARGET_DATA, out var icc))
        {
            var iccGamut = IccColorProfileDetector.DetectColorGamut(icc);
            var iccTransfer = IccColorProfileDetector.DetectTransferCharacteristics(icc);
            if (iccGamut != GainMapColorGamut.Unknown && iccTransfer is null or 1)
            {
                resolvedTransfer = iccTransfer == 1
                    ? DecodedBitmapTransfer.Bt709
                    : DecodedBitmapTransfer.Sdr;
                return iccGamut;
            }
        }

        throw new NotSupportedException("JPEG XL embedded ICC profile requires external WIC color management.");
    }

    // The jhgm color_encoding_size delimits the entire structure. Only the
    // fields through primaries affect gain-map working-space selection.
    internal static GainMapColorGamut ReadCompactColorGamut(ReadOnlySpan<byte> encoding)
    {
        var bits = new ColorEncodingBits(encoding);
        if (bits.Read(1) != 0) // all_default (sRGB)
        {
            return GainMapColorGamut.Bt709;
        }

        var wantIcc = bits.Read(1) != 0;
        var colorSpace = bits.ReadEnum();
        if (wantIcc || colorSpace != JXL_COLOR_SPACE_RGB)
        {
            return GainMapColorGamut.Unknown;
        }

        if (bits.ReadEnum() == 2) // custom white point
        {
            bits.ReadCoordinate();
            bits.ReadCoordinate();
        }

        var profile = new JxlColorEncoding { Primaries = bits.ReadEnum() };
        if (profile.Primaries == JXL_PRIMARIES_CUSTOM)
        {
            profile.PrimariesRedX = bits.ReadCoordinate();
            profile.PrimariesRedY = bits.ReadCoordinate();
            profile.PrimariesGreenX = bits.ReadCoordinate();
            profile.PrimariesGreenY = bits.ReadCoordinate();
            profile.PrimariesBlueX = bits.ReadCoordinate();
            profile.PrimariesBlueY = bits.ReadCoordinate();
        }

        return MapGamutFromColorEncoding(profile);
    }

    private ref struct ColorEncodingBits(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public int Read(int count)
        {
            if (_position + count > _bytes.Length * 8)
                throw new InvalidDataException("Truncated JPEG XL alternate primaries.");

            int value = 0;
            for (int i = 0; i < count; ++i, ++_position)
                value |= ((_bytes[_position / 8] >> (_position % 8)) & 1) << i;
            return value;
        }

        public int ReadEnum() => Read(2) switch
        {
            0 => 0,
            1 => 1,
            2 => 2 + Read(4),
            _ => 18 + Read(6),
        };

        public double ReadCoordinate()
        {
            var packed = Read(2) switch
            {
                0 => Read(19),
                1 => 524288 + Read(19),
                2 => 1048576 + Read(20),
                _ => 2097152 + Read(21),
            };
            return ((packed >> 1) ^ -(packed & 1)) * 1e-6;
        }
    }

    private static GainMapColorGamut MapGamutFromColorEncoding(in JxlColorEncoding profile)
    {
        switch (profile.Primaries)
        {
            case JXL_PRIMARIES_SRGB:
                return GainMapColorGamut.Bt709;
            case JXL_PRIMARIES_2100:
                return GainMapColorGamut.Bt2100;
            case JXL_PRIMARIES_P3:
                return GainMapColorGamut.DisplayP3;
            case JXL_PRIMARIES_CUSTOM:
                if (MatchesChromaticities(profile, 0.680, 0.320, 0.265, 0.690, 0.150, 0.060))
                {
                    return GainMapColorGamut.DisplayP3;
                }

                if (MatchesChromaticities(profile, 0.708, 0.292, 0.170, 0.797, 0.131, 0.046))
                {
                    return GainMapColorGamut.Bt2100;
                }

                if (MatchesChromaticities(profile, 0.640, 0.330, 0.210, 0.710, 0.150, 0.060))
                {
                    return GainMapColorGamut.AdobeRgb;
                }

                if (MatchesChromaticities(profile, 0.7347, 0.2653, 0.1596, 0.8404, 0.0366, 0.0001))
                {
                    return GainMapColorGamut.ProPhoto;
                }

                if (MatchesChromaticities(profile, 0.640, 0.330, 0.300, 0.600, 0.150, 0.060))
                {
                    return GainMapColorGamut.Bt709;
                }

                return GainMapColorGamut.Unknown;
            default:
                return GainMapColorGamut.Unknown;
        }
    }

    private static bool MatchesChromaticities(
        in JxlColorEncoding profile,
        double rx,
        double ry,
        double gx,
        double gy,
        double bx,
        double by)
    {
        const double tolerance = 0.006;
        return Math.Abs(profile.PrimariesRedX - rx) <= tolerance
            && Math.Abs(profile.PrimariesRedY - ry) <= tolerance
            && Math.Abs(profile.PrimariesGreenX - gx) <= tolerance
            && Math.Abs(profile.PrimariesGreenY - gy) <= tolerance
            && Math.Abs(profile.PrimariesBlueX - bx) <= tolerance
            && Math.Abs(profile.PrimariesBlueY - by) <= tolerance;
    }

    private static bool TryReadIccProfile(IntPtr decoder, int target, out byte[] iccProfile)
    {
        iccProfile = [];
        if (JxlDecoderGetICCProfileSize(decoder, target, out var size) != JXL_DEC_SUCCESS
            || size == 0
            || size > MaxIccProfileBytes)
        {
            return false;
        }

        var buffer = new byte[(int)size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (JxlDecoderGetColorAsICCProfile(decoder, target, handle.AddrOfPinnedObject(), size) != JXL_DEC_SUCCESS)
            {
                return false;
            }
        }
        finally
        {
            handle.Free();
        }

        iccProfile = buffer;
        return true;
    }

    private static bool IsWideGamut(GainMapColorGamut gamut)
    {
        return gamut is GainMapColorGamut.DisplayP3
            or GainMapColorGamut.Bt2100
            or GainMapColorGamut.AdobeRgb
            or GainMapColorGamut.ProPhoto;
    }

    private static void SetOpaqueAlpha(byte[] rgbaPixels, bool is8Bit)
    {
        if (is8Bit)
        {
            for (var i = 3; i < rgbaPixels.Length; i += 4)
            {
                rgbaPixels[i] = 255;
            }
        }
        else
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(rgbaPixels.AsSpan());
            for (var i = 3; i < samples.Length; i += 4)
            {
                samples[i] = ushort.MaxValue;
            }
        }
    }

    private static void UnpremultiplyHalfPixels(byte[] rgbaHalfPixels, int width, int height, CancellationToken cancellationToken)
    {
        var samples = MemoryMarshal.Cast<byte, Half>(rgbaHalfPixels.AsSpan());
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var offset = rowOffset + (x * 4);
                var alpha = (float)samples[offset + 3];
                if (alpha >= 1.0f)
                {
                    continue;
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    samples[offset + channel] = alpha > 0.0f && float.IsFinite(alpha)
                        ? (Half)Math.Clamp((float)samples[offset + channel] / alpha, -65504.0f, 65504.0f)
                        : (Half)0.0f;
                }
            }
        }
    }

    private static void ConvertHalfPixelsToBt709(
        byte[] rgbaHalfPixels,
        int width,
        int height,
        GainMapColorGamut gamut,
        CancellationToken cancellationToken)
    {
        var samples = MemoryMarshal.Cast<byte, Half>(rgbaHalfPixels.AsSpan());
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var offset = rowOffset + (x * 4);
                var rgb = new Vector3(
                    (float)samples[offset],
                    (float)samples[offset + 1],
                    (float)samples[offset + 2]);
                var bt709 = ConvertLinearGamutToBt709(rgb, gamut);
                samples[offset] = (Half)Math.Clamp(bt709.X, -65504.0f, 65504.0f);
                samples[offset + 1] = (Half)Math.Clamp(bt709.Y, -65504.0f, 65504.0f);
                samples[offset + 2] = (Half)Math.Clamp(bt709.Z, -65504.0f, 65504.0f);
            }
        }
    }

    private static Vector3 ConvertLinearGamutToBt709(Vector3 v, GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.Bt2100 => new Vector3(
                (1.660491f * v.X) - (0.587641f * v.Y) - (0.072850f * v.Z),
                (-0.124550f * v.X) + (1.132900f * v.Y) - (0.008349f * v.Z),
                (-0.018151f * v.X) - (0.100579f * v.Y) + (1.118730f * v.Z)),
            GainMapColorGamut.DisplayP3 => new Vector3(
                (1.224940f * v.X) - (0.224940f * v.Y),
                (-0.042057f * v.X) + (1.042057f * v.Y),
                (-0.019638f * v.X) - (0.078636f * v.Y) + (1.098274f * v.Z)),
            GainMapColorGamut.AdobeRgb => new Vector3(
                (1.398356f * v.X) - (0.398356f * v.Y),
                v.Y,
                (-0.042929f * v.Y) + (1.042929f * v.Z)),
            GainMapColorGamut.ProPhoto => new Vector3(
                (2.034368f * v.X) - (0.727634f * v.Y) - (0.306733f * v.Z),
                (-0.228827f * v.X) + (1.231753f * v.Y) - (0.002927f * v.Z),
                (-0.008558f * v.X) - (0.153268f * v.Y) + (1.161827f * v.Z)),
            _ => v,
        };
    }

    private static int ReadUpTo(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void ThrowOnError(int status, string call)
    {
        if (status != JXL_DEC_SUCCESS)
        {
            throw new InvalidOperationException($"libjxl {call} failed with status {status}.");
        }
    }

    private static void EnsureNativeLibrariesLoaded()
    {
        lock (LoadLock)
        {
            if (_librariesLoaded)
            {
                return;
            }

            var jxlPath = NativeToolLocator.FindFirstTool("libjxl.dll", JxlLibraryName)
                ?? throw new DllNotFoundException($"Could not locate {JxlLibraryName} (libjxl native decoder library).");

            var jxlHandle = LoadLibraryEx(jxlPath, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            if (jxlHandle == IntPtr.Zero)
            {
                throw new DllNotFoundException($"Failed to load {jxlPath} (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            NativeCodecLibraryResolver.Register(JxlLibraryName, (_, _, _) => jxlHandle);

            var threadsFileName = Path.GetFileName(jxlPath).StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                ? "libjxl_threads.dll"
                : JxlThreadsLibraryName;
            var threadsPath = Path.Combine(Path.GetDirectoryName(jxlPath)!, threadsFileName);
            if (File.Exists(threadsPath))
            {
                var threadsHandle = LoadLibraryEx(threadsPath, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
                if (threadsHandle != IntPtr.Zero
                    && NativeLibrary.TryGetExport(threadsHandle, "JxlThreadParallelRunner", out var runnerFunction))
                {
                    _threadParallelRunnerFunction = runnerFunction;
                    NativeCodecLibraryResolver.Register(JxlThreadsLibraryName, (_, _, _) => threadsHandle);
                }
            }

            _decoderVersion = JxlDecoderVersion();
            _librariesLoaded = true;
        }
    }

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint JxlDecoderVersion();

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr JxlDecoderCreate(IntPtr memoryManager);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlDecoderDestroy(IntPtr decoder);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSubscribeEvents(IntPtr decoder, int eventsWanted);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetParallelRunner(IntPtr decoder, IntPtr parallelRunner, IntPtr parallelRunnerOpaque);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetInput(IntPtr decoder, IntPtr data, nuint size);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlDecoderCloseInput(IntPtr decoder);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderProcessInput(IntPtr decoder);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderGetBasicInfo(IntPtr decoder, out JxlBasicInfo info);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetUnpremultiplyAlpha(IntPtr decoder, int unpremulAlpha);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetDecompressBoxes(IntPtr decoder, int decompress);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderGetBoxType(IntPtr decoder, out uint boxType, int decompressed);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderGetColorAsEncodedProfile(IntPtr decoder, int target, out JxlColorEncoding colorEncoding);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetPreferredColorProfile(IntPtr decoder, in JxlColorEncoding colorEncoding);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderGetICCProfileSize(IntPtr decoder, int target, out nuint size);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderGetColorAsICCProfile(IntPtr decoder, int target, IntPtr iccProfile, nuint size);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderImageOutBufferSize(IntPtr decoder, in JxlPixelFormat format, out nuint size);

    [DllImport(JxlLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int JxlDecoderSetImageOutBuffer(IntPtr decoder, in JxlPixelFormat format, IntPtr buffer, nuint size);

    [DllImport(JxlThreadsLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr JxlThreadParallelRunnerCreate(IntPtr memoryManager, nuint numWorkerThreads);

    [DllImport(JxlThreadsLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void JxlThreadParallelRunnerDestroy(IntPtr runner);

    [DllImport(JxlThreadsLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint JxlThreadParallelRunnerDefaultNumWorkerThreads();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlPixelFormat
    {
        public uint NumChannels;
        public int DataType;
        public int Endianness;
        public nuint Align;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlColorEncoding
    {
        public int ColorSpace;
        public int WhitePoint;
        public double WhitePointX;
        public double WhitePointY;
        public int Primaries;
        public double PrimariesRedX;
        public double PrimariesRedY;
        public double PrimariesGreenX;
        public double PrimariesGreenY;
        public double PrimariesBlueX;
        public double PrimariesBlueY;
        public int TransferFunction;
        public double Gamma;
        public int RenderingIntent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlBasicInfo
    {
        public int HaveContainer;
        public uint Xsize;
        public uint Ysize;
        public uint BitsPerSample;
        public uint ExponentBitsPerSample;
        public float IntensityTarget;
        public float MinNits;
        public int RelativeToMaxDisplay;
        public float LinearBelow;
        public int UsesOriginalProfile;
        public int HavePreview;
        public int HaveAnimation;
        public int Orientation;
        public uint NumColorChannels;
        public uint NumExtraChannels;
        public uint AlphaBits;
        public uint AlphaExponentBits;
        public int AlphaPremultiplied;
        public uint PreviewXsize;
        public uint PreviewYsize;
        public uint AnimationTpsNumerator;
        public uint AnimationTpsDenominator;
        public uint AnimationNumLoops;
        public int AnimationHaveTimecodes;
        public uint IntrinsicXsize;
        public uint IntrinsicYsize;
        public ulong Padding0;
        public ulong Padding1;
        public ulong Padding2;
        public ulong Padding3;
        public ulong Padding4;
        public ulong Padding5;
        public ulong Padding6;
        public ulong Padding7;
        public ulong Padding8;
        public ulong Padding9;
        public ulong Padding10;
        public ulong Padding11;
        public ulong Padding12;
    }
}
