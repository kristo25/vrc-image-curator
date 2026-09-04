using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VrcImageCurator.Core.Imaging;

namespace VrcImageCurator.Tests.Imaging;

internal enum FixtureFormat
{
    Png,
    Gif,
    Jpeg,
    Webp,
    Bmp,
}

internal static class ImageFixtureFactory
{
    public static Image<Rgba32> CreatePattern(int seed, int width = 64, int height = 48)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var cell = ((x / 8) + ((y / 8) * 3) + seed) % 7;
                    var diagonal = (x + (y * (seed % 5 + 1))) % 19;
                    row[x] = new Rgba32(
                        (byte)((seed * 37 + x * 3 + cell * 29) % 256),
                        (byte)((seed * 71 + y * 5 + diagonal * 11) % 256),
                        (byte)((seed * 19 + x * 2 + y * 3 + cell * 41) % 256),
                        255);
                }
            }
        });
        return image;
    }

    public static Image<Rgba32> CreateSolid(Rgba32 color, int width = 24, int height = 16) =>
        new(width, height, color);

    public static async Task<string> SaveAsync(
        TestDirectory directory,
        string fileName,
        Image<Rgba32> image,
        FixtureFormat format,
        bool addMetadata = false)
    {
        var path = directory.GetPath(fileName);
        using var copy = image.Clone();
        if (addMetadata)
        {
            copy.Metadata.ExifProfile = new ExifProfile();
            copy.Metadata.ExifProfile.SetValue(ExifTag.ImageDescription, $"metadata-{fileName}");
            copy.Metadata.ExifProfile.SetValue(ExifTag.ColorSpace, (ushort)1);
        }

        await copy.SaveAsync(path, Encoder(format));
        return path;
    }

    public static async Task<string> SaveOrientedPngAsync(
        TestDirectory directory,
        string fileName,
        Image<Rgba32> displayedImage)
    {
        using var storedImage = displayedImage.Clone(context => context.Rotate(RotateMode.Rotate270));
        storedImage.Metadata.ExifProfile = new ExifProfile();
        storedImage.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        return await SaveAsync(directory, fileName, storedImage, FixtureFormat.Png);
    }

    public static async Task<string> SaveGifAsync(
        TestDirectory directory,
        string fileName,
        IReadOnlyList<Image<Rgba32>> frames,
        IReadOnlyList<int> delaysMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);
        ArgumentOutOfRangeException.ThrowIfNotEqual(frames.Count, delaysMilliseconds.Count);

        using var animation = frames[0].Clone();
        animation.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delaysMilliseconds[0] / 10;
        for (var index = 1; index < frames.Count; index++)
        {
            animation.Frames.AddFrame(frames[index].Frames.RootFrame);
            animation.Frames[index].Metadata.GetGifMetadata().FrameDelay = delaysMilliseconds[index] / 10;
        }

        var path = directory.GetPath(fileName);
        await animation.SaveAsync(path, new GifEncoder());
        return path;
    }

    public static DecodedImage ToDecodedImage(Image<Rgba32> image)
    {
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        return new DecodedImage(
            image.Width,
            image.Height,
            "fixture",
            [new DecodedImageFrame(pixels, 0)]);
    }

    /// <summary>
    /// A visibly identical but not byte-identical copy: close enough to be proposed as a review
    /// candidate, different enough that it is never an exact match.
    /// </summary>
    public static Image<Rgba32> CreateNearDuplicate(Image<Rgba32> source)
    {
        var variant = source.Clone();
        variant.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    row[x] = new Rgba32(
                        (byte)Math.Min(255, pixel.R + 5),
                        (byte)Math.Max(0, pixel.G - 3),
                        (byte)Math.Min(255, pixel.B + 4),
                        pixel.A);
                }
            }
        });
        return variant;
    }

    public static IReadOnlyList<Image<Rgba32>> CreatePerceptualVariants(Image<Rgba32> source)
    {
        var resized = source.Clone(context => context.Resize(source.Width + 19, source.Height + 13));
        var lightlyRecolored = CreateNearDuplicate(source);
        var cropped = source.Clone(context => context
            .Crop(new Rectangle(2, 2, source.Width - 4, source.Height - 4))
            .Resize(source.Width, source.Height));
        var softened = source.Clone(context => context.GaussianBlur(0.65f));
        var recompressed = RoundTripJpeg(source, quality: 82);

        return [resized, lightlyRecolored, cropped, softened, recompressed];
    }

    private static Image<Rgba32> RoundTripJpeg(Image<Rgba32> source, int quality)
    {
        using var stream = new MemoryStream();
        source.Save(stream, new JpegEncoder { Quality = quality });
        stream.Position = 0;
        return Image.Load<Rgba32>(stream);
    }

    private static IImageEncoder Encoder(FixtureFormat format) => format switch
    {
        FixtureFormat.Png => new PngEncoder(),
        FixtureFormat.Gif => new GifEncoder(),
        FixtureFormat.Jpeg => new JpegEncoder { Quality = 90 },
        FixtureFormat.Webp => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless },
        FixtureFormat.Bmp => new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 },
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
