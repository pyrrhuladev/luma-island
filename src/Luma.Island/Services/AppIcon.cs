using System;
using System.Buffers.Binary;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace Luma.Island.Services;

/// <summary>One native icon. Embedded icons are owned; the last-resort system icon is shared.</summary>
public sealed class AppIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    private const int MaximumIconBytes = 4 * 1024 * 1024;
    public int PixelSize { get; }
    public bool IsFallback { get; }

    private AppIcon(IntPtr icon, int pixels, bool fallback) : base(!fallback)
    {
        SetHandle(icon);
        PixelSize = pixels;
        IsFallback = fallback;
    }

    public static AppIcon LoadTrayForWindow(IntPtr owner) => LoadTray(TrayPixelSize(owner));

    public static AppIcon LoadTray(int pixels) => FromResource(
        new Uri("/Luma.Island;component/Assets/Luma.Tray.ico", UriKind.Relative), pixels);

    internal static AppIcon FromResource(Uri resourceUri, int pixels)
    {
        pixels = Math.Clamp(pixels, 16, 64);
        try
        {
            var resource = Application.GetResourceStream(resourceUri);
            if (resource is null) return SystemFallback(pixels);
            using Stream input = resource.Stream;
            using var output = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = input.Read(chunk)) != 0)
            {
                if (output.Length + count > MaximumIconBytes) return SystemFallback(pixels);
                output.Write(chunk, 0, count);
            }
            return FromIco(output.ToArray(), pixels);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or
            NotSupportedException or MissingManifestResourceException)
        { return SystemFallback(pixels); }
    }

    /// <summary>The notification area belongs to Explorer, which can have a different DPI than the capsule.</summary>
    public static int TrayPixelSize(IntPtr owner)
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar);
        if (dpi == 0 && owner != IntPtr.Zero) dpi = GetDpiForWindow(owner);
        int pixels = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi); // SM_CXSMICON
        return Math.Clamp(pixels > 0 ? pixels : 16, 16, 64);
    }

    internal static AppIcon FromIco(byte[] ico, int pixels)
    {
        pixels = Math.Clamp(pixels, 16, 64);
        try
        {
            byte[] image = SelectFrame(ico, pixels);
            IntPtr icon = CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, pixels, pixels, 0);
            return icon != IntPtr.Zero ? new AppIcon(icon, pixels, false) : SystemFallback(pixels);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        { return SystemFallback(pixels); }
    }

    private static byte[] SelectFrame(byte[] ico, int pixels)
    {
        if (ico.Length < 6 || ico.Length > MaximumIconBytes || Word(ico, 0) != 0 || Word(ico, 2) != 1)
            throw new InvalidDataException("Invalid icon directory.");
        int count = Word(ico, 4);
        if (count is < 1 or > 256 || 6 + 16 * count > ico.Length) throw new InvalidDataException("Invalid icon frame count.");
        int selected = -1, bestCost = int.MaxValue;
        for (int index = 0; index < count; index++)
        {
            int entry = 6 + index * 16;
            int width = ico[entry] == 0 ? 256 : ico[entry];
            int height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            uint size = Dword(ico, entry + 8), offset = Dword(ico, entry + 12);
            if (size == 0 || offset < 6 + 16 * count || offset > ico.Length || size > ico.Length - offset)
                throw new InvalidDataException("Invalid icon frame bounds.");
            if (width != height) continue;
            // Prefer exact frames, then downsampling a larger frame over stretching a smaller one.
            int cost = width >= pixels ? width - pixels : 10000 + pixels - width;
            if (cost < bestCost) { selected = entry; bestCost = cost; }
        }
        if (selected < 0) throw new InvalidDataException("No square icon frame.");
        return ico.AsSpan(checked((int)Dword(ico, selected + 12)), checked((int)Dword(ico, selected + 8))).ToArray();
    }

    private static ushort Word(byte[] data, int start) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start, 2));
    private static uint Dword(byte[] data, int start) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start, 4));

    private static AppIcon SystemFallback(int pixels)
    {
        // LR_SHARED system handles must never be passed to DestroyIcon.
        IntPtr icon = LoadImage(IntPtr.Zero, new IntPtr(32512), 1, pixels, pixels, 0x8000); // IDI_APPLICATION / IMAGE_ICON
        if (icon == IntPtr.Zero) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        return new AppIcon(icon, pixels, true);
    }

    protected override bool ReleaseHandle()
    {
        IntPtr icon = handle;
        handle = IntPtr.Zero;
        return DestroyIcon(icon);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
