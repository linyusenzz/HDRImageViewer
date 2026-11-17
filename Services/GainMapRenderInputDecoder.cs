using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

internal static class GainMapRenderInputDecoder
{
    public static bool CanDecode(HdrImageDocument document)
    {
        return document.GainMapProbe?.IsRenderableUltraHdr == true
            || document.HeifAvifProbe is { IsHeifFamily: true } heifProbe
                && (heifProbe.HasGainMapAuxiliary || heifProbe.HasIsoGainMapSignal)
            || document.JxlProbe is { IsJxl: true, HasGainMapBox: true };
    }

    public static Task<GainMapBaseThumbnailInput> DecodeBaseForThumbnailAsync(
        HdrImageDocument document,
        int maxPixelSize,
        CancellationToken cancellationToken = default)
    {
        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            return UltraHdrGainMapDecoder.DecodeBaseForThumbnailAsync(document, maxPixelSize, cancellationToken);
        }

        if (document.HeifAvifProbe is { IsHeifFamily: true } heifProbe
            && (heifProbe.HasGainMapAuxiliary || heifProbe.HasIsoGainMapSignal))
        {
            return HeifGainMapDecoder.DecodeBaseForThumbnailAsync(document, maxPixelSize, cancellationToken);
        }

        if (document.JxlProbe is { IsJxl: true, HasGainMapBox: true })
        {
            return JxlGainMapDecoder.DecodeBaseForThumbnailAsync(document, maxPixelSize, cancellationToken);
        }

        throw new InvalidOperationException("The selected document does not contain a renderable gain map.");
    }

    public static Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        int? maxPixelSize = null,
        CancellationToken cancellationToken = default)
    {
        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            return UltraHdrGainMapDecoder.DecodeRenderInputsAsync(document, maxPixelSize, cancellationToken);
        }

        if (document.HeifAvifProbe is { IsHeifFamily: true } heifProbe
            && (heifProbe.HasGainMapAuxiliary || heifProbe.HasIsoGainMapSignal))
        {
            return HeifGainMapDecoder.DecodeRenderInputsAsync(document, maxPixelSize, cancellationToken);
        }

        if (document.JxlProbe is { IsJxl: true, HasGainMapBox: true })
        {
            return JxlGainMapDecoder.DecodeRenderInputsAsync(document, maxPixelSize, cancellationToken);
        }

        throw new InvalidOperationException("The selected document does not contain a renderable gain map.");
    }

    public static Task<GainMapRenderInputs> DecodeRenderInputsAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        return DecodeRenderInputsAsync(document, maxPixelSize: null, cancellationToken);
    }
}
