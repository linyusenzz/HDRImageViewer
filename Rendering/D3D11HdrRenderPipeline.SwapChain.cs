using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2D = Vortice.Direct2D1.D2D1;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private SwapChainFlags _activeSwapChainFlags;
    private SwapChainPanel? _boundPanel;

    private void EnsureDevice()
    {
        if (_device is not null)
        {
            return;
        }

        try
        {
            _device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels);
        }
        catch (SharpGenException)
        {
            _device = D3D11.D3D11CreateDevice(
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels);
        }

        _deviceGeneration++;
        _context = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();
    }

    private void EnsureDirect2DReady()
    {
        if (_device is null || _swapChain is null)
        {
            return;
        }

        if (_d2dContext is null)
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            EnsureDirect2DDevice(dxgiDevice);
        }

        if (_d2dTargetBitmap is null)
        {
            CreateD2DTargetBitmap();
        }
    }

    private void EnsureDirect2DDevice(IDXGIDevice dxgiDevice)
    {
        if (_d2dContext is not null)
        {
            return;
        }

        _d2dFactory = D2D.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;
        _d2dContext2 = _d2dContext.QueryInterfaceOrNull<ID2D1DeviceContext2>();
        _d2dContext5 = _d2dContext.QueryInterfaceOrNull<ID2D1DeviceContext5>();
        _wicFactory = new IWICImagingFactory();
    }

    private bool IsDeviceLost(SharpGenException ex)
    {
        var hr = ex.ResultCode.Code;
        if (hr is unchecked((int)0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
            or unchecked((int)0x887A0006)    // DXGI_ERROR_DEVICE_HUNG
            or unchecked((int)0x887A0007)    // DXGI_ERROR_DEVICE_RESET
            or unchecked((int)0x887A0020))   // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        {
            return true;
        }

        try
        {
            return _device is not null && _device.DeviceRemovedReason.Failure;
        }
        catch
        {
            return true;
        }
    }

    private void ResetDeviceResources()
    {
        DetachSwapChainFromPanel();
        ReleaseViewerToolGpuResources();
        ReleaseFrameAnalysisStagingTexture();
        ReleaseGainMapResources();

        _gainMapConstantsBuffer?.Dispose();
        _linearClampSampler?.Dispose();
        _linearizePixelShader?.Dispose();
        _baseImagePixelShader?.Dispose();
        _gainMapPixelShader?.Dispose();
        _gainMapVertexShader?.Dispose();
        _gainMapConstantsBuffer = null;
        _linearClampSampler = null;
        _linearizePixelShader = null;
        _baseImagePixelShader = null;
        _gainMapPixelShader = null;
        _gainMapVertexShader = null;

        ReleaseD2DTargetBitmap();
        _wicFactory?.Dispose();
        _d2dContext5?.Dispose();
        _d2dContext2?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _wicFactory = null;
        _d2dContext5 = null;
        _d2dContext2 = null;
        _d2dContext = null;
        _d2dDevice = null;
        _d2dFactory = null;

        _renderTargetView?.Dispose();
        _swapChain3?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _renderTargetView = null;
        _swapChain3 = null;
        _swapChain2 = null;
        _swapChain = null;
        _factory = null;
        _context = null;
        _device = null;

        _activeSwapChainFlags = SwapChainFlags.None;
        _boundPanel = null;
        _scRgbColorSpaceAvailable = false;
        _scRgbColorSpaceApplied = false;
    }

    private void RecoverFromDeviceLost(int targetWidth, int targetHeight)
    {
        var width = targetWidth > 0 ? targetWidth : _pixelWidth;
        var height = targetHeight > 0 ? targetHeight : _pixelHeight;
        ResetDeviceResources();
        EnsureDevice();
        if (_panel is not null && width > 0 && height > 0)
        {
            CreateSwapChain(width, height);
        }
    }

    private void CreateSwapChain(int pixelWidth, int pixelHeight)
    {
        if (_panel is null || _device is null || _factory is null)
        {
            return;
        }

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        var baseDescription = new SwapChainDescription1(
            (uint)pixelWidth,
            (uint)pixelHeight,
            Format.R16G16B16A16_Float,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        try
        {
            var waitableDescription = baseDescription with
            {
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            _swapChain = _factory.CreateSwapChainForComposition(_device, waitableDescription, null);
            _activeSwapChainFlags = SwapChainFlags.FrameLatencyWaitableObject;
        }
        catch (SharpGenException ex) when (!IsDeviceLost(ex))
        {
            _swapChain = _factory.CreateSwapChainForComposition(_device, baseDescription, null);
            _activeSwapChainFlags = SwapChainFlags.None;
        }

        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        _swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();

        if (_swapChain2 is not null && (_activeSwapChainFlags & SwapChainFlags.FrameLatencyWaitableObject) != 0)
        {
            try
            {
                _swapChain2.MaximumFrameLatency = 1;
            }
            catch (SharpGenException)
            {
                // Ignore if latency control is rejected by the composition surface.
            }
        }

        ConfigureScRgbColorSpace();
        ConfigureSwapChainPanelScale();

        if (!TryBindSwapChainToPanel())
        {
            return;
        }

        CreateRenderTargetView();
    }

    private void ResizeSwapChain(int pixelWidth, int pixelHeight)
    {
        if (_swapChain is null)
        {
            return;
        }

        ReleaseD2DTargetBitmap();
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _swapChain.ResizeBuffers(
            bufferCount: 2,
            width: (uint)pixelWidth,
            height: (uint)pixelHeight,
            newFormat: Format.R16G16B16A16_Float,
            swapChainFlags: _activeSwapChainFlags).CheckError();

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        ConfigureScRgbColorSpace();
        ConfigureSwapChainPanelScale();
        if ((!IsSwapChainPanelBound || !ReferenceEquals(_boundPanel, _panel))
            && !TryBindSwapChainToPanel())
        {
            return;
        }

        CreateRenderTargetView();
    }

    private void ConfigureScRgbColorSpace()
    {
        _scRgbColorSpaceAvailable = false;
        _scRgbColorSpaceApplied = false;

        if (_swapChain3 is null)
        {
            return;
        }

        var support = _swapChain3.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
        _scRgbColorSpaceAvailable = (support & SwapChainColorSpaceSupportFlags.Present) == SwapChainColorSpaceSupportFlags.Present;
        if (_scRgbColorSpaceAvailable)
        {
            _swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            _scRgbColorSpaceApplied = true;
        }
    }

    private void ConfigureSwapChainPanelScale()
    {
        if (_panel is null || _swapChain2 is null)
        {
            _swapChainTransformStatus = "swap chain DPI transform unavailable";
            return;
        }

        var scaleX = Math.Max(0.0001f, _panel.CompositionScaleX);
        var scaleY = Math.Max(0.0001f, _panel.CompositionScaleY);
        var transform = Matrix3x2.CreateScale(1.0f / scaleX, 1.0f / scaleY);
        _swapChain2.MatrixTransform = transform;
        _swapChainTransformStatus = $"DPI transform {1.0f / scaleX:0.###}x{1.0f / scaleY:0.###} for scale {scaleX:0.###}x{scaleY:0.###}";
    }

    private void CreateRenderTargetView()
    {
        if (_device is null || _swapChain is null)
        {
            LastRenderStatus = "Create render target skipped: device or swap chain missing";
            return;
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer, null);
    }

    private void CreateD2DTargetBitmap()
    {
        if (_d2dContext is null || _swapChain is null)
        {
            return;
        }

        ReleaseD2DTargetBitmap();
        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new DCommonPixelFormat(Format.R16G16B16A16_Float, DCommonAlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _d2dTargetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, properties);
        _d2dContext.Target = _d2dTargetBitmap;
    }

    private void ReleaseD2DTargetBitmap()
    {
        if (_d2dContext is not null)
        {
            _d2dContext.Target = null;
        }

        _d2dTargetBitmap?.Dispose();
        _d2dTargetBitmap = null;
    }

    private void PresentProbeFrame()
    {
        EnsureRenderTargetView();

        if (_context is null || _swapChain is null || _renderTargetView is null)
        {
            LastRenderStatus = "Probe frame skipped: D3D resources missing";
            return;
        }

        var color = new Color4(0.0f, 0.0f, 0.0f, 1.0f);
        _context.ClearRenderTargetView(_renderTargetView, color);
        _swapChain.Present(1, PresentFlags.None).CheckError();
        LastRenderStatus = $"Probe frame presented at {_pixelWidth}x{_pixelHeight}; {BuildOutputSummary()}";
    }

    private bool TryBindSwapChainToPanel()
    {
        if (_panel is null || _swapChain is null)
        {
            _panelBindingStatus = "WinUI swap chain bind skipped";
            LastRenderStatus = "Swap chain panel bind skipped: panel or swap chain missing";
            return false;
        }

        try
        {
            SetSwapChainOnPanel(_panel, _swapChain.NativePointer);
            _panelBindingStatus = "WinUI swap chain bound";
            _boundPanel = _panel;
            IsSwapChainPanelBound = true;
            return true;
        }
        catch (Exception ex)
        {
            _panelBindingStatus = $"WinUI swap chain bind failed: {ex.GetType().Name}";
            _boundPanel = null;
            IsSwapChainPanelBound = false;
            LastRenderStatus = $"Swap chain panel bind failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void DetachSwapChainFromPanel()
    {
        _boundPanel = null;
        if (_panel is null)
        {
            IsSwapChainPanelBound = false;
            return;
        }

        try
        {
            SetSwapChainOnPanel(_panel, IntPtr.Zero);
            IsSwapChainPanelBound = false;
        }
        catch
        {
            // Best-effort cleanup; the panel may already be leaving the XAML tree.
            IsSwapChainPanelBound = false;
        }
    }

    private static void SetSwapChainOnPanel(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = Marshal.GetIUnknownForObject(panel);
        var panelNative = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in WinUiSwapChainPanelNativeGuid, out panelNative));
            var vtable = Marshal.ReadIntPtr(panelNative);
            var setSwapChainPointer = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);
            var setSwapChain = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(setSwapChainPointer);
            Marshal.ThrowExceptionForHR(setSwapChain(panelNative, swapChain));
        }
        finally
        {
            if (panelNative != IntPtr.Zero)
            {
                Marshal.Release(panelNative);
            }

            Marshal.Release(unknown);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr panelNative, IntPtr swapChain);
}
