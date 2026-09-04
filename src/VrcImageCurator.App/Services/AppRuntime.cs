using System.IO;
using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Scanning;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.App.Services;

public sealed class AppRuntime : IDisposable
{
    internal static readonly TimeSpan ProductionFileSettleDelay = TimeSpan.FromMilliseconds(750);

    public AppRuntime(string? stateDirectory = null, bool allowStartupRegistration = true)
    {
        var isolated = stateDirectory is not null;
        StateDirectory = stateDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcImageCurator")
            : Path.GetFullPath(stateDirectory);
        AllowStartupRegistration = allowStartupRegistration;
        StateStore = new JsonStateStore(
            StateDirectory,
            () => isolated
                ? AppStateDefaults.Create(
                    Path.Combine(StateDirectory, "Profile"),
                    StateDirectory,
                    StateDirectory)
                : AppStateDefaults.Create(stateDirectoryPath: StateDirectory));
        Decoder = new ImageDecoder();
        Indexer = new ArchiveIndexer(StateStore, Decoder);
        Router = new FileRouter(StateStore, Decoder, new WindowsRecycleBinService());
        Scanner = new ScanCoordinator(
            StateStore,
            Indexer,
            Decoder,
            Router,
            ProductionFileSettleDelay);
        Watcher = new WatchService(Scanner, Indexer);
        Startup = new StartupRegistrationService();
    }

    public JsonStateStore StateStore { get; }

    public string StateDirectory { get; }

    public bool AllowStartupRegistration { get; }

    public ImageDecoder Decoder { get; }

    public ArchiveIndexer Indexer { get; }

    public FileRouter Router { get; }

    public ScanCoordinator Scanner { get; }

    public WatchService Watcher { get; }

    public StartupRegistrationService Startup { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _ = await StateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _ = await Router.RecoverPendingOperationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyAutomationSettingsAsync(bool updateStartupRegistration)
    {
        var state = await StateStore.LoadAsync().ConfigureAwait(false);
        if (Watcher.IsRunning)
        {
            await Watcher.StopAsync().ConfigureAwait(false);
            Watcher.Start(
                state.Settings.CategoryMappings,
                state.Settings.LegacyArchiveMappings,
                SweepInterval(state.Settings.Automation),
                state.Settings.Automation.WatchMode == WatchMode.OnDetection);
        }

        if (updateStartupRegistration && AllowStartupRegistration)
        {
            Startup.SetEnabled(
                state.Settings.Automation.StartWithWindows,
                Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable."));
        }
    }

    public async Task StartWatchingAsync()
    {
        var state = await StateStore.LoadAsync().ConfigureAwait(false);
        Watcher.Start(
            state.Settings.CategoryMappings,
            state.Settings.LegacyArchiveMappings,
            SweepInterval(state.Settings.Automation),
            state.Settings.Automation.WatchMode == WatchMode.OnDetection);
    }

    public Task StopWatchingAsync() => Watcher.StopAsync();

    private static TimeSpan? SweepInterval(AutomationSettings automation) =>
        automation.WatchMode == WatchMode.OnInterval ? automation.WatchScanInterval : null;

    public void Dispose()
    {
        Watcher.Dispose();
        StateStore.Dispose();
    }
}
