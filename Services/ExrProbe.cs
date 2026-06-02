using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class ExrProbe
{
    public static bool IsExrExtension(string extension)
    {
        return string.Equals(extension, ".exr", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<ExrProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsExrExtension(Path.GetExtension(path)))
        {
            return Task.FromResult<ExrProbeResult?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!NativeExrDecoder.IsAvailable)
            {
                return Task.FromResult<ExrProbeResult?>(new ExrProbeResult(
                    true,
                    null,
                    null,
                    "HdrImageViewer.Native OpenEXR",
                    "OpenEXR native backend unavailable"));
            }

            var metadata = NativeExrDecoder.ProbeHeader(path);
            return Task.FromResult<ExrProbeResult?>(new ExrProbeResult(
                true,
                metadata?.PixelWidth,
                metadata?.PixelHeight,
                metadata?.DecoderName ?? "HdrImageViewer.Native OpenEXR",
                metadata is null ? "OpenEXR header probe failed" : "OpenEXR native decode available",
                metadata?.UsesBt2020Primaries == true,
                metadata?.UsesProPhotoPrimaries == true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<ExrProbeResult?>(new ExrProbeResult(
                true,
                null,
                null,
                "HdrImageViewer.Native OpenEXR",
                $"OpenEXR 探测失败: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
