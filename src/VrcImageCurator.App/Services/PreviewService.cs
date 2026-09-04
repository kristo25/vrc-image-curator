using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VrcImageCurator.Core.Imaging;

namespace VrcImageCurator.App.Services;

public sealed class PreviewService : IDisposable
{
    private readonly Dictionary<System.Windows.Controls.Image, PreviewAnimation> _animations = [];
    private readonly Dictionary<System.Windows.Controls.Image, int> _versions = [];

    public BitmapSource? Load(string? path, int decodeWidth = 960)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(path);
            ImageResourceLimits.EnsureSafe(info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count));
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            image.Mutate(context => context.AutoOrient());
            if (image.Width > decodeWidth)
            {
                image.Mutate(context => context.Resize(decodeWidth, 0));
            }

            return CreateBitmapSource(image.Frames.RootFrame, image.Width, image.Height);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or UnknownImageFormatException
                or InvalidImageContentException
                or InvalidDataException
                or OverflowException
                or NotSupportedException)
        {
            return null;
        }
    }

    public async Task ShowAsync(
        System.Windows.Controls.Image target,
        string? path,
        int decodeWidth = 960)
    {
        ArgumentNullException.ThrowIfNull(target);
        Stop(target);
        var version = _versions.GetValueOrDefault(target) + 1;
        _versions[target] = version;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            target.Source = null;
            return;
        }

        if (!Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            target.Source = Load(path, decodeWidth);
            return;
        }

        try
        {
            var info = await SixLabors.ImageSharp.Image.IdentifyAsync(path);
            ImageResourceLimits.EnsureSafe(info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count));
            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(path);
            image.Mutate(context => context.AutoOrient());
            if (image.Width > decodeWidth)
            {
                image.Mutate(context => context.Resize(decodeWidth, 0));
            }

            var frames = new List<BitmapSource>(image.Frames.Count);
            var delays = new List<TimeSpan>(image.Frames.Count);
            foreach (var frame in image.Frames)
            {
                frames.Add(CreateBitmapSource(frame, image.Width, image.Height));
                var milliseconds = Math.Max(20, frame.Metadata.GetGifMetadata().FrameDelay * 10);
                delays.Add(TimeSpan.FromMilliseconds(milliseconds));
            }

            if (_versions.GetValueOrDefault(target) != version)
            {
                return;
            }

            target.Source = frames[0];
            if (frames.Count > 1)
            {
                var animation = new PreviewAnimation(target, frames, delays);
                _animations[target] = animation;
                animation.Start();
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or UnknownImageFormatException
                or InvalidImageContentException
                or InvalidDataException
                or OverflowException)
        {
            target.Source = Load(path, decodeWidth);
        }
    }

    private static BitmapSource CreateBitmapSource(ImageFrame<Rgba32> frame, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        frame.CopyPixelDataTo(rgba);
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            (rgba[offset], rgba[offset + 2]) = (rgba[offset + 2], rgba[offset]);
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            rgba,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public void Stop(System.Windows.Controls.Image target)
    {
        if (_animations.Remove(target, out var animation))
        {
            animation.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var animation in _animations.Values)
        {
            animation.Dispose();
        }

        _animations.Clear();
        _versions.Clear();
    }

    private sealed class PreviewAnimation(
        System.Windows.Controls.Image target,
        IReadOnlyList<BitmapSource> frames,
        IReadOnlyList<TimeSpan> delays) : IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer = new();
        private int _frameIndex;

        public void Start()
        {
            _timer.Interval = delays[0];
            _timer.Tick += Tick;
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Tick;
        }

        private void Tick(object? sender, EventArgs e)
        {
            _frameIndex = (_frameIndex + 1) % frames.Count;
            target.Source = frames[_frameIndex];
            _timer.Interval = delays[_frameIndex];
        }
    }
}
