namespace HdrImageViewer.Services;

/// <summary>Stages output beside its destination so failures never remove an existing file.</summary>
internal sealed class ExportFileTransaction : IDisposable
{
    private readonly string _destination;
    private readonly bool _overwrite;

    public ExportFileTransaction(string destination, bool overwrite = true)
    {
        _overwrite = overwrite;
        _destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(_destination)!;
        Directory.CreateDirectory(directory);
        TemporaryPath = Path.Combine(directory, $".hdr-export-{Guid.NewGuid():N}{Path.GetExtension(destination)}");
    }

    public string TemporaryPath { get; }

    public void Commit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(TemporaryPath).Length == 0)
        {
            throw new InvalidDataException(Localization.GetString("ExportFileEmptyNotReplaced"));
        }

        if (_overwrite && File.Exists(_destination))
        {
            // Do not fall back to delete-then-move on unsupported filesystems.
            File.Replace(TemporaryPath, _destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(TemporaryPath, _destination);
        }
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken, bool overwrite = true)
    {
        using var transaction = new ExportFileTransaction(destination, overwrite);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        await using (var output = new FileStream(transaction.TemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        transaction.Commit(cancellationToken);
    }

    public void Dispose()
    {
        try { File.Delete(TemporaryPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
