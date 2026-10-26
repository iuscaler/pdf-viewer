namespace PDFViewer.Core.Caching;

/// <summary>
/// 비용(바이트 수 등) 상한을 가진 LRU 캐시. 스레드 안전하지 않으므로 한 스레드에서만 사용한다.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _list = new(); // 앞쪽이 가장 최근에 사용된 항목
    private readonly Func<TValue, long> _costOf;
    private readonly Action<TKey, TValue>? _onEvicted;

    public LruCache(long capacity, Func<TValue, long> costOf, Action<TKey, TValue>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _costOf = costOf;
        _onEvicted = onEvicted;
    }

    public long Capacity { get; }

    public long TotalCost { get; private set; }

    public int Count => _map.Count;

    public IEnumerable<TKey> Keys => _map.Keys;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>사용 순서를 갱신하지 않고 조회한다.</summary>
    public bool TryPeek(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        Remove(key);

        var entry = new Entry(key, value, _costOf(value));
        _map[key] = _list.AddFirst(entry);
        TotalCost += entry.Cost;

        // 방금 넣은 항목은 남겨 두고 오래된 항목부터 내보낸다.
        while (TotalCost > Capacity && _list.Count > 1)
        {
            Evict(_list.Last!);
        }
    }

    public bool Remove(TKey key)
    {
        if (!_map.TryGetValue(key, out var node))
        {
            return false;
        }

        Evict(node);
        return true;
    }

    public void Clear()
    {
        while (_list.Last is { } node)
        {
            Evict(node);
        }
    }

    private void Evict(LinkedListNode<Entry> node)
    {
        _list.Remove(node);
        _map.Remove(node.Value.Key);
        TotalCost -= node.Value.Cost;
        _onEvicted?.Invoke(node.Value.Key, node.Value.Value);
    }

    private readonly record struct Entry(TKey Key, TValue Value, long Cost);
}
