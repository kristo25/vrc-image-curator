using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.Automation;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void EnableWritesQuotedPortablePathAndDisableRemovesOnlyOwnValue()
    {
        var registry = new FakeStartupRegistry();
        var service = new StartupRegistrationService(registry);
        var executable = @"C:\Portable Apps\VRC Image Curator.exe";

        service.SetEnabled(true, executable);

        Assert.Equal("\"C:\\Portable Apps\\VRC Image Curator.exe\" --background", registry.Values[StartupRegistrationService.ValueName]);
        Assert.Equal(StartupRegistrationStatus.Current, service.GetStatus(executable));

        service.SetEnabled(false, executable);

        Assert.False(registry.Values.ContainsKey(StartupRegistrationService.ValueName));
        Assert.Equal("untouched", registry.Values["AnotherApplication"]);
    }

    [Fact]
    public void MovedExecutableIsReportedAsStaleUntilRepaired()
    {
        var registry = new FakeStartupRegistry();
        var service = new StartupRegistrationService(registry);
        service.SetEnabled(true, @"C:\Old\VrcImageCurator.exe");

        Assert.Equal(
            StartupRegistrationStatus.Stale,
            service.GetStatus(@"D:\Portable\VrcImageCurator.exe"));

        service.SetEnabled(true, @"D:\Portable\VrcImageCurator.exe");

        Assert.Equal(
            StartupRegistrationStatus.Current,
            service.GetStatus(@"D:\Portable\VrcImageCurator.exe"));
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AnotherApplication"] = "untouched",
        };

        public string? Read(string valueName) => Values.GetValueOrDefault(valueName);

        public void Write(string valueName, string command) => Values[valueName] = command;

        public void Delete(string valueName) => Values.Remove(valueName);
    }
}
