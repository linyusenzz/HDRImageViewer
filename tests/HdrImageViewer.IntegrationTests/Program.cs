using System.Numerics;
using System.Buffers.Binary;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Windows.Graphics.Imaging;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Explicit integration runner: accepts a synthetic HDR PNG and an isolated output directory,
// or --localization to run Windows MRT and localization regression checks.
if (args.Length >= 1 && args[0] is "--localization" or "--mrt")
{
    await HdrImageViewer.IntegrationTests.MrtLocalizationRegressionChecks.RunAsync();
    return;
}

if (args.Length >= 2 && args[0] == "--probe-resource")
{
    var key = args[1];
    if (args.Length >= 3 && args[2] != "--use-settings")
    {
        Localization.ApplyLanguagePreference(args[2]);
    }
    else
    {
        Localization.ApplyLanguagePreference(AppSettingsService.Current.Language);
    }

    var text = Localization.GetString(key);
    var applied = Localization.GetAppliedLanguageOverride();
    Console.WriteLine($"OVERRIDE={applied}");
    Console.WriteLine($"TEXT={text}");
    return;
}

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--codecs" or "--thumbnails")))
    throw new ArgumentException("Usage: <HDR PNG fixture> <test output directory> [--codecs|--thumbnails] OR --localization");
var source = Path.GetFullPath(args[0]);
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
if (args.Length == 3 && args[2] == "--thumbnails")
{
    await ThumbnailRegressionChecks.RunAsync(source, directory);
    return;
}
var broken = Path.Combine(directory, "broken.png");
await File.WriteAllTextAsync(broken, "invalid integration fixture");
var queue = new BatchExportController();
queue.Add([broken, source]);
await queue.RunAsync((item, token) => BatchImageExportService.ExportAsync(item, directory, 0, token), CancellationToken.None);
if (queue.Items[0].State != BatchExportState.Failed || queue.Items[1].State != BatchExportState.Completed)
    throw new InvalidOperationException(string.Join("\n", queue.Items.Select(item => item.Detail)));
var png = Directory.GetFiles(directory, "*-export.png").Single();
await using (var stream = File.OpenRead(png))
{
    using var random = stream.AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(random);
    if (decoder.PixelWidth != 640 || decoder.PixelHeight != 320) throw new InvalidDataException("SDR export dimensions changed.");
    var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
    if (pixels.Length == 0 || pixels.Distinct().Count() < 4) throw new InvalidDataException("Export has no useful pixel data.");
}
var hdr = await BatchImageExportService.ExportAsync(new BatchExportItem(source), directory, 3, CancellationToken.None);
await using var hdrStream = File.OpenRead(hdr);
var hdrProbe = await PngColorMetadataReader.ReadAsync(hdrStream);
if (hdrProbe?.CicpTransfer != 16 || hdrProbe.CicpPrimaries != 9 || hdrProbe.BitsPerChannel != 16)
    throw new InvalidDataException("HDR PQ metadata was not preserved.");
Console.WriteLine($"SDR queue verified: failed file did not stop the valid file; HDR exported to {hdr}");
Console.WriteLine($"PNG metadata: {hdrProbe}");

if (args.Length == 3)
{
    var loaded = await ImageDocumentLoader.LoadAsync(source);
    foreach (var extension in new[] { ".avif", ".heic", ".jxl", ".exr" })
    {
        foreach (var transfer in new[] { SingleLayerHdrExportTransfer.Pq, SingleLayerHdrExportTransfer.Hlg })
        {
            if (extension == ".exr" && transfer == SingleLayerHdrExportTransfer.Hlg) continue;
            var output = Path.Combine(directory, $"codec-{transfer}{extension}");
            await SingleLayerHdrExportService.ExportAsync(loaded.Document, output, transfer);
            var reopened = await ImageDocumentLoader.LoadAsync(output);
            var pixels = await BitmapDecodeService.DecodeDocumentAsync(reopened.Document);
            if (pixels.PixelWidth != 640 || pixels.PixelHeight != 320 || !pixels.IsHdrEncoded || pixels.RgbaPixels.Distinct().Count() < 4)
                throw new InvalidDataException($"Invalid HDR round trip: {output}: {pixels.RenderEncodingSummary}");
            var thumbnail = await BitmapDecodeService.DecodeDocumentForThumbnailAsync(reopened.Document, 128);
            if (thumbnail.PixelWidth != 128 || thumbnail.PixelHeight != 64)
                throw new InvalidDataException($"Thumbnail size limit ignored: {output}: {thumbnail.PixelWidth}x{thumbnail.PixelHeight}");
            if (extension is ".heic" or ".avif")
            {
                var probe = reopened.Document.HeifAvifProbe;
                if (probe?.TransferCharacteristics != (transfer == SingleLayerHdrExportTransfer.Pq ? 16 : 18)
                    || probe.ColorPrimaries != 9 || probe.MaxBitDepth < 10
                    || !pixels.DecoderName.StartsWith("libheif in-process", StringComparison.Ordinal))
                    throw new InvalidDataException($"HEIF/AVIF metadata or native decode regression: {output}: {probe}; {pixels.DecoderName}");
            }
            if (extension == ".jxl" && (!pixels.DecoderName.StartsWith("libjxl in-process", StringComparison.Ordinal)
                || reopened.Document.JxlProbe?.TransferSummary?.Contains(transfer == SingleLayerHdrExportTransfer.Pq ? "PQ" : "HLG", StringComparison.Ordinal) != true))
                throw new InvalidDataException($"JXL probe or in-process decode regression: {output}");
            Console.WriteLine($"PASS {Path.GetFileName(output)}: {pixels.RenderEncodingSummary}");
        }
    }

    foreach (var channelMode in Enum.GetValues<UltraHdrGainMapChannelMode>())
    {
        var output = Path.Combine(directory, $"ultrahdr-{channelMode}.jpg");
        await GainMapHdrExportService.ExportJpegUltraHdrAsync(loaded.Document, output,
            UltraHdrExportOptions.Default with { GainMapChannelMode = channelMode });
        var reopened = await ImageDocumentLoader.LoadAsync(output);
        if (reopened.Document.GainMapProbe is not { IsRenderableUltraHdr: true, HasIso21496Signal: true, HasUltraHdrSignal: true })
            throw new InvalidDataException("Ultra HDR XMP/ISO metadata was lost.");
        var inputs = await UltraHdrGainMapDecoder.DecodeRenderInputsAsync(reopened.Document);
        if (inputs.Primary.PixelWidth != 640 || inputs.GainMap.RgbaPixels.Length == 0)
            throw new InvalidDataException("Ultra HDR base/gain map decode failed.");
        Console.WriteLine($"PASS Ultra HDR {channelMode}: base + gain map + XMP + ISO");
    }
    await ExportRegressionChecks.RunAsync(Path.Combine(directory, "ultrahdr-Rgb.jpg"), directory);
    foreach (var extension in new[] { ".heic", ".avif" })
    {
        foreach (var channelMode in Enum.GetValues<UltraHdrGainMapChannelMode>())
        {
            var output = Path.Combine(directory, $"gainmap-{channelMode}{extension}");
            await GainMapHdrExportService.ExportAsync(loaded.Document, output,
                new UltraHdrExportOptions(channelMode, UltraHdrSdrBaseColorGamut.DisplayP3));
            var reopened = await ImageDocumentLoader.LoadAsync(output);
            var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(reopened.Document);
            if (reopened.Document.HeifAvifProbe is not { ColorPrimaries: 12, TransferCharacteristics: 13 }
                || inputs.Constants.GainMapControl.Z != 1 || inputs.Constants.SourceEncoding.X != 0)
                throw new InvalidDataException("Gain Map SDR base must be Display P3 with sRGB transfer.");
            if (!reopened.Document.HasRenderableGainMap || inputs.Primary.PixelWidth != 640
                || inputs.Primary.PixelHeight != 320 || inputs.GainMap.RgbaPixels.Distinct().Count() < 4
                || inputs.Constants.HdrCapacity.Y <= 0)
                throw new InvalidDataException($"Invalid {extension} Gain Map round trip.");
            var preview = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(reopened.Document, maxPixelSize: 128);
            if (preview.Primary.PixelWidth != 128 || preview.Primary.PixelHeight != 64
                || preview.GainMap.PixelWidth != 128 || preview.GainMap.PixelHeight != 64)
                throw new InvalidDataException("Gain Map preview did not respect the requested size.");
            await VerifyReconstructionAsync(output, inputs);
            Console.WriteLine($"PASS {extension} Gain Map {channelMode}: ISO metadata, SDR base, gain map; capacity {inputs.Constants.HdrCapacity}");
        }
        var crop = Path.Combine(directory, "gainmap-crop" + extension);
        await GainMapHdrExportService.ExportAsync(loaded.Document,
            new BitmapBounds { X = 32, Y = 24, Width = 257, Height = 129 }, crop);
        var cropDoc = await ImageDocumentLoader.LoadAsync(crop);
        var cropInputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(cropDoc.Document);
        if (cropInputs.Primary.PixelWidth != 257 || cropInputs.Primary.PixelHeight != 129)
            throw new InvalidDataException("Gain Map odd-size crop dimensions changed.");
        Console.WriteLine($"PASS {extension} Gain Map crop 257x129");
        var batch = await BatchImageExportService.ExportAsync(new BatchExportItem(source), directory,
            extension == ".heic" ? 8 : 9, CancellationToken.None);
        if (!File.Exists(batch)) throw new InvalidDataException("Batch Gain Map output missing.");
        Console.WriteLine($"PASS {extension} Gain Map batch");
    }
    var reexportSource = await ImageDocumentLoader.LoadAsync(Path.Combine(directory, "gainmap-Rgb.heic"));
    var reexportPath = Path.Combine(directory, "gainmap-reexport.avif");
    await GainMapHdrExportService.ExportAsync(reexportSource.Document, reexportPath);
    var reexport = await ImageDocumentLoader.LoadAsync(reexportPath);
    if (!reexport.Document.HasRenderableGainMap) throw new InvalidDataException("Gain Map source re-export failed.");
    Console.WriteLine("PASS HEIC Gain Map source re-export to AVIF Gain Map");
    var protectedOutput = Path.Combine(directory, "protected.heic");
    await File.WriteAllTextAsync(protectedOutput, "existing file");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try
        {
            await GainMapHdrExportService.ExportAsync(loaded.Document, protectedOutput, cancellationToken: canceled.Token);
            throw new InvalidDataException("Canceled export unexpectedly completed.");
        }
        catch (OperationCanceledException) { }
    }
    try
    {
        await GainMapHdrExportService.ExportAsync(loaded.Document,
            new BitmapBounds { X = 639, Y = 0, Width = 16, Height = 16 }, protectedOutput);
        throw new InvalidDataException("Invalid crop unexpectedly completed.");
    }
    catch (InvalidOperationException) { }
    if (await File.ReadAllTextAsync(protectedOutput) != "existing file")
        throw new InvalidDataException("Failed/canceled export changed the existing destination.");
    Console.WriteLine("PASS Gain Map cancellation/failure preserves existing output");
    if (Environment.GetEnvironmentVariable("HDRVIEWER_AVIF_GAINMAP_FIXTURE") is { Length: > 0 } gainMapFixture)
    {
        var gainMapDocument = await ImageDocumentLoader.LoadAsync(gainMapFixture);
        if (gainMapDocument.Document.HeifAvifProbe?.HasIsoGainMapSignal != true)
            throw new InvalidDataException("Expected an AVIF ISO gain-map fixture.");
        var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(gainMapDocument.Document);
        if (inputs.Primary.RgbaPixels.Length == 0 || inputs.GainMap.RgbaPixels.Length == 0)
            throw new InvalidDataException("AVIF gain-map extraction produced empty images.");
        Console.WriteLine($"PASS AVIF ISO gain map: base {inputs.Primary.PixelWidth}x{inputs.Primary.PixelHeight}, gain {inputs.GainMap.PixelWidth}x{inputs.GainMap.PixelHeight}");
    }
}

static async Task VerifyReconstructionAsync(string path, GainMapRenderInputs inputs)
{
    var rawPath = path + ".reference.raw";
    using var process = NativeProcessRunner.Create(NativeToolLocator.FindTool("ultrahdr_app.exe")!);
    foreach (var arg in new[] { "-m", "1", "-j", path, "-o", "0", "-O", "4", "-z", rawPath })
        process.StartInfo.ArgumentList.Add(arg);
    await NativeProcessRunner.RunAsync(process, "Ultra HDR reference decode", CancellationToken.None);
    var reference = await File.ReadAllBytesAsync(rawPath);
    if (reference.Length != inputs.Primary.PixelWidth * inputs.Primary.PixelHeight * 8)
        throw new InvalidDataException("Reference decoder dimensions changed.");
    double error = 0, energy = 0;
    var samples = 0;
    for (var y = 8; y < inputs.Primary.PixelHeight - 8; y += 13)
    for (var x = 8; x < inputs.Primary.PixelWidth - 8; x += 13)
    {
        var sdr = HdrColorMath.DecodeGainMapBaseToLinear(ReadRgb(inputs.Primary, x, y), inputs.Constants);
        var gain = ReadRgb(inputs.GainMap, x * inputs.GainMap.PixelWidth / inputs.Primary.PixelWidth,
            y * inputs.GainMap.PixelHeight / inputs.Primary.PixelHeight);
        var reconstructed = HdrColorMath.ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, 1);
        reconstructed = Vector3.Max(Vector3.Zero, HdrColorMath.ConvertGainMapBaseToBt2020(reconstructed, inputs.Constants));
        var offset = (y * inputs.Primary.PixelWidth + x) * 8;
        var expected = new Vector3(ReadHalf(offset), ReadHalf(offset + 2), ReadHalf(offset + 4));
        if (!float.IsFinite(reconstructed.LengthSquared()) || !float.IsFinite(expected.LengthSquared()))
            throw new InvalidDataException("Non-finite HDR reconstruction.");
        error += Vector3.DistanceSquared(reconstructed, expected);
        energy += expected.LengthSquared();
        samples++;
    }
    var relativeRms = Math.Sqrt(error / Math.Max(energy, 1e-12));
    if (relativeRms > 0.025) throw new InvalidDataException($"HDR reconstruction differs from libultrahdr: {relativeRms:P2}");
    Console.WriteLine($"PASS {Path.GetFileName(path)} HDR reconstruction vs libultrahdr: {relativeRms:P3}, {samples} samples");
    float ReadHalf(int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(reference.AsSpan(offset, 2)));
}

static Vector3 ReadRgb(DecodedBitmap image, int x, int y)
{
    var offset = (y * image.PixelWidth + x) * image.BytesPerPixel;
    return image.BytesPerPixel == 4
        ? new Vector3(image.RgbaPixels[offset], image.RgbaPixels[offset + 1], image.RgbaPixels[offset + 2]) / 255f
        : new Vector3(Read16(offset), Read16(offset + 2), Read16(offset + 4)) / 65535f;
    ushort Read16(int index) => BinaryPrimitives.ReadUInt16LittleEndian(image.RgbaPixels.AsSpan(index, 2));
}
