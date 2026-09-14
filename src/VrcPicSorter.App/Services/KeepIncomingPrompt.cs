namespace VrcPicSorter.App.Services;

/// <summary>
/// Remembers that a person has already agreed to keep an incoming image over a review's archived
/// matches, so a review holding several matches asks once instead of once per match.
/// </summary>
/// <remarks>
/// Keeping the incoming image settles one match at a time, because each match is its own archived
/// file to dispose of. Asking again for every one of them turns a single decision a person has
/// already made into a row of identical dialogs.
/// <para>
/// The agreement is remembered per review and per outcome. A match on a drive with no Recycle Bin
/// is moved to the Replaced folder rather than recycled, which is a different thing to have agreed
/// to, so a match like that asks again even part-way through a review that has already been agreed.
/// </para>
/// </remarks>
public sealed class KeepIncomingPrompt
{
    private Guid _reviewId;
    private bool _canRecycle;
    private bool _agreed;

    /// <summary>True when this match still has to be put to the person.</summary>
    public bool MustAsk(Guid reviewId, bool canRecycle) =>
        !_agreed || _reviewId != reviewId || _canRecycle != canRecycle;

    /// <summary>Records the agreement, so the rest of this review's matches do not ask again.</summary>
    public void Agreed(Guid reviewId, bool canRecycle)
    {
        _reviewId = reviewId;
        _canRecycle = canRecycle;
        _agreed = true;
    }

    /// <summary>
    /// Drops the agreement once the review it belonged to is settled, so nothing carries over to
    /// whatever a person looks at next.
    /// </summary>
    public void Forget() => _agreed = false;
}
