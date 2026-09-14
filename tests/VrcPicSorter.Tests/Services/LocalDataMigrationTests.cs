using System.IO;
using VrcPicSorter.App.Services;

namespace VrcPicSorter.Tests.Services;

public sealed class LocalDataMigrationTests
{
    [Fact]
    public void TheOldFolderIsCarriedOverWhenThereIsNoNewOne()
    {
        using var directory = new TestDirectory();
        var previous = directory.GetPath("VrcImageCurator");
        var current = directory.GetPath("VrcPicSorter");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "state.json"), "{\"SchemaVersion\":5}");

        Assert.True(LocalDataMigration.CarryOver(previous, current));

        Assert.False(Directory.Exists(previous));
        Assert.Equal("{\"SchemaVersion\":5}", File.ReadAllText(Path.Combine(current, "state.json")));
    }

    [Fact]
    public void AnExistingNewFolderIsNeverOverwritten()
    {
        // Both present means the application has already run under the new name. Merging would
        // mean choosing between two state documents, and the newer name's is the live one.
        using var directory = new TestDirectory();
        var previous = directory.GetPath("VrcImageCurator");
        var current = directory.GetPath("VrcPicSorter");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(previous, "state.json"), "old");
        File.WriteAllText(Path.Combine(current, "state.json"), "live");

        Assert.False(LocalDataMigration.CarryOver(previous, current));

        Assert.Equal("live", File.ReadAllText(Path.Combine(current, "state.json")));
        Assert.True(Directory.Exists(previous));
    }

    [Fact]
    public void NothingToCarryIsNotAFailure()
    {
        using var directory = new TestDirectory();

        Assert.False(LocalDataMigration.CarryOver(
            directory.GetPath("VrcImageCurator"),
            directory.GetPath("VrcPicSorter")));
    }

    [Fact]
    public void AFreshInstallIsLeftWithNothingToDo()
    {
        // The overwhelmingly common case once the rename is behind everyone: no old folder, and
        // the new one created by the state store a moment later.
        using var directory = new TestDirectory();
        var current = directory.GetPath("VrcPicSorter");

        Assert.False(LocalDataMigration.CarryOver(directory.GetPath("VrcImageCurator"), current));

        Assert.False(Directory.Exists(current));
    }
}
