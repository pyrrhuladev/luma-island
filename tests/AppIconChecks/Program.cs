using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly List<object> Frames = new();
    private static readonly List<BitmapSource> Renders = new();
    private static string Output = null!;
    private static bool? ShellRegistered;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "app-icon-checks"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        try { CheckResources(); CheckNativeIcons(); CheckFallback(); CheckLifetime(); CheckTray(); SavePreview(); }
        catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
        finally { app.Shutdown(); }
        var assembly = typeof(AppIcon).Assembly;
        File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
        {
            passed = failure is null, checks = Checks.Count, completedChecks = Checks,
            version = assembly.GetName().Version?.ToString(), assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            iconFrames = Frames, shellIconRegistered = ShellRegistered,
            isolation = "Pack resources, published EXE resource extraction and private native icon handles only. One invisible nonactivating Window and temporary tray icon; no App startup, preferences, real input, global hotkey, Explorer restart or display-setting changes.",
            failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} application icon checks." : "FAIL: application icon checks.");
        return failure is null ? 0 : 1;
    }

    private static void CheckResources()
    {
        byte[] applicationIco = Array.Empty<byte>();
        foreach (var pair in new[] { (Name: "Luma.ico", Sizes: new[] { 16, 20, 24, 32, 48, 64, 128, 256 }),
            (Name: "Luma.Tray.ico", Sizes: new[] { 16, 20, 24, 32, 48, 64 }) })
        {
            var uri = new Uri("/Luma.Island;component/Assets/" + pair.Name, UriKind.Relative);
            using Stream stream = Application.GetResourceStream(uri)?.Stream ?? throw new FileNotFoundException(pair.Name);
            using var copy = new MemoryStream(); stream.CopyTo(copy);
            byte[] ico = copy.ToArray();
            if (pair.Name == "Luma.ico") applicationIco = ico;
            copy.Position = 0;
            var decoder = new IconBitmapDecoder(copy, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int[] sizes = decoder.Frames.Select(frame => frame.PixelWidth).Distinct().OrderBy(size => size).ToArray();
            Check(pair.Sizes.All(sizes.Contains) && decoder.Frames.All(frame => frame.PixelHeight == frame.PixelWidth),
                pair.Name + " is embedded with square frames for standard DPI sizes");
            Frames.Add(new { name = pair.Name, sizes });
        }
        string executable = Path.Combine(AppContext.BaseDirectory, "Published-Luma.Island.exe");
        IntPtr[] large = new IntPtr[1], small = new IntPtr[1];
        uint count = ExtractIconEx(executable, 0, large, small, 1);
        try
        {
            Check(count > 0 && large[0] != IntPtr.Zero && small[0] != IntPtr.Zero && Size(large[0]).Width > 0 && Size(small[0]).Width > 0,
                "The actual published EXE embeds a native application icon");
            var fromIco = typeof(AppIcon).GetMethod("FromIco", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (IntPtr handle in new[] { large[0], small[0] })
            {
                using var expected = (AppIcon)fromIco.Invoke(null, new object[] { applicationIco, (int)Size(handle).Width })!;
                Check(!expected.IsFallback && Pixels(IconBitmap(handle)).SequenceEqual(Pixels(IconBitmap(expected.DangerousGetHandle()))),
                    "Published EXE icon pixels match the embedded branded application asset");
            }
        }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
    }

    private static void CheckNativeIcons()
    {
        foreach (int pixels in new[] { 16, 20, 24, 32, 48, 64 })
        {
            using var icon = AppIcon.LoadTray(pixels);
            IntPtr handle = icon.DangerousGetHandle();
            Check(!icon.IsInvalid && !icon.IsFallback && icon.PixelSize == pixels && Size(handle) == new Size(pixels, pixels),
                $"Tray resource creates an owned {pixels}px HICON without falling back");
            BitmapSource image = IconBitmap(handle);
            byte[] bytes = Pixels(image);
            int transparent = 0, visible = 0;
            for (int i = 0; i < bytes.Length; i += 4)
            {
                if (bytes[i + 3] == 0) transparent++;
                if (bytes[i + 3] > 0) visible++;
            }
            Check(transparent > 0 && visible >= pixels * pixels / 50, $"Native {pixels}px rendering preserves transparent padding and the visible logo");
            Renders.Add(image);
            icon.Dispose();
            Check(icon.IsClosed && !TrySize(handle, out _), $"The owned {pixels}px HICON is destroyed at disposal");
            icon.Dispose();
        }
    }

    private static void CheckFallback()
    {
        var method = typeof(AppIcon).GetMethod("FromIco", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (byte[] corrupt in new[] { Array.Empty<byte>(), new byte[] { 0, 0, 1, 0, 1, 0 }, new byte[32] })
        {
            using var icon = (AppIcon)method.Invoke(null, new object[] { corrupt, 20 })!;
            IntPtr handle = icon.DangerousGetHandle();
            Check(icon.IsFallback && !icon.IsInvalid && TrySize(handle, out _), "A malformed ICO uses a valid shared system icon");
            icon.Dispose();
            Check(TrySize(handle, out _), "Disposing the fallback wrapper does not destroy the shared system icon");
        }
        var resource = typeof(AppIcon).GetMethod("FromResource", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var missing = (AppIcon)resource.Invoke(null, new object[] { new Uri("/Luma.Island;component/Assets/Missing.ico", UriKind.Relative), 24 })!;
        Check(missing.IsFallback && !missing.IsInvalid, "A missing embedded icon falls back without crashing startup");
    }

    private static void CheckLifetime()
    {
        using var process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 1);
        for (int iteration = 0; iteration < 20; iteration++)
            foreach (int size in new[] { 16, 20, 24, 32 }) { using var icon = AppIcon.LoadTray(size); }
        uint after = GetGuiResources(process.Handle, 1);
        Check(after <= before + 2, "Repeated icon replacement releases USER handles instead of accumulating native icons");
    }

    private static void CheckTray()
    {
        var window = new Window { Width = 100, Height = 100, Left = -30000, Top = -30000, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false };
        TrayService? tray = null;
        try
        {
            window.Show();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            int pixels = AppIcon.TrayPixelSize(hwnd);
            Check(pixels is >= 16 and <= 64, "Tray size follows Explorer DPI with an owner-window fallback");
            tray = new TrayService(window, () => { }, () => { }, () => { });
            ShellRegistered = tray.IsRegistered;
            var iconField = typeof(TrayService).GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var icon = (AppIcon)iconField.GetValue(tray)!;
            IntPtr original = icon.DangerousGetHandle();
            Check(!icon.IsFallback && icon.PixelSize == pixels && TrySize(original, out _), "TrayService owns the embedded logo at the notification-area size");
            var callback = typeof(TrayService).GetMethod("WindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (int message in new[] { 0x001A, 0x007E, 0x02E0 })
                callback.Invoke(tray, new object[] { hwnd, message, IntPtr.Zero, IntPtr.Zero, false });
            Check(ReferenceEquals(iconField.GetValue(tray), icon) && !icon.IsClosed,
                "DPI/settings events reuse the handle when the notification-area size is unchanged");
            // Delete only this test icon, then simulate the owner's Explorer-recreated notification.
            // No real Explorer restart or broadcast occurs.
            FieldInfo dataField = typeof(TrayService).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shell = typeof(TrayService).GetMethod("ShellNotifyIcon", BindingFlags.Static | BindingFlags.NonPublic)!;
            shell.Invoke(null, new object[] { 2u, dataField.GetValue(tray)! });
            uint recreated = RegisterWindowMessage("TaskbarCreated");
            callback.Invoke(tray, new object[] { hwnd, unchecked((int)recreated), IntPtr.Zero, IntPtr.Zero, false });
            Check(ReferenceEquals(iconField.GetValue(tray), icon) && TrySize(original, out _) && tray.IsRegistered == ShellRegistered,
                "Explorer recreation re-adds the same live icon without leaking or replacing it");
            tray.Dispose();
            Check(!tray.IsRegistered && icon.IsClosed && !TrySize(original, out _), "Tray disposal unregisters the icon and destroys its owned HICON");
            tray.Dispose();
        }
        finally { tray?.Dispose(); window.Close(); }
    }

    private static void SavePreview()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 32)), null, new Rect(0, 0, 480, 114));
            for (int i = 0; i < Renders.Count; i++)
            {
                BitmapSource image = Renders[i]; double x = 40 + 80 * i;
                drawing.DrawImage(image, new Rect(x - image.PixelWidth / 2d, 58 - image.PixelHeight / 2d, image.PixelWidth, image.PixelHeight));
                var text = new FormattedText(image.PixelWidth + "px", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, Brushes.WhiteSmoke, 1);
                drawing.DrawText(text, new Point(x - text.Width / 2, 95));
            }
        }
        var bitmap = new RenderTargetBitmap(480, 114, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using Stream stream = File.Create(Path.Combine(Output, "native-tray-icons.png")); encoder.Save(stream);
    }

    private static Size Size(IntPtr icon) => TrySize(icon, out Size size) ? size : default;
    private static BitmapSource IconBitmap(IntPtr icon)
    {
        BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze(); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; bgra.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
    private static bool TrySize(IntPtr icon, out Size size)
    {
        size = default;
        if (!GetIconInfo(icon, out IconInfo info)) return false;
        try
        {
            if (info.Color == IntPtr.Zero || GetObject(info.Color, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap bitmap) == 0) return false;
            size = new Size(bitmap.Width, bitmap.Height); return true;
        }
        finally { if (info.Color != IntPtr.Zero) DeleteObject(info.Color); if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); }
    }
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return folder.FullName;
        throw new DirectoryNotFoundException("Overlay repository root was not found.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int Icon; public uint X, Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, Bits; public IntPtr Pixels; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr item, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flag);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string value);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[] large, [Out] IntPtr[] small, uint count);
}
