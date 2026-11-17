using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HdrImageViewer.Pages;

// Live Photo / Motion Photo companion-media playback: the WinUI media
// overlay above the still image, zero-copy embedded motion-clip streaming,
// and playback status reporting. Split from HomePage.xaml.cs for readability;
// shared fields stay in the main partial.
public sealed partial class HomePage
{
    private static int s_legacyMotionTempCleaned;
    private MediaSource? _livePhotoMediaSource;
    private BoundedFileRandomAccessStream? _livePhotoStream;

    internal static void CleanupLegacyMotionPhotoTempDirectoryOnce()
    {
        if (Interlocked.Exchange(ref s_legacyMotionTempCleaned, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var legacyDirectory = Path.Combine(Path.GetTempPath(), "HdrImageViewer", "motion");
                if (Directory.Exists(legacyDirectory))
                {
                    Directory.Delete(legacyDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of legacy extracted motion clips.
            }
        });
    }

    private async void LivePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleCompanionMediaPlaybackAsync();
        }
        catch (Exception ex)
        {
            HandleUnexpectedUiException(ex);
        }
    }

    private Task ToggleCompanionMediaPlaybackAsync()
    {
        if (_isCompanionMediaPlaybackActive || _isCompanionMediaPreparing)
        {
            StopCompanionMediaPlayback(resetSource: true);
            return Task.CompletedTask;
        }

        return PlayCompanionMediaAsync();
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
        var document = _currentDocument;
        var media = document?.CompanionMedia;
        if (media is null)
        {
            return;
        }

        CleanupLegacyMotionPhotoTempDirectoryOnce();

        var operation = _companionLoads.Begin();
        var token = operation.Token;
        _isCompanionMediaPreparing = true;
        ToolTipService.SetToolTip(LivePhotoButton, Localization.GetString("LivePhotoPreparing"));
        BoundedFileRandomAccessStream? boundedStream = null;
        MediaSource? mediaSource = null;

        try
        {
            if (media.IsEmbedded)
            {
                if (media.EmbeddedOffset is not { } offset || media.EmbeddedLength is not { } length)
                {
                    ViewModel.UpdateRenderStatus(Localization.GetString("StatusLivePhotoVideoUnavailable", media.DisplaySummary));
                    return;
                }

                boundedStream = await Task.Run(() => TryOpenEmbeddedCompanionStream(media.Path, offset, length), token);
                token.ThrowIfCancellationRequested();
                if (!_companionLoads.IsCurrent(operation) || !ReferenceEquals(document, _currentDocument))
                {
                    boundedStream?.Dispose();
                    boundedStream = null;
                    return;
                }

                if (boundedStream is null)
                {
                    ViewModel.UpdateRenderStatus(Localization.GetString("StatusLivePhotoVideoUnavailable", media.DisplaySummary));
                    return;
                }

                mediaSource = MediaSource.CreateFromStream(boundedStream, "video/mp4");
            }
            else
            {
                var sidecarExists = await Task.Run(
                    () => !string.IsNullOrWhiteSpace(media.Path) && File.Exists(media.Path),
                    token);
                token.ThrowIfCancellationRequested();
                if (!_companionLoads.IsCurrent(operation) || !ReferenceEquals(document, _currentDocument))
                {
                    return;
                }

                if (!sidecarExists)
                {
                    ViewModel.UpdateRenderStatus(Localization.GetString("StatusLivePhotoVideoUnavailable", media.DisplaySummary));
                    return;
                }

                if (media is { VideoProbe: null, VideoProbeCompleted: false })
                {
                    // Playback is an explicit request to fetch the sidecar. Complete
                    // deferred metadata and persist it without resetting viewer state.
                    var refreshed = await ImagePreloadCache.RefreshCompanionMediaAsync(
                        document!.Path,
                        allowCloudDownload: true,
                        token);
                    token.ThrowIfCancellationRequested();
                    if (!_companionLoads.IsCurrent(operation) || !ReferenceEquals(document, _currentDocument))
                    {
                        return;
                    }

                    if (refreshed.Document.CompanionMedia is { } refreshedMedia && refreshedMedia.Path == media.Path)
                    {
                        media = refreshedMedia;
                        _currentDocument = document with { CompanionMedia = media };
                        ViewModel.UpdateCompanionVideoMetadata(media);
                    }
                }

                mediaSource = MediaSource.CreateFromUri(new Uri(media.Path));
            }

            // Native media events carry no source generation. A fresh player per
            // playback lets even callbacks delivered late identify their owner.
            DisposeCompanionMediaSource();
            InitializeLivePhotoPlayer();
            _livePhotoStream = boundedStream;
            boundedStream = null;
            _livePhotoMediaSource = mediaSource;
            mediaSource = null;

            LivePhotoPlayer.Opacity = 0.0;
            LivePhotoPlayer.Visibility = Visibility.Visible;
            _livePhotoMediaPlayer!.IsMuted = ViewModel.IsCompanionMediaMuted;
            _livePhotoMediaPlayer.Source = _livePhotoMediaSource;
            _isCompanionMediaPlaybackActive = true;
            ToolTipService.SetToolTip(LivePhotoButton, Localization.GetString("LivePhotoStop"));
            ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus(
                $"opening native overlay; source {DescribeCompanionPlaybackSource(media)}"));
            _livePhotoMediaPlayer.Play();
        }
        catch (OperationCanceledException)
        {
            mediaSource?.Dispose();
            boundedStream?.Dispose();
        }
        catch (Exception ex) when (!token.IsCancellationRequested && _companionLoads.IsCurrent(operation))
        {
            mediaSource?.Dispose();
            boundedStream?.Dispose();
            StopCompanionMediaPlayback(resetSource: true);
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusLivePhotoPlaybackFailed", $"{ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            mediaSource?.Dispose();
            boundedStream?.Dispose();
        }
        finally
        {
            if (_companionLoads.IsCurrent(operation))
            {
                _isCompanionMediaPreparing = false;
                if (!_isCompanionMediaPlaybackActive)
                {
                    ToolTipService.SetToolTip(LivePhotoButton, ViewModel.CompanionMediaSummary);
                }
            }

            _companionLoads.Complete(operation);
        }
    }

    private static BoundedFileRandomAccessStream? TryOpenEmbeddedCompanionStream(
        string sourcePath,
        long offset,
        long length)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || offset < 0 || length <= 0)
        {
            return null;
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists || offset > sourceInfo.Length || length > sourceInfo.Length - offset)
            {
                return null;
            }

            return new BoundedFileRandomAccessStream(sourcePath, offset, length);
        }
        catch
        {
            return null;
        }
    }

    private void StopCompanionMediaPlayback(bool resetSource = false, string? status = null)
    {
        _companionLoads.CancelCurrent();
        _isCompanionMediaPreparing = false;
        if (_livePhotoMediaPlayer is not null)
        {
            _livePhotoMediaPlayer.Pause();
            if (resetSource)
            {
                _livePhotoMediaPlayer.Source = null;
            }
        }

        if (resetSource)
        {
            DisposeCompanionMediaSource();
        }

        if (LivePhotoPlayer is not null)
        {
            LivePhotoPlayer.Opacity = 0.0;
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

    private void DisposeCompanionMediaSource()
    {
        if (_livePhotoMediaSource is { } source)
        {
            _livePhotoMediaSource = null;
            source.Dispose();
        }

        if (_livePhotoStream is { } stream)
        {
            _livePhotoStream = null;
            stream.Dispose();
        }
    }

    private void LivePhotoMediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(sender, _livePhotoMediaPlayer))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!_isCompanionMediaPlaybackActive || !ReferenceEquals(sender, _livePhotoMediaPlayer))
                {
                    return;
                }

                var session = sender.PlaybackSession;
                var size = session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0
                    ? $"{session.NaturalVideoWidth}x{session.NaturalVideoHeight}"
                    : "unknown size";
                var duration = session.NaturalDuration > TimeSpan.Zero
                    ? session.NaturalDuration.ToString(@"m\:ss\.fff")
                    : "unknown duration";
                ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus(
                    $"opened native overlay; video {size}; duration {duration}; state {session.PlaybackState}"));

                await Task.Delay(24);
                if (_isCompanionMediaPlaybackActive && ReferenceEquals(sender, _livePhotoMediaPlayer) && LivePhotoPlayer is not null)
                {
                    LivePhotoPlayer.Opacity = 1.0;
                }
            }
            catch (Exception ex)
            {
                HandleUnexpectedUiException(ex);
            }
        });
    }

    private void LivePhotoMediaPlayer_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (_livePhotoMediaPlayer is null || !ReferenceEquals(sender, _livePhotoMediaPlayer.PlaybackSession))
        {
            return;
        }

        if (sender.PlaybackState == MediaPlaybackState.Playing)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (_isCompanionMediaPlaybackActive
                    && _livePhotoMediaPlayer is not null
                    && ReferenceEquals(sender, _livePhotoMediaPlayer.PlaybackSession)
                    && LivePhotoPlayer is not null)
                {
                    LivePhotoPlayer.Opacity = 1.0;
                }
            });
        }
    }

    private void LivePhotoMediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(sender, _livePhotoMediaPlayer))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isCompanionMediaPlaybackActive || !ReferenceEquals(sender, _livePhotoMediaPlayer))
            {
                return;
            }

            StopCompanionMediaPlayback(resetSource: true, status: "ended; source reset");
        });
    }

    private void LivePhotoMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (!ReferenceEquals(sender, _livePhotoMediaPlayer))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isCompanionMediaPlaybackActive || !ReferenceEquals(sender, _livePhotoMediaPlayer))
            {
                return;
            }

            StopCompanionMediaPlayback(resetSource: true);
            ViewModel.UpdateCompanionVideoStatus(CreateCompanionVideoStatus($"failed {args.Error}: {args.ErrorMessage}"));
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusLivePhotoPlaybackFailed", $"{args.Error}: {args.ErrorMessage}"));
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

    private static string DescribeCompanionPlaybackSource(CompanionMedia media)
    {
        if (!media.IsEmbedded)
        {
            return Path.GetFileName(media.Path);
        }

        return $"{Path.GetFileName(media.Path)} zero-copy stream offset {media.EmbeddedOffset}, length {media.EmbeddedLength}";
    }
}
