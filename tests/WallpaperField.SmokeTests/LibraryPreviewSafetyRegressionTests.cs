using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Controls;
using WallpaperField.Models;

internal static class LibraryPreviewSafetyRegressionTests
{
    internal static Task RunAsync(Action<bool, string> assert)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"WallpaperField-PreviewSafety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                VerifyStaticBoundaries(root, assert);
                VerifyPendingLoadOwnership(assert);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void VerifyStaticBoundaries(string root, Action<bool, string> assert)
    {
        var portrait = Path.Combine(root, "portrait.png");
        WritePng(portrait, 1, 128);
        var bitmap = AnimatedPreviewImage.DecodeStaticPreview(portrait, 480, CancellationToken.None);
        assert(bitmap.IsFrozen && bitmap.PixelWidth == 1 && bitmap.PixelHeight == 128,
            "Static library preview upscaled a narrow source or did not freeze its pixels.");
        using (var exclusive = new FileStream(portrait, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            assert(pixels.Length == 512, "Detached static preview pixels cannot be read after source close.");
        }

        var oversizedDimensions = Path.Combine(root, "wide.png");
        WritePng(oversizedDimensions, 4097, 1);
        Reject(() => AnimatedPreviewImage.DecodeStaticPreview(
                oversizedDimensions, 480, CancellationToken.None),
            "over-budget source dimensions", assert);

        var oversizedBytes = Path.Combine(root, "oversized.png");
        using (var stream = File.Create(oversizedBytes))
        {
            stream.SetLength(PreviewThumbnailLimits.MaximumInputBytes + 1);
        }

        Reject(() => AnimatedPreviewImage.DecodeStaticPreview(
                oversizedBytes, 480, CancellationToken.None),
            "over-budget encoded bytes", assert);
        Reject(() => AnimatedPreviewImage.DecodeStaticPreview(
                Path.Combine(root, "NUL.png"), 480, CancellationToken.None),
            "a Windows reserved device path", assert);

        var renamedGif = Path.Combine(root, "renamed.png");
        File.WriteAllBytes(renamedGif, GifPreviewSafetyRegressionTests.BuildGif(1, 1, 32767, 32767));
        Reject(() => AnimatedPreviewImage.DecodeStaticPreview(
                renamedGif, 480, CancellationToken.None),
            "GIF content with an oversized frame under a PNG extension", assert);

        using (var writer = new FileStream(portrait, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Reject(() => AnimatedPreviewImage.DecodeStaticPreview(portrait, 480, CancellationToken.None),
                "source being concurrently modified", assert);
        }

        var gif = Path.Combine(root, "valid.gif");
        File.WriteAllBytes(gif, GifPreviewSafetyRegressionTests.BuildGif(1, 1, 1, 1, 2));
        var firstFrame = AnimatedPreviewImage.DecodeStaticPreview(gif, 480, CancellationToken.None);
        assert(firstFrame.IsFrozen && firstFrame.PixelWidth == 1 && firstFrame.PixelHeight == 1,
            "Static GIF first-frame decoding changed after validation.");

        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "linked-preview");
        Directory.CreateDirectory(target);
        WritePng(Path.Combine(target, "preview.png"), 1, 1);
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(startInfo) ?? throw new IOException("Could not create preview junction fixture."))
        {
            process.WaitForExit();
            assert(process.ExitCode == 0, "The preview junction fixture could not be created.");
        }

        try
        {
            Reject(() => AnimatedPreviewImage.DecodeStaticPreview(
                    Path.Combine(junction, "preview.png"), 480, CancellationToken.None),
                "a preview under a directory junction", assert);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private static void VerifyPendingLoadOwnership(Action<bool, string> assert)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pendingField = typeof(AnimatedPreviewImage).GetField("_loadCancellation", flags)!;
        var versionField = typeof(AnimatedPreviewImage).GetField("_loadVersion", flags)!;
        var refresh = typeof(AnimatedPreviewImage).GetMethod("RefreshViewportState", flags)!;
        var complete = typeof(AnimatedPreviewImage).GetMethod("CompletePendingLoad", flags)!;
        var image = new AnimatedPreviewImage();
        var window = new Window
        {
            Content = image, Width = 32, Height = 32,
            Left = -10_000, Top = -10_000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            image.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            using var pending = new CancellationTokenSource();
            var token = pending.Token;
            pendingField.SetValue(image, pending);
            var version = (int)versionField.GetValue(image)!;
            for (var index = 0; index < 10; index++)
            {
                refresh.Invoke(image, null);
            }

            assert(ReferenceEquals(pendingField.GetValue(image), pending)
                   && !token.IsCancellationRequested
                   && (int)versionField.GetValue(image)! == version,
                "Viewport refresh restarted an existing preview load.");
            using var stale = new CancellationTokenSource();
            complete.Invoke(image, [version - 1, stale.Token]);
            assert(ReferenceEquals(pendingField.GetValue(image), pending),
                "A stale preview completion retired the current load.");
            complete.Invoke(image, [version, stale.Token]);
            assert(ReferenceEquals(pendingField.GetValue(image), pending),
                "A different preview token retired the current load.");
            complete.Invoke(image, [version, token]);
            assert(pendingField.GetValue(image) is null,
                "The matching preview completion retained its cancellation owner.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[checked(width * height * 4)], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Reject(Action action, string scenario, Action<bool, string> assert)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return;
        }

        assert(false, $"Static library preview accepted {scenario}.");
    }
}
