using System.Buffers.Binary;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using Windows.Graphics.Imaging;

internal static class ThumbnailRegressionChecks
{
    private static async Task VerifyFailedNavigationAsync(string validPath, string directory)
    {
        var corruptHdr = Path.Combine(directory, "corrupt.HDR");
        var corrupt = Path.Combine(directory, "corrupt.png");
        var header = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4 +X 8\n");
        await File.WriteAllBytesAsync(corruptHdr, [.. header, 0x02, 0x02, 0x00, 0x08, 0x85]);
        await File.WriteAllTextAsync(corrupt, "invalid PNG fixture");
        var attempts = new List<string>();
        string? displayed = null;
        var failures = await ImageNavigationContext.NavigateAsync(
            new[] { validPath, corruptHdr, corrupt, validPath }, 0, 1, async path =>
            {
                attempts.Add(path);
                try
                {
                    await ImagePreloadCache.PreloadAsync(path, 192);
                    displayed = path;
                    return ImageLoadOutcome.Opened;
                }
                catch (Exception)
                {
                    return ImageLoadOutcome.Failed;
                }
            }, CancellationToken.None);
        if (failures.Count != 2 || attempts.Count != 3 || displayed != validPath)
            throw new InvalidDataException("Real corrupt files blocked navigation to the next valid image.");
        Console.WriteLine("PASS file navigation: corrupt Radiance .HDR and corrupt PNG failed; following valid image decoded successfully");
        await VerifyValidRadianceHdrAsync(directory);
    }

    private static async Task VerifyValidRadianceHdrAsync(string directory)
    {
        var validHdr = Path.Combine(directory, "valid-scene.hdr");
        var header = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 8 +X 16\n");
        var bytes = new List<byte>(header);
        for (var y = 0; y < 8; y++)
        {
            bytes.AddRange(new byte[] { 0x02, 0x02, 0x00, 0x10 });
            bytes.AddRange(new byte[] { 128 + 8, 64, 128 + 8, 200 });     // R: dark left, bright right
            bytes.AddRange(new byte[] { 128 + 8, 64, 128 + 8, 160 });     // G
            bytes.AddRange(new byte[] { 128 + 8, 64, 128 + 8, 80 });      // B
            bytes.AddRange(new byte[] { 128 + 8, 128, 128 + 8, 132 });    // E: ~0.25 left, ~12.5 HDR highlight right
        }

        await File.WriteAllBytesAsync(validHdr, bytes.ToArray());
        var loaded = await ImageDocumentLoader.LoadAsync(validHdr);
        if (loaded.Document.Format.Kind != HdrImageKind.SingleLayerHdr)
            throw new InvalidDataException($"Valid Radiance .hdr was not classified as SingleLayerHdr: {loaded.Document.Format.Kind}.");

        await ImagePreloadCache.PreloadAsync(validHdr, 8);
        var thumb = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(loaded.Document, 8, CancellationToken.None)
            ?? throw new InvalidDataException("Radiance .hdr thumbnail decode returned null.");
        if (!thumb.IsHdrEncoded || thumb.PixelFormat != DecodedBitmapPixelFormat.Rgba16Float || thumb.PixelWidth != 8 || thumb.PixelHeight != 4)
            throw new InvalidDataException($"Unexpected Radiance .hdr thumbnail output: {thumb.RenderEncodingSummary} {thumb.PixelWidth}x{thumb.PixelHeight}.");

        var highlightR = ReadHalf(thumb.RgbaPixels, (2 * thumb.PixelWidth + 6) * 8);
        if (highlightR < 10.0f)
            throw new InvalidDataException($"Radiance .hdr lost HDR highlight amplitude: {highlightR}.");

        var bgra = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(thumb, CancellationToken.None);
        if (bgra.Length != thumb.PixelWidth * thumb.PixelHeight * 4 || bgra[2] >= bgra[(6 * 4) + 2])
            throw new InvalidDataException("Radiance .hdr tone-mapped BGRA8 thumbnail failed contrast check.");
        Console.WriteLine($"PASS Radiance .hdr: {thumb.PixelWidth}x{thumb.PixelHeight} RGBA16F; peak R={highlightR:F2}; HDR thumbnail tone-mapped cleanly");
    }

    public static async Task RunAsync(string hdrSource, string directory)
    {
        await VerifyFailedNavigationAsync(hdrSource, directory);
        var sdrPng = Path.Combine(directory, "transparent-srgb.png");
        var p3Png = Path.Combine(directory, "transparent-p3.png");
        var pqPng = Path.Combine(directory, "transparent-pq.png");
        await WriteTransparentFixtureAsync(sdrPng);
        await AddCicpAsync(sdrPng, p3Png, 12, 13);
        await AddCicpAsync(sdrPng, pqPng, 9, 16);
        var jxl = Path.Combine(directory, "transparent-sdr.jxl");
        var hdrJxl = Path.Combine(directory, "transparent-pq.jxl");
        foreach (var pair in new[] { (Source: sdrPng, Output: jxl), (Source: pqPng, Output: hdrJxl) })
        {
            using var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("cjxl.exe")
                ?? throw new InvalidOperationException("cjxl is required for the SDR JXL thumbnail regression."));
            foreach (var argument in new[] { pair.Source, pair.Output, "-d", "0", "-e", "1" })
                encoder.StartInfo.ArgumentList.Add(argument);
            await NativeProcessRunner.RunAsync(encoder, "JXL fixture", CancellationToken.None);
        }

        foreach (var path in new[] { jxl, p3Png, hdrJxl })
        {
            var loaded = await ImageDocumentLoader.LoadAsync(path);
            var thumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(loaded.Document, 192, CancellationToken.None)
                ?? throw new InvalidDataException($"Thumbnail route skipped {Path.GetFileName(path)}.");
            if (thumbnail.IsHdrEncoded != (path == hdrJxl) || thumbnail.PixelWidth != 192 || thumbnail.PixelHeight != 48)
                throw new InvalidDataException($"Unexpected SDR thumbnail: {thumbnail.RenderEncodingSummary}; {thumbnail.PixelWidth}x{thumbnail.PixelHeight}.");
            var pixels = thumbnail.IsHdrEncoded
                ? ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(thumbnail, CancellationToken.None)
                : ThumbnailPixelConverter.ConvertSdrToBgra8(thumbnail, CancellationToken.None, premultiplyAlpha: true);
            var opaque = thumbnail.IsHdrEncoded
                ? ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(WithOpaqueAlpha(thumbnail), CancellationToken.None)
                : ThumbnailPixelConverter.ConvertSdrToBgra8(thumbnail, CancellationToken.None);
            for (var band = 0; band < 3; band++)
            {
                var x = (band * 64) + 32;
                var offset = (24 * thumbnail.PixelWidth + x) * 4;
                var expectedAlpha = new[] { 0, 128, 255 }[band];
                if (Math.Abs(pixels[offset + 3] - expectedAlpha) > 1)
                    throw new InvalidDataException($"Thumbnail alpha lost: {Path.GetFileName(path)}, band {band}, alpha {pixels[offset + 3]}.");
                for (var channel = 0; channel < 3; channel++)
                {
                    var expected = opaque[offset + channel] * expectedAlpha / 255.0;
                    if (Math.Abs(pixels[offset + channel] - expected) > 1)
                        throw new InvalidDataException("WriteableBitmap thumbnail RGB is not premultiplied.");
                }
            }

            var mainPreview = await BitmapDecodeService.DecodeDocumentAsync(loaded.Document, 384);
            var fullThumbnail = await BitmapDecodeService.DecodeDocumentForThumbnailAsync(loaded.Document, 384);
            var legacyExport = await BitmapDecodeService.DecodeDocumentForHdrExportAsync(loaded.Document);
            if (!mainPreview.RgbaPixels.AsSpan().SequenceEqual(fullThumbnail.RgbaPixels))
                throw new InvalidDataException($"Main preview decode lost transparency for {Path.GetFileName(path)}.");
            if (fullThumbnail.PixelFormat != legacyExport.PixelFormat || fullThumbnail.PixelWidth != legacyExport.PixelWidth
                || fullThumbnail.PixelHeight != legacyExport.PixelHeight)
                throw new InvalidDataException("Thumbnail alpha changed the legacy decode layout.");
            var bytesPerChannel = fullThumbnail.BytesPerPixel / 4;
            for (var offset = 0; offset < legacyExport.RgbaPixels.Length; offset += legacyExport.BytesPerPixel)
            {
                if (!fullThumbnail.RgbaPixels.AsSpan(offset, bytesPerChannel * 3)
                    .SequenceEqual(legacyExport.RgbaPixels.AsSpan(offset, bytesPerChannel * 3)))
                    throw new InvalidDataException("Thumbnail alpha changed RGB compared with the legacy export decode.");
            }
            Console.WriteLine($"PASS {Path.GetFileName(path)}: main preview + thumbnail alpha 0/128/255 preserved; premultiplied BGRA; export RGB unchanged");
        }

        await VerifyBt709AndPremultipliedJxlInProcessAsync(sdrPng, directory);

        var hdr = await ImageDocumentLoader.LoadAsync(hdrSource);
        await ImagePreloadCache.PreloadAsync(hdrSource, 192);
        if (!ImagePreloadCache.TryGetBaseBitmap(hdrSource, File.GetLastWriteTimeUtc(hdrSource), 192, out var prepared)
            || !prepared.IsHdrEncoded || Math.Max(prepared.PixelWidth, prepared.PixelHeight) > 192)
            throw new InvalidDataException("Prepared HDR frame is unavailable for the viewer handoff.");
        Console.WriteLine("PASS prepared HDR handoff: decoded frame ready in cache before presentation");
        var hdrThumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(hdr.Document, 192, CancellationToken.None)
            ?? throw new InvalidDataException("HDR thumbnail route was skipped.");
        if (!hdrThumbnail.IsHdrEncoded || Math.Max(hdrThumbnail.PixelWidth, hdrThumbnail.PixelHeight) > 192)
            throw new InvalidDataException("HDR thumbnail lost transfer or exceeded its requested size.");
        var mapped = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(hdrThumbnail, CancellationToken.None);
        if (mapped.Length != hdrThumbnail.PixelWidth * hdrThumbnail.PixelHeight * 4)
            throw new InvalidDataException("HDR thumbnail BGRA size mismatch.");
        Console.WriteLine($"PASS HDR thumbnail: {hdrThumbnail.PixelWidth}x{hdrThumbnail.PixelHeight}; {hdrThumbnail.Transfer}");

        var minimal = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(hdr.Document, 0, CancellationToken.None);
        if (minimal is null || Math.Max(minimal.PixelWidth, minimal.PixelHeight) != 1)
            throw new InvalidDataException("Zero thumbnail size must be clamped to one pixel, not decode full resolution.");
        Console.WriteLine("PASS zero thumbnail size clamps to 1 pixel");
        await VerifyHdrAppearanceAsync(directory);
    }

    private static async Task VerifyBt709AndPremultipliedJxlInProcessAsync(string sdrPng, string directory)
    {
        var bt709Png = Path.Combine(directory, "transparent-bt709.png");
        await AddCicpAsync(sdrPng, bt709Png, 1, 1);
        var bt709Jxl = Path.Combine(directory, "transparent-bt709.jxl");
        using (var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("cjxl.exe")!))
        {
            foreach (var argument in new[] { bt709Png, bt709Jxl, "-d", "0", "-e", "1" })
                encoder.StartInfo.ArgumentList.Add(argument);
            await NativeProcessRunner.RunAsync(encoder, "BT.709 JXL fixture", CancellationToken.None);
        }

        var bt709Doc = await ImageDocumentLoader.LoadAsync(bt709Jxl);
        var bt709Decoded = await BitmapDecodeService.DecodeDocumentAsync(bt709Doc.Document);
        if (!bt709Decoded.DecoderName.StartsWith("libjxl in-process", StringComparison.Ordinal)
            || bt709Decoded.Transfer != DecodedBitmapTransfer.Bt709
            || bt709Decoded.ColorManagedToSrgb)
            throw new InvalidDataException($"BT.709 JXL was not decoded as raw BT.709 in-process: {bt709Decoded.RenderEncodingSummary}");

        var premulSourcePng = Path.Combine(directory, "premultiplied-source.png");
        {
            const int pw = 64, ph = 16;
            var premulPixels = new byte[pw * ph * 4];
            for (var i = 0; i < premulPixels.Length; i += 4)
            {
                premulPixels[i] = 100;     // 200 * (128/255) premultiplied R
                premulPixels[i + 1] = 50;  // 100 * (128/255) premultiplied G
                premulPixels[i + 2] = 25;  // 50  * (128/255) premultiplied B
                premulPixels[i + 3] = 128; // 50% alpha
            }
            await using var s = new FileStream(premulSourcePng, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var r = s.AsRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, r);
            enc.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, pw, ph, 96, 96, premulPixels);
            await enc.FlushAsync();
        }

        var premulJxl = Path.Combine(directory, "premultiplied-sdr.jxl");
        using (var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("cjxl.exe")!))
        {
            foreach (var argument in new[] { premulSourcePng, premulJxl, "-d", "0", "-e", "1", "--premultiply=1" })
                encoder.StartInfo.ArgumentList.Add(argument);
            await NativeProcessRunner.RunAsync(encoder, "premultiplied JXL fixture", CancellationToken.None);
        }

        var premulDoc = await ImageDocumentLoader.LoadAsync(premulJxl);
        var premulDecoded = await BitmapDecodeService.DecodeDocumentAsync(premulDoc.Document);
        if (!premulDecoded.DecoderName.StartsWith("libjxl in-process", StringComparison.Ordinal))
            throw new InvalidDataException($"Premultiplied-alpha JXL bailed out of in-process libjxl: {premulDecoded.DecoderName}");
        var halfAlphaOffset = (8 * premulDecoded.PixelWidth + 32) * premulDecoded.BytesPerPixel;
        if (premulDecoded.BytesPerPixel != 4
            || Math.Abs(premulDecoded.RgbaPixels[halfAlphaOffset] - 200) > 2
            || Math.Abs(premulDecoded.RgbaPixels[halfAlphaOffset + 1] - 100) > 2
            || Math.Abs(premulDecoded.RgbaPixels[halfAlphaOffset + 3] - 128) > 1)
            throw new InvalidDataException($"Premultiplied-alpha JXL was not unpremultiplied cleanly: RGBA=({premulDecoded.RgbaPixels[halfAlphaOffset]},{premulDecoded.RgbaPixels[halfAlphaOffset + 1]},{premulDecoded.RgbaPixels[halfAlphaOffset + 2]},{premulDecoded.RgbaPixels[halfAlphaOffset + 3]}).");
        Console.WriteLine("PASS BT.709 transfer JXL & premultiplied-alpha JXL: both decoded in-process via libjxl with accurate transfer and unpremultiplied alpha");
    }

    private static async Task VerifyHdrAppearanceAsync(string directory)
    {
        const int width = 640, height = 320;
        float[] gray = [0f, 0.01f, 0.05f, 0.18f, 0.5f, 1f, 203f / 80f, 8f];
        var scenePixels = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var white = gray[x / 80];
                var rgb = y < height / 2 ? new Vector3(white) : new Vector3(white, white * 0.5f, white * 0.1f);
                var offset = (y * width + x) * 8;
                BinaryPrimitives.WriteUInt16LittleEndian(scenePixels.AsSpan(offset), BitConverter.HalfToUInt16Bits((Half)rgb.X));
                BinaryPrimitives.WriteUInt16LittleEndian(scenePixels.AsSpan(offset + 2), BitConverter.HalfToUInt16Bits((Half)rgb.Y));
                BinaryPrimitives.WriteUInt16LittleEndian(scenePixels.AsSpan(offset + 4), BitConverter.HalfToUInt16Bits((Half)rgb.Z));
                BinaryPrimitives.WriteUInt16LittleEndian(scenePixels.AsSpan(offset + 6), BitConverter.HalfToUInt16Bits((Half)1));
            }
        }
        var scenePath = Path.Combine(directory, "appearance-scene.exr");
        NativeExrDecoder.Encode(scenePath, width, height, scenePixels);
        await VerifyLinearJxlAsync(directory, width, height, scenePixels);
        var scene = await ImageDocumentLoader.LoadAsync(scenePath);
        byte[]? pqPreview = null;
        foreach (var transfer in new[] { SingleLayerHdrExportTransfer.Pq, SingleLayerHdrExportTransfer.Hlg })
        {
            var path = Path.Combine(directory, $"appearance-{transfer}.png");
            await SingleLayerHdrExportService.ExportAsync(scene.Document, path, transfer);
            var loaded = await ImageDocumentLoader.LoadAsync(path);
            var thumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(loaded.Document, 192, CancellationToken.None)
                ?? throw new InvalidDataException("Single-layer HDR appearance route was skipped.");
            var previous = ThumbnailPixelConverter.ConvertHdrToBgra8(thumbnail, CancellationToken.None);
            var preview = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(thumbnail, CancellationToken.None);
            await WriteBgraAsync(Path.Combine(directory, $"appearance-{transfer}-before.png"), thumbnail.PixelWidth, thumbnail.PixelHeight, previous);
            await WriteBgraAsync(Path.Combine(directory, $"appearance-{transfer}-after.png"), thumbnail.PixelWidth, thumbnail.PixelHeight, preview);
            if (pqPreview is null) pqPreview = preview;
            else
            {
                var error = MeanRgbError(pqPreview, preview);
                if (error > 1.5) throw new InvalidDataException($"Equivalent PQ/HLG thumbnails differ by {error:F3} RGB levels.");
                Console.WriteLine($"PASS equivalent PQ/HLG appearance: mean RGB difference {error:F3}/255; 203-nit white, grayscale and warm colors");
            }
        }

        var gainPath = Path.Combine(directory, "appearance-gainmap.jpg");
        await GainMapHdrExportService.ExportAsync(scene.Document, gainPath);
        await VerifyGainMapAppearanceAsync(gainPath, directory, "appearance-gainmap");
        var photograph = Path.GetFullPath("artifacts/issue-9/reported-ultrahdr.jpg");
        if (File.Exists(photograph)) await VerifyGainMapAppearanceAsync(photograph, directory, "photograph-gainmap");
    }

    private static async Task VerifyLinearJxlAsync(string directory, int width, int height, byte[] scenePixels)
    {
        var alphaPixels = (byte[])scenePixels.Clone();
        for (var y = 0; y < height; y++)
        {
            var alpha = y < height / 3 ? 0f : y < height * 2 / 3 ? 0.5f : 1f;
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 8;
                // OpenEXR stores associated alpha; retain valid premultiplied
                // source pixels, including >1 values in the half-alpha band.
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = ReadHalf(alphaPixels, offset + channel * 2) * alpha;
                    BinaryPrimitives.WriteUInt16LittleEndian(alphaPixels.AsSpan(offset + channel * 2),
                        BitConverter.HalfToUInt16Bits((Half)value));
                }
                BinaryPrimitives.WriteUInt16LittleEndian(alphaPixels.AsSpan(offset + 6), BitConverter.HalfToUInt16Bits((Half)alpha));
            }
        }
        var exrPath = Path.Combine(directory, "linear-alpha-source.exr");
        var jxlPath = Path.Combine(directory, "linear-alpha.jxl");
        NativeExrDecoder.Encode(exrPath, width, height, alphaPixels);
        using (var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("cjxl.exe")
            ?? throw new InvalidOperationException("cjxl is required for the floating-point JXL regression.")))
        {
            foreach (var argument in new[] { exrPath, jxlPath, "-d", "0", "-e", "1" })
                encoder.StartInfo.ArgumentList.Add(argument);
            await NativeProcessRunner.RunAsync(encoder, "floating-point JXL fixture", CancellationToken.None);
        }
        var sourceDocument = await ImageDocumentLoader.LoadAsync(exrPath);
        var jxlDocument = await ImageDocumentLoader.LoadAsync(jxlPath);
        if (jxlDocument.Document.JxlProbe?.TransferFunction != "linear")
            throw new InvalidDataException("Floating-point JXL fixture lost its linear color encoding.");
        var source = await BitmapDecodeService.DecodeDocumentForHdrExportAsync(sourceDocument.Document);
        var decoded = await BitmapDecodeService.DecodeDocumentForHdrExportAsync(jxlDocument.Document);
        if (decoded.PixelFormat != DecodedBitmapPixelFormat.Rgba16Float
            || decoded.Transfer != DecodedBitmapTransfer.LinearSceneScRgb
            || decoded.EffectiveColorGamut != GainMapColorGamut.Bt709
            || decoded.PixelWidth != width || decoded.PixelHeight != height)
            throw new InvalidDataException($"Linear JXL lost floating-point scene-linear BT.709 output: {decoded.RenderEncodingSummary}.");
        double maxRgbError = 0, maxAlphaError = 0;
        float peak = 0;
        for (var offset = 0; offset < source.RgbaPixels.Length; offset += 8)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                var actual = ReadHalf(decoded.RgbaPixels, offset + channel * 2);
                var expected = ReadHalf(source.RgbaPixels, offset + channel * 2);
                var error = Math.Abs(actual - expected);
                if (channel < 3) { maxRgbError = Math.Max(maxRgbError, error); peak = Math.Max(peak, actual); }
                else maxAlphaError = Math.Max(maxAlphaError, error);
            }
        }
        if (maxRgbError > 0.01 || maxAlphaError > 0.001 || peak < 7.99f)
            throw new InvalidDataException($"Linear JXL altered HDR/alpha: RGB error {maxRgbError}, alpha error {maxAlphaError}, peak {peak}.");

        var sourceThumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(sourceDocument.Document, 192, CancellationToken.None)
            ?? throw new InvalidDataException("EXR thumbnail route was skipped.");
        var jxlThumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(jxlDocument.Document, 192, CancellationToken.None)
            ?? throw new InvalidDataException("Linear JXL thumbnail route was skipped.");
        if (jxlThumbnail.PixelWidth != 192 || jxlThumbnail.PixelHeight != 96
            || jxlThumbnail.Transfer != sourceThumbnail.Transfer)
            throw new InvalidDataException("Linear JXL thumbnail changed dimensions or scene-linear units.");
        var reference = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(sourceThumbnail, CancellationToken.None);
        var preview = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(jxlThumbnail, CancellationToken.None);
        var previewError = MeanRgbError(reference, preview);
        if (previewError > 1)
            throw new InvalidDataException($"Linear JXL thumbnail differs from source EXR by {previewError:F3}/255.");
        for (var offset = 3; offset < reference.Length; offset += 4)
            if (reference[offset] != preview[offset]) throw new InvalidDataException("Linear JXL thumbnail alpha differs from source EXR.");
        await WriteBgraAsync(Path.Combine(directory, "linear-alpha-source-preview.png"), sourceThumbnail.PixelWidth, sourceThumbnail.PixelHeight, reference);
        await WriteBgraAsync(Path.Combine(directory, "linear-alpha-jxl-preview.png"), jxlThumbnail.PixelWidth, jxlThumbnail.PixelHeight, preview);
        Console.WriteLine($"PASS floating-point linear JXL: scene-linear BT.709; RGB peak {peak:F3}, max RGB error {maxRgbError:F6}, alpha error {maxAlphaError:F6}; thumbnail 192x96, mean RGB difference {previewError:F3}/255 vs source EXR");
        await VerifyLinearWhiteAlphaAsync(directory);
    }

    private static async Task VerifyLinearWhiteAlphaAsync(string directory)
    {
        const int width = 384, height = 96;
        var pixels = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = x < 128 ? 0f : x < 256 ? 0.5f : 1f;
                var offset = (y * width + x) * 8;
                for (var channel = 0; channel < 3; channel++)
                    BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + channel * 2),
                        BitConverter.HalfToUInt16Bits((Half)(203f / 80f * alpha)));
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 6), BitConverter.HalfToUInt16Bits((Half)alpha));
            }
        var exrPath = Path.Combine(directory, "linear-white-alpha.exr");
        var jxlPath = Path.Combine(directory, "linear-white-alpha.jxl");
        NativeExrDecoder.Encode(exrPath, width, height, pixels);
        using (var encoder = NativeProcessRunner.Create(NativeToolLocator.FindTool("cjxl.exe")!))
        {
            foreach (var argument in new[] { exrPath, jxlPath, "-d", "0", "-e", "1" })
                encoder.StartInfo.ArgumentList.Add(argument);
            await NativeProcessRunner.RunAsync(encoder, "linear white alpha fixture", CancellationToken.None);
        }
        foreach (var path in new[] { exrPath, jxlPath })
        {
            var document = await ImageDocumentLoader.LoadAsync(path);
            var thumbnail = await PhotoThumbnailService.DecodeDocumentThumbnailAsync(document.Document, 192, CancellationToken.None)
                ?? throw new InvalidDataException("Linear white alpha thumbnail route was skipped.");
            var bgra = ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(thumbnail, CancellationToken.None);
            for (var band = 0; band < 3; band++)
            {
                var offset = (24 * thumbnail.PixelWidth + band * 64 + 32) * 4;
                var expected = new[] { 0, 128, 255 }[band];
                for (var channel = 0; channel < 4; channel++)
                    if (Math.Abs(bgra[offset + channel] - expected) > 1)
                        throw new InvalidDataException($"{Path.GetFileName(path)} white alpha band {band}: BGRA channel {channel} is {bgra[offset + channel]}, expected {expected}; associated alpha was applied twice.");
            }
            Console.WriteLine($"PASS {Path.GetFileName(path)}: known 203-nit white, premultiplied BGRA 0/128/255 across alpha 0/0.5/1");
        }
    }

    private static float ReadHalf(byte[] pixels, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(offset)));

    private static async Task VerifyGainMapAppearanceAsync(string gainPath, string directory, string prefix)
    {
        var gainDocument = await ImageDocumentLoader.LoadAsync(gainPath);
        var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(gainDocument.Document, 192);
        await ImagePreloadCache.PreloadAsync(gainPath, 192);
        if (!ImagePreloadCache.TryGetGainMapInputs(gainPath, File.GetLastWriteTimeUtc(gainPath), 192, out var prepared)
            || !prepared.Primary.RgbaPixels.AsSpan().SequenceEqual(inputs.Primary.RgbaPixels)
            || !prepared.GainMap.RgbaPixels.AsSpan().SequenceEqual(inputs.GainMap.RgbaPixels))
            throw new InvalidDataException("Prepared gain-map pixels differ from the direct decoder.");
        var orientation = new ExifOrientationTransform(inputs.Primary.PixelWidth, inputs.Primary.PixelHeight, (int)inputs.Constants.Orientation.X);
        var oldGain = ThumbnailPixelConverter.ConvertGainMapToBgra8(inputs, CancellationToken.None);
        var basePreview = ThumbnailPixelConverter.ConvertGainMapBaseToBgra8(inputs, CancellationToken.None);
        await WriteBgraAsync(Path.Combine(directory, prefix + "-before.png"), orientation.Width, orientation.Height, oldGain);
        await WriteBgraAsync(Path.Combine(directory, prefix + "-after.png"), orientation.Width, orientation.Height, basePreview);

        var probe = gainDocument.Document.GainMapProbe!;
        var baseBytes = await UltraHdrGainMapDecoder.ReadSegmentAsync(gainPath, 0, checked((int)probe.PrimaryImageEndOffset!.Value), CancellationToken.None);
        var srgbBase = await BitmapDecodeService.DecodeBytesAsync(baseBytes, colorManageToSrgb: true,
            respectExifOrientation: true, maxPixelSize: 192, cancellationToken: CancellationToken.None);
        var reference = ThumbnailPixelConverter.ConvertSdrToBgra8(srgbBase, CancellationToken.None);
        await WriteBgraAsync(Path.Combine(directory, prefix + "-reference-sdr.png"), srgbBase.PixelWidth, srgbBase.PixelHeight, reference);
        var oldError = MeanRgbError(oldGain, reference);
        var baseError = MeanRgbError(basePreview, reference);
        if (baseError > 5) throw new InvalidDataException($"Gain-map base preview differs from independent WIC sRGB base by {baseError:F3}/255.");
        var changedConstants = inputs.Constants;
        changedConstants.GainMapMax *= 2;
        changedConstants.HdrCapacity *= 2;
        var changedGain = inputs with { Constants = changedConstants };
        if (!basePreview.AsSpan().SequenceEqual(ThumbnailPixelConverter.ConvertGainMapBaseToBgra8(changedGain, CancellationToken.None)))
            throw new InvalidDataException("SDR-base thumbnail depends on HDR gain capacity.");
        Console.WriteLine($"PASS {prefix}: {orientation.Width}x{orientation.Height}, EXIF {(int)inputs.Constants.Orientation.X}; mean RGB error vs embedded SDR before {oldError:F3}/255, after {baseError:F3}/255; independent of gain capacity");
    }

    private static double MeanRgbError(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) throw new InvalidDataException("Appearance comparison sizes differ.");
        long error = 0;
        for (var pixel = 0; pixel < first.Length; pixel += 4)
            for (var channel = 0; channel < 3; channel++) error += Math.Abs(first[pixel + channel] - second[pixel + channel]);
        return error / (first.Length / 4.0 * 3);
    }

    private static async Task WriteBgraAsync(string path, int width, int height, byte[] pixels)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var random = output.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static async Task WriteTransparentFixtureAsync(string path)
    {
        const int width = 384, height = 96;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = 200;
                pixels[offset + 1] = 100;
                pixels[offset + 2] = 50;
                pixels[offset + 3] = x < 128 ? (byte)0 : x < 256 ? (byte)128 : (byte)255;
            }
        }
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var random = stream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static DecodedBitmap WithOpaqueAlpha(DecodedBitmap bitmap)
    {
        var pixels = (byte[])bitmap.RgbaPixels.Clone();
        for (var offset = 0; offset < pixels.Length; offset += bitmap.BytesPerPixel)
        {
            if (bitmap.BytesPerPixel == 4) pixels[offset + 3] = 255;
            else BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 6),
                bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Float ? BitConverter.HalfToUInt16Bits((Half)1) : ushort.MaxValue);
        }
        return bitmap with { RgbaPixels = pixels };
    }

    private static async Task AddCicpAsync(string source, string destination, byte primaries, byte transfer)
    {
        var png = await File.ReadAllBytesAsync(source);
        byte[] chunk = [0, 0, 0, 4, (byte)'c', (byte)'I', (byte)'C', (byte)'P', primaries, transfer, 0, 1, 0, 0, 0, 0];
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, 8))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), ~crc);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(png.AsMemory(0, 33));
        await output.WriteAsync(chunk);
        await output.WriteAsync(png.AsMemory(33));
    }
}
