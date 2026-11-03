using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Luma.Island.Services;

internal static class ArtworkDecoder
{
    public static BitmapSource Decode(Stream stream)
    {
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        int side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        if (side <= 0) throw new InvalidDataException("Artwork has no pixels.");
        var crop = new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side);

        // Crop before reducing the image; setting both decode dimensions would distort it.
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.SourceRect = crop;
        image.DecodePixelWidth = Math.Min(128, side);
        image.StreamSource = stream;
        image.EndInit();

        // Keep only the small square, detached from the encoded stream and its DPI metadata.
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
