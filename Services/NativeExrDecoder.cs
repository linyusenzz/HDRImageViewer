using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Buffers.Binary;

namespace HdrImageViewer.Services;

public static class NativeExrDecoder
{
    private const string NativeLibraryName = "HdrImageViewer.Native";
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
    private const float AdobeLinearRec2020ReferenceWhiteScale = 203.0f / 80.0f;

    static NativeExrDecoder()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeExrDecoder).Assembly, ResolveNativeLibrary);
    }

    public static bool IsAvailable => TryLoadNativeLibrary(out _);

    public static void Encode(string path, int width, int height, byte[] rgbaHalfPixels)
    {
        if (!TryLoadNativeLibrary(out var loadError))
        {
            throw new DllNotFoundException(loadError);
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "EXR dimensions must be positive.");
        }

        var expectedLength = checked(width * height * 8);
        if (rgbaHalfPixels.Length != expectedLength)
        {
            throw new ArgumentException($"EXR RGBA16F pixel buffer must be {expectedLength} bytes.", nameof(rgbaHalfPixels));
        }

        var handle = GCHandle.Alloc(rgbaHalfPixels, GCHandleType.Pinned);
        try
        {
            var error = new StringBuilder(1024);
            var result = hdriv_exr_encode_rgba16f(
                path,
                width,
                height,
                handle.AddrOfPinnedObject(),
                error,
                error.Capacity);
            if (result != 0)
            {
                throw new InvalidOperationException(error.Length > 0
                    ? error.ToString()
                    : $"HdrImageViewer.Native EXR encode failed with code {result}.");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public static DecodedBitmap Decode(string path)
    {
        if (!TryLoadNativeLibrary(out var loadError))
        {
            throw new DllNotFoundException(loadError);
        }

        var error = new StringBuilder(1024);
        var result = hdriv_exr_decode_rgba16f(path, out var image, error, error.Capacity);
        if (result != 0)
        {
            throw new InvalidOperationException(error.Length > 0
                ? error.ToString()
                : $"HdrImageViewer.Native EXR decode failed with code {result}.");
        }

        if (image.Width <= 0 || image.Height <= 0 || image.RgbaHalfPixels == IntPtr.Zero)
        {
            hdriv_exr_free(image.RgbaHalfPixels);
            throw new InvalidOperationException("HdrImageViewer.Native returned an empty EXR image.");
        }

        try
        {
            var byteLength = checked(image.Width * image.Height * 8);
            var pixels = new byte[byteLength];
            Marshal.Copy(image.RgbaHalfPixels, pixels, 0, byteLength);
            var colorMetadata = ReadExrColorMetadata(path);
            if (colorMetadata?.UsesAdobeLinearRec2020Reference == true)
            {
                ScaleRgbaHalfPixels(pixels, AdobeLinearRec2020ReferenceWhiteScale);
            }

            var decoderName = colorMetadata?.Name is { Length: > 0 } name
                ? $"HdrImageViewer.Native OpenEXR ({name}{(colorMetadata.UsesAdobeLinearRec2020Reference ? ", 203-nit reference" : string.Empty)})"
                : "HdrImageViewer.Native OpenEXR";
            return new DecodedBitmap(
                image.Width,
                image.Height,
                pixels,
                ColorManagedToSrgb: false,
                decoderName,
                DecodedBitmapPixelFormat.Rgba16Float,
                DecodedBitmapTransfer.LinearScRgb,
                UsesBt2020Primaries: colorMetadata?.UsesBt2020Primaries == true);
        }
        finally
        {
            hdriv_exr_free(image.RgbaHalfPixels);
        }
    }

    private static ExrColorMetadata? ReadExrColorMetadata(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) != header.Length || BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != 0x01312F76)
            {
                return null;
            }

            var usesBt2020 = false;
            string? colorName = null;
            var usesAdobeLinearRec2020Reference = false;
            var sizeBytes = new byte[4];

            for (var attributeIndex = 0; attributeIndex < 1024 && stream.Position < stream.Length; attributeIndex++)
            {
                var name = ReadExrNullTerminatedAscii(stream, 256);
                if (name is null)
                {
                    return null;
                }

                if (name.Length == 0)
                {
                    break;
                }

                var type = ReadExrNullTerminatedAscii(stream, 256);
                if (type is null)
                {
                    return null;
                }

                if (stream.Read(sizeBytes, 0, sizeBytes.Length) != sizeBytes.Length)
                {
                    return null;
                }

                var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
                if (size < 0 || size > 16 * 1024 * 1024 || stream.Position > stream.Length - size)
                {
                    return null;
                }

                if (name == "chromaticities" && type == "chromaticities" && size >= 32)
                {
                    var data = new byte[size];
                    if (stream.Read(data, 0, data.Length) != data.Length)
                    {
                        return null;
                    }

                    if (IsBt2020Chromaticities(data))
                    {
                        usesBt2020 = true;
                        colorName ??= "Linear Rec. 2020";
                    }
                }
                else if (name == "colorInteropID" && type == "string")
                {
                    var data = new byte[size];
                    if (stream.Read(data, 0, data.Length) != data.Length)
                    {
                        return null;
                    }

                    var text = DecodeExrStringAttribute(data);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        colorName = text;
                        if (ContainsBt2020Hint(text))
                        {
                            usesBt2020 = true;
                            usesAdobeLinearRec2020Reference |= ContainsAdobeLinearRec2020Hint(text);
                        }
                    }
                }
                else
                {
                    stream.Seek(size, SeekOrigin.Current);
                }
            }

            return usesBt2020 || colorName is not null
                ? new ExrColorMetadata(usesBt2020, colorName, usesAdobeLinearRec2020Reference)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadExrNullTerminatedAscii(Stream stream, int maxBytes)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < maxBytes; i++)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return null;
            }

            if (value == 0)
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add((byte)value);
        }

        return null;
    }

    private static bool IsBt2020Chromaticities(byte[] data)
    {
        var redX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0, 4));
        var redY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4));
        var greenX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8, 4));
        var greenY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(12, 4));
        var blueX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(16, 4));
        var blueY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(20, 4));

        return IsClose(redX, 0.708f) && IsClose(redY, 0.292f)
            && IsClose(greenX, 0.170f) && IsClose(greenY, 0.797f)
            && IsClose(blueX, 0.131f) && IsClose(blueY, 0.046f);
    }

    private static bool IsClose(float value, float expected) => MathF.Abs(value - expected) <= 0.015f;

    private static string? DecodeExrStringAttribute(byte[] data)
    {
        var offset = data.Length >= 4 ? 4 : 0;
        if (data.Length >= 4)
        {
            var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
            if (declaredLength < 0 || declaredLength > data.Length - 4)
            {
                offset = 0;
            }
        }

        var text = Encoding.UTF8.GetString(data, offset, data.Length - offset).Trim('\0', ' ', '\r', '\n', '\t');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ContainsBt2020Hint(string value) =>
        value.Contains("Rec. 2020", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Rec2020", StringComparison.OrdinalIgnoreCase)
        || value.Contains("BT.2020", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAdobeLinearRec2020Hint(string value) =>
        value.Contains("Linear Rec. 2020", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Linear Rec2020", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Linear BT.2020", StringComparison.OrdinalIgnoreCase);

    private static void ScaleRgbaHalfPixels(byte[] pixels, float scale)
    {
        for (var offset = 0; offset + 7 < pixels.Length; offset += 8)
        {
            WriteScaledHalf(pixels, offset, scale);
            WriteScaledHalf(pixels, offset + 2, scale);
            WriteScaledHalf(pixels, offset + 4, scale);
        }
    }

    private static void WriteScaledHalf(byte[] pixels, int offset, float scale)
    {
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(offset, 2));
        var value = (float)BitConverter.UInt16BitsToHalf(bits) * scale;
        var scaled = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(value, -65504.0f, 65504.0f));
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset, 2), scaled);
    }

    private static bool TryLoadNativeLibrary(out string error)
    {
        var loadFailures = new List<string>();
        foreach (var candidate in EnumerateNativeLibraryCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var handle = LoadLibraryEx(candidate, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            if (handle != IntPtr.Zero)
            {
                FreeLibrary(handle);
                error = string.Empty;
                return true;
            }

            loadFailures.Add($"{candidate} (Win32 {Marshal.GetLastWin32Error()})");
        }

        error = loadFailures.Count > 0
            ? $"找到 HdrImageViewer.Native.dll，但 LoadLibraryEx 加载失败，可能缺少 OpenEXR 依赖 DLL: {string.Join("; ", loadFailures)}。"
            : "未找到 HdrImageViewer.Native.dll。";
        return false;
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeLibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in EnumerateNativeLibraryCandidates())
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

    private static IEnumerable<string> EnumerateNativeLibraryCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "HdrImageViewer.Native.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "native", RuntimeInformation.ProcessArchitecture.ToString(), "HdrImageViewer.Native.dll");
        yield return Path.Combine(Environment.CurrentDirectory, "native", "HdrImageViewer.Native", "build", RuntimeInformation.ProcessArchitecture.ToString(), "Release", "HdrImageViewer.Native.dll");
        yield return Path.Combine(Environment.CurrentDirectory, "native", "HdrImageViewer.Native", "build", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "Release", "HdrImageViewer.Native.dll");
    }

    [DllImport(NativeLibraryName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hdriv_exr_decode_rgba16f(
        string path,
        out NativeExrImage image,
        StringBuilder errorBuffer,
        int errorBufferLength);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hdriv_exr_free(IntPtr pointer);

    [DllImport(NativeLibraryName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hdriv_exr_encode_rgba16f(
        string path,
        int width,
        int height,
        IntPtr rgbaHalfPixels,
        StringBuilder errorBuffer,
        int errorBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeExrImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly IntPtr RgbaHalfPixels;
    }

    private sealed record ExrColorMetadata(bool UsesBt2020Primaries, string? Name, bool UsesAdobeLinearRec2020Reference);
}
