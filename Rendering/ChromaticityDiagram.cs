using System.Globalization;
using System.Numerics;

namespace HdrImageViewer.Rendering;

public static class ChromaticityDiagram
{
    public const int BinWidth = 320;
    public const int BinHeight = 360;
    public const float MaxX = 0.8f;
    public const float MaxY = 0.9f;
    private const int SubGridSize = 8;

    public static Vector2 WhitePoint => new(0.3127f, 0.3290f);
    public static IReadOnlyList<Vector2> Srgb { get; } = new[] { new Vector2(.64f, .33f), new Vector2(.30f, .60f), new Vector2(.15f, .06f) };
    public static IReadOnlyList<Vector2> DisplayP3 { get; } = new[] { new Vector2(.68f, .32f), new Vector2(.265f, .69f), new Vector2(.15f, .06f) };
    public static IReadOnlyList<Vector2> Bt2020 { get; } = new[] { new Vector2(.708f, .292f), new Vector2(.170f, .797f), new Vector2(.131f, .046f) };
    private static readonly Lazy<Vector2[]> Locus = new(ReadSpectralLocus);
    private static readonly Lazy<LocusLookupTable> LocusLookup = new(CreateLocusLookup);

    // Half a density bin. This absorbs FP16 rounding and the conventional P3-red
    // primary's tiny distance from the tabulated locus, not visible excursions.
    public const float LocusTolerance = MaxX / BinWidth / 2;
    public static IReadOnlyList<Vector2> SpectralLocus => Locus.Value;

    // Linear scRGB has sRGB/BT.709 primaries and a D65 white. Do not clamp RGB:
    // negative components are required to retain colors outside the sRGB triangle.
    // Matrix derived from the ICC sRGB primaries and D65 white coordinates.
    public static Vector2? FromScRgb(Vector3 rgb)
    {
        if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) return null;
        return TryFromScRgbFinite(rgb, out var xy) ? xy : null;
    }

    internal static bool TryFromScRgbFinite(Vector3 rgb, out Vector2 xy)
    {
        var x = (0.4123907992659595 * rgb.X) + (0.3575843393838780 * rgb.Y) + (0.1804807884018343 * rgb.Z);
        var y = (0.2126390058715104 * rgb.X) + (0.7151686787677559 * rgb.Y) + (0.0721923153607337 * rgb.Z);
        var z = (0.0193308187155919 * rgb.X) + (0.1191947797946259 * rgb.Y) + (0.9505321522496606 * rgb.Z);
        var sum = x + y + z;
        if (sum <= 1e-10 || y <= 1e-10)
        {
            xy = default;
            return false;
        }

        xy = new Vector2((float)(x / sum), (float)(y / sum));
        return true;
    }

    public static bool IsPhysical(Vector2 xy) => Classify(xy, out _);

    internal static bool Classify(Vector2 xy, out int bin)
    {
        if (!float.IsFinite(xy.X) || !float.IsFinite(xy.Y))
        {
            bin = -1;
            return false;
        }

        return ClassifyFinite(xy, LocusLookup.Value, out bin);
    }

    internal static LocusLookupTable GetLocusLookup() => LocusLookup.Value;

    internal static bool ClassifyFinite(Vector2 xy, LocusLookupTable lookup, out int bin)
    {
        if (xy.X < 0 || xy.X > MaxX || xy.Y < 0 || xy.Y > MaxY)
        {
            bin = -1;
            return false;
        }

        var scaledX = xy.X / MaxX * BinWidth;
        var scaledY = xy.Y / MaxY * BinHeight;
        var bx = Math.Min(BinWidth - 1, (int)scaledX);
        var by = Math.Min(BinHeight - 1, (int)scaledY);
        bin = (by * BinWidth) + bx;

        var coarse = lookup.CoarseCells[bin];
        if (coarse != 0)
        {
            return coarse > 0;
        }

        var subOffset = lookup.SubGridOffsets[bin];
        if (subOffset >= 0)
        {
            var sx = Math.Clamp((int)((scaledX - bx) * SubGridSize), 0, SubGridSize - 1);
            var sy = Math.Clamp((int)((scaledY - by) * SubGridSize), 0, SubGridSize - 1);
            var subCell = lookup.SubCells[subOffset + (sy * SubGridSize) + sx];
            if (subCell != 0)
            {
                return subCell > 0;
            }
        }

        return InsideLocus(xy) || DistanceToLocusSquared(xy) <= LocusTolerance * LocusTolerance;
    }

    private static LocusLookupTable CreateLocusLookup()
    {
        const int totalBins = BinWidth * BinHeight;
        const float cellWidth = MaxX / BinWidth;
        const float cellHeight = MaxY / BinHeight;
        var coarseCells = new sbyte[totalBins];
        var subGridOffsets = new int[totalBins];
        Array.Fill(subGridOffsets, -1);

        var halfDiagonal = new Vector2(cellWidth, cellHeight).Length() * 0.5f;
        var coarseMarginSquared = (halfDiagonal + LocusTolerance) * (halfDiagonal + LocusTolerance);
        var boundaryBins = new List<int>(1536);

        for (var i = 0; i < totalBins; i++)
        {
            var bx = i % BinWidth;
            var by = i / BinWidth;
            var center = new Vector2((bx + 0.5f) * cellWidth, (by + 0.5f) * cellHeight);
            if (DistanceToLocusSquared(center) > coarseMarginSquared)
            {
                coarseCells[i] = InsideLocus(center) ? (sbyte)1 : (sbyte)-1;
            }
            else
            {
                boundaryBins.Add(i);
            }
        }

        const float subWidth = cellWidth / SubGridSize;
        const float subHeight = cellHeight / SubGridSize;
        var subHalfDiagonal = (new Vector2(subWidth, subHeight).Length() * 0.5f) + 1e-6f;
        var innerMargin = Math.Max(0.0f, LocusTolerance - subHalfDiagonal);
        var innerMarginSquared = innerMargin * innerMargin;
        var outerMargin = LocusTolerance + subHalfDiagonal;
        var outerMarginSquared = outerMargin * outerMargin;

        const int subStride = SubGridSize * SubGridSize;
        var subCells = new sbyte[boundaryBins.Count * subStride];
        for (var b = 0; b < boundaryBins.Count; b++)
        {
            var bin = boundaryBins[b];
            var baseX = (bin % BinWidth) * cellWidth;
            var baseY = (bin / BinWidth) * cellHeight;
            var offset = b * subStride;
            subGridOffsets[bin] = offset;

            for (var sy = 0; sy < SubGridSize; sy++)
            {
                var cy = baseY + ((sy + 0.5f) * subHeight);
                for (var sx = 0; sx < SubGridSize; sx++)
                {
                    var subCenter = new Vector2(baseX + ((sx + 0.5f) * subWidth), cy);
                    if (InsideLocus(subCenter))
                    {
                        // Since subHalfDiagonal < LocusTolerance, every point in this sub-cell
                        // is either inside the locus or within LocusTolerance of subCenter.
                        subCells[offset + (sy * SubGridSize) + sx] = 1;
                    }
                    else
                    {
                        var distSq = DistanceToLocusSquared(subCenter);
                        if (innerMargin > 0.0f && distSq <= innerMarginSquared)
                        {
                            subCells[offset + (sy * SubGridSize) + sx] = 1;
                        }
                        else if (distSq > outerMarginSquared)
                        {
                            subCells[offset + (sy * SubGridSize) + sx] = -1;
                        }
                    }
                }
            }
        }

        return new LocusLookupTable(coarseCells, subGridOffsets, subCells);
    }

    private static float DistanceToLocusSquared(Vector2 p)
    {
        var points = Locus.Value;
        var minimum = float.PositiveInfinity;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var edge = points[i] - points[j];
            var lengthSquared = edge.LengthSquared();
            var t = lengthSquared > 0 ? Math.Clamp(Vector2.Dot(p - points[j], edge) / lengthSquared, 0, 1) : 0;
            minimum = Math.Min(minimum, Vector2.DistanceSquared(p, points[j] + t * edge));
        }
        return minimum;
    }

    public static int Bin(Vector2 xy)
    {
        if (!float.IsFinite(xy.X) || !float.IsFinite(xy.Y) || xy.X < 0 || xy.X > MaxX || xy.Y < 0 || xy.Y > MaxY) return -1;
        var x = Math.Min(BinWidth - 1, (int)(xy.X / MaxX * BinWidth));
        var y = Math.Min(BinHeight - 1, (int)(xy.Y / MaxY * BinHeight));
        return y * BinWidth + x;
    }

    public static bool Contains(IReadOnlyList<Vector2> triangle, Vector2 xy)
    {
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        var a = Cross(triangle[1] - triangle[0], xy - triangle[0]);
        var b = Cross(triangle[2] - triangle[1], xy - triangle[1]);
        var c = Cross(triangle[0] - triangle[2], xy - triangle[2]);
        const float tolerance = 0.00001f;
        return (a >= -tolerance && b >= -tolerance && c >= -tolerance)
            || (a <= tolerance && b <= tolerance && c <= tolerance);
    }

    // Approximate screen colors for the diagram only, not used for coordinates or gamut tests.
    public static Vector3 IllustrationColor(Vector2 xy)
    {
        var xyz = new Vector3(xy.X, xy.Y, 1 - xy.X - xy.Y);
        var rgb = Vector3.Max(Vector3.Zero, new Vector3(
            3.24096994f * xyz.X - 1.53738318f * xyz.Y - .49861076f * xyz.Z,
            -.96924364f * xyz.X + 1.8759675f * xyz.Y + .04155506f * xyz.Z,
            .05563008f * xyz.X - .20397696f * xyz.Y + 1.05697151f * xyz.Z));
        rgb /= Math.Max(.00001f, Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z)));
        static float Encode(float c) => c <= .0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f;
        return new Vector3(Encode(rgb.X), Encode(rgb.Y), Encode(rgb.Z));
    }

    public static byte[] CreateBackground()
    {
        var result = new byte[BinWidth * BinHeight * 4];
        for (var y = 0; y < BinHeight; y++)
            for (var x = 0; x < BinWidth; x++)
            {
                var xy = new Vector2((x + .5f) / BinWidth * MaxX, (y + .5f) / BinHeight * MaxY);
                if (!InsideLocus(xy)) continue;
                var rgb = IllustrationColor(xy);
                var offset = ((BinHeight - 1 - y) * BinWidth + x) * 4;
                const byte alpha = 48;
                result[offset] = (byte)(rgb.Z * alpha);
                result[offset + 1] = (byte)(rgb.Y * alpha);
                result[offset + 2] = (byte)(rgb.X * alpha);
                result[offset + 3] = alpha;
            }
        return result;
    }

    private static bool InsideLocus(Vector2 p)
    {
        var points = Locus.Value;
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            if ((points[i].Y > p.Y) != (points[j].Y > p.Y)
                && p.X < (points[j].X - points[i].X) * (p.Y - points[i].Y) / (points[j].Y - points[i].Y) + points[i].X)
                inside = !inside;
        return inside;
    }

    private static Vector2[] ReadSpectralLocus()
    {
        using var stream = typeof(ChromaticityDiagram).Assembly.GetManifestResourceStream("HdrImageViewer.CIE1931")
            ?? throw new InvalidOperationException("Missing CIE 1931 observer data.");
        using var reader = new StreamReader(stream);
        var points = new List<Vector2>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            var wavelength = int.Parse(cells[0], CultureInfo.InvariantCulture);
            if (wavelength < 380 || wavelength > 700) continue;
            var x = double.Parse(cells[1], CultureInfo.InvariantCulture);
            var y = double.Parse(cells[2], CultureInfo.InvariantCulture);
            var z = double.Parse(cells[3], CultureInfo.InvariantCulture);
            points.Add(new Vector2((float)(x / (x + y + z)), (float)(y / (x + y + z))));
        }
        return points.ToArray();
    }

    internal sealed record LocusLookupTable(sbyte[] CoarseCells, int[] SubGridOffsets, sbyte[] SubCells);
}
