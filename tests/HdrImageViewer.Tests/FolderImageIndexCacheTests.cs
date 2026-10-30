using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class FolderImageIndexCacheTests
{
    public static TheoryData<string[]> NaturallySortedFileNames { get; } = new()
    {
        new[] { "1.jpg", "2.png", "3.avif", "4.heic", "9.jpg", "10.png", "11.jpg", "12.jpg" },
        new[] { "IMG_1.jpg", "img_2.jpg", "IMG_9.jpg", "img_10.jpg", "IMG_11.jpg" },
        new[] { "\u56fe\u7247 (1).jpg", "\u56fe\u7247 (2).jpg", "\u56fe\u7247 (9).jpg", "\u56fe\u7247 (10).jpg" },
        new[] { "set2_frame2.jpg", "set2_frame10.jpg", "set10_frame1.jpg" },
        new[] { "001.jpg", "01.jpg", "1.jpg", "2.jpg", "010.jpg", "10.jpg", "11.jpg" },
        new[] { "image99999999999999999999.jpg", "image100000000000000000000.jpg", "image100000000000000000001.jpg" },
    };

    [Theory]
    [MemberData(nameof(NaturallySortedFileNames))]
    public void GetFolderImages_SortsNumbersNaturallyAndTracksCurrentImage(string[] fileNames)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var expected = fileNames.Select(name => Path.Combine(directory, name)).ToArray();
            foreach (var path in expected.Reverse())
            {
                File.WriteAllBytes(path, [1]);
            }

            using var cache = new FolderImageIndexCache();
            var initial = cache.GetFolderImages(expected[1], CancellationToken.None);
            Assert.Equal(expected, initial.Paths);
            Assert.Equal(1, initial.CurrentIndex);

            var cached = cache.GetFolderImages(expected[^1], CancellationToken.None);
            Assert.Equal(expected, cached.Paths);
            Assert.Equal(expected.Length - 1, cached.CurrentIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetFolderImages_CachesSortedSupportedPathsUntilInvalidated()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(directory, "a.jpg");
            var second = Path.Combine(directory, "B.png");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            File.WriteAllBytes(Path.Combine(directory, "ignored.txt"), [3]);

            using var cache = new FolderImageIndexCache();
            var initial = cache.GetFolderImages(second, CancellationToken.None);
            Assert.Equal([first, second], initial.Paths);
            Assert.Equal(1, initial.CurrentIndex);

            var added = Path.Combine(directory, "c.avif");
            File.WriteAllBytes(added, [4]);
            cache.InvalidatePath(added);
            var refreshed = cache.GetFolderImages(second, CancellationToken.None);

            Assert.Equal([first, second, added], refreshed.Paths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetFolderImages_ObservesCancellationBeforeEnumeration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var current = Path.Combine(directory, "image.jpg");
            File.WriteAllBytes(current, [1]);
            using var cache = new FolderImageIndexCache();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => cache.GetFolderImages(current, cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetFolderImages_InsertsCurrentImageInNaturalOrder(bool createCurrentFile)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(directory, "1.jpg");
            var current = Path.Combine(directory, "2.jpg");
            var last = Path.Combine(directory, "10.jpg");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(last, [1]);

            using var cache = new FolderImageIndexCache();
            Assert.Equal([first, last], cache.GetFolderImages(first, CancellationToken.None).Paths);

            if (createCurrentFile)
            {
                File.WriteAllBytes(current, [1]);
                cache.InvalidatePath(current);
            }

            var result = cache.GetFolderImages(current, CancellationToken.None);
            Assert.Equal([first, current, last], result.Paths);
            Assert.Equal(1, result.CurrentIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "HdrImageViewer.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
