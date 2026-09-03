using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Clicky.Core;

namespace Clicky.Windows.Native;

/// <summary>Bounds model image work without changing the physical capture used for guidance coordinates.</summary>
public static class ImagePreparation
{
    public const int MaximumFileBytes = 14 * 1024 * 1024;
    public const long MaximumPixels = 32_000_000;
    public const long MaximumDecodedBytes = 256 * 1024 * 1024;
    private const int MaximumDimension = 32768;

    public static ImageAttachment ForModel(ImageAttachment image, int maximumEdge = 768)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maximumEdge is < 64 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maximumEdge), "Model image size must be between 64 and 2048 pixels.");
        if (string.IsNullOrWhiteSpace(image.Base64))
            throw new InvalidDataException("The attached image is empty.");
        // Check the encoded length before allocating a decoded buffer. Whitespace is also bounded.
        if (image.Base64.Length > ((MaximumFileBytes + 2L) / 3) * 4)
            throw new InvalidDataException("Images sent to a model must be no larger than 14 MiB.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image.Base64);
        }
        catch (FormatException exception) { throw new InvalidDataException("The attached image is not valid Base64 data.", exception); }
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Images sent to a model must be no larger than 14 MiB.");

        try
        {
            using var metadataStream = new MemoryStream(bytes, false);
            // On-demand decoding reads dimensions before any full raster is requested.
            var decoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                throw new InvalidDataException("The attached image contains no image frame.");
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            var pixels = (long)width * height;
            var bytesPerPixel = Math.Max(4, (frame.Format.BitsPerPixel + 7) / 8);
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension || pixels > MaximumPixels || pixels * bytesPerPixel > MaximumDecodedBytes)
                throw new InvalidDataException("The image exceeds the supported decoded dimensions. Use a screenshot or a smaller image (up to 32 megapixels).");

            if (Math.Max(width, height) <= maximumEdge)
                return image;

            var scale = maximumEdge / (double)Math.Max(width, height);
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
            using var input = new MemoryStream(bytes, false);
            var resized = new BitmapImage();
            resized.BeginInit();
            resized.CacheOption = BitmapCacheOption.OnLoad;
            resized.DecodePixelWidth = targetWidth;
            resized.DecodePixelHeight = targetHeight;
            resized.StreamSource = input;
            resized.EndInit();
            resized.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var output = new MemoryStream();
            encoder.Save(output);
            return new ImageAttachment(Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length)), "image/png", image.Name);
        }
        catch (Exception exception) when (exception is NotSupportedException or FormatException or COMException)
        {
            throw new InvalidDataException("The image could not be decoded. Use a standard PNG or JPEG image.", exception);
        }
    }
}
