using SixLabors.ImageSharp;
using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Scanning;
using VrcImageCurator.Tests.FileSystem;
using VrcImageCurator.Tests.Imaging;

namespace VrcImageCurator.Tests.Scanning;

public sealed class ScanCoordinatorTests
{
    [Fact]
    public async Task FileChangedDuringSettleWindowIsNotMoved()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var incoming = Path.Combine(sourceRoot, "changing.png");
        using (var image = ImageFixtureFactory.CreatePattern(20))
        {
            await image.SaveAsPngAsync(incoming);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.FromMilliseconds(400));

        var scan = coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        await Task.Delay(150);
        File.SetLastWriteTimeUtc(incoming, DateTime.UtcNow.AddSeconds(1));
        var result = await scan;

        Assert.Contains(result.Errors, error => error.Contains("still being written", StringComparison.Ordinal));
        Assert.True(File.Exists(incoming));
        Assert.Empty(Directory.GetFiles(archiveRoot));
    }

    [Fact]
    public async Task ExactMatchStaysInIncomingFolderAndPersistsEveryCandidate()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(21);
        var incoming = Path.Combine(sourceRoot, "different-name.png");
        var archived = Path.Combine(archiveRoot, "original.png");
        await image.SaveAsPngAsync(incoming);
        await image.SaveAsPngAsync(archived);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "ignored.temp"), "not an image");
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var router = new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService());
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            router,
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.HeldForReview);
        Assert.True(File.Exists(incoming));
        Assert.True(File.Exists(Path.Combine(sourceRoot, "ignored.temp")));
        var review = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Equal(incoming, review.HeldFilePath);
        Assert.True(review.IsIncomingInPlace);
        Assert.Equal(archived, Assert.Single(review.Candidates).ArchivePath);
        Assert.Equal(MatchKind.Exact, review.Candidates[0].MatchKind);
    }

    [Fact]
    public async Task UniqueImageMovesToArchiveAndSecondScanIsIdempotent()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(31);
        var incoming = Path.Combine(sourceRoot, "unique.png");
        await image.SaveAsPngAsync(incoming);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var first = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        var second = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, first.MovedUnique);
        Assert.Equal(0, second.Examined);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task NormalScanRefreshesIndexAfterExternalArchiveAddition()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);
        await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        var incoming = Path.Combine(sourceRoot, "incoming.png");
        var archived = Path.Combine(archiveRoot, "externally-added.png");
        using (var image = ImageFixtureFactory.CreatePattern(205))
        {
            await image.SaveAsPngAsync(incoming);
            await image.SaveAsPngAsync(archived);
        }

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Empty(result.Errors);
        Assert.Equal(0, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        Assert.True(File.Exists(archived));
        Assert.True(File.Exists(incoming));
        Assert.Equal(archived, Assert.Single(Assert.Single((await store.LoadAsync()).ReviewQueue).Candidates).ArchivePath);
    }

    [Fact]
    public async Task ConcurrentScanRequestsRouteAUniqueFileExactlyOnce()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(41);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "once.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var results = await Task.WhenAll(
            coordinator.ScanCategoryAsync(VrcImageCategory.Emoji),
            coordinator.ScanCategoryAsync(VrcImageCategory.Emoji));

        Assert.Equal(1, results.Sum(result => result.MovedUnique));
        Assert.Single(Directory.GetFiles(archiveRoot, "*.png"));
        Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images);
    }

    [Fact]
    public async Task ArchiveChangeRefreshWaitsForActiveScanBeforeInvalidatingIndex()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var incomingPaths = new[]
        {
            Path.Combine(sourceRoot, "01.png"),
            Path.Combine(sourceRoot, "02.png"),
            Path.Combine(sourceRoot, "03.png"),
        };
        for (var index = 0; index < incomingPaths.Length; index++)
        {
            using var image = ImageFixtureFactory.CreatePattern(80 + index);
            await image.SaveAsPngAsync(incomingPaths[index]);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.FromMilliseconds(100));

        var activeScan = coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);
        await WaitUntilAsync(() => !File.Exists(incomingPaths[0]), TimeSpan.FromSeconds(5));
        var watcherRefresh = coordinator.ScanCategoryAfterArchiveChangeAsync(VrcImageCategory.Emoji);
        var first = await activeScan;
        var second = await watcherRefresh;

        Assert.Empty(first.Errors);
        Assert.Equal(3, first.MovedUnique);
        Assert.Equal(0, second.Examined);
        Assert.Equal(3, Directory.GetFiles(archiveRoot, "*.png").Length);
        Assert.Equal(IndexStatus.Current, (await store.LoadAsync()).ArchiveIndex.Categories[0].Status);
    }

    [Fact]
    public async Task ConfiguredScanPreservesCategoryRelativeSubfolders()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var month = Path.Combine(sourceRoot, "2025-05");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(month);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(63);
        await image.SaveAsPngAsync(Path.Combine(month, "emoji.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "2025-05", "emoji.png")));
    }

    [Fact]
    public async Task ManualScanUsesSelectedCategoryWithoutEnablingItsConfiguredSource()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(64);
        await image.SaveAsPngAsync(Path.Combine(manualSource, "manual.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji).IsEnabled = false;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var progress = new RecordingProgress<ScanProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "manual.png")));
        Assert.Equal(new ScanProgress(VrcImageCategory.Emoji, 0, 1), progress.Values[0]);
        Assert.Equal(new ScanProgress(VrcImageCategory.Emoji, 1, 1), progress.Values[^1]);
    }

    [Fact]
    public async Task ManualScanProgressUsesOneStableFileSnapshot()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var first = ImageFixtureFactory.CreatePattern(70);
        using var later = ImageFixtureFactory.CreatePattern(71);
        await first.SaveAsPngAsync(Path.Combine(manualSource, "first.png"));
        var staged = directory.GetPath("later.png");
        await later.SaveAsPngAsync(staged);
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var values = new List<ScanProgress>();
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            values.Add(value);
            if (value.ScannedImages == 0)
            {
                File.Copy(staged, Path.Combine(manualSource, "added-after-count.png"));
            }
        });

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.Equal(1, result.Examined);
        Assert.Equal(new ScanProgress(VrcImageCategory.Emoji, 1, 1), values[^1]);
        Assert.True(File.Exists(Path.Combine(manualSource, "added-after-count.png")));
    }

    [Fact]
    public async Task RoutesEachImageBeforeReadingTheNextImage()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        var paths = new[]
        {
            Path.Combine(manualSource, "first.png"),
            Path.Combine(manualSource, "second.png"),
        };
        for (var index = 0; index < paths.Length; index++)
        {
            using var image = ImageFixtureFactory.CreatePattern(90 + index);
            await image.SaveAsPngAsync(paths[index]);
        }

        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var firstRoutedBeforeSecondReadCompleted = false;
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            if (value.ScannedImages == 2)
            {
                firstRoutedBeforeSecondReadCompleted = !File.Exists(paths[0]) && File.Exists(paths[1]);
            }
        });

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.True(firstRoutedBeforeSecondReadCompleted);
        Assert.Equal(2, result.MovedUnique);
    }

    [Fact]
    public async Task LaterImageMatchesUniqueMovedEarlierInSameScan()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(98);
        var first = Path.Combine(sourceRoot, "01-first.png");
        var second = Path.Combine(sourceRoot, "02-copy.png");
        await image.SaveAsPngAsync(first);
        await image.SaveAsPngAsync(second);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        var archived = Path.Combine(archiveRoot, "01-first.png");
        Assert.True(File.Exists(archived));
        Assert.True(File.Exists(second));
        var review = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.Equal(second, review.HeldFilePath);
        Assert.Equal(archived, Assert.Single(review.Candidates).ArchivePath);
    }

    [Fact]
    public async Task ProcessingProgressCompletesAfterUniqueAndReviewRouting()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var duplicate = ImageFixtureFactory.CreatePattern(96);
        using var unique = ImageFixtureFactory.CreatePattern(97);
        await duplicate.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        await duplicate.SaveAsPngAsync(Path.Combine(manualSource, "duplicate.png"));
        await unique.SaveAsPngAsync(Path.Combine(manualSource, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var readingProgress = new RecordingProgress<ScanProgress>();
        var processingProgress = new RecordingProgress<ScanProcessingProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            readingProgress,
            processingProgress);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        Assert.Equal(new ScanProcessingProgress(0, 2), processingProgress.Values[0]);
        Assert.Equal(new ScanProcessingProgress(2, 2), processingProgress.Values[^1]);
        Assert.Equal(3, processingProgress.Values.Count);
    }

    [Fact]
    public async Task ProcessesReviewAndUniqueImagesInPathOrder()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var duplicate = ImageFixtureFactory.CreatePattern(92);
        using var unique = ImageFixtureFactory.CreatePattern(93);
        await duplicate.SaveAsPngAsync(Path.Combine(archiveRoot, "existing.png"));
        await duplicate.SaveAsPngAsync(Path.Combine(sourceRoot, "01-duplicate.png"));
        await unique.SaveAsPngAsync(Path.Combine(sourceRoot, "02-unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.MovedUnique);
        Assert.Equal(1, result.HeldForReview);
        var state = await store.LoadAsync();
        Assert.True(File.Exists(Path.Combine(sourceRoot, "01-duplicate.png")));
        Assert.False(File.Exists(Path.Combine(sourceRoot, "02-unique.png")));
        var operations = state.History
            .Select(entry => entry.Message)
            .ToArray();
        Assert.Contains("Queued incoming image for review.", operations);
        Assert.Contains(nameof(JournalOperationPurpose.MoveUnique), operations);
        Assert.True(
            Array.IndexOf(operations, "Queued incoming image for review.")
            < Array.IndexOf(operations, nameof(JournalOperationPurpose.MoveUnique)));
    }

    [Fact]
    public async Task ScanAllProcessesEnabledCategoriesSequentially()
    {
        using var directory = new TestDirectory();
        var emojiSource = directory.GetPath("incoming", "Emoji");
        var printsSource = directory.GetPath("incoming", "Prints");
        var emojiArchive = directory.GetPath("archive", "Emoji");
        var printsArchive = directory.GetPath("archive", "Prints");
        Directory.CreateDirectory(emojiSource);
        Directory.CreateDirectory(printsSource);
        Directory.CreateDirectory(emojiArchive);
        Directory.CreateDirectory(printsArchive);
        var duplicatePath = Path.Combine(emojiSource, "duplicate.png");
        var uniquePath = Path.Combine(printsSource, "unique.png");
        using var duplicate = ImageFixtureFactory.CreatePattern(94);
        using var unique = ImageFixtureFactory.CreatePattern(95);
        await duplicate.SaveAsPngAsync(Path.Combine(emojiArchive, "existing.png"));
        await duplicate.SaveAsPngAsync(duplicatePath);
        await unique.SaveAsPngAsync(uniquePath);
        using var store = FileRouterTests.CreateStore(directory, emojiSource, emojiArchive);
        await store.UpdateAsync(state =>
        {
            var prints = state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Prints);
            prints.SourcePath = printsSource;
            prints.ArchivePath = printsArchive;
            prints.IsEnabled = true;
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var emojiProcessedBeforePrintsRead = false;
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            if (value.Category == VrcImageCategory.Prints && value.ScannedImages == 2)
            {
                emojiProcessedBeforePrintsRead = File.Exists(duplicatePath) && File.Exists(uniquePath);
            }
        });
        var processingProgress = new RecordingProgress<ScanProcessingProgress>();

        var results = await coordinator.ScanAllAsync(progress, processingProgress);

        Assert.True(emojiProcessedBeforePrintsRead);
        Assert.Equal(1, results.Sum(result => result.MovedUnique));
        Assert.Equal(1, results.Sum(result => result.HeldForReview));
        Assert.Equal(new ScanProcessingProgress(0, 2), processingProgress.Values[0]);
        Assert.Equal(new ScanProcessingProgress(2, 2), processingProgress.Values[^1]);
        Assert.Equal(3, processingProgress.Values.Count);
        var operations = (await store.LoadAsync()).History
            .Where(entry => entry.OperationId is not null)
            .Select(entry => entry.Message)
            .ToArray();
        Assert.Single(operations);
        Assert.Equal(nameof(JournalOperationPurpose.MoveUnique), operations[0]);
    }

    [Fact]
    public async Task ProgressCompletesWhenArchiveIndexCannotBeBuilt()
    {
        using var directory = new TestDirectory();
        var configuredSource = directory.GetPath("configured");
        var manualSource = directory.GetPath("manual");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(configuredSource);
        Directory.CreateDirectory(manualSource);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(72);
        await image.SaveAsPngAsync(Path.Combine(manualSource, "blocked.png"));
        using var store = FileRouterTests.CreateStore(directory, configuredSource, archiveRoot);
        Directory.Delete(archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));
        var progress = new RecordingProgress<ScanProgress>();

        var result = await coordinator.ScanFolderAsync(
            manualSource,
            VrcImageCategory.Emoji,
            progress: progress);

        Assert.NotEmpty(result.Errors);
        Assert.Equal(new ScanProgress(VrcImageCategory.Emoji, 1, 1), progress.Values[^1]);
    }

    [Fact]
    public async Task ManualScanRejectsAnInputInsideTheOutputRoot()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(archiveRoot, VrcImageCategory.Emoji));
    }

    [Fact]
    public async Task ManualScanRejectsRetainedLegacyArchive()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("configured");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var legacyRoot = directory.GetPath("legacy", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(legacyRoot);
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
            {
                Category = VrcImageCategory.Emoji,
                ArchivePath = legacyRoot,
            });
            return true;
        });
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ScanFolderAsync(legacyRoot, VrcImageCategory.Emoji));
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected scan state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task UnreadableArchiveFileDoesNotBlockScanningTheCategory()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var archived = ImageFixtureFactory.CreatePattern(41);
        await archived.SaveAsPngAsync(Path.Combine(archiveRoot, "readable.png"));
        var unreadable = Path.Combine(archiveRoot, "unreadable.png");
        await File.WriteAllBytesAsync(unreadable, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        using var image = ImageFixtureFactory.CreatePattern(42);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
        Assert.True(File.Exists(unreadable));
        Assert.Contains(result.Errors, error => error.Contains(unreadable, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanCreatesAMissingOutputFolderInsteadOfFailing()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        using var image = ImageFixtureFactory.CreatePattern(43);
        await image.SaveAsPngAsync(Path.Combine(sourceRoot, "unique.png"));
        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var coordinator = new ScanCoordinator(
            store,
            new ArchiveIndexer(store, decoder),
            decoder,
            new FileRouter(store, decoder, new FileRouterTests.FakeRecycleBinService()),
            TimeSpan.Zero);
        Assert.False(Directory.Exists(archiveRoot));

        var result = await coordinator.ScanCategoryAsync(VrcImageCategory.Emoji);

        Assert.True(Directory.Exists(archiveRoot));
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.MovedUnique);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "unique.png")));
    }
}
