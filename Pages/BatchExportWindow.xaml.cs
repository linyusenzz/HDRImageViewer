using HdrImageViewer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace HdrImageViewer.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "RunAsync disposes the task cancellation source in finally; closing the window cancels it.")]
public sealed partial class BatchExportWindow : Window
{
    private static readonly HashSet<string> s_supportedExtensions =
        DecoderCatalog.FileTypeFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly BatchExportController _queue = new();
    private readonly Dictionary<BatchExportItem, BatchExportState> _observedStates = new();
    private CancellationTokenSource? _cancellation;
    private string? _directory;
    private int _pendingCount;
    private int _completedCount;
    private int _failedCount;
    private int _canceledCount;
    private bool _closed;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public BatchExportWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(BatchTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = Localization.GetString("BatchExportTitle");
        BatchTitleBar.Title = Localization.GetString("BatchExportTitle");
        FolderText.Text = Localization.GetString("BatchExportSelectFolder");

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(AddFilesButton, Localization.GetString("BatchExportAddToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddFilesButton, Localization.GetString("BatchExportAddToolTip"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(RemoveButton, Localization.GetString("BatchExportRemoveToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RemoveButton, Localization.GetString("BatchExportRemoveToolTip"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ClearButton, Localization.GetString("BatchExportClearToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ClearButton, Localization.GetString("BatchExportClearToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QueueList, Localization.GetString("BatchExportQueueList"));

        BatchFormatItemSdrPng.Content = Localization.GetString("BatchFormatSdrPng");
        BatchFormatItemSdrJpeg.Content = Localization.GetString("BatchFormatSdrJpeg");
        BatchFormatItemGainMapJpeg.Content = Localization.GetString("BatchFormatGainMapJpeg");
        BatchFormatItemHdrPq.Content = Localization.GetString("BatchFormatHdrPq");
        BatchFormatItemHdrHlg.Content = Localization.GetString("BatchFormatHdrHlg");
        BatchFormatItemHdrTiff.Content = Localization.GetString("BatchFormatHdrTiff");
        BatchFormatItemOpenExr.Content = Localization.GetString("BatchFormatOpenExr");
        BatchFormatItemOriginal.Content = Localization.GetString("BatchFormatOriginal");
        BatchFormatItemGainMapHeic.Content = Localization.GetString("BatchFormatGainMapHeic");
        BatchFormatItemGainMapAvif.Content = Localization.GetString("BatchFormatGainMapAvif");

        var scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(880 * scale), (int)(720 * scale)));
        QueueList.ItemsSource = _queue.Items;

        AppSettingsService.SettingsChanged += AppSettingsService_SettingsChanged;
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        ApplyTheme(AppSettingsService.Current.Theme);
        Closed += BatchExportWindow_Closed;

        UpdateFormatNote();
        RebuildSummaryCountsAndRender();
    }

    private void BatchExportWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        AppSettingsService.SettingsChanged -= AppSettingsService_SettingsChanged;
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        _cancellation?.Cancel();
    }

    private void AppSettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTheme(AppSettingsService.Current.Theme);
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed)
                {
                    ApplyTheme(AppSettingsService.Current.Theme);
                }
            });
        }
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args) => UpdateCaptionTheme();

    private void ApplyTheme(AppTheme theme)
    {
        WindowRoot.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionTheme();
    }

    private void UpdateCaptionTheme()
    {
        AppWindow.TitleBar.PreferredTheme =
            WindowRoot.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    private void WindowRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!_queue.IsRunning && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Localization.GetString("BatchExportAddToolTip");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void WindowRoot_Drop(object sender, DragEventArgs e)
    {
        if (_closed || _queue.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            var droppedItems = await e.DataView.GetStorageItemsAsync();
            if (_closed || _queue.IsRunning)
            {
                return;
            }

            var filePaths = new List<string>();
            var folderPaths = new List<string>();
            foreach (var storageItem in droppedItems)
            {
                if (storageItem is StorageFile file && IsSupportedImagePath(file.Path))
                {
                    filePaths.Add(file.Path);
                }
                else if (storageItem is StorageFolder folder && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    folderPaths.Add(folder.Path);
                }
            }

            if (folderPaths.Count > 0)
            {
                var enumerated = await Task.Run(() => CollectSupportedImagesFromFolders(folderPaths));
                filePaths.AddRange(enumerated);
            }

            if (_closed || _queue.IsRunning)
            {
                return;
            }

            var distinctPaths = filePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctPaths.Length == 0)
            {
                SummaryText.Text = Localization.GetString("StatusDragDropNoSupportedFiles");
                return;
            }

            _queue.Add(distinctPaths);
            RebuildSummaryCountsAndRender();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SummaryText.Text = ex.Message;
            }
        }
    }

    private static List<string> CollectSupportedImagesFromFolders(IReadOnlyList<string> folderPaths)
    {
        var collected = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };

        foreach (var folder in folderPaths)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var filePath in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (IsSupportedImagePath(filePath))
                    {
                        collected.Add(filePath);
                    }
                }
            }
            catch
            {
            }
        }

        return collected;
    }

    private static bool IsSupportedImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && s_supportedExtensions.Contains(extension);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            foreach (var fileType in DecoderCatalog.FileTypeFilter)
            {
                picker.FileTypeFilter.Add(fileType);
            }

            var results = await picker.PickMultipleFilesAsync();
            if (_closed)
            {
                return;
            }

            _queue.Add(results.Select(file => file.Path));
            RebuildSummaryCountsAndRender();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SummaryText.Text = ex.Message;
            }
        }
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
            if (_closed)
            {
                return;
            }

            if (result is not null)
            {
                _directory = result.Path;
                var displayName = Path.GetFileName(result.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                FolderText.Text = string.IsNullOrWhiteSpace(displayName) ? result.Path : displayName;
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(FolderButton, result.Path);
                RenderSummaryFromCounts();
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SummaryText.Text = ex.Message;
            }
        }
    }

    private void Format_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        UpdateFormatNote();
    }

    private void UpdateFormatNote()
    {
        if (FormatNoteText is null || FormatSelector is null)
        {
            return;
        }

        FormatNoteText.Text = FormatSelector.SelectedIndex switch
        {
            0 or 1 => Localization.GetString("BatchFormatNoteSdr"),
            2 or 8 or 9 => Localization.GetString("BatchFormatNoteGainMap"),
            3 => Localization.GetString("BatchFormatNotePq"),
            4 => Localization.GetString("BatchFormatNoteHlg"),
            5 or 6 => Localization.GetString("BatchFormatNoteFloat"),
            _ => Localization.GetString("BatchFormatNoteOriginal")
        };
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in QueueList.SelectedItems.Cast<BatchExportItem>().ToArray())
        {
            _ = _queue.Items.Remove(item);
        }

        RebuildSummaryCountsAndRender();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _queue.Items.Clear();
        RebuildSummaryCountsAndRender();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SummaryText.Text = ex.Message;
            }
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _queue.RetryUnfinished();
            RebuildSummaryCountsAndRender();
            await RunAsync();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                SummaryText.Text = ex.Message;
            }
        }
    }

    private async Task RunAsync()
    {
        if (_queue.IsRunning)
        {
            return;
        }

        if (_directory is null)
        {
            SummaryText.Text = Localization.GetString("BatchExportSelectFolderPrompt");
            return;
        }

        RebuildSummaryCountsAndRender();
        if (_pendingCount == 0)
        {
            return;
        }

        var format = FormatSelector.SelectedIndex;
        var directory = _directory;
        _cancellation = new CancellationTokenSource();
        var subscribedItems = _queue.Items.ToArray();
        SetRunning(true);
        foreach (var item in subscribedItems)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }

        try
        {
            await _queue.RunAsync((item, token) => BatchImageExportService.ExportAsync(item, directory, format, token), _cancellation.Token);
        }
        finally
        {
            foreach (var item in subscribedItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }

            _cancellation.Dispose();
            _cancellation = null;
            if (!_closed)
            {
                SetRunning(false);
                RebuildSummaryCountsAndRender();
            }
        }
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_closed
            || (e.PropertyName is not null && e.PropertyName != nameof(BatchExportItem.State))
            || sender is not BatchExportItem item)
        {
            return;
        }

        var currentState = item.State;
        if (_observedStates.TryGetValue(item, out var previousState))
        {
            if (previousState == currentState)
            {
                return;
            }

            AdjustStateCount(previousState, -1);
        }

        _observedStates[item] = currentState;
        AdjustStateCount(currentState, 1);
        RenderSummaryFromCounts();
    }

    private void AdjustStateCount(BatchExportState state, int delta)
    {
        switch (state)
        {
            case BatchExportState.Pending:
                _pendingCount += delta;
                break;
            case BatchExportState.Completed:
                _completedCount += delta;
                break;
            case BatchExportState.Failed:
                _failedCount += delta;
                break;
            case BatchExportState.Canceled:
                _canceledCount += delta;
                break;
        }
    }

    private void SetRunning(bool running)
    {
        AddFilesButton.IsEnabled = RemoveButton.IsEnabled = ClearButton.IsEnabled = FolderButton.IsEnabled =
            FormatSelector.IsEnabled = StartButton.IsEnabled = RetryButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        QueueProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildSummaryCountsAndRender()
    {
        _observedStates.Clear();
        _pendingCount = 0;
        _completedCount = 0;
        _failedCount = 0;
        _canceledCount = 0;

        foreach (var item in _queue.Items)
        {
            var state = item.State;
            _observedStates[item] = state;
            AdjustStateCount(state, 1);
        }

        RenderSummaryFromCounts();
    }

    private void RenderSummaryFromCounts()
    {
        var total = _queue.Items.Count;
        var completed = _completedCount;
        var failed = _failedCount;
        var canceled = _canceledCount;

        FileCountText.Text = total == 0
            ? Localization.GetString("BatchExportFileCountNone")
            : Localization.GetString("BatchExportFileCountFormat", total);
        QueueEmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = total == 0
            ? Localization.GetString("BatchExportReady")
            : Localization.GetString("BatchExportProgressFormat", completed, total)
              + (failed > 0 ? Localization.GetString("BatchExportFailedSuffix", failed) : string.Empty)
              + (canceled > 0 ? Localization.GetString("BatchExportCanceledSuffix", canceled) : string.Empty);
        RetryButton.Visibility = !_queue.IsRunning && failed + canceled > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !_queue.IsRunning && _directory is not null && _pendingCount > 0;
        QueueProgress.Maximum = Math.Max(1, total);
        QueueProgress.Value = completed + failed + canceled;
    }
}
