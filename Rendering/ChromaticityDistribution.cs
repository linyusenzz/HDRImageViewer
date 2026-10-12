using System.Numerics;

namespace HdrImageViewer.Rendering;

/// <summary>Analysis-only classification; never clips or changes the captured RGB.</summary>
public sealed class ChromaticityDistribution
{
    public int[] PhysicalBins { get; } = new int[ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight];
    public int[] NonPhysicalBins { get; } = new int[ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight];
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
        if (LuminanceSnapshot.ToNits(sample) < MinimumNits)
        {
            DarkCount++;
            return;
        }
        if (ChromaticityDiagram.FromScRgb(sample) is not { } xy)
        {
            InvalidCount++;
            return;
        }
        var bin = ChromaticityDiagram.Bin(xy);
        if (ChromaticityDiagram.IsPhysical(xy))
        {
            PhysicalCount++;
            PhysicalBins[bin]++;
        }
        else
        {
            NonPhysicalCount++;
            if (bin >= 0) NonPhysicalBins[bin]++;
            else NonPhysicalOutsidePlotCount++;
        }
    }
}
