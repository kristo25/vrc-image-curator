using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.Automation;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public void SecondInstanceSignalsOwningInstance()
    {
        var testScope = Guid.NewGuid().ToString("N");
        using var owner = new SingleInstanceService("VrcImageCurator.Tests", testScope);
        using var activated = new ManualResetEventSlim();
        Assert.True(owner.TryAcquireOwnership());
        owner.StartActivationListener(activated.Set);

        var contenderAcquired = true;
        var signalSucceeded = false;
        Exception? contenderError = null;
        var contenderThread = new Thread(
            () =>
            {
                try
                {
                    using var contender = new SingleInstanceService("VrcImageCurator.Tests", testScope);
                    contenderAcquired = contender.TryAcquireOwnership();
                    signalSucceeded = contender.SignalExistingInstance();
                }
                catch (Exception exception)
                {
                    contenderError = exception;
                }
            });

        contenderThread.Start();
        Assert.True(contenderThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(contenderError);
        Assert.False(contenderAcquired);
        Assert.True(signalSucceeded);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AbandonedMutexAllowsCleanOwnershipRecovery()
    {
        var testScope = Guid.NewGuid().ToString("N");
        SingleInstanceService? abandoned = null;
        Exception? ownerError = null;
        var ownerAcquired = false;
        var ownerThread = new Thread(
            () =>
            {
                try
                {
                    abandoned = new SingleInstanceService("VrcImageCurator.Tests", testScope);
                    ownerAcquired = abandoned.TryAcquireOwnership();
                }
                catch (Exception exception)
                {
                    ownerError = exception;
                }
            });

        ownerThread.Start();
        Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerError);
        Assert.True(ownerAcquired);
        Assert.NotNull(abandoned);

        using (var recovered = new SingleInstanceService("VrcImageCurator.Tests", testScope))
        {
            Assert.True(recovered.TryAcquireOwnership());
        }

        abandoned.Dispose();
    }

    [Fact]
    public void ActivationListenerContinuesAfterCallbackFailure()
    {
        var testScope = Guid.NewGuid().ToString("N");
        using var owner = new SingleInstanceService("VrcImageCurator.Tests", testScope);
        using var activatedTwice = new ManualResetEventSlim();
        var calls = 0;
        Assert.True(owner.TryAcquireOwnership());
        owner.StartActivationListener(
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("simulated activation failure");
                }

                activatedTwice.Set();
            });

        Assert.True(owner.SignalExistingInstance());
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)));
        Assert.True(owner.SignalExistingInstance());

        Assert.True(activatedTwice.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
