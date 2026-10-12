using System.Globalization;
using System.Numerics;

namespace HdrImageViewer.Rendering;

public static class ChromaticityDiagram
{
    public const int BinWidth = 320;
    public const int BinHeight = 360;
    public const float MaxX = 0.8f;
    public const float MaxY = 0.9f;
    public static Vector2 WhitePoint => new(0.3127f, 0.3290f);
    public static IReadOnlyList<Vector2> Srgb { get; } = new[] { new Vector2(.64f, .33f), new Vector2(.30f, .60f), new Vector2(.15f, .06f) };
    public static IReadOnlyList<Vector2> DisplayP3 { get; } = new[] { new Vector2(.68f, .32f), new Vector2(.265f, .69f), new Vector2(.15f, .06f) };
    public static IReadOnlyList<Vector2> Bt2020 { get; } = new[] { new Vector2(.708f, .292f), new Vector2(.170f, .797f), new Vector2(.131f, .046f) };
    private static readonly Lazy<Vector2[]> Locus = new(ReadSpectralLocus);
    private static readonly Lazy<sbyte[]> LocusCells = new(CreateLocusCells);
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
        var x = .4123907993 * rgb.X + .3575843394 * rgb.Y + .1804807884 * rgb.Z;
        var y = .2126390059 * rgb.X + .7151686788 * rgb.Y + .0721923154 * rgb.Z;
        var z = .0193308187 * rgb.X + .1191947798 * rgb.Y + .9505321522 * rgb.Z;
        var sum = x + y + z;
        if (sum <= 1e-10 || y <= 1e-10) return null;
        // Retain signed XYZ coordinates for diagnostics, even outside the plot.
        return new Vector2((float)(x / sum), (float)(y / sum));
    }

    public static bool IsPhysical(Vector2 xy)
    {
        var bin = Bin(xy);
        if (bin < 0) return false;
        var cell = LocusCells.Value[bin];
        return cell != 0 ? cell > 0 : InsideLocus(xy) || DistanceToLocusSquared(xy) <= LocusTolerance * LocusTolerance;
    }

    private static sbyte[] CreateLocusCells()
    {
        var cells = new sbyte[BinWidth * BinHeight];
        var halfDiagonal = new Vector2(MaxX / BinWidth, MaxY / BinHeight).Length() / 2;
        var marginSquared = MathF.Pow(halfDiagonal + LocusTolerance, 2);
        for (var i = 0; i < cells.Length; i++)
        {
            var center = new Vector2((i % BinWidth + .5f) * MaxX / BinWidth, (i / BinWidth + .5f) * MaxY / BinHeight);
            // Only cells safely away from the boundary can use a cached result.
            if (DistanceToLocusSquared(center) > marginSquared)
                cells[i] = InsideLocus(center) ? (sbyte)1 : (sbyte)-1;
        }
        return cells;
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
}
