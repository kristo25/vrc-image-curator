using System.IO;

namespace VrcImageCurator.App.Services;

public static class DiagnosticLog
{
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
