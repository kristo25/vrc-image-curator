using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using VrcImageCurator.Core.Atlas;

namespace VrcImageCurator.Tests.Atlas;

public sealed class AtlasGifExporterTests
{
    [Theory]
    [InlineData(10, 10, 10)]
    [InlineData(25, 4, 25)]
    [InlineData(31, 3, 33)]
    // GIF cannot express these, and a viewer that sees a delay under two hundredths silently
    // plays it at ten frames a second. Clamping keeps the file honest about its own rate.
    [InlineData(60, 2, 50)]
    [InlineData(64, 2, 50)]
    [InlineData(120, 2, 50)]
    public void TheFrameDelayIsWhatAViewerWillActuallyHonour(int fps, int delay, int effective)
    {
        Assert.Equal(delay, AtlasGifExporter.FrameDelayFor(fps));
        Assert.Equal(effective, 100 / AtlasGifExporter.FrameDelayFor(fps));
    }

    [Fact]
    public async Task ExportsOneFramePerCellAtTheRateTheNameAsksFor()
    {
        using var directory = new TestDirectory();
        var source = WriteSheet(directory, "x_a_16frames_10fps_linearloopStyle.png", frames: 16, canvas: 512);
        var destination = directory.GetPath("out", "emoji.gif");

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            destination,
            Parse("x_a_16frames_10fps_linearloopStyle.png"));

        Assert.Equal(16, result.FrameCount);
        Assert.Equal(10, result.FrameDelayCentiseconds);
        Assert.Equal(10, result.EffectiveFramesPerSecond);

        using var gif = await Image.LoadAsync<Rgba32>(destination);
        Assert.Equal(16, gif.Frames.Count);
        Assert.Equal(128, gif.Width);
        Assert.Equal(128, gif.Height);
        Assert.Equal(0, (int)gif.Metadata.GetGifMetadata().RepeatCount);
        Assert.All(gif.Frames, frame => Assert.Equal(10, frame.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public async Task PingPongDoublesBackWithoutRepeatingEitherEnd()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_pingpongloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);

        var result = await new AtlasGifExporter().ExportAsync(
            source,
            directory.GetPath("out", "pingpong.gif"),
            Parse(name));

        Assert.Equal(6, result.FrameCount);
    }

    [Fact]
    public async Task TheSheetItselfIsNeverTouched()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var before = await File.ReadAllBytesAsync(source);

        await new AtlasGifExporter().ExportAsync(source, directory.GetPath("out", "a.gif"), Parse(name));

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ExportingTwiceLeavesNoTemporaryFileBehind()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_4frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 4, canvas: 128);
        var destination = directory.GetPath("out", "a.gif");
        var exporter = new AtlasGifExporter();

        await exporter.ExportAsync(source, destination, Parse(name));
        await exporter.ExportAsync(source, destination, Parse(name));

        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public async Task RefusesASheetTheFramesCannotSitOnEvenly()
    {
        using var directory = new TestDirectory();
        const string name = "x_a_20frames_10fps_linearloopStyle.png";
        var source = WriteSheet(directory, name, frames: 20, canvas: 1020);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AtlasGifExporter().ExportAsync(
                source,
                directory.GetPath("out", "a.gif"),
                Parse(name)));
    }

    private static EmojiAtlasName Parse(string name)
    {
        Assert.True(EmojiAtlasName.TryParse(name, out var parsed));
        return parsed;
    }

    /// <summary>A square sheet whose cells each hold one distinct solid colour.</summary>
    private static string WriteSheet(TestDirectory directory, string name, int frames, int canvas)
    {
        var path = directory.GetPath("sheets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var side = AtlasLayout.SideFor(frames);
        var cell = canvas / side;
        using var image = new Image<Rgba32>(canvas, canvas);
        for (var index = 0; index < frames; index++)
        {
            var row = index / side;
            var column = index % side;
            var colour = new Rgba32((byte)(20 + (index * 11 % 220)), (byte)(40 + (index * 37 % 200)), 90, 255);
            image.ProcessPixelRows(accessor =>
            {
                for (var y = row * cell; y < (row + 1) * cell; y++)
                {
                    var span = accessor.GetRowSpan(y);
                    for (var x = column * cell; x < (column + 1) * cell; x++)
                    {
                        span[x] = colour;
                    }
                }
            });
        }

        image.SaveAsPng(path);
        return path;
    }
}
