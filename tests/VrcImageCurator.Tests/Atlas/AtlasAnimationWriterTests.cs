using VrcImageCurator.Core.Atlas;

namespace VrcImageCurator.Tests.Atlas;

public sealed class AtlasAnimationWriterTests
{
    [Theory]
    [InlineData(@"D:\Archive\Emoji\Animated\x.gif", true)]
    [InlineData(@"D:\Archive\Emoji\Animated\2026-06\x.gif", true)]
    [InlineData(@"D:\Archive\Emoji\animated\x.gif", true)]
    [InlineData(@"D:\Archive\Emoji\2026-06\x.png", false)]
    [InlineData(@"D:\Archive\Emoji\AnimatedThings\x.png", false)]
    [InlineData("", false)]
    public void TheAnimationFolderIsRecognisedSoTheIndexerCanSkipIt(string path, bool expected)
    {
        Assert.Equal(expected, AtlasAnimationWriter.IsAnimationFolder(path));
    }

    [Fact]
    public void AnimationsKeepTheSubfolderTheirSheetSitsIn()
    {
        var destination = AtlasAnimationWriter.BuildDestination(
            Path.Combine("D:", "Archive", "Emoji", "2026-06", "sheet_16frames_10fps.png"),
            Path.Combine("D:", "Archive", "Emoji"));

        Assert.Equal(
            Path.Combine("D:", "Archive", "Emoji", "Animated", "2026-06", "sheet_16frames_10fps.gif"),
            destination);
    }

    [Fact]
    public void ASheetDirectlyInTheArchiveRootAnimatesOneLevelDown()
    {
        var destination = AtlasAnimationWriter.BuildDestination(
            Path.Combine("D:", "Archive", "Emoji", "sheet_16frames_10fps.png"),
            Path.Combine("D:", "Archive", "Emoji"));

        Assert.Equal(
            Path.Combine("D:", "Archive", "Emoji", "Animated", "sheet_16frames_10fps.gif"),
            destination);
    }

    [Fact]
    public void ASheetOutsideTheArchiveRootAnimatesBesideItself()
    {
        var destination = AtlasAnimationWriter.BuildDestination(
            Path.Combine("D:", "Elsewhere", "sheet_16frames_10fps.png"),
            Path.Combine("D:", "Archive", "Emoji"));

        Assert.Equal(
            Path.Combine("D:", "Elsewhere", "Animated", "sheet_16frames_10fps.gif"),
            destination);
    }

    [Theory]
    [InlineData(@"D:\Archive\Emoji\Animated\x.gif", true)]
    [InlineData(@"D:\Incoming\2026-06\x.GIF", true)]
    [InlineData(@"D:\Archive\Emoji\x.png", false)]
    [InlineData(@"D:\Archive\Emoji\x.webp", false)]
    [InlineData("", false)]
    public void AnAnimationIsRecognisedWhoeverMadeIt(string path, bool expected)
    {
        Assert.Equal(expected, AtlasAnimationWriter.IsAnimation(path));
    }

    [Theory]
    // The same base name is the same emoji: VRChat puts the player, the emoji's own id and its
    // frame parameters in there, so nothing else can collide with it.
    [InlineData(@"D:\In\a_4frames_10fps.gif", @"D:\Archive\Emoji\a_4frames_10fps.png", true)]
    [InlineData(@"D:\In\A_4FRAMES_10FPS.gif", @"D:\Archive\Emoji\a_4frames_10fps.png", true)]
    [InlineData(@"D:\In\b_4frames_10fps.gif", @"D:\Archive\Emoji\a_4frames_10fps.png", false)]
    // A GIF is not the animation of another GIF, and a still is not the animation of anything.
    [InlineData(@"D:\In\a_4frames_10fps.gif", @"D:\Archive\Emoji\a_4frames_10fps.gif", false)]
    [InlineData(@"D:\In\a_4frames_10fps.png", @"D:\Archive\Emoji\a_4frames_10fps.png", false)]
    public void AnAnimationIsPairedWithItsSheetByName(string animation, string atlas, bool expected)
    {
        Assert.Equal(expected, AtlasAnimationWriter.IsAnimationOf(animation, atlas));
    }

    [Fact]
    public void AnAnimatedSheetIsFiledOneLevelBelowItsAnimation()
    {
        var reference = AtlasAnimationWriter.BuildReferenceDestination(
            Path.Combine("D:", "Archive", "Emoji", "sheet_16frames_10fps.png"),
            Path.Combine("D:", "Archive", "Emoji"));

        Assert.Equal(
            Path.Combine("D:", "Archive", "Emoji", "Animated", "Gif Ref", "sheet_16frames_10fps.png"),
            reference);
    }

    [Fact]
    public void AFiledSheetStillAnimatesToWhereItAlreadyDid()
    {
        // Asking a second time must answer the same place. Measuring from the sheet's new folder
        // without climbing back out would bury the GIF under Animated/Gif Ref/Animated, and every
        // re-export from the Animations tab would bury it one level deeper again.
        var filed = Path.Combine("D:", "Archive", "Emoji", "Animated", "Gif Ref", "sheet_16frames_10fps.png");
        var root = Path.Combine("D:", "Archive", "Emoji");

        Assert.Equal(
            Path.Combine("D:", "Archive", "Emoji", "Animated", "sheet_16frames_10fps.gif"),
            AtlasAnimationWriter.BuildDestination(filed, root));
        Assert.Equal(filed, AtlasAnimationWriter.BuildReferenceDestination(filed, root));
    }

    [Fact]
    public void AFiledSheetKeepsTheSubfolderItWasArchivedIn()
    {
        var filed = Path.Combine(
            "D:", "Archive", "Emoji", "Animated", "Gif Ref", "2026-06", "sheet_16frames_10fps.png");
        var root = Path.Combine("D:", "Archive", "Emoji");

        Assert.Equal(
            Path.Combine("D:", "Archive", "Emoji", "Animated", "2026-06", "sheet_16frames_10fps.gif"),
            AtlasAnimationWriter.BuildDestination(filed, root));
        Assert.Equal(filed, AtlasAnimationWriter.BuildReferenceDestination(filed, root));
    }

    [Fact]
    public void AFiledSheetOutsideTheArchiveAlsoStaysPut()
    {
        var filed = Path.Combine("D:", "Elsewhere", "Animated", "Gif Ref", "sheet_16frames_10fps.png");
        var root = Path.Combine("D:", "Archive", "Emoji");

        Assert.Equal(
            Path.Combine("D:", "Elsewhere", "Animated", "sheet_16frames_10fps.gif"),
            AtlasAnimationWriter.BuildDestination(filed, root));
        Assert.Equal(filed, AtlasAnimationWriter.BuildReferenceDestination(filed, root));
    }

    [Fact]
    public async Task AStillEmojiIsLeftAlone()
    {
        using var directory = new TestDirectory();
        var archive = directory.GetPath("archive");
        var still = Path.Combine(archive, "player_inv_id_shakeanimationStyle.png");
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(still, "not really an image");

        var result = await new AtlasAnimationWriter().TryWriteAsync(still, archive);

        Assert.False(result.Exported);
        Assert.Null(result.Warning);
        Assert.Empty(Directory.GetDirectories(archive));
    }

    [Fact]
    public async Task AnUnreadableSheetWarnsInsteadOfThrowing()
    {
        using var directory = new TestDirectory();
        var archive = directory.GetPath("archive");
        var broken = Path.Combine(archive, "player_inv_id_a_16frames_10fps_linearloopStyle.png");
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(broken, "not really an image");

        var result = await new AtlasAnimationWriter().TryWriteAsync(broken, archive);

        Assert.False(result.Exported);
        Assert.NotNull(result.Warning);
    }
}
