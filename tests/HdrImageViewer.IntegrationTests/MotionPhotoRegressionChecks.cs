using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using HdrImageViewer.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

internal static class MotionPhotoRegressionChecks
{
    public static async Task RunAsync(string path)
    {
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var loaded = await ImageDocumentLoader.LoadAsync(path);
        var media = loaded.Document.CompanionMedia;
        if (media is not { IsEmbedded: true, EmbeddedOffset: { } offset, EmbeddedLength: { } length })
            throw new InvalidDataException("The real file did not expose embedded motion video.");
        Console.WriteLine($"PASS probe: offset={offset}; length={length}; {media.VideoProbe?.DisplaySummary}");

        // Exercise the exact zero-copy BoundedFileRandomAccessStream used by the playback button.
        var sourceBytes = await File.ReadAllBytesAsync(path);
        using var verifyStream = new BoundedFileRandomAccessStream(path, offset, length);
        var slicedBytes = new byte[checked((int)length)];
        await using (var managedView = verifyStream.AsStreamForRead())
        {
            await managedView.ReadExactlyAsync(slicedBytes, CancellationToken.None);
        }
        if (!sourceBytes.AsSpan(checked((int)offset), checked((int)length)).SequenceEqual(slicedBytes))
            throw new InvalidDataException("The bounded stream slice differs from the source range.");
        Console.WriteLine($"PASS zero-copy bounded stream slice: offset={offset}, length={length}");

        using var player = new MediaPlayer();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            var error = new InvalidOperationException($"Native playback: {e.Error}: {e.ErrorMessage}");
            opened.TrySetException(error);
            advanced.TrySetException(error);
        };
        player.PlaybackSession.PositionChanged += (session, _) =>
        {
            if (session.Position > TimeSpan.FromMilliseconds(250)) advanced.TrySetResult();
        };
        using var playbackSlice = new BoundedFileRandomAccessStream(path, offset, length);
        using var source = MediaSource.CreateFromStream(playbackSlice, "video/mp4");
        player.IsMuted = true;
        player.Source = source;
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
        player.Play();
        await advanced.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PASS Windows MediaPlayer: {player.PlaybackSession.NaturalVideoWidth}x{player.PlaybackSession.NaturalVideoHeight}; duration={player.PlaybackSession.NaturalDuration}; position={player.PlaybackSession.Position}");
        player.Pause();
        player.Source = null;
        var finalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        if (!originalHash.SequenceEqual(finalHash))
            throw new InvalidDataException("Source file was modified.");
        Console.WriteLine("PASS original file hash unchanged");
    }
}
