using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace VrcPicSorter.App.Services;

/// <summary>
/// Answers whether Windows has been told to delete permanently rather than recycle on a given
/// volume. Two settings say so: the administered policy, and the per-drive checkbox in Recycle Bin
/// properties. Neither changes what <c>SHFileOperation</c> reports back, so asking beforehand is
/// the only way to know that a "recycle" would really be a deletion.
/// </summary>
public static class RecycleBinPolicy
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    /// <param name="volumeRoot">A volume root such as <c>C:\</c>.</param>
    /// <returns>
    /// True only when a setting positively says files are deleted immediately. Anything unreadable
    /// answers false: not knowing is not evidence, and answering true would send every exact
    /// duplicate to the review queue on machines that are perfectly fine.
    /// </returns>
    public static bool IsDisabledForVolume(string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            return false;
        }

        try
        {
            if (ReadFlag(Registry.CurrentUser, PolicyKey, "NoRecycleFiles")
                || ReadFlag(Registry.LocalMachine, PolicyKey, "NoRecycleFiles"))
            {
                return true;
            }

            return TryGetVolumeGuid(volumeRoot) is { } volume
                && ReadFlag(Registry.CurrentUser, $@"{BitBucketKey}\{volume}", "NukeOnDelete");
        }
        catch (Exception exception) when (
            exception is SecurityException
                or UnauthorizedAccessException
                or IOException
                or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool ReadFlag(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path, writable: false);
        return key?.GetValue(name) is int value && value != 0;
    }

    /// <summary>
    /// Turns <c>C:\</c> into the <c>{guid}</c> the BitBucket key is named after. The registry has
    /// no drive letters in it, so this is the only link between the two.
    /// </summary>
    private static string? TryGetVolumeGuid(string volumeRoot)
    {
        var mountPoint = Path.EndsInDirectorySeparator(volumeRoot)
            ? volumeRoot
            : volumeRoot + Path.DirectorySeparatorChar;
        var buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length))
        {
            return null;
        }

        // The API answers with "\\?\Volume{guid}\"; the key is named for the braced guid alone.
        var name = new string(buffer);
        var open = name.IndexOf('{', StringComparison.Ordinal);
        var close = name.IndexOf('}', StringComparison.Ordinal);
        return open >= 0 && close > open ? name[open..(close + 1)] : null;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeNameForVolumeMountPointW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        [Out] char[] volumeName,
        int bufferLength);
}
