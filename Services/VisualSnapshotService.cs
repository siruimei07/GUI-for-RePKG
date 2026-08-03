using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FieldStation.Services;

/// <summary>Non-persistent visual QA. Snapshot mode never starts a backend cycle.</summary>
public static class VisualSnapshotService
{
    public static bool HasFlag(string[] args, string flag)
        => args.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    public static bool TryGetOption(string[] args, string option, out string value)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            value = args[index + 1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetSnapshotPath(string[] args, out string path)
    {
        if (TryGetOption(args, "--snapshot", out var rawPath))
        {
            path = Path.GetFullPath(rawPath);
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static int GetIntOption(string[] args, string option, int fallback)
        => TryGetOption(args, option, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

    public static void Capture(Window window, string outputPath, int width, int height)
    {
        window.WindowState = WindowState.Normal;
        window.Width = Math.Max((int)window.MinWidth, width);
        window.Height = Math.Max((int)window.MinHeight, height);
        window.Left = 0;
        window.Top = 0;
        window.UpdateLayout();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
