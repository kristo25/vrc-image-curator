using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VrcImageCurator.Core.Imaging;

public enum ImageDecodeFailureKind
{
    FileNotFound,
    UnsupportedFormat,
    InvalidContent,
    AccessDenied,
    InputOutput,
}

public sealed record ImageDecodeFailure(
    ImageDecodeFailureKind Kind,
    string Message,
    string? SourceName = null);

public sealed record ImageDecodeResult(DecodedImage? Image, ImageDecodeFailure? Failure)
{
    public bool IsSuccess => Image is not null && Failure is null;

    internal static ImageDecodeResult Success(DecodedImage image) => new(image, null);

    internal static ImageDecodeResult Failed(
        ImageDecodeFailureKind kind,
        string message,
        string? sourceName = null) =>
        new(null, new ImageDecodeFailure(kind, message, sourceName));
}

public sealed class ImageDecoder
{
    private static readonly HashSet<string> SupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "PNG", "GIF", "JPEG", "WEBP", "BMP" };

    public async Task<ImageDecodeResult> DecodeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.FileNotFound,
                "The image file does not exist.",
                path);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await DecodeAsync(stream, path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.AccessDenied, exception.Message, path);
        }
        catch (IOException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InputOutput, exception.Message, path);
        }
    }

    public async Task<ImageDecodeResult> DecodeAsync(
        Stream stream,
        string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            if (!stream.CanSeek)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InputOutput,
                    "The image stream must support seeking for safe decoding.",
                    sourceName);
            }

            if (stream.Length - stream.Position > ImageResourceLimits.MaximumEncodedBytes)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InvalidContent,
                    "The image exceeds the configured encoded-file resource limit.",
                    sourceName);
            }

            var startPosition = stream.Position;
            var info = await Image.IdentifyAsync(stream, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.InvalidContent,
                    "The image header could not be read.",
                    sourceName);
            }

            ImageResourceLimits.EnsureSafe(
                info.Width,
                info.Height,
                Math.Max(1, info.FrameMetadataCollection.Count));
            stream.Position = startPosition;
            var options = new DecoderOptions { MaxFrames = ImageResourceLimits.MaximumFrames };
            using var image = await Image.LoadAsync<Rgba32>(options, stream, cancellationToken).ConfigureAwait(false);
            var formatName = image.Metadata.DecodedImageFormat?.Name;
            if (formatName is null || !SupportedFormats.Contains(formatName))
            {
                return ImageDecodeResult.Failed(
                    ImageDecodeFailureKind.UnsupportedFormat,
                    $"The decoded format '{formatName ?? "unknown"}' is not supported.",
                    sourceName);
            }

            image.Mutate(context => context.AutoOrient());
            var frames = new List<DecodedImageFrame>(image.Frames.Count);
            foreach (var frame in image.Frames)
            {
                var pixels = new byte[checked(image.Width * image.Height * 4)];
                frame.CopyPixelDataTo(pixels);
                NormalizeTransparentPixels(pixels);
                var delay = formatName.Equals("GIF", StringComparison.OrdinalIgnoreCase)
                    ? checked(frame.Metadata.GetGifMetadata().FrameDelay * 10)
                    : 0;
                frames.Add(new DecodedImageFrame(pixels, delay));
            }

            return ImageDecodeResult.Success(new DecodedImage(
                image.Width,
                image.Height,
                formatName,
                frames));
        }
        catch (UnknownImageFormatException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.UnsupportedFormat,
                exception.Message,
                sourceName);
        }
        catch (InvalidImageContentException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.InvalidContent,
                exception.Message,
                sourceName);
        }
        catch (ImageFormatException exception)
        {
            return ImageDecodeResult.Failed(
                ImageDecodeFailureKind.InvalidContent,
                exception.Message,
                sourceName);
        }
        catch (InvalidDataException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InvalidContent, exception.Message, sourceName);
        }
        catch (OverflowException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InvalidContent, exception.Message, sourceName);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.AccessDenied, exception.Message, sourceName);
        }
        catch (IOException exception)
        {
            return ImageDecodeResult.Failed(ImageDecodeFailureKind.InputOutput, exception.Message, sourceName);
        }
    }

    private static void NormalizeTransparentPixels(Span<byte> pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
            }
        }
    }
}
