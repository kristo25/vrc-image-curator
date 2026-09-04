namespace VrcImageCurator.App.Services;

public static class AsyncCommandRunner
{
    public static async Task RunAsync(Func<Task> command, Func<Exception, Task> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(reportFailure);

        try
        {
            await command().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await reportFailure(exception).ConfigureAwait(false);
            }
            catch (Exception reportingException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to report background exception: {reportingException}");
            }
        }
    }
}
