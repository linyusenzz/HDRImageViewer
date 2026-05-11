using System.Numerics;
using System.Runtime.InteropServices;

namespace HdrImageViewer.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct GainMapShaderConstants
{
    public Vector4 GainMapMin;
    public Vector4 GainMapMax;
    public Vector4 Gamma;
    public Vector4 OffsetSdr;
    public Vector4 OffsetHdr;
    public Vector4 Weight;
    public Vector4 Orientation;
    public Vector4 DisplayAdjustment;
    public Vector4 HdrCapacity;
    public Vector4 ImageLayout;
    public Vector4 ToneMap;
    public Vector4 ToneMap2;
}
