using System.Numerics;

namespace HdrImageViewer.Rendering;

/// <summary>Analysis-only classification; never clips or changes the captured RGB.</summary>
public sealed class ChromaticityDistribution
{
    internal const int TotalBinCount = ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight;

    public int[] PhysicalBins { get; } = new int[TotalBinCount];
    public int[] NonPhysicalBins { get; } = new int[TotalBinCount];
    public float MinimumNits { get; }
    public int PhysicalCount { get; private set; }
    public int NonPhysicalCount { get; private set; }
    public int NonPhysicalOutsidePlotCount { get; private set; }
    public int DarkCount { get; private set; }
    public int InvalidCount { get; private set; }
    public int EligibleCount => PhysicalCount + NonPhysicalCount;
    public double NonPhysicalFraction => EligibleCount == 0 ? 0 : (double)NonPhysicalCount / EligibleCount;

    public ChromaticityDistribution(float minimumNits = 0)
    {
        if (!float.IsFinite(minimumNits) || minimumNits < 0) throw new ArgumentOutOfRangeException(nameof(minimumNits));
        MinimumNits = minimumNits;
    }

    public void Add(Vector3 sample)
    {
        if (!float.IsFinite(sample.X) || !float.IsFinite(sample.Y) || !float.IsFinite(sample.Z))
        {
            InvalidCount++;
            return;
        }

        var nits = LuminanceSnapshot.ToNits(sample);
        var physicalCount = PhysicalCount;
        var nonPhysicalCount = NonPhysicalCount;
        var nonPhysicalOutsidePlotCount = NonPhysicalOutsidePlotCount;
        var darkCount = DarkCount;
        var invalidCount = InvalidCount;
        AccumulateFiniteSample(
            sample,
            nits,
            MinimumNits,
            ChromaticityDiagram.GetLocusLookup(),
            PhysicalBins,
            NonPhysicalBins,
            ref physicalCount,
            ref nonPhysicalCount,
            ref nonPhysicalOutsidePlotCount,
            ref darkCount,
            ref invalidCount);
        PhysicalCount = physicalCount;
        NonPhysicalCount = nonPhysicalCount;
        NonPhysicalOutsidePlotCount = nonPhysicalOutsidePlotCount;
        DarkCount = darkCount;
        InvalidCount = invalidCount;
    }

    internal static void AccumulateFiniteSample(
        Vector3 sample,
        float nits,
        float minimumNits,
        ChromaticityDiagram.LocusLookupTable lookup,
        int[] physicalBins,
        int[] nonPhysicalBins,
        ref int physicalCount,
        ref int nonPhysicalCount,
        ref int nonPhysicalOutsidePlotCount,
        ref int darkCount,
        ref int invalidCount)
    {
        if (nits < minimumNits)
        {
            darkCount++;
            return;
        }

        if (!ChromaticityDiagram.TryFromScRgbFinite(sample, out var xy))
        {
            invalidCount++;
            return;
        }

        if (ChromaticityDiagram.ClassifyFinite(xy, lookup, out var bin))
        {
            physicalCount++;
            physicalBins[bin]++;
        }
        else
        {
            nonPhysicalCount++;
            if (bin >= 0) nonPhysicalBins[bin]++;
            else nonPhysicalOutsidePlotCount++;
        }
    }

    internal void Merge(
        ReadOnlySpan<int> physicalBins,
        ReadOnlySpan<int> nonPhysicalBins,
        int physicalCount,
        int nonPhysicalCount,
        int nonPhysicalOutsidePlotCount,
        int darkCount,
        int invalidCount)
    {
        for (var i = 0; i < TotalBinCount; i++)
        {
            PhysicalBins[i] += physicalBins[i];
            NonPhysicalBins[i] += nonPhysicalBins[i];
        }

        AddCounts(physicalCount, nonPhysicalCount, nonPhysicalOutsidePlotCount, darkCount, invalidCount);
    }

    internal void AddCounts(
        int physicalCount,
        int nonPhysicalCount,
        int nonPhysicalOutsidePlotCount,
        int darkCount,
        int invalidCount)
    {
        PhysicalCount += physicalCount;
        NonPhysicalCount += nonPhysicalCount;
        NonPhysicalOutsidePlotCount += nonPhysicalOutsidePlotCount;
        DarkCount += darkCount;
        InvalidCount += invalidCount;
    }
}
