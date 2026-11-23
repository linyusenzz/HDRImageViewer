using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private const string HeatmapPixelShaderResourceName = "HdrImageViewer.Rendering.Shaders.Heatmap.PS.cso";
    private readonly Func<byte[]> _loadHeatmapShader;

    public D3D11HdrRenderPipeline() : this(() => LoadEmbeddedShaderBytecode(HeatmapPixelShaderResourceName)) { }

    internal D3D11HdrRenderPipeline(Func<byte[]> loadHeatmapShader) => _loadHeatmapShader = loadHeatmapShader;

    private ID3D11PixelShader? _heatmapPixelShader;
    private ID3D11Buffer? _heatmapConstantsBuffer;
    private ID3D11Texture2D? _heatmapPixels;
    private ID3D11RenderTargetView? _heatmapPixelsTarget;
    private ID3D11ShaderResourceView? _heatmapPixelsView;
    internal HeatmapPresentation HeatmapState { get; private set; }
    internal event Action? HeatmapPresentationChanged;

    private void SetHeatmapState(HeatmapPresentation state)
    {
        if (HeatmapState == state) return;
        HeatmapState = state;
        HeatmapPresentationChanged?.Invoke();
    }

    // A palette change only re-presents the current document and viewport.
    // Navigation, zoom and settings redraws still invalidate their analysis.
    internal async Task SetLuminanceHeatmapAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _renderOperationGate.WaitAsync(cancellationToken);
        try
        {
            LuminanceHeatmapEnabled = enabled;
            if (_document is null || _swapChain is null || _primaryTextureView is null) return;

            try
            {
                if (_loadedGainMapMode)
                    RenderGainMap(invalidateAnalysis: false);
                else
                    RenderBaseImage(_document, invalidateAnalysis: false);
            }
            catch (SharpGenException ex) when (!_isDisposed && IsDeviceLost(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverFromDeviceLost(_pixelWidth, _pixelHeight);
                await EnsureTexturesReadyForAnalysisAsync(cancellationToken);
            }
        }
        finally
        {
            _renderOperationGate.Release();
        }
    }

    public bool LuminanceHeatmapEnabled { get; set; }

    private bool ShouldDrawLuminanceHeatmap => LuminanceHeatmapEnabled && !ComparisonEnabled;

    [StructLayout(LayoutKind.Sequential)]
    private struct HeatmapConstants
    {
        public Vector4 Layout;
        public Vector4 Band0;
        public Vector4 Band1;
        public Vector4 Band2;
        public Vector4 Band3;
        public Vector4 Band4;
        public Vector4 Band5;
        public Vector4 Band6;
        public Vector4 Band7;
    }

    private void PresentImageFrame()
    {
        var state = HeatmapPresentation.Inactive;
        if (ShouldDrawLuminanceHeatmap && _renderTargetView is not null)
        {
            try
            {
                DrawLuminanceHeatmap(_renderTargetView, _pixelHeight, 0);
                state = HeatmapPresentation.Visible;
            }
            catch (SharpGenException ex) when (!IsDeviceLost(ex))
            {
                // The ordinary frame remains intact if optional GPU resources fail.
                ReleaseHeatmapPixels();
                state = HeatmapPresentation.Unavailable;
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
        else
        {
            ReleaseHeatmapPixels();
        }

        _swapChain!.Present(1, PresentFlags.None).CheckError();
        SetHeatmapState(state);
    }

    private void DrawLuminanceHeatmap(ID3D11RenderTargetView targetView, int targetHeight, int startY)
    {
        var device = _device!;
        var context = _context!;
        _heatmapPixelShader ??= device.CreatePixelShader(_loadHeatmapShader(), null);
        _heatmapConstantsBuffer ??= device.CreateBuffer(
            (uint)Marshal.SizeOf<HeatmapConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);

        if (_heatmapPixels?.Description.Width != _pixelWidth || _heatmapPixels.Description.Height != targetHeight
            || _heatmapPixelsTarget is null || _heatmapPixelsView is null)
        {
            ReleaseHeatmapPixels();
            _heatmapPixels = device.CreateTexture2D(new Texture2DDescription(
                Format.R16G16B16A16_Float, (uint)_pixelWidth, (uint)targetHeight, arraySize: 1, mipLevels: 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            _heatmapPixelsTarget = device.CreateRenderTargetView(_heatmapPixels);
            _heatmapPixelsView = device.CreateShaderResourceView(_heatmapPixels);
        }

        var bands = LuminanceHeatmap.Bands;
        var whiteScale = _displayConfiguration.SceneToSdrWhiteScale;
        var constants = new HeatmapConstants
        {
            Layout = GetCurrentImageLayout(),
            Band0 = bands[0].ToShaderBand(whiteScale),
            Band1 = bands[1].ToShaderBand(whiteScale),
            Band2 = bands[2].ToShaderBand(whiteScale),
            Band3 = bands[3].ToShaderBand(whiteScale),
            Band4 = bands[4].ToShaderBand(whiteScale),
            Band5 = bands[5].ToShaderBand(whiteScale),
            Band6 = bands[6].ToShaderBand(whiteScale),
            Band7 = bands[7].ToShaderBand(whiteScale),
        };
        context.UpdateSubresource(in constants, _heatmapConstantsBuffer);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null, 0);
        context.OMSetRenderTargets(_heatmapPixelsTarget, null);
        context.RSSetState(null);
        context.RSSetViewport(0.0f, -startY, _pixelWidth, _pixelHeight, 0.0f, 1.0f);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_gainMapVertexShader);
        context.PSSetShader(_loadedGainMapMode ? _gainMapPixelShader : _baseImagePixelShader);
        context.PSSetShaderResource(0, _primaryTextureView!);
        if (_loadedGainMapMode) context.PSSetShaderResource(1, _gainMapTextureView!);
        context.PSSetSampler(0, _linearClampSampler);
        context.PSSetConstantBuffer(0, _gainMapConstantsBuffer);
        var savedTools = _viewerToolsConstants;
        try
        {
            _viewerToolsConstants = new Vector4(0, 1, 1, 0);
            UpdateGainMapConstantsBuffer();
            context.Draw(3, 0);
            context.OMSetRenderTargets(targetView, null);
            context.PSSetShader(_heatmapPixelShader);
            context.PSSetConstantBuffer(1, _heatmapConstantsBuffer);
            context.PSSetShaderResource(0, _heatmapPixelsView);
            context.Draw(3, 0);
        }
        finally
        {
            _viewerToolsConstants = savedTools;
            context.PSUnsetShaderResources(0, 2);
            context.PSUnsetConstantBuffer(1);
            UpdateGainMapConstantsBuffer();
        }
    }

    private void ReleaseHeatmapPixels()
    {
        _heatmapPixelsView?.Dispose();
        _heatmapPixelsTarget?.Dispose();
        _heatmapPixels?.Dispose();
        _heatmapPixelsView = null;
        _heatmapPixelsTarget = null;
        _heatmapPixels = null;
    }

    private void ReleaseLuminanceHeatmapGpuResources()
    {
        SetHeatmapState(HeatmapPresentation.Inactive);
        ReleaseHeatmapPixels();
        _heatmapPixelShader?.Dispose();
        _heatmapConstantsBuffer?.Dispose();
        _heatmapPixelShader = null;
        _heatmapConstantsBuffer = null;
    }
}
