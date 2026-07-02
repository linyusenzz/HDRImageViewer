using System.Diagnostics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

/// <summary>
/// In-process JPEG XL decoding through P/Invoke bindings to the bundled
/// libjxl (jxl.dll in external/encoders), replacing the djxl.exe spawn +
/// temp-PPM16 round trip for HDR stills. Pixels are requested as interleaved
/// RGBA uint16 little-endian with the decoder's default orientation handling
/// (EXIF orientation applied), which byte-matches what the djxl PPM16 path
/// produced. The caller keeps djxl as the fallback for any failure here.
/// </summary>
public static class JxlNativeDecoder
{
    private const string JxlLibraryName = "jxl.dll";
    private const string JxlThreadsLibraryName = "jxl_threads.dll";
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;

    // JxlDecoderStatus values from libjxl decode.h.
    private const int JXL_DEC_SUCCESS = 0;
    private const int JXL_DEC_ERROR = 1;
    private const int JXL_DEC_NEED_MORE_INPUT = 2;
    private const int JXL_DEC_NEED_IMAGE_OUT_BUFFER = 5;
    private const int JXL_DEC_BASIC_INFO = 0x40;
    private const int JXL_DEC_FULL_IMAGE = 0x1000;

    // JxlDataType / JxlEndianness values from libjxl types.h.
    private const int JXL_TYPE_UINT16 = 3;
    private const int JXL_LITTLE_ENDIAN = 1;

    private static readonly object LoadLock = new();
    private static bool _librariesLoaded;
    private static IntPtr _threadParallelRunnerFunction;

    public static DecodedBitmap Decode(
        string path,
        DecodedBitmapTransfer transfer,
        bool usesBt2020Primaries,
        string colorDescription,
        CancellationToken cancellationToken)
    {
        EnsureNativeLibrariesLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var readTimer = Stopwatch.StartNew();
        var encodedBytes = File.ReadAllBytes(path);
        var readMs = readTimer.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

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

            ThrowOnError(JxlDecoderSubscribeEvents(decoder, JXL_DEC_BASIC_INFO | JXL_DEC_FULL_IMAGE), "JxlDecoderSubscribeEvents");
            ThrowOnError(JxlDecoderSetInput(decoder, inputHandle.AddrOfPinnedObject(), (nuint)encodedBytes.Length), "JxlDecoderSetInput");
            JxlDecoderCloseInput(decoder);

            var format = new JxlPixelFormat
            {
                NumChannels = 4,
                DataType = JXL_TYPE_UINT16,
                Endianness = JXL_LITTLE_ENDIAN,
                Align = 0,
            };
            var width = 0;
            var height = 0;
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

                        if (info.AlphaBits != 0)
                        {
                            // The djxl PPM16 path this decoder replaces always
                            // produced opaque RGB; route alpha-carrying images
                            // through that same fallback to render identically.
                            throw new InvalidOperationException("JPEG XL image has an alpha channel; deferring to the djxl fallback.");
                        }

                        break;

                    case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
                        ThrowOnError(JxlDecoderImageOutBufferSize(decoder, in format, out var bufferSize), "JxlDecoderImageOutBufferSize");
                        var expectedBytes = checked((nuint)width * (nuint)height * 8);
                        if (bufferSize != expectedBytes)
                        {
                            throw new InvalidOperationException($"libjxl requested an output buffer of {bufferSize} bytes for a {width}x{height} RGBA16 image (expected {expectedBytes}).");
                        }

                        pixels = new byte[checked((int)expectedBytes)];
                        outputHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                        ThrowOnError(JxlDecoderSetImageOutBuffer(decoder, in format, outputHandle.AddrOfPinnedObject(), bufferSize), "JxlDecoderSetImageOutBuffer");
                        break;

                    case JXL_DEC_FULL_IMAGE:
                        // First frame only; a still viewer never needs the
                        // remaining frames of an animated JPEG XL.
                        gotFullImage = true;
                        break;

                    case JXL_DEC_SUCCESS:
                        throw new InvalidOperationException("libjxl finished decoding without producing a full image.");

                    case JXL_DEC_NEED_MORE_INPUT:
                        throw new InvalidOperationException("libjxl requested more input past the end of the file (truncated JPEG XL stream).");

                    default:
                        throw new InvalidOperationException($"libjxl decode failed (JxlDecoderProcessInput status {status}).");
                }
            }

            if (pixels is null)
            {
                throw new InvalidOperationException("libjxl produced a full image without requesting an output buffer.");
            }

            var decodeMs = decodeTimer.ElapsedMilliseconds;
            return new DecodedBitmap(
                width,
                height,
                pixels,
                ColorManagedToSrgb: false,
                $"libjxl in-process [read {readMs}ms, decode {decodeMs}ms]; {colorDescription}",
                DecodedBitmapPixelFormat.Rgba16Unorm,
                transfer,
                usesBt2020Primaries,
                usesBt2020Primaries ? GainMapColorGamut.Bt2100 : GainMapColorGamut.Bt709);
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

            var jxlPath = NativeToolLocator.FindTool(JxlLibraryName)
                ?? throw new DllNotFoundException($"未找到 {JxlLibraryName}（libjxl 解码库）。");

            // Loading by absolute path first means the later DllImports on the
            // bare module name bind to this already-loaded module, without
            // registering a DllImportResolver (NativeExrDecoder already owns
            // the single resolver slot for this assembly).
            var jxlHandle = LoadLibraryEx(jxlPath, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            if (jxlHandle == IntPtr.Zero)
            {
                throw new DllNotFoundException($"加载 {jxlPath} 失败 (Win32 {Marshal.GetLastWin32Error()})，可能缺少依赖 DLL。");
            }

            // jxl_threads is optional: without it decoding still works, just
            // single-threaded.
            var threadsPath = Path.Combine(Path.GetDirectoryName(jxlPath)!, JxlThreadsLibraryName);
            if (!File.Exists(threadsPath))
            {
                threadsPath = NativeToolLocator.FindTool(JxlThreadsLibraryName) ?? string.Empty;
            }

            if (threadsPath.Length > 0)
            {
                var threadsHandle = LoadLibraryEx(threadsPath, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
                if (threadsHandle != IntPtr.Zero
                    && NativeLibrary.TryGetExport(threadsHandle, "JxlThreadParallelRunner", out var runnerFunction))
                {
                    _threadParallelRunnerFunction = runnerFunction;
                }
            }

            _librariesLoaded = true;
        }
    }

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

    /// <summary>Matches libjxl's JxlPixelFormat (types.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JxlPixelFormat
    {
        public uint NumChannels;
        public int DataType;
        public int Endianness;
        public nuint Align;
    }

    /// <summary>
    /// Matches libjxl's JxlBasicInfo (codestream_header.h, 204 bytes: 104 bytes
    /// of fields plus uint8_t padding[100]). Every field is a 4-byte scalar, so
    /// sequential layout matches the native struct with no packing concerns;
    /// the trailing reserved area is over-allocated to 104 bytes with ulongs.
    /// </summary>
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
        private ulong _padding0;
        private ulong _padding1;
        private ulong _padding2;
        private ulong _padding3;
        private ulong _padding4;
        private ulong _padding5;
        private ulong _padding6;
        private ulong _padding7;
        private ulong _padding8;
        private ulong _padding9;
        private ulong _padding10;
        private ulong _padding11;
        private ulong _padding12;
    }
}
