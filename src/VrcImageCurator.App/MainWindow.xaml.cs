using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;
using VrcImageCurator.App.Services;
using VrcImageCurator.Core.Imaging;
using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Scanning;
using VrcImageCurator.Core.Storage;
using MessageBox = System.Windows.MessageBox;

namespace VrcImageCurator.App;

public partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly PreviewService _previewService = new();
    private readonly LatestRequestGuard _reviewDisplayRequests = new();
    private bool _busy;
    private bool _loadingSettings;
    private bool _scanProgressActive;
    private Guid? _validatedIncomingReviewId;
    private bool _validatedIncomingCurrent;

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        InitializeComponent();
        SimilarityCombo.ItemsSource = Enum.GetValues<SimilarityProfile>();
    }

    private ReviewItem? SelectedReview => QueueList.SelectedItem as ReviewItem;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(selectFirst: false, refreshSettings: true);
        var state = await _runtime.StateStore.LoadAsync();
        ShowPage(ReviewPage);
        UpdateWatchingDisplay();
        if (_runtime.StateStore.LastRecoveryNotice is { } recovery)
        {
            SetStatus("Unreadable local state was quarantined and the app recovered safely.");
            var source = recovery.RestoredBackup ? "The last valid backup was restored." : "Default settings were restored.";
            MessageBox.Show(
                this,
                $"{source}\n\nThe unreadable file was preserved at:\n{recovery.QuarantinedPath}",
                "Application state recovered",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (!state.Settings.OutputRootConfirmed)
        {
            SetStatus("Confirm the migrated main output folder in Settings before scanning.");
        }
        else if (!state.Settings.CategoryMappings.Any(mapping => mapping.IsEnabled))
        {
            SetStatus("Review is ready. Configure folders in Settings before scanning.");
        }
    }

    private async Task RefreshAsync(
        Guid? selectReviewId = null,
        bool selectFirst = true,
        bool refreshSettings = false)
    {
        _reviewDisplayRequests.Invalidate();
        var state = await _runtime.StateStore.LoadAsync();
        var queue = state.ReviewQueue
            .Where(item => item.Status != ReviewStatus.Resolved)
            .OrderBy(item => item.CreatedUtc)
            .ToArray();
        QueueList.ItemsSource = queue;
        QueueCountText.Text = $"{queue.Length} pending";
        ClearQueueButton.IsEnabled = !_busy && queue.Length > 0;
        HistoryList.ItemsSource = state.History.OrderByDescending(item => item.OccurredUtc).ToArray();
        var pendingOperations = state.OperationJournal.Where(item => item.Phase != JournalPhase.Completed).ToArray();
        var attentionCount = pendingOperations.Count(item => item.Phase == JournalPhase.NeedsAttention);
        RecoveryStatusText.Text = pendingOperations.Length == 0
            ? "No file operations need attention."
            : $"{pendingOperations.Length} operation(s) pending; {attentionCount} require a decision.";
        RetryRecoveryButton.IsEnabled = !_busy && pendingOperations.Length > 0;
        DismissRecoveryButton.IsEnabled = !_busy && attentionCount > 0;
        if (refreshSettings)
        {
            LoadSettings(state.Settings);
        }

        if (queue.Length == 0)
        {
            QueueList.SelectedItem = null;
            ShowEmptyReview(
                state.Settings.OutputRootConfirmed
                && state.Settings.CategoryMappings.Any(mapping => mapping.IsEnabled));
            return;
        }

        if (!selectFirst)
        {
            var preservedReview = selectReviewId is null
                ? null
                : queue.FirstOrDefault(item => item.Id == selectReviewId);
            if (preservedReview is null)
            {
                QueueList.SelectedItem = null;
                ShowEmptyReview();
                ReviewTitle.Text = "Select a review";
                ReviewSubtitle.Text = $"{queue.Length} pending review(s). Select one from the queue to load its images.";
                return;
            }

            QueueList.SelectedItem = preservedReview;
            return;
        }

        QueueList.SelectedItem = selectReviewId is null
            ? queue[0]
            : queue.FirstOrDefault(item => item.Id == selectReviewId) ?? queue[0];
    }

    private void LoadSettings(AppSettings settings)
    {
        // Populating the controls raises the same change events that drive auto-save.
        _loadingSettings = true;
        try
        {
            LoadCategory(settings, VrcImageCategory.Emoji, EmojiSource, EmojiEnabled);
            LoadCategory(settings, VrcImageCategory.Prints, PrintsSource, PrintsEnabled);
            LoadCategory(settings, VrcImageCategory.Stickers, StickersSource, StickersEnabled);
            OutputRoot.Text = settings.OutputRootPath;
            UpdateResolvedDestinations(settings.OutputRootPath);
            SimilarityCombo.SelectedItem = settings.SimilarityProfile;
            StartWithWindowsCheck.IsChecked = settings.Automation.StartWithWindows;
            StartWithWindowsCheck.IsEnabled = _runtime.AllowStartupRegistration;
            BringReviewForwardCheck.IsChecked = settings.BringReviewForwardWhenHeld;
            UpdateStartupStatusText();
            var missingFolders = CaptureSettingsDraftOrNull()?.DescribeMissingFolders();
            ShowSettingsNotice(
                missingFolders ?? "Settings save automatically.",
                isWarning: missingFolders is not null);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void UpdateStartupStatusText()
    {
        var executablePath = Environment.ProcessPath;
        StartupRegistrationStatus? startupStatus = !_runtime.AllowStartupRegistration
            ? null
            : executablePath is null
            ? StartupRegistrationStatus.Stale
            : _runtime.Startup.GetStatus(executablePath);
        StartupStatusText.Text = startupStatus switch
        {
            null => "Windows startup changes are disabled for this isolated data profile.",
            StartupRegistrationStatus.Disabled => "Windows startup is not registered.",
            StartupRegistrationStatus.Current => "Windows startup points to this executable.",
            StartupRegistrationStatus.Stale => "Windows startup points to an old location. Toggle the checkbox to repair it.",
            _ => "Windows startup status is unknown.",
        };
    }

    private static void LoadCategory(
        AppSettings settings,
        VrcImageCategory category,
        System.Windows.Controls.TextBox source,
        System.Windows.Controls.CheckBox enabled)
    {
        var mapping = settings.CategoryMappings.Single(item => item.Category == category);
        source.Text = mapping.SourcePath;
        enabled.IsChecked = mapping.IsEnabled;
    }

    private void UpdateResolvedDestinations(string outputRoot)
    {
        EmojiDestinationText.Text = $"Emoji: {Path.Combine(outputRoot, nameof(VrcImageCategory.Emoji))}";
        PrintsDestinationText.Text = $"Prints: {Path.Combine(outputRoot, nameof(VrcImageCategory.Prints))}";
        StickersDestinationText.Text = $"Stickers: {Path.Combine(outputRoot, nameof(VrcImageCategory.Stickers))}";
    }

    private async void QueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var displayRequest = _reviewDisplayRequests.Begin();
        var review = SelectedReview;
        if (review is null)
        {
            ShowEmptyReview();
            return;
        }

        ReviewTitle.Text = Path.GetFileName(review.IncomingOriginalPath);
        ReviewTitle.ToolTip = review.IncomingOriginalPath;
        _validatedIncomingReviewId = review.Id;
        _validatedIncomingCurrent = false;
        MoveUniqueButton.IsEnabled = false;
        CandidateList.IsEnabled = false;
        RemoveReviewButton.IsEnabled = !_busy;
        var incomingTiming = review.IncomingImageFingerprint is { FrameCount: > 1 } incoming
            ? $" | {incoming.FrameCount} frames, {incoming.FrameDelaysMilliseconds.Sum()} ms"
            : string.Empty;
        ReviewSubtitle.Text = $"{review.Category} | {review.Candidates.Count} possible match(es) | {review.Status}{incomingTiming}";
        IncomingResolutionText.Text = review.IncomingImageFingerprint is { } incomingFingerprint
            ? $"{incomingFingerprint.Width} x {incomingFingerprint.Height}"
            : "Resolution unavailable";
        IncomingAvailabilityText.Text = "Checking incoming file...";
        IncomingLocationText.Text = review.IsIncomingInPlace
            ? $"Incoming file: {review.IncomingOriginalPath}"
            : $"Original: {review.IncomingOriginalPath}\nLegacy review copy: {review.HeldFilePath}";
        IncomingLocationText.ToolTip = IncomingLocationText.Text;
        IncomingLocationText.SetValue(
            System.Windows.Automation.AutomationProperties.HelpTextProperty,
            IncomingLocationText.Text);
        await _previewService.ShowAsync(IncomingPreview, review.HeldFilePath);
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        var incomingCurrent = await IsIncomingCurrentAsync(review);
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        _validatedIncomingReviewId = review.Id;
        _validatedIncomingCurrent = incomingCurrent;
        IncomingAvailabilityText.Text = incomingCurrent
            ? "Incoming file is available and unchanged."
            : "Incoming file is missing or changed. Remove this item or scan the file again.";

        var state = await _runtime.StateStore.LoadAsync();
        if (!_reviewDisplayRequests.IsCurrent(displayRequest) || SelectedReview?.Id != review.Id)
        {
            return;
        }

        var indexed = state.ArchiveIndex.Categories
            .SelectMany(category => category.Images)
            .ToDictionary(image => image.Id);
        CandidateList.ItemsSource = review.Candidates.Select(candidate =>
        {
            indexed.TryGetValue(candidate.IndexedImageId, out var record);
            var resolution = record is null
                ? "Resolution unavailable"
                : $"{record.Width} x {record.Height}";
            var timing = record?.Fingerprint is { FrameCount: > 1 } fingerprint
                ? $" Frames: {fingerprint.FrameCount}; duration: {fingerprint.FrameDelaysMilliseconds.Sum()} ms."
                : string.Empty;
            return new CandidatePreviewItem(
                candidate,
                $"{candidate.MatchKind} | {candidate.SimilarityScore:P0} similar",
                resolution,
                string.Join("  ", candidate.MatchReasons) + timing,
                candidate.ArchivePath);
        }).ToArray();
        CandidateHint.Text = review.Candidates.Count == 0
            ? "No remaining matches."
            : $"{review.Candidates.Count} ranked candidate(s)";
        UpdateActionAvailability(review);
    }

    private void ShowEmptyReview(bool isConfigured = true)
    {
        ReviewTitle.Text = isConfigured ? "Review queue is clear" : "Set up your image folders";
        ReviewTitle.ToolTip = null;
        ReviewSubtitle.Text = isConfigured
            ? "New matches will appear here after a scan."
            : "Open Settings to confirm the output folder and enable at least one VRCX category.";
        _previewService.Stop(IncomingPreview);
        IncomingPreview.Source = null;
        CandidateList.ItemsSource = null;
        IncomingResolutionText.Text = string.Empty;
        IncomingAvailabilityText.Text = string.Empty;
        IncomingLocationText.Text = string.Empty;
        IncomingLocationText.ToolTip = null;
        IncomingLocationText.ClearValue(System.Windows.Automation.AutomationProperties.HelpTextProperty);
        CandidateHint.Text = "No pending matches.";
        MoveUniqueButton.IsEnabled = false;
        RemoveReviewButton.IsEnabled = false;
        _validatedIncomingReviewId = null;
        _validatedIncomingCurrent = false;
    }

    private void UpdateActionAvailability(ReviewItem review)
    {
        var incomingCurrent = _validatedIncomingReviewId == review.Id
            ? _validatedIncomingCurrent
            : File.Exists(review.HeldFilePath);
        MoveUniqueButton.IsEnabled = !_busy && incomingCurrent;
        CandidateList.IsEnabled = !_busy && incomingCurrent;
        RemoveReviewButton.IsEnabled = !_busy;
        ClearQueueButton.IsEnabled = !_busy && QueueList.Items.Count > 0;
        if (!incomingCurrent)
        {
            CandidateHint.Text = "The incoming file is missing or changed. Remove this review or scan the file again.";
        }
    }

    private async Task<bool> IsIncomingCurrentAsync(ReviewItem review)
    {
        if (!File.Exists(review.HeldFilePath))
        {
            return false;
        }

        var decoded = await _runtime.Decoder.DecodeAsync(review.HeldFilePath);
        return decoded.IsSuccess
            && ImageFingerprint.Create(decoded.Image!).ExactIdentity == review.IncomingFingerprint;
    }

    private async void ScanNow(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RunBusyAsync(
            "Scanning enabled categories...",
            async () =>
            {
                BeginScanProgress();
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var processingProgress = new Progress<ScanProcessingProgress>(UpdateScanProcessingProgress);
                var results = await _runtime.Scanner.ScanAllAsync(
                    progress: progress,
                    processingProgress: processingProgress);
                if (results.Count == 0)
                {
                    SetStatus("Nothing scanned: enable at least one category in Settings.");
                    MessageBox.Show(this, StatusText.Text, "Scan now", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var moved = results.Sum(result => result.MovedUnique);
                var held = results.Sum(result => result.HeldForReview);
                var examined = results.Sum(result => result.Examined);
                var skipped = results.Sum(result => result.Skipped);
                var errors = results.Sum(result => result.Errors.Count);
                SetStatus($"Scan complete: {examined} examined, {moved} moved, {held} queued, {skipped} skipped, {errors} failed.");
                await ReportScanErrorsAsync(results.SelectMany(result => result.Errors));
                await RefreshAsync();
                ShowPage(ReviewPage);
            });
    }

    private async void ScanAnotherFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose another image folder to scan",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var categoryDialog = new CategorySelectionDialog { Owner = this };
        if (categoryDialog.ShowDialog() != true)
        {
            return;
        }

        await RunBusyAsync(
            $"Scanning {picker.FolderName}...",
            async () =>
            {
                BeginScanProgress();
                var progress = new Progress<ScanProgress>(UpdateScanProgress);
                var processingProgress = new Progress<ScanProcessingProgress>(UpdateScanProcessingProgress);
                var result = await _runtime.Scanner.ScanFolderAsync(
                    picker.FolderName,
                    categoryDialog.SelectedCategory,
                    progress: progress,
                    processingProgress: processingProgress);
                SetStatus($"Folder scan complete: {result.Examined} examined, {result.MovedUnique} moved, {result.HeldForReview} queued, {result.Skipped} skipped, {result.Errors.Count} failed.");
                await ReportScanErrorsAsync(result.Errors);
                await RefreshAsync();
                ShowPage(ReviewPage);
            });
    }

    private async void ToggleWatching(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_runtime.Watcher.IsRunning)
        {
            await _runtime.StopWatchingAsync();
            UpdateWatchingDisplay();
            SetStatus("Watching stopped.");
            return;
        }

        await RunBusyAsync(
            "Starting folder monitoring...",
            async () =>
            {
                var state = await _runtime.StateStore.LoadAsync();
                var enabled = state.Settings.CategoryMappings.Where(mapping => mapping.IsEnabled).ToArray();
                if (enabled.Length == 0)
                {
                    throw new InvalidOperationException("Enable at least one category in Settings before starting watching.");
                }

                await _runtime.StartWatchingAsync();
                UpdateWatchingDisplay();
                SetStatus("Watching configured VRCX folders.");
            });
    }

    private void UpdateWatchingDisplay()
    {
        var running = _runtime.Watcher.IsRunning;
        WatchButton.Content = running ? "Stop watching" : "Start watching";
        WatchButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            running ? "Stop watching configured image folders" : "Start watching configured image folders");
        WatchStatusText.Text = running ? "Watching" : "Not watching";
    }

    private async void MoveAsUnique(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        if (review is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunReviewActionAsync(
            async () =>
            {
                if (!await ValidateReviewAsync(review))
                {
                    return false;
                }

                return await MoveReviewAsync(review);
            });
    }

    private async void KeepIncoming(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        var candidate = (sender as System.Windows.Controls.Button)?.CommandParameter as ReviewCandidate;
        if (review is null || candidate is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunBusyAsync(
            "Keeping incoming image...",
            async () =>
            {
                if (!await ValidateReviewAsync(review))
                {
                    return;
                }

                var canRecycle = _runtime.Router.CanRecycle(candidate.ArchivePath);
                if (MessageBox.Show(
                        this,
                        canRecycle
                            ? "Recycle this archived match and keep the incoming image? If other matches remain, the review will stay open."
                            : "Windows Recycle Bin is unavailable for this archive drive. Move the archived match into the VRC Image Curator Replaced folder and keep the incoming image?",
                        "Keep incoming",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    SetStatus("Keep incoming canceled.");
                    return;
                }

                KeepIncomingResult result;
                try
                {
                    result = await _runtime.Router.KeepIncomingOverMatchAsync(review, candidate);
                }
                catch
                {
                    await RefreshAsync();
                    throw;
                }

                await RefreshAsync(result.ReviewResolved ? null : review.Id);
                SetStatus(result.PreservedMatchPath is not null
                    ? $"Incoming image kept. The previous match was preserved at {result.PreservedMatchPath}"
                    : result.ReviewResolved
                        ? "Incoming image kept and moved into the archive."
                        : "Archived match removed. Review the remaining matches.");
            });
    }

    private async void KeepMatch(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        var candidate = (sender as System.Windows.Controls.Button)?.CommandParameter as ReviewCandidate;
        if (review is null || candidate is null)
        {
            await RefreshChangedReviewAsync();
            return;
        }

        await RunReviewActionAsync(
            async () =>
            {
                if (!await ValidateReviewAsync(review))
                {
                    return false;
                }

                if (!_runtime.Router.CanRecycle(review.HeldFilePath))
                {
                    MessageBox.Show(this, "This incoming file location does not support the Windows Recycle Bin.", "Keep match unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (MessageBox.Show(
                        this,
                        "Recycle the incoming image and keep this archived match?",
                        "Keep match",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    SetStatus("Keep match canceled.");
                    return false;
                }

                await _runtime.Router.KeepMatchAsync(review, candidate);
                return true;
            });
    }

    private async Task RefreshChangedReviewAsync()
    {
        SetStatus("That review changed before the action could start. Refreshing the queue...");
        await RefreshAsync();
    }

    private async Task<bool> MoveReviewAsync(ReviewItem review)
    {
        if (review.IncomingImageFingerprint is null)
        {
            MessageBox.Show(this, "The incoming fingerprint is missing. Run a new scan.", "Cannot move", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        await _runtime.Router.MoveUniqueAsync(
                review.HeldFilePath,
                review.Category,
                review.IncomingImageFingerprint,
                review.RoutingContext,
                reviewItemId: review.Id,
                duplicateOverride: false);
        return true;
    }

    private async void ClearReviewQueue(object sender, RoutedEventArgs e)
    {
        var state = await _runtime.StateStore.LoadAsync();
        var reviews = state.ReviewQueue
            .Where(review => review.Status != ReviewStatus.Resolved)
            .OrderBy(review => review.CreatedUtc)
            .ToArray();
        if (reviews.Length == 0)
        {
            return;
        }

        var legacyHeldCount = reviews.Count(review => !review.IsIncomingInPlace);
        var message = legacyHeldCount == 0
            ? $"Remove {reviews.Length} item(s) from the review queue? The incoming files will remain untouched."
            : $"Clear {reviews.Length} review item(s)? In-place incoming files will remain untouched, and {legacyHeldCount} legacy held file(s) will be returned to their original folders.";
        if (MessageBox.Show(
                this,
                message,
                "Clear review queue",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Clearing review queue...",
            async () =>
            {
                BeginReturnProgress(reviews.Length);
                var progress = new Progress<ReviewRestoreProgress>(UpdateReturnProgress);
                var result = await _runtime.Router.RestoreReviewsAsync(reviews, progress);

                await RefreshAsync(selectFirst: false);
                SetStatus(result.Failures.Count == 0
                    ? $"Review queue cleared. {result.Restored} item(s) removed."
                    : $"Cleared {result.Restored} item(s); {result.Failures.Count} review(s) remain.");
                if (result.Failures.Count > 0)
                {
                    MessageBox.Show(this, string.Join(Environment.NewLine, result.Failures.Take(8)), "Some reviews could not be cleared", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
    }

    private async void RemoveSelectedReview(object sender, RoutedEventArgs e)
    {
        var review = SelectedReview;
        if (review is null)
        {
            return;
        }

        var message = review.IsIncomingInPlace
            ? "Remove this item from the Review queue? The incoming file will remain untouched."
            : File.Exists(review.HeldFilePath)
                ? "Return this legacy held image to its original folder and remove it from the Review queue?"
                : "Remove this unavailable legacy review entry? No image file was found to move.";
        if (MessageBox.Show(
                this,
                message,
                "Remove review item",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Removing review item...",
            async () =>
            {
                if (review.IsIncomingInPlace || !File.Exists(review.HeldFilePath))
                {
                    await _runtime.Router.DismissReviewAsync(review.Id);
                }
                else
                {
                    await _runtime.Router.RestoreReviewAsync(review);
                }

                await RefreshAsync(selectFirst: false);
                SetStatus("Review item removed. The incoming image was not deleted.");
            });
    }

    private async Task<bool> ValidateReviewAsync(ReviewItem review)
    {
        if (!File.Exists(review.HeldFilePath))
        {
            MessageBox.Show(this, "The incoming file is missing. Remove this review item or scan the file again.", "Review changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (review.Status == ReviewStatus.NeedsReconciliation)
        {
            return await ReconcileReviewAsync(review);
        }

        var staleIds = new List<Guid>();
        foreach (var candidate in review.Candidates)
        {
            var decoded = await _runtime.Decoder.DecodeAsync(candidate.ArchivePath);
            if (!decoded.IsSuccess
                || ImageFingerprint.Create(decoded.Image!).ExactIdentity != candidate.ExpectedFingerprint)
            {
                staleIds.Add(candidate.Id);
            }
        }

        if (staleIds.Count == 0)
        {
            return true;
        }

        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                var current = state.ReviewQueue.Single(item => item.Id == review.Id);
                current.Candidates.RemoveAll(item => staleIds.Contains(item.Id));
                current.Status = ReviewStatus.Pending;
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Warning,
                    Level = ActivityLevel.Warning,
                    Category = current.Category,
                    Message = $"Removed {staleIds.Count} changed or missing candidate(s) from a pending review.",
                    SourcePath = current.HeldFilePath,
                });

                return true;
            });
        await RefreshAsync(review.Id);
        MessageBox.Show(this, "One or more archive candidates changed or disappeared. Their stale references were removed safely; review the remaining matches before choosing a terminal action.", "Review reconciled", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task<bool> ReconcileReviewAsync(ReviewItem review)
    {
        SetStatus("Refreshing this review with the current matcher...");
        var incomingDecoded = await _runtime.Decoder.DecodeAsync(review.HeldFilePath);
        if (!incomingDecoded.IsSuccess)
        {
            MessageBox.Show(
                this,
                "The incoming image could not be decoded. Remove this review item or scan the file again.",
                "Review refresh failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var incomingFingerprint = ImageFingerprint.Create(incomingDecoded.Image!);
        var refreshedIndex = await _runtime.Indexer.RefreshAsync(review.Category);
        if (refreshedIndex.Status != IndexStatus.Current || refreshedIndex.Errors.Count > 0)
        {
            MessageBox.Show(
                this,
                "The archive index could not be refreshed completely. This review remains locked for reconciliation; check History for the indexing errors and try again.",
                "Review refresh incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var currentState = await _runtime.StateStore.LoadAsync();
        var indexedCategory = currentState.ArchiveIndex.Categories
            .Single(category => category.Category == review.Category);
        var indexedByKey = indexedCategory.Images
            .Where(image => image.Fingerprint is { HasCurrentFeatures: true })
            .ToDictionary(image => image.Id.ToString("D"), StringComparer.Ordinal);
        var ranked = ImageMatcher.RankCandidates(
            incomingFingerprint,
            indexedByKey.Select(pair => new ImageCandidate(pair.Key, pair.Value.Fingerprint!)),
            currentState.Settings.SimilarityProfile);
        var refreshedCandidates = ranked
            .Select(match =>
            {
                var indexed = indexedByKey[match.CandidateKey];
                return new ReviewCandidate
                {
                    Id = Guid.NewGuid(),
                    IndexedImageId = indexed.Id,
                    ArchivePath = indexed.Path,
                    ExpectedFingerprint = indexed.Fingerprint!.ExactIdentity,
                    MatchKind = match.MatchKind,
                    SimilarityScore = match.SimilarityScore,
                    MatchReasons = match.MatchReasons.ToList(),
                };
            })
            .ToList();

        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                var current = state.ReviewQueue.Single(item => item.Id == review.Id);
                current.IncomingFingerprint = incomingFingerprint.ExactIdentity;
                current.IncomingImageFingerprint = incomingFingerprint;
                current.IndexGeneration = refreshedIndex.Generation;
                current.Candidates = refreshedCandidates;
                current.Status = ReviewStatus.Pending;
                return true;
            });

        await RefreshAsync(review.Id);
        MessageBox.Show(
            this,
            refreshedCandidates.Count == 0
                ? "This image no longer matches anything in the archive. Review it, then choose Move as Unique."
                : "This review was rescanned with the current matcher. Review the refreshed matches, then choose an action again.",
            "Review refreshed",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private async Task RunReviewActionAsync(Func<Task<bool>> action)
    {
        await RunBusyAsync(
            "Applying review decision...",
            async () =>
            {
                if (!await action())
                {
                    return;
                }

                await RefreshAsync();
                StatusText.Text = "Review decision completed.";
                ReviewNavButton.Focus();
            });
    }

    private void SettingsToggled(object sender, RoutedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsFieldCommitted(object sender, RoutedEventArgs e) => BeginAutoSaveSettings();

    private void SettingsFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        BeginAutoSaveSettings();
    }

    private void BeginAutoSaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _ = AsyncCommandRunner.RunAsync(AutoSaveSettingsAsync, ReportSettingsAutoSaveFailureAsync);
    }

    /// <summary>
    /// Persists the settings controls as soon as a field is committed. Folder-existence problems
    /// are reported inline rather than refused, because a watched folder is allowed to appear
    /// later; only the overlap invariants that could make the app scan its own archive block a save.
    /// </summary>
    private async Task AutoSaveSettingsAsync()
    {
        var draft = CaptureSettingsDraftOrNull();
        if (draft is null)
        {
            ShowSettingsNotice("Choose a main output folder.", isWarning: true);
            return;
        }

        var settings = (await _runtime.StateStore.LoadAsync()).Settings;
        if (draft.DescribeBlockingProblem(settings) is { } blocking)
        {
            ShowSettingsNotice(blocking, isWarning: true);
            return;
        }

        var startupChanged = settings.Automation.StartWithWindows != draft.StartWithWindows;
        await _runtime.StateStore.UpdateAsync(
            state =>
            {
                draft.ApplyTo(state);
                return true;
            });

        if (startupChanged)
        {
            await _runtime.ApplyAutomationSettingsAsync(updateStartupRegistration: true);
        }

        UpdateResolvedDestinations(draft.OutputRootPath);
        UpdateStartupStatusText();
        var missing = draft.DescribeMissingFolders();
        ShowSettingsNotice(
            missing ?? $"Settings saved at {DateTime.Now:t}.",
            isWarning: missing is not null);
    }

    private async Task ReportSettingsAutoSaveFailureAsync(Exception exception)
    {
        var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        await Dispatcher.InvokeAsync(
            () => ShowSettingsNotice($"Settings were not saved. {exception.Message}{detail}", isWarning: true));
    }

    private void ShowSettingsNotice(string? message, bool isWarning)
    {
        SettingsNoticeText.Text = message ?? string.Empty;
        SettingsNoticeText.Foreground = isWarning && !string.IsNullOrWhiteSpace(message)
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
    }

    private async void CreateMissingFolders(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "Creating folders...",
            async () =>
            {
                var draft = CaptureSettingsDraftOrNull();
                if (draft is null)
                {
                    ShowSettingsNotice("Choose a main output folder.", isWarning: true);
                    return;
                }

                Directory.CreateDirectory(draft.OutputRootPath);
                foreach (var category in AppStateDefaults.FixedCategories)
                {
                    Directory.CreateDirectory(Path.Combine(draft.OutputRootPath, category.ToString()));
                }

                foreach (var category in draft.Categories.Where(
                    item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.SourcePath)))
                {
                    Directory.CreateDirectory(category.SourcePath);
                }

                await AutoSaveSettingsAsync();
                SetStatus("Folders created.");
            });
    }

    private SettingsDraft? CaptureSettingsDraftOrNull()
    {
        try
        {
            return CaptureSettingsDraft();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private SettingsDraft CaptureSettingsDraft()
    {
        var outputRoot = OutputRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new InvalidOperationException("Choose a main output folder.");
        }

        return new SettingsDraft(
            [
                new CategorySettingsDraft(VrcImageCategory.Emoji, EmojiSource.Text.Trim(), EmojiEnabled.IsChecked == true),
                new CategorySettingsDraft(VrcImageCategory.Prints, PrintsSource.Text.Trim(), PrintsEnabled.IsChecked == true),
                new CategorySettingsDraft(VrcImageCategory.Stickers, StickersSource.Text.Trim(), StickersEnabled.IsChecked == true),
            ],
            outputRoot,
            (SimilarityProfile?)SimilarityCombo.SelectedItem ?? SimilarityProfile.Conservative,
            StartWithWindowsCheck.IsChecked == true,
            BringReviewForwardCheck.IsChecked == true);
    }

    private async void RebuildIndexes(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "Rebuilding enabled archive indexes...",
            async () =>
            {
                var state = await _runtime.StateStore.LoadAsync();
                foreach (var mapping in state.Settings.CategoryMappings.Where(item => item.IsEnabled))
                {
                    await _runtime.Indexer.RebuildAsync(mapping.Category);
                }

                StatusText.Text = "Archive indexes rebuilt.";
                await RefreshAsync();
            });
    }

    private async void ClearLocalData(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        if (MessageBox.Show(
                this,
                "Clear settings, cache, index, and history? Images are never removed by this action.",
                "Clear local data",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Clearing local application data...",
            async () =>
            {
                var wasWatching = _runtime.Watcher.IsRunning;
                await _runtime.StopWatchingAsync();
                ClearLocalDataResult result;
                try
                {
                    result = await _runtime.StateStore.TryClearLocalDataAsync();
                }
                catch
                {
                    if (wasWatching)
                    {
                        await _runtime.StartWatchingAsync();
                    }

                    throw;
                }
                if (!result.WasCleared)
                {
                    if (wasWatching)
                    {
                        await _runtime.StartWatchingAsync();
                    }

                    if (result.Status == ClearLocalDataStatus.BlockedByHeldFiles)
                    {
                        var holdingRoot = (await _runtime.StateStore.LoadAsync()).Settings.HoldingRootPath;
                        var open = MessageBox.Show(
                            this,
                            $"Local data was not cleared because orphaned images remain in the holding folder. Move them somewhere safe first.\n\n{holdingRoot}\n\nOpen this folder now?",
                            "Held images need attention",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning);
                        if (open == MessageBoxResult.OK)
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(
                                    new System.Diagnostics.ProcessStartInfo(holdingRoot) { UseShellExecute = true });
                            }
                            catch (Exception exception) when (
                                exception is System.ComponentModel.Win32Exception
                                    or IOException
                                    or InvalidOperationException)
                            {
                                MessageBox.Show(
                                    this,
                                    $"The holding folder could not be opened. Open it manually:\n\n{holdingRoot}\n\n{exception.Message}",
                                    "Could not open folder",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            }
                        }

                        return;
                    }

                    var message = result.Status == ClearLocalDataStatus.BlockedByUnsafeHoldingRoot
                        ? "Local data was not cleared because the stored holding folder is outside the application-data boundary."
                        : "Local data cannot be cleared while reviews or file operations are pending.";
                    MessageBox.Show(this, message, "Clear blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_runtime.AllowStartupRegistration && Environment.ProcessPath is { } executablePath)
                {
                    _runtime.Startup.SetEnabled(false, executablePath);
                }

                _ = await _runtime.StateStore.LoadAsync();
                await RefreshAsync(refreshSettings: true);
                SetStatus("Local application data cleared. Watching and Windows startup were stopped.");
            });
    }

    private async void RetryRecovery(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(
            "Retrying recoverable file operations...",
            async () =>
            {
                var decisions = await _runtime.Router.RecoverPendingOperationsAsync();
                await RefreshAsync(selectFirst: false);
                var unresolved = (await _runtime.StateStore.LoadAsync()).OperationJournal.Count;
                SetStatus($"Recovery checked {decisions.Count} operation(s); {unresolved} still need attention.");
            });
    }

    private async void DismissRecovery(object sender, RoutedEventArgs e)
    {
        if (_busy
            || MessageBox.Show(
                this,
                "Dismiss all ambiguous operations? No files will be moved or deleted. Affected indexes will be rebuilt on the next scan.",
                "Dismiss recovery operations",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            "Dismissing ambiguous operations...",
            async () =>
            {
                var dismissed = await _runtime.Router.DismissNeedsAttentionOperationsAsync();
                await RefreshAsync(selectFirst: false);
                SetStatus($"Dismissed {dismissed} operation(s) without changing files.");
            });
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string targetName }
            || FindName(targetName) is not System.Windows.Controls.TextBox target)
        {
            return;
        }

        var picker = new OpenFolderDialog
        {
            Title = "Choose folder",
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : null,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) == true)
        {
            target.Text = picker.FolderName;
            if (ReferenceEquals(target, OutputRoot))
            {
                UpdateResolvedDestinations(target.Text);
            }
        }
    }

    private void OutputRootChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (EmojiDestinationText is not null)
        {
            UpdateResolvedDestinations(OutputRoot.Text.Trim());
        }
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (_busy)
        {
            SetStatus("Another operation is still running. Wait for it to finish, then try again.");
            return;
        }

        _busy = true;
        ScanButton.IsEnabled = false;
        ScanAnotherButton.IsEnabled = false;
        WatchButton.IsEnabled = false;
        RetryRecoveryButton.IsEnabled = false;
        DismissRecoveryButton.IsEnabled = false;
        SetStatus(status);
        if (SelectedReview is { } review)
        {
            UpdateActionAvailability(review);
        }

        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            SetStatus("Action failed. No permanent deletion fallback was used.");
            var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
            var details = logPath is null
                ? "\n\nThe diagnostic log could not be written."
                : $"\n\nDetails were written to:\n{logPath}";
            MessageBox.Show(this, exception.Message + details, "VRC Image Curator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndScanProgress();
            _busy = false;
            ScanButton.IsEnabled = true;
            ScanAnotherButton.IsEnabled = true;
            WatchButton.IsEnabled = true;
            UpdateWatchingDisplay();
            if (SelectedReview is { } currentReview)
            {
                UpdateActionAvailability(currentReview);
            }
        }
    }

    private void ShowReviewPage(object sender, RoutedEventArgs e) => ShowPage(ReviewPage);

    private void ShowHistoryPage(object sender, RoutedEventArgs e) => ShowPage(HistoryPage);

    private void ShowSettingsPage(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);

    private void ShowPage(UIElement page)
    {
        ReviewPage.Visibility = page == ReviewPage ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == HistoryPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        ReviewNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == ReviewPage ? "Current page" : string.Empty);
        HistoryNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == HistoryPage ? "Current page" : string.Empty);
        SettingsNavButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, page == SettingsPage ? "Current page" : string.Empty);

        if (IsLoaded)
        {
            _ = page == ReviewPage
                ? ReviewHeading.Focus()
                : page == HistoryPage
                    ? HistoryHeading.Focus()
                    : SettingsHeading.Focus();
        }
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IncomingScale is null)
        {
            return;
        }

        IncomingScale.ScaleX = e.NewValue;
        IncomingScale.ScaleY = e.NewValue;
    }

    private async void CandidatePreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image { Tag: string path } preview)
        {
            await _previewService.ShowAsync(preview, path, decodeWidth: 360);
        }
    }

    private void CandidatePreviewUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image preview)
        {
            _previewService.Stop(preview);
        }
    }

    public Task RefreshFromExternalAsync() => RefreshAsync(SelectedReview?.Id, selectFirst: false);

    public async Task ScanFromTrayAsync()
    {
        var results = await _runtime.Scanner.ScanAllAsync();
        var refreshTask = await Dispatcher.InvokeAsync(() => RefreshAsync());
        await refreshTask;
        var queued = results.Sum(result => result.HeldForReview);
        await Dispatcher.InvokeAsync(() => SetStatus($"Background scan complete: {queued} queued for review."));
    }

    public void ReportWatcherFailure(VrcImageCategory category, string message, string? logPath)
    {
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        SetStatus($"Watching {category} failed: {message}.{detail}");
        WatchStatusText.Text = "Watching - error";
    }

    public void ReportBackgroundFailure(string operation, string message, string? logPath)
    {
        var detail = logPath is null ? string.Empty : $" Details: {logPath}";
        SetStatus($"{operation} failed: {message}.{detail}");
    }

    private async Task ReportScanErrorsAsync(IEnumerable<string> errors)
    {
        var failures = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var exception = new InvalidOperationException(string.Join(Environment.NewLine, failures));
        var logPath = await DiagnosticLog.TryWriteAsync(_runtime.StateDirectory, exception);
        var details = logPath is null ? string.Empty : $"\n\nFull details:\n{logPath}";
        MessageBox.Show(
            this,
            $"{failures.Length} scan issue(s) occurred.\n\n{string.Join(Environment.NewLine, failures.Take(5))}{details}",
            "Scan completed with warnings",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void BeginScanProgress()
    {
        _scanProgressActive = true;
        ScanProgressLabel.Text = "Reading";
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = 1;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "Counting";
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = 1;
        ProcessingProgressBar.Value = 0;
        ProcessingProgressText.Text = string.Empty;
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Maximum = Math.Max(1, progress.TotalImages);
        ScanProgressBar.Value = Math.Min(progress.ScannedImages, ScanProgressBar.Maximum);
        ScanProgressText.Text = $"{progress.ScannedImages} / {progress.TotalImages}";
        SetStatus($"Scanning {progress.Category}: {progress.ScannedImages} of {progress.TotalImages} images.");
    }

    private void UpdateScanProcessingProgress(ScanProcessingProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ProcessingProgressPanel.Visibility = Visibility.Visible;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressBar.Maximum = Math.Max(1, progress.TotalImages);
        ProcessingProgressBar.Value = Math.Min(progress.ProcessedImages, ProcessingProgressBar.Maximum);
        ProcessingProgressText.Text = $"{progress.ProcessedImages} / {progress.TotalImages}";
        SetStatus($"Processing scan results: {progress.ProcessedImages} of {progress.TotalImages} images.");
    }

    private void BeginReturnProgress(int total)
    {
        _scanProgressActive = true;
        ScanProgressLabel.Text = "Queue";
        ScanProgressPanel.Visibility = Visibility.Visible;
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = Math.Max(1, total);
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = $"0 / {total}";
        SetStatus($"Clearing review queue: 0 of {total} processed.");
    }

    private void UpdateReturnProgress(ReviewRestoreProgress progress)
    {
        if (!_scanProgressActive)
        {
            return;
        }

        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Maximum = Math.Max(1, progress.Total);
        ScanProgressBar.Value = Math.Min(progress.Processed, ScanProgressBar.Maximum);
        ScanProgressText.Text = $"{progress.Processed} / {progress.Total}";
        SetStatus($"Clearing review queue: {progress.Processed} of {progress.Total} processed, {progress.Restored} cleared.");
    }

    private void EndScanProgress()
    {
        _scanProgressActive = false;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressPanel.Visibility = Visibility.Collapsed;
        ProcessingProgressBar.IsIndeterminate = false;
        ProcessingProgressPanel.Visibility = Visibility.Collapsed;
    }

    private sealed record CandidatePreviewItem(
        ReviewCandidate Candidate,
        string MatchLabel,
        string Resolution,
        string Details,
        string ArchivePath);

    protected override void OnClosed(EventArgs e)
    {
        _previewService.Dispose();
        base.OnClosed(e);
    }
}
