using System.IO;

namespace VrcPicSorter.App.Services;

public static class DiagnosticLog
{
    /// <summary>
    /// Writes a diagnostic entry synchronously. Crash handlers must not await, because the
    /// continuation would be posted back to a dispatcher that may already be tearing down.
    /// </summary>
    public static string? TryWrite(string stateDirectory, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var path = Path.Combine(stateDirectory, "error.log");
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<string?> TryWriteAsync(string stateDirectory, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var path = Path.Combine(stateDirectory, "error.log");
            await File.AppendAllTextAsync(
                path,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
