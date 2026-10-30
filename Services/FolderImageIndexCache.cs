using System.Globalization;

namespace HdrImageViewer.Services;

internal sealed class FolderImageIndexCache : IDisposable
{
    private const int MaximumCachedDirectories = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, DirectoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public (List<string> Paths, int CurrentIndex) GetFolderImages(
        string currentPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return ([currentPath], 0);
        }

        DirectoryEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(directory, out entry!))
            {
                entry = new DirectoryEntry(directory, MarkDirty);
                _entries.Add(directory, entry);
            }

            entry.Touch();
        }

        List<string> snapshot;
        lock (entry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsDirty && entry.Paths is not null)
            {
                snapshot = entry.Paths;
            }
            else
            {
                snapshot = BuildSnapshot(directory, cancellationToken);
                entry.Paths = snapshot;
                entry.IsDirty = false;
                entry.EnsureWatcher();
            }
        }

        Trim(directory);
        var paths = new List<string>(snapshot);
        var currentIndex = paths.FindIndex(
            path => string.Equals(
                path,
                currentPath,
                StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            paths.Add(currentPath);
            paths.Sort(CompareByFileName);
            currentIndex = paths.FindIndex(
                path => string.Equals(
                    path,
                    currentPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        return (paths, Math.Max(currentIndex, 0));
    }

    public void InvalidatePath(string path)
    {
        var directory = Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        MarkDirty(directory);
    }

    public void Dispose()
    {
        List<DirectoryEntry> entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    private static List<string> BuildSnapshot(
        string directory,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var inspectedCount = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if ((inspectedCount++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (IsSupportedImagePath(path))
            {
                paths.Add(path);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        paths.Sort(CompareByFileName);
        return paths;
    }

    private static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return DecoderCatalog.FileTypeFilter.Any(
            filter => string.Equals(
                filter,
                extension,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int CompareByFileName(string left, string right)
    {
        var leftName = Path.GetFileName(left);
        var rightName = Path.GetFileName(right);
        var comparison = CultureInfo.CurrentCulture.CompareInfo.Compare(
            leftName,
            rightName,
            CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        // Keep equivalent numeric names (such as 1.jpg and 01.jpg) in a
        // deterministic order, independent of directory enumeration order.
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(leftName, rightName);
    }

    private void MarkDirty(string directory)
    {
        DirectoryEntry? entry;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(directory, out entry))
            {
                return;
            }
        }

        lock (entry.Gate)
        {
            entry.IsDirty = true;
        }
    }

    private void Trim(string activeDirectory)
    {
        List<DirectoryEntry> removed = [];
        lock (_gate)
        {
            if (_entries.Count <= MaximumCachedDirectories)
            {
                return;
            }

            foreach (var entry in _entries.Values
                         .Where(entry => !string.Equals(
                             entry.Directory,
                             activeDirectory,
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.LastAccessTicks)
                         .Take(_entries.Count - MaximumCachedDirectories)
                         .ToList())
            {
                if (_entries.Remove(entry.Directory))
                {
                    removed.Add(entry);
                }
            }
        }

        foreach (var entry in removed)
        {
            entry.Dispose();
        }
    }

    private sealed class DirectoryEntry : IDisposable
    {
        private readonly Action<string> _markDirty;
        private FileSystemWatcher? _watcher;
        private long _lastAccessTicks = Environment.TickCount64;

        public DirectoryEntry(
            string directory,
            Action<string> markDirty)
        {
            Directory = directory;
            _markDirty = markDirty;
        }

        public object Gate { get; } = new();

        public string Directory { get; }

        public List<string>? Paths { get; set; }

        public bool IsDirty { get; set; } = true;

        public long LastAccessTicks => Interlocked.Read(ref _lastAccessTicks);

        public void Touch()
        {
            Interlocked.Exchange(ref _lastAccessTicks, Environment.TickCount64);
        }

        public void EnsureWatcher()
        {
            if (_watcher is not null)
            {
                return;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(Directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch
            {
                watcher?.Dispose();
                _watcher = null;
            }
        }

        public void Dispose()
        {
            var watcher = Interlocked.Exchange(ref _watcher, null);
            if (watcher is null)
            {
                return;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            _markDirty(Directory);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            _markDirty(Directory);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            _markDirty(Directory);
        }
    }
}
