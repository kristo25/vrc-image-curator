using SixLabors.ImageSharp;
using VrcImageCurator.App.Services;
using VrcImageCurator.Tests.Imaging;

namespace VrcImageCurator.Tests.Services;

public sealed class PreviewServiceTests
{
    [Fact]
    public async Task StaticImageLoadsWithoutUsingTheWpfUriCache()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("preview.png");
        using var image = ImageFixtureFactory.CreatePattern(32);
        await image.SaveAsPngAsync(path);
        using var preview = new PreviewService();

        var bitmap = preview.Load(path, decodeWidth: 32);

        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.PixelWidth);
        Assert.True(bitmap.IsFrozen);
    }
}
