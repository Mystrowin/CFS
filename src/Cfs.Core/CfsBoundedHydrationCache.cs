namespace Cfs.Core;

/// <summary>
/// Deduplicates concurrent ProjFS payload loads while retaining only a bounded
/// working set. Oversized payloads are returned to the caller but never cached.
/// </summary>
internal sealed class CfsBoundedHydrationCache
{
    public const long DefaultByteLimit = 256L * 1024 * 1024;
    public const int DefaultEntryLimit = 64;

    private sealed class Entry(Lazy<byte[]> value, LinkedListNode<string> node)
    {
        public Lazy<byte[]> Value { get; } = value;
        public LinkedListNode<string> Node { get; } = node;
        public long Size { get; set; }
        public bool Accounted { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly long _byteLimit;
    private readonly int _entryLimit;
    private long _retainedBytes;

    public CfsBoundedHydrationCache(long byteLimit = DefaultByteLimit, int entryLimit = DefaultEntryLimit)
    {
        if (byteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        if (entryLimit <= 0) throw new ArgumentOutOfRangeException(nameof(entryLimit));
        _byteLimit = byteLimit;
        _entryLimit = entryLimit;
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public long ByteLimit => _byteLimit;
    public int EntryLimit => _entryLimit;
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return _entries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public byte[] GetOrAdd(string path, Func<byte[]> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(factory);
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out entry!))
            {
                Touch(entry);
            }
            else
            {
                var node = _lru.AddFirst(path);
                entry = new Entry(new Lazy<byte[]>(factory, LazyThreadSafetyMode.ExecutionAndPublication), node);
                _entries.Add(path, entry);
            }
        }

        byte[] bytes;
        try { bytes = entry.Value.Value; }
        catch
        {
            lock (_gate) RemoveIfCurrent(path, entry);
            throw;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var current) || !ReferenceEquals(current, entry))
                return bytes;
            if (!entry.Accounted)
            {
                entry.Size = bytes.LongLength;
                entry.Accounted = true;
                _retainedBytes = checked(_retainedBytes + entry.Size);
            }
            Touch(entry);
            if (entry.Size > _byteLimit)
            {
                RemoveIfCurrent(path, entry);
                return bytes;
            }
            while (_entries.Count > _entryLimit || _retainedBytes > _byteLimit)
            {
                var oldest = _lru.Last;
                if (oldest is null) break;
                RemoveIfCurrent(oldest.Value, _entries[oldest.Value]);
            }
        }
        return bytes;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _retainedBytes = 0;
        }
    }

    private void Touch(Entry entry)
    {
        if (ReferenceEquals(_lru.First, entry.Node)) return;
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void RemoveIfCurrent(string path, Entry expected)
    {
        if (!_entries.TryGetValue(path, out var current) || !ReferenceEquals(current, expected)) return;
        _entries.Remove(path);
        _lru.Remove(expected.Node);
        if (expected.Accounted) _retainedBytes -= expected.Size;
    }
}
