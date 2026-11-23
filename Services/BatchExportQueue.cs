using System.Collections.ObjectModel;
using HdrImageViewer.Infrastructure;

namespace HdrImageViewer.Services;

public enum BatchExportState { Pending, Running, Completed, Failed, Canceled }

public sealed class BatchExportItem(string sourcePath) : ObservableObject
{
    private BatchExportState _state;
    private string _detail = Localization.GetString("BatchDetailWaiting");
    public string SourcePath { get; } = Path.GetFullPath(sourcePath);
    public string FileName => Path.GetFileName(SourcePath);
    public BatchExportState State
    {
        get => _state;
        internal set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(StateLabel)); }
    }
    public string StateLabel => State switch
    {
        BatchExportState.Running => Localization.GetString("BatchStateRunning"),
        BatchExportState.Completed => Localization.GetString("BatchStateCompleted"),
        BatchExportState.Failed => Localization.GetString("BatchStateFailed"),
        BatchExportState.Canceled => Localization.GetString("BatchStateCanceled"),
        _ => Localization.GetString("BatchStateWaiting")
    };
    public string Detail { get => _detail; internal set => SetProperty(ref _detail, value); }
}

public sealed class BatchExportController
{
    private static readonly AsyncLocal<PathReservationScope?> ActiveReservationScope = new();

    public ObservableCollection<BatchExportItem> Items { get; } = [];
    public bool IsRunning { get; private set; }
    public int MaxConcurrency { get; set; } = Math.Clamp(Environment.ProcessorCount / 8, 1, 2);

    public void Add(IEnumerable<string> paths)
    {
        if (IsRunning) throw new InvalidOperationException(Localization.GetString("BatchQueueRunningCannotAdd"));
        var existing = Items.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(Path.GetFullPath))
            if (existing.Add(path)) Items.Add(new BatchExportItem(path));
    }

    public void RetryUnfinished()
    {
        if (IsRunning) throw new InvalidOperationException(Localization.GetString("BatchQueueStillRunning"));
        foreach (var item in Items.Where(item => item.State is BatchExportState.Failed or BatchExportState.Canceled))
        { item.State = BatchExportState.Pending; item.Detail = Localization.GetString("BatchDetailWaitingRetry"); }
    }

    public async Task RunAsync(Func<BatchExportItem, CancellationToken, Task<string>> export, CancellationToken cancellationToken)
    {
        if (IsRunning) throw new InvalidOperationException(Localization.GetString("BatchQueueAlreadyRunning"));
        IsRunning = true;
        var previousScope = ActiveReservationScope.Value;
        ActiveReservationScope.Value = new PathReservationScope();
        try
        {
            var pending = Items.Where(item => item.State == BatchExportState.Pending).ToArray();
            var concurrency = Math.Max(1, MaxConcurrency);
            if (concurrency == 1 || pending.Length <= 1)
            {
                foreach (var item in pending)
                {
                    await ExecuteItemAsync(item, export, cancellationToken);
                }

                return;
            }

            using var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = new Task[pending.Length];
            for (var index = 0; index < pending.Length; index++)
            {
                tasks[index] = ExecuteBoundedItemAsync(pending[index], semaphore, export, cancellationToken);
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            ActiveReservationScope.Value = previousScope;
            IsRunning = false;
        }
    }

    private static async Task ExecuteBoundedItemAsync(
        BatchExportItem item,
        SemaphoreSlim semaphore,
        Func<BatchExportItem, CancellationToken, Task<string>> export,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            item.State = BatchExportState.Canceled;
            item.Detail = Localization.GetString("BatchDetailCanceledNotWritten");
            return;
        }

        try
        {
            await semaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.State = BatchExportState.Canceled;
            item.Detail = Localization.GetString("BatchDetailCanceledNotWritten");
            return;
        }

        try
        {
            await ExecuteItemAsync(item, export, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task ExecuteItemAsync(
        BatchExportItem item,
        Func<BatchExportItem, CancellationToken, Task<string>> export,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            item.State = BatchExportState.Canceled;
            item.Detail = Localization.GetString("BatchDetailCanceledNotWritten");
            return;
        }

        item.State = BatchExportState.Running;
        item.Detail = Localization.GetString("BatchDetailDecoding");
        try
        {
            var output = await export(item, cancellationToken);
            item.State = BatchExportState.Completed;
            item.Detail = Localization.GetString("BatchDetailCompletedFormat", output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.State = BatchExportState.Canceled;
            item.Detail = Localization.GetString("BatchDetailCanceled");
        }
        catch (Exception ex)
        {
            item.State = BatchExportState.Failed;
            item.Detail = Localization.GetString("BatchDetailFailedFormat", ex.Message);
        }
    }

    public static string ChooseOutputPath(string directory, string source, string extension)
    {
        var scope = ActiveReservationScope.Value;
        return ChooseOutputPath(directory, source, extension, scope?.ReservedPaths, scope?.SyncRoot);
    }

    public static string ChooseOutputPath(
        string directory,
        string source,
        string extension,
        ISet<string>? reservedPaths,
        object? syncRoot = null)
    {
        var name = Path.GetFileNameWithoutExtension(source) + "-export";
        if (reservedPaths is null)
        {
            for (var index = 1; ; index++)
            {
                var suffix = index == 1 ? string.Empty : $"-{index}";
                var path = Path.Combine(directory, name + suffix + extension);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    return path;
                }
            }
        }

        lock (syncRoot ?? reservedPaths)
        {
            for (var index = 1; ; index++)
            {
                var suffix = index == 1 ? string.Empty : $"-{index}";
                var path = Path.GetFullPath(Path.Combine(directory, name + suffix + extension));
                if (!File.Exists(path) && !Directory.Exists(path) && reservedPaths.Add(path))
                {
                    return path;
                }
            }
        }
    }

    private sealed class PathReservationScope
    {
        public object SyncRoot { get; } = new();
        public HashSet<string> ReservedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
