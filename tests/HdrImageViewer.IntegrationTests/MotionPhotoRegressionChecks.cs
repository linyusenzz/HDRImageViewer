using System.Reflection;
using System.Security.Cryptography;
using HdrImageViewer.Pages;
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

        // Exercise the exact extraction/cache path used by the playback button.
        var extract = typeof(HomePage).GetMethod("ResolveCompanionMediaPlaybackPathAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var extracted = await (Task<string?>)extract.Invoke(null, [media, CancellationToken.None])!;
        if (extracted is null) throw new InvalidDataException("Playback extraction returned no file.");
        var sourceBytes = await File.ReadAllBytesAsync(path);
        var extractedBytes = await File.ReadAllBytesAsync(extracted);
        if (extractedBytes.LongLength != length
            || !sourceBytes.AsSpan(checked((int)offset), checked((int)length)).SequenceEqual(extractedBytes))
            throw new InvalidDataException("The extracted video differs from the source range.");
        Console.WriteLine($"PASS playback extraction: {extracted}");

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
        using var source = MediaSource.CreateFromUri(new Uri(extracted));
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
