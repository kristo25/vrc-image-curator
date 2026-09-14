using VrcPicSorter.App.Services;
using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Tests.Services;

public sealed class AppRuntimeTests
{
    [Fact]
    public async Task IsolatedRuntimeKeepsEveryDefaultPathInsideItsDataDirectory()
    {
        using var directory = new TestDirectory();
        using var runtime = new AppRuntime(directory.Path, allowStartupRegistration: false);

        await runtime.InitializeAsync();
        var state = await runtime.StateStore.LoadAsync();

        Assert.False(runtime.AllowStartupRegistration);
        Assert.All(
            state.Settings.CategoryMappings,
            mapping =>
            {
                Assert.True(PathBoundary.Contains(directory.Path, mapping.SourcePath));
                Assert.True(PathBoundary.Contains(directory.Path, mapping.ArchivePath));
            });
        Assert.True(PathBoundary.Contains(directory.Path, state.Settings.OutputRootPath));
        Assert.True(PathBoundary.Contains(directory.Path, state.Settings.HoldingRootPath));
        Assert.False(runtime.Watcher.IsRunning);
    }
}
