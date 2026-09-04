namespace VrcImageCurator.Core.FileSystem;

public static class PathBoundary
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Contains(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Path.GetFullPath(candidate);
        return string.Equals(normalizedRoot, Path.TrimEndingDirectorySeparator(normalizedCandidate), StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    public static void EnsureContained(string root, string candidate, string description)
    {
        if (!Contains(root, candidate))
        {
            throw new InvalidOperationException($"{description} escapes its allowed folder.");
        }
    }

    public static void EnsureNoReparsePoints(string path, string description)
    {
        if (File.Exists(path) && IsRedirectingLink(new FileInfo(path)))
        {
            throw new InvalidOperationException($"{description} cannot use a symbolic link: {Path.GetFullPath(path)}");
        }

        var current = new DirectoryInfo(Directory.Exists(path) ? Normalize(path) : Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (current is not null && current.Exists)
        {
            if (IsRedirectingLink(current))
            {
                throw new InvalidOperationException($"{description} cannot use a junction or symbolic link: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    public static IReadOnlyList<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var normalizedRoot = Normalize(root);
        EnsureNoReparsePoints(normalizedRoot, "Scanned folder");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                EnsureContained(normalizedRoot, file, "Scanned file");
                if (!IsRedirectingLink(new FileInfo(file)))
                {
                    files.Add(file);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsRedirectingLink(new DirectoryInfo(directory)))
                {
                    continue;
                }

                EnsureContained(normalizedRoot, directory, "Scanned directory");
                pending.Push(directory);
            }
        }

        return files;
    }

    private static bool IsRedirectingLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return item.LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
