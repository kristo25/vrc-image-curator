using VrcImageCurator.Core.Models;
using VrcImageCurator.Core.Storage;

namespace VrcImageCurator.Tests.Storage;

public sealed class OperationJournalTests
{
    public static TheoryData<JournalPhase, JournalPathState, JournalPathState, JournalReconciliationAction>
        MoveReconciliationCases =>
        new()
        {
            { JournalPhase.IntentRecorded, JournalPathState.ExpectedFile, JournalPathState.Missing, JournalReconciliationAction.RetrySideEffect },
            { JournalPhase.SideEffectStarted, JournalPathState.ExpectedFile, JournalPathState.Missing, JournalReconciliationAction.RetrySideEffect },
            { JournalPhase.IntentRecorded, JournalPathState.Missing, JournalPathState.ExpectedFile, JournalReconciliationAction.NeedsAttention },
            { JournalPhase.SideEffectStarted, JournalPathState.Missing, JournalPathState.ExpectedFile, JournalReconciliationAction.CommitState },
            { JournalPhase.SideEffectApplied, JournalPathState.Missing, JournalPathState.ExpectedFile, JournalReconciliationAction.CommitState },
            { JournalPhase.StateCommitted, JournalPathState.Missing, JournalPathState.ExpectedFile, JournalReconciliationAction.MarkCompleted },
            { JournalPhase.Completed, JournalPathState.Missing, JournalPathState.ExpectedFile, JournalReconciliationAction.None },
        };

    public static TheoryData<JournalPhase, JournalPathState, JournalReconciliationAction>
        RecycleReconciliationCases =>
        new()
        {
            { JournalPhase.IntentRecorded, JournalPathState.ExpectedFile, JournalReconciliationAction.RetrySideEffect },
            { JournalPhase.SideEffectStarted, JournalPathState.ExpectedFile, JournalReconciliationAction.RetrySideEffect },
            { JournalPhase.IntentRecorded, JournalPathState.Missing, JournalReconciliationAction.NeedsAttention },
            { JournalPhase.SideEffectStarted, JournalPathState.Missing, JournalReconciliationAction.CommitState },
            { JournalPhase.SideEffectApplied, JournalPathState.Missing, JournalReconciliationAction.CommitState },
            { JournalPhase.StateCommitted, JournalPathState.Missing, JournalReconciliationAction.MarkCompleted },
            { JournalPhase.Completed, JournalPathState.Missing, JournalReconciliationAction.None },
        };

    [Fact]
    public async Task IntentAndPhaseChangesAreDurable()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var journal = new OperationJournal(store);
        var entry = await journal.RecordIntentAsync(CreateEntry(JournalOperationType.Move));

        await journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted);
        await journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectApplied);
        await journal.AdvanceAsync(entry.Id, JournalPhase.StateCommitted);
        await journal.AdvanceAsync(entry.Id, JournalPhase.Completed);

        var reloaded = await store.LoadAsync();
        Assert.Empty(reloaded.OperationJournal);
    }

    [Theory]
    [MemberData(nameof(MoveReconciliationCases))]
    public void MoveReconciliationSelectsOnlyIdempotentNextAction(
        JournalPhase phase,
        JournalPathState source,
        JournalPathState destination,
        JournalReconciliationAction expected)
    {
        var entry = CreateEntry(JournalOperationType.Move);
        entry.Phase = phase;

        var decision = OperationJournal.Reconcile(
            entry,
            new JournalFileObservation(source, destination));

        Assert.Equal(expected, decision.Action);
    }

    [Theory]
    [MemberData(nameof(RecycleReconciliationCases))]
    public void RecycleReconciliationDoesNotRepeatAnAppliedDeletion(
        JournalPhase phase,
        JournalPathState source,
        JournalReconciliationAction expected)
    {
        var entry = CreateEntry(JournalOperationType.Recycle);
        entry.Phase = phase;

        var decision = OperationJournal.Reconcile(entry, new JournalFileObservation(source));

        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public void AmbiguousMoveStateRequiresAttention()
    {
        var entry = CreateEntry(JournalOperationType.Move);

        var decision = OperationJournal.Reconcile(
            entry,
            new JournalFileObservation(
                JournalPathState.ExpectedFile,
                JournalPathState.ExpectedFile));

        Assert.Equal(JournalReconciliationAction.NeedsAttention, decision.Action);
    }

    [Fact]
    public void NeedsAttentionMoveCanRetryWhenOnlyExpectedSourceExists()
    {
        var entry = CreateEntry(JournalOperationType.Move);
        entry.Phase = JournalPhase.NeedsAttention;

        var decision = OperationJournal.Reconcile(
            entry,
            new JournalFileObservation(JournalPathState.ExpectedFile, JournalPathState.Missing));

        Assert.Equal(JournalReconciliationAction.RetrySideEffect, decision.Action);
    }

    private static JournalEntry CreateEntry(JournalOperationType operationType) =>
        new()
        {
            Id = Guid.NewGuid(),
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = operationType,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Source\image.png",
            DestinationPath = operationType == JournalOperationType.Move
                ? @"D:\Destination\image.png"
                : null,
            ExpectedSource = new ExpectedFileIdentity
            {
                Fingerprint = "fingerprint",
                FileSize = 321,
                LastWriteUtc = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            },
        };

    private static JsonStateStore CreateStore(TestDirectory directory) =>
        new(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(
                directory.GetPath("profile"),
                directory.GetPath("local-app-data")));
}
