using System.Collections;
using System.Resources;

namespace VrcPicSorter.Tests.Packaging;

public sealed class ApplicationResourceTests
{
    [Fact]
    public void WindowIconIsPackagedAsAWpfResource()
    {
        using var stream = typeof(VrcPicSorter.App.App).Assembly
            .GetManifestResourceStream("VrcPicSorter.g.resources");
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream);
        var resourceNames = reader.Cast<DictionaryEntry>()
            .Select(entry => Assert.IsType<string>(entry.Key))
            .ToArray();

        Assert.Contains("assets/vrcpicsorter.ico", resourceNames, StringComparer.OrdinalIgnoreCase);
    }
}
