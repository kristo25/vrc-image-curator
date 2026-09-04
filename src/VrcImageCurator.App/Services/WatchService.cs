using System.IO;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Scanning;

namespace VrcImageCurator.App.Services;

public sealed class WatchService : IDisposable
{
    private readonly ScanCoordinator _scanner;
    private readonly ArchiveIndexer _indexer;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryInterval;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<VrcImageCategory, PendingCategory> _pending = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<WatchTarget> _targets = [];
    private System.Threading.Timer? _retryTimer;
    private CancellationTokenSource _runCancellation = new();
    private bool _isRunning;
    private bool _disposed;

    public WatchService(
        ScanCoordinator scanner,
        ArchiveIndexer indexer,
        TimeSpan? debounce = null,
        TimeSpan? retryInterval = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _debounce = debounce ?? TimeSpan.FromSeconds(1.5);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(15);
    }

    public event EventHandler<CategoryScanResult>? ScanCompleted;

    public event EventHandler<WatcherFailureEventArgs>? ScanFailed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    public void Start(
        IEnumerable<CategoryMapping> mappings,
        IEnumerable<LegacyArchiveMapping>? legacyArchives = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replacements = new List<FileSystemWatcher>();
        var enabledMappings = mappings.Where(item => item.IsEnabled).ToArray();
        var enabledCategories = enabledMappings.Select(item => item.Category).ToHashSet();
        var retainedArchives = (legacyArchives ?? [])
            .Where(item => enabledCategories.Contains(item.Category))
            .ToArray();
        try
        {
            foreach (var mapping in enabledMappings)
            {
                AddWatcher(replacements, mapping.SourcePath, mapping.Category, archive: false);
                AddWatcher(replacements, mapping.ArchivePath, mapping.Category, archive: true);
            }


            foreach (var legacy in retainedArchives)
            {
                AddWatcher(replacements, legacy.ArchivePath, legacy.Category, archive: true);
            }
        }
        catch
        {
            DisposeWatchers(replacements);
            throw;
        }

        try
        {
            lock (_sync)
            {
                _runCancellation.Cancel();
                _runCancellation = new CancellationTokenSource();
                DisposeWatchers(_watchers);
                _watchers.Clear();
                _watchers.AddRange(replacements);
                _targets.Clear();
                _targets.AddRange(enabledMappings.SelectMany(mapping => new[]
                {
                    new WatchTarget(mapping.SourcePath, mapping.Category, false),
                    new WatchTarget(mapping.ArchivePath, mapping.Category, true),
                }));
                _targets.AddRange(retainedArchives
                    .Select(item => new WatchTarget(item.ArchivePath, item.Category, true)));
                _isRunning = enabledMappings.Length > 0;
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = true;
                }

                _retryTimer?.Dispose();
                _retryTimer = _isRunning
                    ? new System.Threading.Timer(_ => RetryAttachNewlyAvailableFolders(), null, _retryInterval, _retryInterval)
                    : null;
            }
        }
        catch
        {
            lock (_sync)
            {
                DisposeWatchers(_watchers);
                _watchers.Clear();
                _targets.Clear();
                _isRunning = false;
                _retryTimer?.Dispose();
                _retryTimer = null;
            }

            throw;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isRunning = false;
            _runCancellation.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.Timer.Dispose();
            }

            _pending.Clear();
            _retryTimer?.Dispose();
            _retryTimer = null;
            DisposeWatchers(_watchers);
            _watchers.Clear();
            _targets.Clear();
        }
    }

    public async Task StopAsync()
    {
        Stop();
        await _scanGate.WaitAsync().ConfigureAwait(false);
        _scanGate.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _runCancellation.Dispose();
        _scanGate.Dispose();
    }

    private static void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void AddWatcher(
        ICollection<FileSystemWatcher> watchers,
        string path,
        VrcImageCategory category,
        bool archive)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = false,
        };
        FileSystemEventHandler changed = (_, e) => Queue(category, archive, e.FullPath);
        RenamedEventHandler renamed = (_, e) => Queue(category, archive, e.FullPath);
        ErrorEventHandler error = (_, _) => HandleWatcherError(watcher, category, archive);
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watchers.Add(watcher);
    }

    private void HandleWatcherError(FileSystemWatcher watcher, VrcImageCategory category, bool archive)
    {
        lock (_sync)
        {
            if (_watchers.Remove(watcher))
            {
                DisposeWatchers([watcher]);
            }

            // The watcher buffer overflowed or the handle died, so individual change
            // notifications were lost. A full sweep is the only safe recovery.
            Queue(category, archive, path: null, requestFullScan: true);
        }
    }

    private void Queue(
        VrcImageCategory category,
        bool archive,
        string? path,
        bool requestFullScan = false)
    {
        lock (_sync)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            if (_pending.TryGetValue(category, out var existing))
            {
                Accumulate(existing, archive, path, requestFullScan);
                existing.Timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            var pending = new PendingCategory(archive);
            Accumulate(pending, archive, path, requestFullScan);
            pending.Timer = new System.Threading.Timer(
                _ => _ = ProcessAsync(category),
                null,
                _debounce,
                Timeout.InfiniteTimeSpan);
            _pending[category] = pending;
        }
    }

    private static void Accumulate(
        PendingCategory pending,
        bool archive,
        string? path,
        bool requestFullScan)
    {
        pending.ArchiveChanged |= archive;
        pending.FullScanRequested |= requestFullScan;
        if (!archive && !string.IsNullOrWhiteSpace(path))
        {
            pending.SourcePaths.Add(path);
        }
    }

    private void AttachNewlyAvailableFolders()
    {
        lock (_sync)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            for (var index = _watchers.Count - 1; index >= 0; index--)
            {
                var watcher = _watchers[index];
                if (Directory.Exists(watcher.Path))
                {
                    continue;
                }

                _watchers.RemoveAt(index);
                DisposeWatchers([watcher]);
            }

            foreach (var target in _targets)
            {
                if (!Directory.Exists(target.Path)
                    || _watchers.Any(watcher => string.Equals(
                        watcher.Path,
                        Path.GetFullPath(target.Path),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var additions = new List<FileSystemWatcher>();
                    AddWatcher(additions, target.Path, target.Category, target.Archive);
                    foreach (var watcher in additions)
                    {
                        _watchers.Add(watcher);
                        try
                        {
                            watcher.EnableRaisingEvents = true;
                        }
                        catch
                        {
                            _watchers.Remove(watcher);
                            DisposeWatchers([watcher]);
                            throw;
                        }
                    }

                    Queue(target.Category, target.Archive, path: null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    ScanFailed?.Invoke(this, new WatcherFailureEventArgs(target.Category, exception));
                }
            }
        }
    }

    private void RetryAttachNewlyAvailableFolders()
    {
        try
        {
            AttachNewlyAvailableFolders();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reattach watched folders: {exception}");
        }
    }

    private async Task ProcessAsync(VrcImageCategory category)
    {
        bool fullScanRequested;
        string[] sourcePaths;
        CancellationToken cancellationToken;
        lock (_sync)
        {
            if (!_pending.Remove(category, out var pending))
            {
                return;
            }

            pending.Timer.Dispose();
            fullScanRequested = pending.FullScanRequested;
            sourcePaths = [.. pending.SourcePaths];
            cancellationToken = _runCancellation.Token;
        }

        var enteredScanGate = false;
        try
        {
            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredScanGate = true;
            // Watching analyzes only images that arrived while it was running. A full sweep
            // happens on demand from Scan now, or here when a watcher error lost events.
            CategoryScanResult result;
            if (fullScanRequested)
            {
                result = await _scanner.ScanCategoryAsync(category, cancellationToken).ConfigureAwait(false);
            }
            else if (sourcePaths.Length > 0)
            {
                result = await _scanner.ScanIncomingPathsAsync(category, sourcePaths, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Archive-only change: keep the fingerprint index current without
                // re-analyzing anything in the input folders.
                var index = await _indexer.RefreshAsync(category, cancellationToken).ConfigureAwait(false);
                result = new CategoryScanResult(category, 0, 0, 0, 0, index.Errors);
            }

            ScanCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                Exception failure = exception;
                try
                {
                    await _indexer.MarkStaleAsync(category, "Watcher scan failed.").ConfigureAwait(false);
                }
                catch (Exception staleException)
                {
                    failure = new AggregateException(exception, staleException);
                }

                try
                {
                    ScanFailed?.Invoke(this, new WatcherFailureEventArgs(category, failure));
                }
                catch (Exception reportingException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Failed to report watcher scan exception: {reportingException}");
                }
            }
        }
        finally
        {
            if (enteredScanGate)
            {
                _scanGate.Release();
            }
        }
    }

    private sealed class PendingCategory(bool archiveChanged)
    {
        public bool ArchiveChanged { get; set; } = archiveChanged;

        public bool FullScanRequested { get; set; }

        /// <summary>Source files the watcher reported since the last debounce window.</summary>
        public HashSet<string> SourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public System.Threading.Timer Timer { get; set; } = null!;
    }

    private sealed record WatchTarget(string Path, VrcImageCategory Category, bool Archive);
}

public sealed record WatcherFailureEventArgs(VrcImageCategory Category, Exception Exception);
