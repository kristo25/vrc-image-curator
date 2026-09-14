using System.IO;
using Microsoft.Win32;

namespace VrcPicSorter.App.Services;

public enum StartupRegistrationStatus
{
    Disabled,
    Current,
    Stale,
}

public interface IStartupRegistry
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

public sealed class CurrentUserStartupRegistry : IStartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

public sealed class StartupRegistrationService
{
    public const string ValueName = "VrcPicSorter";
    private readonly IStartupRegistry _registry;

    public StartupRegistrationService(IStartupRegistry? registry = null)
    {
        _registry = registry ?? new CurrentUserStartupRegistry();
    }

    public StartupRegistrationStatus GetStatus(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var current = _registry.Read(ValueName);
        if (string.IsNullOrWhiteSpace(current))
        {
            return StartupRegistrationStatus.Disabled;
        }

        return string.Equals(current, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationStatus.Current
            : StartupRegistrationStatus.Stale;
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (enabled)
        {
            _registry.Write(ValueName, BuildCommand(executablePath));
        }
        else
        {
            _registry.Delete(ValueName);
        }
    }

    /// <summary>The value this application registered itself under before it was renamed.</summary>
    public const string PreviousValueName = "VrcImageCurator";

    /// <summary>
    /// Carries a start-with-Windows registration across the rename: the old value is removed, and
    /// the intent behind it is kept by registering this executable under the current name.
    /// </summary>
    /// <remarks>
    /// Removing it without re-registering would quietly turn the option off for someone who had
    /// chosen it; leaving it in place would launch whatever now sits at the old executable's path
    /// while Settings showed the option as off. An existing registration under the current name is
    /// left exactly as it is - it is the newer of the two statements of intent.
    /// </remarks>
    /// <returns><see langword="true"/> when an old registration was found and removed.</returns>
    public bool CarryOverPreviousName(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (string.IsNullOrWhiteSpace(_registry.Read(PreviousValueName)))
        {
            return false;
        }

        _registry.Delete(PreviousValueName);
        if (string.IsNullOrWhiteSpace(_registry.Read(ValueName)))
        {
            _registry.Write(ValueName, BuildCommand(executablePath));
        }

        return true;
    }

    public static string BuildCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" --background";
}
