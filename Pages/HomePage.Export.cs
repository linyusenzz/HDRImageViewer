using HdrImageViewer.Rendering;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using HdrImageViewer.ViewModels;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
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
using Windows.Storage.Streams;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private CancellationTokenSource? _exportCancellation;
    private CancellationToken ExportToken => _exportCancellation?.Token ?? _lifetime.Token;

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        _exportCancellation?.Cancel();
        CancelExportButton.IsEnabled = false;
        UpdateExportProgress(Localization.GetString("StatusExportCancelling"));
    }

    private async Task<bool> TryBeginExportProgressAsync(string title, string detail)
    {
        if (_isExportInProgress)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusExportAlreadyInProgress")}");
            return false;
        }

        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancelExportButton.IsEnabled = true;
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
        SetExportProgress(Localization.GetString("ExportOverlayTitle.Text"), detail);
    }

    private void EndExportProgress()
    {
        _exportCancellation?.Dispose();
        _exportCancellation = null;
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

        var pixelWidth = (uint)Math.Max(0, _renderer.ContentPixelWidth);
        var pixelHeight = (uint)Math.Max(0, _renderer.ContentPixelHeight);
        if (SelectedCropExportMode == CropExportMode.SdrPreview)
        {
            // SDR display bypasses D3D. Use the same source and orientation as
            // the export decoder, never the renderer's empty or stale texture.
            await using var input = File.OpenRead(_currentDocument.Path);
            using var stream = input.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            pixelWidth = decoder.OrientedPixelWidth;
            pixelHeight = decoder.OrientedPixelHeight;
        }

        if (!TryCalculateCropBounds(out var bounds, pixelWidth, pixelHeight))
        {
            await ShowCropExportErrorAsync(Localization.GetString("ErrorCropBoundsInvalid"));
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

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.SdrPreview),
            DefaultFileExtension = ".png",
        };
        picker.FileTypeChoices.Add(Localization.GetString("CropFormatPngSdr"), [".png"]);
        picker.FileTypeChoices.Add(Localization.GetString("CropFormatTiffSdr"), [".tif"]);
        picker.FileTypeChoices.Add(Localization.GetString("CropFormatJpegSdr"), [".jpg"]);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync(Localization.GetString("ExportOverlayTitle.Text"), Localization.GetString("StatusWritingSdrCropFormat", outputFile.Path));
            if (!progressStarted)
            {
                return;
            }

            using var transaction = new ExportFileTransaction(outputFile.Path);
            ExportToken.ThrowIfCancellationRequested();
            await using var inputStream = File.OpenRead(_currentDocument.Path);
            using var source = inputStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var transform = new BitmapTransform { Bounds = bounds };
            var exportFormat = GetSdrPreviewExportFormat(outputFile.FileType);
            UpdateExportProgress(Localization.GetString("StatusDecodingCropRegion"));
            var pixelData = await decoder.GetPixelDataAsync(
                exportFormat.PixelFormat,
                exportFormat.AlphaMode,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            UpdateExportProgress(Localization.GetString("StatusEncodingCropFormat", exportFormat.DisplayName));
            ExportToken.ThrowIfCancellationRequested();
            await using (var output = new FileStream(transaction.TemporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
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
            }
            transaction.Commit(ExportToken);

            SetCropMode(false);
            var hdrNote = IsHdrCropExportPreview(_currentDocument)
                ? Localization.GetString("StatusCropSdrNotice")
                : string.Empty;
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusExportedCropFormat", exportFormat.DisplayName, outputFile.Path)}{hdrNote}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            await ShowCropExportErrorAsync(ex.Message);
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusGainMapCropUnavailable")}");
            return;
        }

        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusGainMapCropNoBackendFormat", bounds.X, bounds.Y, bounds.Width, bounds.Height, HdrExportBackendCatalog.BuildBackendSummary())}");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.GainMapPreserve),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync(Localization.GetString("ExportOverlayTitle.Text"), Localization.GetString("StatusPackagingGainMapCropFormat", outputFile.Path));
            if (!progressStarted)
            {
                return;
            }

            UpdateExportProgress(Localization.GetString("StatusCroppingBaseAndGainMap"));
            var exportSummary = await GainMapHdrExportService.ExportPreservedJpegGainMapCropAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                ExportToken);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusExportedGainMapCropFormat", outputFile.Path, exportSummary)}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusGainMapCropFailedFormat", ex.GetType().Name, ex.Message)}");
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusUltraHdrConvertUnavailable")}");
            return;
        }

        var sourceKind = DescribeGainMapExportSource(_currentDocument);
        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusUltraHdrNoBackendFormat", sourceKind, bounds.X, bounds.Y, bounds.Width, bounds.Height, HdrExportBackendCatalog.BuildBackendSummary())}");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.UltraHdrConvert),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync(Localization.GetString("ExportOverlayTitle.Text"), Localization.GetString("StatusGeneratingUltraHdrFormat", outputFile.Path));
            if (!progressStarted)
            {
                return;
            }

            UpdateExportProgress(Localization.GetString("StatusEncodingGainMapFormat", DescribeUltraHdrGainMapChannelMode(SelectedUltraHdrGainMapChannelMode)));
            var exportSummary = await GainMapHdrExportService.ExportJpegUltraHdrAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                new UltraHdrExportOptions(SelectedUltraHdrGainMapChannelMode, SelectedUltraHdrSdrBaseColorGamut),
                ExportToken);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusExportedUltraHdrFormat", outputFile.Path, exportSummary)}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusUltraHdrConvertFailedFormat", ex.GetType().Name, ex.Message)}");
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
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSingleLayerHdrNoBackendFormat", bounds.X, bounds.Y, bounds.Width, bounds.Height, HdrExportBackendCatalog.BuildBackendSummary())}");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = _isCropModeEnabled
                ? CreateCropSuggestedFileName(_currentDocument, CropExportMode.SingleLayerHdr)
                : CreateSaveAsSuggestedFileName(_currentDocument, "single-layer-hdr"),
        };
        AddAvailableExportChoices(picker, HdrExportMode.SingleLayer);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            var transfer = SelectedCropHdrTransfer;
            progressStarted = await TryBeginExportProgressAsync(Localization.GetString("ExportOverlayTitle.Text"), Localization.GetString("StatusGeneratingSingleLayerHdrFormat", DescribeCropHdrTransfer(transfer), outputFile.Path));
            if (!progressStarted)
            {
                return;
            }

            var exportTransfer = transfer == CropHdrTransfer.Hlg
                ? SingleLayerHdrExportTransfer.Hlg
                : SingleLayerHdrExportTransfer.Pq;
            UpdateExportProgress(Localization.GetString("StatusPreparingHdrPixels"));
            var exportSummary = await SingleLayerHdrExportService.ExportAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                exportTransfer,
                ExportToken);

            SetCropMode(false);
            var transferLabel = transfer == CropHdrTransfer.Hlg ? "HLG" : "PQ";
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusExportedSingleLayerHdrFormat", transferLabel, outputFile.Path, exportSummary, bounds.Width, bounds.Height)}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSingleLayerHdrExportFailedFormat", ex.GetType().Name, ex.Message)}");
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsHdrUnavailable")}");
            return null;
        }

        var modeSelector = new ComboBox
        {
            Header = Localization.GetString("SaveAsDialogExportMode"),
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
            Header = Localization.GetString("SaveAsDialogSingleLayerCurve"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        transferSelector.Items.Add(new ComboBoxItem { Content = "PQ HDR10" });
        transferSelector.Items.Add(new ComboBoxItem { Content = "HLG" });

        var hlgPeakBox = new NumberBox
        {
            Header = Localization.GetString("SaveAsDialogHlgPeak"),
            Minimum = 400,
            Maximum = 1000,
            SmallChange = 100,
            LargeChange = 500,
            Value = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var gainMapWeightBox = new NumberBox
        {
            Header = Localization.GetString("SaveAsDialogGainMapWeight"),
            Minimum = 0,
            Maximum = 100,
            SmallChange = 5,
            LargeChange = 25,
            Value = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var autoGainMapWeightCheckBox = new CheckBox
        {
            Content = Localization.GetString("SaveAsDialogAutoGainMapWeight"),
            IsChecked = true,
        };
        var autoGainMapWeightText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var ultraHdrGainMapModeSelector = new ComboBox
        {
            Header = Localization.GetString("SaveAsDialogUltraHdrGainMap"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        ultraHdrGainMapModeSelector.Items.Add(new ComboBoxItem { Content = Localization.GetString("SaveAsGainMapMonochrome") });
        ultraHdrGainMapModeSelector.Items.Add(new ComboBoxItem { Content = Localization.GetString("SaveAsGainMapRgb") });
        var ultraHdrBaseGamutSelector = new ComboBox
        {
            Header = Localization.GetString("SaveAsDialogUltraHdrSdrGamut"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        ultraHdrBaseGamutSelector.Items.Add(new ComboBoxItem { Content = Localization.GetString("SaveAsGamutAutoMatch") });
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
            Title = Localization.GetString("SaveAsDialogTitle"),
            Content = panel,
            PrimaryButtonText = Localization.GetString("SaveAsDialogContinue"),
            CloseButtonText = Localization.GetString("CropCancel"),
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
                ? Localization.GetString("SaveAsAutoEstimateFormat", $"{value * 100.0f:0}")
                : Localization.GetString("SaveAsAutoEstimateUnavailable");
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
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsNoBackendFormat", DescribeSaveAsExportMode(mode), HdrExportBackendCatalog.BuildBackendSummary())}");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateSaveAsSuggestedFileName(document, GetSaveAsSuffix(mode)),
        };
        AddAvailableExportChoices(picker, exportMode);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        var progressStarted = false;
        try
        {
            var progressDetail = mode switch
            {
                SaveAsExportMode.SingleLayerHdr => Localization.GetString("StatusGeneratingSingleLayerHdrFormat", DescribeCropHdrTransfer(transfer), outputFile.Path),
                SaveAsExportMode.GainMapPreserve => $"{Localization.GetString("StatusCopyingOriginalGainMapBitstream")}: {outputFile.Path}",
                _ => $"{Localization.GetString("StatusGeneratingUltraHdrFormat", outputFile.Path)} ({DescribeUltraHdrGainMapChannelMode(options.UltraHdrGainMapChannelMode)}, {DescribeUltraHdrSdrBaseColorGamut(options.UltraHdrSdrBaseColorGamut, document)})",
            };
            progressStarted = await TryBeginExportProgressAsync(Localization.GetString("ExportOverlayTitle.Text"), progressDetail);
            if (!progressStarted)
            {
                return;
            }

            string exportSummary;
            if (mode == SaveAsExportMode.SingleLayerHdr)
            {
                var exportTransfer = transfer == CropHdrTransfer.Hlg
                    ? SingleLayerHdrExportTransfer.Hlg
                    : SingleLayerHdrExportTransfer.Pq;
                UpdateExportProgress(Localization.GetString("StatusPreparingFullHdrPixels"));
                exportSummary = await SingleLayerHdrExportService.ExportAsync(
                    document,
                    outputFile.Path,
                    exportTransfer,
                    CreateSingleLayerSaveAsOptions(options),
                    ExportToken);
            }
            else if (mode == SaveAsExportMode.GainMapPreserve)
            {
                if (document.GainMapProbe?.IsRenderableUltraHdr != true)
                {
                    throw new InvalidOperationException(Localization.GetString("ExceptionGainMapPreserveNotSupported"));
                }

                UpdateExportProgress(Localization.GetString("StatusCopyingOriginalGainMapBitstream"));
                await ExportFileTransaction.CopyAsync(document.Path, outputFile.Path, ExportToken);
                exportSummary = "preserved original JPEG gain-map bitstream";
            }
            else
            {
                UpdateExportProgress(Localization.GetString("StatusEncodingGainMapFormat", DescribeUltraHdrGainMapChannelMode(options.UltraHdrGainMapChannelMode)));
                exportSummary = await GainMapHdrExportService.ExportJpegUltraHdrAsync(
                    document,
                    outputFile.Path,
                    new UltraHdrExportOptions(options.UltraHdrGainMapChannelMode, options.UltraHdrSdrBaseColorGamut),
                    ExportToken);
            }

            var transferLabel = transfer == CropHdrTransfer.Hlg ? "HLG" : "PQ";
            var modeLabel = mode == SaveAsExportMode.SingleLayerHdr
                ? $"{DescribeSaveAsExportMode(mode)} {transferLabel}"
                : DescribeSaveAsExportMode(mode);
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSavedAsFormat", modeLabel, outputFile.Path, exportSummary)}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsHdrFailedFormat", ex.GetType().Name, ex.Message)}");
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
            SaveAsExportMode.UltraHdrConvert => Localization.GetString("SaveAsModeUltraHdrConvert"),
            SaveAsExportMode.GainMapPreserve => Localization.GetString("SaveAsModeGainMapPreserve"),
            _ => Localization.GetString("SaveAsModeSingleLayerHdr"),
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

        if (choices.Length > 0)
        {
            picker.DefaultFileExtension = choices[0].Extension;
        }
        else
        {
            var fallback = mode == HdrExportMode.GainMap
                ? HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jpg")
                : HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jxl");
            picker.FileTypeChoices.Add($"{fallback.DisplayName} ({fallback.Backend})", [fallback.Extension]);
            picker.DefaultFileExtension = fallback.Extension;
        }
    }

    private string BuildCropExportModeStatus()
    {
        return SelectedCropExportMode switch
        {
            CropExportMode.GainMapPreserve => Localization.GetString("StatusCropModeGainMapPreserve"),
            CropExportMode.UltraHdrConvert => Localization.GetString("StatusCropModeUltraHdrConvertFormat", DescribeUltraHdrGainMapChannelMode(SelectedUltraHdrGainMapChannelMode)),
            CropExportMode.SingleLayerHdr => Localization.GetString("StatusCropModeSingleLayerHdrFormat", DescribeCropHdrTransfer(SelectedCropHdrTransfer)),
            _ => Localization.GetString("StatusCropModeSdrPreview"),
        };
    }

    private static string DescribeUltraHdrGainMapChannelMode(UltraHdrGainMapChannelMode mode)
    {
        return mode == UltraHdrGainMapChannelMode.Rgb ? "RGB" : Localization.GetString("SaveAsGainMapMonoLabel");
    }

    private static string DescribeUltraHdrSdrBaseColorGamut(
        UltraHdrSdrBaseColorGamut gamut,
        HdrImageDocument document)
    {
        var resolved = GainMapHdrExportService.ResolveSdrBaseColorGamut(gamut, document);
        return gamut == UltraHdrSdrBaseColorGamut.Auto
            ? Localization.GetString("GamutAutoFormat", GainMapHdrExportService.DescribeSdrBaseGamut(resolved))
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
                Localization.GetString("CropFormatJpegSdr")),
            ".tif" or ".tiff" => new CropExportFormat(
                BitmapEncoder.TiffEncoderId,
                BitmapPixelFormat.Rgba16,
                BitmapAlphaMode.Premultiplied,
                Localization.GetString("CropFormatTiffSdr")),
            _ => new CropExportFormat(
                BitmapEncoder.PngEncoderId,
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Premultiplied,
                Localization.GetString("CropFormatPngSdr")),
        };
    }

    private sealed record ExportDestination(string Path)
    {
        public string FileType => System.IO.Path.GetExtension(Path);
    }

    private static async Task<ExportDestination?> PickSaveDestinationAsync(FileSavePicker picker)
    {
        var result = await picker.PickSaveFileAsync();
        return result is null ? null : new ExportDestination(result.Path);
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
