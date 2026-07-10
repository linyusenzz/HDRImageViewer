namespace HdrImageViewer.Presentation;

internal sealed class ImageLoadController : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _currentSource;
    private long _generation;
    private bool _isDisposed;

    public ImageLoadController(CancellationToken lifetimeToken)
    {
        _lifetimeToken = lifetimeToken;
    }

    public ImageLoadOperation Begin()
    {
        CancellationTokenSource? previous;
        ImageLoadOperation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            previous = _currentSource;
            var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _currentSource = source;
            operation = new ImageLoadOperation(source, ++_generation);
        }

        TryCancel(previous);
        return operation;
    }

    public bool IsCurrent(ImageLoadOperation operation)
    {
        lock (_gate)
        {
            return !_isDisposed
                && operation.Generation == _generation
                && ReferenceEquals(_currentSource, operation.Source);
        }
    }

    public void Complete(ImageLoadOperation operation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentSource, operation.Source))
            {
                _currentSource = null;
            }
        }

        operation.Dispose();
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _currentSource;
        }

        TryCancel(source);
    }

    public void Dispose()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            source = _currentSource;
            _currentSource = null;
        }

        TryCancel(source);
        source?.Dispose();
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class ImageLoadOperation : IDisposable
{
    private CancellationTokenSource? _source;

    internal ImageLoadOperation(CancellationTokenSource source, long generation)
    {
        _source = source;
        Generation = generation;
    }

    internal CancellationTokenSource Source => _source
        ?? throw new ObjectDisposedException(nameof(ImageLoadOperation));

    public CancellationToken Token => Source.Token;

    public long Generation { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _source, null)?.Dispose();
    }
}
