using System.IO;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

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
    public void AHoldingFolderInsideTheOldDataFolderIsRepointedAtTheNewOne()
    {
        // The bug this exists for. Moving the folder is only half the job: the holding root is
        // stored absolute, so it went on naming a folder that the move had just emptied, and
        // clearing local data refuses outright when that path sits outside the data folder.
        var settings = new AppSettings
        {
            HoldingRootPath = @"C:\Users\someone\AppData\Local\VrcImageCurator\Holding",
            OutputRootPath = @"K:\Pictures\VRChat Archive",
        };

        Assert.True(LocalDataMigration.NeedsRebase(settings, @"C:\Users\someone\AppData\Local\VrcImageCurator"));
        Assert.Equal(
            1,
            LocalDataMigration.RebasePaths(
                settings,
                @"C:\Users\someone\AppData\Local\VrcImageCurator",
                @"C:\Users\someone\AppData\Local\VrcPicSorter"));

        Assert.Equal(@"C:\Users\someone\AppData\Local\VrcPicSorter\Holding", settings.HoldingRootPath);
    }

    [Fact]
    public void FoldersOutsideTheOldDataFolderAreLeftAlone()
    {
        // Someone's archive on another drive has nothing to do with what this application is
        // called, and rewriting it would move their files out from under them.
        var settings = new AppSettings
        {
            HoldingRootPath = @"K:\Kissou Stuff\Holding",
            OutputRootPath = @"K:\Kissou Stuff\! VRChat Picture\Other Image From VRC",
        };
        settings.CategoryMappings.Add(new CategoryMapping
        {
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji",
            ArchivePath = @"K:\Kissou Stuff\! VRChat Picture\Other Image From VRC\Emoji",
        });

        Assert.False(LocalDataMigration.NeedsRebase(settings, @"C:\Users\someone\AppData\Local\VrcImageCurator"));
        Assert.Equal(
            0,
            LocalDataMigration.RebasePaths(
                settings,
                @"C:\Users\someone\AppData\Local\VrcImageCurator",
                @"C:\Users\someone\AppData\Local\VrcPicSorter"));

        Assert.Equal(@"K:\Kissou Stuff\Holding", settings.HoldingRootPath);
        Assert.Equal(@"C:\Users\someone\OneDrive\Images\VRChat\Emoji", settings.CategoryMappings[0].SourcePath);
    }

    [Fact]
    public void AnIsolatedProfileUnderTheOldFolderIsCarriedAcrossWholesale()
    {
        // --data-dir puts everything under one folder, so a profile that lived under the old data
        // folder has every one of its paths rewritten rather than just the holding root.
        const string previous = @"C:\Users\someone\AppData\Local\VrcImageCurator";
        const string current = @"C:\Users\someone\AppData\Local\VrcPicSorter";
        var settings = new AppSettings
        {
            HoldingRootPath = previous + @"\Holding",
            OutputRootPath = previous + @"\Archive",
        };
        settings.CategoryMappings.Add(new CategoryMapping
        {
            Category = VrcImageCategory.Prints,
            SourcePath = previous + @"\Profile\Prints",
            ArchivePath = previous + @"\Archive\Prints",
        });
        settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
        {
            Category = VrcImageCategory.Prints,
            ArchivePath = previous + @"\Old\Prints",
        });

        Assert.Equal(5, LocalDataMigration.RebasePaths(settings, previous, current));

        Assert.Equal(current + @"\Holding", settings.HoldingRootPath);
        Assert.Equal(current + @"\Archive", settings.OutputRootPath);
        Assert.Equal(current + @"\Profile\Prints", settings.CategoryMappings[0].SourcePath);
        Assert.Equal(current + @"\Archive\Prints", settings.CategoryMappings[0].ArchivePath);
        Assert.Equal(current + @"\Old\Prints", settings.LegacyArchiveMappings[0].ArchivePath);
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
