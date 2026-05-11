namespace HdrImageViewer.Rendering;

public sealed record HdrDisplayConfiguration(
    string AdvancedColorKind,
    bool IsHighDynamicRange,
    bool IsHighDynamicRangeAvailable,
    double SdrWhiteLevelInNits,
    double MaxLuminanceInNits,
    double MaxAverageFullFrameLuminanceInNits,
    string Details)
{
    private const double NominalReferenceWhiteInNits = 80.0;

    public static HdrDisplayConfiguration Unknown { get; } = new(
        "Unknown",
        false,
        false,
        NominalReferenceWhiteInNits,
        0.0,
        0.0,
        "Display HDR state unavailable");

    public float SceneToSdrWhiteScale =>
        IsHighDynamicRange
            ? (float)Math.Clamp(SdrWhiteLevelInNits / NominalReferenceWhiteInNits, 1.0, 12.5)
            : 1.0f;

    public bool HasReliablePeakLuminance =>
        IsHighDynamicRange && MaxLuminanceInNits > SdrWhiteLevelInNits && SdrWhiteLevelInNits > 0.0;

    public bool ReportedPeakLooksLikeFullFrame =>
        IsHighDynamicRange
        && MaxLuminanceInNits > 0.0
        && MaxAverageFullFrameLuminanceInNits > 0.0
        && Math.Abs(MaxLuminanceInNits - MaxAverageFullFrameLuminanceInNits) <= Math.Max(1.0, MaxLuminanceInNits * 0.02);

    public float MaxSceneValue =>
        HasReliablePeakLuminance
            ? (float)Math.Clamp(MaxLuminanceInNits / NominalReferenceWhiteInNits, 1.0, 128.0)
            : 0.0f;

    public float MaxFullFrameSceneValue =>
        IsHighDynamicRange && MaxAverageFullFrameLuminanceInNits > 0.0
            ? (float)Math.Clamp(MaxAverageFullFrameLuminanceInNits / NominalReferenceWhiteInNits, 1.0, 128.0)
            : 0.0f;

    public float MaxDisplayBoostLog2 =>
        HasReliablePeakLuminance
            ? (float)Math.Clamp(Math.Log2(MaxLuminanceInNits / SdrWhiteLevelInNits), 0.0, 16.0)
            : 0.0f;

    public string RenderSummary =>
        string.Equals(AdvancedColorKind, "Unknown", StringComparison.Ordinal)
            ? Details
            : IsHighDynamicRange && HasReliablePeakLuminance
            ? $"Display HDR active; SDR white {SdrWhiteLevelInNits:0} nits; peak {MaxLuminanceInNits:0} nits; full-frame {MaxAverageFullFrameLuminanceInNits:0} nits; capacity {MaxDisplayBoostLog2:0.###} stops ({Math.Pow(2.0, MaxDisplayBoostLog2):0.##}x); {Details}"
            : IsHighDynamicRange
            ? $"Display HDR active; SDR white {SdrWhiteLevelInNits:0} nits; reported peak {MaxLuminanceInNits:0} nits, full-frame {MaxAverageFullFrameLuminanceInNits:0} nits; auto peak not usable; capacity 0 stops; {Details}"
            : IsHighDynamicRangeAvailable
                ? $"Display {AdvancedColorKind}; HDR available but not active"
                : $"Display {AdvancedColorKind}; HDR not active";
}
