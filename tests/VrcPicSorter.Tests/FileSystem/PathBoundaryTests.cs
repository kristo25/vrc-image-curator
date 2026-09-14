using System.Diagnostics;
using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class PathBoundaryTests
{
    [Fact]
    public async Task EnumerationIncludesFilesInOrdinaryNestedFolders()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var image = Path.Combine(nested, "image.png");
        await File.WriteAllTextAsync(image, "test");

        var files = PathBoundary.EnumerateFilesWithoutReparsePoints(root);

        Assert.Equal([image], files);
    }

    [Fact]
    public async Task EnumerationDoesNotFollowDirectoryJunctions()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("root");
        var outside = directory.GetPath("outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var outsideImage = Path.Combine(outside, "outside.png");
        await File.WriteAllTextAsync(outsideImage, "test");
        var linked = Path.Combine(root, "linked");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }.WithArguments("/d", "/c", "mklink", "/J", linked, outside));
        Assert.NotNull(process);
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);

        try
        {
            var files = PathBoundary.EnumerateFilesWithoutReparsePoints(root);
            Assert.Empty(files);
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
