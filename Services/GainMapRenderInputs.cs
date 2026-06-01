using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

public sealed record GainMapRenderInputs(
    DecodedBitmap Primary,
    DecodedBitmap GainMap,
    GainMapShaderConstants Constants)
{
    public long ApproximateByteCount => Primary.ApproximateByteCount + GainMap.ApproximateByteCount;
}
