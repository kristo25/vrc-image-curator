using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.Services;

public sealed class LatestRequestGuardTests
{
    [Fact]
    public void BeginInvalidatesEarlierRequest()
    {
        var guard = new LatestRequestGuard();

        var earlier = guard.Begin();
        var latest = guard.Begin();

        Assert.False(guard.IsCurrent(earlier));
        Assert.True(guard.IsCurrent(latest));
    }

    [Fact]
    public void InvalidateRejectsCurrentRequest()
    {
        var guard = new LatestRequestGuard();
        var request = guard.Begin();

        guard.Invalidate();

        Assert.False(guard.IsCurrent(request));
    }
}
