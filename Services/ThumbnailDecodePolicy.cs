using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

internal static class ThumbnailDecodePolicy
{
    public static bool RequiresPixelDecode(HdrImageDocument document) =>
        document.Format.Kind == HdrImageKind.SingleLayerHdr
        || document.HeifAvifProbe?.HasHdrTransfer == true
        || document.JxlProbe?.IsJxl == true
        || document.ExrProbe?.IsOpenExr == true
        || document.WicImageProbe?.UsesDisplayP3Primaries == true
        || document.WicImageProbe?.UsesBt2020Primaries == true
        || document.WicImageProbe?.ResolvedColorGamut is GainMapColorGamut.AdobeRgb or GainMapColorGamut.ProPhoto;
}
