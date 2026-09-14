using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Core.FileSystem;

/// <summary>One archive folder that still holds files, and where those files would go.</summary>
public sealed record ArchiveRelocationStep(
    VrcImageCategory Category,
    string From,
    string To,
    int FileCount,
    long TotalBytes);

/// <summary>What a relocation managed to do.</summary>
/// <param name="Moved">Files that reached the new archive.</param>
/// <param name="LeftBehind">Files deliberately not moved, plus any that could not be.</param>
/// <param name="Errors">One line per file that failed, in the words of the failure.</param>
public sealed record ArchiveRelocationResult(
    int Moved,
    int LeftBehind,
    IReadOnlyList<string> Errors);

public sealed record ArchiveRelocationProgress(int Moved, int Total, string FileName);

/// <summary>
/// Moves an archive that has been left behind by a change of output folder.
/// </summary>
/// <remarks>
/// Changing where images are filed has never moved the images already filed, which is the right
/// default - shifting someone's files as a side effect of editing a text box would be rude. What
/// it left behind was a person with their archive in one place, their new output in another, and
/// no way from inside the application to bring the two together.
/// <para>
/// Every archive folder the application knows about is a candidate, not just the current one. A
/// folder that was the output root two changes ago is still indexed and still holds files, and it
/// is exactly as stranded as the one abandoned a moment ago.
/// </para>
/// </remarks>
public static class ArchiveRelocation
{
    /// <summary>
    /// Works out which known archive folders hold files that do not sit under
    /// <paramref name="newOutputRoot"/> yet.
    /// </summary>
    /// <remarks>
    /// A folder that is already the destination is not a step, and neither is an empty one. The
    /// count and total size are read here so the question put to a person can say how much is
    /// about to move rather than asking them to agree to an unknown.
    /// </remarks>
    public static IReadOnlyList<ArchiveRelocationStep> Plan(AppSettings settings, string newOutputRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOutputRoot);

        var root = Path.GetFullPath(newOutputRoot);
        var steps = new List<ArchiveRelocationStep>();

        foreach (var mapping in settings.CategoryMappings.Where(item => item.IsEnabled))
        {
            var destination = Path.Combine(root, mapping.Category.ToString());
            var candidates = settings.LegacyArchiveMappings
                .Where(legacy => legacy.Category == mapping.Category)
                .Select(legacy => legacy.ArchivePath)
                .Prepend(mapping.ArchivePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (PathBoundary.Contains(destination, candidate) || !Directory.Exists(candidate))
                {
                    continue;
                }

                var files = SafeEnumerate(candidate);
                if (files.Count == 0)
                {
                    continue;
                }

                steps.Add(new ArchiveRelocationStep(
                    mapping.Category,
                    PathBoundary.Normalize(candidate),
                    destination,
                    files.Count,
                    files.Sum(SafeLength)));
            }
        }

        return steps;
    }

    /// <summary>
    /// Moves every file of each step into its destination, keeping the folders it sat in.
    /// </summary>
    /// <remarks>
    /// A file whose name is already taken at the destination is left exactly where it is rather
    /// than overwritten. Two archives can hold different images under one name, and the copy
    /// already filed is the one the index is describing.
    /// </remarks>
    public static async Task<ArchiveRelocationResult> RelocateAsync(
        IEnumerable<ArchiveRelocationStep> steps,
        IProgress<ArchiveRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var planned = steps.ToArray();
        var total = planned.Sum(step => step.FileCount);
        var moved = 0;
        var leftBehind = 0;
        var errors = new List<string>();

        foreach (var step in planned)
        {
            foreach (var file in SafeEnumerate(step.From))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(step.From, file);
                var destination = Path.Combine(step.To, relative);
                try
                {
                    if (File.Exists(destination))
                    {
                        leftBehind++;
                        errors.Add($"{file}: a file of that name is already in the new archive.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(file, destination);
                    moved++;
                    progress?.Report(new ArchiveRelocationProgress(moved, total, Path.GetFileName(file)));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    leftBehind++;
                    errors.Add($"{file}: {exception.Message}");
                }

                await Task.Yield();
            }
        }

        return new ArchiveRelocationResult(moved, leftBehind, errors);
    }

    /// <summary>
    /// Points indexed images at their new homes.
    /// </summary>
    /// <remarks>
    /// Without this the whole archive would have to be read and fingerprinted again - minutes of
    /// work to rediscover what is already known. The fingerprints are held in a sidecar keyed by
    /// each record's id, so moving the record's path keeps them.
    /// </remarks>
    /// <returns>How many indexed images were repointed.</returns>
    public static int RebaseIndex(AppStateDocument state, IEnumerable<ArchiveRelocationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(steps);

        var rebased = 0;
        foreach (var step in steps)
        {
            var index = state.ArchiveIndex.Categories.FirstOrDefault(item => item.Category == step.Category);
            if (index is null)
            {
                continue;
            }

            foreach (var image in index.Images)
            {
                if (string.IsNullOrWhiteSpace(image.Path) || !PathBoundary.Contains(step.From, image.Path))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(step.From, image.Path);
                image.Path = relative == "." ? step.To : Path.Combine(step.To, relative);
                rebased++;
            }
        }

        return rebased;
    }

    /// <summary>
    /// Drops the archive folders a relocation has emptied, so nothing goes on treating them as
    /// places images live.
    /// </summary>
    /// <remarks>
    /// This is what frees the folder for ordinary use again: while it is still a known archive,
    /// scanning it is refused, because scanning a folder the index describes would have every
    /// file match itself.
    /// </remarks>
    public static int ForgetRelocated(AppSettings settings, IEnumerable<ArchiveRelocationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(steps);

        var moved = steps
            .Select(step => step.From)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return settings.LegacyArchiveMappings.RemoveAll(
            legacy => !string.IsNullOrWhiteSpace(legacy.ArchivePath)
                && moved.Contains(PathBoundary.Normalize(legacy.ArchivePath)));
    }

    private static IReadOnlyList<string> SafeEnumerate(string root)
    {
        try
        {
            return PathBoundary.EnumerateFilesWithoutReparsePoints(root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
