using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class TexBitmapTestSupport
{
    internal static BitmapDecoder Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        foreach (var frame in decoder.Frames) frame.Freeze();
        return decoder;
    }

    internal static Color Pixel(BitmapSource source, int x = 0, int y = 0)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
