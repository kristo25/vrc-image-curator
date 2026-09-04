using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.Core.Scanning;

public sealed record ArchiveIndexResult(
    VrcImageCategory Category,
    IndexStatus Status,
    long Generation,
    int IndexedFiles,
    IReadOnlyList<string> Errors);

public sealed class ArchiveIndexer
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".gif", ".jpg", ".jpeg", ".webp", ".bmp",
        };

    private readonly JsonStateStore _stateStore;
    private readonly ImageDecoder _decoder;
    private readonly TimeProvider _timeProvider;

    public ArchiveIndexer(
        JsonStateStore stateStore,
        ImageDecoder decoder,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ArchiveIndexResult> RefreshAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        BuildAsync(category, reuseUnchanged: true, attempt: 0, cancellationToken);

    public Task<ArchiveIndexResult> RebuildAsync(
        VrcImageCategory category,
        CancellationToken cancellationToken = default) =>
        BuildAsync(category, reuseUnchanged: false, attempt: 0, cancellationToken);

    private async Task<ArchiveIndexResult> BuildAsync(
        VrcImageCategory category,
        bool reuseUnchanged,
        int attempt,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mapping = state.Settings.CategoryMappings.Single(item => item.Category == category);
        var previousIndex = state.ArchiveIndex.Categories.Single(item => item.Category == category);
        var previousRecords = previousIndex.Images
            .Where(item => item.Fingerprint is not null)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var archiveRoots = state.Settings.LegacyArchiveMappings
            .Where(item => item.Category == category)
            .Select(item => item.ArchivePath)
            .Prepend(mapping.ArchivePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archiveRoots.Length == 0)
        {
            await PublishUnavailableAsync(category, "Archive folder is unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return new ArchiveIndexResult(category, IndexStatus.Unavailable, 0, 0, ["Archive folder is unavailable."]);
        }

        if (!reuseUnchanged)
        {
            await SetStatusAsync(category, IndexStatus.Building, null, cancellationToken).ConfigureAwait(false);
        }

        var errors = new List<string>();
        var indexed = new List<IndexedImageRecord>();
        IEnumerable<string> paths;
        try
        {
            paths = archiveRoots.SelectMany(PathBoundary.EnumerateFilesWithoutReparsePoints)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await PublishUnavailableAsync(category, exception.Message, cancellationToken).ConfigureAwait(false);
            return new ArchiveIndexResult(category, IndexStatus.Unavailable, 0, 0, [exception.Message]);
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (reuseUnchanged
                && previousRecords.TryGetValue(path, out var previous)
                && previous.Fingerprint!.HasCurrentFeatures
                && previous.FileSize == info.Length
                && previous.LastWriteUtc == info.LastWriteTimeUtc)
            {
                indexed.Add(previous);
                continue;
            }

            var decoded = await _decoder.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
            if (!decoded.IsSuccess)
            {
                errors.Add($"{path}: {decoded.Failure!.Message}");
                continue;
            }

            var fingerprint = ImageFingerprint.Create(decoded.Image!);
            previousRecords.TryGetValue(path, out var priorRecord);
            indexed.Add(new IndexedImageRecord
            {
                Id = priorRecord?.Id ?? Guid.NewGuid(),
                Category = category,
                Path = path,
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Width = fingerprint.Width,
                Height = fingerprint.Height,
                ExactFingerprint = fingerprint.ExactIdentity,
                PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                Fingerprint = fingerprint,
            });
        }

        if (errors.Count == 0
            && reuseUnchanged
            && previousIndex.Status == IndexStatus.Current
            && HaveSameFileSet(previousIndex.Images, indexed))
        {
            var current = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var currentIndex = current.ArchiveIndex.Categories.Single(item => item.Category == category);
            if (currentIndex.Generation != previousIndex.Generation)
            {
                if (attempt < 3)
                {
                    return await BuildAsync(category, reuseUnchanged, attempt + 1, cancellationToken).ConfigureAwait(false);
                }

                throw new ArchiveIndexChangedException();
            }

            return new ArchiveIndexResult(
                category,
                IndexStatus.Current,
                previousIndex.Generation,
                indexed.Count,
                errors);
        }

        ArchiveIndexResult result;
        try
        {
            result = await _stateStore.UpdateAsync(
                current =>
                {
                    var index = current.ArchiveIndex.Categories.Single(item => item.Category == category);
                    if (index.Generation != previousIndex.Generation)
                    {
                        throw new ArchiveIndexChangedException();
                    }

                    var changed = !HaveSameFileSet(index.Images, indexed);
                    if (!reuseUnchanged || changed)
                    {
                        index.Generation++;
                    }

                    index.Images = indexed;
                    index.Status = errors.Count == 0 ? IndexStatus.Current : IndexStatus.Unavailable;
                    index.LastCompletedUtc = _timeProvider.GetUtcNow();
                    index.LastError = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Take(10));
                    if (!reuseUnchanged || changed || errors.Count > 0)
                    {
                        current.History.Add(new ActivityEntry
                        {
                            Id = Guid.NewGuid(),
                            OccurredUtc = _timeProvider.GetUtcNow(),
                            Kind = ActivityKind.Scan,
                            Level = errors.Count == 0 ? ActivityLevel.Information : ActivityLevel.Warning,
                            Category = category,
                            Message = errors.Count == 0
                                ? $"Indexed {indexed.Count} archive images."
                                : $"Index stopped with {errors.Count} unreadable image(s).",
                        });
                    }

                    return new ArchiveIndexResult(
                    category,
                    index.Status,
                    index.Generation,
                    indexed.Count,
                    errors);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveIndexChangedException) when (attempt < 3)
        {
            return await BuildAsync(category, reuseUnchanged, attempt + 1, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static bool HaveSameFileSet(
        IReadOnlyCollection<IndexedImageRecord> left,
        IReadOnlyCollection<IndexedImageRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightByPath = right.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        return left.All(
            item => rightByPath.TryGetValue(item.Path, out var other)
                && item.Id == other.Id
                && item.FileSize == other.FileSize
                && item.LastWriteUtc == other.LastWriteUtc
                && item.Fingerprint is not null
                && other.Fingerprint is not null
                && item.Fingerprint.HasCurrentFeatures
                && other.Fingerprint.HasCurrentFeatures);
    }

    public Task MarkStaleAsync(
        VrcImageCategory category,
        string reason,
        CancellationToken cancellationToken = default) =>
        SetStatusAsync(category, IndexStatus.Stale, reason, cancellationToken);

    private Task SetStatusAsync(
        VrcImageCategory category,
        IndexStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        _stateStore.UpdateAsync(
            state =>
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == category);
                index.Status = status;
                index.LastError = error;
                return true;
            },
            cancellationToken);

    private Task PublishUnavailableAsync(
        VrcImageCategory category,
        string error,
        CancellationToken cancellationToken) =>
        SetStatusAsync(category, IndexStatus.Unavailable, error, cancellationToken);

    private sealed class ArchiveIndexChangedException : InvalidOperationException;
}
