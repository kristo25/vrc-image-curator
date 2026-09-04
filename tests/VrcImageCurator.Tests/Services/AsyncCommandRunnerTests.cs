using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.Services;

public sealed class AsyncCommandRunnerTests
{
    [Fact]
    public async Task FailureIsReportedWithoutEscapingTheCommandBoundary()
    {
        var expected = new InvalidOperationException("scan failed");
        Exception? reported = null;

        await AsyncCommandRunner.RunAsync(
            () => Task.FromException(expected),
            exception =>
            {
                reported = exception;
                return Task.CompletedTask;
            });

        Assert.Same(expected, reported);
    }

    [Fact]
    public async Task SuccessfulCommandDoesNotReportAFailure()
    {
        var reports = 0;

        await AsyncCommandRunner.RunAsync(
            () => Task.CompletedTask,
            _ =>
            {
                reports++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task FailureReporterExceptionDoesNotEscapeTheBoundary()
    {
        await AsyncCommandRunner.RunAsync(
            () => Task.FromException(new InvalidOperationException("command failed")),
            _ => Task.FromException(new IOException("reporting failed")));
    }
}
