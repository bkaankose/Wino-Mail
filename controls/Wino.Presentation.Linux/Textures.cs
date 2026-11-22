namespace Wino.Presentation.Linux;

/// <summary>
/// <c>Gdk.Texture</c> from a file or from bytes. Files are read off the main thread with <see cref="ReadAsync"/>;
/// textures are created on the main thread.
/// </summary>
// Creating Gir.Core wrappers on a worker thread was never proven safe; a thumbnail is 80 to 128 px, decodes in well
// under a millisecond and is decoded once per address, so only the file read leaves the main thread.
public static class Textures
{
    public const int MaxFileBytes = 8 * 1024 * 1024;

    public static Gdk.Texture? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes) return null;
            return Gdk.Texture.NewFromFilename(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Gdk.Texture? FromBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var data = GLib.Bytes.New(bytes);
            return Gdk.Texture.NewFromBytes(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the file on any thread; null when it is missing, empty, larger than <see cref="MaxFileBytes"/> or unreadable.</summary>
    public static async Task<byte[]?> ReadAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes) return null;
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// A bounded least-recently-used cache of textures by key, main thread only. A stored null means "known to have none".
/// Eviction only drops the reference: a texture is immutable and may still be shown.
/// </summary>
public sealed class TextureCache(int capacity = 256)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, Gdk.Texture? Texture)>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Gdk.Texture? Texture)> _order = new();
    private readonly int _capacity = Math.Max(1, capacity);

    public int Count => _entries.Count;

    public bool TryGet(string key, out Gdk.Texture? texture)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            texture = null;
            return false;
        }
        _order.Remove(node);
        _order.AddFirst(node);
        texture = node.Value.Texture;
        return true;
    }

    public void Set(string key, Gdk.Texture? texture)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            existing.Value = (key, texture);
            _order.AddFirst(existing);
            return;
        }
        _entries[key] = _order.AddFirst((key, texture));
        while (_entries.Count > _capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }
    }

    public bool Remove(string key)
    {
        if (!_entries.Remove(key, out var node)) return false;
        _order.Remove(node);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
    }
}
