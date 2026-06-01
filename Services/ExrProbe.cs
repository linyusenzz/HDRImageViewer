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
            var bitmap = NativeExrDecoder.Decode(path);
            return Task.FromResult<ExrProbeResult?>(new ExrProbeResult(
                true,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                bitmap.DecoderName,
                "OpenEXR native decode available",
                bitmap.UsesBt2020Primaries));
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
