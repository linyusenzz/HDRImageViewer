using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Services;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private Vector4 _viewerToolsConstants = new(0, 1, 0, 0);
    private bool _captureAnalysis;
    public bool ComparisonEnabled { get; set; }
    public float ComparisonPosition { get; set; } = 0.5f;
    public long PreviewVersion { get; private set; }
    public LuminanceSnapshot? AnalysisSnapshot { get; private set; }
    public string? AnalysisError { get; private set; }

    public void ClearAnalysis() { AnalysisSnapshot = null; _captureAnalysis = false; PreviewVersion++; }

    public void RequestAnalysis() => _captureAnalysis = true;

    private void DrawWithViewerTools()
    {
        PreviewVersion++;
        var savedMode = _viewMode;
        try
        {
            // The comparison's HDR side always uses system/manual adaptive HDR.
            if (ComparisonEnabled) { _viewMode = GainmapViewMode.Adaptive; InvalidateToneMapAnalysis(); }
            _viewerToolsConstants = new Vector4(0, 1, 0, 0);
            UpdateGainMapConstantsBuffer();
            _context!.Draw(3, 0);
            if (_captureAnalysis)
            {
                _captureAnalysis = false;
                CaptureLuminanceSnapshot();
            }
            if (ComparisonEnabled)
            {
                _viewMode = GainmapViewMode.Sdr;
                InvalidateToneMapAnalysis();
                _viewerToolsConstants = new Vector4(0, Math.Clamp(ComparisonPosition, 0, 1), 0, 0);
                UpdateGainMapConstantsBuffer();
                _context.Draw(3, 0);
            }
        }
        finally
        {
            _viewMode = savedMode;
            _viewerToolsConstants = new Vector4(0, 1, 0, 0);
            if (ComparisonEnabled) InvalidateToneMapAnalysis();
        }
    }

    private void CaptureLuminanceSnapshot()
    {
        AnalysisSnapshot = null;
        AnalysisError = null;
        try
        {
            if ((long)_pixelWidth * _pixelHeight > 8_500_000)
                throw new InvalidOperationException(Localization.GetString("AnalysisErrorTooLarge"));
            using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            using var staging = _device!.CreateTexture2D(new Texture2DDescription(
                Format.R16G16B16A16_Float, (uint)_pixelWidth, (uint)_pixelHeight,
                1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            _context!.CopyResource(staging, backBuffer);
            _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            var bytes = new byte[checked(_pixelWidth * _pixelHeight * 8)];
            try
            {
                for (var y = 0; y < _pixelHeight; y++)
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)), bytes, y * _pixelWidth * 8, _pixelWidth * 8);
            }
            finally { _context.Unmap(staging, 0); }
            AnalysisSnapshot = new LuminanceSnapshot(_pixelWidth, _pixelHeight, bytes, GetCurrentImageLayout(), PreviewVersion,
                EffectiveSceneToSdrWhiteScale * 80);
        }
        catch (Exception ex) { AnalysisError = ex.Message; }
    }
}
