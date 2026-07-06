using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

internal static class AvifGainMapDecoder
{
    public static async Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.HeifAvifProbe is not { IsHeifFamily: true, HasIsoGainMapSignal: true })
        {
            throw new InvalidOperationException("The selected document does not contain an AVIF ISO gain map.");
        }

        var tool = NativeToolLocator.FindTool("avifgainmaputil.exe")
            ?? throw new InvalidOperationException("未找到 avifgainmaputil.exe，无法提取 AVIF gain-map。请把 libavif 工具放到 external\\encoders\\x64。");
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "avif-gainmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var gainPath = Path.Combine(tempDir, "gain.png");

        try
        {
            using var metadataProcess = NativeProcessRunner.Create(tool);
            metadataProcess.StartInfo.ArgumentList.Add("printmetadata");
            metadataProcess.StartInfo.ArgumentList.Add(document.Path);
            var metadataOutput = await NativeProcessRunner.RunAsync(metadataProcess, "libavif avifgainmaputil printmetadata", cancellationToken);
            var metadata = IsoGainMapMetadataParser.ParseAvifGainMapUtilOutput(metadataOutput);

            using var extractProcess = NativeProcessRunner.Create(tool);
            extractProcess.StartInfo.ArgumentList.Add("extractgainmap");
            extractProcess.StartInfo.ArgumentList.Add(document.Path);
            extractProcess.StartInfo.ArgumentList.Add(gainPath);
            extractProcess.StartInfo.ArgumentList.Add("-q");
            extractProcess.StartInfo.ArgumentList.Add("100");
            await NativeProcessRunner.RunAsync(extractProcess, "libavif avifgainmaputil extractgainmap", cancellationToken);

            var primaryGamut = ResolvePrimaryColorGamut(document.HeifAvifProbe);
            var primaryColorManageToSrgb = primaryGamut is GainMapColorGamut.Unknown or GainMapColorGamut.Bt709;
            var primary = await BitmapDecodeService.DecodeFileSdrWithWindowsImagingAsync(
                document.Path,
                primaryColorManageToSrgb,
                respectExifOrientation: false,
                decoderName: "Windows Imaging AVIF gain-map base",
                maxPixelSize: maxPixelSize,
                colorGamut: primaryGamut,
                cancellationToken: cancellationToken);
            primary = primary with
            {
                DecoderName = $"Windows Imaging AVIF gain-map base ({(primaryColorManageToSrgb ? "ColorManageToSrgb" : "DoNotColorManage")})",
            };
            var gainMap = await BitmapDecodeService.DecodeFileRawRgba16Async(
                gainPath,
                "libavif avifgainmaputil gain map",
                maxPixelSize,
                cancellationToken);
            return new GainMapRenderInputs(
                primary,
                gainMap,
                metadata.CreateConstants(primaryGamut, ResolvePrimaryTransfer(document.HeifAvifProbe)));
        }
        finally
        {
            TryDeleteFile(gainPath);
            TryDeleteDirectory(tempDir);
        }
    }

    private static GainMapColorGamut ResolvePrimaryColorGamut(HeifAvifProbeResult probe)
    {
        return probe.ColorPrimaries switch
        {
            9 => GainMapColorGamut.Bt2100,
            12 => GainMapColorGamut.DisplayP3,
            1 => GainMapColorGamut.Bt709,
            _ => GainMapColorGamut.Bt709,
        };
    }

    private static float ResolvePrimaryTransfer(HeifAvifProbeResult probe)
    {
        return probe.TransferCharacteristics is 1 or 6
            ? HdrColorMath.GainMapBaseTransferBt709
            : HdrColorMath.GainMapBaseTransferSrgb;
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
