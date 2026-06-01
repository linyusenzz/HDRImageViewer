namespace HdrImageViewer.Models;

public sealed record HeifAvifProbeResult(
    bool IsHeifFamily,
    string MajorBrand,
    IReadOnlyList<string> CompatibleBrands,
    int? PrimaryItemId,
    string? PrimaryItemType,
    IReadOnlyList<string> ItemTypes,
    string? ColorProfile,
    int? ColorPrimaries,
    int? TransferCharacteristics,
    int? MatrixCoefficients,
    bool? FullRange,
    int? MaxBitDepth,
    bool HasHdrTransfer,
    bool HasBt2020,
    bool HasGainMapAuxiliary,
    bool HasAppleHdrGainMapSignal,
    bool HasIsoGainMapSignal = false)
{
    public bool HasGainMapSignal => HasGainMapAuxiliary || HasAppleHdrGainMapSignal || HasIsoGainMapSignal;

    public string TransferSummary =>
        TransferCharacteristics switch
        {
            16 => "PQ / SMPTE ST 2084",
            18 => "HLG",
            13 => "sRGB",
            1 => "BT.709",
            6 => "SMPTE 170M",
            _ => TransferCharacteristics is { } value ? $"nclx transfer {value}" : "未知"
        };

    public string ColorSummary
    {
        get
        {
            if (ColorPrimaries is null && MatrixCoefficients is null && FullRange is null)
            {
                if (ColorProfile is not null && ColorProfile is not ("nclx" or "nclc"))
                {
                    var iccLabel = HasAppleHdrGainMapSignal ? "ICC (Display P3 推断)" : $"ICC ({ColorProfile})";
                    return iccLabel;
                }
                return ColorProfile is not null ? $"{ColorProfile}" : "未指定";
            }

            var primaries = ColorPrimaries switch
            {
                9 => "BT.2020",
                1 => "BT.709",
                12 => "Display P3",
                _ => ColorPrimaries is { } value ? $"primaries {value}" : "primaries 未知"
            };
            var matrix = MatrixCoefficients switch
            {
                9 => "BT.2020 non-constant",
                1 => "BT.709",
                0 => "RGB",
                _ => MatrixCoefficients is { } value ? $"matrix {value}" : "matrix 未知"
            };
            var range = FullRange is { } fullRange ? (fullRange ? "full range" : "limited range") : "range 未知";
            return $"{primaries}, {matrix}, {range}";
        }
    }

    public string DisplayStatus
    {
        get
        {
            if (!IsHeifFamily)
            {
                return "不是 HEIF/AVIF 容器。";
            }

            if (HasGainMapAuxiliary)
            {
                return "检测到 HEIF 系 gain-map，使用 D3D11 着色器进行 HDR 效果重建。";
            }

            if (HasGainMapSignal)
            {
                return "检测到 HEIF/AVIF gain-map 信号；当前未定位到可直接渲染的辅助图像，先按 HDR 候选显示。";
            }

            if (HasHdrTransfer)
            {
                return "检测到单层 HEIF/AVIF HDR；系统解码器可提供帧时使用原生 HDR 表面预览。";
            }

            return "检测到 HEIF/AVIF 容器；安装 Windows 编解码器时使用 WIC 解码预览。";
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (!IsHeifFamily)
            {
                return "无";
            }

            var brands = CompatibleBrands.Count > 0 ? string.Join(", ", CompatibleBrands) : "无";
            var itemTypes = ItemTypes.Count > 0 ? string.Join(", ", ItemTypes.Distinct(StringComparer.OrdinalIgnoreCase)) : "未知";
            var bitDepth = MaxBitDepth is { } depth ? $"{depth}-bit" : "位深未知";
            var color = ColorProfile is not null ? $"{ColorProfile}; {ColorSummary}" : ColorSummary;
            var gainMap = HasGainMapSignal
                ? $"; gain-map signal {(HasGainMapAuxiliary ? "auxiliary" : HasIsoGainMapSignal ? "tmap" : "metadata")}"
                : string.Empty;
            return $"brand {MajorBrand}; compatible {brands}; primary {PrimaryItemType ?? "未知"}#{PrimaryItemId?.ToString() ?? "?"}; items {itemTypes}; {TransferSummary}; {color}; {bitDepth}{gainMap}";
        }
    }
}
