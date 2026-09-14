using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Automation;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void EnableWritesQuotedPortablePathAndDisableRemovesOnlyOwnValue()
    {
        var registry = new FakeStartupRegistry();
        var service = new StartupRegistrationService(registry);
        var executable = @"C:\Portable Apps\VRC Pic Sorter.exe";

        service.SetEnabled(true, executable);

        Assert.Equal("\"C:\\Portable Apps\\VRC Pic Sorter.exe\" --background", registry.Values[StartupRegistrationService.ValueName]);
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
        service.SetEnabled(true, @"C:\Old\VrcPicSorter.exe");

        Assert.Equal(
            StartupRegistrationStatus.Stale,
            service.GetStatus(@"D:\Portable\VrcPicSorter.exe"));

        service.SetEnabled(true, @"D:\Portable\VrcPicSorter.exe");

        Assert.Equal(
            StartupRegistrationStatus.Current,
            service.GetStatus(@"D:\Portable\VrcPicSorter.exe"));
    }

    [Fact]
    public void ARegistrationMadeUnderTheOldNameIsCarriedToTheNewOne()
    {
        var registry = new FakeStartupRegistry();
        registry.Values[StartupRegistrationService.PreviousValueName] =
            "\"C:\\Portable Apps\\VrcImageCurator.exe\" --background";
        var service = new StartupRegistrationService(registry);

        Assert.True(service.CarryOverPreviousName(@"C:\Portable Apps\VrcPicSorter.exe"));

        Assert.False(registry.Values.ContainsKey(StartupRegistrationService.PreviousValueName));
        Assert.Equal(
            "\"C:\\Portable Apps\\VrcPicSorter.exe\" --background",
            registry.Values[StartupRegistrationService.ValueName]);
        Assert.Equal("untouched", registry.Values["AnotherApplication"]);
    }

    [Fact]
    public void AnExistingRegistrationUnderTheCurrentNameWins()
    {
        // The current name's value is the newer statement of intent, so the old one is only
        // cleared away rather than allowed to overwrite it.
        var registry = new FakeStartupRegistry();
        registry.Values[StartupRegistrationService.PreviousValueName] = "\"C:\\Old\\VrcImageCurator.exe\" --background";
        registry.Values[StartupRegistrationService.ValueName] = "\"D:\\Chosen\\VrcPicSorter.exe\" --background";
        var service = new StartupRegistrationService(registry);

        Assert.True(service.CarryOverPreviousName(@"C:\Somewhere\Else\VrcPicSorter.exe"));

        Assert.False(registry.Values.ContainsKey(StartupRegistrationService.PreviousValueName));
        Assert.Equal(
            "\"D:\\Chosen\\VrcPicSorter.exe\" --background",
            registry.Values[StartupRegistrationService.ValueName]);
    }

    [Fact]
    public void NoOldRegistrationMeansNothingIsWritten()
    {
        var registry = new FakeStartupRegistry();
        var service = new StartupRegistrationService(registry);

        Assert.False(service.CarryOverPreviousName(@"C:\Portable\VrcPicSorter.exe"));

        Assert.False(registry.Values.ContainsKey(StartupRegistrationService.ValueName));
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
