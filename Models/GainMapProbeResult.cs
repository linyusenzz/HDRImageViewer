namespace HdrImageViewer.Models;

public sealed record GainMapProbeResult(
    bool IsJpeg,
    bool HasUltraHdrSignal,
    bool HasGContainerDirectory,
    bool HasGainMapItem,
    bool HasGainMapImage,
    bool HasIso21496Signal,
    bool HasAppleHdrGainMapSignal,
    int? ExifOrientation,
    bool HasPrimaryIccProfile,
    long? PrimaryImageEndOffset,
    long? GainMapOffset,
    int? GainMapLength,
    GainMapMetadata? Metadata)
{
    public bool IsRenderableUltraHdr =>
        IsJpeg
        && (HasUltraHdrSignal || HasAppleHdrGainMapSignal)
        && HasGainMapImage
        && Metadata?.HasRequiredRenderingFields == true;

    public string DisplayStatus
    {
        get
        {
            if (!IsJpeg)
            {
                return "不是 JPEG 文件。";
            }

            if (IsRenderableUltraHdr)
            {
                return HasAppleHdrGainMapSignal && !HasUltraHdrSignal
                    ? "已定位 Apple HDRGainMap；shader 重建元数据可用。"
                    : "已定位 Ultra HDR gain map；shader 重建元数据可用。";
            }

            if (HasIso21496Signal && !HasUltraHdrSignal)
            {
                return "检测到 ISO 21496-1 gain-map 元数据；需要补齐 ISO 元数据解析后才能重建 HDR。";
            }

            if (HasAppleHdrGainMapSignal && !HasGainMapImage)
            {
                return "找到 Apple HDRGainMap 元数据，但未定位 gain map 图像。";
            }

            if (HasUltraHdrSignal && !HasGainMapImage)
            {
                return "找到 Ultra HDR 元数据，但未定位 gain map 图像。";
            }

            if (HasGainMapImage && Metadata?.HasRequiredRenderingFields != true)
            {
                return "已定位 gain map 图像，但缺少必要的渲染元数据。";
            }

            return "未发现 JPEG gain map 信号。";
        }
    }
}
