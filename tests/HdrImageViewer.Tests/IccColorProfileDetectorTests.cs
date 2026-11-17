using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class IccColorProfileDetectorTests
{
    [Fact]
    public void DetectColorGamut_ReadsDisplayP3MatrixWithoutProfileName()
    {
        var profile = CreateMatrixProfile(
        [
            [0.515102, 0.241182, -0.001049],
            [0.291965, 0.692236, 0.041882],
            [0.157153, 0.066582, 0.784378],
        ]);

        var gamut = IccColorProfileDetector.DetectColorGamut(profile);

        Assert.Equal(GainMapColorGamut.DisplayP3, gamut);
    }

    [Fact]
    public void DetectColorInformation_ReadsCicpPrimariesAndTransfer()
    {
        var profile = CreateCicpProfile(primaries: 9, transfer: 18);

        Assert.Equal(9, IccColorProfileDetector.DetectColorPrimaries(profile));
        Assert.Equal(18, IccColorProfileDetector.DetectTransferCharacteristics(profile));
        Assert.Equal(GainMapColorGamut.Bt2100, IccColorProfileDetector.DetectColorGamut(profile));
    }

    [Fact]
    public void DetectColorGamut_ReadsAdobeRgb1998Matrix()
    {
        var profile = CreateMatrixProfile(
        [
            [0.609741, 0.311111, 0.019470],
            [0.205276, 0.625671, 0.060867],
            [0.149185, 0.063217, 0.744568],
        ]);

        Assert.Equal(GainMapColorGamut.AdobeRgb, IccColorProfileDetector.DetectColorGamut(profile));
    }

    [Fact]
    public void DetectColorGamut_ReadsProPhotoRommMatrix()
    {
        var profile = CreateMatrixProfile(
        [
            [0.797675, 0.288040, 0.000000],
            [0.135192, 0.711874, 0.000000],
            [0.031353, 0.000086, 0.824905],
        ]);

        Assert.Equal(GainMapColorGamut.ProPhoto, IccColorProfileDetector.DetectColorGamut(profile));
    }

    [Fact]
    public void DetectColorGamut_ReadsIccV4MlucDescriptionAndExtractsProfileName()
    {
        var p3Profile = CreateMlucDescProfile("Display P3");
        var adobeProfile = CreateV2DescProfile("Adobe RGB (1998)");
        var proPhotoProfile = CreateMlucDescProfile("ProPhoto RGB");

        Assert.Equal(GainMapColorGamut.DisplayP3, IccColorProfileDetector.DetectColorGamut(p3Profile));
        Assert.Equal("Display P3", IccColorProfileDetector.ExtractProfileDescription(p3Profile));
        Assert.Equal(GainMapColorGamut.AdobeRgb, IccColorProfileDetector.DetectColorGamut(adobeProfile));
        Assert.Equal("Adobe RGB (1998)", IccColorProfileDetector.ExtractProfileDescription(adobeProfile));
        Assert.Equal(GainMapColorGamut.ProPhoto, IccColorProfileDetector.DetectColorGamut(proPhotoProfile));
        Assert.Equal("ProPhoto RGB", IccColorProfileDetector.ExtractProfileDescription(proPhotoProfile));
    }

    [Fact]
    public void DetectColorGamut_DoesNotFalseMatchRandomBinaryLutBytesContainingP3()
    {
        var profile = CreateTagTableProfile(("desc", 32), ("A2B0", 64));
        var descOffset = ReadTagOffset(profile, 0);
        WriteAscii(profile, descOffset, "desc");
        var text = Encoding.ASCII.GetBytes("Custom Camera Profile\0");
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(descOffset + 8, 4), (uint)text.Length);
        text.CopyTo(profile.AsSpan(descOffset + 12));

        var lutOffset = ReadTagOffset(profile, 1);
        WriteAscii(profile, lutOffset, "mft2");
        // Embed literal "P3" in binary LUT table bytes; must NOT trigger DisplayP3!
        WriteAscii(profile, lutOffset + 16, "P3P3");

        Assert.Equal(GainMapColorGamut.Unknown, IccColorProfileDetector.DetectColorGamut(profile));
    }

    [Fact]
    public async Task WicImageProbeResult_PersistsMatrixDetectedIccGamutAcrossDirectoryMetadataCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hdriv-icc-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tiffPath = Path.Combine(directory, "matrix-adobe.tiff");
            await File.WriteAllBytesAsync(tiffPath, [0x49, 0x49, 0x2A, 0x00]);
            var wicProbe = new WicImageProbeResult(
                IsWicCandidate: true,
                Extension: ".tiff",
                PixelWidth: 64,
                PixelHeight: 64,
                BitDepth: 48,
                ColorSpace: "Uncalibrated",
                DecoderName: "WIC TIFF",
                TiffBitsPerSample: [16, 16, 16],
                TiffSamplesPerPixel: 3,
                TiffSampleFormat: 1,
                TiffPhotometricInterpretation: 2,
                TiffIccProfileName: null,
                IccColorGamut: GainMapColorGamut.AdobeRgb);
            var descriptor = DecoderCatalog.Describe(tiffPath, wicImageProbe: wicProbe);
            var document = new HdrImageDocument(tiffPath, Path.GetFileName(tiffPath), descriptor, WicImageProbe: wicProbe);
            var loadResult = new ImageLoadResult(document, "EXIF", File.GetLastWriteTimeUtc(tiffPath));

            await DirectoryMetadataCache.StoreAsync(loadResult, FileContainerKind.Unknown);
            await DirectoryMetadataCache.FlushAsync();

            var cacheJson = await File.ReadAllTextAsync(DirectoryMetadataCache.GetCacheFilePath(directory));
            var deserialized = System.Text.Json.JsonSerializer.Deserialize(
                cacheJson,
                DirectoryMetadataJsonContext.Default.DirectoryMetadataCacheFile);
            Assert.NotNull(deserialized);
            var persistedProbe = deserialized.Entries["matrix-adobe.tiff"].WicImageProbe;
            Assert.NotNull(persistedProbe);
            Assert.Null(persistedProbe.TiffIccProfileName);
            Assert.Equal(GainMapColorGamut.AdobeRgb, persistedProbe.IccColorGamut);
            Assert.Equal(GainMapColorGamut.AdobeRgb, persistedProbe.ResolvedColorGamut);

            var cached = await DirectoryMetadataCache.TryLoadAsync(tiffPath);
            Assert.NotNull(cached?.Document.WicImageProbe);
            Assert.Equal(GainMapColorGamut.AdobeRgb, cached.Document.WicImageProbe.IccColorGamut);
            Assert.Equal(GainMapColorGamut.AdobeRgb, cached.Document.WicImageProbe.ResolvedColorGamut);
        }
        finally
        {
            await DirectoryMetadataCache.FlushAsync();
            var cachePath = DirectoryMetadataCache.GetCacheFilePath(directory);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] CreateV2DescProfile(string description)
    {
        var asciiBytes = Encoding.ASCII.GetBytes(description + "\0");
        var tagLength = 12 + asciiBytes.Length;
        var profile = CreateTagTableProfile(("desc", tagLength));
        var offset = ReadTagOffset(profile, 0);
        WriteAscii(profile, offset, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset + 8, 4), (uint)asciiBytes.Length);
        asciiBytes.CopyTo(profile.AsSpan(offset + 12));
        return profile;
    }

    private static byte[] CreateMlucDescProfile(string description)
    {
        var utf16 = Encoding.BigEndianUnicode.GetBytes(description);
        var tagLength = 28 + utf16.Length;
        var profile = CreateTagTableProfile(("desc", tagLength));
        var offset = ReadTagOffset(profile, 0);
        WriteAscii(profile, offset, "mluc");
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset + 8, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset + 12, 4), 12u);
        WriteAscii(profile, offset + 16, "enUS");
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset + 20, 4), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset + 24, 4), 28u);
        utf16.CopyTo(profile.AsSpan(offset + 28));
        return profile;
    }

    private static byte[] CreateCicpProfile(byte primaries, byte transfer)
    {
        var profile = CreateTagTableProfile(("cicp", 12));
        var offset = ReadTagOffset(profile, 0);
        WriteAscii(profile, offset, "cicp");
        profile[offset + 8] = primaries;
        profile[offset + 9] = transfer;
        profile[offset + 10] = 9;
        profile[offset + 11] = 1;
        return profile;
    }

    private static byte[] CreateMatrixProfile(double[][] matrix)
    {
        var profile = CreateTagTableProfile(("rXYZ", 20), ("gXYZ", 20), ("bXYZ", 20));
        for (var index = 0; index < 3; index++)
        {
            var offset = ReadTagOffset(profile, index);
            WriteAscii(profile, offset, "XYZ ");
            WriteS15Fixed16(profile, offset + 8, matrix[index][0]);
            WriteS15Fixed16(profile, offset + 12, matrix[index][1]);
            WriteS15Fixed16(profile, offset + 16, matrix[index][2]);
        }

        return profile;
    }

    private static byte[] CreateTagTableProfile(params (string Signature, int Length)[] tags)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        var tableLength = 4 + (tags.Length * tagEntryLength);
        var dataOffset = headerLength + tableLength;
        var profile = new byte[dataOffset + tags.Sum(tag => tag.Length)];
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(headerLength, 4), tags.Length);

        for (var index = 0; index < tags.Length; index++)
        {
            var entryOffset = headerLength + 4 + (index * tagEntryLength);
            WriteAscii(profile, entryOffset, tags[index].Signature);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entryOffset + 4, 4), dataOffset);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entryOffset + 8, 4), tags[index].Length);
            dataOffset += tags[index].Length;
        }

        return profile;
    }

    private static int ReadTagOffset(byte[] profile, int index)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        return BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(headerLength + 4 + (index * tagEntryLength) + 4, 4));
    }

    private static void WriteS15Fixed16(byte[] data, int offset, double value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), (int)Math.Round(value * 65536.0));
    }

    private static void WriteAscii(byte[] data, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            data[offset + i] = (byte)text[i];
        }
    }
}
