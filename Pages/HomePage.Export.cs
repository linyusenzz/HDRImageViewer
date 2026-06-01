using HdrImageViewer.Rendering;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using HdrImageViewer.ViewModels;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using SharpGen.Runtime;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private async Task<bool> TryBeginExportProgressAsync(string title, string detail)
    {
        if (_isExportInProgress)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已有导出任务正在进行，请等待完成。若系统保存对话框仍打开，请先完成或取消它。");
            return false;
        }

        _isExportInProgress = true;
        SetExportProgress(title, detail);
        if (ExportProgressOverlay is not null)
        {
            ExportProgressOverlay.Visibility = Visibility.Visible;
        }

        if (ExportProgressRing is not null)
        {
            ExportProgressRing.IsActive = true;
        }

        SetExportControlsEnabled(false);
        await Task.Yield();
        await Task.Delay(50);
        return true;
    }

    private void UpdateExportProgress(string detail)
    {
        SetExportProgress("正在导出", detail);
    }

    private void EndExportProgress()
    {
        _isExportInProgress = false;
        if (ExportProgressRing is not null)
        {
            ExportProgressRing.IsActive = false;
        }

        if (ExportProgressOverlay is not null)
        {
            ExportProgressOverlay.Visibility = Visibility.Collapsed;
        }

        SetExportControlsEnabled(true);
        UpdateFolderNavigationOverlay();
    }

    private void SetExportProgress(string title, string detail)
    {
        if (ExportProgressTitleText is not null)
        {
            ExportProgressTitleText.Text = title;
        }

        if (ExportProgressDetailText is not null)
        {
            ExportProgressDetailText.Text = detail;
        }
    }

    private void SetExportControlsEnabled(bool isEnabled)
    {
        if (CropButton is not null)
        {
            CropButton.IsEnabled = isEnabled && ViewModel.HasImage;
        }

        if (TopCropButton is not null)
        {
            TopCropButton.IsEnabled = isEnabled && ViewModel.HasImage;
        }

        if (SingleLayerHdrSaveAsButton is not null)
        {
            SingleLayerHdrSaveAsButton.IsEnabled = isEnabled && ViewModel.HasImage;
        }

    }

    private async Task ExportCurrentCropAsync()
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!TryCalculateCropBounds(out var bounds))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 裁切失败: 裁切框没有覆盖图片");
            return;
        }

        switch (SelectedCropExportMode)
        {
            case CropExportMode.GainMapPreserve:
                await ExportPreservedGainMapCropAsync(bounds);
                return;
            case CropExportMode.UltraHdrConvert:
                await ExportUltraHdrConvertedCropAsync(bounds);
                return;
            case CropExportMode.SingleLayerHdr:
                await ExportSingleLayerHdrCropAsync(bounds);
                return;
            default:
                await ExportSdrPreviewCropAsync(bounds);
                return;
        }
    }

    private async Task ExportSdrPreviewCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.SdrPreview),
        };
        picker.FileTypeChoices.Add("PNG SDR 预览", [".png"]);
        picker.FileTypeChoices.Add("TIFF 16-bit SDR 预览", [".tif"]);
        picker.FileTypeChoices.Add("JPEG SDR 预览", [".jpg"]);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync("正在导出", $"正在写入 SDR 裁切: {outputFile.Path}");
            if (!progressStarted)
            {
                return;
            }

            CachedFileManager.DeferUpdates(outputFile);
            await using var inputStream = File.OpenRead(_currentDocument.Path);
            using var source = inputStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var transform = new BitmapTransform { Bounds = bounds };
            var exportFormat = GetSdrPreviewExportFormat(outputFile.FileType);
            UpdateExportProgress("正在解码裁切区域");
            var pixelData = await decoder.GetPixelDataAsync(
                exportFormat.PixelFormat,
                exportFormat.AlphaMode,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            UpdateExportProgress($"正在编码 {exportFormat.DisplayName}");
            await using var output = await outputFile.OpenStreamForWriteAsync();
            output.SetLength(0);
            using var destination = output.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(exportFormat.EncoderId, destination);
            encoder.SetPixelData(
                exportFormat.PixelFormat,
                exportFormat.AlphaMode,
                bounds.Width,
                bounds.Height,
                decoder.DpiX,
                decoder.DpiY,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();
            await CachedFileManager.CompleteUpdatesAsync(outputFile);

            SetCropMode(false);
            var hdrNote = IsHdrCropExportPreview(_currentDocument)
                ? "; 注意: 当前导出为 SDR 预览裁切，不保留 HLG/PQ/gain-map HDR 元数据"
                : string.Empty;
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已导出裁切 {exportFormat.DisplayName}: {outputFile.Path}{hdrNote}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 裁切导出失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private async Task ExportPreservedGainMapCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (_currentDocument.GainMapProbe?.IsRenderableUltraHdr != true)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; Gain-map 保真裁切不可用: 只支持已经包含可渲染 JPEG gain-map 的图片。");
            return;
        }

        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; Gain-map 保真裁切暂未接入可写后端: 需要 libultrahdr scenario 4 重新封装 base + gain map。裁切区域 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} 已计算；未弹保存框、未生成文件。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.GainMapPreserve),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync("正在导出", $"正在保真封装 gain-map 裁切: {outputFile.Path}");
            if (!progressStarted)
            {
                return;
            }

            await DeleteUnwrittenPickerFileAsync(outputFile);
            UpdateExportProgress("正在裁切 base 与 gain-map，并调用 libultrahdr 封装");
            var exportSummary = await GainMapHdrExportService.ExportPreservedJpegGainMapCropAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                _lifetime.Token);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已导出 Gain-map 保真裁切: {outputFile.Path}; {exportSummary}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; Gain-map 保真裁切失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private async Task ExportUltraHdrConvertedCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!CanExportGainMapHdr(_currentDocument))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 转为 Ultra HDR 不可用: 当前文件没有可重建的 gain-map 或单层 HDR 数据。");
            return;
        }

        var sourceKind = DescribeGainMapExportSource(_currentDocument);
        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 转为 Ultra HDR 暂未接入可写后端: {sourceKind} 需要 libultrahdr/libavif/libheif 写出 gain-map metadata。裁切区域 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} 已计算；未弹保存框、未生成文件。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.UltraHdrConvert),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync("正在导出", $"正在生成 Ultra HDR: {outputFile.Path}");
            if (!progressStarted)
            {
                return;
            }

            await DeleteUnwrittenPickerFileAsync(outputFile);
            UpdateExportProgress($"正在编码 {DescribeUltraHdrGainMapChannelMode(SelectedUltraHdrGainMapChannelMode)} gain-map JPEG");
            var exportSummary = await GainMapHdrExportService.ExportJpegUltraHdrAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                new UltraHdrExportOptions(SelectedUltraHdrGainMapChannelMode, SelectedUltraHdrSdrBaseColorGamut),
                _lifetime.Token);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已转为 Ultra HDR: {outputFile.Path}; {exportSummary}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 转为 Ultra HDR 失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private async Task ExportSingleLayerHdrCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.SingleLayer)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 单层 HDR 裁切暂未接入可写后端: 需要 native libjxl/libavif/libheif 工具。裁切区域 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} 已计算；未弹保存框、未生成文件。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = _isCropModeEnabled
                ? CreateCropSuggestedFileName(_currentDocument, CropExportMode.SingleLayerHdr)
                : CreateSaveAsSuggestedFileName(_currentDocument, "single-layer-hdr"),
        };
        AddAvailableExportChoices(picker, HdrExportMode.SingleLayer);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            var transfer = SelectedCropHdrTransfer;
            progressStarted = await TryBeginExportProgressAsync("正在导出", $"正在生成单层 HDR {DescribeCropHdrTransfer(transfer)}: {outputFile.Path}");
            if (!progressStarted)
            {
                return;
            }

            await DeleteUnwrittenPickerFileAsync(outputFile);
            var exportTransfer = transfer == CropHdrTransfer.Hlg
                ? SingleLayerHdrExportTransfer.Hlg
                : SingleLayerHdrExportTransfer.Pq;
            UpdateExportProgress("正在写入临时 16-bit HDR PNG 并调用原生编码器");
            var exportSummary = await SingleLayerHdrExportService.ExportAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                exportTransfer,
                _lifetime.Token);

            SetCropMode(false);
            var transferLabel = transfer == CropHdrTransfer.Hlg ? "HLG" : "PQ";
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 已导出单层 HDR {transferLabel}: {outputFile.Path}; {exportSummary}; {bounds.Width}x{bounds.Height}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 单层 HDR 裁切导出失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private async Task ExportSingleLayerHdrSaveAsAsync()
    {
        if (_currentDocument is null)
        {
            return;
        }

        var options = await PickSaveAsExportOptionsAsync(_currentDocument);
        if (options is null)
        {
            return;
        }

        await ExportCurrentImageSaveAsAsync(_currentDocument, options.Value);
    }

    private async Task<SaveAsExportOptions?> PickSaveAsExportOptionsAsync(HdrImageDocument document)
    {
        var modes = new List<SaveAsExportMode>();
        if (CanExportGainMapHdr(document))
        {
            modes.Add(SaveAsExportMode.SingleLayerHdr);
            modes.Add(SaveAsExportMode.UltraHdrConvert);
        }

        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            modes.Add(SaveAsExportMode.GainMapPreserve);
        }

        if (modes.Count == 0)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 另存为 HDR 不可用: 当前文件没有可重建的 gain-map 或单层 HDR 数据。");
            return null;
        }

        var modeSelector = new ComboBox
        {
            Header = "导出模式",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320,
        };
        foreach (var mode in modes)
        {
            modeSelector.Items.Add(new ComboBoxItem
            {
                Content = DescribeSaveAsExportMode(mode),
                Tag = mode,
            });
        }

        modeSelector.SelectedIndex = 0;

        var transferSelector = new ComboBox
        {
            Header = "单层 HDR 曲线",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        transferSelector.Items.Add(new ComboBoxItem { Content = "PQ HDR10" });
        transferSelector.Items.Add(new ComboBoxItem { Content = "HLG" });

        var hlgPeakBox = new NumberBox
        {
            Header = "HLG 目标峰值",
            Minimum = 400,
            Maximum = 1000,
            SmallChange = 100,
            LargeChange = 500,
            Value = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var gainMapWeightBox = new NumberBox
        {
            Header = "Gain-map 重建强度 (%)",
            Minimum = 0,
            Maximum = 100,
            SmallChange = 5,
            LargeChange = 25,
            Value = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var autoGainMapWeightCheckBox = new CheckBox
        {
            Content = "自动计算 Gain-map 重建强度",
            IsChecked = true,
        };
        var autoGainMapWeightText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var ultraHdrGainMapModeSelector = new ComboBox
        {
            Header = "Ultra HDR Gain-map",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        ultraHdrGainMapModeSelector.Items.Add(new ComboBoxItem { Content = "单色 Gain-map" });
        ultraHdrGainMapModeSelector.Items.Add(new ComboBoxItem { Content = "RGB Gain-map" });
        var ultraHdrBaseGamutSelector = new ComboBox
        {
            Header = "Ultra HDR SDR base 色域",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        ultraHdrBaseGamutSelector.Items.Add(new ComboBoxItem { Content = "自动匹配源" });
        ultraHdrBaseGamutSelector.Items.Add(new ComboBoxItem { Content = "BT.709 / sRGB" });
        ultraHdrBaseGamutSelector.Items.Add(new ComboBoxItem { Content = "Display P3" });
        ultraHdrBaseGamutSelector.Items.Add(new ComboBoxItem { Content = "BT.2020 / Rec.2100" });

        modeSelector.SelectionChanged += (_, _) =>
        {
            UpdateOptionVisibility();
        };
        transferSelector.SelectionChanged += (_, _) => UpdateOptionVisibility();
        ultraHdrGainMapModeSelector.SelectionChanged += (_, _) => UpdateOptionVisibility();
        ultraHdrBaseGamutSelector.SelectionChanged += (_, _) => UpdateOptionVisibility();
        hlgPeakBox.ValueChanged += (_, _) => UpdateAutoGainMapWeightText();
        autoGainMapWeightCheckBox.Checked += (_, _) => UpdateOptionVisibility();
        autoGainMapWeightCheckBox.Unchecked += (_, _) => UpdateOptionVisibility();

        var panel = new StackPanel
        {
            Spacing = 12,
        };
        panel.Children.Add(modeSelector);
        panel.Children.Add(transferSelector);
        panel.Children.Add(hlgPeakBox);
        panel.Children.Add(ultraHdrGainMapModeSelector);
        panel.Children.Add(ultraHdrBaseGamutSelector);
        panel.Children.Add(autoGainMapWeightCheckBox);
        panel.Children.Add(autoGainMapWeightText);
        panel.Children.Add(gainMapWeightBox);
        UpdateOptionVisibility();

        var dialog = new ContentDialog
        {
            Title = "另存为 HDR",
            Content = panel,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return new SaveAsExportOptions(
            GetSelectedMode(),
            transferSelector.SelectedIndex == 1 ? CropHdrTransfer.Hlg : CropHdrTransfer.Pq,
            Math.Clamp((float)(double.IsNaN(hlgPeakBox.Value) ? 1000.0 : hlgPeakBox.Value), 400.0f, 1000.0f),
            Math.Clamp((float)(double.IsNaN(gainMapWeightBox.Value) ? 100.0 : gainMapWeightBox.Value) / 100.0f, 0.0f, 1.0f),
            autoGainMapWeightCheckBox.IsChecked == true,
            ultraHdrGainMapModeSelector.SelectedIndex == 1 ? UltraHdrGainMapChannelMode.Rgb : UltraHdrGainMapChannelMode.Monochrome,
            GetSelectedUltraHdrBaseGamut());

        SaveAsExportMode GetSelectedMode()
        {
            return modeSelector.SelectedItem is ComboBoxItem { Tag: SaveAsExportMode mode }
                ? mode
                : SaveAsExportMode.SingleLayerHdr;
        }

        void UpdateOptionVisibility()
        {
            var selectedMode = GetSelectedMode();
            var isSingleLayer = selectedMode == SaveAsExportMode.SingleLayerHdr;
            var isUltraHdrConvert = selectedMode == SaveAsExportMode.UltraHdrConvert;
            transferSelector.Visibility = isSingleLayer ? Visibility.Visible : Visibility.Collapsed;
            hlgPeakBox.Visibility = isSingleLayer && transferSelector.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            ultraHdrGainMapModeSelector.Visibility = isUltraHdrConvert ? Visibility.Visible : Visibility.Collapsed;
            ultraHdrBaseGamutSelector.Visibility = isUltraHdrConvert ? Visibility.Visible : Visibility.Collapsed;
            var showGainMapWeight = isSingleLayer && document.GainMapProbe?.IsRenderableUltraHdr == true;
            autoGainMapWeightCheckBox.Visibility = showGainMapWeight
                ? Visibility.Visible
                : Visibility.Collapsed;
            autoGainMapWeightText.Visibility = showGainMapWeight && autoGainMapWeightCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            gainMapWeightBox.Visibility = showGainMapWeight && autoGainMapWeightCheckBox.IsChecked != true
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateAutoGainMapWeightText();
        }

        void UpdateAutoGainMapWeightText()
        {
            if (document.GainMapProbe?.IsRenderableUltraHdr != true)
            {
                autoGainMapWeightText.Text = string.Empty;
                return;
            }

            var estimate = SingleLayerHdrExportService.EstimatePreviewGainMapWeight(
                document,
                CalculateCurrentPreviewDisplayBoostLog2());
            autoGainMapWeightText.Text = estimate is { } value
                ? $"自动参考: {value * 100.0f:0}%（匹配当前预览）"
                : "自动参考: 无法从 metadata 估算，导出时使用当前预览参数";
        }

        UltraHdrSdrBaseColorGamut GetSelectedUltraHdrBaseGamut()
        {
            return ultraHdrBaseGamutSelector.SelectedIndex switch
            {
                1 => UltraHdrSdrBaseColorGamut.Bt709,
                2 => UltraHdrSdrBaseColorGamut.DisplayP3,
                3 => UltraHdrSdrBaseColorGamut.Bt2100,
                _ => UltraHdrSdrBaseColorGamut.Auto,
            };
        }
    }

    private async Task ExportCurrentImageSaveAsAsync(
        HdrImageDocument document,
        SaveAsExportOptions options)
    {
        var mode = options.Mode;
        var transfer = options.Transfer;
        var exportMode = mode == SaveAsExportMode.SingleLayerHdr ? HdrExportMode.SingleLayer : HdrExportMode.GainMap;
        var availableChoices = HdrExportBackendCatalog.GetChoices(exportMode)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; {DescribeSaveAsExportMode(mode)} 暂未接入可写后端。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateSaveAsSuggestedFileName(document, GetSaveAsSuffix(mode)),
        };
        AddAvailableExportChoices(picker, exportMode);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            var progressDetail = mode switch
            {
                SaveAsExportMode.SingleLayerHdr => $"正在生成单层 HDR {DescribeCropHdrTransfer(transfer)}: {outputFile.Path}",
                SaveAsExportMode.GainMapPreserve => $"正在复制原始 gain-map JPEG: {outputFile.Path}",
                _ => $"正在生成 Ultra HDR {DescribeUltraHdrGainMapChannelMode(options.UltraHdrGainMapChannelMode)} gain-map / base {DescribeUltraHdrSdrBaseColorGamut(options.UltraHdrSdrBaseColorGamut, document)}: {outputFile.Path}",
            };
            progressStarted = await TryBeginExportProgressAsync("正在导出", progressDetail);
            if (!progressStarted)
            {
                return;
            }

            await DeleteUnwrittenPickerFileAsync(outputFile);
            string exportSummary;
            if (mode == SaveAsExportMode.SingleLayerHdr)
            {
                var exportTransfer = transfer == CropHdrTransfer.Hlg
                    ? SingleLayerHdrExportTransfer.Hlg
                    : SingleLayerHdrExportTransfer.Pq;
                UpdateExportProgress("正在写入全尺寸临时 16-bit HDR PNG 并调用原生编码器");
                exportSummary = await SingleLayerHdrExportService.ExportAsync(
                    document,
                    outputFile.Path,
                    exportTransfer,
                    CreateSingleLayerSaveAsOptions(options),
                    _lifetime.Token);
            }
            else if (mode == SaveAsExportMode.GainMapPreserve)
            {
                if (document.GainMapProbe?.IsRenderableUltraHdr != true)
                {
                    throw new InvalidOperationException("Gain-map 保真另存为只支持已经包含可渲染 JPEG gain-map 的图片。");
                }

                UpdateExportProgress("正在复制原始 JPEG gain-map bitstream");
                File.Copy(document.Path, outputFile.Path, overwrite: true);
                exportSummary = "preserved original JPEG gain-map bitstream";
            }
            else
            {
                UpdateExportProgress($"正在编码 {DescribeUltraHdrGainMapChannelMode(options.UltraHdrGainMapChannelMode)} gain-map JPEG");
                exportSummary = await GainMapHdrExportService.ExportJpegUltraHdrAsync(
                    document,
                    outputFile.Path,
                    new UltraHdrExportOptions(options.UltraHdrGainMapChannelMode, options.UltraHdrSdrBaseColorGamut),
                    _lifetime.Token);
            }

            var transferLabel = transfer == CropHdrTransfer.Hlg ? "HLG" : "PQ";
            var modeLabel = mode == SaveAsExportMode.SingleLayerHdr
                ? $"{DescribeSaveAsExportMode(mode)} {transferLabel}"
                : DescribeSaveAsExportMode(mode);
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 已另存为 {modeLabel}: {outputFile.Path}; {exportSummary}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; HDR 另存为失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private static string DescribeSaveAsExportMode(SaveAsExportMode mode)
    {
        return mode switch
        {
            SaveAsExportMode.UltraHdrConvert => "转为 Ultra HDR / gain-map JPEG",
            SaveAsExportMode.GainMapPreserve => "Gain-map 保真另存为",
            _ => "单层 HDR",
        };
    }

    private static string GetSaveAsSuffix(SaveAsExportMode mode)
    {
        return mode switch
        {
            SaveAsExportMode.UltraHdrConvert => "ultra-hdr",
            SaveAsExportMode.GainMapPreserve => "gainmap-preserve",
            _ => "single-layer-hdr",
        };
    }

    private SingleLayerHdrExportOptions CreateSingleLayerSaveAsOptions(SaveAsExportOptions options)
    {
        var matchGainMapPreview = _currentDocument?.GainMapProbe?.IsRenderableUltraHdr == true
            && options.AutoGainMapWeight;
        var displayConfiguration = _renderer.DisplayConfiguration;
        return new SingleLayerHdrExportOptions(
            options.HlgPeakNits,
            options.GainMapWeight,
            options.AutoGainMapWeight,
            matchGainMapPreview,
            CalculateCurrentPreviewDisplayBoostLog2(),
            displayConfiguration.SceneToSdrWhiteScale,
            CalculateCurrentPreviewMaxSceneValue());
    }

    private float CalculateCurrentPreviewDisplayBoostLog2()
    {
        return GetSelectedHdrViewMode() switch
        {
            GainmapViewMode.Sdr => 0.0f,
            GainmapViewMode.AlternateImage => 16.0f,
            _ => HdrHeadroomModeSelector?.SelectedIndex == 1
                ? CalculateManualDisplayCapacityStops()
                : _renderer.DisplayConfiguration.MaxDisplayBoostLog2,
        };
    }

    private float CalculateCurrentPreviewMaxSceneValue()
    {
        return GetSelectedHdrViewMode() == GainmapViewMode.Sdr
            ? Math.Max(_renderer.DisplayConfiguration.SceneToSdrWhiteScale, 1.0f)
            : HdrHeadroomModeSelector?.SelectedIndex == 1
                ? 0.0f
                : _renderer.DisplayConfiguration.MaxSceneValue;
    }

    private static void AddAvailableExportChoices(FileSavePicker picker, HdrExportMode mode)
    {
        var choices = HdrExportBackendCatalog.GetChoices(mode)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        foreach (var choice in choices)
        {
            picker.FileTypeChoices.Add($"{choice.DisplayName} ({choice.Backend})", [choice.Extension]);
        }

        if (choices.Length == 0)
        {
            var fallback = mode == HdrExportMode.GainMap
                ? HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jpg")
                : HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jxl");
            picker.FileTypeChoices.Add($"{fallback.DisplayName} ({fallback.Backend})", [fallback.Extension]);
        }
    }

    private string BuildCropExportModeStatus()
    {
        return SelectedCropExportMode switch
        {
            CropExportMode.GainMapPreserve => "导出模式: Gain-map 保真裁切，裁切 base/gainmap 并保留原映射参数",
            CropExportMode.UltraHdrConvert => $"导出模式: 转为 Ultra HDR，从重建 HDR 重新生成 SDR base、{DescribeUltraHdrGainMapChannelMode(SelectedUltraHdrGainMapChannelMode)} gainmap 和 metadata",
            CropExportMode.SingleLayerHdr => $"导出模式: 单层 HDR 转换，目标 {DescribeCropHdrTransfer(SelectedCropHdrTransfer)}；JXL/AVIF/HEIF metadata 会写入对应 transfer",
            _ => "导出模式: SDR 预览，使用系统 BitmapEncoder 输出 sRGB 裁切图",
        };
    }

    private static string DescribeUltraHdrGainMapChannelMode(UltraHdrGainMapChannelMode mode)
    {
        return mode == UltraHdrGainMapChannelMode.Rgb ? "RGB" : "单色";
    }

    private static string DescribeUltraHdrSdrBaseColorGamut(
        UltraHdrSdrBaseColorGamut gamut,
        HdrImageDocument document)
    {
        var resolved = GainMapHdrExportService.ResolveSdrBaseColorGamut(gamut, document);
        return gamut == UltraHdrSdrBaseColorGamut.Auto
            ? $"自动 ({GainMapHdrExportService.DescribeSdrBaseGamut(resolved)})"
            : GainMapHdrExportService.DescribeSdrBaseGamut(resolved);
    }

    private static bool CanExportGainMapHdr(HdrImageDocument document)
    {
        return document.GainMapProbe?.IsRenderableUltraHdr == true
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.HeifAvifProbe?.HasGainMapAuxiliary == true
            || document.Format.Kind is HdrImageKind.SingleLayerHdr;
    }

    private static string DescribeGainMapExportSource(HdrImageDocument document)
    {
        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            return "JPEG Ultra HDR / Apple HDRGainMap source";
        }

        if (document.HeifAvifProbe?.HasGainMapAuxiliary == true)
        {
            return "HEIF/AVIF gain-map auxiliary source";
        }

        if (document.HeifAvifProbe?.HasHdrTransfer == true || document.Format.Kind is HdrImageKind.SingleLayerHdr)
        {
            return "single-layer HDR source reconstructed to JPEG Ultra HDR";
        }

        return document.Format.DisplayName;
    }

    private static string DescribeCropHdrTransfer(CropHdrTransfer transfer)
    {
        return transfer == CropHdrTransfer.Hlg ? "HLG (ARIB STD-B67)" : "PQ HDR10 (SMPTE ST 2084)";
    }

    private static string CreateCropSuggestedFileName(HdrImageDocument document, CropExportMode mode)
    {
        var suffix = mode switch
        {
            CropExportMode.GainMapPreserve => "crop-gainmap-preserve",
            CropExportMode.UltraHdrConvert => "crop-ultra-hdr",
            CropExportMode.SingleLayerHdr => "crop-single-layer-hdr",
            _ => IsHdrCropExportPreview(document) ? "crop-sdr-preview" : "crop",
        };
        return $"{Path.GetFileNameWithoutExtension(document.FileName)}-{suffix}";
    }

    private static string CreateSaveAsSuggestedFileName(HdrImageDocument document, string suffix)
    {
        return $"{Path.GetFileNameWithoutExtension(document.FileName)}-{suffix}";
    }

    private static bool IsHdrCropExportPreview(HdrImageDocument document)
    {
        return document.Format.Kind is HdrImageKind.GainMap or HdrImageKind.SingleLayerHdr
            || document.GainMapProbe?.IsRenderableUltraHdr == true
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.HeifAvifProbe?.HasGainMapAuxiliary == true;
    }

    private static CropExportFormat GetSdrPreviewExportFormat(string fileType)
    {
        return fileType.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new CropExportFormat(
                BitmapEncoder.JpegEncoderId,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                "JPEG SDR 预览"),
            ".tif" or ".tiff" => new CropExportFormat(
                BitmapEncoder.TiffEncoderId,
                BitmapPixelFormat.Rgba16,
                BitmapAlphaMode.Premultiplied,
                "TIFF 16-bit SDR 预览"),
            _ => new CropExportFormat(
                BitmapEncoder.PngEncoderId,
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Premultiplied,
                "PNG SDR 预览"),
        };
    }

    private static async Task DeleteUnwrittenPickerFileAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // Best-effort cleanup: the important part is that unsupported HDR exports never write fake image data.
        }
    }

    private static bool HasExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, fileName)))
                {
                    return true;
                }
            }
            catch
            {
                continue;
            }
        }

        return false;
    }

    private enum CropHdrTransfer
    {
        Pq,
        Hlg,
    }

    private sealed record CropExportFormat(
        Guid EncoderId,
        BitmapPixelFormat PixelFormat,
        BitmapAlphaMode AlphaMode,
        string DisplayName);

    private readonly record struct SaveAsExportOptions(
        SaveAsExportMode Mode,
        CropHdrTransfer Transfer,
        float HlgPeakNits,
        float GainMapWeight,
        bool AutoGainMapWeight,
        UltraHdrGainMapChannelMode UltraHdrGainMapChannelMode,
        UltraHdrSdrBaseColorGamut UltraHdrSdrBaseColorGamut);
}
