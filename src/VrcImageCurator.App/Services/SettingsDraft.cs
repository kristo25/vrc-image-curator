using System.IO;
using VrcImageCurator.Core.Models;

namespace VrcImageCurator.App.Services;

public sealed record CategorySettingsDraft(
    VrcImageCategory Category,
    string SourcePath,
    bool IsEnabled);

public sealed record SettingsDraft(
    IReadOnlyList<CategorySettingsDraft> Categories,
    string OutputRootPath,
    SimilarityProfile SimilarityProfile,
    bool StartWithWindows,
    bool BringReviewForwardWhenHeld)
{
    public void ApplyTo(AppStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var outputRoot = Path.GetFullPath(OutputRootPath);
        var rootChanged = !string.Equals(
            state.Settings.OutputRootPath,
            outputRoot,
            StringComparison.OrdinalIgnoreCase);

        if (rootChanged)
        {
            foreach (var mapping in state.Settings.CategoryMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ArchivePath)
                    || state.Settings.LegacyArchiveMappings.Any(
                        legacy => legacy.Category == mapping.Category
                            && string.Equals(legacy.ArchivePath, mapping.ArchivePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
                {
                    Category = mapping.Category,
                    ArchivePath = mapping.ArchivePath,
                });
            }
        }

        foreach (var draft in Categories)
        {
            var mapping = state.Settings.CategoryMappings.Single(item => item.Category == draft.Category);
            var normalizedSource = string.IsNullOrWhiteSpace(draft.SourcePath)
                ? string.Empty
                : Path.GetFullPath(draft.SourcePath);
            var sourceChanged = !string.Equals(
                mapping.SourcePath,
                normalizedSource,
                StringComparison.OrdinalIgnoreCase);
            mapping.SourcePath = normalizedSource;
            mapping.ArchivePath = Path.Combine(outputRoot, draft.Category.ToString());
            mapping.IsEnabled = draft.IsEnabled;

            if (sourceChanged || rootChanged)
            {
                var index = state.ArchiveIndex.Categories.Single(item => item.Category == draft.Category);
                index.Status = IndexStatus.Stale;
                index.LastError = sourceChanged && rootChanged
                    ? "Source folder and output root changed."
                    : sourceChanged
                        ? "Source folder changed."
                        : "Output root changed.";
            }
        }

        state.Settings.OutputRootPath = outputRoot;
        state.Settings.OutputRootConfirmed = true;
        state.Settings.OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder;
        state.Settings.SimilarityProfile = SimilarityProfile;
        state.Settings.Automation.WatchWhileOpen = false;
        state.Settings.Automation.StartWithWindows = StartWithWindows;
        state.Settings.BringReviewForwardWhenHeld = BringReviewForwardWhenHeld;
    }
}
