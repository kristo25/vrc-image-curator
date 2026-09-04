using VrcImageCurator.Core.Imaging;
using System.Text.Json.Serialization;

namespace VrcImageCurator.Core.Models;

public enum VrcImageCategory
{
    Emoji,
    Prints,
    Stickers,
}

public enum OrganizationPolicy
{
    CategoryRoot,
    CategoryYearMonth,
    PreserveIncomingRelativeFolder,
}

public enum SimilarityProfile
{
    Strict,
    Conservative,
    Broad,
}

public enum IndexStatus
{
    Stale,
    Building,
    Current,
    Unavailable,
}

public enum ReviewStatus
{
    Pending,
    NeedsReconciliation,
    Resolved,
}

public enum MatchKind
{
    Exact,
    Similar,
}

public enum ActivityKind
{
    Scan,
    AutomaticMove,
    ReviewDecision,
    Warning,
    Retry,
    DeletionRequested,
}

public enum ActivityLevel
{
    Information,
    Warning,
    Error,
}

public enum JournalOperationType
{
    Move,
    Recycle,
}

public enum JournalOperationPurpose
{
    HoldForReview,
    MoveUnique,
    MoveDuplicateOverride,
    KeepExisting,
    AutoKeepArchived,
    DeleteArchiveCandidate,
    PreserveArchiveCandidate,
    RestoreReviewToSource,
}

public enum JournalPhase
{
    IntentRecorded,
    SideEffectStarted,
    SideEffectApplied,
    StateCommitted,
    Completed,
    NeedsAttention,
}

public sealed class AppStateDocument
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public long Revision { get; set; }

    public AppSettings Settings { get; set; } = new();

    public ArchiveIndexState ArchiveIndex { get; set; } = new();

    public List<ReviewItem> ReviewQueue { get; set; } = [];

    public List<JournalEntry> OperationJournal { get; set; } = [];

    public List<ActivityEntry> History { get; set; } = [];
}

public sealed class AppSettings
{
    public List<CategoryMapping> CategoryMappings { get; set; } = [];

    public string OutputRootPath { get; set; } = string.Empty;

    public bool OutputRootConfirmed { get; set; } = true;

    public List<LegacyArchiveMapping> LegacyArchiveMappings { get; set; } = [];

    public OrganizationPolicy OrganizationPolicy { get; set; } = OrganizationPolicy.CategoryYearMonth;

    public SimilarityProfile SimilarityProfile { get; set; } = SimilarityProfile.Conservative;

    public AutomationSettings Automation { get; set; } = new();

    public string HoldingRootPath { get; set; } = string.Empty;

    public bool BringReviewForwardWhenHeld { get; set; } = true;
}

public sealed class CategoryMapping
{
    public VrcImageCategory Category { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string ArchivePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
}

public sealed class LegacyArchiveMapping
{
    public VrcImageCategory Category { get; set; }

    public string ArchivePath { get; set; } = string.Empty;
}

public sealed class AutomationSettings
{
    public bool WatchWhileOpen { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// How often folder watching runs a full sweep as a safety net for filesystem events the
    /// watcher may have missed. Clamped to <see cref="MinimumWatchScanSeconds"/>..<see cref="MaximumWatchScanSeconds"/>.
    /// </summary>
    public int WatchScanSeconds { get; set; } = DefaultWatchScanSeconds;

    public const int DefaultWatchScanSeconds = 60;

    public const int MinimumWatchScanSeconds = 15;

    public const int MaximumWatchScanSeconds = 3600;

    public TimeSpan WatchScanInterval => TimeSpan.FromSeconds(
        Math.Clamp(WatchScanSeconds, MinimumWatchScanSeconds, MaximumWatchScanSeconds));
}

public sealed class ArchiveIndexState
{
    public List<CategoryIndexState> Categories { get; set; } = [];
}

public sealed class CategoryIndexState
{
    public VrcImageCategory Category { get; set; }

    public IndexStatus Status { get; set; } = IndexStatus.Stale;

    public long Generation { get; set; }

    public DateTimeOffset? LastCompletedUtc { get; set; }

    public string? LastError { get; set; }

    public List<IndexedImageRecord> Images { get; set; } = [];
}

public sealed class IndexedImageRecord
{
    public Guid Id { get; set; }

    public VrcImageCategory Category { get; set; }

    public string Path { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTimeOffset LastWriteUtc { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string ExactFingerprint { get; set; } = string.Empty;

    public string PerceptualFingerprint { get; set; } = string.Empty;

    public ImageFingerprint? Fingerprint { get; set; }
}

public sealed class ReviewItem
{
    public Guid Id { get; set; }

    public VrcImageCategory Category { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    public string IncomingOriginalPath { get; set; } = string.Empty;

    public string HeldFilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsIncomingInPlace =>
        !string.IsNullOrWhiteSpace(IncomingOriginalPath)
        && string.Equals(IncomingOriginalPath, HeldFilePath, StringComparison.OrdinalIgnoreCase);

    public string IncomingFingerprint { get; set; } = string.Empty;

    public ImageFingerprint? IncomingImageFingerprint { get; set; }

    public long IndexGeneration { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public ScanRoutingContext RoutingContext { get; set; } = new();

    public List<ReviewCandidate> Candidates { get; set; } = [];
}

public sealed class ReviewCandidate
{
    public Guid Id { get; set; }

    public Guid IndexedImageId { get; set; }

    public string ArchivePath { get; set; } = string.Empty;

    public string ExpectedFingerprint { get; set; } = string.Empty;

    public MatchKind MatchKind { get; set; }

    public double SimilarityScore { get; set; }

    public List<string> MatchReasons { get; set; } = [];

    public bool IsSelected { get; set; }

    public bool IsStale { get; set; }
}

public sealed class ExpectedFileIdentity
{
    public string Fingerprint { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTimeOffset LastWriteUtc { get; set; }
}

public sealed class JournalEntry
{
    public Guid Id { get; set; }

    public JournalOperationType OperationType { get; set; }

    public JournalOperationPurpose Purpose { get; set; }

    public JournalPhase Phase { get; set; } = JournalPhase.IntentRecorded;

    public VrcImageCategory Category { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string? DestinationPath { get; set; }

    public ScanRoutingContext? RoutingContext { get; set; }

    public ExpectedFileIdentity ExpectedSource { get; set; } = new();

    public Guid? ReviewItemId { get; set; }

    public Guid? IndexedImageId { get; set; }

    public ReviewItem? ReviewItemAfterCommit { get; set; }

    public IndexedImageRecord? IndexedImageAfterCommit { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public string? LastError { get; set; }
}

public sealed class ScanRoutingContext
{
    public string SourceRootPath { get; set; } = string.Empty;

    public string RelativeDirectory { get; set; } = string.Empty;

    public string OutputRootPath { get; set; } = string.Empty;
}

public sealed class ActivityEntry
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredUtc { get; set; }

    public ActivityKind Kind { get; set; }

    public ActivityLevel Level { get; set; }

    public VrcImageCategory? Category { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? SourcePath { get; set; }

    public string? DestinationPath { get; set; }

    public Guid? OperationId { get; set; }
}

public static class AppStateDefaults
{
    public static readonly IReadOnlyList<VrcImageCategory> FixedCategories =
    [
        VrcImageCategory.Emoji,
        VrcImageCategory.Prints,
        VrcImageCategory.Stickers,
    ];

    public static AppStateDocument Create(
        string? userProfilePath = null,
        string? localAppDataPath = null,
        string? stateDirectoryPath = null)
    {
        userProfilePath ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        localAppDataPath ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Local (non-OneDrive) VRChat image root. The archive lives beside the category
        // folders rather than inside any of them, so no source ever contains the archive.
        var sourceRoot = Path.Combine(userProfilePath, "Images", "VRChat");
        var archiveRoot = Path.Combine(sourceRoot, "Archived Images");

        return new AppStateDocument
        {
            Settings = new AppSettings
            {
                OutputRootPath = archiveRoot,
                CategoryMappings = FixedCategories
                    .Select(category => new CategoryMapping
                    {
                        Category = category,
                        SourcePath = Path.Combine(sourceRoot, category.ToString()),
                        ArchivePath = Path.Combine(archiveRoot, category.ToString()),
                    })
                    .ToList(),
                HoldingRootPath = stateDirectoryPath is null
                    ? Path.Combine(localAppDataPath, "VrcImageCurator", "Holding")
                    : Path.Combine(stateDirectoryPath, "Holding"),
                OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder,
            },
            ArchiveIndex = new ArchiveIndexState
            {
                Categories = FixedCategories
                    .Select(category => new CategoryIndexState { Category = category })
                    .ToList(),
            },
        };
    }
}

public static class AppStateValidator
{
    public static void Validate(AppStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SchemaVersion != AppStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported state schema version {state.SchemaVersion}; expected {AppStateDocument.CurrentSchemaVersion}.");
        }

        ArgumentNullException.ThrowIfNull(state.Settings);
        ArgumentNullException.ThrowIfNull(state.Settings.Automation);
        ArgumentNullException.ThrowIfNull(state.Settings.CategoryMappings);
        ArgumentNullException.ThrowIfNull(state.Settings.LegacyArchiveMappings);
        ArgumentNullException.ThrowIfNull(state.ArchiveIndex);
        ArgumentNullException.ThrowIfNull(state.ArchiveIndex.Categories);
        ArgumentNullException.ThrowIfNull(state.ReviewQueue);
        ArgumentNullException.ThrowIfNull(state.OperationJournal);
        ArgumentNullException.ThrowIfNull(state.History);

        ValidateFixedCategories(
            state.Settings.CategoryMappings.Select(mapping => mapping.Category),
            "category mappings");
        ValidateFixedCategories(
            state.ArchiveIndex.Categories.Select(category => category.Category),
            "archive index categories");

        if (string.IsNullOrWhiteSpace(state.Settings.OutputRootPath))
        {
            throw new InvalidDataException("The output root path is required.");
        }

        foreach (var category in state.ArchiveIndex.Categories)
        {
            ArgumentNullException.ThrowIfNull(category.Images);
        }

        foreach (var item in state.ReviewQueue)
        {
            ArgumentNullException.ThrowIfNull(item.RoutingContext);
            ArgumentNullException.ThrowIfNull(item.Candidates);
            foreach (var candidate in item.Candidates)
            {
                ArgumentNullException.ThrowIfNull(candidate.MatchReasons);
            }
        }
    }

    private static void ValidateFixedCategories(IEnumerable<VrcImageCategory> categories, string fieldName)
    {
        var actual = categories.Order().ToArray();
        var expected = AppStateDefaults.FixedCategories.Order().ToArray();

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"State {fieldName} must contain each fixed category exactly once.");
        }
    }
}

public static class AppStateMigrator
{
    public static bool Migrate(AppStateDocument state, AppStateDocument defaults)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(defaults);

        if (state.SchemaVersion == AppStateDocument.CurrentSchemaVersion)
        {
            return false;
        }

        if (state.SchemaVersion is 2 or 3)
        {
            foreach (var index in state.ArchiveIndex.Categories)
            {
                index.Status = IndexStatus.Stale;
                index.LastError = "Image fingerprints must be refreshed for the improved matcher.";
            }

            MarkUnresolvedReviewsForReconciliation(state);

            state.SchemaVersion = AppStateDocument.CurrentSchemaVersion;
            return true;
        }

        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported state schema version {state.SchemaVersion}; expected 1, 2, 3, or {AppStateDocument.CurrentSchemaVersion}.");
        }

        state.Settings.LegacyArchiveMappings ??= [];
        var mappings = state.Settings.CategoryMappings;
        var inferredRoot = TryInferCommonRoot(mappings);
        state.Settings.OutputRootPath = inferredRoot ?? defaults.Settings.OutputRootPath;
        state.Settings.OutputRootConfirmed = inferredRoot is not null;

        if (inferredRoot is null)
        {
            state.Settings.LegacyArchiveMappings = mappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ArchivePath))
                .Select(mapping => new LegacyArchiveMapping
                {
                    Category = mapping.Category,
                    ArchivePath = mapping.ArchivePath,
                })
                .ToList();
        }

        foreach (var mapping in mappings)
        {
            mapping.ArchivePath = Path.Combine(state.Settings.OutputRootPath, mapping.Category.ToString());
        }

        RepairRoutingContexts(state);
        MarkUnresolvedReviewsForReconciliation(state);

        foreach (var index in state.ArchiveIndex.Categories)
        {
            index.Status = IndexStatus.Stale;
            index.LastError = "Application settings were migrated to the single output-root model.";
        }

        state.Settings.OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder;
        state.Settings.Automation.WatchWhileOpen = false;
        state.SchemaVersion = AppStateDocument.CurrentSchemaVersion;
        return true;
    }

    private static void MarkUnresolvedReviewsForReconciliation(AppStateDocument state)
    {
        foreach (var review in state.ReviewQueue.Where(item => item.Status != ReviewStatus.Resolved))
        {
            review.Status = ReviewStatus.NeedsReconciliation;
        }

        foreach (var journalReview in state.OperationJournal
            .Select(entry => entry.ReviewItemAfterCommit)
            .Where(review => review is not null && review.Status != ReviewStatus.Resolved))
        {
            journalReview!.Status = ReviewStatus.NeedsReconciliation;
        }
    }

    private static void RepairRoutingContexts(AppStateDocument state)
    {
        foreach (var review in state.ReviewQueue)
        {
            if (IsComplete(review.RoutingContext))
            {
                continue;
            }

            review.RoutingContext = TryCreateRoutingContext(
                state,
                review.Category,
                review.IncomingOriginalPath) ?? new ScanRoutingContext();
            if (!IsComplete(review.RoutingContext))
            {
                review.Status = ReviewStatus.NeedsReconciliation;
            }
        }

        foreach (var entry in state.OperationJournal.Where(item => !IsComplete(item.RoutingContext)))
        {
            entry.RoutingContext = entry.ReviewItemAfterCommit is { } review && IsComplete(review.RoutingContext)
                ? review.RoutingContext
                : TryCreateRoutingContext(state, entry.Category, entry.SourcePath);
            if (!IsComplete(entry.RoutingContext)
                && entry.Phase != JournalPhase.Completed
                && entry.OperationType == JournalOperationType.Move)
            {
                entry.Phase = JournalPhase.NeedsAttention;
                entry.LastError = "Routing context could not be reconstructed during migration.";
            }
        }
    }

    private static ScanRoutingContext? TryCreateRoutingContext(
        AppStateDocument state,
        VrcImageCategory category,
        string sourcePath)
    {
        try
        {
            var sourceRoot = state.Settings.CategoryMappings.Single(item => item.Category == category).SourcePath;
            if (string.IsNullOrWhiteSpace(sourceRoot)
                || string.IsNullOrWhiteSpace(sourcePath)
                || !Path.GetFullPath(sourcePath).StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(sourcePath)!;
            var relative = Path.GetRelativePath(sourceRoot, parent);
            return new ScanRoutingContext
            {
                SourceRootPath = Path.GetFullPath(sourceRoot),
                RelativeDirectory = relative == "." ? string.Empty : relative,
                OutputRootPath = Path.GetFullPath(state.Settings.OutputRootPath),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsComplete(ScanRoutingContext? context) =>
        context is not null
        && !string.IsNullOrWhiteSpace(context.SourceRootPath)
        && !string.IsNullOrWhiteSpace(context.OutputRootPath);

    private static string? TryInferCommonRoot(IReadOnlyCollection<CategoryMapping> mappings)
    {
        if (mappings.Count != AppStateDefaults.FixedCategories.Count)
        {
            return null;
        }

        var parents = new List<string>();
        try
        {
            foreach (var mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ArchivePath)
                    || !string.Equals(
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(mapping.ArchivePath)),
                        mapping.Category.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(mapping.ArchivePath));
                if (string.IsNullOrWhiteSpace(parent))
                {
                    return null;
                }

                parents.Add(Path.GetFullPath(parent));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return parents.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? parents[0] : null;
    }
}
