using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Windows.Graphics.Imaging;

internal static class ExportRegressionChecks
{
    private static async Task VerifyAlternateSpaceCropAsync(HdrImageDocument document, string jpeg, string directory)
    {
        var baseJpeg = Path.Combine(directory, "alternate-base.jpg");
        var auxiliary = Path.Combine(directory, "alternate-auxiliary.jpg");
        var config = Path.Combine(directory, "alternate.cfg");
        var original = Path.Combine(directory, "alternate.jpg");
        var output = Path.Combine(directory, "alternate-crop.jpg");
        await SdrImageExportService.ExportAsync(document, baseJpeg, jpeg: true, CancellationToken.None);
        // Use the fixture's ICC-tagged primary as the auxiliary of a new ISO JPEG.
        var bytes = await File.ReadAllBytesAsync(jpeg);
        await File.WriteAllBytesAsync(auxiliary, bytes[..checked((int)document.GainMapProbe!.PrimaryImageEndOffset!.Value)]);
        await File.WriteAllLinesAsync(config, (string[])[
            "--minContentBoost 1", "--maxContentBoost 2 4 8", "--gamma 1",
            "--offsetSdr 0", "--offsetHdr 0", "--hdrCapacityMin 1", "--hdrCapacityMax 8", "--useBaseColorSpace 0"]);
        using var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("ultrahdr_app.exe")!);
        foreach (var arg in new[] { "-m", "0", "-i", baseJpeg, "-g", auxiliary, "-f", config, "-z", original })
            encoder.StartInfo.ArgumentList.Add(arg);
        await NativeProcessRunner.RunAsync(encoder, "ISO JPEG fixture", CancellationToken.None);

        var source = (await ImageDocumentLoader.LoadAsync(original)).Document;
        await GainMapHdrExportService.ExportPreservedJpegGainMapCropAsync(source,
            new BitmapBounds { X = 8, Y = 8, Width = 128, Height = 64 }, output);
        var cropped = (await ImageDocumentLoader.LoadAsync(output)).Document;
        var metadata = cropped.GainMapProbe?.Metadata;
        if (source.GainMapProbe?.Metadata is not { UseBaseColorSpace: false } input
            || metadata is not { UseBaseColorSpace: false }
            || input.AlternateColorGamut == GainMapColorGamut.Unknown
            || metadata.AlternateColorGamut != input.AlternateColorGamut
            || metadata.GainMapMax != input.GainMapMax)
            throw new InvalidDataException($"Alternate-space crop changed metadata: {metadata}");
        Console.WriteLine($"PASS ISO JPEG preserved crop: alternate {metadata.AlternateColorGamut}, gain {metadata.GainMapMax}");
    }

    public static async Task RunAsync(string gainMapPath, string directory)
    {
        var loaded = await ImageDocumentLoader.LoadAsync(gainMapPath);
        var probe = loaded.Document.GainMapProbe
            ?? throw new InvalidDataException("An Ultra HDR JPEG fixture is required.");
        await VerifyAlternateSpaceCropAsync(loaded.Document, gainMapPath, directory);
        var referenceDocument = loaded.Document with { GainMapProbe = probe with { ExifOrientation = 1 } };
        var referencePath = Path.Combine(directory, "orientation-reference.png");
        await SingleLayerHdrExportService.ExportAsync(referenceDocument, referencePath, SingleLayerHdrExportTransfer.Pq);
        var reference = await ReadRgba16Async(referencePath);
        var sdrReferencePath = Path.Combine(directory, "orientation-reference-sdr.png");
        await SdrImageExportService.ExportAsync(referenceDocument, sdrReferencePath, jpeg: false, CancellationToken.None);
        var sdrReference = await ReadRgba16Async(sdrReferencePath);

        var reencodedPath = Path.Combine(directory, "orientation-reference.jpg");
        await GainMapHdrExportService.ExportAsync(referenceDocument, reencodedPath);
        var reencodedReference = await ReadRgba16Async(reencodedPath);
        var sourceReference = await ReadRgba16Async(gainMapPath, ColorManagementMode.ColorManageToSRgb);

        for (var orientation = 2; orientation <= 8; orientation++)
        {
            // The decoder consumes direction from the parsed probe and decodes
            // unrotated JPEG segments. Override that input without recompressing
            // the fixture, so every direction starts with identical raw pixels.
            var document = loaded.Document with { GainMapProbe = probe with { ExifOrientation = orientation } };
            var transform = new ExifOrientationTransform(reference.Width, reference.Height, orientation);
            var crop = new BitmapBounds { X = 17, Y = 23, Width = 131, Height = 79 };
            var full = new BitmapBounds { Width = (uint)transform.Width, Height = (uint)transform.Height };

            var pngPath = Path.Combine(directory, $"orientation-{orientation}.png");
            await SingleLayerHdrExportService.ExportAsync(document, pngPath, SingleLayerHdrExportTransfer.Pq);
            AssertPixels(await ReadRgba16Async(pngPath), reference, transform, full, tolerance: 0);

            var cropPath = Path.Combine(directory, $"orientation-{orientation}-crop.png");
            await SingleLayerHdrExportService.ExportAsync(document, crop, cropPath, SingleLayerHdrExportTransfer.Pq);
            AssertPixels(await ReadRgba16Async(cropPath), reference, transform, crop, tolerance: 0);

            var sdrPath = Path.Combine(directory, $"orientation-{orientation}-sdr.png");
            await SdrImageExportService.ExportAsync(document, sdrPath, jpeg: false, CancellationToken.None);
            AssertPixels(await ReadRgba16Async(sdrPath), sdrReference, transform, full, tolerance: 0);

            var jpegPath = Path.Combine(directory, $"orientation-{orientation}.jpg");
            await GainMapHdrExportService.ExportAsync(document, jpegPath);
            AssertPixels(await ReadRgba16Async(jpegPath), reencodedReference, transform, full, tolerance: 0.025);

            var preservedCrop = Path.Combine(directory, $"orientation-{orientation}-preserved.jpg");
            await GainMapHdrExportService.ExportPreservedJpegGainMapCropAsync(document, crop, preservedCrop);
            AssertPixels(await ReadRgba16Async(preservedCrop), sourceReference, transform, crop, tolerance: 0.025);
            Console.WriteLine($"PASS EXIF {orientation}: HDR/SDR PNG exact pixels and HDR crop; Gain Map re-encode and preserved crop");
        }

        foreach (var extension in new[] { ".tif", ".exr" })
        {
            var path = Path.Combine(directory, "linear-direct" + extension);
            var summary = await SingleLayerHdrExportService.ExportAsync(referenceDocument, path, SingleLayerHdrExportTransfer.Pq);
            if (!summary.Contains("linear scRGB", StringComparison.Ordinal)
                || summary.Contains("CLLI", StringComparison.Ordinal))
                throw new InvalidDataException($"Linear export reports discarded PQ statistics: {summary}");
            var reopened = await ImageDocumentLoader.LoadAsync(path);
            var bitmap = await BitmapDecodeService.DecodeDocumentForHdrExportAsync(reopened.Document);
            if (bitmap.PixelWidth != reference.Width || bitmap.PixelHeight != reference.Height || !bitmap.IsHdrEncoded)
                throw new InvalidDataException($"Invalid linear export round trip: {extension}");

            var protectedPath = Path.Combine(directory, "linear-protected" + extension);
            await File.WriteAllTextAsync(protectedPath, "existing destination");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await SingleLayerHdrExportService.ExportAsync(referenceDocument, protectedPath, SingleLayerHdrExportTransfer.Pq, canceled.Token);
                throw new InvalidDataException("Canceled linear export unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { }
            if (await File.ReadAllTextAsync(protectedPath) != "existing destination")
                throw new InvalidDataException("Canceled linear export modified the destination.");
            Console.WriteLine($"PASS {extension}: linear round trip and cancellation preserves destination");
        }
    }

    private static async Task<PixelImage> ReadRgba16Async(string path, ColorManagementMode colorManagement = ColorManagementMode.DoNotColorManage)
    {
        await using var stream = File.OpenRead(path);
        using var random = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, colorManagement);
        return new PixelImage(checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), data.DetachPixelData());
    }

    private static void AssertPixels(PixelImage actual, PixelImage reference, ExifOrientationTransform transform, BitmapBounds bounds, double tolerance)
    {
        if (actual.Width != bounds.Width || actual.Height != bounds.Height)
            throw new InvalidDataException($"Orientation dimensions differ: {actual.Width}x{actual.Height}, expected {bounds.Width}x{bounds.Height}.");
        double absoluteError = 0;
        long channelCount = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var source = transform.MapToSource(x + (int)bounds.X, y + (int)bounds.Y);
                var expectedOffset = (source.Y * reference.Width + source.X) * 8;
                var actualOffset = (y * actual.Width + x) * 8;
                for (var channel = 0; channel < 3; channel++)
                {
                    var expectedValue = BitConverter.ToUInt16(reference.Pixels, expectedOffset + channel * 2);
                    var actualValue = BitConverter.ToUInt16(actual.Pixels, actualOffset + channel * 2);
                    absoluteError += Math.Abs((int)expectedValue - actualValue) / 65535.0;
                    channelCount++;
                }
            }
        }
        var meanError = absoluteError / channelCount;
        if (meanError > tolerance)
            throw new InvalidDataException($"Orientation pixel mismatch: mean RGB error {meanError:P4}, limit {tolerance:P4}.");
    }

    private sealed record PixelImage(int Width, int Height, byte[] Pixels);
}
