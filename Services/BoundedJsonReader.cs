using System.Text.Json;

namespace WallpaperField.Services;

public sealed class InputBudgetExceededException : IOException
{
    public InputBudgetExceededException(
        string inputName,
        long limitBytes,
        long observedBytes)
        : base($"{inputName} 超过 {limitBytes} 字节 JSON 上限（已观察 {observedBytes} 字节）。")
    {
        InputName = inputName;
        LimitBytes = limitBytes;
        ObservedBytes = observedBytes;
    }

    public string InputName { get; } = string.Empty;

    public long LimitBytes { get; }

    public long ObservedBytes { get; }
}

internal static class BoundedJsonReader
{
    internal const long MaxJsonBytes = 4L * 1024 * 1024;

    internal static async Task<JsonDocument> ParseDocumentAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonDocument.Parse(RemoveUtf8Bom(bytes));
    }

    internal static async Task<T?> DeserializeAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<T>(RemoveUtf8Bom(bytes).Span, options);
    }

    internal static async Task<byte[]> ReadAllBytesAsync(
        Stream stream,
        string inputName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        inputName = string.IsNullOrWhiteSpace(inputName) ? "JSON 输入" : inputName;

        var initialLength = TryGetRemainingLength(stream);
        if (initialLength is > MaxJsonBytes)
        {
            throw new InputBudgetExceededException(
                inputName,
                MaxJsonBytes,
                initialLength.Value);
        }

        var initialCapacity = initialLength is >= 0 and <= MaxJsonBytes
            ? checked((int)initialLength.Value)
            : 0;
        using var destination = new MemoryStream(initialCapacity);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximumRead = checked((int)Math.Min(
                buffer.Length,
                MaxJsonBytes - totalBytes + 1));
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(0, maximumRead), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes = checked(totalBytes + bytesRead);
            if (totalBytes > MaxJsonBytes)
            {
                throw new InputBudgetExceededException(
                    inputName,
                    MaxJsonBytes,
                    totalBytes);
            }

            destination.Write(buffer, 0, bytesRead);
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> ReadFileBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAllBytesAsync(stream, path, cancellationToken).ConfigureAwait(false);
    }

    private static long? TryGetRemainingLength(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        try
        {
            return Math.Max(0, checked(stream.Length - stream.Position));
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static ReadOnlyMemory<byte> RemoveUtf8Bom(byte[] bytes)
        => bytes.Length >= 3
           && bytes[0] == 0xEF
           && bytes[1] == 0xBB
           && bytes[2] == 0xBF
            ? bytes.AsMemory(3)
            : bytes;
}
