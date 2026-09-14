using Microsoft.VisualBasic.FileIO;

namespace VrcPicSorter.Core.FileSystem;

public interface IRecycleBinService
{
    bool CanRecycle(string path);

    Task RecycleAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class WindowsRecycleBinService : IRecycleBinService
{
    public bool CanRecycle(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\"))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return root is not null
                && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!CanRecycle(path))
        {
            throw new NotSupportedException("Windows Recycle Bin behavior is unavailable for this path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
        return Task.CompletedTask;
    }
}
