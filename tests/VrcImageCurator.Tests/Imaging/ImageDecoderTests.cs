using SixLabors.ImageSharp.PixelFormats;
using VrcImageCurator.Core.Imaging;

namespace VrcImageCurator.Tests.Imaging;

public sealed class ImageDecoderTests
{
    [Theory]
    [InlineData(FixtureFormat.Png, "image.png")]
    [InlineData(FixtureFormat.Gif, "image.gif")]
    [InlineData(FixtureFormat.Jpeg, "image.jpg")]
    [InlineData(FixtureFormat.Webp, "image.webp")]
    [InlineData(FixtureFormat.Bmp, "image.bmp")]
    internal async Task SupportedFormatsDecodeToCanonicalRgbaFrames(FixtureFormat format, string fileName)
    {
        using var directory = new TestDirectory();
        using var source = ImageFixtureFactory.CreatePattern(4);
        var path = await ImageFixtureFactory.SaveAsync(directory, fileName, source, format);

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(source.Width, result.Image!.Width);
        Assert.Equal(source.Height, result.Image.Height);
        Assert.Single(result.Image.Frames);
        Assert.Equal(source.Width * source.Height * 4, result.Image.Frames[0].RgbaPixels.Length);
    }

    [Fact]
    public async Task UnsupportedContentReturnsTypedFailure()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("not-an-image.temp");
        await File.WriteAllTextAsync(path, "not an image");

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImageDecodeFailureKind.UnsupportedFormat, result.Failure!.Kind);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task AnimatedGifPreservesOrderedFrameDelays()
    {
        using var directory = new TestDirectory();
        using var red = ImageFixtureFactory.CreateSolid(new Rgba32(255, 0, 0));
        using var green = ImageFixtureFactory.CreateSolid(new Rgba32(0, 255, 0));
        var path = await ImageFixtureFactory.SaveGifAsync(directory, "timed.gif", [red, green], [80, 230]);

        var result = await new ImageDecoder().DecodeAsync(path);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal([80, 230], result.Image!.Frames.Select(frame => frame.DelayMilliseconds));
    }

    [Theory]
    [InlineData(1, 1, ImageResourceLimits.MaximumFrames + 1)]
    [InlineData(ImageResourceLimits.MaximumWidth + 1, 1, 1)]
    [InlineData(4096, 4096, 9)]
    public void UnsafeDecodedImageSizesAreRejected(int width, int height, int frames)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => ImageResourceLimits.EnsureSafe(width, height, frames));

        Assert.Contains("resource limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
