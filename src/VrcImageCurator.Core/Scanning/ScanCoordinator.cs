using VrcImageCurator.Core.Atlas;
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
    IReadOnlyList<string> Errors,
    int AutoKeptArchived = 0,
    int Animated = 0);

/// <summary><see cref="Activity"/> and <see cref="FileName"/> describe what the scan is doing
/// right now, so the UI can say more than a bare count.</summary>
public sealed record ScanProgress(
    VrcImageCategory Category,
    int ScannedImages,
    int TotalImages,
    string Activity = "",
    string? FileName = null);

public sealed record ScanProcessingProgress(
    int ProcessedImages,
    int TotalImages,
    string Activity = "",
    string? FileName = null);

public sealed class ScanCoordinator
{
    private readonly JsonStateStore _stateStore;
    private readonly ArchiveIndexer _indexer;
    private readonly ImageDecoder _decoder;
    private readonly FileRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _settleDelay;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly AtlasAnimationWriter _animationWriter = new();

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
                            onImageScanned: (activity, file) =>
                            {
                                scannedImages++;
                                progress?.Report(new ScanProgress(
                                    mapping.Category,
                                    scannedImages,
                                    totalImages,
                                    activity,
                                    file));
                            },
                            onImageProcessed: (activity, file) =>
                            {
                                processedImages++;
                                processingProgress?.Report(
                                    new ScanProcessingProgress(processedImages, totalImages, activity, file));
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

    /// <summary>
    /// Scans only the given incoming paths, ignoring everything else in the source folder.
    /// Folder watching uses this so a newly added image costs one decode instead of a full
    /// sweep. Paths outside the category's configured source folder are never processed.
    /// </summary>
    public async Task<CategoryScanResult> ScanIncomingPathsAsync(
        VrcImageCategory category,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sourceRoot = state.Settings.CategoryMappings
                .Single(item => item.Category == category)
                .SourcePath;
            var snapshot = CreateSnapshotFromPaths(sourceRoot, paths);
            if (snapshot.Error is null && snapshot.Paths.Count == 0)
            {
                return new CategoryScanResult(category, 0, 0, 0, snapshot.UnsupportedFiles, []);
            }

            return await ScanCategoryCoreAsync(
                    category,
                    sourceRoot,
                    requireEnabled: true,
                    snapshot,
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
                    onImageScanned: (activity, file) =>
                    {
                        scannedImages++;
                        progress?.Report(new ScanProgress(
                            category,
                            scannedImages,
                            totalImages,
                            activity,
                            file));
                    },
                    onImageProcessed: (activity, file) =>
                    {
                        processedImages++;
                        processingProgress?.Report(
                            new ScanProcessingProgress(processedImages, totalImages, activity, file));
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
        Action<string, string?>? onImageScanned,
        Action<string, string?>? onImageProcessed,
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

        // The output folder is where this scan puts files, so create it on demand instead of
        // failing. Source folders are never created: an empty one would have nothing to scan.
        // This runs after the snapshot so a failure can still complete both progress streams.
        try
        {
            Directory.CreateDirectory(mapping.ArchivePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportUnprocessed(
                sourceSnapshot.Paths.Count,
                "Output folder unavailable",
                onImageScanned,
                onImageProcessed);
            return new CategoryScanResult(
                category,
                0,
                0,
                0,
                0,
                [$"The {category} output folder could not be created: {exception.Message}"]);
        }

        var indexResult = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
        if (indexResult.Status != IndexStatus.Current)
        {
            ReportUnprocessed(
                sourceSnapshot.Paths.Count,
                "Archive index unavailable",
                onImageScanned,
                onImageProcessed);
            return new CategoryScanResult(category, 0, 0, 0, 0, indexResult.Errors);
        }

        // Individual unreadable archive files are surfaced as scan warnings, not as a
        // reason to abort the category.
        var errors = new List<string>(
            indexResult.SkippedFiles.Select(skipped => $"Archive file skipped - {skipped}"));

        // A sheet whose pixels disagree with its name is worth saying out loud, but it is not a
        // failure: the export did exactly what it was asked to. Kept apart from the errors so it
        // lands in the history as information rather than raising "scan completed with warnings"
        // over a sheet that animated perfectly well.
        var notes = new List<string>();
        var examined = 0;
        var moved = 0;
        var held = 0;
        var autoKept = 0;
        var skipped = 0;

        // Sheets already in the archive were archived before this existed, so animating only what
        // a scan newly archives would leave the whole existing archive untouched. This fills in
        // whatever is missing, which also means a deleted animation comes back on the next scan.
        // A sheet whose pixels contradict its name is left for the Animations tab rather than
        // reported here, or it would warn on every scan forever.
        var animated = await AnimateArchivedSheetsAsync(
                category,
                mapping.ArchivePath,
                errors,
                notes,
                cancellationToken)
            .ConfigureAwait(false);
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
            var fileName = Path.GetFileName(path);
            var readingReported = false;
            var outcome = "Skipped";
            try
            {
                if (queuedPaths.Contains(path))
                {
                    skipped++;
                    outcome = "Already in the review queue";
                    continue;
                }

                if (!settledPaths.Contains(path))
                {
                    errors.Add($"{path}: file is still being written or locked.");
                    outcome = "Still being written";
                    continue;
                }

                var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
                onImageScanned?.Invoke("Reading", fileName);
                readingReported = true;
                if (!decoded.IsSuccess)
                {
                    errors.Add($"{path}: {decoded.Failure!.Message}");
                    outcome = "Could not be read";
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
                    outcome = "Archive index went stale";
                    continue;
                }

                // Everything the incoming image may be compared against. Normally that is just the
                // index, but a ready-made GIF is compared against the animation the archive's own
                // sheet produces - generating it first if it has not been made yet, so there is
                // something to compare against at all.
                var comparable = index.Images.ToDictionary(item => item.Id.ToString("N"), StringComparer.Ordinal);
                if (AtlasAnimationWriter.IsAnimation(path))
                {
                    var companion = await BuildCompanionAnimationAsync(
                            index,
                            path,
                            mapping.ArchivePath,
                            errors,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (companion is not null)
                    {
                        comparable[companion.Id.ToString("N")] = companion;
                    }
                }

                var candidates = comparable.Values
                    .Where(item => item.Fingerprint is not null)
                    .Select(item => new ImageCandidate(item.Id.ToString("N"), item.Fingerprint!))
                    .ToArray();
                var matches = ImageMatcher.RankCandidates(
                    fingerprint,
                    candidates,
                    current.Settings.SimilarityProfile);

                if (matches.Count > 0)
                {
                    // A match reported as 100% is the same picture, so the archived copy wins
                    // without asking. Everything below 100% is a judgement call and goes to
                    // review, as does a 100% match at a different resolution.
                    IndexedImageRecord? duplicate = null;
                    foreach (var match in matches)
                    {
                        var indexed = comparable[match.CandidateKey];
                        if (indexed.Fingerprint is not null
                            && ImageMatcher.IsSamePicture(match, fingerprint, indexed.Fingerprint))
                        {
                            duplicate = indexed;
                            break;
                        }
                    }

                    if (duplicate is not null)
                    {
                        try
                        {
                            await _router.AutoKeepArchivedAsync(
                                    path,
                                    category,
                                    fingerprint,
                                    duplicate.Path,
                                    duplicate.ExactFingerprint,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            autoKept++;
                            outcome = "Exact duplicate recycled";
                            continue;
                        }
                        catch (Exception exception) when (
                            exception is NotSupportedException
                                or InvalidOperationException
                                or IOException
                                or UnauthorizedAccessException)
                        {
                            // The Recycle Bin was unavailable, or the archived copy changed
                            // between indexing and now. Never delete and never drop the image:
                            // fall through and let the user decide.
                            errors.Add($"{path}: could not resolve automatically ({exception.Message}).");
                        }
                    }

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
                                    var indexed = comparable[match.CandidateKey];
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
                    outcome = "Queued for review";
                    continue;
                }

                var beforeMove = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var freshIndex = beforeMove.ArchiveIndex.Categories.Single(item => item.Category == category);
                if (freshIndex.Status != IndexStatus.Current || freshIndex.Generation != index.Generation)
                {
                    errors.Add($"{path}: index generation changed before routing; retry required.");
                    outcome = "Index changed; retry needed";
                    continue;
                }

                var route = await _router.MoveUniqueAsync(
                        path,
                        category,
                        fingerprint,
                        routingContext,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                moved++;
                outcome = "Archived as unique";

                // VRChat writes the frame count, rate and loop direction into the name of an
                // animated emoji, so a sheet identifies itself and needs no detection. The image
                // is already archived safely by this point, so a failure to animate it is a
                // warning on the scan rather than a failure of the image.
                if (route.DestinationPath is { } archivedPath)
                {
                    // An animation may already be sitting there - VRCX hands over ready-made GIFs
                    // for some emoji, and one that arrived earlier is archived under exactly the
                    // name this sheet's export would take. Writing over it would destroy an
                    // archived file and leave the index describing pixels that no longer exist, so
                    // the existing animation is adopted instead. Re-exporting from the Animations
                    // tab still overwrites, because there a person has asked for it.
                    var animation = await AnimateOrAdoptAsync(
                            archivedPath,
                            mapping.ArchivePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (animation.Exported)
                    {
                        animated++;
                        outcome = "Archived as unique, animated";

                        // Said out loud rather than swallowed. The sheet was animated to its name
                        // and then filed away as finished, so the history is the only place a
                        // person would ever hear what the export made of it.
                        if (animation.Note is { } exportNote)
                        {
                            notes.Add($"{archivedPath}: {exportNote}");
                        }

                        // The sheet has served its purpose as a still, so it is filed with the
                        // animation rather than left among the images a person browses. Only a
                        // sheet that actually produced a GIF moves: one still waiting on review is
                        // unfinished work and stays where it can be seen.
                        await FileAnimatedSheetAsync(
                                route,
                                archivedPath,
                                category,
                                fingerprint,
                                mapping.ArchivePath,
                                errors,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (animation.Warning is { } warning)
                    {
                        errors.Add($"{archivedPath}: {warning}");
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                errors.Add($"{path}: {exception.Message}");
                outcome = "Failed";
            }
            finally
            {
                if (!readingReported)
                {
                    onImageScanned?.Invoke("Reading", fileName);
                }

                onImageProcessed?.Invoke(outcome, fileName);
            }
        }

        await _stateStore.UpdateAsync(
                state =>
                {
                    foreach (var note in notes)
                    {
                        state.History.Add(new ActivityEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredUtc = _timeProvider.GetUtcNow(),
                            Kind = ActivityKind.Scan,
                            Level = ActivityLevel.Information,
                            Category = category,
                            Message = note,
                            SourcePath = sourceRoot,
                        });
                    }

                    state.History.Add(new ActivityEntry
                    {
                        Id = Guid.NewGuid(),
                        OccurredUtc = _timeProvider.GetUtcNow(),
                        Kind = ActivityKind.Scan,
                        Level = errors.Count == 0 ? ActivityLevel.Information : ActivityLevel.Warning,
                        Category = category,
                        Message = $"Scanned {sourceRoot}: {examined} examined, {moved} moved, {animated} animated, {autoKept} exact duplicates recycled, {held} queued, {skipped} skipped, {errors.Count} failed.",
                        SourcePath = sourceRoot,
                    });
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

        return new CategoryScanResult(category, examined, moved, held, skipped, errors, autoKept, animated);
    }

    /// <summary>
    /// Makes the animation for a freshly archived sheet, unless one already stands in its place.
    /// </summary>
    /// <remarks>
    /// The existing file is only adopted when it really is this sheet's animation, checked by frame
    /// count. Two different emoji can carry the same file name - the archive root disambiguates
    /// them, but the animation folder is reached by name alone - and adopting a stranger's GIF
    /// would leave this sheet reported as animated while no animation of it exists anywhere.
    /// </remarks>
    private async Task<AtlasAnimationResult> AnimateOrAdoptAsync(
        string archivedPath,
        string archiveRoot,
        CancellationToken cancellationToken)
    {
        if (!EmojiAtlasName.TryParse(archivedPath, out var name))
        {
            return AtlasAnimationResult.NotASheet;
        }

        try
        {
            var destination = AtlasAnimationWriter.BuildDestination(archivedPath, archiveRoot);
            if (File.Exists(destination))
            {
                return await AdoptAsync(destination, name, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Fall through and let the writer report the failure in its own words.
        }

        return await _animationWriter
            .TryWriteAsync(archivedPath, archiveRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the animation already sitting where this sheet's own would go, if it plays the
    /// number of frames this sheet's name promises.
    /// </summary>
    private async Task<AtlasAnimationResult> AdoptAsync(
        string destination,
        EmojiAtlasName name,
        CancellationToken cancellationToken)
    {
        var decoded = await _decoder.DecodeAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess)
        {
            return new AtlasAnimationResult(
                false,
                null,
                $"an animation already sits at {destination} but could not be read, so this sheet was left alone.");
        }

        // Ping-pong plays out and back without repeating either end, so 4 frames play as 6.
        var played = name.LoopStyle == AtlasLoopStyle.PingPong && name.FrameCount > 2
            ? (name.FrameCount * 2) - 2
            : name.FrameCount;
        if (decoded.Image!.Frames.Count != played)
        {
            return new AtlasAnimationResult(
                false,
                null,
                $"a different animation already sits at {destination} - it plays "
                    + $"{decoded.Image!.Frames.Count} frames where this sheet promises {played} - so this "
                    + "sheet was left alone rather than being reported as animated.");
        }

        return new AtlasAnimationResult(true, destination, null);
    }

    /// <summary>
    /// The animation the archive's own sheet produces for an incoming GIF, so the two can be
    /// compared, or null when the archive holds no sheet for it.
    /// </summary>
    /// <remarks>
    /// A ready-made GIF and a sheet are never alike as pixels - one is a frame playing, the other a
    /// grid of every frame - so comparing them directly would always say "different" and archive
    /// both. What can be compared is animation against animation, and the sheet can produce one.
    /// So the sheet's own GIF is made first, if it does not exist yet, and the incoming file is
    /// judged against that by exactly the same rules as any other pair of images: identical means
    /// the archived one wins and the incoming copy is recycled, anything short of identical goes to
    /// review.
    /// The record handed back is not written to the index. The next scan indexes the generated file
    /// properly; this one only needs something to hold the fingerprint while the decision is made.
    /// </remarks>
    private async Task<IndexedImageRecord?> BuildCompanionAnimationAsync(
        CategoryIndexState index,
        string incomingAnimationPath,
        string archiveRoot,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var sheet = index.Images.FirstOrDefault(
            item => AtlasAnimationWriter.IsAnimationOf(incomingAnimationPath, item.Path));
        if (sheet is null)
        {
            return null;
        }

        try
        {
            var companionPath = AtlasAnimationWriter.BuildDestination(sheet.Path, archiveRoot);

            // Animations are indexed, so the sheet's own is usually already a candidate. Adding a
            // second record for the same file would put two rows with one path into the review,
            // one of them carrying an id the index has never heard of.
            if (index.Images.Any(item => string.Equals(item.Path, companionPath, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (!File.Exists(companionPath))
            {
                var written = await _animationWriter
                    .TryWriteAsync(sheet.Path, archiveRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!written.Exported)
                {
                    // The sheet cannot be animated, so there is nothing to compare against and the
                    // incoming GIF is the only copy of this animation there is. Let it through.
                    return null;
                }

                companionPath = written.Path ?? companionPath;
            }

            var decoded = await _decoder.DecodeAsync(companionPath, cancellationToken).ConfigureAwait(false);
            if (!decoded.IsSuccess)
            {
                return null;
            }

            var fingerprint = ImageFingerprint.Create(decoded.Image!);
            var info = new FileInfo(companionPath);
            return new IndexedImageRecord
            {
                Id = Guid.NewGuid(),
                Category = sheet.Category,
                Path = companionPath,
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Width = decoded.Image!.Width,
                Height = decoded.Image!.Height,
                ExactFingerprint = fingerprint.ExactIdentity,
                PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                Fingerprint = fingerprint,
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException)
        {
            errors.Add($"{incomingAnimationPath}: could not be compared against its sheet ({exception.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Moves a freshly animated sheet into the reference folder beside its animation.
    /// </summary>
    /// <remarks>
    /// A failure here is reported and nothing else: the image is archived, the animation is
    /// written, and the only cost of the sheet staying where it is is that it sits among the
    /// stills. Turning that into a failed scan would be out of proportion to it.
    /// </remarks>
    private async Task FileAnimatedSheetAsync(
        FileRouteResult route,
        string archivedPath,
        VrcImageCategory category,
        ImageFingerprint fingerprint,
        string archiveRoot,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (route.IndexedImageId is not { } indexedImageId)
        {
            return;
        }

        try
        {
            var reference = AtlasAnimationWriter.BuildReferenceDestination(archivedPath, archiveRoot);

            // Two different sheets can carry the same file name - the archive root disambiguates
            // them, but the first one filed vacates that name, so the second arrives thinking it is
            // unique. Moving onto an existing file throws inside the journal and leaves an entry
            // that reconciliation can never settle, so the collision is caught out here instead and
            // the sheet simply stays where it is.
            if (File.Exists(reference))
            {
                errors.Add(
                    $"{archivedPath}: animated, but a different sheet of the same name is already "
                    + "filed with its animation, so this one was left in place.");
                return;
            }

            await _router
                .FileAnimatedSheetAsync(
                    indexedImageId,
                    archivedPath,
                    reference,
                    category,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException)
        {
            errors.Add($"{archivedPath}: animated, but could not be filed with its animation ({exception.Message}).");
        }
    }

    /// <summary>
    /// Writes the missing animations for sheets that were archived before the app could make them.
    /// </summary>
    /// <remarks>
    /// This one only ever adds files. Sheets it animates keep their place in the archive rather
    /// than being filed into the reference folder, because rearranging an archive a person has
    /// already organised is theirs to decide, not a side effect of a scan. Only sheets arriving
    /// from here on are filed.
    /// </remarks>
    private async Task<int> AnimateArchivedSheetsAsync(
        VrcImageCategory category,
        string archiveRoot,
        List<string> errors,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = state.ArchiveIndex.Categories.Single(item => item.Category == category);
        var skipped = new HashSet<string>(state.SkippedAnimations, StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var image in index.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EmojiAtlasName.TryParse(image.Path, out _))
            {
                continue;
            }

            // A skipped sheet is one a person has already looked at and decided against. Without
            // this it would be decoded in full on every single scan forever: a sheet that cannot
            // be animated never produces the file whose absence is what puts it back on the list.
            if (skipped.Contains(image.ExactFingerprint))
            {
                continue;
            }

            string destination;
            try
            {
                destination = AtlasAnimationWriter.BuildDestination(image.Path, archiveRoot);
                if (File.Exists(destination))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{image.Path}: could not be animated ({exception.Message})");
                continue;
            }

            var animation = await _animationWriter
                .TryWriteAsync(image.Path, archiveRoot, cancellationToken)
                .ConfigureAwait(false);
            if (animation.Exported)
            {
                written++;
                if (animation.Note is { } note)
                {
                    notes.Add($"{image.Path}: {note}");
                }
            }
            else if (animation.Warning is { } warning)
            {
                errors.Add($"{image.Path}: {warning}");
            }
        }

        return written;
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

    private static SourceSnapshot CreateSnapshotFromPaths(
        string sourceRoot,
        IReadOnlyCollection<string> paths)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return SourceSnapshot.Empty;
        }

        try
        {
            var root = PathBoundary.Normalize(sourceRoot);
            var supported = new List<string>();
            var unsupported = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(path);
                    // Only files inside the configured source folder are ever processed, and
                    // never through a junction or symlink.
                    if (!PathBoundary.Contains(root, full) || !File.Exists(full))
                    {
                        continue;
                    }

                    PathBoundary.EnsureNoReparsePoints(full, "Incoming image");
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or NotSupportedException
                        or PathTooLongException
                        or InvalidOperationException)
                {
                    continue;
                }

                if (ArchiveIndexer.SupportedExtensions.Contains(Path.GetExtension(full)))
                {
                    supported.Add(full);
                }
                else
                {
                    unsupported++;
                }
            }

            return new SourceSnapshot(
                supported
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                unsupported,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SourceSnapshot([], 0, exception.Message);
        }
    }

    private static void ReportUnprocessed(
        int count,
        string activity,
        Action<string, string?>? onImageScanned,
        Action<string, string?>? onImageProcessed)
    {
        for (var index = 0; index < count; index++)
        {
            onImageScanned?.Invoke(activity, null);
            onImageProcessed?.Invoke(activity, null);
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

            // Share everything: VRCX may be writing into this folder right now and must never
            // be blocked by this check. Whether a file has settled is decided by comparing two
            // observations, not by holding a lock.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return new FileObservation(stream.Length, file.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record FileObservation(long Length, DateTime LastWriteTimeUtc);
}
