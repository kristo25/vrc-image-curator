using System.Text.Json;
using System.Text.Json.Serialization;
using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Models;

namespace VrcImageCurator.Core.Storage;

public enum ClearLocalDataStatus
{
    Cleared,
    BlockedByPendingReview,
    BlockedByPendingOperation,
    BlockedByHeldFiles,
    BlockedByUnsafeHoldingRoot,
}

public sealed record ClearLocalDataResult(ClearLocalDataStatus Status, IReadOnlyList<Guid> BlockingIds)
{
    public bool WasCleared => Status == ClearLocalDataStatus.Cleared;
}

public sealed record StateRecoveryNotice(string QuarantinedPath, bool RestoredBackup);

public sealed class StateRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException(
        $"State revision {expectedRevision} is stale; the current revision is {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;

    public long ActualRevision { get; } = actualRevision;
}

public sealed class JsonStateStore : IDisposable
{
    public const string StateFileName = "state.json";
    public const string TemporaryFileName = "state.json.tmp";
    public const string BackupFileName = "state.json.bak";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly Func<AppStateDocument> _defaultStateFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public JsonStateStore(string stateDirectory, Func<AppStateDocument>? defaultStateFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        StateDirectory = Path.GetFullPath(stateDirectory);
        StatePath = Path.Combine(StateDirectory, StateFileName);
        TemporaryPath = Path.Combine(StateDirectory, TemporaryFileName);
        BackupPath = Path.Combine(StateDirectory, BackupFileName);
        _defaultStateFactory = defaultStateFactory ?? (() => AppStateDefaults.Create());
    }

    public string StateDirectory { get; }

    public string StatePath { get; }

    public string TemporaryPath { get; }

    public string BackupPath { get; }

    public StateRecoveryNotice? LastRecoveryNotice { get; private set; }

    public static JsonStateStore CreateForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new JsonStateStore(Path.Combine(localAppData, "VrcImageCurator"));
    }

    public async Task<AppStateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await LoadCoreAsync(createWhenMissing: true, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The state factory returned no state document.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppStateDocument state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await WriteCoreAsync(state, requireCurrentRevision: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<AppStateDocument, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await LoadCoreAsync(createWhenMissing: false, cancellationToken).ConfigureAwait(false)
                ?? _defaultStateFactory();
            var result = update(state);
            await WriteCoreAsync(state, requireCurrentRevision: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClearLocalDataResult> TryClearLocalDataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await LoadCoreAsync(createWhenMissing: false, cancellationToken).ConfigureAwait(false)
                ?? _defaultStateFactory();
            {
                var heldReviews = state.ReviewQueue
                    .Where(item => item.Status != ReviewStatus.Resolved
                        && !string.IsNullOrWhiteSpace(item.HeldFilePath))
                    .Select(item => item.Id)
                    .ToArray();

                if (heldReviews.Length > 0)
                {
                    return new ClearLocalDataResult(
                        ClearLocalDataStatus.BlockedByPendingReview,
                        heldReviews);
                }

                var pendingOperations = state.OperationJournal
                    .Where(entry => entry.Phase is not JournalPhase.Completed)
                    .Select(entry => entry.Id)
                    .ToArray();

                if (pendingOperations.Length > 0)
                {
                    return new ClearLocalDataResult(
                        ClearLocalDataStatus.BlockedByPendingOperation,
                        pendingOperations);
                }

                if (string.IsNullOrWhiteSpace(state.Settings.HoldingRootPath)
                    || !PathBoundary.Contains(StateDirectory, state.Settings.HoldingRootPath))
                {
                    return new ClearLocalDataResult(ClearLocalDataStatus.BlockedByUnsafeHoldingRoot, []);
                }

                try
                {
                    PathBoundary.EnsureNoReparsePoints(state.Settings.HoldingRootPath, "Application holding folder");
                }
                catch (InvalidOperationException)
                {
                    return new ClearLocalDataResult(ClearLocalDataStatus.BlockedByUnsafeHoldingRoot, []);
                }

                if (Directory.Exists(state.Settings.HoldingRootPath)
                    && Directory.EnumerateFiles(state.Settings.HoldingRootPath, "*", SearchOption.AllDirectories).Any())
                {
                    return new ClearLocalDataResult(ClearLocalDataStatus.BlockedByHeldFiles, []);
                }

                if (Directory.Exists(state.Settings.HoldingRootPath))
                {
                    Directory.Delete(state.Settings.HoldingRootPath, recursive: true);
                }
            }

            File.Delete(TemporaryPath);
            File.Delete(StatePath);
            File.Delete(BackupPath);
            if (Directory.Exists(StateDirectory))
            {
                foreach (var quarantined in Directory.EnumerateFiles(
                    StateDirectory,
                    "state.corrupt-*.json",
                    SearchOption.TopDirectoryOnly))
                {
                    File.Delete(quarantined);
                }
            }

            if (Directory.Exists(StateDirectory)
                && !Directory.EnumerateFileSystemEntries(StateDirectory).Any())
            {
                Directory.Delete(StateDirectory);
            }
            return new ClearLocalDataResult(ClearLocalDataStatus.Cleared, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private async Task<AppStateDocument?> LoadCoreAsync(
        bool createWhenMissing,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            if (!createWhenMissing)
            {
                return null;
            }

            var defaults = _defaultStateFactory();
            await WriteCoreAsync(defaults, requireCurrentRevision: false, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            return await ReadAndPrepareStateAsync(StatePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnreadableState(exception))
        {
            return await RecoverUnreadableStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AppStateDocument> ReadAndPrepareStateAsync(
        string path,
        CancellationToken cancellationToken,
        bool persistChanges = true)
    {
        AppStateDocument state;
        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            state = await JsonSerializer.DeserializeAsync<AppStateDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The state document is empty.");
        }

        var migrated = AppStateMigrator.Migrate(state, _defaultStateFactory());
        var compacted = AppStateCompactor.Compact(state);
        AppStateValidator.Validate(state);
        if (persistChanges && (migrated || compacted))
        {
            await WriteCoreAsync(state, requireCurrentRevision: false, cancellationToken).ConfigureAwait(false);
        }

        return state;
    }

    private async Task<AppStateDocument> RecoverUnreadableStateAsync(CancellationToken cancellationToken)
    {
        AppStateDocument? recovered = null;
        if (File.Exists(BackupPath))
        {
            try
            {
                recovered = await ReadAndPrepareStateAsync(BackupPath, cancellationToken, persistChanges: false)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsUnreadableState(exception))
            {
                recovered = null;
            }
        }

        var restoredBackup = recovered is not null;
        var quarantinedPath = QuarantineStateFile();
        recovered ??= _defaultStateFactory();
        recovered.History.Add(new ActivityEntry
        {
            Id = Guid.NewGuid(),
            OccurredUtc = DateTimeOffset.UtcNow,
            Kind = ActivityKind.Warning,
            Level = ActivityLevel.Warning,
            Message = restoredBackup
                ? $"Unreadable application state was quarantined at {quarantinedPath}; the last backup was restored."
                : $"Unreadable application state was quarantined at {quarantinedPath}; defaults were restored.",
        });
        LastRecoveryNotice = new StateRecoveryNotice(quarantinedPath, restoredBackup);
        await WriteCoreAsync(recovered, requireCurrentRevision: false, cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    private string QuarantineStateFile()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var candidate = Path.Combine(StateDirectory, $"state.corrupt-{timestamp}.json");
        for (var suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(StateDirectory, $"state.corrupt-{timestamp}-{suffix}.json");
        }

        File.Move(StatePath, candidate);
        return candidate;
    }

    private static bool IsUnreadableState(Exception exception) =>
        exception is JsonException or InvalidDataException or ArgumentException or OverflowException;

    private async Task WriteCoreAsync(
        AppStateDocument state,
        bool requireCurrentRevision,
        CancellationToken cancellationToken)
    {
        AppStateCompactor.Compact(state);
        AppStateValidator.Validate(state);
        Directory.CreateDirectory(StateDirectory);

        var expectedRevision = state.Revision;
        if (requireCurrentRevision)
        {
            var currentRevision = await ReadCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
            if (currentRevision != expectedRevision)
            {
                throw new StateRevisionConflictException(expectedRevision, currentRevision);
            }
        }

        state.Revision = checked(expectedRevision + 1);

        try
        {
            await using (var stream = new FileStream(
                TemporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(StatePath))
            {
                File.Replace(TemporaryPath, StatePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TemporaryPath, StatePath);
            }
        }
        catch
        {
            state.Revision = expectedRevision;
            throw;
        }
    }

    private async Task<long> ReadCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return 0;
        }

        await using var stream = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return document.RootElement.TryGetProperty("revision", out var revision)
            ? revision.GetInt64()
            : 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
