using System.Collections;
using System.Resources;

namespace VrcImageCurator.Tests.Packaging;

public sealed class ApplicationResourceTests
{
    [Fact]
    public void WindowIconIsPackagedAsAWpfResource()
    {
        using var stream = typeof(VrcImageCurator.App.App).Assembly
            .GetManifestResourceStream("VrcImageCurator.g.resources");
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream);
        var resourceNames = reader.Cast<DictionaryEntry>()
            .Select(entry => Assert.IsType<string>(entry.Key))
            .ToArray();

        Assert.Contains("assets/vrcimagecurator.ico", resourceNames, StringComparer.OrdinalIgnoreCase);
    }
}
