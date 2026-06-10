using HdrImageViewer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
using System.Text;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HdrImageViewer.Pages;

// Live Photo / Motion Photo companion-media playback: the WinUI media
// overlay above the still image, embedded motion-clip extraction, and
// playback status reporting. Split from HomePage.xaml.cs for readability;
// shared fields stay in the main partial.
public sealed partial class HomePage
{
    private async void LivePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompanionMediaPlaybackActive)
        {
            StopCompanionMediaPlayback(resetSource: true);
            return;
        }

        await PlayCompanionMediaAsync();
    }

    private void LivePhotoMuteToggle_Click(object sender, RoutedEventArgs e)
    {
        var isMuted = LivePhotoMuteToggle.IsChecked == true;
        ViewModel.IsCompanionMediaMuted = isMuted;
        if (_livePhotoMediaPlayer is not null)
        {
            _livePhotoMediaPlayer.IsMuted = isMuted;
        }

        ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus("mute changed"));
    }

    private async Task PlayCompanionMediaAsync()
    {
        var media = _currentDocument?.CompanionMedia;
        if (media is null || _livePhotoMediaPlayer is null)
        {
            return;
        }

        try
        {
            var playbackPath = await ResolveCompanionMediaPlaybackPathAsync(media, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(playbackPath) || !File.Exists(playbackPath))
            {
                ViewModel.UpdateRenderStatus($"动态照片视频不可用: {media.DisplaySummary}");
                return;
            }

            _livePhotoMediaPlayer.Source = MediaSource.CreateFromUri(new Uri(playbackPath));
            _livePhotoMediaPlayer.IsMuted = ViewModel.IsCompanionMediaMuted;
            LivePhotoPlayer.Visibility = Visibility.Visible;
            _isCompanionMediaPlaybackActive = true;
            ToolTipService.SetToolTip(LivePhotoButton, "停止动态照片");
            ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus(
                $"opening native overlay; source {DescribePlaybackPath(playbackPath, media)}"));
            _livePhotoMediaPlayer.Play();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StopCompanionMediaPlayback(resetSource: true);
            ViewModel.UpdateRenderStatus($"动态照片播放失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopCompanionMediaPlayback(bool resetSource = false, string? status = null)
    {
        if (_livePhotoMediaPlayer is not null)
        {
            _livePhotoMediaPlayer.Pause();
            if (resetSource)
            {
                _livePhotoMediaPlayer.Source = null;
            }
        }

        if (LivePhotoPlayer is not null)
        {
            LivePhotoPlayer.Visibility = Visibility.Collapsed;
        }

        _isCompanionMediaPlaybackActive = false;
        if (LivePhotoButton is not null)
        {
            ToolTipService.SetToolTip(LivePhotoButton, ViewModel.CompanionMediaSummary);
        }

        ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus(
            status ?? (resetSource ? "stopped; source reset" : "stopped")));
    }

    private void LivePhotoMediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var session = sender.PlaybackSession;
            var size = session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0
                ? $"{session.NaturalVideoWidth}x{session.NaturalVideoHeight}"
                : "unknown size";
            var duration = session.NaturalDuration > TimeSpan.Zero
                ? session.NaturalDuration.ToString(@"m\:ss\.fff")
                : "unknown duration";
            ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus(
                $"opened native overlay; video {size}; duration {duration}; state {session.PlaybackState}"));
        });
    }

    private void LivePhotoMediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopCompanionMediaPlayback(resetSource: true, status: "ended; source reset");
        });
    }

    private void LivePhotoMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopCompanionMediaPlayback(resetSource: true);
            ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus($"failed {args.Error}: {args.ErrorMessage}"));
            ViewModel.UpdateRenderStatus($"动态照片播放失败: {args.Error}: {args.ErrorMessage}");
        });
    }

    private string CreateCompanionVideoStatus(string state)
    {
        var media = _currentDocument?.CompanionMedia;
        var overlaySize = LivePhotoPlayer is null
            ? "overlay unavailable"
            : $"overlay {LivePhotoPlayer.ActualWidth:0.#}x{LivePhotoPlayer.ActualHeight:0.#}";
        var playerState = _livePhotoMediaPlayer?.PlaybackSession.PlaybackState.ToString() ?? "no player";
        var mediaKind = media?.Kind.ToString() ?? "no companion media";
        return $"WinUI MediaPlayerElement; {state}; muted {(ViewModel.IsCompanionMediaMuted ? "on" : "off")}; playback {playerState}; {overlaySize}; {mediaKind}";
    }

    private static string DescribePlaybackPath(string playbackPath, CompanionMedia media)
    {
        if (!media.IsEmbedded)
        {
            return Path.GetFileName(playbackPath);
        }

        return $"{Path.GetFileName(playbackPath)} temp extract offset {media.EmbeddedOffset}, length {media.EmbeddedLength}";
    }

    private static async Task<string?> ResolveCompanionMediaPlaybackPathAsync(
        CompanionMedia media,
        CancellationToken cancellationToken)
    {
        if (!media.IsEmbedded)
        {
            return media.Path;
        }

        if (media.EmbeddedOffset is not { } offset || media.EmbeddedLength is not { } length)
        {
            return null;
        }

        var sourceInfo = new FileInfo(media.Path);
        if (!sourceInfo.Exists || offset < 0 || length <= 0 || offset + length > sourceInfo.Length)
        {
            return null;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "motion");
        Directory.CreateDirectory(tempDirectory);
        var key = CreateCompanionMediaCacheKey(sourceInfo, offset, length);
        var targetPath = Path.Combine(
            tempDirectory,
            $"{Path.GetFileNameWithoutExtension(sourceInfo.Name)}-{key}.mp4");

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == length)
        {
            return targetPath;
        }

        await CopyFileRangeAsync(media.Path, targetPath, offset, length, cancellationToken);
        return targetPath;
    }

    private static string CreateCompanionMediaCacheKey(FileInfo sourceInfo, long offset, long length)
    {
        var input = $"{sourceInfo.FullName}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}|{offset}|{length}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static async Task CopyFileRangeAsync(
        string sourcePath,
        string targetPath,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            useAsync: true);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);

        source.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Embedded motion-video payload ended before the expected length.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }
}
