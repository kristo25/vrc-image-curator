using System.Text.Json;
using System.Text.Json.Serialization;
using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;
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
    public const string FingerprintFileName = "fingerprints.json";
    public const string FingerprintTemporaryFileName = "fingerprints.json.tmp";

    private const int RevisionProbeBytes = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// Used only to read documents written before fingerprints moved out of the state file.
    /// Those carry an extra "fingerprint" member per image that the strict options reject.
    /// </summary>
    private static readonly JsonSerializerOptions LegacySerializerOptions = CreateSerializerOptions(
        JsonUnmappedMemberHandling.Skip);

    private Dictionary<Guid, ImageFingerprint>? _persistedFingerprints;

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
        FingerprintPath = Path.Combine(StateDirectory, FingerprintFileName);
        FingerprintTemporaryPath = Path.Combine(StateDirectory, FingerprintTemporaryFileName);
        _defaultStateFactory = defaultStateFactory ?? (() => AppStateDefaults.Create());
    }

    public string StateDirectory { get; }

    public string StatePath { get; }

    public string TemporaryPath { get; }

    public string BackupPath { get; }

    public string FingerprintPath { get; }

    public string FingerprintTemporaryPath { get; }

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
            File.Delete(FingerprintPath);
            File.Delete(FingerprintTemporaryPath);
            _persistedFingerprints = null;
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
        var utf8 = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var legacyFingerprints = TryExtractLegacyFingerprints(utf8);

        // A pre-v5 document carries a fingerprint per image that the strict options reject, so
        // it is read leniently and the fingerprints are lifted out by hand.
        var state = JsonSerializer.Deserialize<AppStateDocument>(
                utf8,
                legacyFingerprints is null ? SerializerOptions : LegacySerializerOptions)
            ?? throw new InvalidDataException("The state document is empty.");

        if (legacyFingerprints is not null)
        {
            foreach (var image in state.ArchiveIndex.Categories.SelectMany(category => category.Images))
            {
                if (legacyFingerprints.TryGetValue(image.Id, out var lifted))
                {
                    image.Fingerprint = lifted;
                }
            }

            // Force the sidecar to be written by the migration save below.
            _persistedFingerprints = null;
        }
        else
        {
            await AttachFingerprintsAsync(state, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Returns the fingerprints embedded in a pre-v5 state document, or null when the document
    /// is already current and its fingerprints live in the sidecar.
    /// </summary>
    private static Dictionary<Guid, ImageFingerprint>? TryExtractLegacyFingerprints(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() >= AppStateDocument.CurrentSchemaVersion)
            {
                return null;
            }

            var lifted = new Dictionary<Guid, ImageFingerprint>();
            if (!document.RootElement.TryGetProperty("archiveIndex", out var index)
                || !index.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Array)
            {
                return lifted;
            }

            foreach (var category in categories.EnumerateArray())
            {
                if (!category.TryGetProperty("images", out var images)
                    || images.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var image in images.EnumerateArray())
                {
                    if (image.TryGetProperty("id", out var id)
                        && image.TryGetProperty("fingerprint", out var fingerprint)
                        && fingerprint.ValueKind == JsonValueKind.Object
                        && fingerprint.Deserialize<ImageFingerprint>(SerializerOptions) is { } parsed)
                    {
                        lifted[id.GetGuid()] = parsed;
                    }
                }
            }

            return lifted;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Let the strict read report the real problem.
            return null;
        }
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
            // The sidecar is written first. One that runs ahead of the state document only holds
            // unused entries; a state document ahead of the sidecar would reference fingerprints
            // that are not there and force an avoidable rebuild.
            if (!FingerprintsMatchSidecar(state))
            {
                await WriteFingerprintsAsync(state, cancellationToken).ConfigureAwait(false);
            }

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

    /// <summary>
    /// Reads just the revision number. "revision" is the second property written, so a small
    /// prefix is almost always enough; parsing the whole document here meant every write paid a
    /// second full parse of a file that can be tens of megabytes.
    /// </summary>
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
            bufferSize: RevisionProbeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var probe = new byte[RevisionProbeBytes];
        var read = await stream
            .ReadAtLeastAsync(probe, probe.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (TryReadRevisionFromPrefix(probe.AsSpan(0, read), out var probed))
        {
            return probed;
        }

        // The prefix did not contain it, so fall back to reading the document properly.
        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return document.RootElement.TryGetProperty("revision", out var revision)
            ? revision.GetInt64()
            : 0;
    }

    private static bool TryReadRevisionFromPrefix(ReadOnlySpan<byte> prefix, out long revision)
    {
        revision = 0;
        if (prefix.IsEmpty)
        {
            return false;
        }

        try
        {
            // isFinalBlock: false so a token cut off by the end of the prefix simply stops the
            // scan instead of being reported as malformed.
            var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
            var depth = 0;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName
                        when depth == 1 && reader.ValueTextEquals("revision"u8):
                        return reader.Read() && reader.TryGetInt64(out revision);
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static JsonSerializerOptions CreateSerializerOptions(
        JsonUnmappedMemberHandling unmappedMembers = JsonUnmappedMemberHandling.Disallow)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            UnmappedMemberHandling = unmappedMembers,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private async Task<Dictionary<Guid, ImageFingerprint>> ReadFingerprintsAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FingerprintPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                FingerprintPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer
                .DeserializeAsync<Dictionary<Guid, ImageFingerprint>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (IsUnreadableState(exception))
        {
            // Fingerprints are derived data. Losing them costs a rebuild, never an image, so an
            // unreadable sidecar is treated as empty and the affected indexes go stale.
            return [];
        }
    }

    /// <summary>
    /// Reattaches stored fingerprints. Any index holding a record without one is marked stale:
    /// a record with no fingerprint is silently skipped when matching, which would show up as
    /// duplicates being archived instead of queued for review.
    /// </summary>
    private async Task AttachFingerprintsAsync(AppStateDocument state, CancellationToken cancellationToken)
    {
        var fingerprints = await ReadFingerprintsAsync(cancellationToken).ConfigureAwait(false);
        _persistedFingerprints = fingerprints;

        foreach (var category in state.ArchiveIndex.Categories)
        {
            var incomplete = false;
            foreach (var image in category.Images)
            {
                if (fingerprints.TryGetValue(image.Id, out var fingerprint))
                {
                    image.Fingerprint = fingerprint;
                }
                else
                {
                    incomplete = true;
                }
            }

            if (incomplete && category.Status == IndexStatus.Current)
            {
                category.Status = IndexStatus.Stale;
                category.LastError = "Stored image fingerprints are incomplete; the index will be rebuilt.";
            }
        }
    }

    private async Task WriteFingerprintsAsync(AppStateDocument state, CancellationToken cancellationToken)
    {
        var fingerprints = state.ArchiveIndex.Categories
            .SelectMany(category => category.Images)
            .Where(image => image.Fingerprint is not null)
            .GroupBy(image => image.Id)
            .ToDictionary(group => group.Key, group => group.First().Fingerprint!);

        await using (var stream = new FileStream(
            FingerprintTemporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, fingerprints, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(FingerprintTemporaryPath, FingerprintPath, overwrite: true);
        _persistedFingerprints = fingerprints;
    }

    /// <summary>
    /// Compares the fingerprints held by the state document against the ones last read from or
    /// written to the sidecar. The comparison is by reference: a fingerprint that was re-derived
    /// is always a new instance, so a record whose file changed under the same identifier is
    /// still detected. Comparing identifiers alone would miss it and strand the stale fingerprint
    /// on disk.
    /// </summary>
    private bool FingerprintsMatchSidecar(AppStateDocument state)
    {
        if (_persistedFingerprints is null)
        {
            return false;
        }

        var seen = 0;
        foreach (var image in state.ArchiveIndex.Categories.SelectMany(category => category.Images))
        {
            if (image.Fingerprint is not { } fingerprint)
            {
                continue;
            }

            if (!_persistedFingerprints.TryGetValue(image.Id, out var persisted)
                || !ReferenceEquals(persisted, fingerprint))
            {
                return false;
            }

            seen++;
        }

        // A sidecar holding entries the index no longer references is rewritten so the orphans
        // are pruned rather than reloaded forever.
        return seen == _persistedFingerprints.Count;
    }
}
