using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class CloudFilePolicyTests
{
    private const int FileAttributeOffline = 0x0000_1000;
    private const int FileAttributeRecallOnOpen = 0x0004_0000;
    private const int FileAttributeRecallOnDataAccess = 0x0040_0000;

    [Theory]
    [InlineData(FileAttributes.Normal)]
    [InlineData(FileAttributes.Archive)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.Hidden)]
    [InlineData(FileAttributes.SparseFile)]
    [InlineData(FileAttributes.Encrypted)]
    [InlineData(FileAttributes.Compressed)]
    [InlineData(FileAttributes.Temporary)]
    // A reparse point alone is a hydrated cloud-files placeholder whose
    // content is already local; only recall/offline bits mean a download.
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive)]
    [InlineData(FileAttributes.Directory)]
    public void LocalFilesAreNotCloudPlaceholders(FileAttributes attributes)
    {
        Assert.False(CloudFilePolicy.IsCloudPlaceholder(attributes));
    }

    [Theory]
    [InlineData(FileAttributeRecallOnOpen)]
    [InlineData(FileAttributeRecallOnDataAccess)]
    [InlineData(FileAttributeOffline)]
    [InlineData(FileAttributeRecallOnOpen | FileAttributeOffline)]
    [InlineData(FileAttributeRecallOnDataAccess | (int)FileAttributes.Archive)]
    [InlineData(FileAttributeRecallOnDataAccess | (int)FileAttributes.ReparsePoint | FileAttributeOffline)]
    public void RecallAttributesMarkCloudPlaceholders(int rawAttributes)
    {
        Assert.True(CloudFilePolicy.IsCloudPlaceholder((FileAttributes)rawAttributes));
    }

    [Fact]
    public void ExistingLocalFileIsNotCloudPlaceholder()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.True(File.Exists(path));
            Assert.False(CloudFilePolicy.IsCloudPlaceholder(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileIsNotCloudPlaceholder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "hdrviewer-missing-" + Guid.NewGuid().ToString("N") + ".jpg");

        Assert.False(CloudFilePolicy.IsCloudPlaceholder(path));
    }

    [Fact]
    public void InvalidPathIsNotCloudPlaceholder()
    {
        Assert.False(CloudFilePolicy.IsCloudPlaceholder(string.Empty));
        Assert.False(CloudFilePolicy.IsCloudPlaceholder("   "));
    }
}
