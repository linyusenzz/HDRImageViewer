using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

// ViewerSessionState is a static holder, so these tests always call SaveImage
// first to put it into a known state. xUnit runs tests within one class
// sequentially, which keeps the shared static safe here.
public class ViewerSessionStateTests : IDisposable
{
    private readonly string _tempDirectory;

    public ViewerSessionStateTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "HdrImageViewerTests", Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TryGetLastImage_ReturnsFalse_WhenCurrentImageMissing()
    {
        var missing = Path.Combine(_tempDirectory, "missing.jpg");
        ViewerSessionState.SaveImage(missing, [missing], hasExplicitNavigationPaths: false);

        Assert.False(ViewerSessionState.TryGetLastImage(out _, out _, out _));
    }

    [Fact]
    public void TryGetLastImage_FiltersStalePathsOnRead()
    {
        var existing = CreateFile("a.jpg");
        var stale = Path.Combine(_tempDirectory, "deleted.jpg");
        ViewerSessionState.SaveImage(existing, [existing, stale], hasExplicitNavigationPaths: true);

        Assert.True(ViewerSessionState.TryGetLastImage(out var path, out var navigationPaths, out var hasExplicit));
        Assert.Equal(existing, path);
        Assert.True(hasExplicit);
        Assert.NotNull(navigationPaths);
        Assert.Single(navigationPaths);
        Assert.Equal(existing, navigationPaths[0]);
    }

    [Fact]
    public void SaveImage_DeduplicatesPathsIgnoringCase()
    {
        var existing = CreateFile("b.jpg");
        var upperCased = existing.ToUpperInvariant();
        ViewerSessionState.SaveImage(existing, [existing, upperCased], hasExplicitNavigationPaths: false);

        Assert.True(ViewerSessionState.TryGetLastImage(out _, out var navigationPaths, out _));
        Assert.NotNull(navigationPaths);
        Assert.Single(navigationPaths);
    }

    [Fact]
    public void TryGetLastImage_SurvivesDeletionBetweenSaveAndRead()
    {
        var current = CreateFile("c.jpg");
        var doomed = CreateFile("d.jpg");
        ViewerSessionState.SaveImage(current, [current, doomed], hasExplicitNavigationPaths: true);

        // SaveImage must not pre-filter: deletion after save is detected at read time.
        File.Delete(doomed);

        Assert.True(ViewerSessionState.TryGetLastImage(out _, out var navigationPaths, out _));
        Assert.NotNull(navigationPaths);
        Assert.DoesNotContain(doomed, navigationPaths);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF]);
        return path;
    }
}
