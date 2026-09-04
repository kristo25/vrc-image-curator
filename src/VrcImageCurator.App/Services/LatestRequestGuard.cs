namespace VrcImageCurator.App.Services;

public sealed class LatestRequestGuard
{
    private long _generation;

    public long Begin() => Interlocked.Increment(ref _generation);

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;
}
