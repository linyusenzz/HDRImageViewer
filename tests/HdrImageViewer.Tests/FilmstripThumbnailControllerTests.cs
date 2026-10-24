using System.Collections.ObjectModel;
using HdrImageViewer.Presentation;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class FilmstripThumbnailControllerTests
{
    [Fact]
    public async Task UnchangedViewport_DoesNotRepeatFileStateQueries()
    {
        var items = Items(1);
        var queries = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => Result("preview"), (_, _) => Result(null), CancellationToken.None)
        {
            RefreshItemState = _ => { queries++; return false; },
        };
        controller.QueueLoads(0);
        await Idle(controller);
        controller.QueueLoads(0);
        Assert.Equal(1, queries);
        controller.QueueLoads(0, refreshState: true);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task RebuildingCachedItems_RefreshesCloudBadgeWithoutDecodingAgain()
    {
        var items = Items(1);
        var calls = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => { calls++; return Result("cloud preview"); }, (_, _) => Result(null), CancellationToken.None)
        {
            RefreshItemState = item => { item.IsCloudPlaceholder = true; return false; },
        };
        controller.QueueLoads(0);
        await Idle(controller);
        items[0] = new TestItem("0");
        controller.QueueLoads(0);
        await Idle(controller);
        Assert.True(items[0].IsCloudPlaceholder);
        Assert.Equal("cloud preview", items[0].Thumbnail);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HydratingInSameWindow_RefreshesBadgeAndRetriesCachedPreviewOrFailure(bool hasCloudPreview)
    {
        var items = Items(1);
        var onlineOnly = true;
        var calls = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => { calls++; return Result(onlineOnly ? hasCloudPreview ? "cloud" : null : "local"); },
            (_, _) => Result(null), CancellationToken.None)
        {
            RefreshItemState = item =>
            {
                var becameLocal = item.IsCloudPlaceholder && !onlineOnly;
                item.IsCloudPlaceholder = onlineOnly;
                return becameLocal;
            },
        };
        controller.QueueLoads(0);
        await Idle(controller);
        Assert.True(items[0].IsCloudPlaceholder);
        Assert.Equal(!hasCloudPreview, items[0].HasLoadError);
        onlineOnly = false;
        controller.QueueLoads(0, refreshState: true); // Opening a file refreshes even the same scope.
        await Idle(controller);
        Assert.False(items[0].IsCloudPlaceholder);
        Assert.False(items[0].HasLoadError);
        Assert.Equal("local", items[0].Thumbnail);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RebuildingItems_MovesQuickPreviewWhileKeepingPendingDecode()
    {
        var items = Items(1);
        var original = items[0];
        var refine = ResultSignal();
        var quickCalls = 0;
        var refineCalls = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => { quickCalls++; return Result("quick"); },
            (_, _) => { refineCalls++; return refine.Task; }, CancellationToken.None);
        controller.QueueLoads(0);
        Assert.Equal("quick", original.Thumbnail);

        items[0] = new TestItem(original.Path);
        controller.QueueLoads(0);

        Assert.Equal("quick", items[0].Thumbnail);
        Assert.Null(original.Thumbnail);
        Assert.True(items[0].IsLoading);
        Assert.Equal(1, quickCalls);
        Assert.Equal(1, refineCalls);
        refine.SetResult(null);
        await Idle(controller);
        Assert.Equal("quick", items[0].Thumbnail);
        Assert.False(items[0].HasLoadError);
        Assert.False(items[0].IsLoading);
    }

    [Fact]
    public async Task Cancel_ReleasesCachedAndProvisionalImagesAndRejectsLateResults()
    {
        var items = Items(2);
        var delayed = ResultSignal();
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (path, _) => Result("quick:" + path),
            (path, _) => path == "1" ? delayed.Task : Result("final:" + path), CancellationToken.None);
        controller.QueueLoads(0);
        Assert.True(controller.TryGetCached("0", out _));
        Assert.Equal("quick:1", items[1].Thumbnail);

        controller.Cancel();

        Assert.All(items, item => { Assert.Null(item.Thumbnail); Assert.False(item.IsLoading); });
        Assert.False(controller.TryGetCached("0", out _));
        delayed.SetResult("late final");
        await Idle(controller);
        Assert.All(items, item => Assert.Null(item.Thumbnail));
        Assert.False(controller.TryGetCached("1", out _));
    }

    [Fact]
    public async Task CancelThenSamePathRestart_OldCompletionCannotClearNewLoadingState()
    {
        var items = Items(1);
        var firstQuick = ResultSignal();
        var newRefine = ResultSignal();
        var newStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quickCalls = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => ++quickCalls == 1 ? firstQuick.Task : Result("new quick"),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                newStarted.TrySetResult();
                return newRefine.Task;
            }, CancellationToken.None);
        controller.QueueLoads(0);
        controller.Cancel();
        controller.QueueLoads(0);
        Assert.Equal(1, quickCalls); // New generation waits for the old native call.

        firstQuick.SetResult("stale quick");
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, quickCalls);
        Assert.Equal("new quick", items[0].Thumbnail);
        Assert.True(items[0].IsLoading);
        newRefine.SetResult("new final");
        await Idle(controller);
        Assert.Equal("new final", items[0].Thumbnail);
        Assert.False(items[0].IsLoading);
    }

    [Fact]
    public async Task FailedItem_IsRetriedOnlyAfterInvalidationOrLeavingRetainedWindow()
    {
        var items = Items(150);
        var badAttempts = 0;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (path, _) => { if (path == "0") badAttempts++; return Result(path == "0" ? null : "quick"); },
            (_, _) => Result(null), CancellationToken.None);
        controller.QueueLoads(0);
        await Idle(controller);
        Assert.True(items[0].HasLoadError);

        controller.QueueLoads(1); // Same failed image remains in the expanded window.
        await Idle(controller);
        Assert.Equal(1, badAttempts);
        Assert.True(items[0].HasLoadError);

        controller.Invalidate("0");
        controller.QueueLoads(1);
        await Idle(controller);
        Assert.Equal(2, badAttempts);

        controller.QueueLoads(100);
        controller.QueueLoads(1);
        await Idle(controller);
        Assert.Equal(3, badAttempts);
    }

    [Fact]
    public async Task Scrolling_ReleasesActualItemReferencesOutsideTheWindow()
    {
        var items = Items(1000);
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (path, _) => Result("quick:" + path), (_, _) => Result(null), CancellationToken.None);
        controller.QueueLoads(0);
        await Idle(controller);
        Assert.NotNull(items[0].Thumbnail);

        controller.QueueLoads(500);
        await Idle(controller);

        Assert.Null(items[0].Thumbnail);
        Assert.False(controller.TryGetCached("0", out _));
        Assert.InRange(items.Count(item => item.Thumbnail is not null), 1, 74);
        controller.Cancel();
        Assert.All(items, item => Assert.Null(item.Thumbnail));
    }

    [Fact]
    public async Task Reload_InvalidatesFinalCacheAndKeepsOnlyTheFreshResult()
    {
        var items = Items(1);
        var version = 1;
        using var controller = new FilmstripThumbnailControllerCore<TestItem, string>(items,
            (_, _) => Result("quick"), (_, _) => Result("final:" + version), CancellationToken.None);
        controller.QueueLoads(0);
        await Idle(controller);
        Assert.Equal("final:1", items[0].Thumbnail);

        version = 2;
        controller.Invalidate("0");
        controller.QueueLoads(0);
        await Idle(controller);

        Assert.Equal("final:2", items[0].Thumbnail);
        Assert.True(controller.TryGetCached("0", out var cached));
        Assert.Equal("final:2", cached);
    }

    private static ObservableCollection<TestItem> Items(int count) =>
        new(Enumerable.Range(0, count).Select(index => new TestItem(index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    private static Task<string?> Result(string? value) => Task.FromResult(value);
    private static TaskCompletionSource<string?> ResultSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Idle(FilmstripThumbnailControllerCore<TestItem, string> controller) =>
        controller.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class TestItem(string path) : IFilmstripThumbnailItem<string>
    {
        public bool IsCloudPlaceholder { get; set; }
        public string Path { get; } = path;
        public string? Thumbnail { get; set; }
        public bool IsLoading { get; set; }
        public bool HasLoadError { get; set; }
    }
}
