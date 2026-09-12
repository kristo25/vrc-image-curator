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
    int EffectiveFramesPerSecond,
    string? Note = null);

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

    /// <summary>
    /// The rate an exported file will really play at, which is not always the one asked for: GIF
    /// stores whole hundredths of a second, so most rates land on the nearest expressible one.
    /// </summary>
    /// <remarks>
    /// The single definition of this on purpose. It used to be worked out in two places with two
    /// different roundings, so the Animations tab and the export result disagreed about the same
    /// file - a sheet at 17fps was reported as playing at 16 in one and 17 in the other.
    /// </remarks>
    public static int EffectiveFramesPerSecondFor(int framesPerSecond) =>
        Math.Max(1, (int)Math.Round(100.0 / FrameDelayFor(framesPerSecond), MidpointRounding.AwayFromZero));

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

        // A file that already carries several frames is an animation, not a sheet. VRChat's own
        // exported GIF sits beside its sheet under the identical name, so this is the only thing
        // that separates them once a caller has bypassed the name check.
        await using (var probe = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var info = await Image.IdentifyAsync(probe, cancellationToken).ConfigureAwait(false);
            if (info is not null && info.FrameMetadataCollection.Count > 1)
            {
                throw new InvalidOperationException(
                    "This image is already animated. A sprite sheet has to be a single still image.");
            }
        }

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

        // The name decides how many frames the animation has, full stop. VRChat writes the count
        // itself and it was right on 55 of the 56 sheets this was measured against, which is better
        // than any reading of the pixels managed. Art sitting past the last named frame is noted and
        // then left out - it is not a reason to refuse a sheet a person asked for, and refusing was
        // the more common mistake by a wide margin.
        var inspection = AtlasInspector.Inspect(atlas, layout);
        string? note = null;
        if (inspection.DisagreesWithTheName)
        {
            // Only worth naming a number when it is a different one. Suggesting the count already
            // in use, as a correction, reads as nonsense.
            var advice = inspection.FrameCountThatWouldFit > name.FrameCount
                ? $"The name decided; {inspection.FrameCountThatWouldFit} frames would take in everything drawn."
                : "The name decided, and the cells it does not reach were left out.";
            note = $"the sheet carries art in {inspection.CellsWithContent} of "
                + $"{layout.Columns * layout.Rows} cells, and its name counts {name.FrameCount} frames. "
                + advice;
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
                EffectiveFramesPerSecondFor(name.FramesPerSecond),
                note);
        }
        finally
        {
            animation?.Dispose();
        }
    }
}
