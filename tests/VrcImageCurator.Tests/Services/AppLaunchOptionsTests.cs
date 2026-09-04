using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.Services;

public sealed class AppLaunchOptionsTests
{
    [Fact]
    public void DataDirectoryIsNormalizedAndUsesAnIsolatedInstanceName()
    {
        using var directory = new TestDirectory();

        var options = AppLaunchOptions.Parse(["--data-dir", directory.Path, "--background"]);

        Assert.True(options.Background);
        Assert.True(options.IsIsolated);
        Assert.Equal(Path.GetFullPath(directory.Path), options.DataDirectory);
        Assert.StartsWith("VrcImageCurator-", options.InstanceName);
        Assert.NotEqual("VrcImageCurator", options.InstanceName);
    }

    [Fact]
    public void MissingOrRelativeDataDirectoryIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-dir"]));
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-dir", "relative"]));
    }
}
