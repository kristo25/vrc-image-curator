using VrcPicSorter.Core.Atlas;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.FileSystem;

namespace VrcPicSorter.Tests.Atlas;

public sealed class AtlasAnimationCatalogTests
{
    [Fact]
    public async Task SkippingASheetTakesItOutOfTheQueueWithoutTouchingIt()
    {
        using var directory = new TestDirectory();
        var archiveRoot = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, archiveRoot, "a", out var sheetPath);
        var catalog = new AtlasAnimationCatalog(store);

        var listed = Assert.Single(await catalog.ListAsync());
        Assert.True(listed.NeedsDecision);
        Assert.False(listed.IsSkipped);

        Assert.Equal(1, await catalog.SkipAsync([listed]));

        var skipped = Assert.Single(await catalog.ListAsync());
        Assert.True(skipped.IsSkipped);
        Assert.False(skipped.NeedsDecision);
        Assert.Contains("skipped", skipped.Summary, StringComparison.Ordinal);
        // A skip records a decision and nothing else. The sheet stays exactly where it was.
        Assert.Equal(sheetPath, skipped.AtlasPath);
    }

    [Fact]
    public async Task SkippingTwiceChangesNothingAndRestoringPutsItBack()
    {
        using var directory = new TestDirectory();
        using var store = CreateStoreWithSheet(directory, directory.GetPath("archive", "Emoji"), "a", out _);
        var catalog = new AtlasAnimationCatalog(store);
        var sheet = Assert.Single(await catalog.ListAsync());

        Assert.Equal(1, await catalog.SkipAsync([sheet]));
        Assert.Equal(0, await catalog.SkipAsync([sheet]));

        var skipped = Assert.Single(await catalog.ListAsync());
        Assert.Equal(1, await catalog.RestoreAsync([skipped]));
        Assert.True(Assert.Single(await catalog.ListAsync()).NeedsDecision);
    }

    [Fact]
    public async Task ASkipSurvivesTheIndexBeingRebuiltAndTheSheetBeingFiled()
    {
        // This is why skips are keyed by fingerprint rather than by index id or path: a rebuild
        // mints a new id for every record, and filing a sheet beside its animation moves it. Keyed
        // by either of those, every skip would come undone the first time the archive was rebuilt.
        using var directory = new TestDirectory();
        var archiveRoot = directory.GetPath("archive", "Emoji");
        using var store = CreateStoreWithSheet(directory, archiveRoot, "a", out var sheetPath);
        var catalog = new AtlasAnimationCatalog(store);
        await catalog.SkipAsync([Assert.Single(await catalog.ListAsync())]);

        var filed = Path.Combine(archiveRoot, "Animated", "Gif Ref", Path.GetFileName(sheetPath));
        await store.UpdateAsync(state =>
        {
            var index = state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji);
            index.Images.Clear();
            index.Images.Add(new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                Path = filed,
                ExactFingerprint = "fingerprint-a",
            });
            return true;
        });

        Assert.True(Assert.Single(await catalog.ListAsync()).IsSkipped);
    }

    [Theory]
    // Only the ceiling is a real clamp. Rates that merely land on a neighbouring hundredth used to
    // report themselves as clamped, which was nearly every sheet in a real archive.
    [InlineData(31, false)]
    [InlineData(30, false)]
    [InlineData(17, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(64, true)]
    public void OnlyARateTooFastForGifCountsAsClamped(int framesPerSecond, bool clamped)
    {
        var sheet = new ArchivedSheet(
            Guid.NewGuid(),
            VrcImageCategory.Emoji,
            "x.png",
            "root",
            "x.gif",
            false,
            new EmojiAtlasName(4, framesPerSecond, AtlasLoopStyle.Linear));

        Assert.Equal(clamped, sheet.RateWasClamped);
    }

    private static JsonStateStore CreateStoreWithSheet(
        TestDirectory directory,
        string archiveRoot,
        string key,
        out string sheetPath)
    {
        Directory.CreateDirectory(archiveRoot);
        sheetPath = Path.Combine(archiveRoot, $"player_{key}_4frames_10fps_linearloopStyle.png");
        var store = FileRouterTests.CreateStore(directory, directory.GetPath("incoming"), archiveRoot);
        var path = sheetPath;
        store.UpdateAsync(state =>
        {
            var index = state.ArchiveIndex.Categories.Single(item => item.Category == VrcImageCategory.Emoji);
            index.Images.Add(new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                Path = path,
                ExactFingerprint = $"fingerprint-{key}",
            });
            return true;
        }).GetAwaiter().GetResult();
        return store;
    }
}
