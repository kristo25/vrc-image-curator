namespace VrcPicSorter.Core.Imaging;

public static class ImageResourceLimits
{
    public const int MaximumWidth = 16_384;
    public const int MaximumHeight = 16_384;
    public const int MaximumFrames = 256;
    public const long MaximumDecodedBytes = 512L * 1024 * 1024;
    public const long MaximumEncodedBytes = 256L * 1024 * 1024;

    public static void EnsureSafe(int width, int height, int frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0
            || width > MaximumWidth
            || height > MaximumHeight
            || frameCount > MaximumFrames)
        {
            throw new InvalidDataException("The image exceeds the configured resource limit.");
        }

        var decodedBytes = checked((long)width * height * 4 * frameCount);
        if (decodedBytes > MaximumDecodedBytes)
        {
            throw new InvalidDataException("The image exceeds the configured decoded-memory resource limit.");
        }
    }
}
