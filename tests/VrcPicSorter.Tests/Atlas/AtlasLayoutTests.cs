using VrcPicSorter.Core.Atlas;

namespace VrcPicSorter.Tests.Atlas;

public sealed class AtlasLayoutTests
{
    [Theory]
    // The grid side is the smallest power of two that can hold the frames. These pairings are the
    // ones observed across an archive of 56 sheets.
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(20, 8)]
    [InlineData(64, 8)]
    [InlineData(65, 16)]
    public void TheGridSideIsTheSmallestPowerOfTwoThatFits(int frames, int expectedSide)
    {
        Assert.Equal(expectedSide, AtlasLayout.SideFor(frames));
    }

    [Fact]
    public void FramesAreLaidOutLeftToRightThenTopToBottom()
    {
        Assert.True(AtlasLayout.TryCreate(16, 1024, 1024, out var layout));
        Assert.Equal(4, layout.Columns);
        Assert.Equal(4, layout.Rows);
        Assert.Equal(256, layout.CellWidth);
        Assert.Equal(256, layout.CellHeight);
        Assert.Equal((0, 0, 256, 256), layout.GetFrame(0));
        Assert.Equal((768, 0, 256, 256), layout.GetFrame(3));
        Assert.Equal((0, 256, 256, 256), layout.GetFrame(4));
        Assert.Equal((768, 768, 256, 256), layout.GetFrame(15));
    }

    [Fact]
    public void ASheetThatDoesNotFillItsGridLeavesTheRestEmpty()
    {
        Assert.True(AtlasLayout.TryCreate(20, 1024, 1024, out var layout));
        Assert.Equal(8, layout.Columns);
        Assert.Equal(8, layout.Rows);
        Assert.Equal(3, layout.UsedRows);
        Assert.Equal(128, layout.CellWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetFrame(20));
    }

    [Theory]
    // A grid that does not divide the canvas would put frame edges between pixels. Declining is
    // better than guessing a rounding.
    [InlineData(20, 1020, 1020)]
    [InlineData(5, 1022, 1024)]
    [InlineData(16, 0, 1024)]
    [InlineData(1, 1024, 1024)]
    public void RefusesALayoutThatWouldNotLandOnWholePixels(int frames, int width, int height)
    {
        Assert.False(AtlasLayout.TryCreate(frames, width, height, out _));
    }

    [Fact]
    public void PingPongPlaysForwardThenBackWithoutRepeatingEitherEnd()
    {
        Assert.True(AtlasLayout.TryCreate(4, 1024, 1024, out var layout));
        Assert.Equal(new[] { 0, 1, 2, 3, 2, 1 }, layout.PlaybackOrder(AtlasLoopStyle.PingPong));
        Assert.Equal(new[] { 0, 1, 2, 3 }, layout.PlaybackOrder(AtlasLoopStyle.Linear));
    }

    [Fact]
    public void PingPongOfTwoFramesIsJustTheTwoFrames()
    {
        Assert.True(AtlasLayout.TryCreate(2, 1024, 1024, out var layout));
        Assert.Equal(new[] { 0, 1 }, layout.PlaybackOrder(AtlasLoopStyle.PingPong));
    }
}
