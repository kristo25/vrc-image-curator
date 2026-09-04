using System.Text.Json;
using System.Text.Json.Serialization;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.Tests.Storage;

public sealed class JsonStateStoreTests
{
    [Fact]
    public async Task CorruptStateIsQuarantinedAndDefaultsRemainUsable()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        _ = await store.LoadAsync();
        await File.WriteAllTextAsync(store.StatePath, "{not-json");

        var recovered = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, recovered.SchemaVersion);
        Assert.NotNull(store.LastRecoveryNotice);
        Assert.True(File.Exists(store.LastRecoveryNotice.QuarantinedPath));
        Assert.Equal("{not-json", await File.ReadAllTextAsync(store.LastRecoveryNotice.QuarantinedPath));
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task CorruptPrimaryStateRecoversFromLastValidBackup()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.Settings.SimilarityProfile = SimilarityProfile.Strict;
        await store.SaveAsync(state);
        state.Settings.SimilarityProfile = SimilarityProfile.Broad;
        await store.SaveAsync(state);
        Assert.True(File.Exists(store.BackupPath));
        await File.WriteAllTextAsync(store.StatePath, "{not-json");

        var recovered = await store.LoadAsync();

        Assert.Equal(SimilarityProfile.Strict, recovered.Settings.SimilarityProfile);
        Assert.NotNull(store.LastRecoveryNotice);
        Assert.True(store.LastRecoveryNotice.RestoredBackup);
    }

    [Fact]
    public async Task FirstLoadCreatesFixedDefaultsWithoutTouchingImageFolders()
    {
        using var directory = new TestDirectory();
        var profilePath = directory.GetPath("profile");
        var localAppDataPath = directory.GetPath("local-app-data");
        using var store = new JsonStateStore(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(profilePath, localAppDataPath));

        var state = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(
            AppStateDefaults.FixedCategories,
            state.Settings.CategoryMappings.Select(mapping => mapping.Category));
        Assert.Equal(
            AppStateDefaults.FixedCategories,
            state.ArchiveIndex.Categories.Select(category => category.Category));
        Assert.All(state.Settings.CategoryMappings, mapping => Assert.False(mapping.IsEnabled));
        Assert.All(state.ArchiveIndex.Categories, category => Assert.Equal(IndexStatus.Stale, category.Status));
        Assert.Equal(SimilarityProfile.Conservative, state.Settings.SimilarityProfile);
        Assert.Equal(OrganizationPolicy.PreserveIncomingRelativeFolder, state.Settings.OrganizationPolicy);
        Assert.Equal(Path.Combine(profilePath, "Pictures", "VRC Images"), state.Settings.OutputRootPath);
        Assert.True(File.Exists(store.StatePath));
        Assert.All(
            state.Settings.CategoryMappings,
            mapping =>
            {
                Assert.False(Directory.Exists(mapping.SourcePath));
                Assert.False(Directory.Exists(mapping.ArchivePath));
            });
        Assert.False(Directory.Exists(state.Settings.HoldingRootPath));
    }

    [Fact]
    public async Task SaveThenLoadPreservesTheCompleteStateDocument()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var timestamp = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
        var imageId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        state.Settings.CategoryMappings[0].SourcePath = @"C:\Incoming\Emoji";
        state.Settings.CategoryMappings[0].ArchivePath = @"D:\Archive\Emoji";
        state.Settings.CategoryMappings[0].IsEnabled = true;
        state.Settings.OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder;
        state.Settings.SimilarityProfile = SimilarityProfile.Broad;
        state.Settings.Automation.WatchWhileOpen = true;
        state.Settings.Automation.StartWithWindows = true;
        state.Settings.HoldingRootPath = @"C:\State\Holding";
        state.Settings.BringReviewForwardWhenHeld = false;

        state.ArchiveIndex.Categories[0].Status = IndexStatus.Current;
        state.ArchiveIndex.Categories[0].Generation = 42;
        state.ArchiveIndex.Categories[0].LastCompletedUtc = timestamp;
        state.ArchiveIndex.Categories[0].LastError = "prior error";
        state.ArchiveIndex.Categories[0].Images.Add(new IndexedImageRecord
        {
            Id = imageId,
            Category = VrcImageCategory.Emoji,
            Path = @"D:\Archive\Emoji\sample.png",
            FileSize = 1234,
            LastWriteUtc = timestamp,
            Width = 512,
            Height = 256,
            ExactFingerprint = "exact",
            PerceptualFingerprint = "perceptual",
        });

        state.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Status = ReviewStatus.NeedsReconciliation,
            IncomingOriginalPath = @"C:\Incoming\Emoji\sample.png",
            HeldFilePath = @"C:\State\Holding\Emoji\sample.png",
            IncomingFingerprint = "incoming",
            IndexGeneration = 42,
            CreatedUtc = timestamp,
            Candidates =
            [
                new ReviewCandidate
                {
                    Id = Guid.NewGuid(),
                    IndexedImageId = imageId,
                    ArchivePath = @"D:\Archive\Emoji\sample.png",
                    ExpectedFingerprint = "exact",
                    MatchKind = MatchKind.Similar,
                    SimilarityScore = 0.93,
                    MatchReasons = ["same dimensions", "close thumbnail"],
                    IsSelected = true,
                    IsStale = true,
                },
            ],
        });

        state.OperationJournal.Add(new JournalEntry
        {
            Id = operationId,
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Phase = JournalPhase.Completed,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"C:\State\Holding\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity
            {
                Fingerprint = "incoming",
                FileSize = 1234,
                LastWriteUtc = timestamp,
            },
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp.AddMinutes(1),
            LastError = "recovered",
        });

        state.History.Add(new ActivityEntry
        {
            Id = Guid.NewGuid(),
            OccurredUtc = timestamp,
            Kind = ActivityKind.ReviewDecision,
            Level = ActivityLevel.Warning,
            Category = VrcImageCategory.Emoji,
            Message = "Queued for reconciliation.",
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"C:\State\Holding\Emoji\sample.png",
            OperationId = operationId,
        });

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equivalent(state, loaded, strict: true);
    }

    [Fact]
    public async Task InterruptedTemporaryWriteLeavesLastSnapshotReadable()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.Settings.SimilarityProfile = SimilarityProfile.Strict;
        await store.SaveAsync(state);

        await File.WriteAllTextAsync(store.TemporaryPath, """{"schemaVersion":1,"settings":""");

        var loaded = await store.LoadAsync();

        Assert.Equal(SimilarityProfile.Strict, loaded.Settings.SimilarityProfile);
        Assert.True(File.Exists(store.TemporaryPath));
    }

    [Fact]
    public async Task SavingAStaleSnapshotCannotEraseANewerJournalEntry()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var stale = await store.LoadAsync();
        var journal = new OperationJournal(store);
        await journal.RecordIntentAsync(new JournalEntry
        {
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"D:\Archive\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });

        stale.Settings.SimilarityProfile = SimilarityProfile.Broad;

        var conflict = await Assert.ThrowsAsync<StateRevisionConflictException>(
            () => store.SaveAsync(stale));
        var current = await store.LoadAsync();

        Assert.Equal(stale.Revision + 1, conflict.ActualRevision);
        Assert.Single(current.OperationJournal);
        Assert.NotEqual(SimilarityProfile.Broad, current.Settings.SimilarityProfile);
    }

    [Fact]
    public async Task ClearLocalDataIsBlockedByPendingJournalOperation()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var journal = new OperationJournal(store);
        var operation = await journal.RecordIntentAsync(new JournalEntry
        {
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"D:\Archive\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByPendingOperation, result.Status);
        Assert.Equal([operation.Id], result.BlockingIds);
    }

    [Fact]
    public async Task ClearLocalDataNeverDeletesAnOrphanedHeldImage()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        Directory.CreateDirectory(state.Settings.HoldingRootPath);
        var orphan = Path.Combine(state.Settings.HoldingRootPath, "orphan.png");
        await File.WriteAllTextAsync(orphan, "image bytes");

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByHeldFiles, result.Status);
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task ClearLocalDataFindsOrphanedHeldImageWhenStateFileIsMissing()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        var defaults = AppStateDefaults.Create(
            directory.GetPath("profile"),
            directory.GetPath("local-app-data"),
            stateDirectory);
        Directory.CreateDirectory(defaults.Settings.HoldingRootPath);
        var orphan = Path.Combine(defaults.Settings.HoldingRootPath, "orphan.png");
        await File.WriteAllTextAsync(orphan, "image bytes");
        using var store = CreateStore(directory);

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByHeldFiles, result.Status);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task ClearLocalDataRejectsHoldingRootOutsideStateDirectory()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var outside = directory.GetPath("unrelated-empty-folder", "nested");
        Directory.CreateDirectory(outside);
        var state = await store.LoadAsync();
        state.Settings.HoldingRootPath = Path.GetDirectoryName(outside)!;
        await store.SaveAsync(state);

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByUnsafeHoldingRoot, result.Status);
        Assert.True(Directory.Exists(outside));
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task LoadingCompactsResolvedReviewsCompletedOperationsAndOldHistory()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Resolved,
            IncomingOriginalPath = "resolved.png",
            RoutingContext = new ScanRoutingContext(),
        });
        state.OperationJournal.Add(new JournalEntry
        {
            Id = Guid.NewGuid(),
            Phase = JournalPhase.Completed,
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            SourcePath = "source.png",
            DestinationPath = "destination.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });
        state.History.AddRange(Enumerable.Range(0, AppStateCompactor.MaximumHistoryEntries + 5).Select(index =>
            new ActivityEntry
            {
                Id = Guid.NewGuid(),
                OccurredUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                Message = index.ToString(),
            }));

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.ReviewQueue);
        Assert.Empty(loaded.OperationJournal);
        Assert.Equal(AppStateCompactor.MaximumHistoryEntries, loaded.History.Count);
        Assert.Equal("5", loaded.History[0].Message);
    }

    [Fact]
    public async Task ClearLocalDataIsBlockedByHeldReviewAndSucceedsAfterResolution()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var reviewId = Guid.NewGuid();
        state.ReviewQueue.Add(new ReviewItem
        {
            Id = reviewId,
            Category = VrcImageCategory.Prints,
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = @"C:\Incoming\Prints\held.png",
            HeldFilePath = @"C:\State\Holding\Prints\held.png",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await store.SaveAsync(state);

        var blocked = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByPendingReview, blocked.Status);
        Assert.Equal([reviewId], blocked.BlockingIds);
        Assert.True(File.Exists(store.StatePath));

        state.ReviewQueue.Single().Status = ReviewStatus.Resolved;
        await store.SaveAsync(state);
        var cleared = await store.TryClearLocalDataAsync();

        Assert.True(cleared.WasCleared);
        Assert.False(File.Exists(store.StatePath));
        Assert.False(File.Exists(store.TemporaryPath));
    }

    [Fact]
    public async Task ClearLocalDataRemovesBackupAndQuarantinedStateFiles()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        await store.SaveAsync(state);
        var quarantine = Path.Combine(store.StateDirectory, "state.corrupt-test.json");
        await File.WriteAllTextAsync(quarantine, "corrupt");

        var result = await store.TryClearLocalDataAsync();

        Assert.True(result.WasCleared);
        Assert.False(File.Exists(store.StatePath));
        Assert.False(File.Exists(store.BackupPath));
        Assert.False(File.Exists(quarantine));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreviousMatcherIndexesAndReviewsRequireRefresh(int schemaVersion)
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = schemaVersion;
        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = directory.GetPath("incoming.png"),
            HeldFilePath = directory.GetPath("held.png"),
            RoutingContext = new ScanRoutingContext(),
        });
        foreach (var category in legacy.ArchiveIndex.Categories)
        {
            category.Status = IndexStatus.Current;
            category.LastError = null;
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.All(
            migrated.ArchiveIndex.Categories,
            category =>
            {
                Assert.Equal(IndexStatus.Stale, category.Status);
                Assert.Contains("fingerprints", category.LastError, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal(ReviewStatus.NeedsReconciliation, Assert.Single(migrated.ReviewQueue).Status);
    }

    [Fact]
    public async Task VersionOneSiblingArchivesMigrateToTheirCommonOutputRoot()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = 1;
        var legacyRoot = directory.GetPath("legacy archive");
        foreach (var mapping in legacy.Settings.CategoryMappings)
        {
            mapping.ArchivePath = Path.Combine(legacyRoot, mapping.Category.ToString());
        }

        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = directory.GetPath("profile", "Emoji", "pending.png"),
            HeldFilePath = directory.GetPath("local", "Holding", "pending.png"),
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = directory.GetPath("profile", "Emoji"),
                OutputRootPath = legacyRoot,
            },
        });

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(Path.GetFullPath(legacyRoot), migrated.Settings.OutputRootPath);
        Assert.Empty(migrated.Settings.LegacyArchiveMappings);
        Assert.All(migrated.ArchiveIndex.Categories, index => Assert.Equal(IndexStatus.Stale, index.Status));
        Assert.Equal(ReviewStatus.NeedsReconciliation, Assert.Single(migrated.ReviewQueue).Status);
    }

    [Fact]
    public async Task VersionOneUnrelatedArchivesRequireOutputConfirmationAndRepairReviewRouting()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = 1;
        foreach (var mapping in legacy.Settings.CategoryMappings)
        {
            mapping.ArchivePath = directory.GetPath($"legacy-{mapping.Category}");
        }

        var emoji = legacy.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji);
        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(emoji.SourcePath, "2025-05", "held.png"),
            HeldFilePath = directory.GetPath("holding", "held.png"),
            RoutingContext = new ScanRoutingContext(),
        });
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.False(migrated.Settings.OutputRootConfirmed);
        Assert.Equal(3, migrated.Settings.LegacyArchiveMappings.Count);
        var review = Assert.Single(migrated.ReviewQueue);
        Assert.Equal("2025-05", review.RoutingContext.RelativeDirectory);
        Assert.False(string.IsNullOrWhiteSpace(review.RoutingContext.OutputRootPath));
    }

    private static JsonStateStore CreateStore(TestDirectory directory) =>
        new(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(
                directory.GetPath("profile"),
                directory.GetPath("local-app-data"),
                directory.GetPath("state")));
}
