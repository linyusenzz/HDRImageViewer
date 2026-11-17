using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace HdrImageViewer.Services;

/// <summary>
/// Read-only <see cref="IRandomAccessStream"/> view over a byte range <c>[offset, offset + length)</c>
/// of an existing file without extracting or copying bytes to temporary storage.
/// </summary>
internal sealed class BoundedFileRandomAccessStream : IRandomAccessStream
{
    private const int DefaultBufferSize = 64 * 1024;

    private readonly string _filePath;
    private readonly long _offset;
    private readonly long _length;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _position;
    private volatile bool _disposed;

    public BoundedFileRandomAccessStream(string filePath, long offset, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        _filePath = filePath;
        _offset = offset;
        _length = length;
        _stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            if (offset > _stream.Length || length > _stream.Length - offset)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    "Bounded stream window exceeds the underlying file length.");
            }
        }
        catch
        {
            _stream.Dispose();
            _gate.Dispose();
            throw;
        }
    }

    public bool CanRead => !_disposed;

    public bool CanWrite => false;

    public ulong Position => (ulong)Interlocked.Read(ref _position);

    public ulong Size
    {
        get => (ulong)_length;
        set => throw new NotSupportedException();
    }

    public void Seek(ulong position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clamped = position > (ulong)long.MaxValue ? long.MaxValue : (long)position;
        Interlocked.Exchange(ref _position, clamped);
    }

    public IRandomAccessStream CloneStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new BoundedFileRandomAccessStream(_filePath, _offset, _length);
    }

    public IInputStream GetInputStreamAt(ulong position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = new BoundedFileRandomAccessStream(_filePath, _offset, _length);
        stream.Seek(position);
        return stream;
    }

    public IOutputStream GetOutputStreamAt(ulong position) =>
        throw new NotSupportedException();

    public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(
        IBuffer buffer,
        uint count,
        InputStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return AsyncInfo.Run<IBuffer, uint>(async (cancellationToken, progress) =>
        {
            var entered = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                ObjectDisposedException.ThrowIf(_disposed, this);

                var currentPosition = Interlocked.Read(ref _position);
                var maxRequested = (int)Math.Min(Math.Min(count, buffer.Capacity), int.MaxValue);
                if (maxRequested <= 0 || currentPosition >= _length)
                {
                    buffer.Length = 0;
                    progress.Report(0);
                    return buffer;
                }

                var remaining = _length - currentPosition;
                var bytesToRead = (int)Math.Min(maxRequested, remaining);
                var allowPartial = (options & InputStreamOptions.Partial) == InputStreamOptions.Partial;
                var rented = ArrayPool<byte>.Shared.Rent(bytesToRead);
                try
                {
                    var totalRead = 0;
                    while (totalRead < bytesToRead)
                    {
                        var read = await RandomAccess.ReadAsync(
                            _stream.SafeFileHandle,
                            rented.AsMemory(totalRead, bytesToRead - totalRead),
                            _offset + currentPosition + totalRead,
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        totalRead += read;
                        progress.Report((uint)totalRead);
                        if (allowPartial)
                        {
                            break;
                        }
                    }

                    Interlocked.Exchange(ref _position, currentPosition + totalRead);
                    if (totalRead == 0)
                    {
                        buffer.Length = 0;
                        return buffer;
                    }

                    rented.CopyTo(0, buffer, 0, totalRead);
                    buffer.Length = (uint)totalRead;
                    return buffer;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            finally
            {
                if (entered && !_disposed)
                {
                    try
                    {
                        _gate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        });
    }

    public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) =>
        throw new NotSupportedException();

    public IAsyncOperation<bool> FlushAsync() =>
        AsyncInfo.Run(_ => Task.FromResult(false));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _gate.Dispose();
    }
}
