using System.Xml.Linq;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class StoreManifestTests
{
    [Fact]
    public void StoreDisplayNamesRemainReservedWhileUiLanguagesAreRetained()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HdrImageViewer.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var manifest = XDocument.Load(Path.Combine(directory.FullName, "Package.appxmanifest"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        Assert.Equal("HdrImageViewer", manifest.Root!.Element(foundation + "Properties")!.Element(foundation + "DisplayName")!.Value);
        Assert.All(manifest.Descendants(uap + "VisualElements"), element =>
            Assert.Equal("HdrImageViewer", (string?)element.Attribute("DisplayName")));
        Assert.Equal(12, manifest.Root.Element(foundation + "Resources")!.Elements(foundation + "Resource").Count());
    }
}
