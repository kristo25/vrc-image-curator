using VrcImageCurator.Core.Atlas;

namespace VrcImageCurator.Tests.Atlas;

public sealed class EmojiAtlasNameTests
{
    [Theory]
    [InlineData("Player_inv_id_stopanimationStyle_64frames_31fps_linearloopStyle.png", 64, 31, AtlasLoopStyle.Linear)]
    [InlineData("Player_inv_id_beesanimationStyle_62frames_38fps_pingpongloopStyle.png", 62, 38, AtlasLoopStyle.PingPong)]
    [InlineData("Player_inv_id_splashanimationStyle_13frames_10fps_linearloopStyle.png", 13, 10, AtlasLoopStyle.Linear)]
    [InlineData(@"C:\Archive\Emoji\2026-06\x_ideaanimationStyle_7frames_17fps_linearloopStyle.png", 7, 17, AtlasLoopStyle.Linear)]
    public void ReadsTheAnimationFromTheName(string name, int frames, int fps, AtlasLoopStyle loop)
    {
        Assert.True(EmojiAtlasName.TryParse(name, out var parsed));
        Assert.Equal(frames, parsed.FrameCount);
        Assert.Equal(fps, parsed.FramesPerSecond);
        Assert.Equal(loop, parsed.LoopStyle);
    }

    [Theory]
    // A still emoji: an animation style, but no frame count. This is how most of an archive looks,
    // and it is the test for whether a file is a sheet at all.
    [InlineData("Player_inv_id_shakeanimationStyle.png")]
    [InlineData("Player_inv_id_moneyanimationStyle.png")]
    [InlineData("holiday-photo.png")]
    // The exported animation carries the same name as the sheet it came from, so only the
    // extension separates them. Listing one as a sheet would offer to slice an animation.
    [InlineData("Player_inv_id_stopanimationStyle_64frames_31fps_linearloopStyle.gif")]
    [InlineData("Player_inv_id_stopanimationStyle_64frames_31fps_linearloopStyle.mp4")]
    [InlineData("Player_inv_id_stopanimationStyle_64frames_31fps_linearloopStyle")]
    [InlineData("")]
    [InlineData(null)]
    // A single frame is a still image, not an animation.
    [InlineData("x_stopanimationStyle_1frames_10fps_linearloopStyle.png")]
    // Values a malformed or hostile name could carry.
    [InlineData("x_stopanimationStyle_0frames_10fps_linearloopStyle.png")]
    [InlineData("x_stopanimationStyle_9999frames_10fps_linearloopStyle.png")]
    [InlineData("x_stopanimationStyle_16frames_0fps_linearloopStyle.png")]
    [InlineData("x_stopanimationStyle_16frames_999fps_linearloopStyle.png")]
    public void RejectsAnythingThatIsNotASheet(string? name)
    {
        Assert.False(EmojiAtlasName.TryParse(name, out _));
    }

    [Fact]
    public void AnUnknownLoopStyleIsTreatedAsLinear()
    {
        Assert.True(EmojiAtlasName.TryParse("x_a_16frames_10fps_spiralloopStyle.png", out var parsed));
        Assert.Equal(AtlasLoopStyle.Linear, parsed.LoopStyle);
    }

    [Fact]
    public void TheLoopStyleIsOptional()
    {
        Assert.True(EmojiAtlasName.TryParse("x_a_16frames_10fps.png", out var parsed));
        Assert.Equal(16, parsed.FrameCount);
        Assert.Equal(AtlasLoopStyle.Linear, parsed.LoopStyle);
    }
}
