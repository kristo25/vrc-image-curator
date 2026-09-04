using VrcImageCurator.Core.Models;

namespace VrcImageCurator.Core.Storage;

public static class AppStateCompactor
{
    public const int MaximumHistoryEntries = 1_000;

    public static bool Compact(AppStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changed = state.ReviewQueue.RemoveAll(item => item.Status == ReviewStatus.Resolved) > 0;
        changed |= state.OperationJournal.RemoveAll(item => item.Phase == JournalPhase.Completed) > 0;

        if (state.History.Count > MaximumHistoryEntries)
        {
            state.History = state.History
                .OrderByDescending(item => item.OccurredUtc)
                .Take(MaximumHistoryEntries)
                .OrderBy(item => item.OccurredUtc)
                .ToList();
            changed = true;
        }

        return changed;
    }
}
