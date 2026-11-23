using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using HdrImageViewer.Models;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace HdrImageViewer.Rendering;

public sealed partial class D3D11HdrRenderPipeline
{
    private string BuildOutputSummary()
    {
        var colorSpace = _scRgbColorSpaceApplied
            ? "scRGB swap chain"
            : _scRgbColorSpaceAvailable
                ? "scRGB color space available but not applied"
                : "scRGB color space unavailable";
        var capacityOverride = _displayCapacityOverrideLog2 is { } value
            ? $"; capacity override {value:0.###} stops target {_displayConfiguration.SdrWhiteLevelInNits * Math.Pow(2.0, value):0} nits"
            : string.Empty;
        return $"{_panelBindingStatus}; {_swapChainTransformStatus}; {colorSpace}; color gamut {BuildColorGamutMappingSummary()}{capacityOverride}; {_displayConfiguration.RenderSummary}";
    }

    private string BuildLayoutSummary()
    {
        var layout = GetCurrentImageLayout();
        return $"source {_contentPixelWidth}x{_contentPixelHeight}, fit {layout.X:0.###}x{layout.Y:0.###}+{layout.Z:0.###},{layout.W:0.###}";
    }

    private string BuildGainMapSummary()
    {
        var modeLabel = _viewMode switch
        {
            GainmapViewMode.Sdr => "SDR",
            GainmapViewMode.Adaptive => "Adaptive",
            GainmapViewMode.AlternateImage => "Alternate Image",
            GainmapViewMode.GainMap => "Gain Map",
            _ => "Adaptive",
        };
        var toneMap = _toneMappingEnabledForCurrentFrame && _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? $", tone gain-map global scale {_toneMapAnalysis.GlobalScale:0.###}x, target {_toneMapAnalysis.AdaptiveTargetPeak:0.###}/{_toneMapAnalysis.PhysicalTargetPeak:0.###} physical ({CalculateToneMapCompressionRatio():0.##}x virtual {_toneMapAnalysis.VirtualTargetPeak:0.###}), full-frame {_toneMapAnalysis.FullFrameLimit:0.###}, content max/p99.5/tone/avg {_toneMapAnalysis.ContentPeak:0.###}/{_toneMapAnalysis.HighPercentilePeak:0.###}/{_toneMapAnalysis.ToneMapPeak:0.###}/{_toneMapAnalysis.ContentAverage:0.###}"
            : ", tone off";
        var baseGamut = _gainMapConstants.GainMapControl.Z switch
        {
            > 3.5f => "Adobe RGB",
            > 2.5f => "ProPhoto RGB",
            > 1.5f => "BT.2020",
            > 0.5f => "Display P3",
            _ => "BT.709/sRGB",
        };
        var baseTransfer = _gainMapConstants.SourceEncoding.X is > 0.5f and < 1.5f ? "BT.709" : "sRGB";
        var gainSampleStats = BuildGainSampleStats();
        return $"mode {modeLabel}, base {baseGamut}/{baseTransfer}, gain min {FormatVector3(_gainMapConstants.GainMapMin)}, max {FormatVector3(_gainMapConstants.GainMapMax)}, gamma {FormatVector3(_gainMapConstants.Gamma)}, cap {_gainMapConstants.HdrCapacity.X:0.###}-{_gainMapConstants.HdrCapacity.Y:0.###}, weight {CalculateGainMapWeightForStatus():0.###}, scene scale {CalculateGainMapSceneScale(_gainMapConstants):0.###}x, white scale {_displayConfiguration.SceneToSdrWhiteScale:0.###}x{gainSampleStats}{toneMap}";
    }

    private string BuildColorGamutMappingSummary()
    {
        return _colorGamutMappingMode switch
        {
            ColorGamutMappingMode.Clip => "clip",
            _ => "managed",
        };
    }

    private static string FormatVector3(Vector4 value)
    {
        return $"[{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}]";
    }

    private string BuildGainSampleStats()
    {
        if (_gainSampleStatsSummary is not null) return _gainSampleStatsSummary;
        if (_gainMapAnalysisSource is not { Samples.Length: > 0 } analysis)
        {
            return string.Empty;
        }

        var samples = analysis.Samples;
        var luma = ArrayPool<float>.Shared.Rent(samples.Length);
        try
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var g = samples[i].Gain;
                luma[i] = MathF.Max(MathF.Max(g.X, g.Y), g.Z);
            }

            Array.Sort(luma, 0, samples.Length);
            float Percentile(float fraction)
            {
                var idx = Math.Clamp((int)MathF.Round(fraction * (samples.Length - 1)), 0, samples.Length - 1);
                return luma[idx];
            }

            return _gainSampleStatsSummary = $", gain sample min/p50/p99/max {luma[0]:0.###}/{Percentile(0.5f):0.###}/{Percentile(0.99f):0.###}/{luma[samples.Length - 1]:0.###}";
        }
        finally
        {
            ArrayPool<float>.Shared.Return(luma);
        }
    }

    private string BuildBaseImageMappingSummary()
    {
        var transfer = _gainMapConstants.SourceEncoding.X switch
        {
            > 4.5f => "scene-linear scRGB",
            > 3.5f => "linear scRGB",
            > 2.5f => "PQ",
            > 1.5f => "HLG",
            _ => "SDR"
        };
        if (transfer == "SDR")
        {
            var sdrPrimaries = _gainMapConstants.SourceEncoding.Y switch
            {
                > 3.5f => "Adobe RGB to scRGB",
                > 2.5f => "ProPhoto RGB to scRGB",
                > 1.5f => "BT.2020 to scRGB",
                > 0.5f => "Display P3 to scRGB",
                _ => "sRGB/BT.709",
            };
            return $"base map SDR {sdrPrimaries}, white scale {EffectiveSceneToSdrWhiteScale:0.###}x";
        }

        var targetScenePeak = CalculateBaseHdrVirtualTargetPeak(_gainMapConstants, CalculateBaseHdrToneMapWhiteScale(_gainMapConstants));
        var primaries = transfer is "linear scRGB" or "scene-linear scRGB"
            ? "working scRGB (P709, extended range)"
            : _gainMapConstants.SourceEncoding.Y switch
            {
                > 3.5f => "Adobe RGB to scRGB",
                > 2.5f => "ProPhoto RGB to scRGB",
                > 1.5f => "BT.2020 to scRGB",
                > 0.5f => "Display P3 to scRGB",
                _ => "source primaries",
            };
        var singleLayerDisplayFit = IsSingleLayerDisplayFitToneMapEnabled();
        var toneMode = singleLayerDisplayFit
            ? "display-fit highlight rolloff"
            : _displayCapacityOverrideLog2 is not null ? "manual peak" : "system auto";
        var scaleLabel = singleLayerDisplayFit ? "midtone scale" : "global scale";
        var toneMap = _toneMappingEnabledForCurrentFrame && _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? $", tone single-layer {toneMode} {scaleLabel} {_toneMapAnalysis.GlobalScale:0.###}x, target {_toneMapAnalysis.AdaptiveTargetPeak:0.###}/{_toneMapAnalysis.PhysicalTargetPeak:0.###} physical ({CalculateToneMapCompressionRatio():0.##}x virtual {_toneMapAnalysis.VirtualTargetPeak:0.###}), full-frame {_toneMapAnalysis.FullFrameLimit:0.###}, content max/p99.5/tone/avg {_toneMapAnalysis.ContentPeak:0.###}/{_toneMapAnalysis.HighPercentilePeak:0.###}/{_toneMapAnalysis.ToneMapPeak:0.###}/{_toneMapAnalysis.ContentAverage:0.###}"
            : ", tone off";
        var modeSummary = _viewMode == GainmapViewMode.GainMap
            ? "Adaptive (Gain Map unavailable: no gain map)"
            : EffectiveViewModeForCurrentFrame.ToString();
        var exposureReferenceWhite = transfer is "PQ" or "HLG" ? 203.0f : 80.0f;
        var exposureSummary = Math.Abs(_referenceWhiteExposureScale - 1.0f) > 0.001f
            ? $", exposure {_referenceWhiteExposureScale:0.###}x (diffuse white {_referenceWhiteExposureScale * exposureReferenceWhite:0} nits)"
            : string.Empty;
        var sdrClampSummary = EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr && _primaryAnalysisSource?.IsHdrEncoded == true
            ? ", SDR clamps HDR source to SDR white"
            : string.Empty;
        return $"base map {modeSummary} {transfer} {primaries}, target {targetScenePeak:0.###} scene ({targetScenePeak * 80.0f:0} nits){exposureSummary}{sdrClampSummary}{toneMap}";
    }

    private bool IsSingleLayerDisplayFitToneMapEnabled()
    {
        return _gainMapAnalysisSource is null
            && _primaryAnalysisSource?.IsHdrEncoded == true
            && (IsEffectiveAdaptiveToneMappingEnabled || _displayCapacityOverrideLog2 is not null);
    }

    private float CalculateToneMapCompressionRatio()
    {
        return _toneMapAnalysis.VirtualTargetPeak > 0.0f
            ? _toneMapAnalysis.AdaptiveTargetPeak / _toneMapAnalysis.VirtualTargetPeak
            : 1.0f;
    }

    private float CalculateGainMapWeightForStatus()
    {
        return HdrColorMath.CalculateGainMapWeight(_gainMapConstants, EffectiveDisplayBoostLog2);
    }

    private float CalculateGainMapSceneScale(GainMapShaderConstants constants)
    {
        var exposureScale = Math.Max(_referenceWhiteExposureScale, 0.0f);
        if (constants.GainMapControl.Y <= 0.5f)
        {
            return (203.0f / 80.0f) * exposureScale;
        }

        return Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f) * exposureScale;
    }

    private GainmapViewMode EffectiveViewModeForCurrentFrame => _viewMode == GainmapViewMode.GainMap && _gainMapAnalysisSource is null
        ? GainmapViewMode.Adaptive
        : _viewMode;

    private float EffectiveDisplayBoostLog2 => EffectiveViewModeForCurrentFrame switch
    {
        GainmapViewMode.Sdr => _gainMapConstants.HdrCapacity.X > _gainMapConstants.HdrCapacity.Y
            ? _gainMapConstants.HdrCapacity.Y
            : 0.0f,
        GainmapViewMode.AlternateImage => _gainMapConstants.HdrCapacity.X > _gainMapConstants.HdrCapacity.Y
            ? _gainMapConstants.HdrCapacity.Y
            : Math.Max(_gainMapConstants.HdrCapacity.Y, _displayConfiguration.MaxDisplayBoostLog2),
        _ => _displayCapacityOverrideLog2 ?? _displayConfiguration.MaxDisplayBoostLog2,
    };

    private float EffectiveMaxSceneValue => EffectiveViewModeForCurrentFrame == GainmapViewMode.Sdr
        ? CalculateSdrModeMaxSceneValue()
        : EffectiveViewModeForCurrentFrame == GainmapViewMode.AlternateImage
            ? 0.0f
        : _displayCapacityOverrideLog2 is null ? _displayConfiguration.MaxSceneValue : 0.0f;

    private float CalculateSdrModeMaxSceneValue()
    {
        var whiteScale = Math.Max(EffectiveSceneToSdrWhiteScale, 1.0f);
        if (_primaryAnalysisSource?.IsHdrEncoded == true)
        {
            return whiteScale;
        }

        // For a wide-gamut (Display P3 / BT.2020 / ProPhoto / Adobe RGB) SDR base image,
        // gamut conversion to BT.709 / scRGB produces channel values ABOVE the SDR white
        // scale for colours outside the BT.709 hull. Allow headroom up to the display's
        // scene capability so wide-gamut channels survive to the scRGB swap chain.
        var gamut = _primaryAnalysisSource?.ColorGamut ?? GainMapColorGamut.Unknown;
        if (gamut is GainMapColorGamut.DisplayP3 or GainMapColorGamut.Bt2100 or GainMapColorGamut.ProPhoto or GainMapColorGamut.AdobeRgb)
        {
            var displayCeiling = _displayConfiguration.MaxSceneValue;
            return displayCeiling > whiteScale ? displayCeiling : whiteScale;
        }

        return whiteScale;
    }

    private float EffectiveSceneToSdrWhiteScale => _displayConfiguration.SceneToSdrWhiteScale;

    private void EnsureRenderTargetView()
    {
        if (_renderTargetView is null && _device is not null && _swapChain is not null)
        {
            CreateRenderTargetView();
        }
    }

    private ID3D11Texture2D GetOrCreateFrameAnalysisStagingTexture(int sampledRowCount)
    {
        // The verification texture contains only the sparse rows copied from the
        // back buffer. At 4K this is roughly 70 rows instead of a second full
        // 2160-row FP16 render target.
        if (_frameAnalysisStagingTexture is not null
            && _frameAnalysisStagingWidth == _pixelWidth
            && _frameAnalysisStagingHeight == sampledRowCount)
        {
            return _frameAnalysisStagingTexture;
        }

        _frameAnalysisStagingTexture?.Dispose();
        _frameAnalysisStagingTexture = null;

        var description = new Texture2DDescription(
            Format.R16G16B16A16_Float,
            checked((uint)_pixelWidth),
            checked((uint)sampledRowCount),
            arraySize: 1,
            mipLevels: 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        var texture = _device!.CreateTexture2D(description);
        _frameAnalysisStagingTexture = texture;
        _frameAnalysisStagingWidth = _pixelWidth;
        _frameAnalysisStagingHeight = sampledRowCount;
        return texture;
    }

    private void ReleaseFrameAnalysisStagingTexture()
    {
        _frameAnalysisStagingTexture?.Dispose();
        _frameAnalysisStagingTexture = null;
        _frameAnalysisStagingWidth = 0;
        _frameAnalysisStagingHeight = 0;
    }

    /// <summary>
    /// Rows that <see cref="AnalyzeBackBuffer"/> reads: the sparse sampling grid
    /// (same stepY formula as the sampling loop) plus the top/middle/bottom
    /// probe rows. Only these rows are copied into the staging texture.
    /// </summary>
    private static int[] GetAnalysisRowIndices(int pixelHeight)
    {
        var stepY = Math.Max(1, pixelHeight / 64);
        var rows = new SortedSet<int>();
        for (var y = 0; y < pixelHeight; y += stepY)
        {
            rows.Add(y);
        }

        rows.Add(Math.Max(0, pixelHeight / 20));
        rows.Add(pixelHeight / 2);
        rows.Add(Math.Max(0, pixelHeight - (pixelHeight / 20) - 1));
        return [.. rows];
    }

    private FrameAnalysis AnalyzeBackBufferIfPending()
    {
        if (!_frameVerificationPending)
        {
            if (_lastFrameAnalysis.HasVisiblePixels)
            {
                return _lastFrameAnalysis with
                {
                    Summary = $"frame verification reused; {_lastFrameAnalysis.Summary}",
                };
            }

            return new FrameAnalysis(true, 0.0f, "frame verification deferred");
        }

        _lastFrameAnalysis = AnalyzeBackBuffer();
        _frameVerificationPending = false;
        return _lastFrameAnalysis;
    }

    private FrameAnalysis AnalyzeBackBuffer()
    {
        if (_device is null || _context is null || _swapChain is null || _pixelWidth <= 0 || _pixelHeight <= 0)
        {
            return new FrameAnalysis(false, 0.0f, "frame stats unavailable");
        }

        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var sampledRows = GetAnalysisRowIndices(_pixelHeight);
            var stagingTexture = GetOrCreateFrameAnalysisStagingTexture(sampledRows.Length);
            for (var destinationRow = 0; destinationRow < sampledRows.Length; destinationRow++)
            {
                var sourceRow = sampledRows[destinationRow];
                _context.CopySubresourceRegion(
                    stagingTexture, 0, 0, (uint)destinationRow, 0,
                    backBuffer, 0,
                    new Box(0, sourceRow, 0, _pixelWidth, sourceRow + 1, 1));
            }

            _context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mappedResource).CheckError();
            try
            {
                var stepX = Math.Max(1, _pixelWidth / 64);
                var stepY = Math.Max(1, _pixelHeight / 64);
                double sum = 0.0;
                var max = 0.0f;
                var samples = 0;

                for (var sourceY = 0; sourceY < _pixelHeight; sourceY += stepY)
                {
                    var compactY = Array.BinarySearch(sampledRows, sourceY);
                    if (compactY < 0)
                    {
                        continue;
                    }

                    var row = IntPtr.Add(mappedResource.DataPointer, checked(compactY * (int)mappedResource.RowPitch));
                    for (var x = 0; x < _pixelWidth; x += stepX)
                    {
                        var pixel = IntPtr.Add(row, checked(x * 8));
                        var r = ReadHalf(pixel, 0);
                        var g = ReadHalf(pixel, 2);
                        var b = ReadHalf(pixel, 4);
                        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                        {
                            continue;
                        }

                        var luminance = Math.Max(0.0f, (0.2126f * r) + (0.7152f * g) + (0.0722f * b));
                        sum += luminance;
                        max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
                        samples++;
                    }
                }

                var average = samples > 0 ? (float)(sum / samples) : 0.0f;
                var visible = max > 0.002f || average > 0.0005f;
                var topY = Array.BinarySearch(sampledRows, Math.Max(0, _pixelHeight / 20));
                var middleY = Array.BinarySearch(sampledRows, _pixelHeight / 2);
                var bottomY = Array.BinarySearch(sampledRows, Math.Max(0, _pixelHeight - (_pixelHeight / 20) - 1));
                var top = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, topY);
                var middle = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, middleY);
                var bottom = SampleLuminance(mappedResource.DataPointer, mappedResource.RowPitch, _pixelWidth / 2, bottomY);
                return new FrameAnalysis(visible, max, $"frame avg {average:0.###}, max {max:0.###}, y {top:0.###}/{middle:0.###}/{bottom:0.###}");
            }
            finally
            {
                _context.Unmap(stagingTexture, 0);
            }
        }
        catch (SharpGenException ex) when (IsDeviceLost(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FrameAnalysis(true, 0.0f, $"frame stats unavailable: {ex.GetType().Name}");
        }
    }

    private static float ReadHalf(IntPtr pixel, int byteOffset)
    {
        var bits = unchecked((ushort)Marshal.ReadInt16(pixel, byteOffset));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static float SampleLuminance(IntPtr dataPointer, uint rowPitch, int x, int y)
    {
        var row = IntPtr.Add(dataPointer, checked(y * (int)rowPitch));
        var pixel = IntPtr.Add(row, checked(x * 8));
        var r = ReadHalf(pixel, 0);
        var g = ReadHalf(pixel, 2);
        var b = ReadHalf(pixel, 4);
        return Math.Max(0.0f, (0.2126f * r) + (0.7152f * g) + (0.0722f * b));
    }

    private static string BuildMissingResourceList(params (string Name, bool Missing)[] resources)
    {
        var missing = resources
            .Where(resource => resource.Missing)
            .Select(resource => resource.Name);
        return string.Join(", ", missing);
    }

    private static string DescribeWicPixelFormat(Guid pixelFormat)
    {
        if (pixelFormat == WicPixelFormat.Format64bppPRGBA)
        {
            return "64bpp PRGBA";
        }

        if (pixelFormat == WicPixelFormat.Format64bppRGBA)
        {
            return "64bpp RGBA";
        }

        if (pixelFormat == WicPixelFormat.Format64bppPRGBAHalf)
        {
            return "64bpp PRGBA half";
        }

        if (pixelFormat == WicPixelFormat.Format64bppRGBAHalf)
        {
            return "64bpp RGBA half";
        }

        if (pixelFormat == WicPixelFormat.Format32bppPRGBA)
        {
            return "32bpp PRGBA";
        }

        if (pixelFormat == WicPixelFormat.Format32bppRGBA)
        {
            return "32bpp RGBA";
        }

        if (pixelFormat == WicPixelFormat.Format32bppR10G10B10A2HDR10)
        {
            return "32bpp R10G10B10A2 HDR10";
        }

        return pixelFormat.ToString("D");
    }

    private static bool IsFloatingPointWicPixelFormat(Guid pixelFormat)
    {
        return pixelFormat == WicPixelFormat.Format64bppRGBAHalf
            || pixelFormat == WicPixelFormat.Format64bppPRGBAHalf
            || pixelFormat == WicPixelFormat.Format64bppRGBHalf
            || pixelFormat == WicPixelFormat.Format48bppRGBHalf
            || pixelFormat == WicPixelFormat.Format96bppRGBFloat
            || pixelFormat == WicPixelFormat.Format128bppRGBAFloat
            || pixelFormat == WicPixelFormat.Format128bppPRGBAFloat
            || pixelFormat == WicPixelFormat.Format128bppRGBFloat;
    }
}
