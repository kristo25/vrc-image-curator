using SixLabors.ImageSharp;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Scanning;
using VrcImageCurator.Tests.FileSystem;
using VrcImageCurator.Tests.Imaging;

namespace VrcImageCurator.Tests.Scanning;

public sealed class ArchiveIndexerTests
{
    [Fact]
    public async Task RefreshReusesPersistedFingerprintForUnchangedFile()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var archived = Path.Combine(archiveRoot, "existing.png");
        using (var image = ImageFixtureFactory.CreatePattern(201))
        {
            await image.SaveAsPngAsync(archived);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        var first = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var generation = first.Generation;
        var indexedId = Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images).Id;

        await using var locked = new FileStream(archived, FileMode.Open, FileAccess.Read, FileShare.None);
        var second = await indexer.RefreshAsync(VrcImageCategory.Emoji);

        Assert.Equal(IndexStatus.Current, second.Status);
        Assert.Equal(generation, second.Generation);
        Assert.Equal(indexedId, Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshRebuildsAnUnchangedNonCurrentFingerprint(bool corruptCurrentFingerprint)
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var archived = Path.Combine(archiveRoot, "existing.png");
        using (var image = ImageFixtureFactory.CreatePattern(205))
        {
            await image.SaveAsPngAsync(archived);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        var first = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        await store.UpdateAsync(state =>
        {
            var record = Assert.Single(state.ArchiveIndex.Categories[0].Images);
            record.Fingerprint = corruptCurrentFingerprint
                ? record.Fingerprint! with { PerceptualFrames = [] }
                : record.Fingerprint! with { FeatureVersion = 1 };
            return true;
        });

        var second = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var rebuilt = Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal(ImageFingerprint.CurrentFeatureVersion, rebuilt.Fingerprint!.FeatureVersion);
        Assert.True(rebuilt.Fingerprint.HasCurrentFeatures);
    }

    [Fact]
    public async Task RefreshAddsAndRemovesFilesWithoutReprocessingUnchangedRecords()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var retained = Path.Combine(archiveRoot, "retained.png");
        var removed = Path.Combine(archiveRoot, "removed.png");
        using (var image = ImageFixtureFactory.CreatePattern(202))
        {
            await image.SaveAsPngAsync(retained);
        }

        using (var image = ImageFixtureFactory.CreatePattern(203))
        {
            await image.SaveAsPngAsync(removed);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var initial = (await store.LoadAsync()).ArchiveIndex.Categories[0];
        var retainedId = initial.Images.Single(item => item.Path == retained).Id;
        var firstGeneration = initial.Generation;

        File.Delete(removed);
        var added = Path.Combine(archiveRoot, "added.png");
        using (var image = ImageFixtureFactory.CreatePattern(204))
        {
            await image.SaveAsPngAsync(added);
        }

        var result = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var refreshed = (await store.LoadAsync()).ArchiveIndex.Categories[0];

        Assert.Equal(IndexStatus.Current, result.Status);
        Assert.Equal(firstGeneration + 1, result.Generation);
        Assert.Equal(2, refreshed.Images.Count);
        Assert.Equal(retainedId, refreshed.Images.Single(item => item.Path == retained).Id);
        Assert.Contains(refreshed.Images, item => item.Path == added);
        Assert.DoesNotContain(refreshed.Images, item => item.Path == removed);
    }

    [Fact]
    public async Task UnreadableArchiveFileIsSkippedWithoutDisablingTheCategory()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var readable = Path.Combine(archiveRoot, "readable.png");
        using (var image = ImageFixtureFactory.CreatePattern(206))
        {
            await image.SaveAsPngAsync(readable);
        }

        var unreadable = Path.Combine(archiveRoot, "unreadable.png");
        await File.WriteAllBytesAsync(unreadable, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());

        var result = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var index = (await store.LoadAsync()).ArchiveIndex.Categories[0];

        Assert.Equal(IndexStatus.Current, result.Status);
        Assert.Equal(IndexStatus.Current, index.Status);
        Assert.Empty(result.Errors);
        Assert.Contains(unreadable, Assert.Single(result.SkippedFiles));
        Assert.Equal(readable, Assert.Single(index.Images).Path);
        Assert.NotNull(index.LastError);
    }
}
