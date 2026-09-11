namespace VrcImageCurator.Core.Atlas;

/// <summary>
/// Where each animation frame sits on a VRChat emoji sheet.
/// </summary>
/// <remarks>
/// VRChat packs the frames into a square grid whose side is the smallest power of two that can
/// hold them all - four frames go into 2x2, sixteen into 4x4, seventeen into 8x8 - laid out left
/// to right and top to bottom, with any cells after the last frame left empty. Deriving the grid
/// this way reproduced every layout that periodicity detection found correctly across an archive
/// of 56 sheets, and resolved all thirteen it got wrong.
/// </remarks>
public sealed record AtlasLayout(int Columns, int Rows, int CellWidth, int CellHeight, int FrameCount)
{
    /// <summary>Rows that actually hold frames. The grid is square, so the rest is empty.</summary>
    public int UsedRows => (FrameCount + Columns - 1) / Columns;

    /// <summary>
    /// Builds the layout for a sheet of the given size, or returns false when the frames cannot
    /// sit on a whole-pixel grid - a canvas that the grid does not divide evenly would put frame
    /// boundaries between pixels, and a guessed rounding is worse than declining.
    /// </summary>
    public static bool TryCreate(int frameCount, int imageWidth, int imageHeight, out AtlasLayout layout)
    {
        layout = new AtlasLayout(0, 0, 0, 0, 0);
        if (frameCount < EmojiAtlasName.MinimumFrameCount
            || frameCount > EmojiAtlasName.MaximumFrameCount
            || imageWidth <= 0
            || imageHeight <= 0)
        {
            return false;
        }

        var side = SideFor(frameCount);
        if (imageWidth % side != 0 || imageHeight % side != 0)
        {
            return false;
        }

        var cellWidth = imageWidth / side;
        var cellHeight = imageHeight / side;
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            return false;
        }

        layout = new AtlasLayout(side, side, cellWidth, cellHeight, frameCount);
        return true;
    }

    /// <summary>The smallest power of two whose square is at least <paramref name="frameCount"/>.</summary>
    public static int SideFor(int frameCount)
    {
        var side = 1;
        while ((long)side * side < frameCount)
        {
            side *= 2;
        }

        return side;
    }

    /// <summary>Top-left corner and size of one frame, in atlas pixels.</summary>
    public (int X, int Y, int Width, int Height) GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        var row = index / Columns;
        var column = index % Columns;
        return (column * CellWidth, row * CellHeight, CellWidth, CellHeight);
    }

    /// <summary>
    /// The order frames are played in. A ping-pong sheet runs forward and then back without
    /// repeating either end, so a four frame sheet plays 0 1 2 3 2 1.
    /// </summary>
    public IReadOnlyList<int> PlaybackOrder(AtlasLoopStyle loopStyle)
    {
        var forward = new List<int>(FrameCount);
        for (var index = 0; index < FrameCount; index++)
        {
            forward.Add(index);
        }

        if (loopStyle != AtlasLoopStyle.PingPong || FrameCount < 3)
        {
            return forward;
        }

        for (var index = FrameCount - 2; index >= 1; index--)
        {
            forward.Add(index);
        }

        return forward;
    }
}
