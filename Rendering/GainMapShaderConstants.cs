using System.Numerics;
using System.Runtime.InteropServices;

namespace HdrImageViewer.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct GainMapShaderConstants
{
    // Keep this layout matched with the HLSL GainMapConstants cbuffer.
    public Vector4 GainMapMin;
    public Vector4 GainMapMax;
    public Vector4 Gamma;
    public Vector4 OffsetSdr;
    public Vector4 OffsetHdr;
    // x: explicit gain-map weight, y: Apple HDRGainMap semantics flag, z: SDR base gamut.
    public Vector4 GainMapControl;
    // x: transfer, y: single-layer gamut, z: use alternate gain-map gamut, w: alternate gamut code.
    public Vector4 SourceEncoding;
    public Vector4 Orientation;
    // x: SDR white scene scale, y: max scene clamp, z: target boost stops, w: full-frame scene limit.
    public Vector4 DisplayMapping;
    public Vector4 HdrCapacity;
    public Vector4 ImageLayout;
    // x: enabled, y: virtual target peak, z: tone-map source peak, w: content average.
    public Vector4 ToneMapInput;
    // x: physical target peak, y: tone mode, z: adaptive target peak, w: global/midtone scale.
    public Vector4 ToneMapOutput;
    // x: GainmapViewMode, y: HdrHeadroomMode, z: reference-white exposure, w: ColorGamutMappingMode.
    public Vector4 ViewModeParams;
    // x/y: visible horizontal interval; z: offscreen analysis/probe flag; w: reserved.
    public Vector4 ViewerTools;
}
