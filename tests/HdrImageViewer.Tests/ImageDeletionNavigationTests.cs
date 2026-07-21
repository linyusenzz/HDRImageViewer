using HdrImageViewer.Presentation;
using Xunit;

namespace HdrImageViewer.Tests;

public class ImageDeletionNavigationTests
{
    [Fact]
    public void SelectNextPath_UsesFollowingImage_WhenDeletingFromMiddle()
    {
        Assert.Equal("c.jpg", ImageDeletionNavigation.SelectNextPath(["a.jpg", "b.jpg", "c.jpg"], 1));
    }

    [Fact]
    public void SelectNextPath_UsesPreviousImage_WhenDeletingLastImage()
    {
        Assert.Equal("b.jpg", ImageDeletionNavigation.SelectNextPath(["a.jpg", "b.jpg", "c.jpg"], 2));
    }

    [Fact]
    public void SelectNextPath_ReturnsNull_WhenDeletingOnlyImage()
    {
        Assert.Null(ImageDeletionNavigation.SelectNextPath(["a.jpg"], 0));
    }
}
