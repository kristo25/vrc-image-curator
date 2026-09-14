using System.IO;

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
}
