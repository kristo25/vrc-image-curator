using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using VrcImageCurator.Core.FileSystem;
using VrcImageCurator.Core.Imaging;

namespace VrcImageCurator.Core.Atlas;

public sealed record AtlasExportResult(
    string Path,
    int FrameCount,
    int FrameDelayCentiseconds,
    int EffectiveFramesPerSecond);

/// <summary>
/// Turns a VRChat emoji sheet into an animated GIF, using the frame count, rate and loop
/// direction taken from the file name.
/// </summary>
public sealed class AtlasGifExporter
{
    /// <summary>
    /// GIF stores a delay in hundredths of a second, and browsers and chat clients silently
    /// promote anything under two hundredths to ten - so a sheet asking for 60fps would play at
    /// 10fps almost everywhere. Clamping here means the exported file plays at the rate it claims.
    /// </summary>
    public const int MinimumFrameDelayCentiseconds = 2;

    public const string GifExtension = ".gif";

    private static readonly GifEncoder Encoder = new()
    {
        Quantizer = new WuQuantizer(new QuantizerOptions { Dither = KnownDitherings.FloydSteinberg }),
    };

    /// <summary>Highest rate a GIF can express without being rewritten by the viewer.</summary>
    public static int MaximumRepresentableFramesPerSecond => 100 / MinimumFrameDelayCentiseconds;

    public static int FrameDelayFor(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        var delay = (int)Math.Round(100.0 / framesPerSecond, MidpointRounding.AwayFromZero);
        return Math.Max(MinimumFrameDelayCentiseconds, delay);
    }

    /// <summary>
    /// Writes <paramref name="destinationPath"/> from the sheet at <paramref name="sourcePath"/>.
    /// The sheet is only ever read. The GIF is built beside its destination and moved into place,
    /// so an interrupted export leaves either the previous file or nothing, never a partial one.
    /// </summary>
    public async Task<AtlasExportResult> ExportAsync(
        string sourcePath,
        string destinationPath,
        EmojiAtlasName name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(name);
        PathBoundary.EnsureNoReparsePoints(sourcePath, "Atlas image");

        var options = new DecoderOptions { MaxFrames = 1 };
        using var atlas = await Image
            .LoadAsync<Rgba32>(options, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        ImageResourceLimits.EnsureSafe(atlas.Width, atlas.Height, 1);

        if (!AtlasLayout.TryCreate(name.FrameCount, atlas.Width, atlas.Height, out var layout))
        {
            throw new InvalidOperationException(
                $"A {atlas.Width}x{atlas.Height} image cannot hold {name.FrameCount} frames on a whole-pixel grid.");
        }

        var order = layout.PlaybackOrder(name.LoopStyle);
        var exportedBytes = checked((long)order.Count * layout.CellWidth * layout.CellHeight * 4);
        if (exportedBytes > ImageResourceLimits.MaximumDecodedBytes)
        {
            throw new InvalidOperationException(
                "The animation would exceed the configured decoded-memory resource limit.");
        }

        var delay = FrameDelayFor(name.FramesPerSecond);
        Image<Rgba32>? animation = null;
        try
        {
            foreach (var index in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (x, y, width, height) = layout.GetFrame(index);
                using var cell = atlas.Clone(context => context.Crop(new Rectangle(x, y, width, height)));
                if (animation is null)
                {
                    animation = cell.Clone();
                }
                else
                {
                    animation.Frames.AddFrame(cell.Frames.RootFrame);
                }
            }

            if (animation is null)
            {
                throw new InvalidOperationException("The sheet produced no frames.");
            }

            animation.Metadata.GetGifMetadata().RepeatCount = 0;
            foreach (var frame in animation.Frames)
            {
                var metadata = frame.Metadata.GetGifMetadata();
                metadata.FrameDelay = delay;

                // Each cell is drawn whole and carries its own transparency. Without clearing
                // between frames a transparent pixel keeps whatever the previous frame left
                // there, which smears the animation instead of replacing it.
                metadata.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = destinationPath + ".tmp";
            await animation.SaveAsync(temporaryPath, Encoder, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new AtlasExportResult(
                destinationPath,
                order.Count,
                delay,
                Math.Max(1, (int)Math.Round(100.0 / delay, MidpointRounding.AwayFromZero)));
        }
        finally
        {
            animation?.Dispose();
        }
    }
}
