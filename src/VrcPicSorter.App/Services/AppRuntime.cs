using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.App.Services;

public sealed class AppRuntime : IDisposable
{
    internal static readonly TimeSpan ProductionFileSettleDelay = TimeSpan.FromMilliseconds(750);

    public AppRuntime(string? stateDirectory = null, bool allowStartupRegistration = true)
    {
        var isolated = stateDirectory is not null;
        if (stateDirectory is not null)
        {
            StateDirectory = Path.GetFullPath(stateDirectory);
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            StateDirectory = Path.Combine(localAppData, "VrcPicSorter");

            // The application answered to another name until 1.4.0, and everything it remembers
            // lives in a folder named after it. Carried across here rather than anywhere later,
            // because the state store below reads that folder the moment it is constructed. A
            // folder given with --data-dir is left alone: it was named by whoever passed it.
            LocalDataMigration.CarryOver(
                Path.Combine(localAppData, LocalDataMigration.PreviousFolderName),
                StateDirectory);
        }
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

        // A start-with-Windows registration made under the old name would otherwise keep launching
        // whatever now sits at the old executable's path, while Settings reported the option as
        // off. This is the first point where the executable's own path is known.
        if (AllowStartupRegistration && Environment.ProcessPath is { } executablePath)
        {
            Startup.CarryOverPreviousName(executablePath);
        }
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
