using System.Numerics;

namespace HdrImageViewer.Rendering;

internal enum HeatmapPresentation
{
    Inactive,
    Visible,
    Unavailable,
}

internal static class LuminanceHeatmap
{
    internal readonly record struct Band(int MinimumNits, byte R, byte G, byte B)
    {
        public Vector4 ToShaderBand(float whiteScale)
        {
            var rgb = new Vector3(R, G, B) / 255.0f;
            return new Vector4(HdrColorMath.SrgbToLinear(rgb) * whiteScale, MinimumNits);
        }
    }

    // Lower bounds are inclusive. The same palette feeds both the shader and its legend.
    private static readonly Band[] s_bands =
    [
        new(0, 37, 21, 76),
        new(1, 44, 63, 165),
        new(10, 25, 157, 224),
        new(50, 60, 200, 113),
        new(203, 236, 219, 75),
        new(400, 251, 143, 48),
        new(1000, 236, 63, 70),
        new(2000, 252, 160, 253),
    ];

    public static ReadOnlySpan<Band> Bands => s_bands;
}
