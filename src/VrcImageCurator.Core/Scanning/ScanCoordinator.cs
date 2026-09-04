using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.Core.Scanning;

public sealed record CategoryScanResult(
    VrcImageCategory Category,
    int Examined,
    int MovedUnique,
    int HeldForReview,
    int Skipped,
    IReadOnlyList<string> Errors);

public sealed record ScanProgress(VrcImageCategory Category, int ScannedImages, int TotalImages);

public sealed record ScanProcessingProgress(int ProcessedImages, int TotalImages);

public sealed class ScanCoordinator
{
    private readonly JsonStateStore _stateStore;
    private readonly ArchiveIndexer _indexer;
    private readonly ImageDecoder _decoder;
    private readonly FileRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _settleDelay;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public ScanCoordinator(
        JsonStateStore stateStore,
        ArchiveIndexer indexer,
        ImageDecoder decoder,
        FileRouter router,
        TimeSpan? settleDelay = null,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _settleDelay = settleDelay ?? TimeSpan.Zero;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(CancellationToken cancellationToken = default) =>
        ScanAllAsync(progress: null, processingProgress: null, cancellationToken);

    public Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default) =>
        ScanAllAsync(progress, processingProgress: null, cancellationToken);

    public async Task<IReadOnlyList<CategoryScanResult>> ScanAllAsync(
        IProgress<ScanProgress>? progress,
        IProgress<ScanProcessingProgress>? processingProgress,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var mappings = state.Settings.CategoryMappings.Where(item => item.IsEnabled).ToArray();
            if (mappings.Length == 0)
            {
                return [];
            }

            var snapshots = mappings.ToDictionary(
                mapping => mapping.Category,
                mapping => state.Settings.OutputRootConfirmed && Directory.Exists(mapping.SourcePath)
                    ? CreateSourceSnapshotSafe(mapping.SourcePath)
                    : SourceSnapshot.Empty);
            var totalImages = snapshots.Values.Sum(snapshot => snapshot.Paths.Count);
            var scannedImages = 0;
            var processedImages = 0;
            progress?.Report(new ScanProgress(mappings[0].Category, 0, totalImages));
            processingProgress?.Report(new ScanProcessingProgress(0, totalImages));
            var results = new List<CategoryScanResult>(mappings.Length);
            foreach (var mapping in mappings)
            {
                try
                {
                    results.Add(await ScanCategoryCoreAsync(
                            mapping.Category,
                            mapping.SourcePath,
                            requireEnabled: true,
                            snapshots[mapping.Category],
                            onImageScanned: () =>
                            {
                                scannedImages++;
                                progress?.Report(new ScanProgress(mapping.Category, scannedImages, totalImages));
                            },
                            onImageProcessed: () =>
                            {
                                processedImages++;
                                processingProgress?.Report(
                                    new ScanProcessingProgress(processedImages, totalImages));
                            },
                            cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or NotSupportedException
                        or ArgumentException)
                {
                    results.Add(new CategoryScanResult(mapping.Category, 0, 0, 0, 0, [exception.Message]));
                }
            }

            return results;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public Task<CategoryScanResult> ScanCategoryAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        ScanCategoryCoreWithLockAsync(category, cancellationToken);

    public Task<CategoryScanResult> ScanCategoryAfterArchiveChangeAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        ScanCategoryCoreWithLockAsync(category, cancellationToken);

    private async Task<CategoryScanResult> ScanCategoryCoreWithLockAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sourceRoot = state.Settings.CategoryMappings.Single(item => item.Category == category).SourcePath;
            return await ScanCategoryCoreAsync(
                    category,
                    sourceRoot,
                    requireEnabled: true,
                    sourceSnapshot: null,
                    onImageScanned: null,
                    onImageProcessed: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        ScanFolderAsync(sourceRoot, category, progress: null, processingProgress: null, cancellationToken);

    public Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default) =>
        ScanFolderAsync(sourceRoot, category, progress, processingProgress: null, cancellationToken);

    public async Task<CategoryScanResult> ScanFolderAsync(
        string sourceRoot,
        VrcImageCategory category,
        IProgress<ScanProgress>? progress,
        IProgress<ScanProcessingProgress>? processingProgress,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = PathBoundary.Normalize(sourceRoot);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var disallowed = state.Settings.CategoryMappings
                .Where(mapping => mapping.IsEnabled)
                .Select(mapping => mapping.SourcePath)
                .Append(state.Settings.OutputRootPath)
                .Append(state.Settings.HoldingRootPath)
                .Concat(state.Settings.LegacyArchiveMappings.Select(mapping => mapping.ArchivePath))
                .Where(path => !string.IsNullOrWhiteSpace(path));
            if (disallowed.Any(path => PathBoundary.Overlaps(normalizedSource, path)))
            {
                throw new InvalidOperationException(
                    "The selected folder overlaps a configured source, output, or holding folder.");
            }

            var snapshot = state.Settings.OutputRootConfirmed
                ? CreateSourceSnapshotSafe(normalizedSource)
                : SourceSnapshot.Empty;
            var totalImages = snapshot.Paths.Count;
            var scannedImages = 0;
            var processedImages = 0;
            progress?.Report(new ScanProgress(category, scannedImages, totalImages));
            processingProgress?.Report(new ScanProcessingProgress(processedImages, totalImages));
            return await ScanCategoryCoreAsync(
                    category,
                    normalizedSource,
                    requireEnabled: false,
                    snapshot,
                    onImageScanned: () =>
                    {
                        scannedImages++;
                        progress?.Report(new ScanProgress(category, scannedImages, totalImages));
                    },
                    onImageProcessed: () =>
                    {
                        processedImages++;
                        processingProgress?.Report(
                            new ScanProcessingProgress(processedImages, totalImages));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<CategoryScanResult> ScanCategoryCoreAsync(
        VrcImageCategory category,
        string sourceRoot,
        bool requireEnabled,
        SourceSnapshot? sourceSnapshot,
        Action? onImageScanned,
        Action? onImageProcessed,
        CancellationToken cancellationToken)
    {
        var initial = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!initial.Settings.OutputRootConfirmed)
        {
            return new CategoryScanResult(
                category,
                0,
                0,
                0,
                0,
                ["Confirm the migrated main output folder in Settings before scanning."]);
        }

        var mapping = initial.Settings.CategoryMappings.Single(item => item.Category == category);
        if ((requireEnabled && !mapping.IsEnabled) || !Directory.Exists(sourceRoot))
        {
            return new CategoryScanResult(category, 0, 0, 0, 0, ["Source folder is unavailable or disabled."]);
        }

        sourceSnapshot ??= CreateSourceSnapshotSafe(sourceRoot);
        if (sourceSnapshot.Error is not null)
        {
            return new CategoryScanResult(category, 0, 0, 0, 0, [sourceSnapshot.Error]);
        }

        var indexResult = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
        if (indexResult.Status != IndexStatus.Current)
        {
            ReportUnprocessed(sourceSnapshot.Paths.Count, onImageScanned, onImageProcessed);
            return new CategoryScanResult(category, 0, 0, 0, 0, indexResult.Errors);
        }

        // Individual unreadable archive files are surfaced as scan warnings, not as a
        // reason to abort the category.
        var errors = new List<string>(
            indexResult.SkippedFiles.Select(skipped => $"Archive file skipped - {skipped}"));
        var examined = 0;
        var moved = 0;
        var held = 0;
        var skipped = 0;
        var paths = sourceSnapshot.Paths;
        skipped = sourceSnapshot.UnsupportedFiles;
        var settledPaths = await FindSettledPathsAsync(paths, cancellationToken).ConfigureAwait(false);
        var queuedPaths = initial.ReviewQueue
            .Where(item => item.Status != ReviewStatus.Resolved)
            .Select(item => item.IncomingOriginalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            var readingReported = false;
            try
            {
                if (queuedPaths.Contains(path))
                {
                    skipped++;
                    continue;
                }

                if (!settledPaths.Contains(path))
                {
                    errors.Add($"{path}: file is still being written or locked.");
                    continue;
                }

                var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
                onImageScanned?.Invoke();
                readingReported = true;
                if (!decoded.IsSuccess)
                {
                    errors.Add($"{path}: {decoded.Failure!.Message}");
                    continue;
                }

                var fingerprint = ImageFingerprint.Create(decoded.Image!);
                var relativeDirectory = Path.GetRelativePath(
                    sourceRoot,
                    Path.GetDirectoryName(path) ?? sourceRoot);
                var routingContext = new ScanRoutingContext
                {
                    SourceRootPath = sourceRoot,
                    RelativeDirectory = relativeDirectory == "." ? string.Empty : relativeDirectory,
                    OutputRootPath = initial.Settings.OutputRootPath,
                };
                var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var index = current.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (index.Status != IndexStatus.Current)
                {
                    errors.Add($"{path}: archive index became stale; rescan required.");
                    continue;
                }

                var candidates = index.Images
                    .Where(item => item.Fingerprint is not null)
                    .Select(item => new ImageCandidate(item.Id.ToString("N"), item.Fingerprint!))
                    .ToArray();
                var matches = ImageMatcher.RankCandidates(
                    fingerprint,
                    candidates,
                    current.Settings.SimilarityProfile);

                if (matches.Count > 0)
                {
                    var review = new ReviewItem
                    {
                        Id = Guid.NewGuid(),
                        Category = category,
                        Status = ReviewStatus.Pending,
                        IncomingOriginalPath = path,
                        HeldFilePath = path,
                        IncomingFingerprint = fingerprint.ExactIdentity,
                        IncomingImageFingerprint = fingerprint,
                        IndexGeneration = index.Generation,
                        CreatedUtc = _timeProvider.GetUtcNow(),
                        RoutingContext = routingContext,
                        Candidates = matches.Select(
                                match =>
                                {
                                    var indexed = index.Images.Single(
                                        item => item.Id.ToString("N") == match.CandidateKey);
                                    return new ReviewCandidate
                                    {
                                        Id = Guid.NewGuid(),
                                        IndexedImageId = indexed.Id,
                                        ArchivePath = indexed.Path,
                                        ExpectedFingerprint = indexed.ExactFingerprint,
                                        MatchKind = match.MatchKind,
                                        SimilarityScore = match.SimilarityScore,
                                        MatchReasons = match.MatchReasons.ToList(),
                                    };
                                })
                            .ToList(),
                    };
                    await _router.QueueForReviewAsync(review, cancellationToken).ConfigureAwait(false);
                    queuedPaths.Add(path);
                    held++;
                    continue;
                }

                var beforeMove = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var freshIndex = beforeMove.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (freshIndex.Status != IndexStatus.Current || freshIndex.Generation != index.Generation)
                {
                    errors.Add($"{path}: index generation changed before routing; retry required.");
                    continue;
                }

                await _router.MoveUniqueAsync(
                        path,
                        category,
                        fingerprint,
                        routingContext,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                moved++;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
            finally
            {
                if (!readingReported)
                {
                    onImageScanned?.Invoke();
                }
                onImageProcessed?.Invoke();
            }
        }

        await _stateStore.UpdateAsync(
                state =>
                {
                    state.History.Add(new ActivityEntry
                    {
                        Id = Guid.NewGuid(),
                        OccurredUtc = _timeProvider.GetUtcNow(),
                        Kind = ActivityKind.Scan,
                        Level = errors.Count == 0 ? ActivityLevel.Information : ActivityLevel.Warning,
                        Category = category,
                        Message = $"Scanned {sourceRoot}: {examined} examined, {moved} moved, {held} queued, {skipped} skipped, {errors.Count} failed.",
                        SourcePath = sourceRoot,
                    });
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

        return new CategoryScanResult(category, examined, moved, held, skipped, errors);
    }

    private static SourceSnapshot CreateSourceSnapshotSafe(string sourceRoot)
    {
        try
        {
            var allPaths = PathBoundary.EnumerateFilesWithoutReparsePoints(sourceRoot);
            var paths = allPaths
                .Where(path => ArchiveIndexer.SupportedExtensions.Contains(Path.GetExtension(path)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new SourceSnapshot(paths, allPaths.Count - paths.Length, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SourceSnapshot([], 0, exception.Message);
        }
    }

    private static void ReportUnprocessed(
        int count,
        Action? onImageScanned,
        Action? onImageProcessed)
    {
        for (var index = 0; index < count; index++)
        {
            onImageScanned?.Invoke();
            onImageProcessed?.Invoke();
        }
    }

    private sealed record SourceSnapshot(IReadOnlyList<string> Paths, int UnsupportedFiles, string? Error)
    {
        public static SourceSnapshot Empty { get; } = new([], 0, null);
    }

    private async Task<HashSet<string>> FindSettledPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var first = new Dictionary<string, FileObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = TryObserveReadableFile(path);
            if (observation is not null)
            {
                first[path] = observation;
            }
        }

        if (first.Count > 0 && _settleDelay > TimeSpan.Zero)
        {
            await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
        }

        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, firstObservation) in first)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryObserveReadableFile(path) == firstObservation)
            {
                settled.Add(path);
            }
        }

        return settled;
    }

    private static FileObservation? TryObserveReadableFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return new FileObservation(stream.Length, file.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record FileObservation(long Length, DateTime LastWriteTimeUtc);
}
