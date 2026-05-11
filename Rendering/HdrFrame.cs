namespace HdrImageViewer.Rendering;

public sealed record HdrFrame(int PixelWidth, int PixelHeight, ushort[] RgbaHalfPixels);
