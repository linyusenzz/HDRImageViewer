using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

/// <summary>
/// HDR color correctness gate for the in-process libjxl decoder: the RGBA16
/// pixels produced by <see cref="JxlNativeDecoder"/> must be byte-identical to
/// the djxl.exe --bits_per_sample 16 PPM16 path it replaces, for every PQ/HLG
/// JPEG XL fixture under the repo's local test/ directory. The test silently
/// passes when the local-only fixtures or bundled libjxl binaries are absent
/// (fresh checkouts / CI without the external/ toolchain).
/// </summary>
public sealed class JxlNativeDecoderParityTests
{
    [Fact]
    public async Task InProcessDecodeMatchesDjxlPpm16ForHdrFixtures()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var fixtures = FindJxlFixtures();
        var djxl = FindTool("djxl.exe");
        var jxlDll = FindTool("jxl.dll");
        if (fixtures.Count == 0 || djxl is null || jxlDll is null)
        {
            return;
        }

        foreach (var fixture in fixtures)
        {
            var (width, height, expectedPixels) = await DecodeWithDjxlToRgba16Async(djxl, fixture);
            var actual = JxlNativeDecoder.Decode(
                fixture,
                DecodedBitmapTransfer.Pq,
                usesBt2020Primaries: true,
                colorDescription: "parity test",
                CancellationToken.None);

            Assert.Equal(width, actual.PixelWidth);
            Assert.Equal(height, actual.PixelHeight);
            Assert.Equal(DecodedBitmapPixelFormat.Rgba16Unorm, actual.PixelFormat);
            Assert.Equal(expectedPixels.Length, actual.RgbaPixels.Length);

            var mismatch = DescribeFirstMismatch(expectedPixels, actual.RgbaPixels, width);
            Assert.True(mismatch is null, $"{Path.GetFileName(fixture)}: {mismatch}");
        }
    }

    private static string? DescribeFirstMismatch(byte[] expected, byte[] actual, int width)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return null;
        }

        var mismatchedSamples = 0;
        var maxDelta = 0;
        var firstIndex = -1;
        for (var i = 0; i + 1 < expected.Length; i += 2)
        {
            int e = expected[i] | (expected[i + 1] << 8);
            int a = actual[i] | (actual[i + 1] << 8);
            if (e != a)
            {
                mismatchedSamples++;
                maxDelta = Math.Max(maxDelta, Math.Abs(e - a));
                if (firstIndex < 0)
                {
                    firstIndex = i;
                }
            }
        }

        var pixel = firstIndex / 8;
        return $"pixels differ from the djxl PPM16 output: {mismatchedSamples} uint16 samples mismatch, max delta {maxDelta}, first at byte {firstIndex} (pixel {pixel % width},{pixel / width}).";
    }

    private static async Task<(int Width, int Height, byte[] Rgba16Pixels)> DecodeWithDjxlToRgba16Async(string djxl, string jxlPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewerTests", "jxl-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var ppmPath = Path.Combine(tempDir, "decoded.ppm");
        try
        {
            using var process = NativeProcessRunner.Create(djxl);
            process.StartInfo.ArgumentList.Add(jxlPath);
            process.StartInfo.ArgumentList.Add(ppmPath);
            process.StartInfo.ArgumentList.Add("--quiet");
            process.StartInfo.ArgumentList.Add("--output_format");
            process.StartInfo.ArgumentList.Add("ppm");
            process.StartInfo.ArgumentList.Add("--bits_per_sample");
            process.StartInfo.ArgumentList.Add("16");
            await NativeProcessRunner.RunAsync(process, "libjxl djxl", CancellationToken.None);
            return ReadPpm16AsRgba16(await File.ReadAllBytesAsync(ppmPath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Mirrors BitmapDecodeService.ReadPpmAsRgba16: big-endian PPM P6 samples
    /// to little-endian RGBA16 with opaque alpha.
    /// </summary>
    private static (int Width, int Height, byte[] Rgba16Pixels) ReadPpm16AsRgba16(byte[] data)
    {
        var offset = 0;
        var magic = ReadToken(data, ref offset);
        Assert.Equal("P6", magic);
        var width = int.Parse(ReadToken(data, ref offset), CultureInfo.InvariantCulture);
        var height = int.Parse(ReadToken(data, ref offset), CultureInfo.InvariantCulture);
        var maxValue = int.Parse(ReadToken(data, ref offset), CultureInfo.InvariantCulture);
        Assert.Equal(65535, maxValue);
        offset++; // single whitespace byte after the header

        var pixels = new byte[checked(width * height * 8)];
        var destination = 0;
        for (var i = 0; i < width * height; i++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[destination] = data[offset + 1];
                pixels[destination + 1] = data[offset];
                offset += 2;
                destination += 2;
            }

            pixels[destination] = 0xFF;
            pixels[destination + 1] = 0xFF;
            destination += 2;
        }

        return (width, height, pixels);
    }

    private static string ReadToken(byte[] data, ref int offset)
    {
        while (offset < data.Length && char.IsWhiteSpace((char)data[offset]))
        {
            offset++;
        }

        var start = offset;
        while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]))
        {
            offset++;
        }

        return Encoding.ASCII.GetString(data, start, offset - start);
    }

    private static List<string> FindJxlFixtures()
    {
        var fixtures = new List<string>();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var testDirectory = Path.Combine(directory.FullName, "test");
            if (Directory.Exists(testDirectory))
            {
                fixtures.AddRange(Directory.EnumerateFiles(testDirectory, "*.jxl", SearchOption.AllDirectories));
                if (fixtures.Count > 0)
                {
                    break;
                }
            }

            directory = directory.Parent;
        }

        return fixtures;
    }

    private static string? FindTool(string fileName)
    {
        return NativeToolLocator.FindTool(fileName);
    }
}
