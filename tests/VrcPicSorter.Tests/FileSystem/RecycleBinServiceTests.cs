using VrcPicSorter.Core.FileSystem;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class RecycleBinServiceTests
{
    /// <summary>
    /// A drive set to "remove files immediately when deleted" still reports a successful recycle,
    /// so nothing downstream can tell the difference. Asking beforehand is the only thing standing
    /// between the automatic duplicate handling and a permanently destroyed picture.
    /// </summary>
    [Fact]
    public void ADriveThatDeletesImmediatelyCountsAsHavingNoRecycleBin()
    {
        using var directory = new TestDirectory();
        var asked = new List<string>();
        var service = new WindowsRecycleBinService(root =>
        {
            asked.Add(root);
            return true;
        });

        Assert.False(service.CanRecycle(directory.GetPath("image.png")));
        Assert.NotEmpty(asked);
    }

    [Fact]
    public void ADriveThatStillRecyclesIsAccepted()
    {
        using var directory = new TestDirectory();
        Assert.True(new WindowsRecycleBinService(_ => false).CanRecycle(directory.GetPath("image.png")));
    }

    [Fact]
    public async Task RecyclingRefusesRatherThanDeletingWhenTheBinIsOff()
    {
        using var directory = new TestDirectory();
        var file = directory.GetPath("image.png");
        await File.WriteAllTextAsync(file, "test");
        var service = new WindowsRecycleBinService(_ => true);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.RecycleAsync(file));

        // The caller is expected to fall back to asking; the file must still be there to ask about.
        Assert.True(File.Exists(file));
    }

    /// <summary>Left unasked, the answer is what it always was: the drive type alone.</summary>
    [Fact]
    public void AnUnansweredQuestionLeavesTheDriveAsItWas()
    {
        using var directory = new TestDirectory();
        Assert.True(new WindowsRecycleBinService().CanRecycle(directory.GetPath("image.png")));
    }
}
