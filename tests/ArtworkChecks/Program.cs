using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main(string[] args)
    {
        string directory = Path.GetFullPath(args.Length > 0 ? args[0] : "work/artwork-fix");
        Directory.CreateDirectory(directory);
        foreach (var fixture in new[]
        {
            ("wide", 512, 256, 96d, 96d), ("tall", 256, 512, 96d, 96d),
            ("square", 256, 256, 96d, 96d), ("tiny", 12, 8, 96d, 96d),
            ("panorama", 8192, 8, 96d, 96d), ("needle", 8, 8192, 96d, 96d),
            ("unequal-dpi", 512, 256, 192d, 96d)
        })
        {
            var (name, width, height, dpiX, dpiY) = fixture;
            BitmapSource input = CreateFixture(width, height, dpiX, dpiY);
            byte[] encoded = Encode(input);
            File.WriteAllBytes(Path.Combine(directory, name + "-input.png"), encoded);
            BitmapSource decoded;
            using (var source = new MemoryStream(encoded, writable: false))
            {
                source.Position = encoded.Length / 2; // Utility must rewind before reading the header.
                decoded = ArtworkDecoder.Decode(source);
            }
            if (name is "wide" or "tall") VerifyLegacyDefect(input, name, directory);
            Array.Clear(encoded); // No decoded pixels may depend on the disposed/changed encoded source.
            int expectedSide = Math.Min(128, Math.Min(width, height));
            Check(decoded.PixelWidth == expectedSide && decoded.PixelHeight == expectedSide, name + ": square dimensions without upscaling");
            Check(decoded.IsFrozen, name + ": frozen result");
            Check(decoded.DpiX == 96 && decoded.DpiY == 96 && decoded.Width == decoded.Height,
                name + ": normalized square DIP geometry");
            byte[] pixels = Pixels(decoded);
            Check(pixels.Length == expectedSide * expectedSide * 4, name + ": independent pixels after source disposal");
            Check(Task.Run(() => Pixels(decoded).Length).GetAwaiter().GetResult() == pixels.Length,
                name + ": frozen bitmap is readable from another thread");
            CheckCircle(decoded, name);
            if (width != height)
            {
                int edge = 0;
                for (int index = 0; index < pixels.Length; index += 4)
                    if (pixels[index + 2] > 180 && pixels[index + 1] < 110 || pixels[index] > 180 && pixels[index + 1] < 110) edge++;
                Check(edge == 0, name + ": colored external strips were cropped away");
            }
            File.WriteAllBytes(Path.Combine(directory, name + "-decoded.png"), Encode(decoded));
            if (name is "wide" or "tall" or "square")
            foreach (int size in new[] { 29, 62 })
            {
                BitmapSource rendered = Render(decoded, size);
                CheckCircle(rendered, name + ": UI " + size);
                Check(Pixels(rendered)[3] == 0, name + ": rounded UI clip " + size);
                File.WriteAllBytes(Path.Combine(directory, name + "-render-" + size + ".png"), Encode(rendered));
            }
        }
        string result = $"PASS: {_checks} artwork checks; wide/tall/square/tiny/extreme aspect/non-square DPI, source disposal, frozen cross-thread reads, 29/62 UI rendering; old decoder demonstrably retains external strips and deforms the circle in both wide/tall controls. Evidence: {directory}";
        File.WriteAllText(Path.Combine(directory, "result.txt"), result + Environment.NewLine);
        Console.WriteLine(result);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static BitmapSource CreateFixture(int width, int height, double dpiX, double dpiY)
    {
        int side = Math.Min(width, height), left = (width - side) / 2, top = (height - side) / 2;
        double centerX = left + side / 2d, centerY = top + side / 2d, radius = side * .28;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte r = 24, g = 90, b = 65;
            if (x < left || y < top || width == height && x < side / 10) { r = 240; g = 50; b = 70; }
            else if (x >= left + side || y >= top + side || width == height && x >= width - side / 10) { r = 40; g = 80; b = 230; }
            double dx = x + .5 - centerX, dy = y + .5 - centerY;
            if (dx * dx + dy * dy <= radius * radius) { r = 245; g = 205; b = 70; }
            int index = (y * width + x) * 4;
            pixels[index] = b; pixels[index + 1] = g; pixels[index + 2] = r; pixels[index + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static void CheckCircle(BitmapSource image, string name)
    {
        byte[] pixels = Pixels(image);
        int minX = image.PixelWidth, minY = image.PixelHeight, maxX = -1, maxY = -1;
        for (int y = 0; y < image.PixelHeight; y++)
        for (int x = 0; x < image.PixelWidth; x++)
        {
            int offset = (y * image.PixelWidth + x) * 4;
            if (pixels[offset + 2] < 180 || pixels[offset + 1] < 150 || pixels[offset] > 110) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        Check(maxX >= minX && maxY >= minY, name + ": center circle remains visible");
        Check(Math.Abs((maxX - minX) - (maxY - minY)) <= 1, name + ": circle remains round");
        Check(Math.Abs(minX + maxX - (image.PixelWidth - 1)) <= 2 &&
            Math.Abs(minY + maxY - (image.PixelHeight - 1)) <= 2, name + ": crop remains centered");
        if (image.PixelWidth >= 29)
            Check(Math.Abs(maxX - minX + 1 - image.PixelWidth * .56) <= 3, name + ": circle keeps its scale");
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    private static void VerifyLegacyDefect(BitmapSource input, string name, string directory)
    {
        using var stream = new MemoryStream(Encode(input), writable: false);
        var legacy = new BitmapImage();
        legacy.BeginInit(); legacy.CacheOption = BitmapCacheOption.OnLoad;
        legacy.DecodePixelWidth = 128; legacy.DecodePixelHeight = 128;
        legacy.StreamSource = stream; legacy.EndInit(); legacy.Freeze();
        byte[] pixels = Pixels(legacy);
        int strips = 0, minX = 128, minY = 128, maxX = -1, maxY = -1;
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            int offset = (y * 128 + x) * 4;
            if (pixels[offset + 2] > 180 && pixels[offset + 1] < 110 || pixels[offset] > 180 && pixels[offset + 1] < 110) strips++;
            if (pixels[offset + 2] < 180 || pixels[offset + 1] < 150 || pixels[offset] > 110) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        Check(strips > 0, name + ": control confirms old decoder fails to crop external strips");
        Check(maxX >= minX && maxY >= minY && Math.Abs((maxX - minX) - (maxY - minY)) > 10,
            name + ": control confirms old decoder turns circle into ellipse");
        File.WriteAllBytes(Path.Combine(directory, name + "-old-decoder.png"), Encode(legacy));
    }

    private static BitmapSource Render(BitmapSource source, int size)
    {
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), size == 29 ? 9 : 15, size == 29 ? 9 : 15) };
        image.Measure(new Size(size, size)); image.Arrange(new Rect(0, 0, size, size)); image.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(image); bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
