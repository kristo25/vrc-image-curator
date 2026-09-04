using System.IO;
using Microsoft.Win32;

namespace VrcImageCurator.App.Services;

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
    public const string ValueName = "VrcImageCurator";
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

    public static string BuildCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" --background";
}
