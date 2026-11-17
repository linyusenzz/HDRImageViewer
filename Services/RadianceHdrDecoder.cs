using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class RadianceHdrDecoder
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const long MaxPixelCount = 268_435_456L;
    private const ushort OpaqueAlphaHalfBits = 0x3C00; // (Half)1.0f

    public static bool IsRadianceHdrExtension(string extension)
    {
        return string.Equals(extension, ".hdr", StringComparison.OrdinalIgnoreCase);
    }

    public static RadianceHdrHeader? TryProbeFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);
            var readLength = (int)Math.Min(stream.Length, MaxHeaderBytes);
            if (readLength <= 0)
            {
                return null;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(readLength);
            try
            {
                var totalRead = 0;
                while (totalRead < readLength)
                {
                    var read = stream.Read(buffer, totalRead, readLength - totalRead);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                }

                return TryProbe(buffer.AsSpan(0, totalRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    public static RadianceHdrHeader? TryProbe(ReadOnlySpan<byte> data)
    {
        try
        {
            var parsed = ParseHeader(data, out _);
            return new RadianceHdrHeader(
                parsed.OutputWidth,
                parsed.OutputHeight,
                parsed.IsXyze,
                parsed.Exposure,
                parsed.Format,
                parsed.ResolutionLine);
        }
        catch
        {
            return null;
        }
    }

    public static DecodedBitmap DecodeFile(
        string path,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readTimer = Stopwatch.StartNew();
        var bytes = File.ReadAllBytes(path);
        var readMs = readTimer.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        var decoded = DecodeCore(bytes, maxPixelSize, readMs, cancellationToken);
        return decoded;
    }

    public static DecodedBitmap Decode(
        ReadOnlySpan<byte> data,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        return DecodeCore(data, maxPixelSize, readMs: null, cancellationToken);
    }

    private static DecodedBitmap DecodeCore(
        ReadOnlySpan<byte> data,
        int? maxPixelSize,
        long? readMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decodeTimer = Stopwatch.StartNew();
        var header = ParseHeader(data, out var scanlineDataOffset);
        var pixelCount = (long)header.ScanlineCount * header.ScanlineLength;
        if (pixelCount <= 0 || pixelCount > MaxPixelCount)
        {
            throw new InvalidDataException($"Radiance HDR dimensions {header.OutputWidth}x{header.OutputHeight} are invalid or exceed limits.");
        }

        var invExposure = double.IsFinite(header.Exposure) && header.Exposure > 0.0
            ? (float)(1.0 / header.Exposure)
            : 1.0f;

        var needsDownscale = TryCalculateScaledSize(
            header.OutputWidth,
            header.OutputHeight,
            maxPixelSize,
            out var targetWidth,
            out var targetHeight);

        var scanlineRgbe = ArrayPool<byte>.Shared.Rent(checked(header.ScanlineLength * 4));
        float[]? targetAccum = null;
        BoxSpan[]? xMap = null;
        BoxSpan[]? yMap = null;
        try
        {
            byte[] outputPixels;
            int finalWidth;
            int finalHeight;

            if (needsDownscale)
            {
                var accumLength = checked(targetWidth * targetHeight * 4);
                targetAccum = ArrayPool<float>.Shared.Rent(accumLength);
                targetAccum.AsSpan(0, accumLength).Clear();
                xMap = ArrayPool<BoxSpan>.Shared.Rent(header.OutputWidth);
                yMap = ArrayPool<BoxSpan>.Shared.Rent(header.OutputHeight);
                BuildAxisBoxSpans(header.OutputWidth, targetWidth, xMap.AsSpan(0, header.OutputWidth));
                BuildAxisBoxSpans(header.OutputHeight, targetHeight, yMap.AsSpan(0, header.OutputHeight));

                var cursor = scanlineDataOffset;
                for (var s = 0; s < header.ScanlineCount; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DecodeScanline(data, ref cursor, header.ScanlineLength, scanlineRgbe.AsSpan(0, header.ScanlineLength * 4));
                    AccumulateScanlineIntoTargetBox(
                        scanlineRgbe.AsSpan(0, header.ScanlineLength * 4),
                        s,
                        in header,
                        invExposure,
                        xMap.AsSpan(0, header.OutputWidth),
                        yMap.AsSpan(0, header.OutputHeight),
                        targetWidth,
                        targetAccum.AsSpan(0, accumLength));
                }

                outputPixels = FinalizeTargetBoxToRgba16Float(
                    targetAccum.AsSpan(0, accumLength),
                    targetWidth,
                    targetHeight);
                finalWidth = targetWidth;
                finalHeight = targetHeight;
            }
            else
            {
                outputPixels = new byte[checked((int)(pixelCount * 8))];
                var cursor = scanlineDataOffset;
                for (var s = 0; s < header.ScanlineCount; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DecodeScanline(data, ref cursor, header.ScanlineLength, scanlineRgbe.AsSpan(0, header.ScanlineLength * 4));
                    ConvertScanlineToRgba16Float(
                        scanlineRgbe.AsSpan(0, header.ScanlineLength * 4),
                        s,
                        in header,
                        invExposure,
                        outputPixels);
                }

                finalWidth = header.OutputWidth;
                finalHeight = header.OutputHeight;
            }

            var decodeMs = decodeTimer.ElapsedMilliseconds;
            var formatName = header.IsXyze ? "XYZE" : "RGBE";
            var timingText = readMs is { } rMs
                ? $"read {rMs}ms, decode {decodeMs}ms"
                : $"decode {decodeMs}ms";
            var scaleNote = needsDownscale
                ? $", downscaled {header.OutputWidth}x{header.OutputHeight}->{finalWidth}x{finalHeight}"
                : string.Empty;

            return new DecodedBitmap(
                finalWidth,
                finalHeight,
                outputPixels,
                ColorManagedToSrgb: false,
                DecoderName: $"RadianceHdrDecoder ({formatName} -> RGBA16F) [{timingText}{scaleNote}]",
                PixelFormat: DecodedBitmapPixelFormat.Rgba16Float,
                Transfer: DecodedBitmapTransfer.LinearSceneScRgb,
                UsesBt2020Primaries: false,
                ColorGamut: GainMapColorGamut.Bt709);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanlineRgbe);
            if (targetAccum is not null)
            {
                ArrayPool<float>.Shared.Return(targetAccum);
            }

            if (xMap is not null)
            {
                ArrayPool<BoxSpan>.Shared.Return(xMap);
            }

            if (yMap is not null)
            {
                ArrayPool<BoxSpan>.Shared.Return(yMap);
            }
        }
    }

    private static ParsedHeader ParseHeader(ReadOnlySpan<byte> data, out int pixelDataOffset)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("Radiance HDR stream is empty.");
        }

        var cursor = 0;
        var hasMagicSignature = false;
        var hasFormatDeclaration = false;
        var isXyze = false;
        var formatValue = "32-bit_rle_rgbe";
        var exposure = 1.0;
        var foundBlankHeaderEnd = false;
        var lineIndex = 0;

        while (cursor < data.Length)
        {
            if (cursor > MaxHeaderBytes)
            {
                throw new InvalidDataException("Radiance HDR header exceeds the maximum allowed size.");
            }

            if (!TryReadAsciiLine(data, ref cursor, out var lineSpan))
            {
                throw new InvalidDataException("Unexpected end of stream inside Radiance HDR header.");
            }

            if (lineSpan.IsEmpty)
            {
                foundBlankHeaderEnd = true;
                break;
            }

            if (lineIndex == 0 && lineSpan.Length >= 2 && lineSpan[0] == (byte)'#' && lineSpan[1] == (byte)'?')
            {
                hasMagicSignature = true;
                lineIndex++;
                continue;
            }

            lineIndex++;
            if (lineSpan[0] == (byte)'#')
            {
                continue;
            }

            var line = Encoding.ASCII.GetString(lineSpan).Trim();
            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            if (key.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase))
                {
                    hasFormatDeclaration = true;
                    isXyze = false;
                    formatValue = "32-bit_rle_rgbe";
                }
                else if (value.Equals("32-bit_rle_xyze", StringComparison.OrdinalIgnoreCase))
                {
                    hasFormatDeclaration = true;
                    isXyze = true;
                    formatValue = "32-bit_rle_xyze";
                }
                else
                {
                    throw new InvalidDataException($"Unsupported Radiance HDR FORMAT '{value}'.");
                }
            }
            else if (key.Equals("EXPOSURE", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedExposure)
                    && double.IsFinite(parsedExposure)
                    && parsedExposure > 0.0)
                {
                    exposure *= parsedExposure;
                }
            }
        }

        if (!foundBlankHeaderEnd || (!hasMagicSignature && !hasFormatDeclaration))
        {
            throw new InvalidDataException("Data does not contain a valid Radiance HDR header.");
        }

        if (!TryReadAsciiLine(data, ref cursor, out var resolutionSpan) || resolutionSpan.IsEmpty)
        {
            throw new InvalidDataException("Missing Radiance HDR resolution line.");
        }

        var resolutionLine = Encoding.ASCII.GetString(resolutionSpan).Trim();
        ParseResolutionLine(
            resolutionLine,
            out var scanlineCount,
            out var scanlineLength,
            out var outputWidth,
            out var outputHeight,
            out var majorIsX,
            out var flipMajor,
            out var flipMinor);

        pixelDataOffset = cursor;
        return new ParsedHeader(
            scanlineCount,
            scanlineLength,
            outputWidth,
            outputHeight,
            majorIsX,
            flipMajor,
            flipMinor,
            isXyze,
            exposure,
            formatValue,
            resolutionLine);
    }

    private static void ParseResolutionLine(
        string resolutionLine,
        out int scanlineCount,
        out int scanlineLength,
        out int outputWidth,
        out int outputHeight,
        out bool majorIsX,
        out bool flipMajor,
        out bool flipMinor)
    {
        var tokens = resolutionLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 4)
        {
            throw new InvalidDataException($"Invalid Radiance HDR resolution string '{resolutionLine}'.");
        }

        ParseAxisToken(tokens[0], out var axis1Sign, out var axis1Char);
        if (!int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out scanlineCount) || scanlineCount <= 0)
        {
            throw new InvalidDataException($"Invalid Radiance HDR major dimension '{tokens[1]}'.");
        }

        ParseAxisToken(tokens[2], out var axis2Sign, out var axis2Char);
        if (!int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out scanlineLength) || scanlineLength <= 0)
        {
            throw new InvalidDataException($"Invalid Radiance HDR minor dimension '{tokens[3]}'.");
        }

        if (axis1Char == axis2Char)
        {
            throw new InvalidDataException($"Radiance HDR resolution axes must specify both X and Y, got '{resolutionLine}'.");
        }

        // Standard top-left image origin uses -Y (top-to-bottom) and +X (left-to-right).
        if (axis1Char == 'Y')
        {
            majorIsX = false;
            outputWidth = scanlineLength;
            outputHeight = scanlineCount;
            flipMajor = axis1Sign == '+';
            flipMinor = axis2Sign == '-';
        }
        else
        {
            majorIsX = true;
            outputWidth = scanlineCount;
            outputHeight = scanlineLength;
            flipMajor = axis1Sign == '-';
            flipMinor = axis2Sign == '+';
        }
    }

    private static void ParseAxisToken(string token, out char sign, out char axis)
    {
        if (token.Length != 2
            || (token[0] != '+' && token[0] != '-')
            || (char.ToUpperInvariant(token[1]) != 'X' && char.ToUpperInvariant(token[1]) != 'Y'))
        {
            throw new InvalidDataException($"Invalid Radiance HDR axis specifier '{token}'.");
        }

        sign = token[0];
        axis = char.ToUpperInvariant(token[1]);
    }

    private static bool TryReadAsciiLine(ReadOnlySpan<byte> data, ref int cursor, out ReadOnlySpan<byte> line)
    {
        var newlineOffset = data[cursor..].IndexOf((byte)'\n');
        if (newlineOffset < 0)
        {
            line = default;
            return false;
        }

        line = data.Slice(cursor, newlineOffset);
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        cursor += newlineOffset + 1;
        return true;
    }

    private static void DecodeScanline(
        ReadOnlySpan<byte> data,
        ref int cursor,
        int scanlineLength,
        Span<byte> destinationRgbe)
    {
        if (cursor + 4 > data.Length)
        {
            throw new InvalidDataException("Truncated Radiance HDR scanline header.");
        }

        var b0 = data[cursor];
        var b1 = data[cursor + 1];
        var b2 = data[cursor + 2];
        var b3 = data[cursor + 3];

        // Adaptive new-style per-channel RLE: 0x02 0x02 width_hi width_lo
        if (scanlineLength is >= 8 and <= 0x7FFF && b0 == 2 && b1 == 2 && (b2 & 0x80) == 0)
        {
            var encodedWidth = (b2 << 8) | b3;
            if (encodedWidth != scanlineLength)
            {
                throw new InvalidDataException(
                    $"Radiance HDR RLE scanline width mismatch: expected {scanlineLength}, found {encodedWidth}.");
            }

            cursor += 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var pos = 0;
                while (pos < scanlineLength)
                {
                    if (cursor >= data.Length)
                    {
                        throw new InvalidDataException("Truncated Radiance HDR adaptive RLE channel stream.");
                    }

                    int code = data[cursor++];
                    if (code > 128)
                    {
                        var runLength = code - 128;
                        if (pos + runLength > scanlineLength || cursor >= data.Length)
                        {
                            throw new InvalidDataException("Corrupt Radiance HDR RLE repeat run.");
                        }

                        var value = data[cursor++];
                        for (var i = 0; i < runLength; i++)
                        {
                            destinationRgbe[((pos + i) * 4) + channel] = value;
                        }

                        pos += runLength;
                    }
                    else
                    {
                        if (code == 0 || pos + code > scanlineLength || cursor + code > data.Length)
                        {
                            throw new InvalidDataException("Corrupt Radiance HDR RLE literal run.");
                        }

                        for (var i = 0; i < code; i++)
                        {
                            destinationRgbe[((pos + i) * 4) + channel] = data[cursor + i];
                        }

                        cursor += code;
                        pos += code;
                    }
                }
            }

            return;
        }

        // Uncompressed flat RGBE or old-style RGBE RLE (1, 1, 1, count).
        var pixelIndex = 0;
        var rshift = 0;
        byte prevR = 0;
        byte prevG = 0;
        byte prevB = 0;
        byte prevE = 0;

        while (pixelIndex < scanlineLength)
        {
            if (cursor + 4 > data.Length)
            {
                throw new InvalidDataException("Truncated Radiance HDR flat/old-RLE scanline data.");
            }

            var r = data[cursor];
            var g = data[cursor + 1];
            var b = data[cursor + 2];
            var e = data[cursor + 3];
            cursor += 4;

            if (r == 1 && g == 1 && b == 1)
            {
                if (pixelIndex == 0 || rshift > 24)
                {
                    throw new InvalidDataException("Invalid old-style RLE repeat packet in Radiance HDR scanline.");
                }

                var repeatCount = e << rshift;
                if (repeatCount < 0 || pixelIndex + repeatCount > scanlineLength)
                {
                    throw new InvalidDataException("Old-style RLE repeat exceeds Radiance HDR scanline bounds.");
                }

                for (var i = 0; i < repeatCount; i++)
                {
                    var dstOffset = (pixelIndex + i) * 4;
                    destinationRgbe[dstOffset] = prevR;
                    destinationRgbe[dstOffset + 1] = prevG;
                    destinationRgbe[dstOffset + 2] = prevB;
                    destinationRgbe[dstOffset + 3] = prevE;
                }

                pixelIndex += repeatCount;
                rshift += 8;
            }
            else
            {
                var dstOffset = pixelIndex * 4;
                destinationRgbe[dstOffset] = r;
                destinationRgbe[dstOffset + 1] = g;
                destinationRgbe[dstOffset + 2] = b;
                destinationRgbe[dstOffset + 3] = e;
                prevR = r;
                prevG = g;
                prevB = b;
                prevE = e;
                pixelIndex++;
                rshift = 0;
            }
        }
    }

    private static void ConvertScanlineToRgba16Float(
        ReadOnlySpan<byte> scanlineRgbe,
        int scanlineIndex,
        in ParsedHeader header,
        float invExposure,
        byte[] outputPixels)
    {
        var majorCoord = header.FlipMajor ? (header.ScanlineCount - 1 - scanlineIndex) : scanlineIndex;
        for (var p = 0; p < header.ScanlineLength; p++)
        {
            var minorCoord = header.FlipMinor ? (header.ScanlineLength - 1 - p) : p;
            var outX = header.MajorIsX ? majorCoord : minorCoord;
            var outY = header.MajorIsX ? minorCoord : majorCoord;

            var srcOffset = p * 4;
            DecodeRgbePixel(
                scanlineRgbe[srcOffset],
                scanlineRgbe[srcOffset + 1],
                scanlineRgbe[srcOffset + 2],
                scanlineRgbe[srcOffset + 3],
                invExposure,
                header.IsXyze,
                out var r,
                out var g,
                out var b);

            var dstOffset = ((outY * header.OutputWidth) + outX) * 8;
            WriteHalf(outputPixels, dstOffset, r);
            WriteHalf(outputPixels, dstOffset + 2, g);
            WriteHalf(outputPixels, dstOffset + 4, b);
            BinaryPrimitives.WriteUInt16LittleEndian(outputPixels.AsSpan(dstOffset + 6, 2), OpaqueAlphaHalfBits);
        }
    }

    private static void BuildAxisBoxSpans(int sourceLength, int targetLength, Span<BoxSpan> spans)
    {
        var invScale = (double)targetLength / sourceLength;
        for (var i = 0; i < sourceLength; i++)
        {
            var u0 = i * invScale;
            var u1 = (i + 1) * invScale;
            var d0 = Math.Clamp((int)Math.Floor(u0), 0, targetLength - 1);
            var boundary = d0 + 1;
            if (u1 <= boundary || d0 >= targetLength - 1)
            {
                spans[i] = new BoxSpan(d0, (float)(u1 - u0), d0, 0.0f);
            }
            else
            {
                var w0 = (float)(boundary - u0);
                var w1 = (float)Math.Max(0.0, u1 - boundary);
                spans[i] = new BoxSpan(d0, w0, d0 + 1, w1);
            }
        }
    }

    private static void AccumulateScanlineIntoTargetBox(
        ReadOnlySpan<byte> scanlineRgbe,
        int scanlineIndex,
        in ParsedHeader header,
        float invExposure,
        ReadOnlySpan<BoxSpan> xMap,
        ReadOnlySpan<BoxSpan> yMap,
        int targetWidth,
        Span<float> targetAccum)
    {
        var majorCoord = header.FlipMajor ? (header.ScanlineCount - 1 - scanlineIndex) : scanlineIndex;
        for (var p = 0; p < header.ScanlineLength; p++)
        {
            var minorCoord = header.FlipMinor ? (header.ScanlineLength - 1 - p) : p;
            var outX = header.MajorIsX ? majorCoord : minorCoord;
            var outY = header.MajorIsX ? minorCoord : majorCoord;

            var srcOffset = p * 4;
            DecodeRgbePixel(
                scanlineRgbe[srcOffset],
                scanlineRgbe[srcOffset + 1],
                scanlineRgbe[srcOffset + 2],
                scanlineRgbe[srcOffset + 3],
                invExposure,
                header.IsXyze,
                out var r,
                out var g,
                out var b);

            var xs = xMap[outX];
            var ys = yMap[outY];
            AccumulateWeightedSample(targetAccum, ((ys.Index0 * targetWidth) + xs.Index0) * 4, r, g, b, xs.Weight0 * ys.Weight0);
            if (xs.Weight1 > 0.0f)
            {
                AccumulateWeightedSample(targetAccum, ((ys.Index0 * targetWidth) + xs.Index1) * 4, r, g, b, xs.Weight1 * ys.Weight0);
            }

            if (ys.Weight1 > 0.0f)
            {
                AccumulateWeightedSample(targetAccum, ((ys.Index1 * targetWidth) + xs.Index0) * 4, r, g, b, xs.Weight0 * ys.Weight1);
                if (xs.Weight1 > 0.0f)
                {
                    AccumulateWeightedSample(targetAccum, ((ys.Index1 * targetWidth) + xs.Index1) * 4, r, g, b, xs.Weight1 * ys.Weight1);
                }
            }
        }
    }

    private static void AccumulateWeightedSample(Span<float> accum, int offset, float r, float g, float b, float weight)
    {
        if (weight <= 0.0f)
        {
            return;
        }

        accum[offset] += r * weight;
        accum[offset + 1] += g * weight;
        accum[offset + 2] += b * weight;
        accum[offset + 3] += weight;
    }

    private static byte[] FinalizeTargetBoxToRgba16Float(
        ReadOnlySpan<float> targetAccum,
        int targetWidth,
        int targetHeight)
    {
        var pixelCount = checked(targetWidth * targetHeight);
        var output = new byte[checked(pixelCount * 8)];
        for (var i = 0; i < pixelCount; i++)
        {
            var srcOffset = i * 4;
            var w = targetAccum[srcOffset + 3];
            var invW = w > 0.0f ? (1.0f / w) : 0.0f;
            var dstOffset = i * 8;
            WriteHalf(output, dstOffset, targetAccum[srcOffset] * invW);
            WriteHalf(output, dstOffset + 2, targetAccum[srcOffset + 1] * invW);
            WriteHalf(output, dstOffset + 4, targetAccum[srcOffset + 2] * invW);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(dstOffset + 6, 2), OpaqueAlphaHalfBits);
        }

        return output;
    }

    private readonly record struct BoxSpan(int Index0, float Weight0, int Index1, float Weight1);

    private static void DecodeRgbePixel(
        byte m0,
        byte m1,
        byte m2,
        byte exponent,
        float invExposure,
        bool isXyze,
        out float r,
        out float g,
        out float b)
    {
        if (exponent == 0)
        {
            r = 0.0f;
            g = 0.0f;
            b = 0.0f;
            return;
        }

        var scale = MathF.ScaleB(1.0f, exponent - 128 - 8) * invExposure;
        var c0 = m0 == 0 ? 0.0f : (m0 + 0.5f) * scale;
        var c1 = m1 == 0 ? 0.0f : (m1 + 0.5f) * scale;
        var c2 = m2 == 0 ? 0.0f : (m2 + 0.5f) * scale;

        if (isXyze)
        {
            r = (3.2404542f * c0) - (1.5371385f * c1) - (0.4985314f * c2);
            g = (-0.9692660f * c0) + (1.8760108f * c1) + (0.0415560f * c2);
            b = (0.0556434f * c0) - (0.2040259f * c1) + (1.0572252f * c2);
        }
        else
        {
            r = c0;
            g = c1;
            b = c2;
        }
    }

    private static bool TryCalculateScaledSize(
        int width,
        int height,
        int? maxPixelSize,
        out int scaledWidth,
        out int scaledHeight)
    {
        scaledWidth = width;
        scaledHeight = height;
        if (maxPixelSize is not > 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var largerSide = Math.Max(width, height);
        if (largerSide <= maxPixelSize.Value)
        {
            return false;
        }

        var scale = maxPixelSize.Value / (double)largerSide;
        scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
        return true;
    }

    private static void WriteHalf(byte[] destination, int offset, float value)
    {
        if (!float.IsFinite(value))
        {
            value = 0.0f;
        }

        var bits = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(value, -65504.0f, 65504.0f));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), bits);
    }

    private readonly record struct ParsedHeader(
        int ScanlineCount,
        int ScanlineLength,
        int OutputWidth,
        int OutputHeight,
        bool MajorIsX,
        bool FlipMajor,
        bool FlipMinor,
        bool IsXyze,
        double Exposure,
        string Format,
        string ResolutionLine);
}
