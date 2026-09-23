using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// Decoded-image cache for the renderer (port of the TS <c>ImageStore</c>). Custom assets
/// arrive as <c>data:</c> URLs (base64) inside the project; they are decoded to
/// <see cref="SKImage"/>s once and kept in an LRU cache. Lookups first try the exact string
/// instance (reference equality — no hashing of large data URLs every frame), then the content.
/// Unresolvable sources (e.g. web-only <c>idb://</c> references) cache as "missing" so they
/// are not retried every frame.
/// </summary>
public sealed class ImageStore : IDisposable
{
    private readonly int _maxSize;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<Entry>> _byReference = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Entry> _lru = new();

    public ImageStore(int maxSize = 300)
    {
        _maxSize = Math.Max(1, maxSize);
    }

    /// <summary>Number of cached sources (including ones that failed to decode).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lru.Count;
            }
        }
    }

    /// <summary>Returns the decoded image for <paramref name="source"/>, or null when it cannot be decoded.</summary>
    public SKImage? Get(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        lock (_gate)
        {
            if (_byReference.TryGetValue(source, out var node) || _byContent.TryGetValue(source, out node))
            {
                _byReference[source] = node;
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }

            var entry = new Entry(source, Decode(source));
            node = _lru.AddFirst(entry);
            _byContent[source] = node;
            _byReference[source] = node;
            while (_lru.Count > _maxSize)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _byContent.Remove(oldest.Value.Source);
                foreach (var key in _byReference.Where(pair => pair.Value == oldest).Select(pair => pair.Key).ToList())
                {
                    _byReference.Remove(key);
                }

                oldest.Value.Image?.Dispose();
            }

            return entry.Image;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _lru)
            {
                entry.Image?.Dispose();
            }

            _lru.Clear();
            _byContent.Clear();
            _byReference.Clear();
        }
    }

    public void Dispose() => Clear();

    /// <summary>Decodes a <c>data:</c> URL (base64 payload) into an image; null when unsupported.</summary>
    public static SKImage? Decode(string source)
    {
        var bytes = DataUrlBytes(source);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
#pragma warning disable CA1031 // Corrupt creator art must never crash rendering.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The raw bytes of a base64 <c>data:</c> URL, or null.</summary>
    public static byte[]? DataUrlBytes(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = source.AsSpan(5, comma - 5);
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(source[(comma + 1)..].Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record Entry(string Source, SKImage? Image);
}
