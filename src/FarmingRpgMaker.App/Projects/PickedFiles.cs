using System.Globalization;
using System.Text;
using Avalonia.Platform.Storage;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.Projects;

/// <summary>A picked file is larger than the editor reads for its purpose.</summary>
public sealed class FileTooLargeException(string message) : IOException(message);

/// <summary>
/// Reads and writes files the creator picks (project JSON, content packs): reads stop at a size
/// cap, so a multi-gigabyte file picked by mistake is refused instead of freezing the editor, and
/// saves to a local file are atomic (a temp file next to it, then a rename), so a failure part-way
/// leaves the previous file as it was.
/// </summary>
internal static class PickedFiles
{
    /// <summary>The largest project JSON Import reads.</summary>
    public const long MaxProjectBytes = 256L * 1024 * 1024;

    /// <summary>The largest content pack JSON the Mods view reads.</summary>
    public const long MaxPackBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The text of <paramref name="file"/> (UTF-8, or the encoding its byte order mark names).
    /// </summary>
    /// <exception cref="FileTooLargeException">The file is larger than <paramref name="maxBytes"/>.</exception>
    public static async Task<string> ReadTextAsync(IStorageFile file, long maxBytes, string what)
    {
        ArgumentNullException.ThrowIfNull(file);
        var properties = await file.GetBasicPropertiesAsync().ConfigureAwait(true);
        if (properties.Size is { } size && size > (ulong)maxBytes)
        {
            throw TooLarge(file.Name, (long)Math.Min(size, long.MaxValue), maxBytes, what);
        }

        await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
        return await ReadTextAsync(stream, maxBytes, file.Name, what).ConfigureAwait(true);
    }

    /// <summary>The text of <paramref name="stream"/>, read up to <paramref name="maxBytes"/>.</summary>
    /// <exception cref="FileTooLargeException">The stream holds more than <paramref name="maxBytes"/>.</exception>
    public static async Task<string> ReadTextAsync(Stream stream, long maxBytes, string name, string what)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length > maxBytes)
        {
            throw TooLarge(name, stream.Length, maxBytes, what);
        }

        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(true)) > 0)
        {
            if (memory.Length + read > maxBytes)
            {
                throw TooLarge(name, memory.Length + read, maxBytes, what);
            }

            await memory.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
        }

        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Writes <paramref name="text"/> (UTF-8) to the picked <paramref name="file"/>: atomically when
    /// it is a local file, otherwise through the storage provider's stream.
    /// </summary>
    public static async Task WriteTextAsync(IStorageFile file, string text)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.TryGetLocalPath() is { } path)
        {
            await Task.Run(() => AtomicFile.WriteAllText(path, text)).ConfigureAwait(true);
            return;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text).ConfigureAwait(true);
    }

    private static FileTooLargeException TooLarge(string name, long size, long maxBytes, string what) =>
        new(string.Format(
            CultureInfo.InvariantCulture,
            "{0} is {1} MB or more; {2} over {3} MB can't be opened.",
            name,
            Math.Max(1, size / (1024 * 1024)),
            what,
            maxBytes / (1024 * 1024)));
}
