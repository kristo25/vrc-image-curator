using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.App.Services;

/// <summary>
/// Carries the local data folder across the rename from VRC Image Curator to VRC Pic Sorter.
/// </summary>
/// <remarks>
/// Settings, the archive index, the review queue, the operation journal and the held incoming files
/// all live in one folder named after the application. Renaming the application without moving that
/// folder would leave an existing installation starting from nothing, with a queue of undecided
/// reviews stranded under a name nothing looks at any more.
/// <para>
/// The move happens once, and only when there is an old folder and no new one. Every other case -
/// both present, neither present, or a failure part-way - leaves the new folder to be created
/// empty. The old folder is never deleted, so the worst outcome of a failed carry-over is starting
/// fresh with the previous data still sitting on disk.
/// </para>
/// </remarks>
public static class LocalDataMigration
{
    /// <summary>The folder this application kept its data in before it was renamed.</summary>
    public const string PreviousFolderName = "VrcImageCurator";

    /// <summary>
    /// Moves <paramref name="previousDirectory"/> to <paramref name="currentDirectory"/> when the
    /// first exists and the second does not yet.
    /// </summary>
    /// <returns><see langword="true"/> when a folder was actually carried over.</returns>
    public static bool CarryOver(string previousDirectory, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        // An existing new folder wins outright. Merging the two would mean deciding which copy of
        // state.json is the real one, and there is no answer to that worth guessing at.
        if (Directory.Exists(currentDirectory) || !Directory.Exists(previousDirectory))
        {
            return false;
        }

        try
        {
            Directory.Move(previousDirectory, currentDirectory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is a recoverable disappointment. Refusing to start is not, so a
            // locked or unreadable old folder is stepped over rather than thrown from.
            return false;
        }
    }

    /// <summary>
    /// True when a setting still names a path inside the old data folder.
    /// </summary>
    /// <remarks>
    /// Moving the folder is only half the job. Paths are stored absolute, so a setting written
    /// while the application had its old name goes on naming the old folder afterwards - which is
    /// now a folder that does not exist. The holding root is the one that matters: it is derived
    /// from the data folder, and clearing local data refuses to run when it sits outside it.
    /// </remarks>
    public static bool NeedsRebase(AppSettings settings, string previousDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDirectory);

        return IsInside(settings.HoldingRootPath, previousDirectory)
            || IsInside(settings.OutputRootPath, previousDirectory)
            || settings.CategoryMappings.Any(mapping =>
                IsInside(mapping.SourcePath, previousDirectory)
                || IsInside(mapping.ArchivePath, previousDirectory))
            || settings.LegacyArchiveMappings.Any(mapping => IsInside(mapping.ArchivePath, previousDirectory));
    }

    /// <summary>
    /// Rewrites every stored path that sat inside <paramref name="previousDirectory"/> so it names
    /// the same place inside <paramref name="currentDirectory"/> instead.
    /// </summary>
    /// <remarks>
    /// Paths outside the old data folder are left exactly as they are. Someone's archive on another
    /// drive has nothing to do with what this application is called.
    /// </remarks>
    /// <returns>How many paths were rewritten.</returns>
    public static int RebasePaths(AppSettings settings, string previousDirectory, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var rebased = 0;
        settings.HoldingRootPath = Rebase(settings.HoldingRootPath, previousDirectory, currentDirectory, ref rebased);
        settings.OutputRootPath = Rebase(settings.OutputRootPath, previousDirectory, currentDirectory, ref rebased);

        foreach (var mapping in settings.CategoryMappings)
        {
            mapping.SourcePath = Rebase(mapping.SourcePath, previousDirectory, currentDirectory, ref rebased);
            mapping.ArchivePath = Rebase(mapping.ArchivePath, previousDirectory, currentDirectory, ref rebased);
        }

        foreach (var mapping in settings.LegacyArchiveMappings)
        {
            mapping.ArchivePath = Rebase(mapping.ArchivePath, previousDirectory, currentDirectory, ref rebased);
        }

        return rebased;
    }

    private static string Rebase(string path, string previousDirectory, string currentDirectory, ref int rebased)
    {
        if (!IsInside(path, previousDirectory))
        {
            return path;
        }

        var relative = Path.GetRelativePath(previousDirectory, path);
        rebased++;
        return relative == "." ? currentDirectory : Path.Combine(currentDirectory, relative);
    }

    private static bool IsInside(string path, string previousDirectory) =>
        !string.IsNullOrWhiteSpace(path) && PathBoundary.Contains(previousDirectory, path);
}
